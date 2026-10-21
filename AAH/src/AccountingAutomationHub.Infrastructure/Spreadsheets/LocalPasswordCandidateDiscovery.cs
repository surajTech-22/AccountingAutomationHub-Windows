using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AccountingAutomationHub.Infrastructure.Spreadsheets;

/// <summary>
/// Discovers likely workbook passwords without requiring a specifically named password file.
/// Candidate order is deterministic and privacy-preserving: the protected file name is checked
/// first, then the smallest nearby files in the same folder. Candidate values are never logged.
/// </summary>
public static partial class LocalPasswordCandidateDiscovery
{
    private const int MaxNearbyFiles = 24;
    private const long MaxNearbyFileBytes = 5L * 1024 * 1024;
    private const int MaxCandidates = 32;
    private const int MaxTextBytesPerFile = 512 * 1024;

    private static readonly HashSet<string> PreferredTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".csv", ".tsv", ".log", ".ini", ".cfg", ".conf", ".json", ".xml", ".md", ".rtf"
    };

    private static readonly HashSet<string> ZipDocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".docm", ".xlsx", ".xlsm", ".xltx", ".xltm"
    };

    /// <summary>
    /// Returns true only for small local helper files that are very likely to exist solely
    /// to supply a password to a nearby protected workbook. Accounting Auto Import must
    /// ignore these files instead of turning them into Action Center exceptions. Candidate
    /// values are inspected locally and are never returned, persisted, or logged here.
    /// </summary>
    public static bool IsLikelyPasswordCompanionFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        FileInfo file;
        try { file = new FileInfo(path); }
        catch { return false; }
        if (file.Length <= 0 || file.Length > MaxNearbyFileBytes) return false;

        var extension = Path.GetExtension(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        // A protected accounting workbook commonly carries its own password in the file name
        // (for example "... PW 91650190562.xlsx"). Filename text alone must therefore never
        // turn a spreadsheet into a password-helper file. Text/config helper files may use the
        // filename shortcut; spreadsheet helpers still require actual small helper content below.
        if (PasswordHelperFileName().IsMatch(stem) && PreferredTextExtensions.Contains(extension)) return true;

        // Do not deeply inspect large arbitrary binaries merely because they are nearby.
        // The password-discovery formats below are the same local formats already trusted
        // by Discover(), and extraction is bounded by MaxTextBytesPerFile.
        if (!PreferredTextExtensions.Contains(extension) &&
            !ZipDocumentExtensions.Contains(extension) &&
            !extension.Equals(".xls", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var pieces = ExtractCandidateText(path)
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Take(12)
                .ToArray();
            if (pieces.Length == 0) return false;

            // A labelled password note is definitive even when the file name is generic.
            if (pieces.Any(value => LabelledPassword().IsMatch(value))) return true;

            // A tiny one/few-cell helper often contains only the password itself. Treat it
            // as a helper only when every meaningful value looks like a standalone secret
            // and there are no accounting/table cues. This deliberately avoids suppressing
            // real CSV/Excel accounting sources.
            if (pieces.Length <= 3 &&
                pieces.All(LooksLikeStandalonePassword) &&
                !pieces.Any(LooksLikeAccountingContent))
                return true;
        }
        catch
        {
            // Best-effort recognition only. Failure falls back to normal classification.
        }

        return false;
    }

    private static bool LooksLikeAccountingContent(string value)
    {
        var text = value.Trim();
        if (text.Length == 0) return false;
        string[] cues =
        [
            "DATE", "NARRATION", "PARTICULAR", "DESCRIPTION", "DEBIT", "CREDIT",
            "BALANCE", "AMOUNT", "INVOICE", "GSTIN", "TAXABLE", "IGST", "CGST",
            "SGST", "LEDGER", "VOUCHER", "ACCOUNT", "BANK", "QUANTITY", "RATE"
        ];
        return cues.Any(cue => text.Contains(cue, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> Discover(string protectedFilePath)
    {
        if (string.IsNullOrWhiteSpace(protectedFilePath)) return Array.Empty<string>();

        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        AddFileNameCandidates(Path.GetFileNameWithoutExtension(protectedFilePath), candidates, seen);
        if (candidates.Count >= MaxCandidates) return candidates;

        var directory = Path.GetDirectoryName(protectedFilePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return candidates;

        IEnumerable<FileInfo> nearby;
        try
        {
            nearby = new DirectoryInfo(directory)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(file => !PathsEqual(file.FullName, protectedFilePath))
                .Where(file => file.Length > 0 && file.Length <= MaxNearbyFileBytes)
                .OrderBy(file => file.Length)
                .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxNearbyFiles)
                .ToArray();
        }
        catch
        {
            return candidates;
        }

        foreach (var file in nearby)
        {
            if (candidates.Count >= MaxCandidates) break;

            // A nearby note may itself carry the password in its file name. This is still
            // size-ordered; no specific file name such as password.txt is required.
            AddFileNameCandidates(Path.GetFileNameWithoutExtension(file.Name), candidates, seen);
            if (candidates.Count >= MaxCandidates) break;

            try
            {
                foreach (var piece in ExtractCandidateText(file.FullName))
                {
                    AddTextCandidates(piece, candidates, seen);
                    if (candidates.Count >= MaxCandidates) break;
                }
            }
            catch
            {
                // Password discovery is best-effort. A damaged/locked nearby file must never
                // prevent the protected statement itself from reaching the normal recovery path.
            }
        }

        return candidates;
    }

    private static IEnumerable<string> ExtractCandidateText(string path)
    {
        var extension = Path.GetExtension(path);
        if (ZipDocumentExtensions.Contains(extension))
        {
            foreach (var value in ExtractZipDocumentValues(path)) yield return value;
            yield break;
        }

        if (PreferredTextExtensions.Contains(extension))
        {
            foreach (var value in ExtractTextFileValues(path)) yield return value;
            yield break;
        }

        // Old .doc/.xls and arbitrary small files are not required to have a particular name or
        // extension. Extract printable ASCII/UTF-16 strings as a low-risk final local source.
        foreach (var value in ExtractBinaryStrings(path)) yield return value;
    }

    private static IEnumerable<string> ExtractTextFileValues(string path)
    {
        var bytes = ReadPrefix(path, MaxTextBytesPerFile);
        if (bytes.Length == 0) yield break;
        var text = DecodeText(bytes);
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var value = line.Trim();
            if (value.Length > 0) yield return value;
        }
    }

    private static IEnumerable<string> ExtractZipDocumentValues(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var extension = Path.GetExtension(path);
        IEnumerable<ZipArchiveEntry> entries;
        if (extension.StartsWith(".doc", StringComparison.OrdinalIgnoreCase))
        {
            entries = archive.Entries.Where(entry =>
                entry.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.StartsWith("word/header", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            entries = archive.Entries.Where(entry =>
                entry.FullName.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase));
        }

        var consumed = 0L;
        foreach (var entry in entries.OrderBy(entry => entry.Length))
        {
            if (consumed >= MaxTextBytesPerFile) yield break;
            if (entry.Length <= 0 || entry.Length > MaxTextBytesPerFile) continue;

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
            var xml = reader.ReadToEnd();
            consumed += Encoding.UTF8.GetByteCount(xml);
            XDocument document;
            try { document = XDocument.Parse(xml, LoadOptions.None); }
            catch { continue; }

            foreach (var node in document.DescendantNodes().OfType<XText>())
            {
                var value = node.Value.Trim();
                if (value.Length > 0) yield return value;
            }
        }
    }

    private static IEnumerable<string> ExtractBinaryStrings(string path)
    {
        var bytes = ReadPrefix(path, MaxTextBytesPerFile);
        if (bytes.Length == 0) yield break;

        foreach (Match match in PrintableAscii().Matches(Encoding.Latin1.GetString(bytes)))
            yield return match.Value.Trim();

        if (bytes.Length >= 4)
        {
            var unicode = Encoding.Unicode.GetString(bytes);
            foreach (Match match in PrintableUnicode().Matches(unicode))
                yield return match.Value.Trim();
        }
    }

    private static void AddFileNameCandidates(string? stem, List<string> candidates, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(stem)) return;
        foreach (Match match in PasswordInFileName().Matches(stem))
        {
            if (match.Groups.Count < 2) continue;
            AddCandidate(match.Groups[1].Value, candidates, seen);
        }
    }

    private static void AddTextCandidates(string text, List<string> candidates, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var trimmed = text.Trim();

        foreach (Match match in LabelledPassword().Matches(trimmed))
        {
            if (match.Groups.Count < 2) continue;
            AddCandidate(match.Groups[1].Value, candidates, seen);
        }

        // If a user keeps a tiny note/Word/Excel file containing only the password, there may be
        // no label. Accept short cell/line-like values but avoid long prose and obvious XML noise.
        foreach (var part in trimmed.Split(['\t', ',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (LooksLikeStandalonePassword(part)) AddCandidate(part, candidates, seen);
            if (candidates.Count >= MaxCandidates) return;
        }
    }

    private static bool LooksLikeStandalonePassword(string value)
    {
        var candidate = TrimDecorations(value);
        if (candidate.Length is < 1 or > 64) return false;
        if (candidate.StartsWith('<') || candidate.EndsWith('>')) return false;
        if (candidate.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            candidate.Contains("https://", StringComparison.OrdinalIgnoreCase)) return false;
        if (candidate.Count(char.IsWhiteSpace) > 2) return false;
        if (candidate.All(char.IsLetter) && candidate.Length > 24) return false;
        return candidate.Any(char.IsLetterOrDigit);
    }

    private static void AddCandidate(string? raw, List<string> candidates, HashSet<string> seen)
    {
        if (candidates.Count >= MaxCandidates || string.IsNullOrWhiteSpace(raw)) return;
        var value = TrimDecorations(raw);
        if (value.Length is < 1 or > 64) return;
        if (!seen.Add(value)) return;
        candidates.Add(value);
    }

    private static string TrimDecorations(string value) =>
        value.Trim().Trim('"', '\'', '`', '[', ']', '(', ')', '{', '}', ':', '=', '-').Trim();

    private static byte[] ReadPrefix(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = (int)Math.Min(stream.Length, maxBytes);
        var bytes = new byte[length];
        var total = 0;
        while (total < length)
        {
            var read = stream.Read(bytes, total, length - total);
            if (read <= 0) break;
            total += read;
        }
        if (total == bytes.Length) return bytes;
        Array.Resize(ref bytes, total);
        return bytes;
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return Path.GetFullPath(first).Equals(Path.GetFullPath(second),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch { return first.Equals(second, StringComparison.OrdinalIgnoreCase); }
    }

    [GeneratedRegex(@"(?ix)(?:^|[\s_\-\(\[\{])(?:password|passwd|pwd|pw|passcode|pass)(?:$|[\s_\-\)\]\}])")]
    private static partial Regex PasswordHelperFileName();

    [GeneratedRegex(@"(?ix)(?:^|[\s_\-\(\[\{])(?:password|passwd|pwd|pw|passcode|pass)\s*[:=_\-]?\s*([^\s_\)\]\}]{1,64})")]
    private static partial Regex PasswordInFileName();

    [GeneratedRegex("(?ix)\\b(?:password|passwd|pwd|pw|passcode|pass)\\b\\s*(?:is\\s*)?[:=\\-]?\\s*[\"']?([^\\r\\n,;|\"']{1,64})")]
    private static partial Regex LabelledPassword();

    [GeneratedRegex(@"[\x20-\x7E]{3,80}")]
    private static partial Regex PrintableAscii();

    [GeneratedRegex(@"[\x20-\x7E]{3,80}")]
    private static partial Regex PrintableUnicode();
}
