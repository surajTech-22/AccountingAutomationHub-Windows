using System.IO;
using System.Security.Cryptography;
using System.Text;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Gst;

namespace AccountingAutomationHub.Infrastructure.Gst;

public sealed class GstPortalAutoImportService(
    SqliteGstRepository repository,
    Gstr3bPdfImportService gstr3bImportService,
    GstLedgerImportService ledgerImportService)
{
    private readonly object _discoveryGate = new();
    private string _cachedDiscoveryRootKey = string.Empty;
    private DateTime _cachedDiscoveryUtc = DateTime.MinValue;
    private PortalDiscoverySnapshot? _cachedDiscovery;
    // Safety-bar cache below the existing whole-discovery cache. Different clients have
    // different linked Automation Folders, but they share Downloads/e-Returns. Reusing each
    // common root for the same two-second maintenance burst prevents N clients from walking
    // the same tree N times without changing which roots/files are authoritative.
    private readonly Dictionary<string, RootDiscoveryCacheEntry> _cachedRootDiscoveries =
        new(StringComparer.OrdinalIgnoreCase);

    public bool HasPotentialChanges(
        ClientProfile client,
        string preferredRoot,
        IReadOnlyList<string>? additionalRoots = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        return EnumerateRegistrationClients(client)
            .Any(registration => HasPotentialChangesForRegistration(
                registration, preferredRoot, additionalRoots));
    }

    private bool HasPotentialChangesForRegistration(
        ClientProfile client, string preferredRoot, IReadOnlyList<string>? additionalRoots)
    {
        // FIXED41 R8 Repair 4 safety bar: the maintenance probe and the real import must
        // fingerprint the SAME authority roots. ImportAvailable includes the linked
        // Automation Folder so moved GST evidence remains durable; comparing that saved
        // fingerprint against only Downloads/e-Returns made every 20-second maintenance
        // pass look like a new change. This does not reduce discovery scope or cadence - it
        // only prevents an unchanged root-set mismatch from restarting the full bootstrap.
        var discovery = GetDiscoverySnapshot(
            preferredRoot,
            forceRefresh: false,
            additionalRoots: additionalRoots);
        var saved = repository.GetBootstrapState(client.Id, client.FinancialYear, Normalize(client.Gstin));
        return saved is null ||
               !saved.DiscoveryFingerprint.Equals(discovery.Fingerprint, StringComparison.Ordinal);
    }

    public GstPortalAutoImportResult ImportAvailable(
        ClientProfile client,
        string preferredRoot,
        bool force = false,
        bool refreshDiscovery = false,
        IReadOnlyList<string>? additionalRoots = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var results = EnumerateRegistrationClients(client)
            .Select(registration => ImportAvailableForRegistration(
                registration, preferredRoot, force, refreshDiscovery, additionalRoots))
            .ToArray();
        return new GstPortalAutoImportResult(
            results.Max(item => item.SearchRoots),
            results.Sum(item => item.Gstr3bPeriods),
            results.Sum(item => item.CreditLedgerPeriods),
            results.Sum(item => item.CashLedgerPeriods),
            results.SelectMany(item => item.Warnings).ToArray(),
            results.All(item => item.CacheHit),
            results.Max(item => item.CandidateFiles));
    }

    private static IEnumerable<ClientProfile> EnumerateRegistrationClients(ClientProfile client)
    {
        // Preserve the existing normal single-GSTIN import flow. Every
        // registration uses the SAME parser, source-state and import sequence.
        yield return client;
        var registrations = client.AdditionalGstins ?? [];
        foreach (var gstin in registrations.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (gstin.Equals(client.Gstin, StringComparison.OrdinalIgnoreCase)) continue;
            var registration = client.Clone();
            registration.Gstin = gstin;
            registration.AdditionalGstins = client.AdditionalGstins;
            yield return registration;
        }
    }

    private GstPortalAutoImportResult ImportAvailableForRegistration(
        ClientProfile client, string preferredRoot, bool force,
        bool refreshDiscovery, IReadOnlyList<string>? additionalRoots)
    {
        // Force a fresh metadata snapshot here because BackgroundAutomationService may
        // have moved a newly validated Downloads file immediately before this call.
        var discovery = GetDiscoverySnapshot(
            preferredRoot,
            forceRefresh: force || refreshDiscovery,
            additionalRoots: additionalRoots);
        var roots = discovery.Roots;
        var files = discovery.Files;
        var discoveryFingerprint = discovery.Fingerprint;
        var savedBootstrap = repository.GetBootstrapState(
            client.Id,
            client.FinancialYear,
            Normalize(client.Gstin));

        // Fix44: an unchanged file set can still contain GSTR-3B rows parsed by an
        // older Nil recognizer. Repair those rows once, identified by the parser-version
        // stamp stored in RawFields, before honoring the discovery cache hit.
        var warnings = new List<string>();
        var repairedNilReturns = RepairStoredGstr3bNilRecognition(client, warnings);

        if (!force &&
            repairedNilReturns == 0 &&
            savedBootstrap is not null &&
            savedBootstrap.DiscoveryFingerprint.Equals(discoveryFingerprint, StringComparison.Ordinal))
        {
            return new GstPortalAutoImportResult(
                roots.Count, 0, 0, 0, [], true, 0);
        }

        var importedReturns = repairedNilReturns + ImportReturns(client, files, warnings, force);
        var creditPeriods = ImportLedger(
            client,
            files,
            "Electronic Credit Ledger",
            IsCreditLedgerCandidate,
            warnings,
            force);
        var cashPeriods = ImportLedger(
            client,
            files,
            "Electronic Cash Ledger",
            IsCashLedgerCandidate,
            warnings,
            force);

        var finalFingerprint = discoveryFingerprint;
        var now = DateTime.UtcNow;
        repository.UpsertBootstrapState(new GstBootstrapState(
            client.Id,
            client.FinancialYear,
            Normalize(client.Gstin),
            finalFingerprint,
            now,
            importedReturns + creditPeriods + cashPeriods > 0
                ? now
                : savedBootstrap?.LastImportedUtc ?? DateTime.MinValue));

        return new GstPortalAutoImportResult(
            roots.Count,
            importedReturns,
            creditPeriods,
            cashPeriods,
            warnings,
            false,
            files.Count);
    }


    private int RepairStoredGstr3bNilRecognition(
        ClientProfile client,
        ICollection<string> warnings)
    {
        var repaired = 0;
        foreach (var existing in repository.GetReturns(client.Id, client.FinancialYear))
        {
            if (!Normalize(existing.Gstin).Equals(Normalize(client.Gstin), StringComparison.Ordinal)) continue;
            if (existing.RawFields.TryGetValue("nil_parser_version", out var version) &&
                version.Equals("FIX44", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrWhiteSpace(existing.SourcePath) || !File.Exists(existing.SourcePath))
                continue;

            try
            {
                var parsed = gstr3bImportService.Parse(
                    existing.SourcePath,
                    client.Id,
                    client.FinancialYear,
                    client.AdditionalGstins is { Count: > 0 }
                        ? string.Empty : client.Gstin);
                if (!MatchesClient(parsed.Gstin, client.Gstin) ||
                    !IsPeriodInFinancialYear(parsed.PeriodKey, client.FinancialYear) ||
                    !parsed.PeriodKey.Equals(existing.PeriodKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                parsed.SourcePath = existing.SourcePath;
                parsed.ImportedUtc = existing.ImportedUtc == DateTime.MinValue ? DateTime.UtcNow : existing.ImportedUtc;
                parsed.LastEditedUtc = existing.LastEditedUtc;
                repository.ReplaceReturn(parsed);
                repaired++;
            }
            catch (Exception exception)
            {
                warnings.Add($"{Path.GetFileName(existing.SourcePath)}: Nil-recognition repair deferred: {exception.Message}");
            }
        }
        return repaired;
    }

    private int ImportReturns(
        ClientProfile client,
        IReadOnlyList<string> files,
        ICollection<string> warnings,
        bool force)
    {
        var imported = 0;
        // Even an explicit force-reimport must keep the newest source version
        // authoritative for a period. Candidates are newest-first, so after one
        // source for a period imports successfully, older same-period evidence is
        // retained/cached but must never overwrite it in the same force run.
        var forcedImportedPeriods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in files
                     .Where(IsGstr3bCandidate)
                     .Select(CreateCandidate)
                     .OrderByDescending(item => item.WrittenUtc)
                     .Take(120))
        {
            try
            {
                var existingState = repository.GetPortalSourceState(
                    client.Id,
                    client.FinancialYear,
                    "GSTR-3B",
                    candidate.Path);

                if (!force &&
                    existingState is not null &&
                    Normalize(existingState.Gstin).Equals(Normalize(client.Gstin), StringComparison.Ordinal) &&
                    existingState.MetadataFingerprint.Equals(candidate.MetadataFingerprint, StringComparison.Ordinal))
                {
                    continue;
                }

                var contentHash = SafeHash(candidate.Path);
                if (!force &&
                    existingState is not null &&
                    Normalize(existingState.Gstin).Equals(Normalize(client.Gstin), StringComparison.Ordinal) &&
                    existingState.ContentHash.Length > 0 &&
                    existingState.ContentHash.Equals(contentHash, StringComparison.OrdinalIgnoreCase))
                {
                    repository.UpsertPortalSourceState(existingState with
                    {
                        SourceSize = candidate.Size,
                        SourceLastWriteUtc = candidate.WrittenUtc,
                        MetadataFingerprint = candidate.MetadataFingerprint,
                        LastCheckedUtc = DateTime.UtcNow,
                        Status = existingState.Status.Length == 0 ? "Unchanged" : existingState.Status
                    });
                    continue;
                }

                var record = gstr3bImportService.Parse(
                    candidate.Path,
                    client.Id,
                    client.FinancialYear,
                    client.AdditionalGstins is { Count: > 0 }
                        ? string.Empty : client.Gstin);

                // Files for another registration under this same client
                // must not be marked Ignored or cached as this registration.
                // Their owner processes them in its own import pass.
                if (!MatchesClient(record.Gstin, client.Gstin))
                    continue;
                if (!IsPeriodInFinancialYear(record.PeriodKey, client.FinancialYear))
                {
                    repository.UpsertPortalSourceState(ToState(
                        client, "GSTR-3B", candidate, record.PeriodKey,
                        contentHash, "Ignored", DateTime.MinValue,
                        existingState?.LastEditedUtc));
                    continue;
                }

                var latestForPeriod = repository.GetLatestPortalPeriodState(
                    client.Id,
                    client.FinancialYear,
                    "GSTR-3B",
                    record.PeriodKey,
                    client.Gstin);

                if (force && forcedImportedPeriods.Contains(record.PeriodKey))
                {
                    repository.UpsertPortalSourceState(ToState(
                        client, "GSTR-3B", candidate, record.PeriodKey,
                        contentHash, "Older Version", DateTime.MinValue,
                        existingState?.LastEditedUtc ?? latestForPeriod?.LastEditedUtc));
                    continue;
                }

                var samePathContentChanged =
                    existingState is not null &&
                    existingState.ContentHash.Length > 0 &&
                    !existingState.ContentHash.Equals(contentHash, StringComparison.OrdinalIgnoreCase);

                if (!force && !samePathContentChanged && latestForPeriod is not null)
                {
                    if (contentHash.Length > 0 &&
                        latestForPeriod.ContentHash.Length > 0 &&
                        latestForPeriod.ContentHash.Equals(contentHash, StringComparison.OrdinalIgnoreCase))
                    {
                        repository.UpsertPortalSourceState(ToState(
                            client, "GSTR-3B", candidate, record.PeriodKey,
                            contentHash, "Duplicate", latestForPeriod.LastImportedUtc,
                            existingState?.LastEditedUtc ?? latestForPeriod.LastEditedUtc));
                        continue;
                    }

                    if (candidate.WrittenUtc < latestForPeriod.SourceLastWriteUtc)
                    {
                        repository.UpsertPortalSourceState(ToState(
                            client, "GSTR-3B", candidate, record.PeriodKey,
                            contentHash, "Older Version", DateTime.MinValue,
                            existingState?.LastEditedUtc));
                        continue;
                    }
                }

                record.SourcePath = candidate.Path;
                record.ImportedUtc = DateTime.UtcNow;
                record.LastEditedUtc = existingState?.LastEditedUtc ?? latestForPeriod?.LastEditedUtc;
                repository.ReplaceReturn(record);
                imported++;
                if (force) forcedImportedPeriods.Add(record.PeriodKey);

                repository.UpsertPortalSourceState(ToState(
                    client,
                    "GSTR-3B",
                    candidate,
                    record.PeriodKey,
                    contentHash,
                    "Imported",
                    record.ImportedUtc,
                    existingState?.LastEditedUtc ?? latestForPeriod?.LastEditedUtc));
            }
            catch (Exception exception)
            {
                warnings.Add($"{Path.GetFileName(candidate.Path)}: {exception.Message}");
            }
        }

        return imported;
    }

    private int ImportLedger(
        ClientProfile client,
        IReadOnlyList<string> files,
        string ledgerType,
        Func<string, bool> candidatePredicate,
        ICollection<string> warnings,
        bool force)
    {
        var candidates = files
            .Where(candidatePredicate)
            .Select(path => new
            {
                Candidate = CreateCandidate(path),
                Score = CandidateScore(path, client)
            })
            .Where(item => item.Score >= 20)
            .Where(item => client.AdditionalGstins is not { Count: > 0 } ||
                ledgerImportService.IsValidSourceForClient(
                    item.Candidate.Path, client.Gstin, client.FinancialYear))
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Candidate.WrittenUtc)
            .Take(40)
            .Select(item => item.Candidate)
            .ToArray();

        if (candidates.Length == 0) return 0;

        var changed = force;
        if (!force)
        {
            foreach (var candidate in candidates)
            {
                var state = repository.GetPortalSourceState(
                    client.Id,
                    client.FinancialYear,
                    ledgerType,
                    candidate.Path);
                if (state is null ||
                    !state.MetadataFingerprint.Equals(candidate.MetadataFingerprint, StringComparison.Ordinal))
                {
                    var hash = SafeHash(candidate.Path);
                    if (state is null ||
                        state.ContentHash.Length == 0 ||
                        !state.ContentHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                    {
                        changed = true;
                        break;
                    }

                    repository.UpsertPortalSourceState(state with
                    {
                        SourceSize = candidate.Size,
                        SourceLastWriteUtc = candidate.WrittenUtc,
                        MetadataFingerprint = candidate.MetadataFingerprint,
                        LastCheckedUtc = DateTime.UtcNow,
                        Status = state.Status.Length == 0 ? "Unchanged" : state.Status
                    });
                }
            }
        }

        if (!changed) return 0;

        var periods = new Dictionary<string, (GstLedgerPeriodBalance Period, DateTime WrittenUtc)>(
            StringComparer.OrdinalIgnoreCase);
        var successfulCandidates = new List<(PortalFileCandidate Candidate, string Hash)>();

        foreach (var candidate in candidates)
        {
            try
            {
                var hash = SafeHash(candidate.Path);
                foreach (var period in ledgerImportService.Parse(
                             candidate.Path,
                             ledgerType,
                             client.Id,
                             client.FinancialYear,
                             client.Gstin))
                {
                    if (!periods.TryGetValue(period.PeriodKey, out var existing) ||
                        candidate.WrittenUtc > existing.WrittenUtc)
                    {
                        periods[period.PeriodKey] = (period, candidate.WrittenUtc);
                    }
                }
                successfulCandidates.Add((candidate, hash));
            }
            catch (Exception exception)
            {
                warnings.Add($"{Path.GetFileName(candidate.Path)}: {exception.Message}");
            }
        }

        if (periods.Count == 0) return 0;

        foreach (var period in periods.Values)
            period.Period.Gstin = Normalize(client.Gstin);
        repository.ReplaceLedgerPeriods(
            client.Id,
            client.FinancialYear,
            ledgerType,
            periods.Values.Select(item => item.Period),
            Normalize(client.Gstin));

        var importedUtc = DateTime.UtcNow;
        foreach (var (candidate, hash) in successfulCandidates)
        {
            var existing = repository.GetPortalSourceState(
                client.Id,
                client.FinancialYear,
                ledgerType,
                candidate.Path);
            repository.UpsertPortalSourceState(ToState(
                client,
                ledgerType,
                candidate,
                "*",
                hash,
                "Imported",
                importedUtc,
                existing?.LastEditedUtc));
        }
        return periods.Count;
    }

    private PortalDiscoverySnapshot GetDiscoverySnapshot(
        string preferredRoot,
        bool forceRefresh,
        IReadOnlyList<string>? additionalRoots = null)
    {
        var roots = GetSearchRoots(preferredRoot, additionalRoots);
        var rootKey = string.Join("|", roots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            .ToUpperInvariant();

        lock (_discoveryGate)
        {
            // One maintenance pass asks this question once per Client/FY. Reuse the same
            // metadata enumeration for two seconds so 11 clients do not rescan the same
            // E-returns tree 11 times. The 20-second checker cadence itself is unchanged.
            if (!forceRefresh &&
                _cachedDiscovery is not null &&
                _cachedDiscoveryRootKey.Equals(rootKey, StringComparison.Ordinal) &&
                DateTime.UtcNow - _cachedDiscoveryUtc <= TimeSpan.FromSeconds(2))
                return _cachedDiscovery;

            // Reuse the shared Downloads/e-Returns enumeration across Client/FY checks
            // in this maintenance burst. A client's linked folder remains a separate root and
            // is still checked; this is enumeration de-duplication only, not a change gate.
            var nowUtc = DateTime.UtcNow;
            var files = roots
                .SelectMany(root => GetRootDiscoveryFiles(root, forceRefresh, nowUtc))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var snapshot = new PortalDiscoverySnapshot(
                roots,
                files,
                BuildDiscoveryFingerprint(files));

            _cachedDiscoveryRootKey = rootKey;
            _cachedDiscoveryUtc = DateTime.UtcNow;
            _cachedDiscovery = snapshot;
            return snapshot;
        }
    }

    private IReadOnlyList<string> GetRootDiscoveryFiles(string root, bool forceRefresh, DateTime nowUtc)
    {
        if (!forceRefresh &&
            _cachedRootDiscoveries.TryGetValue(root, out var cached) &&
            nowUtc - cached.CapturedUtc <= TimeSpan.FromSeconds(2))
            return cached.Files;

        var files = SafeEnumerateFiles(root, 8)
            .Where(IsSupportedPortalFile)
            .Where(IsPotentialPortalFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _cachedRootDiscoveries[root] = new RootDiscoveryCacheEntry(nowUtc, files);

        if (_cachedRootDiscoveries.Count > 64)
        {
            foreach (var stale in _cachedRootDiscoveries
                         .Where(item => nowUtc - item.Value.CapturedUtc > TimeSpan.FromMinutes(2))
                         .Select(item => item.Key)
                         .Take(_cachedRootDiscoveries.Count - 64)
                         .ToArray())
                _cachedRootDiscoveries.Remove(stale);
        }
        return files;
    }

    private static string BuildDiscoveryFingerprint(IEnumerable<string> files)
    {
        using var sha = SHA256.Create();
        var payload = string.Join(
            "\n",
            files.Select(CreateCandidate)
                .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Select(item =>
                    $"{item.Path.ToUpperInvariant()}|{item.Size}|{item.WrittenUtc.Ticks}"));
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    private static GstPortalSourceState ToState(
        ClientProfile client,
        string sourceType,
        PortalFileCandidate candidate,
        string periodKey,
        string contentHash,
        string status,
        DateTime lastImportedUtc,
        DateTime? lastEditedUtc) =>
        new(
            client.Id,
            client.FinancialYear,
            Normalize(client.Gstin),
            sourceType,
            candidate.Path,
            periodKey,
            candidate.Size,
            candidate.WrittenUtc,
            candidate.MetadataFingerprint,
            contentHash,
            DateTime.UtcNow,
            lastImportedUtc,
            lastEditedUtc,
            status);

    private static PortalFileCandidate CreateCandidate(string path)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { fullPath = path; }

        long size = 0;
        DateTime writtenUtc = DateTime.MinValue;
        try
        {
            var info = new FileInfo(fullPath);
            size = info.Exists ? info.Length : 0;
            writtenUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
        }
        catch
        {
            // The subsequent parser will surface a useful warning if this file matters.
        }

        return new PortalFileCandidate(
            fullPath,
            size,
            writtenUtc,
            $"{size}|{writtenUtc.Ticks}");
    }

    private static string SafeHash(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IReadOnlyList<string> GetSearchRoots(string preferredRoot, IReadOnlyList<string>? additionalRoots = null)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredRoot) && Directory.Exists(preferredRoot))
            roots.Add(Path.GetFullPath(preferredRoot));

        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        if (Directory.Exists(downloads) &&
            !roots.Contains(downloads, StringComparer.OrdinalIgnoreCase))
            roots.Add(downloads);

        foreach (var additional in additionalRoots ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(additional) || !Directory.Exists(additional)) continue;
            var full = Path.GetFullPath(additional);
            if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
        }
        return roots;
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, int maxDepth)
    {
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((root, 0));
        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedFiles = 0;

        while (pending.Count > 0 && visitedFiles < 12000)
        {
            var (directory, depth) = pending.Pop();
            string fullDirectory;
            try { fullDirectory = Path.GetFullPath(directory); }
            catch { continue; }

            if (!visitedDirectories.Add(fullDirectory)) continue;

            string[] files;
            try { files = Directory.GetFiles(directory); }
            catch { continue; }

            foreach (var file in files)
            {
                visitedFiles++;
                yield return file;
                if (visitedFiles >= 12000) yield break;
            }

            if (depth >= maxDepth) continue;
            try
            {
                foreach (var child in Directory.GetDirectories(directory))
                {
                    try
                    {
                        var attributes = File.GetAttributes(child);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    pending.Push((child, depth + 1));
                }
            }
            catch
            {
                // Continue with accessible folders.
            }
        }
    }

    private static bool IsPotentialPortalFile(string path) =>
        IsGstr3bCandidate(path) ||
        IsCreditLedgerCandidate(path) ||
        IsCashLedgerCandidate(path);

    private static bool IsSupportedPortalFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".csv", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGstr3bCandidate(string path)
    {
        if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return false;
        return Normalize(path).Contains("GSTR3B", StringComparison.Ordinal);
    }

    private static bool IsCreditLedgerCandidate(string path)
    {
        var normalized = Normalize(path);
        return !normalized.Contains("CASHLEDGER", StringComparison.Ordinal) &&
               (normalized.Contains("ELECTRONICCREDITLEDGER", StringComparison.Ordinal) ||
                normalized.Contains("CREDITLEDGER", StringComparison.Ordinal));
    }

    private static bool IsCashLedgerCandidate(string path)
    {
        var normalized = Normalize(path);
        return normalized.Contains("ELECTRONICCASHLEDGER", StringComparison.Ordinal) ||
               normalized.Contains("CASHLEDGER", StringComparison.Ordinal);
    }

    private static int CandidateScore(string path, ClientProfile client)
    {
        var normalized = Normalize(path);
        var score = 20;
        var gstin = Normalize(client.Gstin);
        if (gstin.Length > 0 && normalized.Contains(gstin, StringComparison.Ordinal)) score += 100;

        foreach (var name in new[] { client.ClientName, client.TradeName, client.LegalName })
        {
            var normalizedName = Normalize(name);
            if (normalizedName.Length >= 4 && normalized.Contains(normalizedName, StringComparison.Ordinal))
                score += 30;
        }

        foreach (var fy in FinancialYearTokens(client.FinancialYear))
        {
            if (normalized.Contains(Normalize(fy), StringComparison.Ordinal))
            {
                score += 25;
                break;
            }
        }
        return score;
    }

    private static IEnumerable<string> FinancialYearTokens(string financialYear)
    {
        var normalized = financialYear.Trim();
        yield return normalized;
        yield return normalized.Replace("-", string.Empty, StringComparison.Ordinal);
        if (normalized.Length >= 7) yield return normalized[..4] + normalized[^2..];
    }

    private static bool MatchesClient(string parsedGstin, string clientGstin) =>
        string.IsNullOrWhiteSpace(clientGstin) ||
        Normalize(parsedGstin).Equals(Normalize(clientGstin), StringComparison.Ordinal);

    private static bool IsPeriodInFinancialYear(string periodKey, string financialYear)
    {
        if (periodKey.Length != 6 ||
            !int.TryParse(periodKey[..4], out var year) ||
            !int.TryParse(periodKey[4..], out var month) ||
            month is < 1 or > 12 ||
            !TryParseFinancialYear(financialYear, out var startYear))
            return false;

        return month >= 4 ? year == startYear : year == startYear + 1;
    }

    private static bool TryParseFinancialYear(string financialYear, out int startYear)
    {
        var digits = new string(financialYear.TakeWhile(character => character != '-').Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out startYear) && startYear >= 2000;
    }

    private static string Normalize(string value) =>
        string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private sealed record RootDiscoveryCacheEntry(
        DateTime CapturedUtc,
        IReadOnlyList<string> Files);

    private sealed record PortalDiscoverySnapshot(
        IReadOnlyList<string> Roots,
        IReadOnlyList<string> Files,
        string Fingerprint);

    private sealed record PortalFileCandidate(
        string Path,
        long Size,
        DateTime WrittenUtc,
        string MetadataFingerprint);
}

public sealed record GstPortalAutoImportResult(
    int SearchRoots,
    int Gstr3bPeriods,
    int CreditLedgerPeriods,
    int CashLedgerPeriods,
    IReadOnlyList<string> Warnings,
    bool CacheHit = false,
    int CandidateFiles = 0)
{
    public int TotalPeriods => Gstr3bPeriods + CreditLedgerPeriods + CashLedgerPeriods;
}
