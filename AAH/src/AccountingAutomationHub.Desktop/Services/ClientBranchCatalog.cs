using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
            if (name.Length == 0) name = "Branch " + gstin;
            if (name.Length > 100 || name.Contains('|'))
                throw new InvalidOperationException("Branch names must be at most 100 characters and cannot contain '|'.");
            if (!seenNames.Add(name)) throw new InvalidOperationException("Duplicate branch name: " + name + ".");
            if (gstin.Length > 0 && !seenGstins.Add(gstin))
                throw new InvalidOperationException("Duplicate branch GSTIN: " + gstin + ".");
            result.Add(new ClientBranchRecord { Name = name, Gstin = gstin });
        }
        if (result.Count > 100)
            throw new InvalidOperationException("A client can have at most 100 branches.");
        return result;
    }

    public static string Format(IEnumerable<ClientBranchRecord> branches) =>
        string.Join(Environment.NewLine, branches.Select(x =>
            string.IsNullOrWhiteSpace(x.Gstin) ? x.Name :
            x.Name.Equals("Branch " + x.Gstin, StringComparison.OrdinalIgnoreCase)
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
            if (name.Length == 0 && gstin.Length > 0) name = "Branch " + gstin;
            if (name.Length == 0 || !usedNames.Add(name)) continue;
            if (gstin.Length > 0 && !usedGstins.Add(gstin)) continue;
            result.Add(new ClientBranchRecord { Name = name, Gstin = gstin });
        }
        // Existing multi-GSTIN clients automatically gain branches, without
        // altering the Client count or creating new client/FY workspaces.
        foreach (var gstin in registered)
        {
            if (usedGstins.Add(gstin))
            {
                var name = "Branch " + gstin;
                if (!usedNames.Add(name)) name += " (GST)";
                result.Add(new ClientBranchRecord { Name = name, Gstin = gstin });
            }
        }
        return result;
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
    public string Name { get; set; } = "";
    public string Gstin { get; set; } = "";
    public string GstinLabel => Gstin.Length == 0 ? "No GSTIN" : Gstin;
}
