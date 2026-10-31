using AccountingAutomationHub.Domain.Clients;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Read-only accounting interpretation of the opt-in Owner Group hierarchy.
/// This never rewrites a Client, Tally company, Master, or Voucher. The normal
/// CA/Auditor/Company Group hierarchy has no ownership-accounting semantics.
/// </summary>
public static class OwnerGroupAccountingPolicy
{
    public static IReadOnlyList<OwnerGroupMember> GetMembers(
        ClientGroupCatalogData catalog,
        IEnumerable<ClientProfile> clients,
        string financialYear,
        bool includeArchived = false)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(clients);
        ClientGroupingCatalog.Normalize(catalog);

        var folders = catalog.Folders.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var assignments = catalog.Assignments
            .GroupBy(x => x.ClientId)
            .ToDictionary(x => x.Key, x => x.Last().FolderId);
        var members = new List<OwnerGroupMember>();

        foreach (var client in clients)
        {
            if (client.Id <= 0 ||
                (!includeArchived && client.IsArchived) ||
                !client.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase) ||
                !assignments.TryGetValue(client.Id, out var folderId))
                continue;

            var groupId = FindOwningGroup(folders, folderId);
            if (groupId.Length == 0) continue;
            var pan = NormalizePan(client.Pan);
            members.Add(new OwnerGroupMember(
                groupId, client.Id, client.ClientCode, client.ClientName,
                client.FinancialYear, pan, IsValidPan(pan)));
        }
        return members;
    }

    /// <summary>
    /// An Owner Group is an explicit same-owner declaration. PAN differentiates
    /// branches of the same taxpayer from distinct legal/tax entities.
    /// Missing/invalid PAN must never be treated as equal or guessed.
    /// </summary>
    public static OwnerGroupRelationship Classify(
        OwnerGroupMember first,
        OwnerGroupMember second)
    {
        if (first.ClientId == second.ClientId)
            return OwnerGroupRelationship.SameClient;
        if (!first.OwnerGroupId.Equals(second.OwnerGroupId, StringComparison.OrdinalIgnoreCase) ||
            !first.FinancialYear.Equals(second.FinancialYear, StringComparison.OrdinalIgnoreCase))
            return OwnerGroupRelationship.Unrelated;
        if (!first.PanVerified || !second.PanVerified)
            return OwnerGroupRelationship.NeedsPanReview;
        return first.Pan.Equals(second.Pan, StringComparison.OrdinalIgnoreCase)
            ? OwnerGroupRelationship.SamePanBranchAccounts
            : OwnerGroupRelationship.SameOwnerDifferentPan;
    }

    /// <summary>
    /// A Contra is valid only for two accounts WITHIN the SAME Tally company.
    /// Two separate Tally companies require independently verified mirrored
    /// intercompany entries and cannot be silently routed as a single Contra.
    /// The actual bank-voucher importer must opt into this policy separately.
    /// </summary>
    public static OwnerTransferDecision ClassifyTransfer(
        string? sourceCompanyKey,
        string? targetCompanyKey,
        string? sourceAccountKey,
        string? targetAccountKey,
        bool cashInvolved,
        bool separateCashLedgerRequested = false,
        string? branchId = null)
    {
        var sourceCompany = (sourceCompanyKey ?? string.Empty).Trim();
        var targetCompany = (targetCompanyKey ?? string.Empty).Trim();
        var sourceAccount = (sourceAccountKey ?? string.Empty).Trim();
        var targetAccount = (targetAccountKey ?? string.Empty).Trim();
        if (sourceCompany.Length == 0 || targetCompany.Length == 0 ||
            sourceAccount.Length == 0 || targetAccount.Length == 0)
            return new OwnerTransferDecision(OwnerTransferTreatment.ReviewRequired, false, false);
        if (!sourceCompany.Equals(targetCompany, StringComparison.OrdinalIgnoreCase))
            return new OwnerTransferDecision(OwnerTransferTreatment.MirroredIntercompanyEntriesRequired, false, false);
        if (sourceAccount.Equals(targetAccount, StringComparison.OrdinalIgnoreCase))
            return new OwnerTransferDecision(OwnerTransferTreatment.ReviewRequired, false, false);

        return new OwnerTransferDecision(
            cashInvolved ? OwnerTransferTreatment.CashContra : OwnerTransferTreatment.CommonInterbankContra,
            cashInvolved && !string.IsNullOrWhiteSpace(branchId),
            cashInvolved && separateCashLedgerRequested);
    }

    public static string NormalizePan(string? value) =>
        new((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    public static bool IsValidPan(string? value)
    {
        var pan = NormalizePan(value);
        if (pan.Length != 10) return false;
        for (var i = 0; i < pan.Length; i++)
        {
            if (i < 5 || i == 9)
            {
                if (pan[i] < 'A' || pan[i] > 'Z') return false;
            }
            else if (pan[i] < '0' || pan[i] > '9') return false;
        }
        return true;
    }

    private static string FindOwningGroup(
        IReadOnlyDictionary<string, ClientGroupFolderRecord> folders,
        string folderId)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrWhiteSpace(folderId) &&
               visited.Add(folderId) &&
               folders.TryGetValue(folderId, out var folder))
        {
            if (folder.Type.Equals(ClientGroupFolderTypes.OwnerGroup, StringComparison.OrdinalIgnoreCase))
                return folder.Id;
            folderId = folder.ParentId;
        }
        return string.Empty;
    }
}

public sealed record OwnerGroupMember(
    string OwnerGroupId,
    long ClientId,
    string ClientCode,
    string ClientName,
    string FinancialYear,
    string Pan,
    bool PanVerified);

public enum OwnerGroupRelationship
{
    Unrelated,
    SameClient,
    NeedsPanReview,
    SamePanBranchAccounts,
    SameOwnerDifferentPan
}

public enum OwnerTransferTreatment
{
    ReviewRequired,
    CashContra,
    CommonInterbankContra,
    MirroredIntercompanyEntriesRequired
}

public sealed record OwnerTransferDecision(
    OwnerTransferTreatment Treatment,
    bool UseBranchWiseCashContraVoucherType,
    bool UseSeparateCashLedger);
