using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccountingAutomationHub.Domain.Clients;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// R90 maps evidence-backed R89 fields into the EXISTING Mobile Link review payload.
/// Never replaces missing data with Android ML Kit guesses or creates accounting entries.
/// </summary>
internal static class InvoiceOnnxAccountingPreviewParser
{
    internal static MobileInvoiceDocumentOcrResult Parse(BankOcrRecognitionBatch recognition,
        ClientProfile client, string originalPayloadJson, string phoneRawOcr)
    {
        // Do not use the single-page R89 template as multi-page authority.
        if (recognition.Pages.Count != 1 || recognition.SourceErrors.Count != 0)
            return MobileInvoiceDocumentOcrResult.Failed("R90 requires all invoice pages to be verified together; multi-page or partially recognized invoices remain in review, not an inferred import.");
        using var file = JsonDocument.Parse(File.ReadAllText(recognition.Pages[0].JsonPath, Encoding.UTF8));
        var source = file.RootElement;
        if (source.GetProperty("engine").GetString() != "AAH-RAPIDOCR-ONNX-R90")
            return MobileInvoiceDocumentOcrResult.Failed("Unrecognized spatial OCR source identity.");
        var preview = source.GetProperty("accounting_preview");
        var errors = new List<string>();
        foreach (var issue in preview.GetProperty("blocking_flags").EnumerateArray())
            if (!string.IsNullOrWhiteSpace(issue.GetString())) errors.Add(issue.GetString()!);

        static string Get(JsonElement node, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(key, out node)) return string.Empty;
            }
            return node.ValueKind == JsonValueKind.Null ? string.Empty : node.ToString().Trim();
        }
        static bool Money(string value, out decimal amount) => decimal.TryParse(value,
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture, out amount);
        static string UpperGstin(string value) => new(value.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        static JsonObject Clone(JsonObject? obj) => obj?.DeepClone().AsObject() ?? new JsonObject();

        var supplierName = Get(preview, "supplier", "name", "value");
        var buyerName = Get(preview, "buyer", "name", "value");
        var supplierGstin = UpperGstin(Get(preview, "supplier", "gstin", "value"));
        var buyerGstin = UpperGstin(Get(preview, "buyer", "gstin", "value"));
        var clientGstin = UpperGstin(client.Gstin ?? string.Empty);
        var sellerChecksum = Get(preview, "supplier", "gstin_checksum_valid").Equals("true", StringComparison.OrdinalIgnoreCase);
        var buyerChecksum = Get(preview, "buyer", "gstin_checksum_valid").Equals("true", StringComparison.OrdinalIgnoreCase);
        if (!sellerChecksum || !buyerChecksum) errors.Add("PARTY_GSTIN_CHECKSUM_UNVERIFIED");
        var clientSeller = clientGstin.Length == 15 && sellerChecksum && clientGstin.Equals(supplierGstin, StringComparison.OrdinalIgnoreCase);
        var clientBuyer = clientGstin.Length == 15 && buyerChecksum && clientGstin.Equals(buyerGstin, StringComparison.OrdinalIgnoreCase);
        if (clientSeller == clientBuyer) errors.Add("CLIENT_GSTIN_DIRECTION_REQUIRES_MANUAL_VERIFICATION");
        var identity = MobileInvoicePartyIdentityResolver.Select(clientGstin,
            supplierName, supplierGstin, buyerName, buyerGstin);
        var direction = identity.Direction;
        var counterpartyName = identity.CounterpartyName;
        var counterpartyGstin = identity.CounterpartyGstin;
        var invoiceNo = Get(preview, "invoice", "number", "value");
        var invoiceDate = Get(preview, "invoice", "date", "value");
        var taxable = Get(preview, "totals", "subtotal", "value");
        var cgst = Get(preview, "totals", "cgst", "value");
        var sgst = Get(preview, "totals", "sgst", "value");
        var grand = Get(preview, "totals", "grand_total", "value");
        var discount = Get(preview, "totals", "discount", "value");
        var roundOff = Get(preview, "totals", "round_off", "value");
        if (!Money(grand, out var invoiceTotal) || invoiceTotal <= 0) errors.Add("POSITIVE_INVOICE_TOTAL_UNVERIFIED");

        JsonObject root;
        try { root = Clone(JsonNode.Parse(originalPayloadJson) as JsonObject); }
        catch (JsonException) { root = new JsonObject(); }
        root["clientId"] = client.Id;
        root["financialYear"] = client.FinancialYear;
        root["direction"] = direction;
        root["directionConfidence"] = direction.Length > 0 ? 100 : 0;
        root["seller"] = new JsonObject { ["name"] = supplierName, ["gstin"] = supplierGstin, ["pan"] = MobileInvoicePartyIdentityResolver.PanFromGstin(supplierGstin) };
        root["buyer"] = new JsonObject { ["name"] = buyerName, ["gstin"] = buyerGstin, ["pan"] = MobileInvoicePartyIdentityResolver.PanFromGstin(buyerGstin) };
        root["counterparty"] = new JsonObject { ["name"] = counterpartyName, ["gstin"] = counterpartyGstin, ["pan"] = identity.CounterpartyPan };
        root["documentType"] = "Tax Invoice";
        root["invoiceNumber"] = invoiceNo;
        root["invoiceDate"] = invoiceDate;
        root["taxableTotal"] = taxable;
        root["discountTotal"] = discount;
        root["cgstTotal"] = cgst;
        root["sgstTotal"] = sgst;
        root["igstTotal"] = string.Empty;
        root["cessTotal"] = string.Empty;
        root["invoiceTotal"] = grand;
        root["roundOff"] = roundOff; // Only a printed round-off with its source text box is accepted; never infer a residual.
        root["otherCharges"] = string.Empty;
        root["summaryLayoutAuthority"] = true;
        root["documentOcrAuthority"] = "AAH R90 ONNX bounded polygon OCR + R89 source-linked review; not automatic posting";
        root["ocrEngine"] = "AAH-RAPIDOCR-ONNX-R90";
        root["ocrSourceSha256"] = source.GetProperty("source_sha256").GetString();
        root["ocrRotationCcw"] = source.GetProperty("rotation_ccw_degrees").GetInt32();
        root["ocrBlockEvidencePath"] = source.GetProperty("block_evidence_path").GetString();
        root["ocrBlockOverlayPath"] = source.GetProperty("block_overlay_path").GetString();
        root["ocrR89Preview"] = JsonNode.Parse(preview.GetRawText()); // Retains every coordinate and discrepancy.

        var rates = new JsonArray();
        var slabs = new JsonArray();
        var taxableSlab = preview.GetProperty("gst").GetProperty("slabs");
        foreach (var slab in taxableSlab.EnumerateArray())
        {
            var rateText = Get(slab, "gst_rate", "value");
            var baseText = Get(slab, "taxable", "value");
            if (!Money(rateText, out var rate) || !Money(baseText, out var baseAmount) || rate <= 0m || baseAmount <= 0m) continue;
            rates.Add(JsonValue.Create(rateText));
            slabs.Add(new JsonObject
            {
                ["gstRate"] = rateText, ["taxable"] = baseText, ["cgst"] = Get(slab, "cgst", "value"),
                ["sgst"] = Get(slab, "sgst", "value"), ["igst"] = string.Empty
            });
        }
        root["gstSlabRates"] = rates;
        root["gstSlabs"] = slabs;
        var rows = new JsonArray();
        foreach (var item in preview.GetProperty("goods").GetProperty("rows").EnumerateArray())
        {
            var description = Get(item, "description", "value");
            var rowTaxable = Get(item, "line_amount", "value");
            var rowRate = Get(item, "gst_rate", "value");
            if (description.Length == 0 || !Money(rowTaxable, out var val) || val <= 0)
            {
                errors.Add("GOODS_ROW_INCOMPLETE_OR_NON_TAXABLE");
                continue;
            }
            var row = new JsonObject
            {
                ["description"] = description, ["hsnSac"] = Get(item, "hsn", "value"),
                ["quantity"] = Get(item, "quantity", "value"), ["unit"] = string.Empty,
                ["rate"] = Get(item, "rate", "value"), ["discount"] = string.Empty,
                ["taxable"] = rowTaxable, ["gstRate"] = rowRate,
                ["cgst"] = string.Empty, ["sgst"] = string.Empty, ["igst"] = string.Empty,
                ["cess"] = string.Empty, ["lineTotal"] = rowTaxable,
                ["stockItem"] = string.Empty, ["ledger"] = string.Empty,
                ["sourceText"] = description, ["derivation"] = "R89 source polygon, needs row tax verification"
            };
            rows.Add(row);
        }
        root["rows"] = rows;
        // A printed goods amount must reconcile exactly to printed taxable; never correct by ₹0.01 silently.
        if (Money(taxable, out var summaryValue))
        {
            var rowSum = rows.OfType<JsonObject>().Sum(r => Money(r["taxable"]?.ToString() ?? "", out var amount) ? amount : 0m);
            if (rowSum != summaryValue) errors.Add("ITEM_SUM_VS_SUBTOTAL_DISCREPANCY");
        }
        if (rows.Count == 0) errors.Add("NO_VERIFIED_GOODS_ROW");
        if (slabs.Count == 0 && Money(taxable, out var positiveTaxable) && positiveTaxable > 0m) errors.Add("GST_SLAB_UNVERIFIED");
        root["ocrBlockingFlags"] = new JsonArray(errors.Distinct(StringComparer.OrdinalIgnoreCase).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        root["ocrRequiresExplicitReconciliation"] = errors.Count > 0;
        root["ocrValidatedImportAllowed"] = false;

        var rawText = new StringBuilder();
        rawText.AppendLine("WINDOWS ONNX R90 SPATIAL INVOICE EVIDENCE (UNVERIFIED FOR POSTING)");
        rawText.AppendLine("Source SHA-256: " + root["ocrSourceSha256"]);
        rawText.AppendLine("Rotation CCW: " + root["ocrRotationCcw"]);
        rawText.AppendLine("Supplier: " + supplierName + " / " + supplierGstin);
        rawText.AppendLine("Buyer: " + buyerName + " / " + buyerGstin);
        rawText.AppendLine("Document: " + invoiceNo + " / " + invoiceDate);
        rawText.AppendLine("R89 review flags: " + string.Join(", ", errors.Distinct()));
        foreach (var block in source.GetProperty("parsing_res_list").EnumerateArray())
            rawText.AppendLine(block.GetProperty("block_bbox").ToString() + " " + block.GetProperty("block_content").GetString());

        return new MobileInvoiceDocumentOcrResult
        {
            WasSuccessful = true,
            PayloadJson = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            RawOcrText = rawText.ToString(),
            CandidatesJson = "{}",
            CounterpartyName = counterpartyName,
            CounterpartyGstin = counterpartyGstin,
            InvoiceNumber = invoiceNo, InvoiceDate = invoiceDate,
            Direction = direction, RecognitionMode = root["recognitionMode"]?.ToString() ?? "Accounting Invoice",
            InvoiceTotal = grand, ItemRowCount = rows.Count, SummarySlabCount = slabs.Count,
            TableCount = 2, Authority = "AAH R90 RapidOCR/ONNX polygons + R89 accounting review; human verification required"
        };
    }
}


// R92: One tax-identity authority for the ONNX parser and Phone Link review.
// Spatial OCR identifies independent seller/buyer; the registered Client GSTIN
// identifies which side owns this workspace. Never guess a party by fuzzy spelling.
internal static class MobileInvoicePartyIdentityResolver
{
    internal sealed record PartySelection(string Direction, string CounterpartyName, string CounterpartyGstin,
        string CounterpartyPan, bool ClientIdentityMatched);

    internal static string NormalizeGstin(string? value) => new((value ?? string.Empty).ToUpperInvariant()
        .Where(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z').ToArray());

    internal static bool ValidGstin(string? value)
    {
        var s = NormalizeGstin(value);
        if (s.Length != 15 || !char.IsDigit(s[0]) || !char.IsDigit(s[1]) ||
            !System.Text.RegularExpressions.Regex.IsMatch(s, @"^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][0-9A-Z][Z][0-9A-Z]$")) return false;
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var sum = 0;
        for (var i = 0; i < 14; i++)
        {
            var index = alphabet.IndexOf(s[13 - i]);
            if (index < 0) return false;
            var product = index * (i % 2 == 0 ? 2 : 1);
            sum += product / 36 + product % 36;
        }
        return alphabet[(36 - sum % 36) % 36] == s[14];
    }

    internal static string PanFromGstin(string? gstin)
    {
        var s = NormalizeGstin(gstin);
        return ValidGstin(s) ? s.Substring(2, 10) : string.Empty;
    }

    internal static PartySelection Select(string? clientGstin, string? supplierName, string? supplierGstin,
        string? buyerName, string? buyerGstin, string? requestedDirection = null)
    {
        var client = NormalizeGstin(clientGstin);
        var supplier = NormalizeGstin(supplierGstin);
        var buyer = NormalizeGstin(buyerGstin);
        var clientIsSupplier = ValidGstin(client) && ValidGstin(supplier) && client == supplier;
        var clientIsBuyer = ValidGstin(client) && ValidGstin(buyer) && client == buyer;
        var uniqueClientMatch = clientIsSupplier != clientIsBuyer;
        // A uniquely recognized registered tax identity overrides stale OCR/phone direction.
        var direction = uniqueClientMatch ? clientIsBuyer ? "Purchase" : "Sales" :
            requestedDirection is "Purchase" or "Sales" ? requestedDirection : string.Empty;
        var oppositeName = direction == "Purchase" ? supplierName?.Trim() ?? string.Empty :
                           direction == "Sales" ? buyerName?.Trim() ?? string.Empty : string.Empty;
        var oppositeGstin = direction == "Purchase" ? supplier : direction == "Sales" ? buyer : string.Empty;
        return new PartySelection(direction, oppositeName, ValidGstin(oppositeGstin) ? oppositeGstin : string.Empty,
            PanFromGstin(oppositeGstin), uniqueClientMatch);
    }

    internal static PartySelection Select(JsonObject root, string? clientGstin, string? requestedDirection = null) =>
        Select(clientGstin, root["seller"]?["name"]?.ToString(), root["seller"]?["gstin"]?.ToString(),
            root["buyer"]?["name"]?.ToString(), root["buyer"]?["gstin"]?.ToString(), requestedDirection);


}
