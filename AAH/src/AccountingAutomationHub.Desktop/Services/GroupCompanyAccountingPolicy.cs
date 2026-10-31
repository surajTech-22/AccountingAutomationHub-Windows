using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AccountingAutomationHub.Domain.Clients;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Read-only grouping authority. Company Group is the ONLY organizer type
/// with special same-PAN/different-PAN accounting classification. Normal
/// CA/Auditor/Firm/Company groups retain their existing meaning.
/// No Tally voucher, company, ledger or accounting record is created here.
/// </summary>
public static class GroupCompanyAccountingPolicy
{
    private static readonly Regex PanPattern = new(
        @"\A[A-Z]{5}[0-9]{4}[A-Z]\z", RegexOptions.CultureInvariant);

    public static GroupCompanySnapshot Build(
        ClientGroupCatalogData catalog, string folderId,
        IEnumerable<ClientProfile> clientProfiles)
    {
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (clientProfiles is null) throw new ArgumentNullException(nameof(clientProfiles));
        var folders = (catalog.Folders ?? []).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        if (!folders.TryGetValue(folderId, out var root) ||
            !root.Type.Equals(ClientGroupFolderTypes.CompanyGroup, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Special company accounting is available only for an explicit Company Group.");

        var descendants = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Id };
        var queue = new Queue<string>();
        queue.Enqueue(root.Id);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var child in folders.Values.Where(x => x.ParentId.Equals(parent, StringComparison.OrdinalIgnoreCase)))
                if (descendants.Add(child.Id)) queue.Enqueue(child.Id);
        }

        var ids = (catalog.Assignments ?? [])
            .Where(x => descendants.Contains(x.FolderId))
            .Select(x => x.ClientId).ToHashSet();
        var members = clientProfiles.Where(x => ids.Contains(x.Id) && !x.IsArchived)
            .GroupBy(x => x.Id).Select(x => x.First())
            .OrderBy(x => x.FinancialYear, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ClientName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var resolved = members.Select(x => new {
            Client = x, PanResult = VerifyPan(x)
        }).ToArray();

        // Financial years are independent books. Never aggregate balances from
        // 2025-26 with 2026-27 just because the PAN happens to be identical.
        var panCounts = resolved.Where(x => x.PanResult.Verified)
            .GroupBy(x => (x.PanResult.Pan, Fy: x.Client.FinancialYear.ToUpperInvariant()))
            .ToDictionary(x => x.Key, x => x.Count());
        var distinctPans = resolved.Where(x => x.PanResult.Verified)
            .Select(x => x.PanResult.Pan).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        var rows = resolved.Select(x =>
        {
            var branches = (x.Client.AdditionalGstins?.Count ?? 0) +
                (string.IsNullOrWhiteSpace(x.Client.Gstin) ? 0 : 1);
            var isBranch = x.PanResult.Verified &&
                (panCounts[(x.PanResult.Pan, x.Client.FinancialYear.ToUpperInvariant())] > 1 || branches > 1);
            return new GroupCompanyMemberRow(
                x.Client.Id, x.Client.ClientCode, x.Client.ClientName,
                x.Client.FinancialYear, x.PanResult.Verified ? x.PanResult.Pan : "REVIEW",
                !x.PanResult.Verified ? "PAN review required" :
                    isBranch ? "Same-PAN branch accounts" : "Separate company / owner",
                branches, x.PanResult.Reason);
        }).ToArray();
        var warning = rows.Where(x => x.Warning.Length > 0)
            .Select(x => x.ClientName + ": " + x.Warning).ToArray();
        return new GroupCompanySnapshot(root.Id, root.Name, rows,
            distinctPans, warning);
    }

    private static (bool Verified, string Pan, string Reason) VerifyPan(ClientProfile client)
    {
        var explicitPan = (client.Pan ?? "").Trim().ToUpperInvariant();
        if (explicitPan.Length > 0 && !PanPattern.IsMatch(explicitPan))
            return (false, "", "Saved PAN format is invalid.");

        var registered = new[] { client.Gstin }
            .Concat(client.AdditionalGstins ?? Array.Empty<string>());
        var gstPans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in registered.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var gstin = raw.Trim().ToUpperInvariant();
            if (gstin.Length != 15 || !PanPattern.IsMatch(gstin.Substring(2, 10)))
                return (false, "", "GSTIN cannot verify the accounting PAN.");
            gstPans.Add(gstin.Substring(2, 10));
        }
        if (gstPans.Count > 1)
            return (false, "", "Saved GST registrations contain different PANs.");
        if (explicitPan.Length > 0 && gstPans.Count == 1 && !gstPans.Contains(explicitPan))
            return (false, "", "Saved PAN conflicts with GST registration PAN.");
        var verified = explicitPan.Length > 0 ? explicitPan : gstPans.FirstOrDefault() ?? "";
        return verified.Length > 0 ? (true, verified, "") :
            (false, "", "PAN or an identifying GST registration is required.");
    }

    /// <summary>
    /// Constructs a classification only, NEVER an importable voucher.
    /// Cross-company postings are two distinct journal/ledger sides and must
    /// not be mistaken for a common contra voucher.
    /// </summary>
    public static GroupTransferPlan PlanTransfer(
        GroupCompanyMemberRow from, GroupCompanyMemberRow to,
        GroupTransferKind kind, bool separateCashLedgers)
    {
        if (from.ClientId == to.ClientId && kind == GroupTransferKind.InterCompany)
            return new(false, "", "Intercompany transfer requires distinct companies.");
        if (from.Pan == "REVIEW" || to.Pan == "REVIEW" ||
            from.FinancialYear != to.FinancialYear)
            return new(false, "", "Verified PAN and matching financial year required.");
        var samePan = from.Pan.Equals(to.Pan, StringComparison.OrdinalIgnoreCase);
        if (kind == GroupTransferKind.CashContra)
        {
            if (!samePan) return new(false, "", "Different-PAN companies cannot share a cash contra.");
            return new(true, "Branch-wise Contra",
                separateCashLedgers ? "Separate cash ledger per branch/division." :
                    "Shared cash ledger; preserve branch-wise source attribution.");
        }
        if (kind == GroupTransferKind.InterbankContra)
        {
            if (!samePan) return new(false, "", "Different-PAN companies require separate intercompany postings.");
            return new(true, "Common Contra",
                "One common Contra voucher type; both bank ledger sides retained.");
        }
        // Do not automatically post or net cross-PAN balances.
        return new(false, "", "Intercompany: preserve separate company ledgers and both DR/CR sides; manual verified Tally mapping required.");
    }
}

public enum GroupTransferKind { CashContra, InterbankContra, InterCompany }

public sealed record GroupCompanyMemberRow(
    long ClientId, string ClientCode, string ClientName, string FinancialYear,
    string Pan, string Relationship, int GstRegistrationCount, string Warning);

public sealed record GroupCompanySnapshot(
    string GroupId, string GroupName, IReadOnlyList<GroupCompanyMemberRow> Members,
    int VerifiedPanCount, IReadOnlyList<string> Warnings);

public sealed record GroupTransferPlan(bool IsPolicyAllowed, string VoucherType, string Detail);
