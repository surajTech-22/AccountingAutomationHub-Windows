using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AccountingAutomationHub.Infrastructure.Tally;

internal static class TallyConsolidatedVoucherDisplayClassifierTests
{
    private static void Check(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidOperationException("Consolidated parent voucher UI: " + reason);
    }

    public static void Run()
    {
        var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Purchase - North"] = "Purchase",
            ["Purchase - South"] = "Purchase",
            ["Sales - North"] = "Sales",
            ["Journal - South"] = "Journal",
            ["Debit Note - North"] = "Debit Note",
            ["Payment - North"] = "Payment",
            ["Receipt - North"] = "Receipt",
            ["Contra - North"] = "Contra"
        };
        var branches = new[] { "North", "South" };
        var sourceRows = new (string Type, string Number, string ExpectedModule, string Branch)[]
        {
            ("Purchase - North", "B-111111111111111111111111-INV-1", "Purchase", "North"),
            ("Purchase - South", "B-222222222222222222222222-INV-2", "Purchase", "South"),
            ("Sales - North", "B-111111111111111111111111-SALE-1", "Sales", "North"),
            ("Journal - South", "AAH-JRNL-2", "Journal", "South"),
            ("Debit Note - North", "B-111111111111111111111111-DN-1", "Debit Note", "North"),
            ("Payment - North", "B-111111111111111111111111-BANK-1", "Bank", "North"),
            ("Receipt - North", "B-111111111111111111111111-BANK-2", "Bank", "North"),
            ("Contra - North", "B-111111111111111111111111-BANK-3", "Bank", "North")
        };
        var projected = sourceRows.Select(row =>
            TallyConsolidatedVoucherDisplayClassifier.Classify(
                row.Type, row.Number, parents, branches)).ToArray();
        Check(projected.Length == sourceRows.Length &&
              projected.Select((row, i) =>
                  row.Module == sourceRows[i].ExpectedModule &&
                  row.BranchName == sourceRows[i].Branch).All(x => x),
              "subtypes must become the original top-level Dashboard parent categories");
        Check(projected.GroupBy(row => row.Module)
                  .ToDictionary(group => group.Key, group => group.Count())
                  .OrderBy(group => group.Key).SequenceEqual(
                  new Dictionary<string, int>
                  {
                      ["Bank"] = 3, ["Purchase"] = 2, ["Sales"] = 1,
                      ["Journal"] = 1, ["Debit Note"] = 1
                  }.OrderBy(group => group.Key)),
              "the UI should show five parent tiles rather than eight subtype tiles");
        // The original source binding is NEVER a destination subtype.
        var basePurchase = TallyConsolidatedVoucherDisplayClassifier.Classify(
            "Purchase", sourceRows[0].Number, parents, branches);
        Check(projected[0].SourceAnchor == basePurchase.SourceAnchor &&
              basePurchase.Module == "Purchase",
              "checking the option cannot create a fresh source anchor / duplicate CREATE");
        var reloaded = TallyConsolidatedVoucherDisplayClassifier.Classify(
            "Purchase - North", sourceRows[0].Number,
            configuredBranchNames: branches);
        Check(reloaded.Module == "Purchase" &&
              reloaded.ParentType == "Purchase" &&
              reloaded.BranchName == "North" &&
              reloaded.SourceAnchor == basePurchase.SourceAnchor,
              "reopening saved XML must restore parent tile and source binding without the in-memory plan");

        var cash = TallyConsolidatedVoucherDisplayClassifier.Classify(
            "Payment - North", "AAH-CASH-12", parents, branches);
        Check(cash.Module == "Cash" && projected[5].Module == "Bank",
              "non-bank Payment must remain Cash, BANK origin Payment must remain Bank");
        var custom = TallyConsolidatedVoucherDisplayClassifier.Classify(
            "Purchase - West", "AAH-OTHER-1", configuredBranchNames: branches);
        Check(custom.Module == "Purchase - West" && custom.ParentType == "Purchase - West",
              "unconfigured foreign voucher types must not be blindly stripped");
        var credit = TallyConsolidatedVoucherDisplayClassifier.Classify(
            "Credit Note - North", "AAH-CN-1", configuredBranchNames: branches);
        Check(credit.Module == "Credit Note",
              "less-common original parent types must still map correctly");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null &&
               !File.Exists(Path.Combine(root.FullName, "AAH", "src",
                   "AccountingAutomationHub.Desktop", "ViewModels", "DashboardViewModel.cs")))
            root = root.Parent;
        Check(root is not null, "production DashboardViewModel source must be available for UI integration assertions");
        var dashboard = File.ReadAllText(Path.Combine(root!.FullName, "AAH", "src",
            "AccountingAutomationHub.Desktop", "ViewModels", "DashboardViewModel.cs"));
        Check(dashboard.Contains("TallyConsolidatedVoucherDisplayClassifier.Classify(",
                  StringComparison.Ordinal) &&
              dashboard.Contains("Module = module,", StringComparison.Ordinal) &&
              dashboard.Contains("Branches: ", StringComparison.Ordinal) &&
              dashboard.Contains("Actual Tally voucher types: ", StringComparison.Ordinal),
              "the ORIGINAL Tally status tiles must use the parent grouping and retain branch details");
        Console.WriteLine("PASS: consolidated original parent voucher UI, branch tooltip, " +
            "saved-preview restoration, stable source anchors, and Bank/Cash distinction.");
    }
}
