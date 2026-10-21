using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Xml.Linq;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Fail-closed pre-send validation for the Bank XML contract.  This validator is
/// intentionally independent from Tally's HTTP response so malformed or mixed
/// master/voucher XML never reaches the user's live Tally company.
/// </summary>
internal static class TallyBankXmlContractValidator
{
    private static readonly HashSet<string> VoucherElementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ENVELOPE", "HEADER", "VERSION", "TALLYREQUEST", "TYPE", "ID",
        "BODY", "DESC", "STATICVARIABLES", "SVCURRENTCOMPANY", "DATA",
        "TALLYMESSAGE", "VOUCHER", "DATE", "NARRATION", "VOUCHERTYPENAME",
        "REFERENCE", "ALLLEDGERENTRIES.LIST",
        "LEDGERNAME", "ISDEEMEDPOSITIVE", "AMOUNT"
    };

    private static readonly HashSet<string> LedgerMasterElementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ENVELOPE", "HEADER", "TALLYREQUEST",
        "BODY", "IMPORTDATA", "REQUESTDESC", "REPORTNAME", "STATICVARIABLES",
        "SVCURRENTCOMPANY", "IMPORTDUPS", "REQUESTDATA",
        "TALLYMESSAGE", "LEDGER", "NAME", "PARENT"
    };

    internal static void ValidateLedgerMasterImport(
        string xml,
        string expectedCompanyName,
        IReadOnlyDictionary<string, string> expectedLedgerParents)
    {
        var document = Parse(xml, "Ledger master");
        ValidateMasterImportHeader(document, expectedCompanyName);
        ValidateAllowedElements(document, LedgerMasterElementNames, "Ledger master");

        if (document.Descendants("VOUCHER").Any())
            Fail("Ledger master import contains a VOUCHER object.");
        if (document.Descendants("CURRENCYNAME").Any())
            Fail("Ledger master import contains CURRENCYNAME. Bank import must use the Tally company base currency.");
        var importDups = document.Descendants("IMPORTDUPS").SingleOrDefault()?.Value.Trim() ?? string.Empty;
        if (importDups.Length > 0 && !importDups.Equals("@@DUPIGNORE", StringComparison.OrdinalIgnoreCase))
            Fail($"Ledger master IMPORTDUPS must be @@DUPIGNORE when present; found '{importDups}'.");

        var expectedParents = expectedLedgerParents
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.Key) &&
                !string.IsNullOrWhiteSpace(item.Value))
            .ToDictionary(
                item => item.Key.Trim(),
                item => item.Value.Trim(),
                StringComparer.OrdinalIgnoreCase);
        var expected = expectedParents.Keys
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var ledgers = document.Descendants("LEDGER").ToArray();
        var requestData = document.Root!.Element("BODY")!.Element("IMPORTDATA")!.Element("REQUESTDATA")!;
        var messages = requestData.Elements("TALLYMESSAGE").ToArray();
        if (messages.Length != ledgers.Length)
            Fail($"Ledger master import must contain one TALLYMESSAGE per LEDGER; found {messages.Length} message(s) and {ledgers.Length} ledger(s).");
        if (requestData.Elements().Count() != messages.Length)
            Fail("Ledger master REQUESTDATA may contain only TALLYMESSAGE elements.");
        foreach (var message in messages)
        {
            if (message.Attributes().Any())
                Fail("Ledger master TALLYMESSAGE must not contain attributes.");
            var objects = message.Elements().ToArray();
            if (objects.Length != 1 || !objects[0].Name.LocalName.Equals("LEDGER", StringComparison.OrdinalIgnoreCase))
                Fail("Each Ledger master TALLYMESSAGE must contain exactly one LEDGER object.");
        }

        var actual = new List<string>(ledgers.Length);
        foreach (var ledger in ledgers)
        {
            var attributes = ledger.Attributes().ToArray();
            var unexpectedAttributes = attributes
                .Where(attribute =>
                    !attribute.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase) &&
                    !attribute.Name.LocalName.Equals("ACTION", StringComparison.OrdinalIgnoreCase))
                .Select(attribute => attribute.Name.LocalName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (unexpectedAttributes.Length > 0)
                Fail($"Ledger master contains unexpected attribute(s): {string.Join(", ", unexpectedAttributes)}.");
            if (attributes.Any(attribute => attribute.Name.Namespace != XNamespace.None))
                Fail("Ledger master attributes must not use XML namespaces.");

            var name = RequiredText(ledger, "NAME", "Ledger master");
            var parent = RequiredText(ledger, "PARENT", $"Ledger '{name}'");
            if (ledger.Elements("NAME").Count() != 1 || ledger.Elements("PARENT").Count() != 1 ||
                ledger.Elements().Count() != 2)
                Fail($"Ledger '{name}' must contain exactly NAME and PARENT, once each.");
            if (ledger.Elements().Any(element => element.Attributes().Any()))
                Fail($"Ledger '{name}' NAME/PARENT tags must not contain attributes.");

            var attributeName = GetAttributeValue(ledger, "NAME");
            var action = GetAttributeValue(ledger, "ACTION");
            if (attributeName.Length == 0 ||
                !attributeName.Equals(name, StringComparison.Ordinal))
                Fail($"Ledger '{name}' NAME attribute must exactly match its NAME tag.");
            if (!action.Equals("Create", StringComparison.OrdinalIgnoreCase))
                Fail($"Ledger '{name}' is not ACTION=Create.");
            if (name.Equals("Suspense A/c", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Suspense A/C", StringComparison.OrdinalIgnoreCase))
                Fail("Suspense A/c is a Tally Group and must not be sent as a Ledger.");
            if (parent.Length == 0)
                Fail($"Ledger '{name}' has a blank PARENT.");
            if (!expectedParents.TryGetValue(name, out var expectedParent))
                Fail($"Ledger '{name}' is not part of the expected missing-master set.");
            if (!parent.Equals(expectedParent, StringComparison.OrdinalIgnoreCase))
                Fail($"Ledger '{name}' PARENT must be '{expectedParent}', found '{parent}'.");

            actual.Add(name);
        }

        var actualNormalized = actual
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ledgers.Length != expected.Length || actualNormalized.Length != ledgers.Length)
            Fail($"Ledger master import must contain exactly {expected.Length} unique LEDGER object(s); found {ledgers.Length} object(s), {actualNormalized.Length} unique.");
        if (!expected.SequenceEqual(actualNormalized, StringComparer.OrdinalIgnoreCase))
        {
            Fail(
                "Ledger master count/name mismatch. Expected [" +
                string.Join(", ", expected) + "] but XML contains [" +
                string.Join(", ", actualNormalized) + "].");
        }
    }

    internal static void ValidateVoucherImport(
        string xml,
        string expectedCompanyName,
        IReadOnlyList<TallyErp9BankVoucherExpectation> expectedVouchers)
    {
        var document = Parse(xml, "Voucher");
        ValidateHeader(document, "Vouchers", expectedCompanyName);
        ValidateAllowedElements(document, VoucherElementNames, "Voucher");

        if (document.Descendants("LEDGER").Any())
            Fail("Voucher import contains a Ledger master. Masters and vouchers must be separate requests.");
        if (document.Descendants("CURRENCYNAME").Any())
            Fail("Voucher import contains CURRENCYNAME.");

        var vouchers = document.Descendants("VOUCHER").ToArray();
        if (vouchers.Length != expectedVouchers.Count)
            Fail($"Voucher count mismatch. Expected {expectedVouchers.Count}, XML contains {vouchers.Length}.");

        var data = document.Root!.Element("BODY")!.Element("DATA")!;
        var messages = data.Elements("TALLYMESSAGE").ToArray();
        if (messages.Length != vouchers.Length)
            Fail($"Voucher import must contain one TALLYMESSAGE per VOUCHER; found {messages.Length} message(s) and {vouchers.Length} voucher(s).");
        if (data.Elements().Count() != messages.Length)
            Fail("Voucher DATA may contain only TALLYMESSAGE elements.");
        foreach (var message in messages)
        {
            if (message.Attributes().Any())
                Fail("Voucher TALLYMESSAGE must not contain attributes.");
            var objects = message.Elements().ToArray();
            if (objects.Length != 1 || !objects[0].Name.LocalName.Equals("VOUCHER", StringComparison.OrdinalIgnoreCase))
                Fail("Each Voucher TALLYMESSAGE must contain exactly one VOUCHER object.");
        }

        for (var index = 0; index < vouchers.Length; index++)
        {
            var voucher = vouchers[index];
            var expectedVoucher = expectedVouchers[index];
            var ordinal = index + 1;
            if (voucher.Attributes().Any())
                Fail($"Voucher {ordinal} must not contain attributes in the strict ERP 9 Bank request.");
            if (voucher.Elements("DATE").Count() != 1 ||
                voucher.Elements("VOUCHERTYPENAME").Count() != 1 ||
                voucher.Elements("NARRATION").Count() > 1 ||
                voucher.Elements("REFERENCE").Count() > 1)
                Fail($"Voucher {ordinal} contains duplicate/missing scalar tags.");
            if (voucher.Elements()
                .Where(element => !element.Name.LocalName.Equals("ALLLEDGERENTRIES.LIST", StringComparison.OrdinalIgnoreCase))
                .Any(element => element.Attributes().Any()))
                Fail($"Voucher {ordinal} scalar tags must not contain attributes.");

            var dateText = RequiredText(voucher, "DATE", $"Voucher {ordinal}");
            if (!DateTime.TryParseExact(
                    dateText,
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _))
                Fail($"Voucher {ordinal} DATE '{dateText}' is not YYYYMMDD.");
            var expectedDate = expectedVoucher.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (!dateText.Equals(expectedDate, StringComparison.Ordinal))
                Fail($"Voucher {ordinal} DATE must be '{expectedDate}', found '{dateText}'.");

            var actualNarration = voucher.Element("NARRATION")?.Value.Trim() ?? string.Empty;
            var expectedNarration = (expectedVoucher.Narration ?? string.Empty).Trim();
            if (!actualNarration.Equals(expectedNarration, StringComparison.Ordinal))
                Fail($"Voucher {ordinal} NARRATION does not match the prepared Bank transaction.");

            var actualReference = voucher.Element("REFERENCE")?.Value.Trim() ?? string.Empty;
            var expectedReference = (expectedVoucher.Reference ?? string.Empty).Trim();
            if (!actualReference.Equals(expectedReference, StringComparison.Ordinal))
                Fail($"Voucher {ordinal} REFERENCE does not match the prepared Bank transaction.");

            var voucherType = RequiredText(voucher, "VOUCHERTYPENAME", $"Voucher {ordinal}");
            if (!voucherType.Equals("Payment", StringComparison.OrdinalIgnoreCase) &&
                !voucherType.Equals("Receipt", StringComparison.OrdinalIgnoreCase) &&
                !voucherType.Equals("Contra", StringComparison.OrdinalIgnoreCase))
                Fail($"Voucher {ordinal} has unsupported VOUCHERTYPENAME '{voucherType}'.");
            if (!voucherType.Equals(expectedVoucher.VoucherType, StringComparison.OrdinalIgnoreCase))
                Fail($"Voucher {ordinal} VOUCHERTYPENAME does not match the prepared Bank transaction.");

            var entries = voucher.Elements("ALLLEDGERENTRIES.LIST").ToArray();
            if (entries.Length != 2 || expectedVoucher.Lines.Count != 2)
                Fail($"Voucher {ordinal} must contain exactly 2 ALLLEDGERENTRIES.LIST blocks.");

            decimal total = 0m;
            var debitCount = 0;
            var creditCount = 0;
            var entryLedgerNames = new List<string>(2);
            for (var entryIndex = 0; entryIndex < entries.Length; entryIndex++)
            {
                var entry = entries[entryIndex];
                var expectedLine = expectedVoucher.Lines[entryIndex];
                if (entry.Attributes().Any())
                    Fail($"Voucher {ordinal} ALLLEDGERENTRIES.LIST must not contain attributes.");
                if (entry.Elements("LEDGERNAME").Count() != 1 ||
                    entry.Elements("ISDEEMEDPOSITIVE").Count() != 1 ||
                    entry.Elements("AMOUNT").Count() != 1 ||
                    entry.Elements().Count() != 3)
                    Fail($"Voucher {ordinal} ledger line must contain exactly LEDGERNAME, ISDEEMEDPOSITIVE and AMOUNT.");
                if (entry.Elements().Any(element => element.Attributes().Any()))
                    Fail($"Voucher {ordinal} ledger-line tags must not contain attributes.");

                var ledgerName = RequiredText(entry, "LEDGERNAME", $"Voucher {ordinal} ledger line");
                if (!ledgerName.Equals(expectedLine.LedgerName.Trim(), StringComparison.Ordinal))
                    Fail($"Voucher {ordinal} ledger line {entryIndex + 1} LEDGERNAME does not match the prepared Bank transaction.");
                entryLedgerNames.Add(ledgerName);
                if (ledgerName.Equals("Suspense A/c", StringComparison.OrdinalIgnoreCase) ||
                    ledgerName.Equals("Suspense A/C", StringComparison.OrdinalIgnoreCase))
                    Fail($"Voucher {ordinal} uses the Suspense A/c Group as a LEDGERNAME.");

                var deemedPositive = RequiredText(
                    entry, "ISDEEMEDPOSITIVE", $"Voucher {ordinal} ledger '{ledgerName}'");
                var isDebit = deemedPositive.Equals("Yes", StringComparison.OrdinalIgnoreCase);
                var isCredit = deemedPositive.Equals("No", StringComparison.OrdinalIgnoreCase);
                if (!isDebit && !isCredit)
                    Fail($"Voucher {ordinal} ledger '{ledgerName}' has invalid ISDEEMEDPOSITIVE '{deemedPositive}'.");
                if (isDebit != expectedLine.IsDebit)
                    Fail($"Voucher {ordinal} ledger line {entryIndex + 1} debit/credit direction does not match the prepared Bank transaction.");

                var amountText = RequiredText(entry, "AMOUNT", $"Voucher {ordinal} ledger '{ledgerName}'");
                if (!decimal.TryParse(
                        amountText,
                        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture,
                        out var amount))
                    Fail($"Voucher {ordinal} ledger '{ledgerName}' has invalid AMOUNT '{amountText}'.");
                if (amount == 0m)
                    Fail($"Voucher {ordinal} ledger '{ledgerName}' has zero AMOUNT.");
                var expectedSigned = expectedLine.IsDebit
                    ? -Math.Abs(expectedLine.Amount)
                    : Math.Abs(expectedLine.Amount);
                var expectedAmountText = expectedSigned.ToString("0.00", CultureInfo.InvariantCulture);
                if (!amountText.Equals(expectedAmountText, StringComparison.Ordinal))
                    Fail($"Voucher {ordinal} ledger line {entryIndex + 1} AMOUNT must be '{expectedAmountText}', found '{amountText}'.");

                if (isDebit && amount >= 0m)
                    Fail($"Voucher {ordinal} debit ledger '{ledgerName}' must have a negative AMOUNT.");
                if (isCredit && amount <= 0m)
                    Fail($"Voucher {ordinal} credit ledger '{ledgerName}' must have a positive AMOUNT.");

                if (isDebit) debitCount++;
                if (isCredit) creditCount++;
                total += amount;
            }

            if (entryLedgerNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2)
                Fail($"Voucher {ordinal} debit and credit must use two different ledgers.");
            if (debitCount != 1 || creditCount != 1)
                Fail($"Voucher {ordinal} must contain one debit line and one credit line.");
            if (Math.Abs(total) > 0.005m)
                Fail($"Voucher {ordinal} is not balanced. Net XML AMOUNT is {total:0.00}.");
        }
    }

    private static XDocument Parse(string xml, string stage)
    {
        if (string.IsNullOrWhiteSpace(xml))
            Fail($"{stage} XML is blank.");

        try
        {
            return XDocument.Parse(xml, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Tally Bank XML contract validation failed: {stage} XML is not well-formed. {ex.Message}",
                ex);
        }
    }

    private static void ValidateMasterImportHeader(
        XDocument document,
        string expectedCompanyName)
    {
        var root = document.Root ??
                   throw new InvalidOperationException("Tally Bank XML contract validation failed: Root element must be ENVELOPE.");
        if (!root.Name.LocalName.Equals("ENVELOPE", StringComparison.OrdinalIgnoreCase))
            Fail("Root element must be ENVELOPE.");
        if (root.Name.Namespace != XNamespace.None || root.Attributes().Any())
            Fail("Ledger master ENVELOPE must be unnamespaced and attribute-free.");

        var header = root.Elements("HEADER").SingleOrDefault();
        var body = root.Elements("BODY").SingleOrDefault();
        if (header is null || body is null || root.Elements().Count() != 2)
            Fail("Ledger master ENVELOPE must contain exactly HEADER and BODY.");
        if (header.Attributes().Any()) Fail("Ledger master HEADER must not contain attributes.");
        RequireSingleEquals(header, "TALLYREQUEST", "Import Data");
        if (header.Elements().Count() != 1)
            Fail("Ledger master HEADER may contain only TALLYREQUEST=Import Data.");

        if (body.Attributes().Any()) Fail("Ledger master BODY must not contain attributes.");
        var importData = body.Elements("IMPORTDATA").SingleOrDefault();
        if (importData is null || body.Elements().Count() != 1)
            Fail("Ledger master BODY must contain exactly one IMPORTDATA block.");
        if (importData.Attributes().Any()) Fail("Ledger master IMPORTDATA must not contain attributes.");

        var requestDesc = importData.Elements("REQUESTDESC").SingleOrDefault();
        var requestData = importData.Elements("REQUESTDATA").SingleOrDefault();
        if (requestDesc is null || requestData is null || importData.Elements().Count() != 2)
            Fail("Ledger master IMPORTDATA must contain exactly REQUESTDESC and REQUESTDATA.");
        if (requestDesc.Attributes().Any() || requestData.Attributes().Any())
            Fail("Ledger master REQUESTDESC/REQUESTDATA must not contain attributes.");
        RequireSingleEquals(requestDesc, "REPORTNAME", "All Masters");

        var staticBlocks = requestDesc.Elements("STATICVARIABLES").ToArray();
        if (staticBlocks.Length != 1)
            Fail($"Ledger master REQUESTDESC must contain exactly one STATICVARIABLES block; found {staticBlocks.Length}.");
        if (requestDesc.Elements().Count() != 2)
            Fail("Ledger master REQUESTDESC may contain only REPORTNAME and STATICVARIABLES.");
        var staticVariables = staticBlocks[0];
        if (staticVariables.Attributes().Any()) Fail("Ledger master STATICVARIABLES must not contain attributes.");
        RequireSingleEquals(staticVariables, "SVCURRENTCOMPANY", expectedCompanyName);
        var dupNodes = staticVariables.Elements("IMPORTDUPS").ToArray();
        if (dupNodes.Length > 1) Fail("Ledger master STATICVARIABLES contains duplicate IMPORTDUPS tags.");
        if (dupNodes.Length == 1 && !dupNodes[0].Value.Trim().Equals("@@DUPIGNORE", StringComparison.OrdinalIgnoreCase))
            Fail("Ledger master IMPORTDUPS must be @@DUPIGNORE.");
        if (staticVariables.Elements().Any(element =>
                !element.Name.LocalName.Equals("SVCURRENTCOMPANY", StringComparison.OrdinalIgnoreCase) &&
                !element.Name.LocalName.Equals("IMPORTDUPS", StringComparison.OrdinalIgnoreCase)))
            Fail("Ledger master STATICVARIABLES contains unsupported tags.");
    }

    private static void ValidateHeader(
        XDocument document,
        string expectedId,
        string expectedCompanyName)
    {
        var root = document.Root ??
                   throw new InvalidOperationException("Tally Bank XML contract validation failed: Root element must be ENVELOPE.");
        if (!root.Name.LocalName.Equals("ENVELOPE", StringComparison.OrdinalIgnoreCase))
            Fail("Root element must be ENVELOPE.");
        if (root.Name.Namespace != XNamespace.None)
            Fail("ENVELOPE must not use an XML namespace in the strict ERP 9 Bank request.");
        if (root.Attributes().Any())
            Fail("ENVELOPE must not contain attributes in the strict ERP 9 Bank request.");

        var headers = root.Elements("HEADER").ToArray();
        var bodies = root.Elements("BODY").ToArray();
        if (headers.Length != 1)
            Fail($"ENVELOPE must contain exactly one HEADER; found {headers.Length}.");
        if (bodies.Length != 1)
            Fail($"ENVELOPE must contain exactly one BODY; found {bodies.Length}.");
        if (root.Elements().Count() != 2)
            Fail("ENVELOPE may contain only HEADER and BODY in the strict ERP 9 Bank request.");

        var header = headers[0];
        if (header.Attributes().Any())
            Fail("HEADER must not contain attributes.");
        RequireSingleEquals(header, "VERSION", "1");
        RequireSingleEquals(header, "TALLYREQUEST", "Import");
        RequireSingleEquals(header, "TYPE", "Data");
        RequireSingleEquals(header, "ID", expectedId);
        if (header.Elements().Count() != 4)
            Fail("HEADER may contain only VERSION, TALLYREQUEST, TYPE and ID.");

        var body = bodies[0];
        if (body.Attributes().Any())
            Fail("BODY must not contain attributes.");
        var descs = body.Elements("DESC").ToArray();
        var dataBlocks = body.Elements("DATA").ToArray();
        if (descs.Length != 1)
            Fail($"BODY must contain exactly one DESC; found {descs.Length}.");
        if (dataBlocks.Length != 1)
            Fail($"BODY must contain exactly one DATA; found {dataBlocks.Length}.");
        if (body.Elements().Count() != 2)
            Fail("BODY may contain only DESC and DATA in the strict ERP 9 Bank request.");
        if (body.Element("IMPORTDATA") is not null)
            Fail("ERP 9 strict Bank import must not mix the alternate IMPORTDATA wrapper with the Data-import envelope.");

        var desc = descs[0];
        if (desc.Attributes().Any())
            Fail("DESC must not contain attributes.");
        var staticBlocks = desc.Elements("STATICVARIABLES").ToArray();
        if (staticBlocks.Length != 1)
            Fail($"DESC must contain exactly one STATICVARIABLES; found {staticBlocks.Length}.");
        if (desc.Elements().Count() != 1)
            Fail("DESC may contain only STATICVARIABLES in the strict ERP 9 Bank request.");

        var staticVariables = staticBlocks[0];
        if (staticVariables.Attributes().Any())
            Fail("STATICVARIABLES must not contain attributes.");
        RequireSingleEquals(
            staticVariables,
            "SVCURRENTCOMPANY",
            (expectedCompanyName ?? string.Empty).Trim());
        if (staticVariables.Elements().Count() != 1)
            Fail("STATICVARIABLES may contain only SVCURRENTCOMPANY in the strict ERP 9 Bank request.");
    }

    private static void ValidateAllowedElements(
        XDocument document,
        IReadOnlySet<string> allowed,
        string stage)
    {
        var allElements = (document.Root?.DescendantsAndSelf() ?? Enumerable.Empty<XElement>()).ToArray();
        var namespaced = allElements
            .Where(element => element.Name.Namespace != XNamespace.None)
            .Select(element => element.Name.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (namespaced.Length > 0)
            Fail($"{stage} XML contains namespaced element(s): {string.Join(", ", namespaced)}.");

        var unexpected = allElements
            .Select(element => element.Name.LocalName)
            .Where(name => !allowed.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (unexpected.Length > 0)
            Fail($"{stage} XML contains unexpected tag(s): {string.Join(", ", unexpected)}.");
    }

    private static void RequireSingleEquals(XElement parent, string tag, string expected)
    {
        var elements = parent.Elements(tag).ToArray();
        if (elements.Length != 1)
            Fail($"{parent.Name.LocalName} must contain exactly one {tag}; found {elements.Length}.");
        if (elements[0].Attributes().Any())
            Fail($"{parent.Name.LocalName}/{tag} must not contain attributes.");
        var actual = elements[0].Value.Trim();
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            Fail($"{parent.Name.LocalName}/{tag} must be '{expected}', found '{actual}'.");
    }

    private static string GetAttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute =>
                attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim() ?? string.Empty;

    private static string RequiredText(XElement parent, string tag, string context)
    {
        var element = parent.Element(tag);
        var value = element?.Value.Trim() ?? string.Empty;
        if (value.Length == 0)
            Fail($"{context} is missing required tag/value {tag}.");
        return value;
    }

    [DoesNotReturn]
    private static void Fail(string message) =>
        throw new InvalidOperationException(
            "Tally Bank XML contract validation failed: " + message);
}


internal sealed record TallyErp9BankVoucherExpectation(
    DateTime Date,
    string VoucherType,
    string Reference,
    string Narration,
    IReadOnlyList<TallyErp9BankLedgerLine> Lines);
