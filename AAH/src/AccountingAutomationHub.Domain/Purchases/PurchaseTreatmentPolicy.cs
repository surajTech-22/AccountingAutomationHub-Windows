using System.Globalization;

namespace AccountingAutomationHub.Domain.Purchases;

/// <summary>
/// Authoritative accounting interpretation of Purchase / Debit Note / RCM treatment.
/// Source tax figures remain immutable accounting evidence on the row. This policy only
/// derives the posting shape (claimable Input GST versus cost, and RCM claim timing).
/// </summary>
public static class PurchaseTreatmentPolicy
{
    public const string ShownInTwoB = "Shown in 2B";
    public const string NotShownInTwoB = "Not shown in 2B";
    public const string GstIncludedInCost = "GST included in cost";
    public const string NotIncludedInPurchase = "Not Included in Purchase";
    public const string IncludedInPurchase = "Included in Purchase";
    public const string DebitNoteDestination = "Debit Note";
    public const string DebitNoteReverseDestination = "Debit Note Reverse";
    public const string RcmSameMonth = "Claimed in Same Month";
    public const string RcmNextMonth = "Claimed in Next Month";
    public const string RcmReversed = "Reversed";

    public static bool IsRcm(PurchaseImportRecord row) =>
        row.Destination.Equals("RCM", StringComparison.OrdinalIgnoreCase);

    public static bool IsDebitNote(PurchaseImportRecord row) =>
        row.Destination.Equals(DebitNoteDestination, StringComparison.OrdinalIgnoreCase);

    public static bool IsDebitNoteReverse(PurchaseImportRecord row) =>
        row.Destination.Equals(DebitNoteReverseDestination, StringComparison.OrdinalIgnoreCase);

    public static bool IsManualBooksImport(PurchaseImportRecord row) =>
        row.ImportMode.Equals("Manual Import", StringComparison.OrdinalIgnoreCase) ||
        row.ReconciliationStatus.Equals("Manual Purchase Register - Books", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Supplier credit notes reduce normal Purchase ITC. Supplier debit notes increase
    /// normal Purchase ITC. Debit Note Reverse therefore has the same GST-input sign as
    /// a Purchase while the existing Debit Note destination keeps the reversing sign.
    /// Tally export maps Debit Note Reverse to Credit Note without changing this books sign.
    /// </summary>
    public static decimal NormalInputGstSign(PurchaseImportRecord row) =>
        IsDebitNote(row) ? -1m : 1m;

    public static bool IsNotShownInTwoB(string? value)
    {
        var normalized = Normalize(value);
        return normalized is
            "NOT SHOWN IN 2B" or
            "NOT IN 2B" or
            "NOT SHOWN" or
            "NO 2B";
    }

    public static bool IsGstIncludedInCost(string? value)
    {
        var normalized = Normalize(value);
        return normalized is
            "GST INCLUDED IN COST" or
            "INCLUDED IN COST" or
            "COST" or
            "MANUAL" or
            "MANUAL LEDGER" or
            "CAPITALISED" or
            "CAPITALIZED";
    }

    public static bool IsTwoBNotIncluded(string? value) =>
        Normalize(value) is "NOT INCLUDED IN PURCHASE" or "NOT INCLUDED";

    public static bool IsTwoBIncluded(string? value) =>
        Normalize(value) is "INCLUDED IN PURCHASE" or "INCLUDED";

    public static bool IsNormalInputGstEligible(string? value) =>
        !IsNotShownInTwoB(value) &&
        !IsGstIncludedInCost(value) &&
        !IsTwoBNotIncluded(value);

    public static string NormalizePurchaseTreatment(string? value)
    {
        if (IsGstIncludedInCost(value)) return GstIncludedInCost;
        if (IsNotShownInTwoB(value)) return NotShownInTwoB;
        return ShownInTwoB;
    }

    public static bool ReconciliationSaysTwoBMissing(string? status)
    {
        var normalized = Normalize(status);
        return normalized is "2A ONLY" or "NOT FOUND IN 2B" or "NOT SHOWN IN 2B" ||
               normalized.Contains("NOT FOUND IN 2B", StringComparison.Ordinal) ||
               normalized.Contains("NOT SHOWN IN 2B", StringComparison.Ordinal);
    }

    public static bool ReconciliationSaysTwoBRejected(string? status) =>
        Normalize(status).Contains("REJECTED IN 2B", StringComparison.Ordinal);

    public static bool ReconciliationSaysTwoBPresent(string? status)
    {
        var normalized = Normalize(status);
        if (normalized.Length == 0 || ReconciliationSaysTwoBMissing(status)) return false;
        return normalized.Contains("MATCHED", StringComparison.Ordinal) ||
               normalized.Contains("AMENDED IN 2B", StringComparison.Ordinal) ||
               normalized.Contains("DIFFERENCE", StringComparison.Ordinal) ||
               normalized.Contains("2B", StringComparison.Ordinal);
    }

    public static string SourceBasedPurchaseTreatment(PurchaseImportRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (ReconciliationSaysTwoBRejected(row.ReconciliationStatus))
            return GstIncludedInCost;
        if (ReconciliationSaysTwoBMissing(row.ReconciliationStatus))
            return NotShownInTwoB;
        if (ReconciliationSaysTwoBPresent(row.ReconciliationStatus) ||
            !string.IsNullOrWhiteSpace(row.Source2BFile) ||
            !string.IsNullOrWhiteSpace(row.Source2BPath))
            return ShownInTwoB;
        return NormalizePurchaseTreatment(row.GstApplicability);
    }

    public static IReadOnlyList<string> GetAllowedPurchaseTreatments(PurchaseImportRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (ReconciliationSaysTwoBRejected(row.ReconciliationStatus))
            return new[] { GstIncludedInCost };

        var sourceTreatment = SourceBasedPurchaseTreatment(row);
        return new[] { sourceTreatment, GstIncludedInCost }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string NormalizeRcmTreatment(string? value)
    {
        var normalized = Normalize(value);
        if (normalized.Contains("NEXT", StringComparison.Ordinal))
        {
            return RcmNextMonth;
        }

        if (normalized.Contains("REVERSE", StringComparison.Ordinal) ||
            normalized.Contains("NOT CLAIM", StringComparison.Ordinal) ||
            normalized.Contains("NO ITC", StringComparison.Ordinal))
        {
            return RcmReversed;
        }

        return RcmSameMonth;
    }

    /// <summary>
    /// Rebuild all treatment-derived fields from the preserved source figures.
    /// This is deliberately deterministic: there is no dependency on the previous
    /// UI value, so reopening a client or changing the same row repeatedly cannot
    /// leave stale Purchase/Input-GST ledger state behind.
    /// </summary>
    public static void ApplyAccountingShape(PurchaseImportRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);

        // GSTR-2B-only rows are reconciliation evidence until the user explicitly
        // promotes the complete, validated invoice into accounting. Keeping this gate
        // here prevents Journal/Tally/ledger-total consumers from treating retained 2B
        // evidence as a Purchase merely because the source section resembles B2B.
        if (row.IsTwoBOnlyEvidence &&
            (row.Destination.Equals("2B Only", StringComparison.OrdinalIgnoreCase) ||
             IsTwoBNotIncluded(row.GstApplicability)))
        {
            row.Destination = "2B Only";
            row.GstApplicability = NotIncludedInPurchase;
            row.PurchaseLedger = string.Empty;
            row.IgstLedger = string.Empty;
            row.CgstLedger = string.Empty;
            row.SgstLedger = string.Empty;
            return;
        }

        if (IsRcm(row))
        {
            row.GstApplicability = NormalizeRcmTreatment(row.GstApplicability);

            // Confirmed RCM source accounting: the Purchase voucher keeps the taxable
            // base against the supplier AND self-accounts RCM tax as rate-wise RCM Input
            // debit against rate-wise RCM Payable credit. The supplier remains base-only.
            // Column A controls only later RCM reclassification (next-month / reversal);
            // it must never transfer RCM Input into an ordinary Input GST ledger.
            if (string.IsNullOrWhiteSpace(row.PurchaseLedger))
                row.PurchaseLedger = DefaultPurchaseLedger(row.Rate);

            // RCM sheet P:R keep the rate-wise RCM Input ledger identity used by
            // the source Purchase voucher. Ordinary Input GST ledger names are never
            // substituted for these RCM Input ledgers.
            ApplyRcmInputLedgerReferences(row);
            return;
        }

        if (row.IsTwoBOnlyEvidence && IsTwoBIncluded(row.GstApplicability))
        {
            row.GstApplicability = IncludedInPurchase;
            if (string.IsNullOrWhiteSpace(row.PurchaseLedger) ||
                IsAutomaticPurchaseLedger(row.PurchaseLedger))
            {
                row.PurchaseLedger = DefaultPurchaseLedger(row.Rate);
            }
            ApplyStandardInputLedgerReferences(row, string.Empty);
            return;
        }

        // Fix116 R1: Manual Purchase Register rows are books evidence and deliberately
        // bypass the 2B/GST-treatment classifier. They flow exactly like an eligible
        // normal Purchase for Journal/GSTR-3B/Tally, while the GST Treatment cell stays
        // blank. Automatic/statutory Purchase rows continue through the normal 2B rules.
        if (IsManualBooksImport(row))
        {
            row.GstApplicability = string.Empty;
            if (string.IsNullOrWhiteSpace(row.PurchaseLedger) ||
                IsAutomaticPurchaseLedger(row.PurchaseLedger))
            {
                row.PurchaseLedger = DefaultPurchaseLedger(row.Rate);
            }
            ApplyStandardInputLedgerReferences(row, string.Empty);
            return;
        }

        // 2B availability is source evidence, not a row-position/UI choice.
        // A matched/found row cannot silently become "Not shown in 2B" merely
        // because a virtualized selector was recreated. The only deliberate
        // non-RCM override retained across source refreshes is GST included in cost.
        row.GstApplicability = IsGstIncludedInCost(row.GstApplicability)
            ? GstIncludedInCost
            : SourceBasedPurchaseTreatment(row);

        if (IsGstIncludedInCost(row.GstApplicability))
        {
            // Non-creditable GST is absorbed by one Purchase/Expense cost ledger.
            // Automatic Purchase@rate and all automatic Input-GST ledgers are not
            // valid posting destinations for this treatment.
            if (IsAutomaticPurchaseLedger(row.PurchaseLedger))
                row.PurchaseLedger = string.Empty;

            row.IgstLedger = string.Empty;
            row.CgstLedger = string.Empty;
            row.SgstLedger = string.Empty;
            return;
        }

        if (string.IsNullOrWhiteSpace(row.PurchaseLedger) ||
            IsAutomaticPurchaseLedger(row.PurchaseLedger))
        {
            row.PurchaseLedger = DefaultPurchaseLedger(row.Rate);
        }

        var suffix = IsNotShownInTwoB(row.GstApplicability)
            ? " - Not shown in 2B"
            : string.Empty;
        ApplyStandardInputLedgerReferences(row, suffix);
    }

    public static string DefaultPurchaseLedger(decimal rate) =>
        rate == 0m
            ? "Exempted Purchase"
            : $"Purchase @ {FormatRate(rate)}%";

    public static bool IsAutomaticPurchaseLedger(string? ledgerName)
    {
        var value = (ledgerName ?? string.Empty).Trim();
        return value.Equals("Exempted Purchase", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("Purchase @ ", StringComparison.OrdinalIgnoreCase);
    }

    public static decimal EffectivePurchaseLedgerAmount(PurchaseImportRecord row) =>
        IsGstIncludedInCost(row.GstApplicability)
            ? row.TaxableValue + row.Igst + row.Cgst + row.Sgst + row.Cess
            : row.TaxableValue;

    public static decimal EffectiveInputIgst(PurchaseImportRecord row) =>
        IsGstIncludedInCost(row.GstApplicability) ? 0m : row.Igst;

    public static decimal EffectiveInputCgst(PurchaseImportRecord row) =>
        IsGstIncludedInCost(row.GstApplicability) ? 0m : row.Cgst;

    public static decimal EffectiveInputSgst(PurchaseImportRecord row) =>
        IsGstIncludedInCost(row.GstApplicability) ? 0m : row.Sgst;

    public static decimal EffectiveInputCess(PurchaseImportRecord row) =>
        IsGstIncludedInCost(row.GstApplicability) ? 0m : row.Cess;

    private static void ApplyStandardInputLedgerReferences(
        PurchaseImportRecord row,
        string suffix)
    {
        row.IgstLedger = string.Empty;
        row.CgstLedger = string.Empty;
        row.SgstLedger = string.Empty;

        if (Math.Abs(row.Igst) > 0.005m)
        {
            row.IgstLedger = $"Input IGST @ {FormatRate(row.Rate)}%{suffix}";
            return;
        }

        var halfRate = FormatRate(row.Rate / 2m);
        if (Math.Abs(row.Cgst) > 0.005m)
            row.CgstLedger = $"Input CGST @ {halfRate}%{suffix}";
        if (Math.Abs(row.Sgst) > 0.005m)
            row.SgstLedger = $"Input SGST @ {halfRate}%{suffix}";
    }

    public static string BuildRcmInputLedgerName(
        string? sourceLedger,
        string? configuredLedger,
        string taxType,
        decimal taxRate)
    {
        var configured = (configuredLedger ?? string.Empty).Trim();
        var defaultName = $"Input {taxType} RCM";
        if (configured.Length > 0 &&
            !configured.Equals(defaultName, StringComparison.OrdinalIgnoreCase) &&
            !configured.Contains("TEMP", StringComparison.OrdinalIgnoreCase))
        {
            return configured.Contains("RCM", StringComparison.OrdinalIgnoreCase)
                ? configured
                : $"{configured} RCM";
        }

        var source = (sourceLedger ?? string.Empty).Trim();
        if (source.Length > 0)
        {
            if (source.Contains("RCM", StringComparison.OrdinalIgnoreCase))
                return source.Replace("Temporary ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            return $"{source} RCM";
        }

        return taxRate > 0m
            ? $"Input {taxType} @ {FormatRate(taxRate)}% RCM"
            : defaultName;
    }

    public static string BuildRcmPayableLedgerName(string taxType, decimal taxRate) =>
        taxRate > 0m
            ? $"{taxType} RCM Payable @ {FormatRate(taxRate)}%"
            : $"{taxType} RCM Payable";


    public const string RcmInputExpensesLedger = "RCM Input Expenses";

    public static string BuildRcmReceivableLedgerName(string taxType, decimal taxRate) =>
        taxRate > 0m
            ? $"{taxType} RCM Input Receivable @ {FormatRate(taxRate)}%"
            : $"{taxType} RCM Input Receivable";

    // Backward-compatible alias used only while older persisted references are migrated.
    public static string BuildRcmPendingLedgerName(string taxType, decimal taxRate) =>
        BuildRcmReceivableLedgerName(taxType, taxRate);

    /// <summary>
    /// Reversed RCM always uses one shared expense account.  Tax head/rate remain on
    /// the RCM Input credit lines and Journal metadata; they never create separate
    /// expense Masters.
    /// </summary>
    public static string BuildRcmExpenseLedgerName(
        string? configuredLedger,
        string taxType,
        decimal taxRate)
    {
        _ = configuredLedger;
        _ = taxType;
        _ = taxRate;
        return RcmInputExpensesLedger;
    }

    public static string ExpectedGeneratedRcmParent(string? ledgerName)
    {
        var name = (ledgerName ?? string.Empty).Trim();
        if (name.Length == 0) return string.Empty;

        if (name.Equals(RcmInputExpensesLedger, StringComparison.OrdinalIgnoreCase) ||
            (name.StartsWith("RCM ", StringComparison.OrdinalIgnoreCase) &&
             name.Contains(" Expense", StringComparison.OrdinalIgnoreCase) &&
             (name.Contains("IGST", StringComparison.OrdinalIgnoreCase) ||
              name.Contains("CGST", StringComparison.OrdinalIgnoreCase) ||
              name.Contains("SGST", StringComparison.OrdinalIgnoreCase))))
        {
            return "Indirect Expenses";
        }

        if ((name.Contains("RCM Input Receivable", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("RCM Input Pending", StringComparison.OrdinalIgnoreCase)) &&
            (name.Contains("IGST", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("CGST", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("SGST", StringComparison.OrdinalIgnoreCase)))
        {
            return "Current Assets";
        }

        if (name.Contains("RCM Payable", StringComparison.OrdinalIgnoreCase) ||
            (name.StartsWith("Input ", StringComparison.OrdinalIgnoreCase) &&
             name.Contains(" RCM", StringComparison.OrdinalIgnoreCase)))
        {
            return "Duties & Taxes";
        }

        return string.Empty;
    }

    public static bool IsGeneratedRcmClassificationLedger(string? ledgerName) =>
        ExpectedGeneratedRcmParent(ledgerName).Length > 0;

    private static void ApplyRcmInputLedgerReferences(PurchaseImportRecord row)
    {
        row.IgstLedger = string.Empty;
        row.CgstLedger = string.Empty;
        row.SgstLedger = string.Empty;

        if (Math.Abs(row.Igst) > 0.005m)
        {
            row.IgstLedger = $"Input IGST @ {FormatRate(row.Rate)}% RCM";
            return;
        }

        var halfRate = FormatRate(row.Rate / 2m);
        if (Math.Abs(row.Cgst) > 0.005m)
            row.CgstLedger = $"Input CGST @ {halfRate}% RCM";
        if (Math.Abs(row.Sgst) > 0.005m)
            row.SgstLedger = $"Input SGST @ {halfRate}% RCM";
    }

    public static string FormatRate(decimal value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Normalize(string? value) =>
        (value ?? string.Empty)
            .Trim()
            .ToUpperInvariant();
}
