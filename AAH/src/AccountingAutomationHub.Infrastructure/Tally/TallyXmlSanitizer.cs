using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Removes characters and numeric character references that are not valid in
/// XML 1.0. Tally can emit control characters such as 0x04 either directly or
/// as &#4; / &#x04;, which causes System.Xml parsers to reject the full response.
/// </summary>
public static partial class TallyXmlSanitizer
{
    [GeneratedRegex(
        "&#(?:(?<decimal>[0-9]+)|[xX](?<hexadecimal>[0-9A-Fa-f]+));",
        RegexOptions.CultureInvariant)]
    private static partial Regex NumericCharacterReferenceRegex();

    [GeneratedRegex(
        "&(?!#(?:[0-9]+|[xX][0-9A-Fa-f]+);|amp;|lt;|gt;|quot;|apos;)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BareAmpersandRegex();

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Older Tally.ERP 9 builds can return otherwise valid XML containing a
        // literal ampersand in narration/ledger text. XDocument correctly rejects
        // that as malformed XML, which previously stopped duplicate verification
        // before any voucher was exported. Repair only bare ampersands while
        // preserving the five XML entities and numeric character references.
        var ampersandsSanitized = BareAmpersandRegex().Replace(value, "&amp;");

        var referencesSanitized = NumericCharacterReferenceRegex().Replace(
            ampersandsSanitized,
            static match => IsValidNumericReference(match)
                ? match.Value
                : string.Empty);

        var builder = new StringBuilder(referencesSanitized.Length);

        for (var index = 0; index < referencesSanitized.Length; index++)
        {
            var character = referencesSanitized[index];

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 < referencesSanitized.Length &&
                    char.IsLowSurrogate(referencesSanitized[index + 1]))
                {
                    var codePoint = char.ConvertToUtf32(
                        character,
                        referencesSanitized[index + 1]);

                    if (IsValidXmlCharacter(codePoint))
                    {
                        builder.Append(character);
                        builder.Append(referencesSanitized[index + 1]);
                    }

                    index++;
                }

                continue;
            }

            if (char.IsLowSurrogate(character))
            {
                continue;
            }

            if (IsValidXmlCharacter(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool IsValidNumericReference(Match match)
    {
        var decimalGroup = match.Groups["decimal"];
        if (decimalGroup.Success)
        {
            return int.TryParse(
                       decimalGroup.Value,
                       NumberStyles.None,
                       CultureInfo.InvariantCulture,
                       out var decimalCodePoint) &&
                   IsValidXmlCharacter(decimalCodePoint);
        }

        var hexadecimalGroup = match.Groups["hexadecimal"];
        return hexadecimalGroup.Success &&
               int.TryParse(
                   hexadecimalGroup.Value,
                   NumberStyles.AllowHexSpecifier,
                   CultureInfo.InvariantCulture,
                   out var hexadecimalCodePoint) &&
               IsValidXmlCharacter(hexadecimalCodePoint);
    }

    private static bool IsValidXmlCharacter(int codePoint) =>
        codePoint is 0x09 or 0x0A or 0x0D ||
        codePoint is >= 0x20 and <= 0xD7FF ||
        codePoint is >= 0xE000 and <= 0xFFFD ||
        codePoint is >= 0x10000 and <= 0x10FFFF;
}
