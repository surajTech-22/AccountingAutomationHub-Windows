using System.Text.RegularExpressions;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Conservative Tally backup family classifier. AAH must never assume that a *.900/TBK900
/// backup is ERP 9 merely from the storage format: TallyPrime/Server releases can also use
/// 900-series backup names. Only positive Prime evidence labels a backup TallyPrime; ERP 9 is
/// assigned only when the source name itself explicitly says ERP 9. Everything else remains
/// product-neutral until restore/live validation proves the runtime family.
/// </summary>
public static class TallyBackupClassifier
{
    public const string TallyPrime = "TallyPrime";
    public const string TallyErp9 = "Tally.ERP 9";
    public const string Unverified = "Tally backup - product family unverified";

    private static readonly Regex Prime1800 = new(
        @"(?ix)(?:^|[^0-9])(?:TBK|TDBK|TSDBK)?1800(?:[_\.-]|$)|(?:company|cmpsav|cmpsave)\.1800(?:$|[^0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ExplicitPrime = new(
        @"(?i)tally[\s._-]*prime",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ExplicitErp9 = new(
        @"(?i)tally[\s._-]*(?:erp[\s._-]*9|erp9)|\berp[\s._-]*9\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static TallyBackupClassification Classify(string? storedPath, string? originalName)
    {
        var name = Path.GetFileName(originalName ?? string.Empty);
        var stored = Path.GetFileName(storedPath ?? string.Empty);
        var evidenceText = string.Join(" ", new[] { name, stored }.Where(value => !string.IsNullOrWhiteSpace(value)));

        if (ExplicitPrime.IsMatch(evidenceText) || Prime1800.IsMatch(evidenceText))
            return new TallyBackupClassification(TallyPrime, "Prime filename/data-generation evidence (1800/TallyPrime).");

        if (ExplicitErp9.IsMatch(evidenceText))
            return new TallyBackupClassification(TallyErp9, "The source filename explicitly identifies Tally.ERP 9.");

        // 900-series names are deliberately neutral. They occur in legacy ERP 9 backups but are
        // also used by TallyPrime/TallyPrime Server in supported backup workflows, so treating
        // TBK900/Company.900 as ERP 9 creates false legacy-mode links.
        return new TallyBackupClassification(Unverified,
            evidenceText.Contains("900", StringComparison.OrdinalIgnoreCase)
                ? "900-series Tally storage detected; product family requires restore/live validation."
                : "No product-specific backup signature was proven; restore/live validation required.");
    }
}

public sealed record TallyBackupClassification(string ProductFamily, string Evidence)
{
    public string StatusLabel => ProductFamily switch
    {
        TallyBackupClassifier.TallyPrime => "TallyPrime backup",
        TallyBackupClassifier.TallyErp9 => "Tally.ERP 9 backup",
        _ => "Tally backup (product family unverified)"
    };
}
