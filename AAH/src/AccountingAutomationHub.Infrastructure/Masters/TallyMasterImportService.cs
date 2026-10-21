using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Infrastructure.Tally;

namespace AccountingAutomationHub.Infrastructure.Masters;

public sealed class TallyMasterImportService
{
    private static readonly string[] NameKeys =
    [
        "NAME", "LEDGERNAME", "GROUPNAME", "STOCKITEMNAME",
        "VOUCHERTYPENAME", "UNITNAME", "GODOWNNAME", "CURRENCYNAME"
    ];

    private static readonly string[] ParentKeys =
    [
        "PARENT", "PARENTNAME", "UNDER", "GROUP", "GROUPNAME"
    ];

    private static readonly HashSet<string> SupportedTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Group", "Ledger", "Voucher Type", "Unit", "State", "Currency",
            "Cost Category", "Cost Centre", "Stock Item", "Stock Group", "Stock Category", "Godown",
            "Tax Classification", "IncomeTaxClassification", "TaxUnit",
            "IncomeTaxSlab"
        };

    public MasterImportResult Parse(
        string path,
        long clientId,
        string financialYear,
        string company)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The selected master file was not found.", path);
        }

        var extension = Path.GetExtension(path);
        var warnings = new List<string>();
        var parsed = extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? ParseJson(path, clientId, financialYear, company, warnings)
            : ParseXml(path, clientId, financialYear, company, warnings);

        return BuildResult(
            parsed,
            path,
            extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? "Tally JSON"
                : "Tally XML",
            warnings);
    }

    public MasterImportResult ParseXmlContent(
        string xmlContent,
        long clientId,
        string financialYear,
        string company,
        string sourceName = "Live Tally")
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
        {
            throw new InvalidOperationException("Tally returned an empty master response.");
        }

        var warnings = new List<string>();
        var parsed = ParseXmlDocument(
            XDocument.Parse(
                TallyXmlSanitizer.Sanitize(xmlContent),
                LoadOptions.None),
            clientId,
            financialYear,
            company,
            warnings,
            sourceName);

        return BuildResult(
            parsed,
            sourceName,
            "Live Tally XML",
            warnings);
    }

    private static MasterImportResult BuildResult(
        IReadOnlyCollection<MasterRecord> parsed,
        string sourcePath,
        string sourceFormat,
        IReadOnlyList<string> warnings)
    {
        var groups = parsed
            .Where(record => record.Name.Length > 0)
            .GroupBy(
                record =>
                {
                    var type = record.Type.Trim().ToUpperInvariant();
                    // Fix72: Ledger/Group identity coming from Tally is MASTERID-first.
                    // Two live Tally Masters are allowed to have the same display name;
                    // collapsing them by normalized name loses one stable identity. Use
                    // MASTERID (GUID fallback) whenever Tally supplied it, and use name
                    // only for unbound/manual/legacy objects.
                    if (type is "LEDGER" or "GROUP" or "STOCK ITEM" or "STOCKITEM")
                    {
                        var masterId = record.TallyMasterId.Trim();
                        if (masterId.Length > 0) return $"{type}\u001fMID\u001f{masterId}";
                        var guid = record.TallyGuid.Trim();
                        if (guid.Length > 0) return $"{type}\u001fGUID\u001f{guid.ToUpperInvariant()}";
                    }
                    var name = type.Equals("LEDGER", StringComparison.OrdinalIgnoreCase)
                        ? TallyMasterIdentity.Key(record.Name)
                        : AccountingText.DuplicateKey(record.Name);
                    return $"{type}\u001fNAME\u001f{name}";
                },
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var unique = groups
            .Select(group => group.First())
            .ToArray();

        var conflicts = groups.Count(group =>
            group.Select(record =>
                    $"{record.Parent.Trim().ToUpperInvariant()}\u001f{record.Company.Trim().ToUpperInvariant()}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .Any());

        return new MasterImportResult
        {
            SourcePath = sourcePath,
            SourceFormat = sourceFormat,
            ParsedObjects = parsed.Count,
            ImportedObjects = unique.Length,
            DuplicateObjects = Math.Max(0, parsed.Count - unique.Length),
            ConflictingObjects = conflicts,
            IgnoredObjects = 0,
            Warnings = warnings,
            Records = unique
        };
    }

    private static List<MasterRecord> ParseJson(
        string path,
        long clientId,
        string financialYear,
        string company,
        List<string> warnings)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 256
            });

        var records = new List<MasterRecord>();
        WalkJson(
            document.RootElement,
            "$",
            string.Empty,
            records,
            clientId,
            financialYear,
            company);

        if (records.Count == 0)
        {
            warnings.Add("No supported Tally master objects were found in the JSON file.");
        }
        return records;
    }

    private static void WalkJson(
        JsonElement element,
        string path,
        string collectionHint,
        List<MasterRecord> records,
        long clientId,
        string financialYear,
        string company)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var properties = element
                    .EnumerateObject()
                    .ToDictionary(
                        property => Normalize(property.Name),
                        property => property.Value,
                        StringComparer.OrdinalIgnoreCase);

                var name = FirstDisplayText(properties, NameKeys);
                var explicitType = FirstText(
                    properties,
                    ["TYPE", "MASTERTYPE", "OBJECTTYPE", "BASEOBJECT"]);
                var type = NormalizeType(explicitType, collectionHint, path);

                if (name.Length > 0 && type.Length > 0)
                {
                    records.Add(new MasterRecord
                    {
                        ClientId = clientId,
                        FinancialYear = financialYear,
                        Name = name,
                        Parent = FirstDisplayText(properties, ParentKeys) is { Length: > 0 } parent
                            ? parent
                            : "Primary",
                        Type = type,
                        Company = FirstText(properties, ["COMPANY", "COMPANYNAME"]) is { Length: > 0 } sourceCompany
                            ? sourceCompany
                            : company,
                        Source = "Tally JSON",
                        OpeningBalance = type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)
                            ? ParseTallyOpeningBalance(FirstText(properties, ["OPENINGBALANCE"]))
                            : null,
                        BaseUnit = FirstText(properties, ["BASEUNITS", "BASEUNIT"]),
                        StockCategoryName = FirstText(properties, ["CATEGORY", "STOCKCATEGORY"]),
                        AdditionalUnit = FirstText(properties, ["ADDITIONALUNITS", "ADDITIONALUNIT"]),
                        PartNumberSku = FirstText(properties, ["MAILINGNAME", "PARTNO", "PARTNUMBER"]),
                        HsnSac = FirstText(properties, ["HSNCODE", "HSNSAC", "GSTHSNCODE"]),
                        GstRate = ParseAmount(FirstText(properties, ["GSTRATE", "GSTRATEOFITEM"])),
                        GstTypeOfSupply = FirstText(properties, ["GSTTYPEOFSUPPLY"]),
                        BatchWise = ParseYesNo(FirstText(properties, ["ISBATCHWISEON", "BATCHWISE"])),
                        Perishable = ParseYesNo(FirstText(properties, ["ISPERISHABLEON", "PERISHABLE"])),
                        HasManufacturingDate = ParseYesNo(FirstText(properties, ["HASMFGDATE", "HASMANUFACTURINGDATE"])),
                        CostingMethod = FirstText(properties, ["COSTINGMETHOD"]),
                        ValuationMethod = FirstText(properties, ["VALUATIONMETHOD"]),
                        FormalName = FirstText(properties, ["ORIGINALNAME", "FORMALNAME"]),
                        DecimalPlaces = ParseInteger(FirstText(properties, ["DECIMALPLACES"])),
                        ReportingUqc = FirstText(properties, ["REPORTINGUQCNAME", "REPORTINGUQC"]),
                        Conversion = ParseAmount(FirstText(properties, ["CONVERSION"])),
                        Denominator = ParseAmount(FirstText(properties, ["DENOMINATOR"])),
                        TallyMasterId = FirstText(properties, ["MASTERID"]),
                        TallyGuid = FirstText(properties, ["GUID"]),
                        TallyAlterId = ParseLong(FirstText(properties, ["ALTERID"]))
                    });
                }

                foreach (var property in element.EnumerateObject())
                {
                    WalkJson(
                        property.Value,
                        $"{path}.{property.Name}",
                        property.Name,
                        records,
                        clientId,
                        financialYear,
                        company);
                }
                break;
            }

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    WalkJson(
                        item,
                        $"{path}[]",
                        collectionHint,
                        records,
                        clientId,
                        financialYear,
                        company);
                }
                break;
        }
    }

    private static List<MasterRecord> ParseXml(
        string path,
        long clientId,
        string financialYear,
        string company,
        List<string> warnings) =>
        ParseXmlDocument(
            XDocument.Parse(
                TallyXmlSanitizer.Sanitize(File.ReadAllText(path)),
                LoadOptions.None),
            clientId,
            financialYear,
            company,
            warnings,
            "Tally XML");

    private static List<MasterRecord> ParseXmlDocument(
        XDocument document,
        long clientId,
        string financialYear,
        string company,
        List<string> warnings,
        string sourceName)
    {
        var records = new List<MasterRecord>();

        foreach (var element in document.Descendants())
        {
            var rawType = StripListSuffix(element.Name.LocalName);
            var type = NormalizeType(rawType, rawType, rawType);
            if (type.Length == 0)
            {
                continue;
            }

            var directName = CleanTallyDisplay(element.Elements().FirstOrDefault(child =>
                Normalize(child.Name.LocalName) is "NAME" or "LEDGERNAME")?.Value);
            var listedName = CleanTallyDisplay(element.Elements().FirstOrDefault(child =>
                    Normalize(child.Name.LocalName) == "NAMELIST")?
                .Elements().FirstOrDefault(child => Normalize(child.Name.LocalName) == "NAME")?.Value);
            var name =
                CleanTallyDisplay(element.Attribute("NAME")?.Value) is { Length: > 0 } attributeName
                    ? attributeName
                    : directName.Length > 0 ? directName : listedName;

            if (name.Length == 0)
            {
                continue;
            }

            var parent =
                CleanTallyDisplay(element.Elements().FirstOrDefault(child =>
                    Normalize(child.Name.LocalName) is "PARENT" or "UNDER")?.Value);

            records.Add(new MasterRecord
            {
                ClientId = clientId,
                FinancialYear = financialYear,
                Name = name,
                Parent = parent.Length == 0 ? "Primary" : parent,
                Type = type,
                Company = company,
                Source = sourceName,
                OpeningBalance = type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)
                    ? ParseTallyOpeningBalance(ChildText(element, "OPENINGBALANCE"))
                    : null,
                BaseUnit = ChildText(element, "BASEUNITS", "BASEUNIT"),
                StockCategoryName = ChildText(element, "CATEGORY", "STOCKCATEGORY"),
                AdditionalUnit = ChildText(element, "ADDITIONALUNITS", "ADDITIONALUNIT"),
                PartNumberSku = DescendantText(element, "MAILINGNAME", "PARTNO", "PARTNUMBER"),
                HsnSac = ChildText(element, "HSNCODE", "HSNSAC", "GSTHSNCODE"),
                GstRate = ParseAmount(ChildText(element, "GSTRATE", "GSTRATEOFITEM")),
                GstTypeOfSupply = ChildText(element, "GSTTYPEOFSUPPLY"),
                BatchWise = ParseYesNo(ChildText(element, "ISBATCHWISEON", "BATCHWISE")),
                Perishable = ParseYesNo(ChildText(element, "ISPERISHABLEON", "PERISHABLE")),
                HasManufacturingDate = ParseYesNo(ChildText(element, "HASMFGDATE", "HASMANUFACTURINGDATE")),
                CostingMethod = ChildText(element, "COSTINGMETHOD"),
                ValuationMethod = ChildText(element, "VALUATIONMETHOD"),
                FormalName = ChildText(element, "ORIGINALNAME", "FORMALNAME"),
                DecimalPlaces = ParseInteger(ChildText(element, "DECIMALPLACES")),
                ReportingUqc = ChildText(element, "REPORTINGUQCNAME", "REPORTINGUQC"),
                Conversion = ParseAmount(ChildText(element, "CONVERSION")),
                Denominator = ParseAmount(ChildText(element, "DENOMINATOR")),
                TallyMasterId = AttributeOrChildText(element, "MASTERID"),
                TallyGuid = AttributeOrChildText(element, "GUID"),
                TallyAlterId = ParseLong(AttributeOrChildText(element, "ALTERID"))
            });
        }

        if (records.Count == 0)
        {
            warnings.Add("No supported Tally master objects were found in the XML response.");
        }
        return records;
    }

    private static string FirstDisplayText(
        IReadOnlyDictionary<string, JsonElement> properties,
        IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (!properties.TryGetValue(Normalize(key), out var value)) continue;
            if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) continue;
            var text = CleanTallyDisplay(value.ToString());
            if (text.Length > 0) return text;
        }
        return string.Empty;
    }

    private static string FirstText(
        IReadOnlyDictionary<string, JsonElement> properties,
        IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (!properties.TryGetValue(Normalize(key), out var value))
            {
                continue;
            }

            if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                var text = Clean(value.ToString());
                if (text.Length > 0)
                {
                    return text;
                }
            }
        }
        return string.Empty;
    }

    private static string NormalizeType(
        string explicitType,
        string collectionHint,
        string path)
    {
        var candidate = $"{explicitType} {collectionHint} {path}".ToUpperInvariant();

        (string Match, string Type)[] mappings =
        [
            ("VOUCHERTYPE", "Voucher Type"),
            ("STOCKITEM", "Stock Item"),
            ("STOCKCATEGORY", "Stock Category"),
            ("STOCKGROUP", "Stock Group"),
            ("COSTCENTRE", "Cost Centre"),
            ("COSTCENTER", "Cost Centre"),
            ("COSTCATEGORY", "Cost Category"),
            ("TAXCLASSIFICATION", "Tax Classification"),
            ("INCOMETAXCLASSIFICATION", "IncomeTaxClassification"),
            ("INCOMETAXSLAB", "IncomeTaxSlab"),
            ("TAXUNIT", "TaxUnit"),
            ("GODOWN", "Godown"),
            ("CURRENCY", "Currency"),
            ("STATE", "State"),
            ("UNIT", "Unit"),
            ("LEDGER", "Ledger"),
            ("GROUP", "Group")
        ];

        foreach (var mapping in mappings)
        {
            if (Normalize(candidate).Contains(mapping.Match, StringComparison.Ordinal))
            {
                return mapping.Type;
            }
        }

        var cleaned = Clean(explicitType);
        return SupportedTypes.Contains(cleaned) ? cleaned : string.Empty;
    }


    private static string ChildText(XElement element, params string[] names)
    {
        var wanted = names.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Clean(element.Elements().FirstOrDefault(child => wanted.Contains(Normalize(child.Name.LocalName)))?.Value);
    }

    private static string DescendantText(XElement element, params string[] names)
    {
        var direct = ChildText(element, names);
        if (direct.Length > 0)
        {
            return direct;
        }

        var wanted = names.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Clean(element.Descendants().FirstOrDefault(child => wanted.Contains(Normalize(child.Name.LocalName)))?.Value);
    }

    private static string AttributeOrChildText(XElement element, string name)
    {
        var normalized = Normalize(name);
        var attribute = element.Attributes().FirstOrDefault(item => Normalize(item.Name.LocalName) == normalized)?.Value;
        var attrText = Clean(attribute);
        return attrText.Length > 0 ? attrText : ChildText(element, name);
    }

    private static bool ParseYesNo(string? value)
    {
        var text = Clean(value);
        return text.Equals("YES", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("Y", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || text == "1";
    }

    private static int ParseInteger(string? value) =>
        int.TryParse(Clean(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static long ParseLong(string? value) =>
        long.TryParse(new string(Clean(value).Where(char.IsDigit).ToArray()), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0L;

    /// <summary>
    /// Tally's native Amount sign is the reverse of AAH/business Ledger balance sign:
    /// native negative = Debit, native positive = Credit. AAH stores business balances as
    /// positive = Dr and negative = Cr. Explicit DR/CR suffixes are also normalized to
    /// that same business convention.
    /// </summary>
    private static decimal? ParseTallyOpeningBalance(string? value)
    {
        var text = Clean(value);
        if (text.Length == 0) return null;

        var upper = text.ToUpperInvariant();
        var isCredit = Regex.IsMatch(upper, @"(?:^|\s)CR(?:$|\s)");
        var isDebit = Regex.IsMatch(upper, @"(?:^|\s)DR(?:$|\s)");
        var numeric = Regex.Replace(text, @"[^0-9.\-]", string.Empty);
        if (!decimal.TryParse(
                numeric,
                NumberStyles.Number | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var amount))
        {
            return null;
        }

        if (isDebit) return Math.Abs(amount);
        if (isCredit) return -Math.Abs(amount);

        // Native Tally XML/JSON OPENINGBALANCE uses Debit as negative and Credit as
        // positive (for example Cash/Bank/Fixed Assets are normally negative in XML).
        // Invert once when entering AAH so every downstream report uses +Dr / -Cr.
        return -amount;
    }

    private static decimal? ParseAmount(string? value)
    {
        var text = Clean(value);
        if (text.Length == 0)
        {
            return null;
        }

        var upper = text.ToUpperInvariant();
        var isCredit = Regex.IsMatch(upper, @"(?:^|\s)CR(?:$|\s)");
        var isDebit = Regex.IsMatch(upper, @"(?:^|\s)DR(?:$|\s)");
        var numeric = Regex.Replace(text, @"[^0-9.\-]", string.Empty);
        if (!decimal.TryParse(
                numeric,
                NumberStyles.Number | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var amount))
        {
            return null;
        }

        if (isCredit)
        {
            return -Math.Abs(amount);
        }
        if (isDebit)
        {
            return Math.Abs(amount);
        }
        return amount;
    }

    private static string StripListSuffix(string value) =>
        value.EndsWith(".LIST", StringComparison.OrdinalIgnoreCase)
            ? value[..^5]
            : value;

    private static string Normalize(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    // Tally Master names are mutable display data and must be preserved exactly
    // (apart from line-break sanitization and outer trim). Internal repeated spaces are
    // significant because the same MASTERID + changed display name must be reflected in
    // AAH byte-for-display, not normalized back to the previous spelling.
    private static string CleanTallyDisplay(string? value) =>
        (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

    private static string Clean(string? value) =>
        string.Join(
            " ",
            (value ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Trim();
}
