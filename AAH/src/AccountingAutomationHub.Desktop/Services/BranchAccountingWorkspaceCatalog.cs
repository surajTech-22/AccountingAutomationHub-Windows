using System;
using System.Collections.Generic;
using System.Linq;
using AccountingAutomationHub.Domain.Clients;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Workspace contract for the existing Client/FY. Scope IDs are durable and
/// do NOT invent hidden Client profiles or Tally companies.
/// Parent Consolidated = Dashboard + Tally Import only. Each branch owns
/// its accounting modules, but they must not be activated until EVERY
/// repository/importer uses this scope key and its data has been reconciled.
/// </summary>
public static class BranchAccountingWorkspaceCatalog
{
    private static readonly string[] ConsolidatedModules =
        ["Dashboard", "Tally Import"];
    private static readonly string[] AccountingModules =
        ["Dashboard", "Purchase", "Sales", "Banking", "Cash", "Ledger Master",
         "Journal", "GST", "Stock", "Fixed Assets", "Other", "Tally Import"];

    public static IReadOnlyList<BranchAccountingWorkspace> Build(
        ClientProfile client, IReadOnlyList<ClientBranchRecord> branchRegistrations)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        if (client.Id <= 0 || string.IsNullOrWhiteSpace(client.FinancialYear))
            throw new InvalidOperationException("Saved Client and Financial Year required.");
        if (branchRegistrations is null) throw new ArgumentNullException(nameof(branchRegistrations));

        if (branchRegistrations.Count == 0)
            return [new BranchAccountingWorkspace(
                client.Id, client.FinancialYear, "NORMAL", client.ClientName,
                "", "", false, AccountingModules, true)];

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gstins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<BranchAccountingWorkspace>
        {
            new(client.Id, client.FinancialYear, "CONSOLIDATED", "Consolidated",
                "", "", true, ConsolidatedModules, false)
        };
        foreach (var branch in branchRegistrations)
        {
            if (string.IsNullOrWhiteSpace(branch.BranchId) ||
                !keys.Add(branch.BranchId) ||
                !names.Add(branch.Name))
                throw new InvalidOperationException("Each branch requires a unique durable ID and name.");
            if (branch.Gstin.Length > 0)
            {
                if (!ClientBranchCatalog.IsGstinFormat(branch.Gstin) ||
                    !gstins.Add(branch.Gstin))
                    throw new InvalidOperationException("Duplicate or invalid GSTIN branch scope.");
                var parentPan = (client.Pan ?? "").Trim().ToUpperInvariant();
                if (parentPan.Length == 0 && client.Gstin.Length == 15)
                    parentPan = client.Gstin.Substring(2, 10).ToUpperInvariant();
                if (parentPan.Length != 10 ||
                    !branch.Gstin.Substring(2, 10).Equals(parentPan, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Branch GSTIN PAN must match the individual Client, not the Group owner.");
            }
            result.Add(new BranchAccountingWorkspace(
                client.Id, client.FinancialYear, branch.BranchId, branch.Name,
                branch.Gstin, branch.Name, false, AccountingModules, false));
        }
        return result;
    }
}

/// <param name="CanUseExistingClientImporter">
/// True only for a normal client with no branch split. It is false for the
/// branched parent and all branch scopes until scoped persistence and readback
/// have been proven. A UI list does not authorize import/posting.
/// </param>
public sealed record BranchAccountingWorkspace(
    long ClientId, string FinancialYear, string ScopeId, string DisplayName,
    string Gstin, string FolderLabel, bool IsConsolidated,
    IReadOnlyList<string> AllowedModules, bool CanUseExistingClientImporter)
{
    public string StorageScopeKey => ClientId + "/" + FinancialYear + "/" + ScopeId;
    public void RequireVerifiedImporter()
    {
        if (!CanUseExistingClientImporter)
            throw new InvalidOperationException(
                "Branch or Consolidated posting needs verified scope-specific storage, migration and Tally read-back.");
    }
}
