using System.Xml.Linq;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Wire-format only. The original AAH/individual branch accounting,
/// GST calculation, source GSTIN, DR/CR and state-of-supply selections are
/// immutable. Tally expects the official state NAME, not the two-digit GST
/// state CODE, in STATENAME, PRIORSTATENAME and PLACEOFSUPPLY.
/// </summary>
public static class TallyConsolidatedStateNamePolicy
{
    private static readonly IReadOnlyDictionary<string, string> Names =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["01"] = "Jammu & Kashmir", ["02"] = "Himachal Pradesh",
            ["03"] = "Punjab", ["04"] = "Chandigarh", ["05"] = "Uttarakhand",
            ["06"] = "Haryana", ["07"] = "Delhi", ["08"] = "Rajasthan",
            ["09"] = "Uttar Pradesh", ["10"] = "Bihar", ["11"] = "Sikkim",
            ["12"] = "Arunachal Pradesh", ["13"] = "Nagaland",
            ["14"] = "Manipur", ["15"] = "Mizoram", ["16"] = "Tripura",
            ["17"] = "Meghalaya", ["18"] = "Assam", ["19"] = "West Bengal",
            ["20"] = "Jharkhand", ["21"] = "Odisha",
            ["22"] = "Chhattisgarh", ["23"] = "Madhya Pradesh",
            ["24"] = "Gujarat", ["25"] = "Daman & Diu",
            ["26"] = "Dadra & Nagar Haveli and Daman & Diu",
            ["27"] = "Maharashtra", ["28"] = "Andhra Pradesh",
            ["29"] = "Karnataka", ["30"] = "Goa", ["31"] = "Lakshadweep",
            ["32"] = "Kerala", ["33"] = "Tamil Nadu", ["34"] = "Puducherry",
            ["35"] = "Andaman & Nicobar Islands", ["36"] = "Telangana",
            ["37"] = "Andhra Pradesh", ["38"] = "Ladakh",
            ["97"] = "Other Territory", ["99"] = "Other Country"
        };

    public static string Normalize(string original)
    {
        var value = (original ?? "").Trim();
        if (value.Length == 0) return value;
        if (!value.All(char.IsAsciiDigit) || value.Length > 2) return value;
        var code = value.PadLeft(2, '0');
        if (!Names.TryGetValue(code, out var name))
            throw new InvalidOperationException(
                "Unknown GST state code '" + code +
                "' in source branch Tally XML; correct the source, do not invent a state.");
        return name;
    }

    public static void NormalizeVoucher(XElement voucher)
    {
        // Preserve the code's actual meaning (place of supply or voucher
        // source state) instead of changing it to the branch's own state.
        NormalizeNamedFields(voucher);
    }

    public static void NormalizeLedger(XElement ledger)
    {
        NormalizeNamedFields(ledger);
        // A registered party's GSTIN prefix is the state of that GST
        // registration. Source exporters sometimes placed the branch
        // state into the party's STATENAME instead. Correct only this
        // source registration metadata, not voucher/place of supply.
        var gstin = ledger.Elements().FirstOrDefault(x =>
            Is(x, "PARTYGSTIN"))?.Value.Trim().ToUpperInvariant() ?? "";
        if (gstin.Length != 15 || !gstin[..2].All(char.IsAsciiDigit))
            return;
        if (!Names.TryGetValue(gstin[..2], out var registeredState))
            throw new InvalidOperationException(
                "Unknown state prefix for source party GSTIN in consolidated Ledger.");
        foreach (var element in ledger.Descendants().Where(x =>
            Is(x, "STATENAME") || Is(x, "PRIORSTATENAME")))
            element.Value = registeredState;
    }

    private static void NormalizeNamedFields(XElement root)
    {
        foreach (var field in root.Descendants().Where(x =>
            Is(x, "STATENAME") || Is(x, "PRIORSTATENAME") ||
            Is(x, "PLACEOFSUPPLY")))
            field.Value = Normalize(field.Value);
    }

    private static bool Is(XElement element, string name) =>
        element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);
}
