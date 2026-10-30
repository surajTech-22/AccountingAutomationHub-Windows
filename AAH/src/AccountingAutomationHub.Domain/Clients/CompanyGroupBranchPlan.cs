using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AccountingAutomationHub.Domain.Clients;

/// <summary>
/// Read-only branch authority for a deliberately created Company Group. AAH member records
/// remain owned by their existing Client/FY and are never copied into a synthetic client.
/// This first stage accepts ONE legal PAN; different-PAN owner businesses are a separate stage.
/// </summary>
public sealed record CompanyGroupBranchRegistration(
    long ClientId, string ClientCode, string ClientName, string FinancialYear,
    string Pan, string Gstin, string State, string BranchName, bool IsPrimary);

public sealed record CompanyGroupTallyMaster(string ObjectType, string Name, string Parent);

public sealed class CompanyGroupBranchPlan
{
    public required string GroupId { get; init; }
    public required string FinancialYear { get; init; }
    public required string Pan { get; init; }
    public required IReadOnlyList<CompanyGroupBranchRegistration> Branches { get; init; }
    public int ClientCount => Branches.Select(x => x.ClientId).Distinct().Count();
    public int GstinCount => Branches.Count;

    public IReadOnlyList<CompanyGroupTallyMaster> BuildPreviewMasters(bool separateCashLedgers)
    {
        var masters = new List<CompanyGroupTallyMaster>();
        // Every branch-specific accounting group retains its statutory Tally parent.
        var categories = new (string Suffix, string Parent)[]
        {
            ("Creditors", "Sundry Creditors"), ("Debtors", "Sundry Debtors"),
            ("Sales", "Sales Accounts"), ("Purchases", "Purchase Accounts"),
            ("Direct Expenses", "Direct Expenses"), ("Indirect Expenses", "Indirect Expenses"),
            ("Direct Incomes", "Direct Incomes"), ("Indirect Incomes", "Indirect Incomes"),
            ("Banks", "Bank Accounts")
        };
        foreach (var branch in Branches)
        {
            foreach (var category in categories)
                masters.Add(new CompanyGroupTallyMaster("GROUP",
                    branch.BranchName + " - " + category.Suffix, category.Parent));
            if (separateCashLedgers)
                masters.Add(new CompanyGroupTallyMaster("LEDGER",
                    "Cash - " + branch.BranchName, "Cash-in-Hand"));
        }
        return masters;
    }

    /// <summary>
    /// This XML is an OFFLINE All Masters PREVIEW only. It is not permission to send to Tally:
    /// the live writer must first synchronize master authority, check collisions/duplicates,
    /// complete source-backed ledger and voucher mapping, then verify the Tally response.
    /// </summary>
    public string BuildOfflineMastersPreviewXml(bool separateCashLedgers)
    {
        var masters = BuildPreviewMasters(separateCashLedgers);
        var messages = masters.Select(master =>
            new XElement("TALLYMESSAGE",
                new XElement(master.ObjectType,
                    new XAttribute("ACTION", "Create"),
                    new XElement("NAME", master.Name),
                    new XElement("PARENT", master.Parent))));
        var doc = new XDocument(
            new XElement("ENVELOPE",
                new XElement("HEADER", new XElement("TALLYREQUEST", "Import Data")),
                new XElement("BODY",
                    new XElement("IMPORTDATA",
                        new XElement("REQUESTDESC",
                            new XElement("REPORTNAME", "All Masters")),
                        new XElement("REQUESTDATA", messages)))));
        return doc.ToString();
    }

    public string VoucherTypeFor(string branchName, string baseType, bool branchVoucherTypes)
    {
        if (!Branches.Any(b => b.BranchName.Equals(branchName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Voucher source does not belong to this Company Group branch.");
        var type = (baseType ?? string.Empty).Trim();
        if (type.Equals("Interbank Transfer", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("Bank-to-Bank", StringComparison.OrdinalIgnoreCase))
            return "Contra";
        if (type.Equals("Cash Contra", StringComparison.OrdinalIgnoreCase))
            return branchVoucherTypes ? "Contra - " + branchName : "Contra";
        if (type is not ("Sales" or "Purchase" or "Journal" or "Receipt" or "Payment"))
            throw new InvalidOperationException("Unknown branch voucher classification.");
        return branchVoucherTypes ? type + " - " + branchName : type;
    }
}

public static class CompanyGroupBranchPlanner
{
    private static readonly Regex PanPattern = new("^[A-Z]{5}[0-9]{4}[A-Z]$", RegexOptions.CultureInvariant);
    private static readonly Regex GstinPattern = new("^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][0-9A-Z]{3}$", RegexOptions.CultureInvariant);
    private static readonly IReadOnlyDictionary<string, string> States =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["01"] = "Jammu and Kashmir", ["02"] = "Himachal Pradesh", ["03"] = "Punjab",
            ["04"] = "Chandigarh", ["05"] = "Uttarakhand", ["06"] = "Haryana",
            ["07"] = "Delhi", ["08"] = "Rajasthan", ["09"] = "Uttar Pradesh",
            ["10"] = "Bihar", ["11"] = "Sikkim", ["12"] = "Arunachal Pradesh",
            ["13"] = "Nagaland", ["14"] = "Manipur", ["15"] = "Mizoram",
            ["16"] = "Tripura", ["17"] = "Meghalaya", ["18"] = "Assam",
            ["19"] = "West Bengal", ["20"] = "Jharkhand", ["21"] = "Odisha",
            ["22"] = "Chhattisgarh", ["23"] = "Madhya Pradesh", ["24"] = "Gujarat",
            ["25"] = "Daman and Diu", ["26"] = "Dadra and Nagar Haveli",
            ["27"] = "Maharashtra", ["28"] = "Andhra Pradesh", ["29"] = "Karnataka",
            ["30"] = "Goa", ["31"] = "Lakshadweep", ["32"] = "Kerala",
            ["33"] = "Tamil Nadu", ["34"] = "Puducherry", ["35"] = "Andaman and Nicobar",
            ["36"] = "Telangana", ["37"] = "Andhra Pradesh", ["38"] = "Ladakh",
            ["97"] = "Other Territory"
        };

    public static CompanyGroupBranchPlan Build(
        string groupId, string groupType, string financialYear, IEnumerable<ClientProfile> members)
    {
        if (string.IsNullOrWhiteSpace(groupId) || !string.Equals(groupType?.Trim(), "Company Group", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Special grouping is allowed only for an explicit Company Group.");
        if (string.IsNullOrWhiteSpace(financialYear))
            throw new InvalidOperationException("Select a financial year for the Company Group.");
        ArgumentNullException.ThrowIfNull(members);
        var clients = members.Where(c => c is not null && !c.IsArchived &&
            c.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (clients.Length == 0)
            throw new InvalidOperationException("No active member clients are present in this Company Group/FY.");
        if (clients.GroupBy(c => c.Id).Any(g => g.Key <= 0 || g.Count() > 1))
            throw new InvalidOperationException("Company Group contains duplicate or unsaved client identity.");

        var pending = new List<(ClientProfile Client, string Pan, string Gstin, string State, bool Primary)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var client in clients)
        {
            var values = new[] { client.Gstin }.Concat(client.AdditionalGstins ?? Array.Empty<string>())
                .Select(x => (x ?? string.Empty).Trim().ToUpperInvariant())
                .Where(x => x.Length != 0).ToArray();
            if (values.Length == 0)
                throw new InvalidOperationException("Branch GSTIN is missing for client " + client.ClientCode + ".");
            foreach (var gstin in values)
            {
                if (!GstinPattern.IsMatch(gstin))
                    throw new InvalidOperationException("Invalid GSTIN for client " + client.ClientCode + ": " + gstin);
                var pan = gstin.Substring(2, 10);
                if (!PanPattern.IsMatch(pan) ||
                    (!string.IsNullOrWhiteSpace(client.Pan) && !pan.Equals(client.Pan.Trim(), StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("PAN/GSTIN mismatch for client " + client.ClientCode + ".");
                if (!seen.Add(gstin))
                    throw new InvalidOperationException("Same GSTIN assigned to multiple Group member records: " + gstin);
                var state = States.TryGetValue(gstin[..2], out var name) ? name : "State " + gstin[..2];
                pending.Add((client, pan, gstin, state, gstin.Equals(client.Gstin?.Trim(), StringComparison.OrdinalIgnoreCase)));
            }
        }
        var pans = pending.Select(x => x.Pan).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (pans.Length != 1)
            throw new InvalidOperationException("Different PAN owner-company records require the separate owner-wise stage; do not merge them as branches.");

        var countByState = pending.GroupBy(x => x.State, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var branches = pending.OrderBy(x => x.State, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Gstin, StringComparer.OrdinalIgnoreCase)
            .Select(x => new CompanyGroupBranchRegistration(
                x.Client.Id, x.Client.ClientCode, x.Client.ClientName, x.Client.FinancialYear,
                x.Pan, x.Gstin, x.State,
                countByState[x.State] > 1
                    ? x.State + " - " + x.Gstin + " Branch"
                    : x.State + " Branch",
                x.Primary))
            .ToArray();
        return new CompanyGroupBranchPlan
        {
            GroupId = groupId.Trim(),
            FinancialYear = financialYear.Trim(),
            Pan = pans[0],
            Branches = branches
        };
    }
}
