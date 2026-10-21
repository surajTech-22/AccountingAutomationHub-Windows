using AccountingAutomationHub.Domain.Imports;

namespace AccountingAutomationHub.Application.Imports;

/// <summary>
/// Global Action Required is reserved for imports/files that did not complete
/// successfully and therefore need user intervention before they can be imported.
/// Normal accounting review (Ledger Mapping, Suspense, reconciliation, Stock/Unit
/// review, etc.) stays inside its owning module and must not turn the global
/// Action Required state red.
/// </summary>
public static class ActionRequiredPolicy
{
    private static readonly string[] ImportBlockingTypes =
    [
        "IMPORT FAILED",
        "IMPORT BLOCKED",
        "BANK IMPORT REVIEW",
        // Bank OCR pages/PDFs already pass through the production bank statement
        // verification/import authority. Only failed/blocked OCR states generate
        // Action Center entries; normal ledger reconciliation stays in Banking.
        "BANK OCR MONITORING DISABLED",
        "BANK OCR REVIEW",
        "BANK OCR BATCH REVIEW",
        "BANK OCR DOCUMENT REVIEW",
        "BANK OCR BATCH FAILED",
        "UNASSIGNED BANK OCR PAGE",
        "PDF IMPORT REVIEW",
        "PURCHASE IMPORT REVIEW",
        "FILE LOCKED",
        "HEADER MAPPING",
        "UNKNOWN FORMAT",
        "UNSUPPORTED FORMAT",
        "PASSWORD REQUIRED",
        "PASSWORD PROTECTED",
        "CORRUPT FILE",
        "CANNOT IMPORT",
        "COULD NOT IMPORT",
        "PERIOD LOCKED"
    ];

    public static bool IsImportBlocking(ActionRequiredItem? item) =>
        item is not null && IsImportBlockingType(item.ActionType);

    public static bool IsImportBlockingType(string? actionType)
    {
        var value = (actionType ?? string.Empty).Trim().ToUpperInvariant();
        if (value.Length == 0) return false;

        return ImportBlockingTypes.Any(type => value.Equals(type, StringComparison.Ordinal));
    }
}
