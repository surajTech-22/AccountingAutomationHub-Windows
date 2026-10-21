namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Release-level feature gates. These gates must never delete or rewrite saved client data.
/// They only control whether a module can be exposed or execute runtime/background work.
/// </summary>
public static class ReleaseFeatureGates
{
    // R9 FIXED16 R22 FIXED6 R52 FIXED25:
    // Financial Statements/NCE is intentionally disabled for this release while its
    // plug-in background authority is redesigned. Existing preparation, mappings,
    // snapshots, Basic Info, PY data and CA links remain stored untouched.
    public static readonly bool FinancialStatements = false;

    public const string FinancialStatementsDisabledReason =
        "Financial Statements is temporarily disabled in this AAH release. Existing saved Financial Statements data is retained for future reactivation.";
}
