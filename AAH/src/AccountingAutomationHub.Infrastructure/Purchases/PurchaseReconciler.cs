using AccountingAutomationHub.Domain.Purchases;

namespace AccountingAutomationHub.Infrastructure.Purchases;

internal sealed class PurchaseReconciler
{
    private const decimal AmountTolerance = 0.01m;

    public IReadOnlyList<PurchaseImportRecord> Reconcile(
        IReadOnlyList<PurchaseSourceRow> rows2A,
        IReadOnlyList<PurchaseSourceRow> rows2B,
        CancellationToken cancellationToken)
    {
        var remaining2B = rows2B
            .GroupBy(
                row => row.DocumentKey,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToList(),
                StringComparer.OrdinalIgnoreCase);

        var records = new List<PurchaseImportRecord>(
            rows2A.Count + rows2B.Count);

        foreach (var row2A in rows2A)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PurchaseSourceRow? row2B = null;

            if (remaining2B.TryGetValue(
                    row2A.DocumentKey,
                    out var candidates) &&
                candidates.Count > 0)
            {
                row2B = candidates
                    .OrderBy(candidate =>
                        DifferenceScore(
                            row2A,
                            candidate))
                    .ThenBy(candidate =>
                        candidate.RowNumber)
                    .First();

                candidates.Remove(row2B);
            }

            records.Add(
                BuildRecord(
                    row2A,
                    row2B));
        }

        foreach (var candidates in remaining2B.Values)
        {
            foreach (var row2B in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                records.Add(
                    BuildRecord(
                        null,
                        row2B));
            }
        }

        return records
            .OrderBy(record =>
                DestinationOrder(
                    record.Destination))
            .ThenBy(record =>
                record.DocumentDate)
            .ThenBy(record =>
                record.SupplierName)
            .ThenBy(record =>
                record.DocumentNumber)
            .ToArray();
    }

    private static PurchaseImportRecord BuildRecord(
        PurchaseSourceRow? row2A,
        PurchaseSourceRow? row2B)
    {
        var source = row2A ?? row2B
            ?? throw new InvalidOperationException(
                "A reconciliation record requires at least one source row.");

        var status = GetReconciliationStatus(
            row2A,
            row2B);

        var destination = status == "2B Only"
            ? "2B Only"
            : GetDestination(source.Section);

        var isRcm = string.Equals(
            destination,
            "RCM",
            StringComparison.OrdinalIgnoreCase);

        var gstApplicability = status == "2B Only"
            ? PurchaseTreatmentPolicy.NotIncludedInPurchase
            : isRcm
                ? PurchaseTreatmentPolicy.RcmSameMonth
                : status == "2A Only"
                    ? PurchaseTreatmentPolicy.NotShownInTwoB
                    : PurchaseTreatmentPolicy.ShownInTwoB;

        var rate = source.Rate;
        var rateKnown = source.RateKnown;

        if (!rateKnown && row2B is not null && row2B.RateKnown)
        {
            rate = row2B.Rate;
            rateKnown = true;
        }

        var taxable = source.TaxableValue;
        var igst = source.Igst;
        var cgst = source.Cgst;
        var sgst = source.Sgst;

        // Fix46: a literal/source-default 0% is never valid when the same line carries
        // non-zero IGST/CGST/SGST. Reconciliation used to build ledgers before applying
        // the Fix13 reverse-rate fallback, which could create Input CGST/SGST @ 0%.
        // Derive only when taxable + tax amounts prove one supported GST rate; a true
        // zero-rated/exempt line has no GST amount and therefore never enters this repair.
        if ((!rateKnown || rate == 0m) &&
            (Math.Abs(igst) + Math.Abs(cgst) + Math.Abs(sgst)) > 0.005m &&
            PurchaseImportUtilities.TryDeriveSupportedRate(
                taxable, igst, cgst, sgst, out var reconciledRate))
        {
            rate = reconciledRate;
            rateKnown = true;
        }
        var totalBeforeRound =
            taxable + igst + cgst + sgst;

        decimal documentValue;
        decimal roundOff;

        if (status == "2B Only")
        {
            documentValue = Math.Abs(source.DocumentValue);
            if (documentValue <= 0.005m)
                documentValue = Math.Abs(totalBeforeRound);
            roundOff = documentValue - totalBeforeRound;
        }
        else if (isRcm)
        {
            documentValue = Math.Abs(
                source.DocumentValue);

            if (documentValue <= 0.005m)
            {
                documentValue = Math.Abs(taxable);
            }

            roundOff = 0m;
        }
        else
        {
            documentValue = decimal.Round(
                totalBeforeRound,
                0,
                MidpointRounding.ToEven);

            roundOff =
                documentValue - totalBeforeRound;
        }

        var ledgers = BuildLedgers(
            rate,
            igst,
            gstApplicability,
            isRcm);

        return new PurchaseImportRecord
        {
            Destination = destination,
            ReconciliationStatus = status,
            GstApplicability = gstApplicability,
            SupplierGstin = source.SupplierGstin,
            SupplierName =
                PurchaseImportUtilities.ToDisplayName(
                    source.SupplierName),
            DocumentType = source.DocumentType,
            DocumentNumber = source.DocumentNumber,
            DocumentDate = source.DocumentDate,
            GstDate = PurchaseImportUtilities.CalculateWorkbookGstDate(source.DocumentDate, source.FilingDate, source.ReturnPeriod),
            SourceDocumentValue = source.DocumentValue,
            DocumentValue = documentValue,
            Rate = rate,
            RateKnown = rateKnown,
            TaxableValue = taxable,
            Igst = igst,
            Cgst = cgst,
            Sgst = sgst,
            Cess = source.Cess,
            RoundOff = roundOff,
            PurchaseLedger = ledgers.Purchase,
            IgstLedger = ledgers.Igst,
            CgstLedger = ledgers.Cgst,
            SgstLedger = ledgers.Sgst,
            ItemName = source.ItemName,
            HsnCode = source.HsnCode,
            ActualQuantity = source.ActualQuantity,
            Unit = source.Unit,
            ItemRate = source.ActualQuantity != 0m
                ? Math.Round(taxable / source.ActualQuantity, 6, MidpointRounding.AwayFromZero)
                : source.BilledQuantity != 0m
                    ? Math.Round(taxable / source.BilledQuantity, 6, MidpointRounding.AwayFromZero)
                    : source.ItemRate,
            BilledQuantity = source.BilledQuantity != 0m ? source.BilledQuantity : source.ActualQuantity,
            ItemAmount = source.ItemAmount != 0m ? source.ItemAmount : taxable,
            ItemPurchaseLedger = source.ItemPurchaseLedger.Length > 0 ? source.ItemPurchaseLedger : ledgers.Purchase,
            AccountingAmount = source.ItemAmount != 0m ? source.ItemAmount : taxable,
            Godown = source.Godown,
            BatchName = source.BatchName,
            DiscountPercent = source.DiscountPercent,
            DiscountAmount = source.DiscountAmount,
            OrderNumber = source.OrderNumber,
            TrackingNumber = source.TrackingNumber,
            ManufacturingDate = source.ManufacturingDate,
            ExpiryDate = source.ExpiryDate,
            ItemDescription = source.ItemDescription,
            CostCentre = source.CostCentre,
            FreeQuantity = source.FreeQuantity,
            OriginalItemName = source.ItemName,
            OriginalUnit = source.Unit,
            OriginalActualQuantity = source.ActualQuantity,
            OriginalBilledQuantity = source.BilledQuantity != 0m ? source.BilledQuantity : source.ActualQuantity,
            OriginalItemRate = source.ActualQuantity != 0m
                ? Math.Round(taxable / source.ActualQuantity, 6, MidpointRounding.AwayFromZero)
                : source.BilledQuantity != 0m
                    ? Math.Round(taxable / source.BilledQuantity, 6, MidpointRounding.AwayFromZero)
                    : source.ItemRate,
            OriginalItemAmount = source.ItemAmount != 0m ? source.ItemAmount : taxable,
            SourceSection = source.Section,
            IsAmendment = string.Equals(
                source.Section,
                "B2B Amendment",
                StringComparison.OrdinalIgnoreCase),
            OriginalDocumentNumber =
                source.OriginalDocumentNumber,
            OriginalDocumentDate =
                source.OriginalDocumentDate,
            PlaceOfSupply = source.PlaceOfSupply,
            ReverseCharge = source.ReverseCharge,
            InvoiceType = source.InvoiceType,
            ItcAvailability = source.ItcAvailability,
            Source2AFile = row2A is null
                ? string.Empty
                : Path.GetFileName(
                    row2A.SourceFilePath),
            Source2BFile = row2B is null
                ? string.Empty
                : Path.GetFileName(
                    row2B.SourceFilePath),
            TwoBInvoiceValue =
                row2B?.DocumentValue,
            TwoBTaxableValue =
                row2B?.TaxableValue,
            TwoBIgst =
                row2B?.Igst,
            TwoBCgst =
                row2B?.Cgst,
            TwoBSgst =
                row2B?.Sgst,
            DifferenceInvoiceValue = Difference(
                row2A?.DocumentValue,
                row2B?.DocumentValue),
            DifferenceTaxableValue = Difference(
                row2A?.TaxableValue,
                row2B?.TaxableValue),
            DifferenceIgst = Difference(
                row2A?.Igst,
                row2B?.Igst),
            DifferenceCgst = Difference(
                row2A?.Cgst,
                row2B?.Cgst),
            DifferenceSgst = Difference(
                row2A?.Sgst,
                row2B?.Sgst),
            DifferenceDetails = BuildDifferenceDetails(
                row2A,
                row2B)
        };
    }

    private static string GetReconciliationStatus(
        PurchaseSourceRow? row2A,
        PurchaseSourceRow? row2B)
    {
        if (row2A is null)
        {
            return "2B Only";
        }

        if (row2B is null)
        {
            return "2A Only";
        }

        return HasDifference(
            row2A,
            row2B)
            ? "Value Difference"
            : "Matched";
    }

    private static bool HasDifference(
        PurchaseSourceRow row2A,
        PurchaseSourceRow row2B)
    {
        if (Math.Abs(
                row2A.DocumentValue -
                row2B.DocumentValue) >
            AmountTolerance)
        {
            return true;
        }

        if (Math.Abs(
                row2A.TaxableValue -
                row2B.TaxableValue) >
            AmountTolerance ||
            Math.Abs(
                row2A.Igst -
                row2B.Igst) >
            AmountTolerance ||
            Math.Abs(
                row2A.Cgst -
                row2B.Cgst) >
            AmountTolerance ||
            Math.Abs(
                row2A.Sgst -
                row2B.Sgst) >
            AmountTolerance ||
            Math.Abs(
                row2A.Cess -
                row2B.Cess) >
            AmountTolerance)
        {
            return true;
        }

        return BothPresentAndDifferent(
                row2A.PlaceOfSupply,
                row2B.PlaceOfSupply) ||
            BothPresentAndDifferent(
                row2A.ReverseCharge,
                row2B.ReverseCharge) ||
            BothPresentAndDifferent(
                row2A.DocumentType,
                row2B.DocumentType);
    }

    private static decimal DifferenceScore(
        PurchaseSourceRow left,
        PurchaseSourceRow right) =>
        Math.Abs(
            left.DocumentValue -
            right.DocumentValue) +
        Math.Abs(
            left.TaxableValue -
            right.TaxableValue) +
        Math.Abs(
            left.Igst -
            right.Igst) +
        Math.Abs(
            left.Cgst -
            right.Cgst) +
        Math.Abs(
            left.Sgst -
            right.Sgst) +
        Math.Abs(
            left.Cess -
            right.Cess);

    private static string BuildDifferenceDetails(
        PurchaseSourceRow? row2A,
        PurchaseSourceRow? row2B)
    {
        if (row2A is null)
        {
            return "Document exists only in GSTR-2B.";
        }

        if (row2B is null)
        {
            return "Document exists only in GSTR-2A.";
        }

        var differences = new List<string>();

        AddAmountDifference(
            differences,
            "Invoice value",
            row2A.DocumentValue,
            row2B.DocumentValue);

        AddAmountDifference(
            differences,
            "Taxable",
            row2A.TaxableValue,
            row2B.TaxableValue);

        AddAmountDifference(
            differences,
            "IGST",
            row2A.Igst,
            row2B.Igst);

        AddAmountDifference(
            differences,
            "CGST",
            row2A.Cgst,
            row2B.Cgst);

        AddAmountDifference(
            differences,
            "SGST",
            row2A.Sgst,
            row2B.Sgst);

        AddAmountDifference(
            differences,
            "Cess",
            row2A.Cess,
            row2B.Cess);

        AddTextDifference(
            differences,
            "Place of supply",
            row2A.PlaceOfSupply,
            row2B.PlaceOfSupply);

        AddTextDifference(
            differences,
            "Reverse charge",
            row2A.ReverseCharge,
            row2B.ReverseCharge);

        AddTextDifference(
            differences,
            "Document type",
            row2A.DocumentType,
            row2B.DocumentType);

        return differences.Count == 0
            ? "No difference."
            : string.Join(
                "; ",
                differences);
    }

    private static void AddAmountDifference(
        ICollection<string> differences,
        string label,
        decimal value2A,
        decimal value2B)
    {
        var difference =
            value2A - value2B;

        if (Math.Abs(difference) <=
            AmountTolerance)
        {
            return;
        }

        differences.Add(
            $"{label}: 2A {value2A:N2}, 2B {value2B:N2}, diff {difference:N2}");
    }

    private static void AddTextDifference(
        ICollection<string> differences,
        string label,
        string value2A,
        string value2B)
    {
        if (!BothPresentAndDifferent(
                value2A,
                value2B))
        {
            return;
        }

        differences.Add(
            $"{label}: 2A '{value2A}', 2B '{value2B}'");
    }

    private static bool BothPresentAndDifferent(
        string left,
        string right)
    {
        if (string.IsNullOrWhiteSpace(left) ||
            string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return !string.Equals(
            PurchaseImportUtilities.NormalizeComparisonText(
                left),
            PurchaseImportUtilities.NormalizeComparisonText(
                right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static decimal? Difference(
        decimal? value2A,
        decimal? value2B)
    {
        if (value2A is null ||
            value2B is null)
        {
            return null;
        }

        return value2A.Value - value2B.Value;
    }

    private static PurchaseLedgerSet BuildLedgers(
        decimal rate,
        decimal igst,
        string gstApplicability,
        bool isRcm)
    {
        if (rate == 0m)
        {
            return new PurchaseLedgerSet(
                "Exempted Purchase",
                string.Empty,
                string.Empty,
                string.Empty);
        }

        var rateText =
            PurchaseImportUtilities.FormatRate(
                rate);

        var purchase =
            $"Purchase @ {rateText}%";

        var notShownSuffix = string.Equals(
            gstApplicability,
            "Not shown in 2B",
            StringComparison.OrdinalIgnoreCase)
            ? " - Not shown in 2B"
            : string.Empty;

        var rcmSuffix = isRcm
            ? " RCM"
            : string.Empty;

        if (igst > 0m)
        {
            return new PurchaseLedgerSet(
                purchase,
                $"Input IGST @ {rateText}%{notShownSuffix}{rcmSuffix}",
                string.Empty,
                string.Empty);
        }

        var halfRateText =
            PurchaseImportUtilities.FormatRate(
                rate / 2m);

        return new PurchaseLedgerSet(
            purchase,
            string.Empty,
            $"Input CGST @ {halfRateText}%{notShownSuffix}{rcmSuffix}",
            $"Input SGST @ {halfRateText}%{notShownSuffix}{rcmSuffix}");
    }

    private static string GetDestination(
        string section)
    {
        if (string.Equals(
                section,
                "B2B-RC",
                StringComparison.OrdinalIgnoreCase))
        {
            return "RCM";
        }

        if (string.Equals(
                section,
                "Dr notes-B2B",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Debit Note";
        }

        return "Purchase";
    }

    private static int DestinationOrder(
        string destination) =>
        destination switch
        {
            "Purchase" => 1,
            "RCM" => 2,
            "Debit Note" => 3,
            _ => 9
        };
}
