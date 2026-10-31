namespace AccountingAutomationHub.Desktop.ViewModels;

/// <summary>
/// Clear, non-posting boundary for a branch/Consolidated workspace while the
/// legacy repositories are still ClientId/FY-scoped. Never display shared
/// parent accounting rows as if they were isolated branch data.
/// </summary>
public sealed class BranchScopePreviewViewModel(
    string title, string subtitle, string scopeName, string scopeId,
    string financialYear, string message) : PageViewModel(title, subtitle)
{
    public string ScopeName { get; } = scopeName;
    public string ScopeId { get; } = scopeId;
    public string FinancialYear { get; } = financialYear;
    public string StatusMessage { get; } = message;
    public string ScopeDetail => $"{ScopeName} · FY {FinancialYear} · Scope {ScopeId}";
    public string SafetyNote =>
        "The existing client accounting records are unchanged. Branch imports and Tally voucher posting remain disabled until branch-specific persistence, source reconciliation and read-back tests pass.";
}
