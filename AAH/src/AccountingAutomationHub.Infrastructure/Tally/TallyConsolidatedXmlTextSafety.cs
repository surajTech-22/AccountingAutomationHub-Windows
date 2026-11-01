using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

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
            // An invalid character can also arrive as &#4; / &#x04;.
            // XmlConvert.VerifyXmlChars does not decode character references,
            // so older code silently let these through to XDocument.Parse.
            if (character == '&' && offset + 2 < xml.Length && xml[offset + 1] == '#' &&
                TryReadForbiddenControlReference(xml, offset, out var referenceEnd))
            {
                RequireRemovableTextField(elements, inMarkup, offset);
                cleaned++;
                offset = referenceEnd;
                continue;
            }
            if (character < ' ' && character is not ('\t' or '\r' or '\n'))
            {
                RequireRemovableTextField(elements, inMarkup, offset);
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

    // Every independent XML boundary must pass the same strict scanner.
    // The caller's stage is included in errors, never raw customer data.
    public static XDocument ParseDocument(
        string xml, string stage, LoadOptions options = LoadOptions.None)
    {
        try
        {
            return XDocument.Parse(Repair(xml).Xml, options);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Consolidated Tally XML rejected at " + stage + ": " + exception.Message,
                exception);
        }
    }

    private static void RequireRemovableTextField(Stack<string> elements, bool inMarkup, int offset)
    {
        if (inMarkup || elements.Count == 0)
            throw new InvalidOperationException(
                "Invalid control character in Tally XML attribute/markup at offset " + offset + ".");
        var field = elements.Peek();
        if (ImmutableFields.Contains(field))
            throw new InvalidOperationException(
                "Invalid control character in protected Tally field '" +
                field + "' at offset " + offset + ". Correct its original source.");
    }

    private static bool TryReadForbiddenControlReference(string xml, int offset, out int end)
    {
        end = xml.IndexOf(';', offset + 2);
        if (end < 0 || end - offset > 20) return false;
        var numeric = xml.AsSpan(offset + 2, end - offset - 2);
        var hex = numeric.Length > 0 && numeric[0] is 'x' or 'X';
        if (hex) numeric = numeric[1..];
        if (numeric.Length == 0 ||
            !int.TryParse(numeric,
                hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
                CultureInfo.InvariantCulture, out var codePoint))
            return false;
        return codePoint < 0x20 && codePoint is not (0x09 or 0x0A or 0x0D);
    }
}
public sealed record TallyConsolidatedXmlTextRepairResult(string Xml, int RemovedControlCharacters);
