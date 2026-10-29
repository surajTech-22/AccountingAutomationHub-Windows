using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AccountingAutomationHub.Application.Clients;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AccountingAutomationHub.Infrastructure.Clients;

public sealed partial class GstinProfileLookupService :
    IGstinProfileLookupService
{
    private const int MaximumSourceFiles = 750;

    private static readonly IReadOnlyDictionary<string, string>
        StateByCode =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["01"] = "Jammu and Kashmir",
                ["02"] = "Himachal Pradesh",
                ["03"] = "Punjab",
                ["04"] = "Chandigarh",
                ["05"] = "Uttarakhand",
                ["06"] = "Haryana",
                ["07"] = "Delhi",
                ["08"] = "Rajasthan",
                ["09"] = "Uttar Pradesh",
                ["10"] = "Bihar",
                ["11"] = "Sikkim",
                ["12"] = "Arunachal Pradesh",
                ["13"] = "Nagaland",
                ["14"] = "Manipur",
                ["15"] = "Mizoram",
                ["16"] = "Tripura",
                ["17"] = "Meghalaya",
                ["18"] = "Assam",
                ["19"] = "West Bengal",
                ["20"] = "Jharkhand",
                ["21"] = "Odisha",
                ["22"] = "Chhattisgarh",
                ["23"] = "Madhya Pradesh",
                ["24"] = "Gujarat",
                ["25"] = "Daman and Diu",
                ["26"] = "Dadra and Nagar Haveli and Daman and Diu",
                ["27"] = "Maharashtra",
                ["28"] = "Andhra Pradesh (Old)",
                ["29"] = "Karnataka",
                ["30"] = "Goa",
                ["31"] = "Lakshadweep",
                ["32"] = "Kerala",
                ["33"] = "Tamil Nadu",
                ["34"] = "Puducherry",
                ["35"] = "Andaman and Nicobar Islands",
                ["36"] = "Telangana",
                ["37"] = "Andhra Pradesh",
                ["38"] = "Ladakh",
                ["97"] = "Other Territory",
                ["99"] = "Centre Jurisdiction"
            };

    private static readonly IReadOnlyDictionary<string, int>
        MonthNumbers =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["JAN"] = 1,
                ["JANUARY"] = 1,
                ["FEB"] = 2,
                ["FEBRUARY"] = 2,
                ["MAR"] = 3,
                ["MARCH"] = 3,
                ["APR"] = 4,
                ["APRIL"] = 4,
                ["MAY"] = 5,
                ["JUN"] = 6,
                ["JUNE"] = 6,
                ["JUL"] = 7,
                ["JULY"] = 7,
                ["AUG"] = 8,
                ["AUGUST"] = 8,
                ["SEP"] = 9,
                ["SEPT"] = 9,
                ["SEPTEMBER"] = 9,
                ["OCT"] = 10,
                ["OCTOBER"] = 10,
                ["NOV"] = 11,
                ["NOVEMBER"] = 11,
                ["DEC"] = 12,
                ["DECEMBER"] = 12
            };

    private readonly string _connectionString;
    private readonly Dictionary<string, GstinProfileLookupResult>
        _sessionCache = new(StringComparer.OrdinalIgnoreCase);

    public GstinProfileLookupService()
    {
        ApplicationDataPaths.EnsureFolders();

        _connectionString =
            new SqliteConnectionStringBuilder
            {
                DataSource = ApplicationDataPaths.DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private
            }.ToString();
    }

    public void Initialize() =>
        SqliteSchemaInitializationGate.RunOnce(nameof(GstinProfileLookupService), _connectionString, InitializeCore);

    private void InitializeCore()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS GstinProfileCache
            (
                Gstin               TEXT PRIMARY KEY COLLATE NOCASE,
                LegalName           TEXT NOT NULL DEFAULT '',
                TradeName           TEXT NOT NULL DEFAULT '',
                Pan                 TEXT NOT NULL DEFAULT '',
                State               TEXT NOT NULL DEFAULT '',
                SourceType          TEXT NOT NULL DEFAULT '',
                SourcePath          TEXT NOT NULL DEFAULT '',
                SourceModifiedUtc   TEXT NOT NULL DEFAULT '',
                Gstr3bYear          TEXT NOT NULL DEFAULT '',
                Gstr3bPeriod        TEXT NOT NULL DEFAULT '',
                FilingFrequency     TEXT NOT NULL DEFAULT '',
                PeriodStartKey      TEXT NOT NULL DEFAULT '',
                PeriodEndKey        TEXT NOT NULL DEFAULT '',
                Arn                 TEXT NOT NULL DEFAULT '',
                ArnDate             TEXT NOT NULL DEFAULT '',
                UpdatedUtc          TEXT NOT NULL
            );
            """;

        command.ExecuteNonQuery();

        EnsureColumn(connection, "GstinProfileCache", "SourceType", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "GstinProfileCache", "Gstr3bYear", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "GstinProfileCache", "Gstr3bPeriod", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "GstinProfileCache", "FilingFrequency", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "GstinProfileCache", "PeriodStartKey", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "GstinProfileCache", "PeriodEndKey", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "GstinProfileCache", "Arn", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "GstinProfileCache", "ArnDate", "TEXT NOT NULL DEFAULT ''");
    }

    public void Invalidate(string gstin)
    {
        var normalized = NormalizeGstin(gstin);

        if (normalized.Length > 0)
        {
            _sessionCache.Remove(normalized);
        }
    }

    public async Task<GstinProfileLookupResult> LookupAsync(
        string gstin,
        string eReturnsRoot,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeGstin(gstin);
        var derived = BuildDerived(normalized);

        if (normalized.Length != 15)
        {
            return derived;
        }

        if (_sessionCache.TryGetValue(normalized, out var sessionResult))
        {
            return sessionResult;
        }

        var cached = ReadCached(normalized);
        var accountingProfile = ReadAccountingProfile(normalized);

        var discovered = await Task.Run(
            () => SearchSources(
                normalized,
                eReturnsRoot,
                cancellationToken),
            cancellationToken);

        var result = MergeCandidates(
            derived,
            cached,
            accountingProfile,
            discovered);

        Upsert(result);
        _sessionCache[normalized] = result;
        return result;
    }

    public async Task<GstinProfileLookupResult> LookupFolderAsync(
        string gstin,
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeGstin(gstin);
        var derived = BuildDerived(normalized);

        if (normalized.Length != 15 || string.IsNullOrWhiteSpace(folderPath))
        {
            return derived;
        }

        var discovered = await Task.Run(
            () => SearchExplicitFolder(normalized, folderPath, cancellationToken),
            cancellationToken);

        // The explicit Automation Folder is the source of truth for this lookup.
        // Keep cached/accounting data only as a fallback for a missing field so a
        // stale profile from another folder can never override a name found here.
        var cached = ReadCached(normalized);
        var accountingProfile = ReadAccountingProfile(normalized);
        var result = MergePreferred(discovered, cached, accountingProfile, derived);
        Upsert(result);
        _sessionCache[normalized] = result;
        return result;
    }


    public async Task<GstinProfileLookupResult> LookupAutomationLinkAsync(
        string gstin,
        string automationFolderPath,
        string eReturnsRoot,
        string financialYear,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeGstin(gstin);
        var derived = BuildDerived(normalized);
        if (normalized.Length != 15) return derived;

        return await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Fix15: Auto-Link ownership lookup order is deliberate and fail-closed:
                // 1) exact GSTIN directory under the configured E-returns FY tree,
                // 2) matching GSTR-3B evidence,
                // 3) not found.  The client Automation Folder itself does not have to
                // contain the portal download tree.
                var exactGstinFolder = FindExactEReturnsGstinFolder(
                    normalized,
                    eReturnsRoot,
                    financialYear,
                    cancellationToken);

                GstinProfileLookupResult primaryEvidence;
                GstinProfileLookupResult gstr3bEvidence = derived;

                if (exactGstinFolder is not null)
                {
                    var folderProfile = SearchExplicitFolder(
                        normalized,
                        exactGstinFolder.FullName,
                        cancellationToken);

                    primaryEvidence = folderProfile.SourcePath.Length > 0
                        ? folderProfile
                        : BuildExactFolderEvidence(normalized, exactGstinFolder);

                    // The exact GSTIN folder proves ownership even when its JSON/ZIP
                    // does not carry Trade/Legal Name.  Continue to GSTR-3B so the
                    // registration window can still fill Client Name automatically.
                    if (primaryEvidence.TradeName.Length == 0 ||
                        primaryEvidence.LegalName.Length == 0)
                    {
                        gstr3bEvidence = SearchAutomationLinkGstr3b(
                            normalized,
                            automationFolderPath,
                            eReturnsRoot,
                            cancellationToken);
                    }
                }
                else
                {
                    gstr3bEvidence = SearchAutomationLinkGstr3b(
                        normalized,
                        automationFolderPath,
                        eReturnsRoot,
                        cancellationToken);

                    if (gstr3bEvidence.SourcePath.Length == 0)
                    {
                        // Do not let an old cache/accounting profile turn a failed
                        // ownership lookup into a false positive.
                        return derived;
                    }

                    primaryEvidence = gstr3bEvidence;
                }

                var cached = ReadCached(normalized);
                var accountingProfile = ReadAccountingProfile(normalized);
                var preferredEvidence = ProfileScore(gstr3bEvidence) > ProfileScore(primaryEvidence)
                    ? gstr3bEvidence
                    : primaryEvidence;
                var result = MergePreferred(
                    preferredEvidence,
                    primaryEvidence,
                    gstr3bEvidence,
                    cached,
                    accountingProfile,
                    derived);

                Upsert(result);
                _sessionCache[normalized] = result;
                return result;
            },
            cancellationToken);
    }


    private static DirectoryInfo? FindExactEReturnsGstinFolder(
        string gstin,
        string eReturnsRoot,
        string financialYear,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(eReturnsRoot)) return null;

        DirectoryInfo root;
        try { root = new DirectoryInfo(eReturnsRoot.Trim()); }
        catch { return null; }
        if (!root.Exists) return null;

        var yearNames = BuildFinancialYearDirectoryNames(financialYear);
        foreach (var yearName in yearNames)
        {
            foreach (var relative in new[]
                     {
                         Path.Combine(yearName, "downloaded", gstin),
                         Path.Combine(yearName, "Downloaded", gstin),
                         Path.Combine(yearName, gstin)
                     })
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var candidate = new DirectoryInfo(Path.Combine(root.FullName, relative));
                    if (candidate.Exists &&
                        candidate.Name.Equals(gstin, StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
                catch
                {
                    // Continue with the safe recursive exact-name fallback.
                }
            }
        }

        var pending = new Stack<DirectoryInfo>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(root);
        var matches = new List<DirectoryInfo>();
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            if (!visited.Add(current.FullName)) continue;

            DirectoryInfo[] directories;
            try { directories = current.GetDirectories(); }
            catch { continue; }

            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (directory.Name.Equals(gstin, StringComparison.OrdinalIgnoreCase) &&
                    PathMatchesFinancialYear(directory.FullName, yearNames))
                {
                    matches.Add(directory);
                    continue;
                }

                try
                {
                    if ((directory.Attributes & FileAttributes.ReparsePoint) == 0)
                        pending.Push(directory);
                }
                catch
                {
                    // Ignore inaccessible/reparse directories.
                }
            }
        }

        return matches
            .OrderByDescending(directory =>
                directory.Parent?.Name.Equals("downloaded", StringComparison.OrdinalIgnoreCase) == true)
            .ThenByDescending(directory => directory.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static IReadOnlyList<string> BuildFinancialYearDirectoryNames(string financialYear)
    {
        var text = (financialYear ?? string.Empty).Trim();
        var match = Regex.Match(text, @"^(?<start>\d{4})\s*[-_/]\s*(?<end>\d{2}|\d{4})$");
        if (!match.Success || !int.TryParse(match.Groups["start"].Value, out var start))
            return text.Length > 0 ? new[] { text } : Array.Empty<string>();

        var end = start + 1;
        return new[]
        {
            $"{start:0000}-{end:0000}",
            $"{start:0000}-{end % 100:00}",
            $"{start:0000}_{end:0000}",
            $"{start:0000}_{end % 100:00}"
        };
    }

    private static bool PathMatchesFinancialYear(
        string path,
        IReadOnlyList<string> yearNames)
    {
        if (yearNames.Count == 0) return true;
        var segments = path.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment =>
            yearNames.Any(year => segment.Equals(year, StringComparison.OrdinalIgnoreCase)));
    }

    private static GstinProfileLookupResult BuildExactFolderEvidence(
        string gstin,
        DirectoryInfo folder)
    {
        var derived = BuildDerived(gstin);
        DateTime modifiedUtc;
        try { modifiedUtc = folder.LastWriteTimeUtc; }
        catch { modifiedUtc = DateTime.MinValue; }

        return new GstinProfileLookupResult
        {
            Gstin = gstin,
            Pan = derived.Pan,
            State = derived.State,
            SourceType = "E-returns GSTIN Folder",
            SourcePath = folder.FullName,
            SourceModifiedUtc = modifiedUtc
        };
    }

    private GstinProfileLookupResult SearchAutomationLinkGstr3b(
        string gstin,
        string automationFolderPath,
        string eReturnsRoot,
        CancellationToken cancellationToken)
    {
        var derived = BuildDerived(gstin);
        var best = derived;
        var bestModifiedUtc = DateTime.MinValue;
        var roots = new List<DirectoryInfo>();
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRoot(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var root = new DirectoryInfo(path.Trim());
                if (root.Exists && seenRoots.Add(root.FullName)) roots.Add(root);
            }
            catch { }
        }

        // GSTR-3B fallback can be in the selected Automation Folder, the configured
        // E-returns tree, or the user's Downloads folder (normal portal download path).
        AddRoot(GetDownloadsDirectory().FullName);
        AddRoot(automationFolderPath);
        AddRoot(eReturnsRoot);

        var normalizedGstin = NormalizeFileName(gstin);
        var processed = 0;
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var files = EnumerateFilesSafe(
                    root,
                    file => file.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
                            NormalizeFileName(file.Name).Contains("GSTR3B", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file =>
                    NormalizeFileName(file.Name).Contains(normalizedGstin, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(GetLastWriteUtc)
                .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seenFiles.Add(file.FullName)) continue;
                if (++processed > MaximumSourceFiles) return best;

                var candidate = FindGstr3bProfile(file, gstin);
                if (candidate.SourcePath.Length == 0) continue;
                best = ChooseBetter(best, candidate, ref bestModifiedUtc);
                if (best.TradeName.Length > 0 && best.LegalName.Length > 0) return best;
            }
        }

        return best;
    }

    private GstinProfileLookupResult SearchExplicitFolder(
        string gstin,
        string folderPath,
        CancellationToken cancellationToken)
    {
        var derived = BuildDerived(gstin);
        DirectoryInfo root;
        try
        {
            root = new DirectoryInfo(folderPath.Trim());
        }
        catch
        {
            return derived;
        }

        if (!root.Exists) return derived;

        // Fix14: the selected Automation Folder itself is the authority for link validation.
        // An exact GSTIN hit is evidence even when a return JSON does not carry the
        // taxpayer Trade/Legal Name. Also inspect GSTR-3B PDFs in this folder because
        // many users keep the 3B PDF beside JSON/ZIP GST returns.
        var bestJson = derived;
        var bestJsonModifiedUtc = DateTime.MinValue;
        var bestGstr3b = derived;
        var bestGstr3bModifiedUtc = DateTime.MinValue;
        var processedJson = 0;
        foreach (var file in EnumerateFilesSafe(
                     root,
                     file => file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                             file.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                 .OrderByDescending(GetLastWriteUtc)
                 .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++processedJson > MaximumSourceFiles) break;

            var modifiedUtc = GetLastWriteUtc(file);
            foreach (var payload in ReadJsonPayloads(file))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = FindJsonProfile(payload, gstin, file.FullName);
                if (candidate.SourcePath.Length == 0) continue;

                candidate = CopyWithSourceModified(candidate, modifiedUtc);
                bestJson = ChooseBetter(bestJson, candidate, ref bestJsonModifiedUtc);
            }
        }

        var normalizedGstin = NormalizeFileName(gstin);
        var processedPdf = 0;
        foreach (var file in EnumerateFilesSafe(
                     root,
                     file =>
                         file.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
                         (NormalizeFileName(file.Name).Contains("GSTR3B", StringComparison.OrdinalIgnoreCase) ||
                          NormalizeFileName(file.Name).Contains(normalizedGstin, StringComparison.OrdinalIgnoreCase)))
                 .OrderByDescending(file =>
                     NormalizeFileName(file.Name).Contains(normalizedGstin, StringComparison.OrdinalIgnoreCase))
                 .ThenByDescending(GetLastWriteUtc)
                 .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++processedPdf > MaximumSourceFiles) break;

            var candidate = FindGstr3bProfile(file, gstin);
            if (candidate.SourcePath.Length == 0) continue;

            bestGstr3b = ChooseBetter(bestGstr3b, candidate, ref bestGstr3bModifiedUtc);
        }

        return MergeCandidates(bestJson, bestGstr3b, derived);
    }

    private static GstinProfileLookupResult MergePreferred(
        GstinProfileLookupResult preferred,
        params GstinProfileLookupResult?[] fallbacks)
    {
        string Pick(Func<GstinProfileLookupResult, string> selector)
        {
            var preferredValue = selector(preferred);
            if (!string.IsNullOrWhiteSpace(preferredValue)) return preferredValue;
            return fallbacks
                       .Where(candidate => candidate is not null)
                       .Cast<GstinProfileLookupResult>()
                       .Select(selector)
                       .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                   ?? string.Empty;
        }

        // LookupFolderAsync must never make cached/accounting evidence look as if it
        // came from the selected Automation Folder. Keep source identity explicit.
        var source = preferred;
        return new GstinProfileLookupResult
        {
            Gstin = Pick(candidate => candidate.Gstin),
            LegalName = Pick(candidate => candidate.LegalName),
            TradeName = Pick(candidate => candidate.TradeName),
            Pan = Pick(candidate => candidate.Pan),
            State = Pick(candidate => candidate.State),
            SourceType = source.SourceType,
            SourcePath = source.SourcePath,
            SourceModifiedUtc = source.SourceModifiedUtc,
            Gstr3bYear = Pick(candidate => candidate.Gstr3bYear),
            Gstr3bPeriod = Pick(candidate => candidate.Gstr3bPeriod),
            FilingFrequency = Pick(candidate => candidate.FilingFrequency),
            PeriodStartKey = Pick(candidate => candidate.PeriodStartKey),
            PeriodEndKey = Pick(candidate => candidate.PeriodEndKey),
            Arn = Pick(candidate => candidate.Arn),
            ArnDate = Pick(candidate => candidate.ArnDate)
        };
    }

    private GstinProfileLookupResult SearchSources(
        string gstin,
        string eReturnsRoot,
        CancellationToken cancellationToken)
    {
        var derived = BuildDerived(gstin);
        var bestJson = derived;
        var bestJsonModifiedUtc = DateTime.MinValue;
        var bestGstr3b = derived;
        var bestGstr3bModifiedUtc = DateTime.MinValue;
        var processedPdfFiles = 0;

        // GSTR-3B is the authoritative client-profile source. Check it first
        // so a normal startup does not traverse hundreds of GST JSON files
        // when the PDF already supplies both names.
        foreach (var file in EnumerateGstr3bPdfs(gstin))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (++processedPdfFiles > MaximumSourceFiles)
            {
                break;
            }

            var candidate = FindGstr3bProfile(file, gstin);

            if (!candidate.WasFound && !candidate.HasGstr3bMetadata)
            {
                continue;
            }

            bestGstr3b = ChooseBetter(
                bestGstr3b,
                candidate,
                ref bestGstr3bModifiedUtc);

            if (bestGstr3b.HasGstr3bMetadata &&
                bestGstr3b.LegalName.Length > 0 &&
                bestGstr3b.TradeName.Length > 0)
            {
                break;
            }
        }

        if (bestGstr3b.LegalName.Length == 0 ||
            bestGstr3b.TradeName.Length == 0)
        {
            var processedJsonFiles = 0;

            foreach (var file in EnumerateJsonCandidates(gstin, eReturnsRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (++processedJsonFiles > MaximumSourceFiles)
                {
                    break;
                }

                var modifiedUtc = GetLastWriteUtc(file);

                foreach (var payload in ReadJsonPayloads(file))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var candidate = FindJsonProfile(
                        payload,
                        gstin,
                        file.FullName);

                    if (!candidate.WasFound)
                    {
                        continue;
                    }

                    candidate = CopyWithSourceModified(candidate, modifiedUtc);
                    bestJson = ChooseBetter(
                        bestJson,
                        candidate,
                        ref bestJsonModifiedUtc);
                }
            }
        }

        return MergeCandidates(
            derived,
            bestJson,
            bestGstr3b);
    }

    private static GstinProfileLookupResult MergeCandidates(
        params GstinProfileLookupResult?[] candidates)
    {
        var ordered = candidates
            .Where(candidate => candidate is not null)
            .Cast<GstinProfileLookupResult>()
            .OrderByDescending(ProfileScore)
            .ThenByDescending(candidate => candidate.SourceModifiedUtc)
            .ToList();

        var primary = ordered.FirstOrDefault()
            ?? new GstinProfileLookupResult();

        string FirstValue(Func<GstinProfileLookupResult, string> selector) =>
            ordered
                .Select(selector)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? string.Empty;

        return new GstinProfileLookupResult
        {
            Gstin = FirstValue(candidate => candidate.Gstin),
            LegalName = FirstValue(candidate => candidate.LegalName),
            TradeName = FirstValue(candidate => candidate.TradeName),
            Pan = FirstValue(candidate => candidate.Pan),
            State = FirstValue(candidate => candidate.State),
            SourceType = primary.SourceType,
            SourcePath = primary.SourcePath,
            SourceModifiedUtc = primary.SourceModifiedUtc,
            Gstr3bYear = FirstValue(candidate => candidate.Gstr3bYear),
            Gstr3bPeriod = FirstValue(candidate => candidate.Gstr3bPeriod),
            FilingFrequency = FirstValue(candidate => candidate.FilingFrequency),
            PeriodStartKey = FirstValue(candidate => candidate.PeriodStartKey),
            PeriodEndKey = FirstValue(candidate => candidate.PeriodEndKey),
            Arn = FirstValue(candidate => candidate.Arn),
            ArnDate = FirstValue(candidate => candidate.ArnDate)
        };
    }

    private static GstinProfileLookupResult ChooseBetter(
        GstinProfileLookupResult current,
        GstinProfileLookupResult candidate,
        ref DateTime currentModifiedUtc)
    {
        var candidateScore = ProfileScore(candidate);
        var currentScore = ProfileScore(current);

        if (candidateScore > currentScore ||
            (candidateScore == currentScore &&
             candidate.SourceModifiedUtc >= currentModifiedUtc))
        {
            currentModifiedUtc = candidate.SourceModifiedUtc;
            return candidate;
        }

        return current;
    }

    private static IEnumerable<FileInfo> EnumerateJsonCandidates(
        string gstin,
        string eReturnsRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in ResolveJsonSearchRoots(gstin, eReturnsRoot))
        {
            IEnumerable<FileInfo> files;

            files = EnumerateFilesSafe(
                    root,
                    file =>
                        file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                        file.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(GetLastWriteUtc)
                .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                if (seen.Add(file.FullName))
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<DirectoryInfo> ResolveJsonSearchRoots(
        string gstin,
        string eReturnsRoot)
    {
        var roots = new List<DirectoryInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(eReturnsRoot))
        {
            var root = new DirectoryInfo(eReturnsRoot.Trim());

            if (root.Exists)
            {
                try
                {
                    foreach (var directory in root.EnumerateDirectories(
                                 gstin,
                                 SearchOption.AllDirectories))
                    {
                        if (seen.Add(directory.FullName))
                        {
                            roots.Add(directory);
                        }
                    }
                }
                catch
                {
                    // Continue with Downloads and already discovered folders.
                }
            }
        }

        var downloads = GetDownloadsDirectory();

        if (downloads.Exists && seen.Add(downloads.FullName))
        {
            roots.Add(downloads);
        }

        return roots;
    }

    private static IEnumerable<FileInfo> EnumerateGstr3bPdfs(
        string targetGstin)
    {
        var downloads = GetDownloadsDirectory();

        if (!downloads.Exists)
        {
            yield break;
        }

        IEnumerable<FileInfo> files;

        files = EnumerateFilesSafe(
                downloads,
                file =>
                    file.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
                    NormalizeFileName(file.Name).Contains(
                        "GSTR3B",
                        StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file =>
                NormalizeFileName(file.Name).Contains(
                    targetGstin,
                    StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(GetLastWriteUtc)
            .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            yield return file;
        }
    }

    private static IEnumerable<FileInfo> EnumerateFilesSafe(
        DirectoryInfo root,
        Func<FileInfo, bool> predicate)
    {
        var pending = new Stack<DirectoryInfo>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            if (!visited.Add(current.FullName))
            {
                continue;
            }

            FileInfo[] files;
            DirectoryInfo[] directories;

            try
            {
                files = current.GetFiles();
            }
            catch
            {
                files = [];
            }

            foreach (var file in files)
            {
                bool include;

                try
                {
                    include = predicate(file);
                }
                catch
                {
                    include = false;
                }

                if (include)
                {
                    yield return file;
                }
            }

            try
            {
                directories = current.GetDirectories();
            }
            catch
            {
                directories = [];
            }

            foreach (var directory in directories)
            {
                try
                {
                    if ((directory.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(directory);
                    }
                }
                catch
                {
                    // Ignore directories whose attributes cannot be read.
                }
            }
        }
    }

    private static DirectoryInfo GetDownloadsDirectory() =>
        new(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads"));

    private static GstinProfileLookupResult FindGstr3bProfile(
        FileInfo file,
        string targetGstin)
    {
        var text = ReadPdfText(file.FullName);

        if (text.Length == 0)
        {
            return BuildDerived(targetGstin);
        }

        var gstin = NormalizeGstin(
            MatchValue(
                text,
                @"GSTIN\s+of\s+the\s+supplier\s+([0-9A-Z]{15})"));

        if (gstin.Length == 0 &&
            NormalizeFileName(file.Name).Contains(targetGstin, StringComparison.OrdinalIgnoreCase))
        {
            gstin = targetGstin;
        }

        if (!gstin.Equals(targetGstin, StringComparison.OrdinalIgnoreCase))
        {
            return BuildDerived(targetGstin);
        }

        var legalName = CleanCapturedName(
            MatchValue(
                text,
                @"2\s*\(\s*a\s*\)\s*\.?\s*Legal\s+name\s+of\s+the\s+registered\s+person\s+(.+?)(?=2\s*\(\s*b\s*\)|Trade\s+name|2\s*\(\s*c\s*\)|\bARN\b)"));

        var tradeName = CleanCapturedName(
            MatchValue(
                text,
                @"2\s*\(\s*b\s*\)\s*\.?\s*Trade\s+name\s*,?\s*if\s+any\s+(.+?)(?=2\s*\(\s*c\s*\)|\bARN\b|2\s*\(\s*d\s*\))"));

        var year = MatchValue(text, @"\bYear\s+([0-9]{4}\s*-\s*[0-9]{2})");
        var period = MatchValue(
            text,
            @"\bPeriod\s+([A-Za-z]+(?:\s*[-–—/]\s*[A-Za-z]+)?)");
        var periodInfo = BuildPeriodInfo(year, period);
        var derived = BuildDerived(targetGstin);

        return new GstinProfileLookupResult
        {
            Gstin = targetGstin,
            LegalName = legalName,
            TradeName = tradeName,
            Pan = derived.Pan,
            State = derived.State,
            SourceType = "GSTR-3B PDF",
            SourcePath = file.FullName,
            SourceModifiedUtc = GetLastWriteUtc(file),
            Gstr3bYear = NormalizeYear(year),
            Gstr3bPeriod = NormalizePeriodLabel(period),
            FilingFrequency = periodInfo.FilingFrequency,
            PeriodStartKey = periodInfo.StartKey,
            PeriodEndKey = periodInfo.EndKey,
            Arn = MatchValue(
                text,
                @"2\s*\(\s*c\s*\)\s*\.?\s*ARN\s+([A-Z0-9]+)"),
            ArnDate = MatchValue(
                text,
                @"2\s*\(\s*d\s*\)\s*\.?\s*Date\s+of\s+ARN\s+([0-9./-]+)")
        };
    }

    private static string ReadPdfText(string path)
    {
        try
        {
            using var document = PdfDocument.Open(path);
            var pages = new List<string>();

            foreach (var page in document.GetPages())
            {
                var text = ContentOrderTextExtractor.GetText(page);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    pages.Add(text);
                }
            }

            return string.Join("\n", pages);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string MatchValue(string text, string pattern)
    {
        var match = Regex.Match(
            text,
            pattern,
            RegexOptions.IgnoreCase |
            RegexOptions.Singleline |
            RegexOptions.CultureInvariant);

        return match.Success
            ? NormalizeWhitespace(match.Groups[1].Value)
            : string.Empty;
    }

    private static string CleanCapturedName(string value)
    {
        var cleaned = NormalizeWhitespace(value);
        cleaned = Regex.Replace(
            cleaned,
            @"\s+(?:2\s*\(|ARN\b|Date\s+of\s+ARN\b).*$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return cleaned.Trim(' ', ':', '-', '.');
    }

    private static (string FilingFrequency, string StartKey, string EndKey)
        BuildPeriodInfo(string yearValue, string periodValue)
    {
        var year = NormalizeYear(yearValue);
        var match = Regex.Match(year, @"^(\d{4})-(\d{2})$");

        if (!match.Success)
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        var startYear = int.Parse(match.Groups[1].Value);
        var endYear = 2000 + int.Parse(match.Groups[2].Value);
        var normalized = NormalizePeriodLabel(periodValue)
            .Replace('–', '-')
            .Replace('—', '-')
            .Replace('/', '-');
        var parts = normalized
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 2)
        {
            var startMonth = MonthNumber(parts[0]);
            var endMonth = MonthNumber(parts[1]);
            var validQuarter =
                (startMonth, endMonth) is
                    (4, 6) or
                    (7, 9) or
                    (10, 12) or
                    (1, 3);

            if (validQuarter)
            {
                var startKey = $"{(startMonth >= 4 ? startYear : endYear):0000}{startMonth:00}";
                var endKey = $"{(endMonth >= 4 ? startYear : endYear):0000}{endMonth:00}";
                return ("Quarterly", startKey, endKey);
            }
        }

        var month = MonthNumber(parts.Length > 0 ? parts[0] : normalized);

        if (month == 0)
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        var calendarYear = month >= 4 ? startYear : endYear;
        var key = $"{calendarYear:0000}{month:00}";
        return ("Monthly", key, key);
    }

    private static int MonthNumber(string value)
    {
        var normalized = NormalizeWhitespace(value).ToUpperInvariant();

        if (MonthNumbers.TryGetValue(normalized, out var direct))
        {
            return direct;
        }

        return normalized.Length >= 3 &&
               MonthNumbers.TryGetValue(normalized[..3], out var shortValue)
            ? shortValue
            : 0;
    }

    private static string NormalizeYear(string value) =>
        Regex.Replace(NormalizeWhitespace(value), @"\s+", string.Empty);

    private static string NormalizePeriodLabel(string value) =>
        NormalizeWhitespace(value)
            .Replace(" / ", "-")
            .Replace("–", "-")
            .Replace("—", "-");

    private static string NormalizeWhitespace(string value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static string NormalizeFileName(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static IReadOnlyList<JsonNode> ReadJsonPayloads(FileInfo file)
    {
        var payloads = new List<JsonNode>();

        if (file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(file.FullName));

                if (node is not null)
                {
                    payloads.Add(node);
                }
            }
            catch
            {
                // Ignore invalid JSON files.
            }

            return payloads;
        }

        if (!file.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return payloads;
        }

        try
        {
            using var archive = ZipFile.OpenRead(file.FullName);

            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    using var stream = entry.Open();
                    var node = JsonNode.Parse(stream);

                    if (node is not null)
                    {
                        payloads.Add(node);
                    }
                }
                catch
                {
                    // Ignore invalid ZIP members.
                }
            }
        }
        catch
        {
            // Ignore invalid ZIP files.
        }

        return payloads;
    }

    private static GstinProfileLookupResult FindJsonProfile(
        JsonNode node,
        string targetGstin,
        string sourcePath)
    {
        var legalName = string.Empty;
        var tradeName = string.Empty;
        var exactGstinMatched = false;

        VisitNode(node, targetGstin, ref legalName, ref tradeName, ref exactGstinMatched);
        var derived = BuildDerived(targetGstin);

        return new GstinProfileLookupResult
        {
            Gstin = targetGstin,
            LegalName = legalName,
            TradeName = tradeName,
            Pan = derived.Pan,
            State = derived.State,
            SourceType = exactGstinMatched ? "GST JSON/ZIP" : string.Empty,
            SourcePath = exactGstinMatched ? sourcePath : string.Empty
        };
    }

    private static void VisitNode(
        JsonNode? node,
        string targetGstin,
        ref string legalName,
        ref string tradeName,
        ref bool exactGstinMatched)
    {
        if (node is JsonObject obj)
        {
            // For Automation Folder ownership, prefer taxpayer/recipient GSTIN keys.
            // Do not treat a supplier/customer CTIN that merely appears inside a return
            // as proof that the folder belongs to that counterparty.
            var ownerGstin = NormalizeGstin(
                FirstText(
                    obj,
                    "gstin",
                    "gstinNo",
                    "gstin_no",
                    "gstinNumber",
                    "gstin_number",
                    "recipientGstin",
                    "recipient_gstin"));
            var counterpartyGstin = NormalizeGstin(
                FirstText(obj, "ctin", "supplierGstin", "supplier_gstin"));
            var candidateGstin = ownerGstin.Length > 0 ? ownerGstin : counterpartyGstin;

            if (candidateGstin == targetGstin)
            {
                if (ownerGstin == targetGstin) exactGstinMatched = true;
                var legal = FirstText(
                    obj,
                    "lgnm",
                    "legalName",
                    "legal_name",
                    "legalNm",
                    "legal_name_of_registered_person",
                    "legal_name_of_taxpayer");

                var trade = FirstText(
                    obj,
                    "trdnm",
                    "tradeName",
                    "trade_name",
                    "tradeNm",
                    "trade_name_of_registered_person",
                    "trade_name_of_taxpayer");

                if (legal.Length > 0)
                {
                    legalName = legal;
                }

                if (trade.Length > 0)
                {
                    tradeName = trade;
                }
            }

            foreach (var child in obj)
            {
                VisitNode(child.Value, targetGstin, ref legalName, ref tradeName, ref exactGstinMatched);
            }

            return;
        }

        if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                VisitNode(child, targetGstin, ref legalName, ref tradeName, ref exactGstinMatched);
            }
        }
    }

    private static string FirstText(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            JsonNode? node = null;
            if (!obj.TryGetPropertyValue(key, out node) || node is null)
            {
                node = obj
                    .FirstOrDefault(property => property.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    .Value;
            }

            if (node is null) continue;

            var value = node.ToString().Trim();

            if (value.Length > 0)
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static GstinProfileLookupResult BuildDerived(string gstin)
    {
        var normalized = NormalizeGstin(gstin);
        var pan = normalized.Length == 15
            ? normalized.Substring(2, 10)
            : string.Empty;

        var state = normalized.Length >= 2 &&
                    StateByCode.TryGetValue(normalized[..2], out var stateName)
            ? stateName
            : string.Empty;

        return new GstinProfileLookupResult
        {
            Gstin = normalized,
            Pan = pan,
            State = state
        };
    }

    private GstinProfileLookupResult? ReadAccountingProfile(string gstin)
    {
        try
        {
            using var connection = OpenConnection();
            var candidates = new List<(GstinProfileLookupResult Profile, DateTime UpdatedUtc)>();

            ReadAccountingCandidate(
                connection,
                "PurchaseRecords",
                "Source2APath",
                gstin,
                candidates);

            ReadAccountingCandidate(
                connection,
                "SalesRecords",
                "SourcePath",
                gstin,
                candidates);

            return candidates
                .OrderByDescending(candidate => candidate.UpdatedUtc)
                .Select(candidate => candidate.Profile)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static void ReadAccountingCandidate(
        SqliteConnection connection,
        string tableName,
        string sourceColumn,
        string gstin,
        ICollection<(GstinProfileLookupResult Profile, DateTime UpdatedUtc)> candidates)
    {
        using var tableCheck = connection.CreateCommand();
        tableCheck.CommandText =
            """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name = $TableName;
            """;
        tableCheck.Parameters.AddWithValue("$TableName", tableName);

        if (Convert.ToInt32(tableCheck.ExecuteScalar()) == 0)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                CompanyName,
                {sourceColumn},
                CASE
                    WHEN UpdatedUtc <> '' THEN UpdatedUtc
                    ELSE CreatedUtc
                END
            FROM {tableName}
            WHERE
                ClientGstin = $Gstin
                AND TRIM(CompanyName) <> ''
            ORDER BY
                CASE
                    WHEN UpdatedUtc <> '' THEN UpdatedUtc
                    ELSE CreatedUtc
                END DESC,
                Id DESC
            LIMIT 30;
            """;
        command.Parameters.AddWithValue("$Gstin", gstin);

        using var reader = command.ExecuteReader();

        // Legacy imports may have stored the raw GSTIN in CompanyName.
        // Those rows are not name evidence. Inspect older source-backed rows
        // instead of returning the first placeholder and poisoning the cache.
        while (reader.Read())
        {
            var sourceName = reader.GetString(0);
            if (!GstClientNameRecognitionPolicy.HasSourceBusinessName(sourceName, gstin))
                continue;
            var derived = BuildDerived(gstin);
            var updatedUtc = DateTime.TryParse(reader.GetString(2), out var parsed)
                ? parsed
                : DateTime.MinValue;

            candidates.Add((
                new GstinProfileLookupResult
                {
                    Gstin = gstin,
                    TradeName = sourceName,
                    Pan = derived.Pan,
                    State = derived.State,
                    SourceType = "Saved accounting data",
                    SourcePath = reader.GetString(1),
                    SourceModifiedUtc = updatedUtc
                },
                updatedUtc));
            break;
        }
    }

    private GstinProfileLookupResult? ReadCached(string gstin)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
                LegalName,
                TradeName,
                Pan,
                State,
                SourceType,
                SourcePath,
                SourceModifiedUtc,
                Gstr3bYear,
                Gstr3bPeriod,
                FilingFrequency,
                PeriodStartKey,
                PeriodEndKey,
                Arn,
                ArnDate
            FROM GstinProfileCache
            WHERE Gstin = $Gstin;
            """;

        command.Parameters.AddWithValue("$Gstin", gstin);

        using var reader = command.ExecuteReader();

        if (!reader.Read())
        {
            return null;
        }

        return new GstinProfileLookupResult
        {
            Gstin = gstin,
            LegalName = reader.GetString(0),
            TradeName = reader.GetString(1),
            Pan = reader.GetString(2),
            State = reader.GetString(3),
            SourceType = reader.GetString(4),
            SourcePath = reader.GetString(5),
            SourceModifiedUtc = ParseUtc(reader.GetString(6)),
            Gstr3bYear = reader.GetString(7),
            Gstr3bPeriod = reader.GetString(8),
            FilingFrequency = reader.GetString(9),
            PeriodStartKey = reader.GetString(10),
            PeriodEndKey = reader.GetString(11),
            Arn = reader.GetString(12),
            ArnDate = reader.GetString(13)
        };
    }

    private void Upsert(GstinProfileLookupResult result)
    {
        if (result.Gstin.Length != 15)
        {
            return;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO GstinProfileCache
            (
                Gstin,
                LegalName,
                TradeName,
                Pan,
                State,
                SourceType,
                SourcePath,
                SourceModifiedUtc,
                Gstr3bYear,
                Gstr3bPeriod,
                FilingFrequency,
                PeriodStartKey,
                PeriodEndKey,
                Arn,
                ArnDate,
                UpdatedUtc
            )
            VALUES
            (
                $Gstin,
                $LegalName,
                $TradeName,
                $Pan,
                $State,
                $SourceType,
                $SourcePath,
                $SourceModifiedUtc,
                $Gstr3bYear,
                $Gstr3bPeriod,
                $FilingFrequency,
                $PeriodStartKey,
                $PeriodEndKey,
                $Arn,
                $ArnDate,
                $UpdatedUtc
            )
            ON CONFLICT(Gstin)
            DO UPDATE SET
                LegalName = excluded.LegalName,
                TradeName = excluded.TradeName,
                Pan = excluded.Pan,
                State = excluded.State,
                SourceType = excluded.SourceType,
                SourcePath = excluded.SourcePath,
                SourceModifiedUtc = excluded.SourceModifiedUtc,
                Gstr3bYear = excluded.Gstr3bYear,
                Gstr3bPeriod = excluded.Gstr3bPeriod,
                FilingFrequency = excluded.FilingFrequency,
                PeriodStartKey = excluded.PeriodStartKey,
                PeriodEndKey = excluded.PeriodEndKey,
                Arn = excluded.Arn,
                ArnDate = excluded.ArnDate,
                UpdatedUtc = excluded.UpdatedUtc;
            """;

        command.Parameters.AddWithValue("$Gstin", result.Gstin);
        command.Parameters.AddWithValue("$LegalName", result.LegalName);
        command.Parameters.AddWithValue("$TradeName", result.TradeName);
        command.Parameters.AddWithValue("$Pan", result.Pan);
        command.Parameters.AddWithValue("$State", result.State);
        command.Parameters.AddWithValue("$SourceType", result.SourceType);
        command.Parameters.AddWithValue("$SourcePath", result.SourcePath);
        command.Parameters.AddWithValue(
            "$SourceModifiedUtc",
            result.SourceModifiedUtc == DateTime.MinValue
                ? string.Empty
                : result.SourceModifiedUtc.ToString("O"));
        command.Parameters.AddWithValue("$Gstr3bYear", result.Gstr3bYear);
        command.Parameters.AddWithValue("$Gstr3bPeriod", result.Gstr3bPeriod);
        command.Parameters.AddWithValue("$FilingFrequency", result.FilingFrequency);
        command.Parameters.AddWithValue("$PeriodStartKey", result.PeriodStartKey);
        command.Parameters.AddWithValue("$PeriodEndKey", result.PeriodEndKey);
        command.Parameters.AddWithValue("$Arn", result.Arn);
        command.Parameters.AddWithValue("$ArnDate", result.ArnDate);
        command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));

        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        return SqliteConnectionPolicy.Open(_connectionString);
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string definition)
    {
        SqliteSchemaColumnCache.EnsureColumn(connection, tableName, columnName, definition);
    }

    private static DateTime GetLastWriteUtc(FileInfo file)
    {
        try
        {
            return file.LastWriteTimeUtc;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static GstinProfileLookupResult CopyWithSourceModified(
        GstinProfileLookupResult source,
        DateTime modifiedUtc) =>
        new()
        {
            Gstin = source.Gstin,
            LegalName = source.LegalName,
            TradeName = source.TradeName,
            Pan = source.Pan,
            State = source.State,
            SourceType = source.SourceType,
            SourcePath = source.SourcePath,
            SourceModifiedUtc = modifiedUtc,
            Gstr3bYear = source.Gstr3bYear,
            Gstr3bPeriod = source.Gstr3bPeriod,
            FilingFrequency = source.FilingFrequency,
            PeriodStartKey = source.PeriodStartKey,
            PeriodEndKey = source.PeriodEndKey,
            Arn = source.Arn,
            ArnDate = source.ArnDate
        };

    private static int ProfileScore(GstinProfileLookupResult result) =>
        (result.SourceType.Equals("GSTR-3B PDF", StringComparison.OrdinalIgnoreCase) ? 20 : 0) +
        (result.LegalName.Length > 0 ? 4 : 0) +
        (result.TradeName.Length > 0 ? 2 : 0) +
        (result.SourcePath.Length > 0 ? 1 : 0);

    private static string NormalizeGstin(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static DateTime ParseUtc(string value) =>
        DateTime.TryParse(value, out var parsed)
            ? parsed
            : DateTime.MinValue;
}
