using AccountingAutomationHub.Domain.Sales;
using AccountingAutomationHub.Infrastructure.Sales;

internal static class SalesAutoImportDiagnosticsTests
{
    public static void Run()
    {
        var state = new SalesAutoImportResult
        {
            ClientGstin = "24AAQCM7513B1ZM",
            FinancialYear = "2025-26",
            Verification = new SalesSourceVerification
            {
                SourceOrigin = "GSTIN Cache",
                SourceBranch = "Shared Sales/Purchase Watcher",
                Status = "Cached Monthly GSTR-1 Selected",
                SelectedPeriods = ["202601", "202602"],
                SelectedSources = ["C:/GST/Jan/one.json", "C:/GST/Feb/two.json"]
            },
            Issues =
            [
                new SalesImportIssue
                {
                    Section = "JSON/ZIP",
                    SourceFile = "March-GSTR1.json",
                    Message = "The JSON value could not be converted at line 344."
                }
            ],
            Warnings = ["One source could not be parsed."]
        };

        var failed = SalesAutoImportDiagnosticFormatter.Build(state, new DateTime(2026, 3, 1));
        Require(failed.Contains("March 2026", StringComparison.Ordinal) &&
                failed.Contains("24AAQCM7513B1ZM", StringComparison.Ordinal) &&
                failed.Contains("Jan 2026", StringComparison.Ordinal) &&
                failed.Contains("Feb 2026", StringComparison.Ordinal) &&
                failed.Contains("GSTIN Cache", StringComparison.Ordinal) &&
                failed.Contains("March-GSTR1.json", StringComparison.Ordinal) &&
                failed.Contains("line 344", StringComparison.Ordinal) &&
                failed.Contains("Existing", StringComparison.OrdinalIgnoreCase) == false,
            "The missing-month message did not provide true selected-source evidence.");

        var succeeded = SalesAutoImportDiagnosticFormatter.Build(new SalesAutoImportResult
        {
            ClientGstin = "24AAQCM7513B1ZM",
            FinancialYear = "2025-26",
            Verification = new SalesSourceVerification
            {
                Status = "Cached Monthly GSTR-1 Selected",
                SelectedPeriods = ["202603"]
            }
        }, new DateTime(2026, 3, 1));
        Require(succeeded.Length == 0,
            "Successful automatic GSTR-1 import unexpectedly emits a missing-period warning.");

        var noSource = SalesAutoImportDiagnosticFormatter.Build(new SalesAutoImportResult
        {
            ClientGstin = "24AAQCM7513B1ZM", FinancialYear = "2025-26"
        });
        Require(noSource.Contains("no verified", StringComparison.OrdinalIgnoreCase) &&
                noSource.Contains("FY 2025-26", StringComparison.Ordinal),
            "Empty source set should explain exact FY and GSTIN.");

        Console.WriteLine("PASS existing automatic GST import diagnostics, exact month, source periods and parser errors.");
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
