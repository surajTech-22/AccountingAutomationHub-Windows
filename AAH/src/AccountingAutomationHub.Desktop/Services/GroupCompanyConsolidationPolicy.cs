using System;
using System.Collections.Generic;
using System.Linq;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Balance-sheet preparation from source-verified member ledger balances.
/// Never merges financial years, never invents eliminated intercompany entries,
/// and never modifies Tally. Source systems must supply complete balance rows;
/// this service cannot be used as a substitute for live ledger read-back.
/// </summary>
public static class GroupCompanyConsolidationPolicy
{
    public static IReadOnlyList<GroupOwnerFinancialSummary> Consolidate(
        GroupCompanySnapshot group,
        IEnumerable<VerifiedMemberLedgerBalance> sourceBalances)
    {
        if (group is null) throw new ArgumentNullException(nameof(group));
        if (sourceBalances is null) throw new ArgumentNullException(nameof(sourceBalances));
        if (group.Warnings.Count != 0)
            throw new InvalidOperationException(
                "Company group has PAN/GST identity conflicts; group consolidation blocked.");
        var members = group.Members.ToDictionary(x => x.ClientId);
        var balances = sourceBalances.ToArray();
        if (balances.Length == 0) return [];
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in balances)
        {
            if (!members.TryGetValue(row.ClientId, out var member) ||
                !member.FinancialYear.Equals(row.FinancialYear, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Group balances must belong to assigned Client and exact financial year.");
            if (string.IsNullOrWhiteSpace(row.LedgerMasterId) ||
                string.IsNullOrWhiteSpace(row.ReadbackReference))
                throw new InvalidOperationException(
                    "Master ID and verified Tally read-back source are required.");
            var unique = row.ClientId + "|" + row.FinancialYear + "|" +
                         row.LedgerMasterId + "|" + row.ReadbackReference;
            if (!keys.Add(unique))
                throw new InvalidOperationException("Duplicate verified balance source.");
        }
        return balances.GroupBy(x => x.FinancialYear, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(fy => new GroupOwnerFinancialSummary(
                fy.Key,
                fy.Sum(x => x.Debit),
                fy.Sum(x => x.Credit),
                fy.GroupBy(x => x.ClientId).Select(member =>
                    new GroupCompanyBalanceSummary(member.Key,
                        members[member.Key].ClientName, members[member.Key].Pan,
                        member.Sum(x => x.Debit), member.Sum(x => x.Credit)))
                    .OrderBy(x => x.ClientName, StringComparer.OrdinalIgnoreCase).ToArray(),
                "Raw source-backed balances only. Intercompany elimination requires explicit paired ledger review."))
            .ToArray();
    }
}

public sealed record VerifiedMemberLedgerBalance(
    long ClientId, string FinancialYear, string LedgerMasterId,
    string ReadbackReference, decimal Debit, decimal Credit);

public sealed record GroupCompanyBalanceSummary(
    long ClientId, string ClientName, string Pan, decimal Debit, decimal Credit);

public sealed record GroupOwnerFinancialSummary(
    string FinancialYear, decimal TotalDebit, decimal TotalCredit,
    IReadOnlyList<GroupCompanyBalanceSummary> Members, string ConsolidationDisclaimer);
