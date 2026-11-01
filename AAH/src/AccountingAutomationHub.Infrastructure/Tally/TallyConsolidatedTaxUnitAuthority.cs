using System.Xml.Linq;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Exact registration-to-Tally Tax Unit authority from the currently selected
/// writable destination's native Master export. A name/state coincidence does
/// not establish GSTIN authority. ERP 9 / incomplete Prime collections remain
/// safely blocked until a supported registration-bearing native response exists.
/// </summary>
public static class TallyConsolidatedTaxUnitAuthorityReader
{
    public static IReadOnlyList<TallyConsolidatedTaxUnitAuthority> Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new InvalidOperationException("Native Tally Tax Unit master response is empty.");
        XDocument document;
        try { document = XDocument.Parse(TallyXmlSanitizer.Sanitize(xml)); }
        catch (Exception ex) when (ex is System.Xml.XmlException or ArgumentException)
        { throw new InvalidOperationException("Native destination Tax Unit XML could not be parsed.", ex); }

        static string Field(XElement item, params string[] keys)
        {
            foreach (var key in keys)
            {
                var child = item.Elements().FirstOrDefault(x =>
                    x.Name.LocalName.Equals(key, StringComparison.OrdinalIgnoreCase));
                var value = child?.Value.Trim() ??
                    item.Attributes().FirstOrDefault(x =>
                        x.Name.LocalName.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value.Trim();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return string.Empty;
        }
        var all = new List<TallyConsolidatedTaxUnitAuthority>();
        foreach (var unit in document.Descendants().Where(x =>
                     x.Name.LocalName.Equals("TAXUNIT", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Field(unit, "NAME");
            var gstin = Field(unit, "GSTREGNUMBER", "GSTREGISTRATIONNUMBER", "GSTIN");
            var state = Field(unit, "STATENAME", "STATE");
            var id = Field(unit, "MASTERID");
            if (name.Length == 0) continue;
            // Do not mistake an unsupported, incomplete TaxUnit collection
            // for positive registration authority. The wrapper will require
            // a single exact GSTIN and MASTERID match before GST vouchers.
            all.Add(new(name, gstin.ToUpperInvariant(), state, id));
        }
        return all;
    }
}

/// <summary>A live, destination-specific TaxUnit with verified registration ID.</summary>
public sealed record TallyConsolidatedTaxUnitAuthority(
    string Name, string Gstin, string State, string MasterId);
