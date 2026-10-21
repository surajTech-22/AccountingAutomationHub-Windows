using System.Security.Cryptography;
using System.Text;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Domain.Sales;

namespace AccountingAutomationHub.Infrastructure.Imports;

/// <summary>
/// Fix39 accounting-data identity for Sales/Purchase GST source sets.
/// Deliberately excludes path, file timestamp, source root and ZIP/container metadata so
/// an unchanged payload copied/touched in another watched location does not rewrite accounting rows.
/// </summary>
public static class SalesPurchaseAccountingContentFingerprint
{
    public static string BuildSales(SalesAutoImportResult result) =>
        BuildSales(result.SourceManifest, result.Verification.CustomerMasterSourceHash);

    public static string BuildSales(
        IEnumerable<SalesSourceManifestItem> manifest,
        string? customerMasterSourceHash)
    {
        var lines = manifest
            .Select(item => BuildLine(
                item.FileType,
                item.ReturnPeriod,
                item.PayloadHash,
                item.SourcePath,
                item.EmbeddedTimestamp))
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var builder = new StringBuilder();
        builder.Append("SALES|MASTER|")
            .Append((customerMasterSourceHash ?? string.Empty).Trim().ToUpperInvariant())
            .AppendLine();
        foreach (var line in lines) builder.AppendLine(line);
        return Hash(builder.ToString());
    }

    public static string BuildPurchase(PurchaseAutoImportResult result) =>
        BuildPurchase(result.SourceManifest);

    public static string BuildPurchase(IEnumerable<PurchaseSourceManifestItem> manifest)
    {
        var lines = manifest
            .Select(item => BuildLine(
                item.FileType,
                item.ReturnPeriod,
                item.PayloadHash,
                item.SourcePath,
                item.EmbeddedTimestamp))
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var builder = new StringBuilder("PURCHASE\n");
        foreach (var line in lines) builder.AppendLine(line);
        return Hash(builder.ToString());
    }

    private static string BuildLine(
        string? fileType,
        string? returnPeriod,
        string? payloadHash,
        string? sourcePath,
        string? embeddedTimestamp)
    {
        var type = (fileType ?? string.Empty).Trim().ToUpperInvariant();
        var period = (returnPeriod ?? string.Empty).Trim().ToUpperInvariant();
        var hash = (payloadHash ?? string.Empty).Trim().ToUpperInvariant();
        if (type.Length == 0 && period.Length == 0 && hash.Length == 0) return string.Empty;

        // A payload hash is the preferred accounting authority. If an older manifest did not
        // store one, fail conservatively by retaining source identity instead of falsely
        // declaring two unproved sources equivalent.
        if (hash.Length == 0)
        {
            var path = NormalizePath(sourcePath);
            var timestamp = (embeddedTimestamp ?? string.Empty).Trim().ToUpperInvariant();
            hash = $"UNPROVED:{path}:{timestamp}";
        }
        return $"{type}|{period}|{hash}";
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path).Trim().ToUpperInvariant(); }
        catch { return path.Trim().ToUpperInvariant(); }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
