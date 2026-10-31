using System.Text.Json;
using AccountingAutomationHub.Domain.Clients;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Separate optional ownership register. Never mutate Clients.GroupFolders.V1:
/// a client can remain in its ordinary CA/Auditor/Firm hierarchy and also be
/// linked to a proprietor's consolidated reporting roster.
/// The register does not merge books or route any Tally voucher automatically.
/// </summary>
public static class OwnerAccountingGroupCatalog
{
    public const string PreferenceKey = "Clients.OwnerAccountingGroups.V1";

    public static OwnerAccountingGroupData Load(string? json)
    {
        OwnerAccountingGroupData data;
        try { data = JsonSerializer.Deserialize<OwnerAccountingGroupData>(json ?? "") ?? new(); }
        catch (JsonException) { data = new(); }
        Normalize(data);
        return data;
    }

    public static string Save(OwnerAccountingGroupData data)
    {
        Normalize(data);
        return JsonSerializer.Serialize(data);
    }

    public static void Normalize(OwnerAccountingGroupData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        data.Groups ??= [];
        data.Members ??= [];
        data.Groups = data.Groups
            .Where(x => x is not null)
            .Select(x =>
            {
                x.Id = (x.Id ?? "").Trim();
                x.Name = (x.Name ?? "").Trim();
                x.Notes = (x.Notes ?? "").Trim();
                return x;
            })
            .Where(x => x.Id.Length > 0 && x.Name.Length > 0)
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Last()).ToList();
        var validIds = data.Groups.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        data.Members = data.Members
            .Where(x => x is not null && x.ClientId > 0 && validIds.Contains((x.OwnerGroupId ?? "").Trim()))
            .Select(x =>
            {
                x.OwnerGroupId = x.OwnerGroupId.Trim();
                x.Role = OwnerMemberRoles.Normalize(x.Role);
                x.BranchOrDivision = (x.BranchOrDivision ?? "").Trim();
                x.CashLedgerName = (x.CashLedgerName ?? "").Trim();
                x.CashContraVoucherType = (x.CashContraVoucherType ?? "").Trim();
                return x;
            })
            // A Client/FY may be assigned to at most one owner. Normal groups remain independent.
            .GroupBy(x => x.ClientId).Select(x => x.Last()).ToList();
    }

    public static OwnerAccountingGroupRecord AddGroup(OwnerAccountingGroupData data, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) throw new ArgumentException("Owner name is required.", nameof(name));
        Normalize(data);
        if (data.Groups.Any(x => x.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("An Owner Group with this name already exists.");
        var added = new OwnerAccountingGroupRecord { Id = Guid.NewGuid().ToString("N"), Name = trimmed };
        data.Groups.Add(added);
        return added;
    }

    public static void Assign(
        OwnerAccountingGroupData data, string groupId, ClientProfile client,
        string role, string branchOrDivision = "", bool separateCashLedger = false,
        string cashLedgerName = "", string cashContraVoucherType = "")
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(client);
        Normalize(data);
        if (client.Id <= 0 || client.IsArchived)
            throw new InvalidOperationException("Only saved active clients can be assigned to an Owner Group.");
        if (!data.Groups.Any(x => x.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Owner Group not found.");
        var normalizedRole = OwnerMemberRoles.Normalize(role);
        if (normalizedRole == OwnerMemberRoles.Branch && string.IsNullOrWhiteSpace(branchOrDivision))
            throw new InvalidOperationException("Enter a branch or division name.");
        if (!string.IsNullOrWhiteSpace(cashContraVoucherType) && normalizedRole != OwnerMemberRoles.Branch)
            throw new InvalidOperationException("Branch-wise Cash Contra voucher type is only for a branch.");
        if (separateCashLedger && string.IsNullOrWhiteSpace(cashLedgerName))
            throw new InvalidOperationException("Enter the separate cash ledger's name.");
        var member = new OwnerAccountingMemberRecord
        {
            OwnerGroupId = groupId,
            ClientId = client.Id,
            Role = normalizedRole,
            BranchOrDivision = (branchOrDivision ?? "").Trim(),
            SeparateCashLedger = separateCashLedger,
            CashLedgerName = separateCashLedger ? cashLedgerName.Trim() : "",
            CashContraVoucherType = (cashContraVoucherType ?? "").Trim()
        };
        data.Members.RemoveAll(x => x.ClientId == client.Id);
        data.Members.Add(member);
    }

    public static OwnerAccountingSummary BuildSummary(
        OwnerAccountingGroupData data, string groupId,
        IEnumerable<ClientProfile> clients, string financialYear)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(clients);
        Normalize(data);
        var group = data.Groups.SingleOrDefault(x => x.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Owner Group not found.");
        var roster = data.Members.Where(x => x.OwnerGroupId.Equals(group.Id, StringComparison.OrdinalIgnoreCase))
            .Join(clients.Where(x => !x.IsArchived &&
                     x.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase)),
                  x => x.ClientId, x => x.Id, (membership, client) => (membership, client)).ToArray();
        var validPan = roster.Where(x => OwnerGroupAccountingPolicy.IsValidPan(x.client.Pan)).ToArray();
        var panGroups = validPan.GroupBy(x => OwnerGroupAccountingPolicy.NormalizePan(x.client.Pan),
            StringComparer.OrdinalIgnoreCase).ToArray();
        var needsReview = roster.Length - validPan.Length;
        var individualCount = roster.Count(x => x.membership.Role == OwnerMemberRoles.Individual);
        var branchCount = roster.Count(x => x.membership.Role == OwnerMemberRoles.Branch);
        var entityCount = roster.Count(x => x.membership.Role == OwnerMemberRoles.Entity);
        var members = roster.Select(x => new OwnerAccountingMemberView(
            x.client.Id, x.client.ClientName, x.client.ClientCode,
            x.client.FinancialYear, x.membership.Role,
            OwnerGroupAccountingPolicy.NormalizePan(x.client.Pan),
            OwnerGroupAccountingPolicy.IsValidPan(x.client.Pan),
            x.membership.BranchOrDivision,
            x.membership.SeparateCashLedger,
            x.membership.CashLedgerName, x.membership.CashContraVoucherType))
            .OrderBy(x => x.Role).ThenBy(x => x.ClientName, StringComparer.OrdinalIgnoreCase).ToArray();
        return new OwnerAccountingSummary(group.Id, group.Name, financialYear, members,
            panGroups.Length, needsReview, individualCount, branchCount, entityCount);
    }
}

public static class OwnerMemberRoles
{
    public const string Individual = "Individual";
    public const string Branch = "Branch/Division";
    public const string Entity = "Separate Entity";
    public static IReadOnlyList<string> All { get; } = [Individual, Branch, Entity];

    public static string Normalize(string? value) => (value ?? "").Trim() switch
    {
        var x when x.Equals(Individual, StringComparison.OrdinalIgnoreCase) => Individual,
        var x when x.Equals(Branch, StringComparison.OrdinalIgnoreCase) => Branch,
        _ => Entity
    };
}

public sealed class OwnerAccountingGroupData
{
    public List<OwnerAccountingGroupRecord> Groups { get; set; } = [];
    public List<OwnerAccountingMemberRecord> Members { get; set; } = [];
}
public sealed class OwnerAccountingGroupRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
}
public sealed class OwnerAccountingMemberRecord
{
    public string OwnerGroupId { get; set; } = "";
    public long ClientId { get; set; }
    public string Role { get; set; } = OwnerMemberRoles.Entity;
    public string BranchOrDivision { get; set; } = "";
    public bool SeparateCashLedger { get; set; }
    public string CashLedgerName { get; set; } = "";
    public string CashContraVoucherType { get; set; } = "";
}
public sealed record OwnerAccountingMemberView(
    long ClientId, string ClientName, string ClientCode, string FinancialYear, string Role,
    string Pan, bool PanFormatValid, string BranchOrDivision, bool SeparateCashLedger,
    string CashLedgerName, string CashContraVoucherType);
public sealed record OwnerAccountingSummary(
    string OwnerGroupId, string OwnerName, string FinancialYear, IReadOnlyList<OwnerAccountingMemberView> Members,
    int DistinctValidPans, int MissingOrInvalidPan, int Individuals, int Branches, int SeparateEntities);
