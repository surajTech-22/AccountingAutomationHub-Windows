using System.Text;
using System.Xml;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>Repair invalid C0 punctuation in original XML narration/text only.
/// Amounts, ledger identities and XML attributes must remain byte-for-byte intact.
/// </summary>
public static class TallyConsolidatedXmlTextSafety
{
    private static readonly HashSet<string> ImmutableFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "AMOUNT", "DATE", "REFERENCEDATE", "VOUCHERNUMBER", "VOUCHERTYPENAME",
        "REFERENCE", "GUID", "MASTERID", "ALTERID", "LEDGERNAME", "PARTYLEDGERNAME",
        "PARTYNAME", "BASICBASEPARTYNAME", "STOCKITEMNAME", "NAME", "PARENT",
        "GSTIN", "PARTYGSTIN", "GSTRATE", "RATE", "ACTUALQTY", "BILLEDQTY",
        "UNIT", "BASEUNITS", "SVCURRENTCOMPANY"
    };

    public static TallyConsolidatedXmlTextRepairResult Repair(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new InvalidOperationException("Prepared Tally XML is missing.");

        var output = new StringBuilder(xml.Length);
        var tag = new StringBuilder();
        var elements = new Stack<string>();
        var inMarkup = false;
        char quote = '\0';
        var cleaned = 0;
        for (var offset = 0; offset < xml.Length; offset++)
        {
            var character = xml[offset];
            if (character < ' ' && character is not ('\t' or '\r' or '\n'))
            {
                if (inMarkup)
                    throw new InvalidOperationException("Invalid control character in Tally XML attribute/markup at offset " + offset + ".");
                var field = elements.Count == 0 ? string.Empty : elements.Peek();
                if (ImmutableFields.Contains(field))
                    throw new InvalidOperationException("Invalid control character in protected Tally field '" +
                        field + "' at offset " + offset + ". Correct its original source.");
                cleaned++; // XML-only formatting cleanup. Do not touch numbers or Tally names.
                continue;
            }
            output.Append(character);
            if (!inMarkup)
            {
                if (character == '<')
                {
                    inMarkup = true;
                    quote = '\0';
                    tag.Clear();
                }
                continue;
            }
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                tag.Append(character);
                continue;
            }
            if (character == '\'' || character == '"')
            {
                quote = character;
                tag.Append(character);
                continue;
            }
            if (character == '>')
            {
                var value = tag.ToString().Trim();
                if (value.StartsWith("/", StringComparison.Ordinal))
                {
                    if (elements.Count != 0) elements.Pop();
                }
                else if (!value.StartsWith("!", StringComparison.Ordinal) &&
                         !value.StartsWith("?", StringComparison.Ordinal) &&
                         !value.EndsWith("/", StringComparison.Ordinal))
                {
                    var name = value.Split(' ', '\t', '\r', '\n')[0];
                    if (name.Length != 0) elements.Push(name);
                }
                inMarkup = false;
                continue;
            }
            tag.Append(character);
        }
        if (inMarkup) throw new InvalidOperationException("Prepared Tally XML has an unfinished tag.");
        var repaired = output.ToString();
        try { XmlConvert.VerifyXmlChars(repaired); }
        catch (XmlException e)
        {
            throw new InvalidOperationException("Branch XML contains invalid Unicode requiring source correction.", e);
        }
        return new(repaired, cleaned);
    }
}
public sealed record TallyConsolidatedXmlTextRepairResult(string Xml, int RemovedControlCharacters);
