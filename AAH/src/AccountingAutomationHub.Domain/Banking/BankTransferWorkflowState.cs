namespace AccountingAutomationHub.Domain.Banking;

/// <summary>
/// Shared authority for Inter-Bank workflow visibility.
/// A candidate match is not a completed transaction match until its required transaction
/// steps have finished. For Imported-to-Tally this means same-MASTERID Contra apply/live
/// verification and imported-duplicate retirement. Final Balance Reconciliation is the last
/// account-level control; a remaining balance difference is surfaced separately and does not
/// undo a transaction pair whose Inter-Bank conditions and Tally Contra are already verified.
/// </summary>
public static class BankTransferWorkflowState
{
    public static bool IsTallyMatch(BankTransferMatch match) =>
        match.MatchOrigin.Equals("Tally Converted Bank Transaction", StringComparison.OrdinalIgnoreCase) ||
        match.MatchId.StartsWith("TALLYTRF|", StringComparison.OrdinalIgnoreCase);

    public static bool IsCompleted(BankTransferMatch match)
    {
        if (!IsTallyMatch(match))
            return match.Status.Equals("Matched", StringComparison.OrdinalIgnoreCase);

        return match.Status.Equals("Tally Contra Applied - Balance Verified", StringComparison.OrdinalIgnoreCase) ||
               match.Status.Equals("Tally Contra Already Correct - Balance Verified", StringComparison.OrdinalIgnoreCase) ||
               match.Status.Equals("Tally Contra Applied - Balance Review Required", StringComparison.OrdinalIgnoreCase) ||
               match.Status.Equals("Tally Contra Already Correct - Balance Review Required", StringComparison.OrdinalIgnoreCase);
    }

    public static string ContraDebugStage(BankTransferMatch match)
    {
        var status = (match.Status ?? string.Empty).Trim();
        if (IsCompleted(match))
            return status.Contains("Balance Review Required", StringComparison.OrdinalIgnoreCase)
                ? "COMPLETED - BALANCE REVIEW"
                : "COMPLETED";

        if (status.Contains("Balance Verification Pending", StringComparison.OrdinalIgnoreCase))
            return "BALANCE VERIFICATION PENDING";
        if (status.Contains("Contra Pending", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Awaiting Contra Apply", StringComparison.OrdinalIgnoreCase))
            return "TALLY CONTRA PENDING";
        if (status.Contains("Balance Review Required", StringComparison.OrdinalIgnoreCase))
            return "BALANCE REVIEW REQUIRED";
        if (status.Contains("Review Required", StringComparison.OrdinalIgnoreCase))
            return "REVIEW REQUIRED";
        if (status.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Error", StringComparison.OrdinalIgnoreCase))
            return "FAILED";
        if (status.Contains("Matched", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Match Found", StringComparison.OrdinalIgnoreCase))
            return "MATCH FOUND";

        return string.IsNullOrWhiteSpace(status) ? "PENDING" : status.ToUpperInvariant();
    }
}
