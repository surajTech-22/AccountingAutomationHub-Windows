using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Governance;

namespace AccountingAutomationHub.Infrastructure.Governance;

/// <summary>
/// Preview-first advanced bank matching. It records an explicit reconciliation
/// group without mutating the original Bank Statement evidence rows. A committed
/// group can represent many bank rows to one book reference, one bank row to many
/// book references, or a partial/adjusted settlement.
/// </summary>
public sealed class AdvancedBankReconciliationService(SqliteGovernanceRepository governance)
{
    public AdvancedReconciliationPreview Preview(
        IReadOnlyList<BankTransactionRecord> bankTransactions,
        decimal bookAmount,
        IEnumerable<string> bookReferences,
        decimal adjustmentAmount = 0m,
        string adjustmentLedger = "")
    {
        if (bankTransactions.Count == 0)
            throw new InvalidOperationException("Select at least one Bank Statement transaction.");
        var clientId = bankTransactions[0].ClientId;
        var fy = bankTransactions[0].FinancialYear;
        if (bankTransactions.Any(x => x.ClientId != clientId || !x.FinancialYear.Equals(fy, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Advanced reconciliation can only combine one client and Financial Year at a time.");

        var refs = bookReferences.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (refs.Length == 0) refs = ["Manual Book Reference"];
        var bankAmount = bankTransactions.Sum(x => Math.Abs(x.Amount));
        var effectiveBook = Math.Abs(bookAmount) + Math.Abs(adjustmentAmount);
        var difference = bankAmount - effectiveBook;
        var type = bankTransactions.Count > 1 && refs.Length == 1 ? "Many → One"
            : bankTransactions.Count == 1 && refs.Length > 1 ? "One → Many"
            : Math.Abs(difference) > 0.005m || adjustmentAmount != 0m ? "Partial / Adjustment"
            : "Matched";
        var preview = new AdvancedReconciliationPreview
        {
            MatchId = $"ADV-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..31],
            MatchType = type,
            BankTransactionIds = bankTransactions.Select(x => x.Id).Where(x => x > 0).Distinct().ToArray(),
            BookReferences = refs,
            BankAmount = bankAmount,
            BookAmount = Math.Abs(bookAmount),
            AdjustmentAmount = adjustmentAmount,
            AdjustmentLedger = adjustmentLedger.Trim(),
            Status = "Preview",
            Detail = $"{bankTransactions.Count:N0} bank row(s) ↔ {refs.Length:N0} book reference(s); difference after adjustment {difference:N2}."
        };
        return preview;
    }

    public AdvancedReconciliationPreview Commit(long clientId,string financialYear,AdvancedReconciliationPreview preview)
    {
        if (!governance.HasPermission(Environment.UserName, GovernancePermissions.EditAccounting))
            throw new UnauthorizedAccessException("The current user does not have Edit Accounting permission.");
        var remaining = preview.BankAmount - preview.BookAmount - Math.Abs(preview.AdjustmentAmount);
        if (Math.Abs(remaining) > 0.005m)
            throw new InvalidOperationException($"The match is not balanced. Remaining difference: {remaining:N2}.");
        if (preview.AdjustmentAmount != 0m && string.IsNullOrWhiteSpace(preview.AdjustmentLedger))
            throw new InvalidOperationException("Select an Adjustment Ledger when an adjustment amount is used.");
        preview.Status = "Committed";
        preview.Detail = $"Committed by {Environment.UserName} on {DateTime.Now:dd-MMM-yyyy HH:mm}. {preview.Detail}";
        governance.SaveAdvancedReconciliation(clientId,financialYear,preview);
        governance.RecordVersion(new EntityVersionRecord{ClientId=clientId,FinancialYear=financialYear,Module="Banking",EntityType="AdvancedReconciliation",EntityKey=preview.MatchId,Operation="Commit",AfterJson=System.Text.Json.JsonSerializer.Serialize(preview),ChangedBy=Environment.UserName,Reason=preview.Detail,ChangedUtc=DateTime.UtcNow});
        return preview;
    }
}
