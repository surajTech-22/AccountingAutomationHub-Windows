using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AccountingAutomationHub.Domain.Clients;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Optional branches inside one Client/FY. Branches never create a second Client,
/// change PAN authority, or change the existing GST-wise import/Tally routing.
/// Persist only names and GSTIN links; AdditionalGstins remains the accounting
/// import authority and is synchronized by Client Management on save.
/// </summary>
public static class ClientBranchCatalog
{
    public const string PreferencePrefix = "Clients.InternalBranches.V1.";
    public static string Key(long clientId) => PreferencePrefix + clientId;

    public static IReadOnlyList<ClientBranchRecord> Parse(string? lines)
    {
        var result = new List<ClientBranchRecord>();
        var seenGstins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (lines ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0) continue;
            var halves = raw.Split('|', StringSplitOptions.TrimEntries);
            if (halves.Length > 2)
                throw new InvalidOperationException("Each branch must use 'Branch Name | GSTIN', just 'Branch Name', or just 'GSTIN'.");
            var name = halves[0].Trim();
            var gstin = halves.Length == 2 ? NormalizeGstin(halves[1]) : "";
            if (halves.Length == 1 && LooksLikeGstin(name))
            {
                gstin = NormalizeGstin(name);
                name = "";
            }
            if (gstin.Length > 0 && !IsGstinFormat(gstin))
                throw new InvalidOperationException("Invalid branch GSTIN: " + gstin + ".");
            if (name.Length == 0 && gstin.Length == 0)
                throw new InvalidOperationException("A branch needs a name or GSTIN.");
            var autoName = name.Length == 0;
            if (autoName) name = StateBranchName(gstin);
            if (name.Length > 100 || name.Contains('|'))
                throw new InvalidOperationException("Branch names must be at most 100 characters and cannot contain '|'.");
            if (gstin.Length > 0 && !seenGstins.Add(gstin))
                throw new InvalidOperationException("Duplicate branch GSTIN: " + gstin + ".");
            if (autoName) name = UniqueDisplayName(name, gstin, seenNames);
            else if (!seenNames.Add(name))
                throw new InvalidOperationException("Duplicate branch name: " + name + ".");
            result.Add(new ClientBranchRecord { Name = name, Gstin = gstin, IsAutoNamed = autoName });
        }
        if (result.Count > 100)
            throw new InvalidOperationException("A client can have at most 100 branches.");
        return result;
    }

    public static string Format(IEnumerable<ClientBranchRecord> branches) =>
        string.Join(Environment.NewLine, branches.Select(x =>
            string.IsNullOrWhiteSpace(x.Gstin) ? x.Name :
            x.IsAutoNamed || x.Name.Equals(StateBranchName(x.Gstin), StringComparison.OrdinalIgnoreCase)
                ? x.Gstin : x.Name + " | " + x.Gstin));

    public static string Serialize(IEnumerable<ClientBranchRecord> branches) =>
        JsonSerializer.Serialize(branches.ToArray());

    public static IReadOnlyList<ClientBranchRecord> Resolve(ClientProfile client, string? savedJson)
    {
        var stored = Array.Empty<ClientBranchRecord>();
        try { stored = JsonSerializer.Deserialize<ClientBranchRecord[]>(savedJson ?? "") ?? []; }
        catch (JsonException) { /* Legacy data stays recoverable via AdditionalGstins. */ }
        var primary = NormalizeGstin(client.Gstin);
        var registered = (client.AdditionalGstins ?? Array.Empty<string>())
            .Select(NormalizeGstin)
            .Where(IsGstinFormat)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var allowed = registered.Append(primary).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<ClientBranchRecord>();
        var usedGstins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in stored)
        {
            if (row is null) continue;
            var name = (row.Name ?? "").Trim();
            var gstin = NormalizeGstin(row.Gstin);
            // The Dashboard GSTIN quick editor can remove registrations. Do not
            // keep a stale branch linked to an unregistered GSTIN.
            if (gstin.Length > 0 && (!IsGstinFormat(gstin) || !allowed.Contains(gstin))) continue;
            var auto = row.IsAutoNamed ||
                (gstin.Length > 0 && (name.Length == 0 ||
                    name.Equals("Branch " + gstin, StringComparison.OrdinalIgnoreCase)));
            if (auto) name = StateBranchName(gstin);
            if (name.Length == 0) continue;
            if (gstin.Length > 0 && !usedGstins.Add(gstin)) continue;
            name = UniqueDisplayName(name, gstin, usedNames);
            result.Add(new ClientBranchRecord
            {
                BranchId = ValidId(row.BranchId) ? row.BranchId : LegacyBranchId(client.Id, gstin, name),
                Name = name, Gstin = gstin, IsAutoNamed = auto
            });
        }
        // Existing multi-GSTIN clients automatically gain branches, without
        // altering the Client count or creating new client/FY workspaces.
        foreach (var gstin in registered)
        {
            if (usedGstins.Add(gstin))
            {
                var name = UniqueDisplayName(StateBranchName(gstin), gstin, usedNames);
                result.Add(new ClientBranchRecord
                {
                    BranchId = LegacyBranchId(client.Id, gstin, name),
                    Name = name, Gstin = gstin, IsAutoNamed = true
                });
            }
        }
        // When a company becomes branched, the existing primary GST registration
        // is also a first-class branch. Its existing accounting records are not
        // migrated here: the scoped importer must reconcile them separately.
        if (result.Count > 0 && IsGstinFormat(primary) && usedGstins.Add(primary))
        {
            var name = UniqueDisplayName(StateBranchName(primary), primary, usedNames);
            result.Insert(0, new ClientBranchRecord
            {
                BranchId = LegacyBranchId(client.Id, primary, name),
                Name = name, Gstin = primary, IsAutoNamed = true
            });
        }
        return result;
    }

    /// <summary>
    /// The line editor predates true accounting branches. Preserve the durable
    /// scope identity when a branch is edited, not merely its display name.
    /// A GSTIN remains its match key. A name-only branch is matched by exact
    /// saved name; renaming it will require an explicit branch identity UI
    /// when branch-scoped books are enabled, never a silent ledger reassignment.
    /// </summary>
    public static IReadOnlyList<ClientBranchRecord> Reconcile(
        long clientId, IReadOnlyList<ClientBranchRecord> proposed,
        IReadOnlyList<ClientBranchRecord> existing)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ClientBranchRecord>();
        foreach (var row in proposed)
        {
            var gstin = NormalizeGstin(row.Gstin);
            var prior = existing.FirstOrDefault(x =>
                gstin.Length > 0 ? x.Gstin.Equals(gstin, StringComparison.OrdinalIgnoreCase) :
                x.Gstin.Length == 0 && x.Name.Equals(row.Name, StringComparison.OrdinalIgnoreCase));
            var branchId = prior is not null && ValidId(prior.BranchId)
                ? prior.BranchId : LegacyBranchId(clientId, gstin, row.Name);
            if (!used.Add(branchId))
                throw new InvalidOperationException("Duplicate durable branch identity; refusing to merge accounting scopes.");
            var auto = row.IsAutoNamed;
            var name = UniqueDisplayName(auto ? StateBranchName(gstin) : row.Name, gstin, names);
            result.Add(new ClientBranchRecord
            {
                BranchId = branchId, Name = name, Gstin = gstin, IsAutoNamed = auto
            });
        }
        return result;
    }

    public static string StateBranchName(string? gstin) => NormalizeGstin(gstin) switch
    {
        var s when s.StartsWith("01", StringComparison.Ordinal) => "Jammu and Kashmir",
        var s when s.StartsWith("02", StringComparison.Ordinal) => "Himachal Pradesh",
        var s when s.StartsWith("03", StringComparison.Ordinal) => "Punjab",
        var s when s.StartsWith("04", StringComparison.Ordinal) => "Chandigarh",
        var s when s.StartsWith("05", StringComparison.Ordinal) => "Uttarakhand",
        var s when s.StartsWith("06", StringComparison.Ordinal) => "Haryana",
        var s when s.StartsWith("07", StringComparison.Ordinal) => "Delhi",
        var s when s.StartsWith("08", StringComparison.Ordinal) => "Rajasthan",
        var s when s.StartsWith("09", StringComparison.Ordinal) => "Uttar Pradesh",
        var s when s.StartsWith("10", StringComparison.Ordinal) => "Bihar",
        var s when s.StartsWith("11", StringComparison.Ordinal) => "Sikkim",
        var s when s.StartsWith("12", StringComparison.Ordinal) => "Arunachal Pradesh",
        var s when s.StartsWith("13", StringComparison.Ordinal) => "Nagaland",
        var s when s.StartsWith("14", StringComparison.Ordinal) => "Manipur",
        var s when s.StartsWith("15", StringComparison.Ordinal) => "Mizoram",
        var s when s.StartsWith("16", StringComparison.Ordinal) => "Tripura",
        var s when s.StartsWith("17", StringComparison.Ordinal) => "Meghalaya",
        var s when s.StartsWith("18", StringComparison.Ordinal) => "Assam",
        var s when s.StartsWith("19", StringComparison.Ordinal) => "West Bengal",
        var s when s.StartsWith("20", StringComparison.Ordinal) => "Jharkhand",
        var s when s.StartsWith("21", StringComparison.Ordinal) => "Odisha",
        var s when s.StartsWith("22", StringComparison.Ordinal) => "Chhattisgarh",
        var s when s.StartsWith("23", StringComparison.Ordinal) => "Madhya Pradesh",
        var s when s.StartsWith("24", StringComparison.Ordinal) => "Gujarat",
        var s when s.StartsWith("25", StringComparison.Ordinal) => "Daman and Diu",
        var s when s.StartsWith("26", StringComparison.Ordinal) => "Dadra and Nagar Haveli and Daman and Diu",
        var s when s.StartsWith("27", StringComparison.Ordinal) => "Maharashtra",
        var s when s.StartsWith("28", StringComparison.Ordinal) => "Andhra Pradesh (Old)",
        var s when s.StartsWith("29", StringComparison.Ordinal) => "Karnataka",
        var s when s.StartsWith("30", StringComparison.Ordinal) => "Goa",
        var s when s.StartsWith("31", StringComparison.Ordinal) => "Lakshadweep",
        var s when s.StartsWith("32", StringComparison.Ordinal) => "Kerala",
        var s when s.StartsWith("33", StringComparison.Ordinal) => "Tamil Nadu",
        var s when s.StartsWith("34", StringComparison.Ordinal) => "Puducherry",
        var s when s.StartsWith("35", StringComparison.Ordinal) => "Andaman and Nicobar Islands",
        var s when s.StartsWith("36", StringComparison.Ordinal) => "Telangana",
        var s when s.StartsWith("37", StringComparison.Ordinal) => "Andhra Pradesh",
        var s when s.StartsWith("38", StringComparison.Ordinal) => "Ladakh",
        var s when s.StartsWith("97", StringComparison.Ordinal) => "Other Territory",
        var s when s.StartsWith("99", StringComparison.Ordinal) => "Centre Jurisdiction",
        _ => "Unregistered Branch"
    };

    private static bool ValidId(string? branchId) =>
        !string.IsNullOrWhiteSpace(branchId) &&
        branchId.Length <= 80 &&
        branchId.All(x => char.IsLetterOrDigit(x) || x is '-' or '_');

    private static string LegacyBranchId(long clientId, string gstin, string name)
    {
        var input = $"{clientId}|{(gstin.Length == 0 ? "NAME:" + name.Trim().ToUpperInvariant() : "GSTIN:" + gstin)}";
        return "B-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..24];
    }

    private static string UniqueDisplayName(string name, string gstin, ISet<string> seen)
    {
        if (seen.Add(name)) return name;
        if (gstin.Length == 0)
            throw new InvalidOperationException("Duplicate named branch: " + name);
        var suffix = gstin.Length >= 6 ? gstin[^6..] : gstin;
        var distinct = name + " (" + suffix + ")";
        if (!seen.Add(distinct))
            throw new InvalidOperationException("Duplicate GSTIN state branch: " + gstin);
        return distinct;
    }

    public static IReadOnlyList<string> AdditionalGstins(
        IReadOnlyList<ClientBranchRecord> branches, string? primaryGstin, string? clientPan)
    {
        var primary = NormalizeGstin(primaryGstin);
        var expectedPan = primary.Length == 15 ? primary.Substring(2, 10) : NormalizePan(clientPan);
        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in branches)
        {
            var gstin = NormalizeGstin(row.Gstin);
            if (gstin.Length == 0) continue;
            if (!IsGstinFormat(gstin))
                throw new InvalidOperationException("Invalid branch GSTIN: " + gstin + ".");
            if (expectedPan.Length == 0 || !gstin.Substring(2, 10).Equals(expectedPan, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Branch GSTIN " + gstin + " does not belong to this Client's PAN.");
            if (gstin.Equals(primary, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(gstin)) throw new InvalidOperationException("Duplicate branch GSTIN: " + gstin + ".");
            values.Add(gstin);
        }
        if (values.Count > 100) throw new InvalidOperationException("Maximum 100 additional GSTIN registrations.");
        return values;
    }

    public static string NormalizeGstin(string? text) =>
        new string((text ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string NormalizePan(string? text) =>
        new string((text ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static bool IsGstinFormat(string value) =>
        value.Length == 15 && value[0] is >= '0' and <= '9' &&
        value[1] is >= '0' and <= '9' &&
        value.All(x => x is >= 'A' and <= 'Z' or >= '0' and <= '9');

    private static bool LooksLikeGstin(string text)
    {
        var candidate = NormalizeGstin(text);
        return candidate.Length == 15 && char.IsDigit(candidate[0]) && char.IsDigit(candidate[1]);
    }
}

public sealed class ClientBranchRecord
{
    public string BranchId { get; set; } = "";
    public bool IsAutoNamed { get; set; }
    public string Name { get; set; } = "";
    public string Gstin { get; set; } = "";
    public string GstinLabel => Gstin.Length == 0 ? "No GSTIN" : Gstin;
}
