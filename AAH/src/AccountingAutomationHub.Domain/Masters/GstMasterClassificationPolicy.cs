using AccountingAutomationHub.Domain.Journals;

namespace AccountingAutomationHub.Domain.Masters;

/// <summary>
/// Fix82 deterministic GST asset-side Master naming/grouping. This policy is deliberately
/// limited to receivable/not-in-2B asset classification; payable/credit-utilisation logic
/// remains unchanged for the later credit-ledger phase.
/// </summary>
public static class GstMasterClassificationPolicy
{
    public const string LoansAndAdvancesAssetGroup = "Loans & Advances (Asset)";
    public const string GstReceivableGroup = "GST Receivable";
    public const string InputNotShownInTwoBGroup = "Input Not Shown in 2B";
    public const string SingleGstReceivableLedger = "GST Receivable";
    public const string IgstReceivableLedger = "IGST Receivable";
    public const string CgstReceivableLedger = "CGST Receivable";
    public const string SgstReceivableLedger = "SGST Receivable";

    public static bool IsReceivableMode(JournalGenerationSettings settings) =>
        settings.BalanceType.Equals("Receivable", StringComparison.OrdinalIgnoreCase);

    public static bool IsSeparateReceivableMode(JournalGenerationSettings settings) =>
        IsReceivableMode(settings) &&
        settings.SettlementLedgerMode.Equals("Separate Ledgers", StringComparison.OrdinalIgnoreCase);

    public static string NormalizeSingleSettlementLedger(string? configured, bool receivable)
    {
        var value = (configured ?? string.Empty).Trim();
        if (!receivable) return value.Length > 0 ? value : "GST Payable / Receivable";
        return value.Length == 0 || value.Equals("GST Payable / Receivable", StringComparison.OrdinalIgnoreCase)
            ? SingleGstReceivableLedger
            : value;
    }

    public static string NormalizeTaxSettlementLedger(string taxType, string? configured, bool receivable)
    {
        var tax = (taxType ?? string.Empty).Trim().ToUpperInvariant();
        var value = (configured ?? string.Empty).Trim();
        var legacy = tax switch
        {
            "IGST" => "IGST Payable / Receivable",
            "CGST" => "CGST Payable / Receivable",
            "SGST" => "SGST Payable / Receivable",
            _ => string.Empty
        };
        if (!receivable) return value.Length > 0 ? value : legacy;
        if (value.Length > 0 && !value.Equals(legacy, StringComparison.OrdinalIgnoreCase)) return value;
        return tax switch
        {
            "IGST" => IgstReceivableLedger,
            "CGST" => CgstReceivableLedger,
            "SGST" => SgstReceivableLedger,
            _ => value
        };
    }

    public static void NormalizeReceivableSettlementNames(JournalGenerationSettings settings)
    {
        if (!IsReceivableMode(settings)) return;
        settings.SingleSettlementLedger = NormalizeSingleSettlementLedger(settings.SingleSettlementLedger, receivable: true);
        settings.IgstSettlementLedger = NormalizeTaxSettlementLedger("IGST", settings.IgstSettlementLedger, receivable: true);
        settings.CgstSettlementLedger = NormalizeTaxSettlementLedger("CGST", settings.CgstSettlementLedger, receivable: true);
        settings.SgstSettlementLedger = NormalizeTaxSettlementLedger("SGST", settings.SgstSettlementLedger, receivable: true);
    }

    public static string EffectiveSingleSettlementLedger(JournalGenerationSettings settings) =>
        NormalizeSingleSettlementLedger(settings.SingleSettlementLedger, IsReceivableMode(settings));

    public static string EffectiveIgstSettlementLedger(JournalGenerationSettings settings) =>
        NormalizeTaxSettlementLedger("IGST", settings.IgstSettlementLedger, IsReceivableMode(settings));

    public static string EffectiveCgstSettlementLedger(JournalGenerationSettings settings) =>
        NormalizeTaxSettlementLedger("CGST", settings.CgstSettlementLedger, IsReceivableMode(settings));

    public static string EffectiveSgstSettlementLedger(JournalGenerationSettings settings) =>
        NormalizeTaxSettlementLedger("SGST", settings.SgstSettlementLedger, IsReceivableMode(settings));

    public static bool IsNotShownInTwoBLedger(string? ledgerName) =>
        (ledgerName ?? string.Empty).Trim().EndsWith(" - Not shown in 2B", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Name-only deterministic parent used by Tally master preflight. It intentionally
    /// covers only the standard generated names whose asset classification is unambiguous.
    /// </summary>
    public static string ExpectedKnownAssetParent(string? ledgerName)
    {
        var name = (ledgerName ?? string.Empty).Trim();
        if (name.Length == 0) return string.Empty;
        if (IsNotShownInTwoBLedger(name)) return InputNotShownInTwoBGroup;
        if (name.Equals(SingleGstReceivableLedger, StringComparison.OrdinalIgnoreCase))
            return LoansAndAdvancesAssetGroup;
        if (name.Equals(IgstReceivableLedger, StringComparison.OrdinalIgnoreCase) ||
            name.Equals(CgstReceivableLedger, StringComparison.OrdinalIgnoreCase) ||
            name.Equals(SgstReceivableLedger, StringComparison.OrdinalIgnoreCase))
            return GstReceivableGroup;
        return string.Empty;
    }

    public static string ExpectedSettlementParent(string? ledgerName, JournalGenerationSettings settings)
    {
        var name = (ledgerName ?? string.Empty).Trim();
        if (name.Length == 0) return string.Empty;
        if (IsNotShownInTwoBLedger(name)) return InputNotShownInTwoBGroup;
        if (!IsReceivableMode(settings)) return string.Empty;

        if (settings.SettlementLedgerMode.Equals("Separate Ledgers", StringComparison.OrdinalIgnoreCase))
        {
            if (name.Equals(EffectiveIgstSettlementLedger(settings), StringComparison.OrdinalIgnoreCase) ||
                name.Equals(EffectiveCgstSettlementLedger(settings), StringComparison.OrdinalIgnoreCase) ||
                name.Equals(EffectiveSgstSettlementLedger(settings), StringComparison.OrdinalIgnoreCase))
                return GstReceivableGroup;
            return string.Empty;
        }

        return name.Equals(EffectiveSingleSettlementLedger(settings), StringComparison.OrdinalIgnoreCase)
            ? LoansAndAdvancesAssetGroup
            : string.Empty;
    }
}
