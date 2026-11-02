using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Verified, read-only AAH branch XML cache for consolidated collection.
/// This is not a Tally import cache or a voucher duplicate/ALTER decision.
/// A saved snapshot is reusable only for the exact private Client/FY, source
/// fingerprint and XML checksum. Legacy snapshots without these proofs rebuild.
/// </summary>
public static class TallyConsolidatedBranchXmlSnapshotCache
{
    public const string FingerprintLabel = "AAH source fingerprint SHA256: ";
    public const string XmlChecksumLabel = "Prepared XML SHA256: ";
    private const string SourceLabel = "Source: saved offline branch accounting; no Tally posting";

    public static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? "")));

    public static bool TryRead(string xmlPath, string statusPath, string clientCode,
        string financialYear, string sourceFingerprint, out string xml, out string status)
    {
        xml = "";
        status = "";
        if (sourceFingerprint.Length != 64 || !File.Exists(xmlPath) || !File.Exists(statusPath))
            return false;
        try
        {
            var candidateStatus = File.ReadAllText(statusPath, Encoding.UTF8);
            var lines = candidateStatus.Split(["\r\n", "\n"], StringSplitOptions.None)
                .Select(x => x.Trim()).ToArray();
            bool HasLine(string line) =>
                lines.Any(x => x.Equals(line, StringComparison.Ordinal));
            if (!HasLine(SourceLabel) ||
                !HasLine("Client: " + clientCode + " | FY " + financialYear) ||
                !HasLine("Tally company: AAH Prepared Branch " + clientCode) ||
                !HasLine(FingerprintLabel + sourceFingerprint))
                return false;
            var checksum = lines.SingleOrDefault(x => x.StartsWith(XmlChecksumLabel, StringComparison.Ordinal));
            if (checksum is null || checksum.Length != XmlChecksumLabel.Length + 64)
                return false;
            var readyLine = lines.SingleOrDefault(x => x.StartsWith("Ready in XML: ", StringComparison.Ordinal));
            if (readyLine is null || !int.TryParse(readyLine["Ready in XML: ".Length..],
                    NumberStyles.None, CultureInfo.InvariantCulture, out var ready) || ready <= 0)
                return false;

            var candidateXml = File.ReadAllText(xmlPath, Encoding.UTF8);
            if (!Sha256(candidateXml).Equals(checksum[XmlChecksumLabel.Length..], StringComparison.Ordinal))
                return false;
            // A valid checksum alone is insufficient; assert original branch
            // ownership, Tally envelope and exactly the original ready count.
            var xmlCompany = TallyConsolidatedBranchSourcePolicy.Validate(candidateXml, candidateStatus,
                clientCode, financialYear, new AccountingAutomationHub.Domain.Tally.TallyCompanyInfo
                { Name = "AAH private-branch source validation" }, null);
            if (!xmlCompany.Equals("AAH Prepared Branch " + clientCode,
                    StringComparison.OrdinalIgnoreCase))
                return false;
            TallyConsolidatedBranchSourcePolicy.RequirePreparedVoucherCount(
                candidateXml, ready, clientCode);
            xml = candidateXml;
            status = candidateStatus;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidOperationException or System.Xml.XmlException or ArgumentException)
        {
            // Invalid, partial, tampered or old-format cache is never accepted.
            // Caller regenerates using the unchanged individual exporter.
            return false;
        }
    }

    /// <summary>
    /// A consolidated build collects several independent branch source snapshots.
    /// A branch may change while another branch is being loaded, even when its own
    /// cache read/generation was individually consistent. Check ALL branch inputs
    /// before and after combining them, and fail closed rather than export a
    /// mixed-time collection of otherwise individually valid XML files.
    /// </summary>
    public static void RequireUnchangedSources<TClient>(
        IEnumerable<(TClient Client, string BranchName, string Fingerprint)> sources,
        Func<TClient, string> computeFingerprint)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(computeFingerprint);
        foreach (var source in sources)
        {
            if (source.Fingerprint.Length != 64 ||
                !source.Fingerprint.Equals(
                    computeFingerprint(source.Client), StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Branch '" + source.BranchName +
                    "' accounting changed during consolidated XML collection. " +
                    "Retry to prepare all branches from consistent saved sources.");
        }
    }

    public static string StampStatus(string existingStatus, string sourceFingerprint, string xml) =>
        existingStatus.TrimEnd('\r', '\n') + Environment.NewLine +
        FingerprintLabel + sourceFingerprint + Environment.NewLine +
        XmlChecksumLabel + Sha256(xml) + Environment.NewLine;
}
