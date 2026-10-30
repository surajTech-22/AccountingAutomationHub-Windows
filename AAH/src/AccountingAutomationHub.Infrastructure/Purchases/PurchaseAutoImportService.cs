using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AccountingAutomationHub.Application.Purchases;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Infrastructure.Imports;

namespace AccountingAutomationHub.Infrastructure.Purchases;

public sealed class PurchaseAutoImportService :
    IPurchaseAutoImportService
{
    private const decimal AmountTolerance = 0.01m;
    private const string ShownInTwoB = "Shown in 2B";
    private const string NotShownInTwoB = "Not shown in 2B";
    private const string GstIncludedInCost = "GST included in cost";

    private static readonly string[] TwoASections =
    [
        "b2b",
        "b2ba",
        "cdn",
        "cdnr",
        "cdnra",
        "cdna"
    ];

    private static readonly HashSet<string> TwoBSections =
        new(
            TwoASections,
            StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, int> MonthFolderNames =
        new(
            StringComparer.OrdinalIgnoreCase)
        {
            ["JANUARY"] = 1,
            ["JAN"] = 1,
            ["FEBRUARY"] = 2,
            ["FEB"] = 2,
            ["MARCH"] = 3,
            ["MAR"] = 3,
            ["APRIL"] = 4,
            ["APR"] = 4,
            ["MAY"] = 5,
            ["JUNE"] = 6,
            ["JUN"] = 6,
            ["JULY"] = 7,
            ["JUL"] = 7,
            ["AUGUST"] = 8,
            ["AUG"] = 8,
            ["SEPTEMBER"] = 9,
            ["SEPT"] = 9,
            ["SEP"] = 9,
            ["OCTOBER"] = 10,
            ["OCT"] = 10,
            ["NOVEMBER"] = 11,
            ["NOV"] = 11,
            ["DECEMBER"] = 12,
            ["DEC"] = 12
        };

    public Task<PurchaseAutoImportResult> ImportAsync(
        PurchaseAutoImportRequest request,
        IProgress<PurchaseImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Task.Run(
            () => Import(
                request,
                progress,
                cancellationToken),
            cancellationToken);
    }

    private PurchaseAutoImportResult Import(
        PurchaseAutoImportRequest request,
        IProgress<PurchaseImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var gstin = NormalizeGstin(
            request.ClientGstin);

        if (gstin.Length != 15)
        {
            throw new InvalidOperationException(
                "The active client must have a valid 15-character GSTIN before automatic Purchase import.");
        }

        var financialYear = NormalizeFinancialYear(
            request.FinancialYear);

        if (financialYear.Length == 0)
        {
            throw new InvalidOperationException(
                "The active client must have a valid Financial Year such as 2025-26.");
        }

        var cachedPurchasePaths = SalesPurchaseSourceCache.GetPaths("Purchase", gstin, financialYear);

        DirectoryInfo? configuredRoot = null;
        if (!string.IsNullOrWhiteSpace(request.EReturnsRootPath))
        {
            var candidate = new DirectoryInfo(request.EReturnsRootPath.Trim());
            if (candidate.Exists) configuredRoot = candidate;
        }

        // Fix39: the shared GSTIN/FY source cache is a valid read-only Purchase source.
        // A missing E-returns root must not block Auto Import when the global watcher has
        // already indexed valid GSTR-2A/2B JSON/ZIP files from Downloads/Automation folders.
        if (configuredRoot is null && cachedPurchasePaths.Count == 0)
        {
            throw new DirectoryNotFoundException(
                "Neither the E-returns root nor a cached GSTR-2A/2B source set is available for automatic Purchase import.");
        }

        var root = configuredRoot ?? new DirectoryInfo(
            Path.GetDirectoryName(cachedPurchasePaths.First())
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        progress?.Report(
            new PurchaseImportProgress(
                5,
                "Resolving cached/E-returns Purchase sources"));

        var issues =
            new List<PurchaseImportIssue>();

        var twoAResolution = ResolveForm(
            root,
            gstin,
            financialYear,
            "2A",
            cachedPurchasePaths,
            issues,
            cancellationToken);

        progress?.Report(
            new PurchaseImportProgress(
                22,
                "Resolving E-returns GSTR-2B sources"));

        var twoBResolution = ResolveForm(
            root,
            gstin,
            financialYear,
            "2B",
            cachedPurchasePaths,
            issues,
            cancellationToken);

        var blocked =
            twoAResolution.Verification.Blocked ||
            twoBResolution.Verification.Blocked;

        var twoASources =
            twoAResolution.Sources;

        var twoBSources =
            twoBResolution.Sources;

        if (request.SearchWindowsDownloads)
        {
            progress?.Report(
                new PurchaseImportProgress(
                    35,
                    "Checking Windows Downloads fallback"));

            var fallback = ReadDownloadsFallback(
                gstin,
                issues,
                cancellationToken);

            if (twoASources.Count == 0 &&
                !twoAResolution.Verification.Blocked)
            {
                twoASources = fallback.TwoA;
                twoAResolution = new FormResolution(
                    twoASources,
                    BuildLegacyVerification(
                        "2A",
                        twoASources,
                        twoAResolution.Verification));
            }

            if (twoBSources.Count == 0 &&
                !twoBResolution.Verification.Blocked)
            {
                twoBSources = fallback.TwoB;
                twoBResolution = new FormResolution(
                    twoBSources,
                    BuildLegacyVerification(
                        "2B",
                        twoBSources,
                        twoBResolution.Verification));
            }
        }

        var verification =
            new PurchaseSourceVerification
            {
                EReturnsRoot = configuredRoot?.FullName ?? "GSTIN Cache",
                Blocked = blocked,
                TwoA = twoAResolution.Verification,
                TwoB = twoBResolution.Verification
            };

        if (blocked)
        {
            return BuildBlockedResult(
                request,
                gstin,
                financialYear,
                verification,
                issues);
        }

        progress?.Report(
            new PurchaseImportProgress(
                52,
                "Reading GSTR-2A Purchase documents"));

        var twoADocuments = SelectEffectiveTwoADocuments(
            twoASources,
            financialYear,
            cancellationToken);

        progress?.Report(
            new PurchaseImportProgress(
                65,
                "Reading GSTR-2B reconciliation documents"));

        var twoBData = ExtractTwoBRecords(
            twoBSources,
            cancellationToken);

        var companyName =
            request.CompanyName.Trim();

        if (companyName.Length == 0)
        {
            companyName = DetectCompanyName(
                gstin,
                twoASources.Concat(twoBSources));
        }

        progress?.Report(
            new PurchaseImportProgress(
                74,
                "Reconciling Purchase, RCM and supplier notes"));

        var records = BuildRecords(
            gstin,
            companyName,
            financialYear,
            twoADocuments,
            twoBData,
            issues,
            cancellationToken);

        var warnings = BuildWarnings(
            request,
            twoASources,
            twoBSources,
            verification,
            twoADocuments,
            twoBData.Records);

        var manifest = BuildManifest(
            records,
            twoADocuments,
            twoASources,
            twoBSources);

        var summary = BuildSummary(
            records,
            twoADocuments.Count,
            twoBData.Records.Count,
            issues.Count);

        progress?.Report(
            new PurchaseImportProgress(
                100,
                "Automatic Purchase import completed"));

        return new PurchaseAutoImportResult
        {
            CompanyName = companyName,
            ClientGstin = gstin,
            FinancialYear = financialYear,
            WasBlocked = false,
            Records = records,
            Issues = issues,
            Warnings = warnings,
            Summary = summary,
            Verification = verification,
            SourceManifest = manifest
        };
    }

    private static PurchaseAutoImportResult BuildBlockedResult(
        PurchaseAutoImportRequest request,
        string gstin,
        string financialYear,
        PurchaseSourceVerification verification,
        IReadOnlyList<PurchaseImportIssue> issues)
    {
        var warnings =
            new List<string>
            {
                "Automatic Purchase replacement was blocked because the Annual and monthly E-returns snapshots do not match. Existing automatic records were retained."
            };

        if (request.SaveSourceFiles)
        {
            warnings.Add(
                "Save Files is reserved for the future storage plan and did not perform any file operation.");
        }

        return new PurchaseAutoImportResult
        {
            CompanyName = request.CompanyName.Trim(),
            ClientGstin = gstin,
            FinancialYear = financialYear,
            WasBlocked = true,
            Records = [],
            Issues = issues,
            Warnings = warnings,
            Summary = new PurchaseImportSummary
            {
                InvalidRowsSkipped = issues.Count
            },
            Verification = verification,
            SourceManifest = []
        };
    }

    private static FormResolution ResolveForm(
        DirectoryInfo eReturnsRoot,
        string gstin,
        string financialYear,
        string sourceType,
        IReadOnlyList<string> cachedPaths,
        List<PurchaseImportIssue> issues,
        CancellationToken cancellationToken)
    {
        // Fix38 cache-first: annual/monthly source selection is performed from the shared
        // GSTIN/FY source index before any recursive E-returns directory walk.
        var cachedCandidates = CollectCachedPurchaseCandidates(
            cachedPaths,
            sourceType,
            gstin,
            financialYear,
            issues,
            cancellationToken);
        if (cachedCandidates.Count > 0)
            return ResolveFormCandidates(
                sourceType,
                "Shared Sales/Purchase Watcher",
                eReturnsRoot,
                cachedCandidates,
                financialYear,
                cancellationToken);

        var financialYearRoot = FindFinancialYearRoot(
            eReturnsRoot,
            financialYear);

        if (financialYearRoot is null)
        {
            return new FormResolution(
                [],
                new PurchaseFormVerification
                {
                    Form = sourceType,
                    SourceOrigin = "E-returns",
                    Status = "Financial Year Folder Not Found"
                });
        }

        List<SourceCandidate> chosenCandidates = [];
        var chosenBranch = string.Empty;
        DirectoryInfo? chosenClientRoot = null;

        foreach (var branchName in new[]
                 {
                     "imported",
                     "downloaded"
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();

            var branchRoot = CaseInsensitiveChild(
                financialYearRoot,
                branchName);

            if (branchRoot is null)
            {
                continue;
            }

            var clientRoot = CaseInsensitiveChild(
                branchRoot,
                gstin);

            if (clientRoot is null)
            {
                continue;
            }

            var candidates = CollectEReturnsCandidates(
                clientRoot,
                sourceType,
                gstin,
                financialYear,
                branchName,
                issues,
                cancellationToken);

            if (candidates.Count == 0)
            {
                continue;
            }

            chosenCandidates = candidates;
            chosenBranch = branchName;
            chosenClientRoot = clientRoot;
            break;
        }

        if (chosenCandidates.Count == 0)
        {
            return new FormResolution(
                [],
                new PurchaseFormVerification
                {
                    Form = sourceType,
                    SourceOrigin = "E-returns",
                    Status = "Not Found"
                });
        }

        var selectedClientRoot =
            chosenClientRoot
            ?? throw new InvalidOperationException(
                "The selected E-returns client folder was not resolved.");

        var annual = LatestSource(
            chosenCandidates.Where(
                candidate =>
                    candidate.Scope == "Annual"));

        var monthly = LatestMonthlySources(
            chosenCandidates);

        if (annual is not null &&
            monthly.Count > 0)
        {
            var comparison = CompareSourceSets(
                sourceType,
                [annual],
                monthly,
                financialYear,
                cancellationToken);

            // The Annual GST export is authoritative. Monthly sources are
            // optional diagnostic cross-checks, not a prerequisite for import.
            // Keep differences in verification evidence without blocking.
            var selected =
                new List<SourceCandidate>
                {
                    annual
                };

            if (sourceType == "2B" && comparison.Matched)
            {
                // Only supplement 2B rejection details from monthly files if
                // the accepted invoice set agrees with the annual export.
                foreach (var monthlySource in monthly)
                {
                    var supplemental = RejectedOnlyTwoBSource(monthlySource);
                    if (supplemental is not null) selected.Add(supplemental);
                }
            }

            var status = comparison.Matched
                ? "Annual Verified with Monthly"
                : "Annual Primary - Monthly Difference (Review Only)";
            foreach (var source in selected) source.VerificationStatus = status;

            return new FormResolution(selected,
                BuildVerification(sourceType, chosenBranch, selectedClientRoot,
                    annual, monthly, selected, status, false, comparison));
        }

        if (annual is not null)
        {
            annual.VerificationStatus =
                "Annual Imported - Monthly Not Available";

            return new FormResolution(
                [annual],
                BuildVerification(
                    sourceType,
                    chosenBranch,
                    selectedClientRoot,
                    annual,
                    [],
                    [annual],
                    annual.VerificationStatus,
                    false,
                    null));
        }

        if (monthly.Count > 0)
        {
            foreach (var source in monthly)
            {
                source.VerificationStatus =
                    "Monthly Imported - Annual Not Available";
            }

            return new FormResolution(
                monthly,
                BuildVerification(
                    sourceType,
                    chosenBranch,
                    selectedClientRoot,
                    null,
                    monthly,
                    monthly,
                    "Monthly Imported - Annual Not Available",
                    false,
                    null));
        }

        return new FormResolution(
            [],
            new PurchaseFormVerification
            {
                Form = sourceType,
                SourceOrigin = "E-returns",
                SourceBranch = chosenBranch,
                ClientRoot = selectedClientRoot.FullName,
                Status = "No Annual or Monthly Source Found"
            });
    }

    private static FormResolution ResolveFormCandidates(
        string sourceType,
        string branch,
        DirectoryInfo clientRoot,
        List<SourceCandidate> candidates,
        string financialYear,
        CancellationToken cancellationToken)
    {
        var annual = LatestSource(candidates.Where(candidate => candidate.Scope == "Annual"));
        var monthly = LatestMonthlySources(candidates);
        if (annual is not null && monthly.Count > 0)
        {
            var comparison = CompareSourceSets(sourceType, [annual], monthly, financialYear, cancellationToken);
            // The Annual GST export is authoritative. Never reject valid
            // annual data because optional monthly exports disagree.
            var selected = new List<SourceCandidate> { annual };
            if (sourceType == "2B" && comparison.Matched)
            {
                foreach (var monthlySource in monthly)
                {
                    var supplemental = RejectedOnlyTwoBSource(monthlySource);
                    if (supplemental is not null) selected.Add(supplemental);
                }
            }
            var status = comparison.Matched
                ? "Annual Verified with Monthly"
                : "Annual Primary - Monthly Difference (Review Only)";
            foreach (var source in selected) source.VerificationStatus = status;
            return new FormResolution(selected, BuildVerification(
                sourceType, branch, clientRoot, annual, monthly, selected,
                status, false, comparison));
        }

        if (annual is not null)
        {
            annual.VerificationStatus = "Annual Imported - Monthly Not Available";
            return new FormResolution([annual], BuildVerification(
                sourceType, branch, clientRoot, annual, [], [annual],
                annual.VerificationStatus, false, null));
        }

        if (monthly.Count > 0)
        {
            foreach (var source in monthly) source.VerificationStatus = "Monthly Imported - Annual Not Available";
            return new FormResolution(monthly, BuildVerification(
                sourceType, branch, clientRoot, null, monthly, monthly,
                "Monthly Imported - Annual Not Available", false, null));
        }

        return new FormResolution([], new PurchaseFormVerification
        {
            Form = sourceType,
            SourceOrigin = "GSTIN Cache",
            SourceBranch = branch,
            ClientRoot = clientRoot.FullName,
            Status = "No Annual or Monthly Source Found"
        });
    }

    private static List<SourceCandidate> CollectCachedPurchaseCandidates(
        IReadOnlyList<string> paths,
        string sourceType,
        string gstin,
        string financialYear,
        List<PurchaseImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var candidates = new List<SourceCandidate>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path)) continue;
            var file = new FileInfo(path);
            foreach (var payload in ReadJsonPayloads(file, issues))
            {
                var classification = ClassifyPayload(payload.Payload);
                if (classification.SourceType != sourceType || classification.Gstin != gstin) continue;
                if (!PeriodInFinancialYear(classification.Period, financialYear)) continue;

                var annualByPath = NormalizePathToken(path).Contains("ANNUAL", StringComparison.OrdinalIgnoreCase);
                var annualAggregate = IsAnnualAggregatePayload(payload.Payload, sourceType);
                var scope = annualByPath || annualAggregate ? "Annual" : "Monthly";
                var embedded = EmbeddedTimestamp(payload.Payload);
                candidates.Add(new SourceCandidate
                {
                    SourceType = sourceType,
                    SourcePath = file.FullName,
                    SourceName = file.Name,
                    InternalName = payload.InternalName,
                    Period = classification.Period,
                    Payload = NormalizePayload(payload.Payload, sourceType),
                    ModifiedUtc = file.LastWriteTimeUtc,
                    EmbeddedTimestamp = embedded,
                    SelectionTimestamp = embedded ?? file.LastWriteTimeUtc,
                    PayloadHash = PayloadHash(payload.Payload),
                    SourceOrigin = "GSTIN Cache",
                    Branch = "Shared Sales/Purchase Watcher",
                    Scope = scope,
                    MonthKey = scope == "Monthly" ? PeriodToMonthKey(classification.Period) : string.Empty,
                    IsAnnualAggregate = annualByPath || annualAggregate,
                    ReadOnly = true,
                    VerificationStatus = "Cached source"
                });
            }
        }
        return candidates;
    }

    private static bool PeriodInFinancialYear(string period, string financialYear)
    {
        var normalized = NormalizePeriod(period);
        if (normalized.Length != 6 || financialYear.Length < 4) return false;
        if (!int.TryParse(normalized[..2], out var month) || !int.TryParse(normalized[2..], out var year)) return false;
        if (!int.TryParse(financialYear[..4], out var startYear)) return false;
        return month >= 4 ? year == startYear : year == startYear + 1;
    }

    private static string PeriodToMonthKey(string period)
    {
        var normalized = NormalizePeriod(period);
        return normalized.Length == 6 ? normalized[2..] + normalized[..2] : string.Empty;
    }

    private static string NormalizePathToken(string path) =>
        new string((path ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static PurchaseFormVerification BuildVerification(
        string sourceType,
        string branch,
        DirectoryInfo clientRoot,
        SourceCandidate? annual,
        IReadOnlyList<SourceCandidate> monthly,
        IReadOnlyList<SourceCandidate> selected,
        string status,
        bool blocked,
        SourceComparison? comparison)
    {
        return new PurchaseFormVerification
        {
            Form = sourceType,
            SourceOrigin = "E-returns",
            SourceBranch = branch,
            Status = status,
            Blocked = blocked,
            ClientRoot = clientRoot.FullName,
            AnnualSource = annual?.SourcePath ?? string.Empty,
            AnnualEmbeddedTimestamp =
                annual?.EmbeddedTimestamp?.ToString("s")
                ?? string.Empty,
            MonthlySources = monthly
                .Select(source => source.SourcePath)
                .ToArray(),
            MonthlyMonthKeys = monthly
                .Select(source => source.MonthKey)
                .ToArray(),
            SelectedSources = selected
                .Select(source => source.SourcePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            AnnualDocumentCount = comparison?.AnnualDocumentCount ?? 0,
            MonthlyDocumentCount = comparison?.MonthlyDocumentCount ?? 0,
            MissingInMonthlyCount = comparison?.MissingInMonthlyCount ?? 0,
            ExtraInMonthlyCount = comparison?.ExtraInMonthlyCount ?? 0,
            DifferentDocumentCount = comparison?.DifferentDocumentCount ?? 0,
            MissingInMonthly = comparison?.MissingInMonthly ?? [],
            ExtraInMonthly = comparison?.ExtraInMonthly ?? [],
            DifferentDocuments = comparison?.DifferentDocuments ?? []
        };
    }

    private static PurchaseFormVerification BuildLegacyVerification(
        string sourceType,
        IReadOnlyList<SourceCandidate> sources,
        PurchaseFormVerification previous)
    {
        if (sources.Count == 0)
        {
            return previous;
        }

        return new PurchaseFormVerification
        {
            Form = sourceType,
            SourceOrigin = "Legacy",
            SourceBranch = "Windows Downloads",
            Status = "Legacy Fallback",
            SelectedSources = sources
                .Select(source => source.SourcePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static DownloadsFallback ReadDownloadsFallback(
        string gstin,
        List<PurchaseImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var downloads = new DirectoryInfo(
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
                "Downloads"));

        if (!downloads.Exists)
        {
            return new DownloadsFallback([], []);
        }

        var twoA = new List<SourceCandidate>();
        var twoB = new List<SourceCandidate>();
        var seen = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var file in downloads
                     .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                     .Where(file =>
                         file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                         file.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(file => file.LastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var payload in ReadJsonPayloads(
                         file,
                         issues))
            {
                var classification = ClassifyPayload(
                    payload.Payload);

                if (classification.Gstin != gstin ||
                    classification.SourceType.Length == 0)
                {
                    continue;
                }

                var hash = PayloadHash(
                    payload.Payload);

                var signature = string.Join(
                    "|",
                    classification.SourceType,
                    classification.Gstin,
                    classification.Period,
                    hash);

                if (!seen.Add(signature))
                {
                    continue;
                }

                var normalized = NormalizePayload(
                    payload.Payload,
                    classification.SourceType);

                var embedded = EmbeddedTimestamp(
                    payload.Payload);

                var candidate =
                    new SourceCandidate
                    {
                        SourceType = classification.SourceType,
                        SourcePath = file.FullName,
                        SourceName = file.Name,
                        InternalName = payload.InternalName,
                        Period = classification.Period,
                        Payload = normalized,
                        ModifiedUtc = file.LastWriteTimeUtc,
                        EmbeddedTimestamp = embedded,
                        SelectionTimestamp = embedded ?? file.LastWriteTimeUtc,
                        PayloadHash = hash,
                        SourceOrigin = "Windows Downloads",
                        Branch = string.Empty,
                        Scope = "Legacy",
                        MonthKey = string.Empty,
                        IsAnnualAggregate = IsAnnualAggregatePayload(
                            payload.Payload,
                            classification.SourceType),
                        ReadOnly = true,
                        VerificationStatus = "Legacy Fallback"
                    };

                if (classification.SourceType == "2A")
                {
                    twoA.Add(candidate);
                }
                else
                {
                    twoB.Add(candidate);
                }
            }
        }

        return new DownloadsFallback(twoA, twoB);
    }

    private static List<SourceCandidate> CollectEReturnsCandidates(
        DirectoryInfo clientRoot,
        string sourceType,
        string gstin,
        string financialYear,
        string branchName,
        List<PurchaseImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var candidates =
            new List<SourceCandidate>();

        IEnumerable<FileInfo> files;

        try
        {
            files = clientRoot
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(file =>
                    file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                    file.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.DirectoryName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception)
        {
            issues.Add(
                new PurchaseImportIssue
                {
                    SourceFile = clientRoot.FullName,
                    SheetName = "E-returns",
                    RowNumber = 0,
                    Message = exception.Message
                });

            return candidates;
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var scope = GetSourceScope(
                clientRoot,
                file,
                financialYear);

            foreach (var payload in ReadJsonPayloads(
                         file,
                         issues))
            {
                var classification = ClassifyPayload(payload.Payload);
                if (classification.SourceType != sourceType ||
                    classification.Gstin != gstin)
                    continue;

                // Winman can write GSTR2A_ANNUAL/GSTR2B_ANNUAL into a
                // month-named or unclassified E-returns folder after upgrades.
                // Verified FormName takes precedence over folder heuristics.
                var isAnnual = IsAnnualAggregatePayload(payload.Payload, sourceType);
                if (scope.Scope == "Other" && !isAnnual)
                    continue;
                var finalScope = isAnnual ? "Annual" : scope.Scope;
                var embedded = EmbeddedTimestamp(payload.Payload);

                candidates.Add(
                    new SourceCandidate
                    {
                        SourceType = sourceType,
                        SourcePath = file.FullName,
                        SourceName = file.Name,
                        InternalName = payload.InternalName,
                        Period = classification.Period,
                        Payload = NormalizePayload(
                            payload.Payload,
                            sourceType),
                        ModifiedUtc = file.LastWriteTimeUtc,
                        EmbeddedTimestamp = embedded,
                        SelectionTimestamp = embedded ?? file.LastWriteTimeUtc,
                        PayloadHash = PayloadHash(
                            payload.Payload),
                        SourceOrigin = "E-returns",
                        Branch = branchName,
                        Scope = finalScope,
                        MonthKey = finalScope == "Annual" ? string.Empty : scope.MonthKey,
                        IsAnnualAggregate = finalScope == "Annual",
                        ReadOnly = true
                    });
            }
        }

        return candidates;
    }

    private static IReadOnlyList<JsonPayload> ReadJsonPayloads(
        FileInfo file,
        List<PurchaseImportIssue> issues)
    {
        var result =
            new List<JsonPayload>();

        try
        {
            if (file.Extension.Equals(
                    ".json",
                    StringComparison.OrdinalIgnoreCase))
            {
                var node = JsonNode.Parse(
                    File.ReadAllText(
                        file.FullName,
                        Encoding.UTF8));

                if (node is JsonObject payload)
                {
                    result.Add(
                        new JsonPayload(
                            file.Name,
                            payload));
                }

                return result;
            }

            using var stream = file.OpenRead();
            using var archive = new ZipArchive(
                stream,
                ZipArchiveMode.Read,
                leaveOpen: false);

            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                    !entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    using var entryStream = entry.Open();
                    using var reader = new StreamReader(
                        entryStream,
                        Encoding.UTF8,
                        detectEncodingFromByteOrderMarks: true);

                    var node = JsonNode.Parse(
                        reader.ReadToEnd());

                    if (node is JsonObject payload)
                    {
                        result.Add(
                            new JsonPayload(
                                entry.FullName,
                                payload));
                    }
                }
                catch (Exception exception)
                {
                    issues.Add(
                        new PurchaseImportIssue
                        {
                            SourceFile = file.FullName,
                            SheetName = entry.FullName,
                            RowNumber = 0,
                            Message =
                                "JSON member could not be read: " +
                                exception.Message
                        });
                }
            }
        }
        catch (Exception exception)
        {
            issues.Add(
                new PurchaseImportIssue
                {
                    SourceFile = file.FullName,
                    SheetName = "JSON/ZIP",
                    RowNumber = 0,
                    Message = exception.Message
                });
        }

        return result;
    }

    private static PayloadClassification ClassifyPayload(
        JsonObject payload)
    {
        var winRequest = Object(
            payload["winreq"]);

        var response = Object(
            payload["resp"]);

        if (winRequest is not null &&
            response is not null)
        {
            var requestId = Text(
                    winRequest["reqid"])
                .ToUpperInvariant();

            var gstin = NormalizeGstin(
                Text(winRequest["gstin"]));

            var period = NormalizePeriod(
                FirstText(
                    winRequest,
                    "retnprd",
                    "rtnprd"));

            if (gstin.Length == 15 &&
                period.Length == 6 &&
                requestId.Contains(
                    "GSTR2A",
                    StringComparison.Ordinal) &&
                TwoASections.Any(section =>
                    Array(response[section]) is not null))
            {
                return new PayloadClassification(
                    "2A",
                    gstin,
                    period);
            }

            if (gstin.Length == 15 &&
                period.Length == 6 &&
                requestId.Contains(
                    "GSTR2B",
                    StringComparison.Ordinal))
            {
                var annualData = Object(
                    response["data"]);

                if (annualData is not null &&
                    new[]
                    {
                        "docdata",
                        "docRejdata",
                        "cpsumm",
                        "cpSummRej"
                    }.Any(key =>
                        Object(annualData[key]) is not null))
                {
                    return new PayloadClassification(
                        "2B",
                        gstin,
                        period);
                }
            }
        }

        var data = Object(
            payload["data"]);

        if (data is not null)
        {
            var gstin = NormalizeGstin(
                Text(data["gstin"]));

            var period = NormalizePeriod(
                Text(data["rtnprd"]));

            if (gstin.Length == 15 &&
                period.Length == 6 &&
                new[]
                {
                    "docdata",
                    "docRejdata",
                    "cpsumm",
                    "cpSummRej"
                }.Any(key =>
                    Object(data[key]) is not null))
            {
                return new PayloadClassification(
                    "2B",
                    gstin,
                    period);
            }
        }

        var monthlyGstin = NormalizeGstin(
            Text(payload["gstin"]));

        var monthlyPeriod = NormalizePeriod(
            FirstText(
                payload,
                "fp",
                "rtnprd"));

        if (monthlyGstin.Length == 15 &&
            monthlyPeriod.Length == 6 &&
            TwoASections.Any(section =>
                Array(payload[section]) is not null))
        {
            return new PayloadClassification(
                "2A",
                monthlyGstin,
                monthlyPeriod);
        }

        return new PayloadClassification(
            string.Empty,
            string.Empty,
            string.Empty);
    }

    private static bool IsAnnualAggregatePayload(
        JsonObject payload,
        string sourceType)
    {
        // Winman marks both GSTR2A_ANNUAL and GSTR2B_ANNUAL in FormName.
        // retnprd=032026 in these files is the financial-year end,
        // not evidence that the export is a monthly March file.
        var winRequest = Object(payload["winreq"]);
        if (winRequest is null) return false;
        var formName = Text(winRequest["FormName"]);
        var expected = sourceType.Equals("2A", StringComparison.OrdinalIgnoreCase)
            ? "GSTR2A"
            : sourceType.Equals("2B", StringComparison.OrdinalIgnoreCase)
                ? "GSTR2B"
                : string.Empty;
        return expected.Length > 0 &&
            formName.Contains(expected, StringComparison.OrdinalIgnoreCase) &&
            formName.Contains("ANNUAL", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject NormalizePayload(
        JsonObject payload,
        string sourceType)
    {
        var winRequest = Object(
            payload["winreq"]);

        var response = Object(
            payload["resp"]);

        if (winRequest is null ||
            response is null)
        {
            return CloneObject(payload);
        }

        var gstin = NormalizeGstin(
            Text(winRequest["gstin"]));

        var period = FirstText(
            winRequest,
            "retnprd",
            "rtnprd");

        if (sourceType == "2A")
        {
            var normalized = CloneObject(
                response);

            normalized["gstin"] = gstin;
            normalized["fp"] = period;

            if (Array(normalized["cdna"]) is JsonArray amended)
            {
                var canonical = Array(
                    normalized["cdnra"])
                    ?? new JsonArray();

                foreach (var item in amended)
                {
                    canonical.Add(
                        item?.DeepClone());
                }

                normalized["cdnra"] = canonical;
                normalized.Remove("cdna");
            }

            return normalized;
        }

        var annualData = Object(
            response["data"]);

        if (annualData is null)
        {
            return CloneObject(payload);
        }

        var normalizedData = CloneObject(
            annualData);

        normalizedData["gstin"] = gstin;
        normalizedData["rtnprd"] = period;

        if (Text(normalizedData["gendt"]).Length == 0)
        {
            normalizedData["gendt"] =
                Text(winRequest["Respdate"]);
        }

        return new JsonObject
        {
            ["data"] = normalizedData
        };
    }

    private static SourceCandidate? RejectedOnlyTwoBSource(
        SourceCandidate source)
    {
        var data = Object(
            source.Payload["data"]);

        if (data is null)
        {
            return null;
        }

        var rejected = Object(
            data["docRejdata"]);

        var rejectedSummary = Object(
            data["cpSummRej"]);

        if (rejected is null &&
            rejectedSummary is null)
        {
            return null;
        }

        var supplementalData =
            new JsonObject
            {
                ["gstin"] = Text(data["gstin"]),
                ["rtnprd"] = Text(data["rtnprd"]),
                ["gendt"] = Text(data["gendt"])
            };

        if (rejected is not null)
        {
            supplementalData["docRejdata"] =
                rejected.DeepClone();
        }

        if (rejectedSummary is not null)
        {
            supplementalData["cpSummRej"] =
                rejectedSummary.DeepClone();
        }

        return source.CopyWith(
            new JsonObject
            {
                ["data"] = supplementalData
            },
            "Monthly Rejected Supplement");
    }

    private static SourceComparison CompareSourceSets(
        string sourceType,
        IReadOnlyList<SourceCandidate> annual,
        IReadOnlyList<SourceCandidate> monthly,
        string financialYear,
        CancellationToken cancellationToken)
    {
        var annualMap = sourceType == "2A"
            ? CanonicalTwoAMap(
                annual,
                financialYear,
                cancellationToken)
            : CanonicalTwoBMap(
                annual,
                financialYear,
                cancellationToken);

        var monthlyMap = sourceType == "2A"
            ? CanonicalTwoAMap(
                monthly,
                financialYear,
                cancellationToken)
            : CanonicalTwoBMap(
                monthly,
                financialYear,
                cancellationToken);

        var allMissing = annualMap.Keys
            .Except(
                monthlyMap.Keys,
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value)
            .ToArray();

        var allExtra = monthlyMap.Keys
            .Except(
                annualMap.Keys,
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value)
            .ToArray();

        var allDifferent = annualMap.Keys
            .Intersect(
                monthlyMap.Keys,
                StringComparer.OrdinalIgnoreCase)
            .Where(key =>
                !string.Equals(
                    annualMap[key],
                    monthlyMap[key],
                    StringComparison.Ordinal))
            .OrderBy(value => value)
            .ToArray();

        return new SourceComparison(
            allMissing.Length == 0 &&
            allExtra.Length == 0 &&
            allDifferent.Length == 0,
            annualMap.Count,
            monthlyMap.Count,
            allMissing.Length,
            allExtra.Length,
            allDifferent.Length,
            allMissing.Take(100).ToArray(),
            allExtra.Take(100).ToArray(),
            allDifferent.Take(100).ToArray());
    }

    private static Dictionary<string, string> CanonicalTwoAMap(
        IReadOnlyList<SourceCandidate> sources,
        string financialYear,
        CancellationToken cancellationToken)
    {
        var result =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources
                     .OrderBy(source => source.SelectionTimestamp)
                     .ThenBy(source => source.ModifiedUtc))
        {
            foreach (var document in EnumerateTwoADocuments(
                         source))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!DateInFinancialYear(
                        document.CurrentDate,
                        financialYear))
                {
                    continue;
                }

                var key = BuildCanonicalDocumentKey(
                    document);

                var itemBreakup = document.Items
                    .Select(item => string.Join(
                        ":",
                        CanonicalAmount(item.Rate),
                        CanonicalAmount(item.Taxable),
                        CanonicalAmount(item.Igst),
                        CanonicalAmount(item.Cgst),
                        CanonicalAmount(item.Sgst),
                        CanonicalAmount(item.Cess)))
                    .OrderBy(value => value)
                    .ToArray();

                result[key] = string.Join(
                    "|",
                    CanonicalAmount(document.InvoiceValue),
                    CanonicalAmount(document.Taxable),
                    CanonicalAmount(document.Igst),
                    CanonicalAmount(document.Cgst),
                    CanonicalAmount(document.Sgst),
                    CanonicalAmount(document.Cess),
                    NormalizeFlag(document.PlaceOfSupply),
                    NormalizeFlag(document.ReverseCharge),
                    NormalizeFlag(document.InvoiceType),
                    string.Join(",", itemBreakup));
            }
        }

        return result;
    }

    private static Dictionary<string, string> CanonicalTwoBMap(
        IReadOnlyList<SourceCandidate> sources,
        string financialYear,
        CancellationToken cancellationToken)
    {
        var extracted = ExtractTwoBRecords(
            sources,
            cancellationToken);

        var result =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var document in extracted.Records)
        {
            if (document.Acceptance != "Accepted" ||
                !DateInFinancialYear(
                    document.CurrentDate,
                    financialYear))
            {
                continue;
            }

            var key = BuildCanonicalDocumentKey(
                document);

            result[key] = string.Join(
                "|",
                CanonicalAmount(document.InvoiceValue),
                CanonicalAmount(document.Taxable),
                CanonicalAmount(document.Igst),
                CanonicalAmount(document.Cgst),
                CanonicalAmount(document.Sgst),
                CanonicalAmount(document.Cess),
                NormalizeFlag(document.PlaceOfSupply),
                NormalizeFlag(document.ReverseCharge),
                NormalizeFlag(document.InvoiceType),
                NormalizeFlag(document.ImsStatus),
                NormalizeFlag(document.ItcAvailability),
                document.Reason.ToUpperInvariant());
        }

        return result;
    }

    private static string BuildCanonicalDocumentKey(
        ParsedDocument document) =>
        string.Join(
            " | ",
            document.SupplierGstin,
            document.DocumentType,
            document.Section,
            NormalizeDocumentNumber(
                document.CurrentNumber),
            FormatDate(document.CurrentDate),
            NormalizeDocumentNumber(
                document.OriginalNumber),
            FormatDate(document.OriginalDate),
            NormalizeFlag(document.InvoiceType));

    private static List<ParsedDocument> SelectEffectiveTwoADocuments(
        IReadOnlyList<SourceCandidate> sources,
        string financialYear,
        CancellationToken cancellationToken)
    {
        var selected =
            new Dictionary<string, ParsedDocument>(
                StringComparer.OrdinalIgnoreCase);

        var fallbackCounter = 0;

        foreach (var source in sources
                     .OrderBy(source => source.ModifiedUtc))
        {
            foreach (var document in EnumerateTwoADocuments(
                         source))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!DateInFinancialYear(
                        document.CurrentDate,
                        financialYear))
                {
                    continue;
                }

                var logicalNumber =
                    document.OriginalNumber.Length > 0
                        ? document.OriginalNumber
                        : document.CurrentNumber;

                var logicalDate =
                    document.OriginalDate ??
                    document.CurrentDate;

                var key = string.Join(
                    "|",
                    document.SupplierGstin,
                    document.DocumentType,
                    NormalizeDocumentNumber(
                        logicalNumber),
                    FormatDate(logicalDate));

                if (NormalizeDocumentNumber(
                        logicalNumber).Length == 0)
                {
                    fallbackCounter++;
                    key +=
                        "|UNKEYED" +
                        fallbackCounter.ToString(
                            CultureInfo.InvariantCulture);
                }

                if (!selected.TryGetValue(
                        key,
                        out var old) ||
                    document.IsAmendment ||
                    !old.IsAmendment)
                {
                    selected[key] = document;
                }
            }
        }

        return selected.Values.ToList();
    }

    private static IEnumerable<ParsedDocument> EnumerateTwoADocuments(
        SourceCandidate source)
    {
        foreach (var section in TwoASections)
        {
            var groups = Array(
                source.Payload[section]);

            if (groups is null)
            {
                continue;
            }

            foreach (var groupNode in groups)
            {
                var supplier = Object(
                    groupNode);

                if (supplier is null)
                {
                    continue;
                }

                var supplierGstin = NormalizeGstin(
                    Text(supplier["ctin"]));

                var filingDate = ParseDate(
                    FirstText(
                        supplier,
                        "fldtr1",
                        "fldtr"));

                var filingPeriod = NormalizePeriod(
                    FirstText(
                        supplier,
                        "flprdr1",
                        "flprdr"));

                if (filingPeriod.Length == 0)
                {
                    filingPeriod = NormalizePeriod(
                        Text(source.Payload["fp"]));
                }

                var documents = Array(
                    supplier["inv"])
                    ?? Array(supplier["nt"]);

                if (documents is null)
                {
                    continue;
                }

                foreach (var documentNode in documents)
                {
                    var document = Object(
                        documentNode);

                    if (document is null)
                    {
                        continue;
                    }

                    yield return ParseDocument(
                        source,
                        section,
                        supplierGstin,
                        document,
                        filingDate,
                        filingPeriod,
                        acceptance: string.Empty,
                        supplierName: string.Empty,
                        generatedDate: null);
                }
            }
        }
    }

    private static TwoBExtraction ExtractTwoBRecords(
        IReadOnlyList<SourceCandidate> sources,
        CancellationToken cancellationToken)
    {
        var records =
            new List<ParsedDocument>();

        var names =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        var nameSources =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources
                     .OrderBy(source => source.ModifiedUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var data = Object(
                source.Payload["data"]);

            if (data is null)
            {
                continue;
            }

            var generatedDate = ParseDate(
                Text(data["gendt"]));

            var quarterlySuppliers = source.IsAnnualAggregate
                ? InferQuarterlyTwoBSuppliers(data)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var containerInfo in new[]
                     {
                         (Name: "docdata", Acceptance: "Accepted"),
                         (Name: "docRejdata", Acceptance: "Rejected")
                     })
            {
                var container = Object(
                    data[containerInfo.Name]);

                if (container is null)
                {
                    continue;
                }

                foreach (var sectionPair in container)
                {
                    if (!TwoBSections.Contains(
                            sectionPair.Key))
                    {
                        continue;
                    }

                    var suppliers = Array(
                        sectionPair.Value);

                    if (suppliers is null)
                    {
                        continue;
                    }

                    foreach (var supplierNode in suppliers)
                    {
                        var supplier = Object(
                            supplierNode);

                        if (supplier is null)
                        {
                            continue;
                        }

                        var supplierGstin = NormalizeGstin(
                            Text(supplier["ctin"]));

                        var supplierName = Text(
                            supplier["trdnm"]);

                        if (supplierGstin.Length > 0 &&
                            supplierName.Length > 0)
                        {
                            names[supplierGstin] =
                                supplierName;

                            nameSources[supplierGstin] =
                                source.SourcePath;
                        }

                        var documents = Array(
                            supplier["inv"])
                            ?? Array(supplier["nt"]);

                        if (documents is null)
                        {
                            continue;
                        }

                        foreach (var documentNode in documents)
                        {
                            var document = Object(
                                documentNode);

                            if (document is null)
                            {
                                continue;
                            }

                            // Annual "Get All Invoices" uses winreq.retnprd as the
                            // FY-end wrapper. Keep supplier supprd as source/fallback
                            // period evidence and supfildt as the actual filing date.
                            // The final GSTR-2B month is resolved later from the real
                            // monthly day-11 / quarterly-IFF day-13 filing cut-off.
                            var supplierFilingDate = ParseDate(
                                Text(supplier["supfildt"]));
                            var supplierPeriod = NormalizePeriod(
                                Text(supplier["supprd"]));
                            // supprd is retained as supplier-period evidence/fallback.
                            // When supfildt exists the actual 2B availability month is
                            // determined from its monthly/quarterly filing-window cut-off,
                            // not by blindly treating supprd as the final GST month.
                            var filingPeriod = !string.IsNullOrWhiteSpace(supplierPeriod)
                                ? supplierPeriod
                                : source.Period;
                            var quarterlyFiling = source.IsAnnualAggregate &&
                                                  quarterlySuppliers.Contains(
                                                      supplierGstin);

                            records.Add(
                                ParseDocument(
                                    source,
                                    sectionPair.Key,
                                    supplierGstin,
                                    document,
                                    filingDate: supplierFilingDate,
                                    filingPeriod: filingPeriod,
                                    acceptance: containerInfo.Acceptance,
                                    supplierName: supplierName,
                                    generatedDate: generatedDate,
                                    quarterlyFiling: quarterlyFiling));
                        }
                    }
                }
            }

            foreach (var summaryName in new[]
                     {
                         "cpsumm",
                         "cpSummRej"
                     })
            {
                var summary = Object(
                    data[summaryName]);

                if (summary is null)
                {
                    continue;
                }

                foreach (var sectionPair in summary)
                {
                    if (!TwoBSections.Contains(
                            sectionPair.Key))
                    {
                        continue;
                    }

                    var suppliers = Array(
                        sectionPair.Value);

                    if (suppliers is null)
                    {
                        continue;
                    }

                    foreach (var supplierNode in suppliers)
                    {
                        var supplier = Object(
                            supplierNode);

                        if (supplier is null)
                        {
                            continue;
                        }

                        var supplierGstin = NormalizeGstin(
                            Text(supplier["ctin"]));

                        var supplierName = Text(
                            supplier["trdnm"]);

                        if (supplierGstin.Length > 0 &&
                            supplierName.Length > 0)
                        {
                            names[supplierGstin] =
                                supplierName;

                            nameSources[supplierGstin] =
                                source.SourcePath;
                        }
                    }
                }
            }
        }

        return new TwoBExtraction(
            records,
            names,
            nameSources);
    }

    private static HashSet<string> InferQuarterlyTwoBSuppliers(
        JsonObject data)
    {
        var periodsBySupplier =
            new Dictionary<string, HashSet<int>>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var containerName in new[] { "docdata", "docRejdata" })
        {
            var container = Object(data[containerName]);
            if (container is null) continue;

            foreach (var sectionPair in container)
            {
                var suppliers = Array(sectionPair.Value);
                if (suppliers is null) continue;

                foreach (var supplierNode in suppliers)
                {
                    var supplier = Object(supplierNode);
                    if (supplier is null) continue;

                    var gstin = NormalizeGstin(Text(supplier["ctin"]));
                    var periodText = NormalizePeriod(Text(supplier["supprd"]));
                    if (gstin.Length == 0 ||
                        !PurchaseImportUtilities.TryParseReturnPeriod(
                            periodText,
                            out var periodDate))
                    {
                        continue;
                    }

                    if (!periodsBySupplier.TryGetValue(gstin, out var months))
                    {
                        months = [];
                        periodsBySupplier[gstin] = months;
                    }

                    months.Add(periodDate.Month);
                }
            }
        }

        return periodsBySupplier
            .Where(pair =>
                pair.Value.Count >= 2 &&
                pair.Value.All(month => month is 3 or 6 or 9 or 12))
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static DateTime? CalculateTwoBOnlyGstDate(
        ParsedDocument document)
    {
        if (!document.CurrentDate.HasValue)
        {
            return null;
        }

        return PurchaseImportUtilities.CalculateGstr2BGstDate(
            document.CurrentDate.Value,
            document.FilingDate,
            document.IsQuarterlyFiling,
            document.Period);
    }

    private static ParsedDocument ParseDocument(
        SourceCandidate source,
        string section,
        string supplierGstin,
        JsonObject document,
        DateTime? filingDate,
        string filingPeriod,
        string acceptance,
        string supplierName,
        DateTime? generatedDate,
        bool quarterlyFiling = false)
    {
        var isNote = new[]
        {
            "cdn",
            "cdnr",
            "cdnra",
            "cdna"
        }.Contains(
            section,
            StringComparer.OrdinalIgnoreCase);

        var currentNumber = isNote
            ? FirstText(
                document,
                "nt_num",
                "ntnum",
                "nt_no",
                "inum",
                "num")
            : FirstText(
                document,
                "inum",
                "inv_num",
                "invno",
                "num");

        var currentDate = ParseDate(
            isNote
                ? FirstText(
                    document,
                    "nt_dt",
                    "ntdt",
                    "nt_date",
                    "idt",
                    "dt")
                : FirstText(
                    document,
                    "idt",
                    "inv_dt",
                    "invdate",
                    "dt"));

        var originalNumber = isNote
            ? FirstText(
                document,
                "ont_num",
                "ontnum",
                "ont_no",
                "oinum",
                "onum")
            : FirstText(
                document,
                "oinum",
                "oinv_num",
                "oinvno",
                "onum");

        var originalDate = ParseDate(
            isNote
                ? FirstText(
                    document,
                    "ont_dt",
                    "ontdt",
                    "ont_date",
                    "oidt",
                    "odt")
                : FirstText(
                    document,
                    "oidt",
                    "oinv_dt",
                    "oinvdate",
                    "odt"));

        var items = ReadItemRows(
            document);

        return new ParsedDocument
        {
            Source = source,
            SupplierGstin = supplierGstin,
            SupplierName = supplierName,
            Section = section.ToUpperInvariant(),
            IsAmendment = section.EndsWith(
                "a",
                StringComparison.OrdinalIgnoreCase),
            Acceptance = acceptance,
            DocumentType = isNote
                ? "Note"
                : "Invoice",
            CurrentNumber = currentNumber,
            CurrentDate = currentDate,
            OriginalNumber = originalNumber,
            OriginalDate = originalDate,
            InvoiceValue = DecimalValue(
                document["val"]),
            Taxable = items.Sum(item => item.Taxable),
            Igst = items.Sum(item => item.Igst),
            Cgst = items.Sum(item => item.Cgst),
            Sgst = items.Sum(item => item.Sgst),
            Cess = items.Sum(item => item.Cess),
            PlaceOfSupply = Text(
                document["pos"]),
            ReverseCharge = FirstText(
                document,
                "rev",
                "rchrg"),
            InvoiceType = isNote
                ? FirstText(
                    document,
                    "ntty",
                    "typ",
                    "inv_typ")
                : FirstText(
                    document,
                    "inv_typ",
                    "typ",
                    "ntty"),
            ImsStatus = FirstText(
                document,
                "imsStatus",
                "ims_status"),
            ItcAvailability = FirstText(
                document,
                "itcavl",
                "itc_availability"),
            Reason = FirstText(
                document,
                "rsn",
                "reason"),
            FilingDate = filingDate,
            Period = NormalizePeriod(
                filingPeriod).Length > 0
                ? NormalizePeriod(
                    filingPeriod)
                : source.Period,
            GeneratedDate = generatedDate,
            IsQuarterlyFiling = quarterlyFiling,
            Items = items
        };
    }

    private static List<ItemRow> ReadItemRows(
        JsonObject document)
    {
        var items = Array(document["itms"]);

        if (items is null || items.Count == 0)
        {
            var taxable = DecimalValue(document["txval"]);
            var igst = DecimalValue(document["igst"] ?? document["iamt"]);
            var cgst = DecimalValue(document["cgst"] ?? document["camt"]);
            var sgst = DecimalValue(document["sgst"] ?? document["samt"]);
            var cess = DecimalValue(document["cess"] ?? document["csamt"]);
            return [BuildItemRow(document["rt"], taxable, igst, cgst, sgst, cess)];
        }

        var result = new List<ItemRow>();
        foreach (var itemNode in items)
        {
            var item = Object(itemNode);
            if (item is null) continue;
            var detail = Object(item["itm_det"]) ?? item;
            var taxable = DecimalValue(detail["txval"]);
            var igst = DecimalValue(detail["iamt"] ?? detail["igst"]);
            var cgst = DecimalValue(detail["camt"] ?? detail["cgst"]);
            var sgst = DecimalValue(detail["samt"] ?? detail["sgst"]);
            var cess = DecimalValue(detail["csamt"] ?? detail["cess"]);
            result.Add(BuildItemRow(detail["rt"], taxable, igst, cgst, sgst, cess));
        }

        if (result.Count == 0)
            result.Add(new ItemRow(0m, false, 0m, 0m, 0m, 0m, 0m));
        return result;
    }

    private static ItemRow BuildItemRow(
        JsonNode? rateNode,
        decimal taxable,
        decimal igst,
        decimal cgst,
        decimal sgst,
        decimal cess)
    {
        var rateProvided = rateNode is not null;
        var rateKnown = rateProvided;
        var rate = rateProvided ? DecimalValue(rateNode) : 0m;
        if (rateProvided && (Math.Abs(igst) + Math.Abs(cgst) + Math.Abs(sgst)) > 0.005m)
        {
            if (!PurchaseImportUtilities.TryDeriveSupportedRate(taxable, igst, cgst, sgst, out var verified))
            {
                rate = 0m;
                rateKnown = false;
            }
            else if (rate == 0m)
            {
                // Fix13 Rate=0 fallback: portal payloads can carry 0 as a placeholder
                // even when taxable value + GST prove the real standard rate. Only the
                // literal-zero case is repaired; other contradictory supplied rates keep
                // the earlier fail-closed behavior below.
                rate = verified;
                rateKnown = true;
            }
            else if (Math.Abs(verified - rate) > 0.001m)
            {
                rate = 0m;
                rateKnown = false;
            }
        }
        else if (!rateProvided &&
                 PurchaseImportUtilities.TryDeriveSupportedRate(taxable, igst, cgst, sgst, out var derived))
        {
            rate = derived;
            rateKnown = true;
        }
        return new ItemRow(rate, rateKnown, taxable, igst, cgst, sgst, cess);
    }

    private static IReadOnlyList<PurchaseImportRecord> BuildRecords(
        string gstin,
        string companyName,
        string financialYear,
        IReadOnlyList<ParsedDocument> twoA,
        TwoBExtraction twoB,
        List<PurchaseImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var indexes = BuildTwoBIndexes(
            twoB.Records);

        var records =
            new List<PurchaseImportRecord>();
        var matchedTwoB = new HashSet<ParsedDocument>();

        foreach (var document in twoA)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var match = FindTwoBMatch(
                document,
                indexes);

            var reconciliation = Reconcile(
                document,
                match.Document,
                match.MatchType);

            var supplierName =
                match.Document?.SupplierName;

            var nameLookupSourcePath = string.Empty;

            if (string.IsNullOrWhiteSpace(
                    supplierName) &&
                twoB.NamesByGstin.TryGetValue(
                    document.SupplierGstin,
                    out var mappedName))
            {
                supplierName = mappedName;

                twoB.NameSourcesByGstin.TryGetValue(
                    document.SupplierGstin,
                    out var resolvedNameLookupSourcePath);

                nameLookupSourcePath =
                    resolvedNameLookupSourcePath
                    ?? string.Empty;
            }

            var noteType = NormalizeFlag(
                document.InvoiceType);
            if (noteType.Length == 0 && match.Document is not null)
                noteType = NormalizeFlag(match.Document.InvoiceType);

            string destination;
            if (document.DocumentType == "Note")
            {
                if (IsSupplierCreditNoteType(noteType))
                {
                    // Supplier credit note reduces Purchase / Input GST and remains in
                    // the existing Debit Notes tab.
                    destination = PurchaseTreatmentPolicy.DebitNoteDestination;
                }
                else if (IsSupplierDebitNoteType(noteType))
                {
                    // GSTR-1/2B supplier debit note increases the recipient's purchase
                    // value / eligible ITC. Keep it visibly separate from purchase-return
                    // credit notes so Journal, GSTR-3B and Tally can apply the opposite
                    // accounting direction without losing the original Note identity.
                    destination = PurchaseTreatmentPolicy.DebitNoteReverseDestination;
                }
                else
                {
                    issues.Add(
                        new PurchaseImportIssue
                        {
                            SourceFile = document.Source.SourcePath,
                            SheetName = document.Section,
                            RowNumber = 0,
                            Message =
                                $"Unsupported supplier note type '{document.InvoiceType}'. The matching GSTR-2B note is retained for reconciliation instead of being silently dropped."
                        });
                    continue;
                }
            }
            else
            {
                destination = NormalizeFlag(document.ReverseCharge) == "Y"
                    ? "RCM"
                    : "Purchase";
            }

            // Mark the 2B record as matched only after the accounting destination has
            // been resolved. This closes the old gap where typ=D was marked matched and
            // then skipped, which prevented it from appearing even as retained 2B evidence.
            if (match.Document is not null) matchedTwoB.Add(match.Document);

            var rcmTreatment = destination == "RCM"
                ? GetRcmTreatment(
                    document,
                    match.Document,
                    reconciliation)
                : string.Empty;

            var documentRoundOff = GetDocumentRoundOff(
                document,
                destination);

            for (var index = 0;
                 index < document.Items.Count;
                 index++)
            {
                var item = document.Items[index];
                var applicability = destination == "RCM"
                    ? rcmTreatment
                    : reconciliation.Applicability;

                var displayedTaxable = item.Taxable;
                var displayedIgst = item.Igst;
                var displayedCgst = item.Cgst;
                var displayedSgst = item.Sgst;

                if (reconciliation.Applicability == GstIncludedInCost &&
                    destination != "RCM")
                {
                    displayedTaxable =
                        item.Taxable +
                        item.Igst +
                        item.Cgst +
                        item.Sgst +
                        item.Cess;

                    displayedIgst = 0m;
                    displayedCgst = 0m;
                    displayedSgst = 0m;
                }

                var ledgers = BuildLedgers(
                    destination == "RCM"
                        ? ShownInTwoB
                        : reconciliation.Applicability,
                    item.Rate,
                    item.Igst);

                var putRoundOff = destination == "Purchase"
                    ? document.Items.Count == 1
                    : index == document.Items.Count - 1;

                records.Add(
                    new PurchaseImportRecord
                    {
                        Destination = destination,
                        ReconciliationStatus = reconciliation.Status,
                        GstApplicability = applicability,
                        SupplierGstin = document.SupplierGstin,
                        SupplierName = ToDisplayName(
                            supplierName),
                        DocumentType = destination == "RCM"
                            ? "RCM Invoice"
                            : document.DocumentType,
                        DocumentNumber = document.CurrentNumber,
                        DocumentDate = document.CurrentDate,
                        GstDate = destination == "RCM"
                            ? document.CurrentDate
                            : CalculateReconciledGstDate(
                                document,
                                match.Document),
                        SourceDocumentValue = document.InvoiceValue,
                        DocumentValue = document.InvoiceValue,
                        Rate = item.Rate,
                        RateKnown = item.RateKnown,
                        TaxableValue = displayedTaxable,
                        Igst = displayedIgst,
                        Cgst = displayedCgst,
                        Sgst = displayedSgst,
                        Cess = item.Cess,
                        RoundOff = putRoundOff
                            ? documentRoundOff
                            : 0m,
                        PurchaseLedger = ledgers.Purchase,
                        IgstLedger = ledgers.Igst,
                        CgstLedger = ledgers.Cgst,
                        SgstLedger = ledgers.Sgst,
                        SourceSection = document.Section,
                        IsAmendment = document.IsAmendment,
                        OriginalDocumentNumber = document.OriginalNumber,
                        OriginalDocumentDate = document.OriginalDate,
                        PlaceOfSupply = document.PlaceOfSupply,
                        ReverseCharge = document.ReverseCharge,
                        InvoiceType = document.InvoiceType,
                        ItcAvailability = reconciliation.ItcAvailability,
                        Source2AFile = document.Source.SourceName,
                        Source2BFile = match.Document?.Source.SourceName
                            ?? Path.GetFileName(
                                nameLookupSourcePath
                                ?? string.Empty)
                            ?? string.Empty,
                        TwoBInvoiceValue = match.Document?.InvoiceValue,
                        TwoBTaxableValue = match.Document?.Taxable,
                        TwoBIgst = match.Document?.Igst,
                        TwoBCgst = match.Document?.Cgst,
                        TwoBSgst = match.Document?.Sgst,
                        DifferenceInvoiceValue = reconciliation.DifferenceInvoiceValue,
                        DifferenceTaxableValue = reconciliation.DifferenceTaxable,
                        DifferenceIgst = reconciliation.DifferenceIgst,
                        DifferenceCgst = reconciliation.DifferenceCgst,
                        DifferenceSgst = reconciliation.DifferenceSgst,
                        DifferenceDetails = reconciliation.DifferenceDetails,
                        ClientGstin = gstin,
                        CompanyName = companyName,
                        ImportMode = "Auto Import",
                        TwoAReturnPeriod = document.Period,
                        TwoBReturnPeriod = reconciliation.TwoBPeriod,
                        ImsStatus = reconciliation.ImsStatus,
                        TwoBReason = reconciliation.Reason,
                        Source2APath = document.Source.SourcePath,
                        Source2BPath = match.Document?.Source.SourcePath
                            ?? nameLookupSourcePath
                            ?? string.Empty,
                        TwoBAcceptance = match.Document?.Acceptance
                            ?? string.Empty,
                        ItemSequence = index + 1
                    });
            }
        }

        AppendTwoBOnlyRecords(
            records,
            twoB,
            matchedTwoB,
            gstin,
            companyName,
            cancellationToken);

        AuditTwoBDebitNoteRetention(twoB.Records, records, issues);

        return records
            .OrderBy(record => DestinationOrder(
                record.Destination))
            .ThenBy(record => record.DocumentDate)
            .ThenBy(record => record.SupplierName)
            .ThenBy(record => record.DocumentNumber)
            .ThenBy(record => record.Rate)
            .ToArray();
    }

    private static void AppendTwoBOnlyRecords(
        ICollection<PurchaseImportRecord> records,
        TwoBExtraction twoB,
        IReadOnlySet<ParsedDocument> matchedTwoB,
        string gstin,
        string companyName,
        CancellationToken cancellationToken)
    {
        foreach (var document in twoB.Records.Where(record => !matchedTwoB.Contains(record)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var supplierName = document.SupplierName;
            var nameLookupSourcePath = string.Empty;
            if (string.IsNullOrWhiteSpace(supplierName) &&
                twoB.NamesByGstin.TryGetValue(document.SupplierGstin, out var mappedName))
            {
                supplierName = mappedName;
                if (twoB.NameSourcesByGstin.TryGetValue(document.SupplierGstin, out var mappedSourcePath))
                    nameLookupSourcePath = mappedSourcePath;
            }

            var applicability = PurchaseTreatmentPolicy.NotIncludedInPurchase;
            var documentRoundOff = GetDocumentRoundOff(document, "Purchase");

            for (var index = 0; index < document.Items.Count; index++)
            {
                var item = document.Items[index];
                records.Add(new PurchaseImportRecord
                {
                    // Retained as reconciliation evidence first. It enters normal Purchase
                    // accounting only after the user explicitly chooses Include 2B Only.
                    Destination = "2B Only",
                    ReconciliationStatus = "2B Only",
                    GstApplicability = applicability,
                    SupplierGstin = document.SupplierGstin,
                    SupplierName = ToDisplayName(supplierName),
                    DocumentType = document.DocumentType,
                    DocumentNumber = document.CurrentNumber,
                    DocumentDate = document.CurrentDate,
                    GstDate = CalculateTwoBOnlyGstDate(document),
                    SourceDocumentValue = document.InvoiceValue,
                    DocumentValue = document.InvoiceValue,
                    Rate = item.Rate,
                    RateKnown = item.RateKnown,
                    TaxableValue = item.Taxable,
                    Igst = item.Igst,
                    Cgst = item.Cgst,
                    Sgst = item.Sgst,
                    Cess = item.Cess,
                    RoundOff = index == document.Items.Count - 1 ? documentRoundOff : 0m,
                    PurchaseLedger = string.Empty,
                    IgstLedger = string.Empty,
                    CgstLedger = string.Empty,
                    SgstLedger = string.Empty,
                    SourceSection = document.Section,
                    IsAmendment = document.IsAmendment,
                    OriginalDocumentNumber = document.OriginalNumber,
                    OriginalDocumentDate = document.OriginalDate,
                    PlaceOfSupply = document.PlaceOfSupply,
                    ReverseCharge = document.ReverseCharge,
                    InvoiceType = document.InvoiceType,
                    ItcAvailability = document.ItcAvailability,
                    Source2AFile = string.Empty,
                    Source2APath = string.Empty,
                    Source2BFile = document.Source.SourceName,
                    Source2BPath = document.Source.SourcePath.Length > 0
                        ? document.Source.SourcePath
                        : nameLookupSourcePath ?? string.Empty,
                    TwoBInvoiceValue = document.InvoiceValue,
                    TwoBTaxableValue = document.Taxable,
                    TwoBIgst = document.Igst,
                    TwoBCgst = document.Cgst,
                    TwoBSgst = document.Sgst,
                    DifferenceInvoiceValue = null,
                    DifferenceTaxableValue = null,
                    DifferenceIgst = null,
                    DifferenceCgst = null,
                    DifferenceSgst = null,
                    DifferenceDetails = "Document exists only in GSTR-2B and is retained until explicitly included in Purchase accounting.",
                    ClientGstin = gstin,
                    CompanyName = companyName,
                    ImportMode = "Auto Import",
                    TwoAReturnPeriod = string.Empty,
                    TwoBReturnPeriod = document.Period,
                    ImsStatus = document.ImsStatus,
                    TwoBReason = document.Reason,
                    TwoBAcceptance = document.Acceptance,
                    ItemSequence = index + 1
                });
            }
        }
    }

    private static void AuditTwoBDebitNoteRetention(
        IReadOnlyList<ParsedDocument> sourceTwoB,
        IReadOnlyCollection<PurchaseImportRecord> outputRecords,
        ICollection<PurchaseImportIssue> issues)
    {
        var sourceDebitNotes = sourceTwoB
            .Where(document =>
                document.DocumentType.Equals("Note", StringComparison.OrdinalIgnoreCase) &&
                IsSupplierDebitNoteType(document.InvoiceType))
            .Select(document => DocumentKey(
                document.SupplierGstin,
                document.CurrentNumber,
                document.CurrentDate))
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sourceDebitNotes.Length == 0) return;

        var retained = outputRecords
            .Where(row => IsSupplierDebitNoteType(row.InvoiceType))
            .Select(row => DocumentKey(
                row.SupplierGstin,
                row.DocumentNumber,
                row.DocumentDate))
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = sourceDebitNotes
            .Where(key => !retained.Contains(key))
            .ToArray();

        if (missing.Length == 0) return;

        issues.Add(new PurchaseImportIssue
        {
            SourceFile = "GSTR-2B",
            SheetName = "Debit Note Reverse",
            RowNumber = 0,
            Message =
                $"GSTR-2B debit-note retention check failed: {missing.Length:N0} of {sourceDebitNotes.Length:N0} supplier debit note(s) were not retained in accounting or 2B evidence. Reimport is not complete until every typ=D document is accounted for."
        });
    }

    private static TwoBIndexes BuildTwoBIndexes(
        IReadOnlyList<ParsedDocument> records)
    {
        var current =
            new Dictionary<string, List<ParsedDocument>>(
                StringComparer.OrdinalIgnoreCase);

        var original =
            new Dictionary<string, List<ParsedDocument>>(
                StringComparer.OrdinalIgnoreCase);

        var noDate =
            new Dictionary<string, List<ParsedDocument>>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            AddIndex(
                current,
                DocumentKey(
                    record.SupplierGstin,
                    record.CurrentNumber,
                    record.CurrentDate),
                record);

            AddIndex(
                original,
                DocumentKey(
                    record.SupplierGstin,
                    record.OriginalNumber,
                    record.OriginalDate),
                record);

            AddIndex(
                noDate,
                DocumentKeyNoDate(
                    record.SupplierGstin,
                    record.CurrentNumber),
                record);
        }

        return new TwoBIndexes(
            current,
            original,
            noDate);
    }

    private static TwoBMatch FindTwoBMatch(
        ParsedDocument twoA,
        TwoBIndexes indexes)
    {
        var currentKey = DocumentKey(
            twoA.SupplierGstin,
            twoA.CurrentNumber,
            twoA.CurrentDate);

        var candidate = LatestTwoB(
            GetCandidates(
                indexes.Current,
                currentKey));

        if (candidate is not null)
        {
            return new TwoBMatch(
                candidate,
                "Current Exact");
        }

        var originalKey = DocumentKey(
            twoA.SupplierGstin,
            twoA.OriginalNumber.Length > 0
                ? twoA.OriginalNumber
                : twoA.CurrentNumber,
            twoA.OriginalDate ??
            twoA.CurrentDate);

        candidate = LatestTwoB(
            GetCandidates(
                indexes.Original,
                originalKey));

        if (candidate is not null)
        {
            return new TwoBMatch(
                candidate,
                !twoA.IsAmendment &&
                candidate.IsAmendment
                    ? "Later 2B Amendment"
                    : "Original to Amendment");
        }

        candidate = LatestTwoB(
            GetCandidates(
                indexes.Original,
                currentKey));

        if (candidate is not null)
        {
            return new TwoBMatch(
                candidate,
                "Later 2B Amendment");
        }

        var noDateKey = DocumentKeyNoDate(
            twoA.SupplierGstin,
            twoA.CurrentNumber);

        candidate = LatestTwoB(
            GetCandidates(
                indexes.NoDate,
                noDateKey));

        return candidate is null
            ? new TwoBMatch(null, string.Empty)
            : new TwoBMatch(
                candidate,
                "Invoice Date Difference");
    }

    private static ParsedDocument? LatestTwoB(
        IReadOnlyList<ParsedDocument> records) =>
        records
            .OrderBy(record => record.GeneratedDate)
            .ThenBy(record => record.Source.ModifiedUtc)
            .ThenBy(record => record.Acceptance == "Rejected")
            .LastOrDefault();

    private static ReconciliationResult Reconcile(
        ParsedDocument twoA,
        ParsedDocument? twoB,
        string matchType)
    {
        if (twoB is null)
        {
            return new ReconciliationResult
            {
                Applicability = NotShownInTwoB,
                Status = "Not Found in 2B",
                MatchType = string.Empty,
                DifferenceInvoiceValue = 0m,
                DifferenceTaxable = 0m,
                DifferenceIgst = 0m,
                DifferenceCgst = 0m,
                DifferenceSgst = 0m,
                DifferenceCess = 0m,
                DifferenceDetails = "Document exists in GSTR-2A but was not found in GSTR-2B."
            };
        }

        var result =
            new ReconciliationResult
            {
                Applicability = twoB.Acceptance == "Rejected"
                    ? GstIncludedInCost
                    : ShownInTwoB,
                MatchType = matchType,
                TwoBPeriod = twoB.Period,
                ImsStatus = twoB.ImsStatus,
                ItcAvailability = twoB.ItcAvailability,
                Reason = twoB.Reason,
                DifferenceInvoiceValue = Difference(
                    twoA.InvoiceValue,
                    twoB.InvoiceValue),
                DifferenceTaxable = Difference(
                    twoA.Taxable,
                    twoB.Taxable),
                DifferenceIgst = Difference(
                    twoA.Igst,
                    twoB.Igst),
                DifferenceCgst = Difference(
                    twoA.Cgst,
                    twoB.Cgst),
                DifferenceSgst = Difference(
                    twoA.Sgst,
                    twoB.Sgst),
                DifferenceCess = Difference(
                    twoA.Cess,
                    twoB.Cess)
            };

        var dateDifference =
            twoA.CurrentDate !=
            twoB.CurrentDate;

        var invoiceValueDifference =
            Math.Abs(
                result.DifferenceInvoiceValue) >
            AmountTolerance;

        var taxableDifference =
            Math.Abs(
                result.DifferenceTaxable) >
            AmountTolerance;

        var taxDifference =
            new[]
            {
                result.DifferenceIgst,
                result.DifferenceCgst,
                result.DifferenceSgst,
                result.DifferenceCess
            }.Any(value =>
                Math.Abs(value) >
                AmountTolerance);

        var posDifference = BothPresentAndDifferent(
            twoA.PlaceOfSupply,
            twoB.PlaceOfSupply);

        var reverseDifference = BothPresentAndDifferent(
            twoA.ReverseCharge,
            twoB.ReverseCharge);

        var invoiceTypeDifference = BothPresentAndDifferent(
            twoA.InvoiceType,
            twoB.InvoiceType);

        var amendmentMatch =
            twoB.IsAmendment ||
            matchType.Contains(
                "Amendment",
                StringComparison.OrdinalIgnoreCase);

        var prefix = amendmentMatch
            ? "Amended in 2B"
            : "Matched";

        result.Status = twoB.Acceptance == "Rejected"
            ? "Rejected in 2B"
            : dateDifference ||
              matchType == "Invoice Date Difference"
                ? prefix + " - Date Difference"
                : invoiceValueDifference
                    ? prefix + " - Invoice Value Difference"
                    : taxableDifference
                        ? prefix + " - Taxable Value Difference"
                        : taxDifference
                            ? prefix + " - Tax Difference"
                            : posDifference
                                ? prefix + " - Place of Supply Difference"
                                : reverseDifference
                                    ? prefix + " - Reverse Charge Difference"
                                    : invoiceTypeDifference
                                        ? prefix + " - Invoice Type Difference"
                                        : amendmentMatch
                                            ? "Amended in 2B - Matched"
                                            : "Matched in 2B";

        var details =
            new List<string>();

        AddDifference(
            details,
            "Invoice value",
            result.DifferenceInvoiceValue);
        AddDifference(
            details,
            "Taxable",
            result.DifferenceTaxable);
        AddDifference(
            details,
            "IGST",
            result.DifferenceIgst);
        AddDifference(
            details,
            "CGST",
            result.DifferenceCgst);
        AddDifference(
            details,
            "SGST",
            result.DifferenceSgst);
        AddDifference(
            details,
            "Cess",
            result.DifferenceCess);

        if (posDifference)
        {
            details.Add("Place of supply differs.");
        }

        if (reverseDifference)
        {
            details.Add("Reverse-charge flag differs.");
        }

        if (invoiceTypeDifference)
        {
            details.Add("Invoice/note type differs.");
        }

        result.DifferenceDetails =
            details.Count == 0
                ? result.Status
                : string.Join(
                    " ",
                    details);

        return result;
    }

    private static string GetRcmTreatment(
        ParsedDocument twoA,
        ParsedDocument? twoB,
        ReconciliationResult reconciliation)
    {
        if (twoB is null ||
            twoB.Acceptance == "Rejected" ||
            (NormalizeFlag(reconciliation.ItcAvailability).Length > 0 &&
             NormalizeFlag(reconciliation.ItcAvailability) != "Y"))
        {
            return "Reversed";
        }

        return twoB.Period.Length > 0 &&
               twoA.Period.Length > 0 &&
               string.CompareOrdinal(
                   twoB.Period,
                   twoA.Period) > 0
            ? "Claimed in Next Month"
            : "Claimed in Same Month";
    }

    private static decimal GetDocumentRoundOff(
        ParsedDocument document,
        string destination)
    {
        decimal calculated;

        if (destination == "RCM")
        {
            calculated = document.Items.Sum(
                item => item.Taxable);
        }
        else
        {
            calculated = document.Items.Sum(
                item =>
                    item.Taxable +
                    item.Igst +
                    item.Cgst +
                    item.Sgst +
                    item.Cess);
        }

        var roundOff = decimal.Round(
            document.InvoiceValue - calculated,
            2,
            MidpointRounding.ToEven);

        return Math.Abs(roundOff) < 0.005m
            ? 0m
            : roundOff;
    }

    private static PurchaseLedgerSet BuildLedgers(
        string applicability,
        decimal rate,
        decimal igst)
    {
        if (applicability == GstIncludedInCost)
        {
            return new PurchaseLedgerSet(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);
        }

        if (Math.Abs(rate) < 0.0000001m)
        {
            return new PurchaseLedgerSet(
                "Exempted Purchase",
                string.Empty,
                string.Empty,
                string.Empty);
        }

        var rateText = FormatRate(
            rate);

        var purchase =
            $"Purchase @ {rateText}%";

        var suffix = applicability == NotShownInTwoB
            ? " - Not shown in 2B"
            : string.Empty;

        if (Math.Abs(igst) > 0.005m)
        {
            return new PurchaseLedgerSet(
                purchase,
                $"Input IGST @ {rateText}%{suffix}",
                string.Empty,
                string.Empty);
        }

        var halfRate = FormatRate(
            rate / 2m);

        return new PurchaseLedgerSet(
            purchase,
            string.Empty,
            $"Input CGST @ {halfRate}%{suffix}",
            $"Input SGST @ {halfRate}%{suffix}");
    }

    private static DateTime? CalculateReconciledGstDate(
        ParsedDocument twoA,
        ParsedDocument? twoB)
    {
        // A matched/accepted GSTR-2B document is the ITC-period authority for a
        // normal Purchase row. Annual 2A/2B exports can carry a broad FY-end
        // wrapper period, so using the 2A source period here can collapse most
        // Purchase ITC into March and therefore corrupt both the generated GST
        // Journal and the GSTR-3B Books ITC column. Reuse the same supplier
        // filing-date rule used by retained 2B-only evidence whenever a matched
        // 2B document exists.
        if (twoB is not null)
        {
            var twoBDate = CalculateTwoBOnlyGstDate(twoB);
            if (twoBDate.HasValue)
            {
                return twoBDate;
            }
        }

        return CalculateGstDate(
            twoA.CurrentDate,
            twoA.FilingDate,
            twoA.Period);
    }

    private static DateTime? CalculateGstDate(
        DateTime? documentDate,
        DateTime? filingDate,
        string returnPeriod)
    {
        if (!documentDate.HasValue) return null;
        return PurchaseImportUtilities.CalculateWorkbookGstDate(
            documentDate.Value,
            filingDate,
            returnPeriod);
    }

    private static IReadOnlyList<PurchaseSourceManifestItem> BuildManifest(
        IReadOnlyList<PurchaseImportRecord> records,
        IReadOnlyList<ParsedDocument> effectiveTwoA,
        IReadOnlyList<SourceCandidate> twoASources,
        IReadOnlyList<SourceCandidate> twoBSources)
    {
        var usedTwoA = effectiveTwoA
            .Select(document => document.Source.SourcePath)
            .Where(path => path.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var reconciledTwoB = records
            .Where(record =>
                record.Source2BPath.Length > 0 &&
                record.TwoBAcceptance.Length > 0)
            .Select(record => record.Source2BPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var nameLookupTwoB = records
            .Where(record =>
                record.Source2BPath.Length > 0 &&
                record.TwoBAcceptance.Length == 0)
            .Select(record => record.Source2BPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result =
            new List<PurchaseSourceManifestItem>();

        foreach (var source in twoASources.Where(source =>
                     usedTwoA.Contains(
                         source.SourcePath)))
        {
            result.Add(
                ToManifest(
                    source,
                    "2A",
                    "Purchase Source"));
        }

        foreach (var source in twoBSources.Where(source =>
                     reconciledTwoB.Contains(source.SourcePath) ||
                     nameLookupTwoB.Contains(source.SourcePath)))
        {
            var usage = reconciledTwoB.Contains(
                source.SourcePath)
                ? "Reconciliation"
                : "Name Lookup";

            result.Add(
                ToManifest(
                    source,
                    "2B",
                    usage));
        }

        return result
            .GroupBy(
                item => string.Join(
                    "|",
                    item.FileType,
                    item.SourcePath,
                    item.InternalJsonName,
                    item.Usage),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.FileType)
            .ThenBy(item => item.ReturnPeriod)
            .ThenBy(item => item.SourceFile)
            .ToArray();
    }

    private static PurchaseSourceManifestItem ToManifest(
        SourceCandidate source,
        string fileType,
        string usage) =>
        new()
        {
            FileType = fileType,
            Usage = usage,
            ReturnPeriod = source.Period,
            SourceFile = source.SourceName,
            SourcePath = source.SourcePath,
            InternalJsonName = source.InternalName,
            SourceOrigin = source.SourceOrigin,
            SourceBranch = source.Branch,
            SourceScope = source.Scope,
            ReadOnlySource = true,
            VerificationStatus = source.VerificationStatus,
            EmbeddedTimestamp =
                source.EmbeddedTimestamp?.ToString("s")
                ?? string.Empty,
            PayloadHash = source.PayloadHash
        };

    private static IReadOnlyList<string> BuildWarnings(
        PurchaseAutoImportRequest request,
        IReadOnlyList<SourceCandidate> twoA,
        IReadOnlyList<SourceCandidate> twoB,
        PurchaseSourceVerification verification,
        IReadOnlyList<ParsedDocument> effectiveTwoA,
        IReadOnlyList<ParsedDocument> twoBRecords)
    {
        var warnings =
            new List<string>();

        if (twoA.Count == 0)
        {
            warnings.Add(
                "No valid GSTR-2A JSON/ZIP source was found for this GSTIN and Financial Year.");
        }
        else if (effectiveTwoA.Count == 0)
        {
            warnings.Add(
                "GSTR-2A source files were found, but no documents belong to the selected Financial Year.");
        }

        if (twoB.Count == 0)
        {
            warnings.Add(
                "No GSTR-2B JSON/ZIP source was found. Purchase rows were imported as Not shown in 2B.");
        }
        else if (twoBRecords.Count == 0)
        {
            warnings.Add(
                "GSTR-2B files were found, but no supported reconciliation documents were read.");
        }

        if (verification.TwoA.SourceOrigin == "Legacy" ||
            verification.TwoB.SourceOrigin == "Legacy")
        {
            warnings.Add(
                "One or more forms were loaded from the Windows Downloads fallback because the preferred E-returns branch did not contain that form.");
        }

        foreach (var form in new[] { verification.TwoA, verification.TwoB })
        {
            if (form.Status.Contains("Monthly Difference", StringComparison.OrdinalIgnoreCase))
                warnings.Add(
                    $"GSTR-{form.Form} optional monthly snapshots differ from the annual primary: " +
                    $"{form.MissingInMonthlyCount} missing, {form.ExtraInMonthlyCount} extra, " +
                    $"{form.DifferentDocumentCount} changed document(s). Annual data was used; review source differences if needed.");
        }

        if (request.SaveSourceFiles)
        {
            warnings.Add(
                "Save Files is a placeholder only. No source file was moved, copied, renamed, deleted or extracted.");
        }

        return warnings;
    }

    private static PurchaseImportSummary BuildSummary(
        IReadOnlyList<PurchaseImportRecord> records,
        int sourceTwoARows,
        int sourceTwoBRows,
        int invalidRows)
    {
        return new PurchaseImportSummary
        {
            Source2ARows = sourceTwoARows,
            Source2BRows = sourceTwoBRows,
            PurchaseRows = records.Count(record =>
                record.Destination == "Purchase"),
            RcmRows = records.Count(record =>
                record.Destination == "RCM"),
            DebitNoteRows = records.Count(record =>
                record.Destination == PurchaseTreatmentPolicy.DebitNoteDestination),
            DebitNoteReverseRows = records.Count(record =>
                record.Destination == PurchaseTreatmentPolicy.DebitNoteReverseDestination),
            MatchedRows = records.Count(record =>
                record.ReconciliationStatus.Contains(
                    "Matched",
                    StringComparison.OrdinalIgnoreCase) &&
                !record.ReconciliationStatus.Contains(
                    "Difference",
                    StringComparison.OrdinalIgnoreCase)),
            TwoAOnlyRows = records.Count(record =>
                record.ReconciliationStatus == "Not Found in 2B"),
            TwoBOnlyRows = records.Count(record =>
                record.ReconciliationStatus == "2B Only"),
            DifferenceRows = records.Count(record =>
                record.ReconciliationStatus.Contains(
                    "Difference",
                    StringComparison.OrdinalIgnoreCase) ||
                record.ReconciliationStatus == "Rejected in 2B"),
            DuplicateRowsSkipped = 0,
            InvalidRowsSkipped = invalidRows
        };
    }

    private static string DetectCompanyName(
        string targetGstin,
        IEnumerable<SourceCandidate> sources)
    {
        var tradeName = string.Empty;
        var legalName = string.Empty;

        foreach (var source in sources)
        {
            FindClientNames(
                source.Payload,
                targetGstin,
                ref tradeName,
                ref legalName);
        }

        return tradeName.Length > 0
            ? tradeName
            : legalName;
    }

    private static void FindClientNames(
        JsonNode? node,
        string targetGstin,
        ref string tradeName,
        ref string legalName)
    {
        if (node is JsonObject obj)
        {
            var candidate = NormalizeGstin(
                FirstText(
                    obj,
                    "gstin",
                    "ctin",
                    "recipientGstin",
                    "supplierGstin"));

            if (candidate == targetGstin)
            {
                var trade = FirstText(
                    obj,
                    "trdnm",
                    "tradeName",
                    "trade_name",
                    "tradeNm");

                var legal = FirstText(
                    obj,
                    "lgnm",
                    "legalName",
                    "legal_name",
                    "legalNm");

                if (trade.Length > 0)
                {
                    tradeName = trade;
                }

                if (legal.Length > 0)
                {
                    legalName = legal;
                }
            }

            foreach (var child in obj)
            {
                FindClientNames(
                    child.Value,
                    targetGstin,
                    ref tradeName,
                    ref legalName);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                FindClientNames(
                    child,
                    targetGstin,
                    ref tradeName,
                    ref legalName);
            }
        }
    }

    private static DirectoryInfo? FindFinancialYearRoot(
        DirectoryInfo root,
        string financialYear)
    {
        var match = Regex.Match(
            financialYear,
            @"^(\d{4})-(\d{2})$");

        if (!match.Success)
        {
            return null;
        }

        var startYear = int.Parse(
            match.Groups[1].Value,
            CultureInfo.InvariantCulture);

        var endYear = startYear + 1;

        var names = new[]
        {
            $"{startYear:0000}-{endYear:0000}",
            financialYear
        };

        if (names.Contains(
                root.Name,
                StringComparer.OrdinalIgnoreCase))
        {
            return root;
        }

        foreach (var name in names)
        {
            var child = CaseInsensitiveChild(
                root,
                name);

            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private static DirectoryInfo? CaseInsensitiveChild(
        DirectoryInfo parent,
        string wanted)
    {
        try
        {
            return parent
                .EnumerateDirectories()
                .FirstOrDefault(directory =>
                    directory.Name.Equals(
                        wanted,
                        StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static SourceScope GetSourceScope(
        DirectoryInfo clientRoot,
        FileInfo file,
        string financialYear)
    {
        var relative = Path.GetRelativePath(
            clientRoot.FullName,
            file.FullName);

        var parts = relative.Split(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        if (parts.Any(part =>
                part.Equals(
                    "Annual",
                    StringComparison.OrdinalIgnoreCase)))
        {
            return new SourceScope(
                "Annual",
                string.Empty);
        }

        var month = parts
            .Select(part => MonthFolderNames.TryGetValue(
                part,
                out var monthNumber)
                ? monthNumber
                : 0)
            .FirstOrDefault(value => value > 0);

        if (month == 0)
        {
            return new SourceScope(
                "Other",
                string.Empty);
        }

        var startYear = int.Parse(
            financialYear[..4],
            CultureInfo.InvariantCulture);

        var year = month >= 4
            ? startYear
            : startYear + 1;

        return new SourceScope(
            "Monthly",
            $"{year:0000}{month:00}");
    }

    private static SourceCandidate? LatestSource(
        IEnumerable<SourceCandidate> candidates) =>
        candidates
            .OrderBy(candidate => candidate.SelectionTimestamp)
            .ThenBy(candidate => candidate.ModifiedUtc)
            .ThenBy(candidate => candidate.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.InternalName, StringComparer.OrdinalIgnoreCase)
            .LastOrDefault();

    private static List<SourceCandidate> LatestMonthlySources(
        IEnumerable<SourceCandidate> candidates) =>
        candidates
            .Where(candidate =>
                candidate.Scope == "Monthly" &&
                candidate.MonthKey.Length == 6)
            .GroupBy(
                candidate => candidate.MonthKey,
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key)
            .Select(group => LatestSource(group))
            .Where(candidate => candidate is not null)
            .Cast<SourceCandidate>()
            .ToList();

    private static string NormalizeGstin(
        string value) =>
        Regex.Replace(
            value.ToUpperInvariant(),
            @"[\s\-\.'\r\n\t]",
            string.Empty);

    private static string NormalizeDocumentNumber(
        string value) =>
        Regex.Replace(
            value.ToUpperInvariant(),
            "[^A-Z0-9]",
            string.Empty);

    private static string NormalizeFlag(
        string value) =>
        Regex.Replace(
            value.ToUpperInvariant(),
            "[^A-Z0-9]",
            string.Empty);

    private static bool IsSupplierCreditNoteType(string value)
    {
        var normalized = NormalizeFlag(value);
        return normalized is "C" or "CN" or "CR" or "CREDIT" or "CREDITNOTE";
    }

    private static bool IsSupplierDebitNoteType(string value)
    {
        var normalized = NormalizeFlag(value);
        return normalized is "D" or "DN" or "DR" or "DEBIT" or "DEBITNOTE";
    }

    private static string NormalizeFinancialYear(
        string value)
    {
        var match = Regex.Match(
            value.Trim(),
            @"^(\d{4})\s*[-/]\s*(\d{2}|\d{4})$");

        if (!match.Success)
        {
            return string.Empty;
        }

        var startYear = int.Parse(
            match.Groups[1].Value,
            CultureInfo.InvariantCulture);

        var endRaw = int.Parse(
            match.Groups[2].Value,
            CultureInfo.InvariantCulture);

        var endYear = endRaw >= 1000
            ? endRaw
            : startYear / 100 * 100 + endRaw;

        return endYear == startYear + 1
            ? $"{startYear:0000}-{endYear % 100:00}"
            : string.Empty;
    }

    private static string NormalizePeriod(
        string value)
    {
        var text = value.Trim();

        if (text.Length == 0)
        {
            return string.Empty;
        }

        var digits = Regex.Replace(
            text,
            "[^0-9]",
            string.Empty);

        if (digits.Length == 6)
        {
            var firstTwo = int.Parse(
                digits[..2],
                CultureInfo.InvariantCulture);

            var lastFour = int.Parse(
                digits[2..],
                CultureInfo.InvariantCulture);

            if (firstTwo is >= 1 and <= 12 &&
                lastFour is >= 2000 and <= 2999)
            {
                return $"{lastFour:0000}{firstTwo:00}";
            }

            var firstFour = int.Parse(
                digits[..4],
                CultureInfo.InvariantCulture);

            var lastTwo = int.Parse(
                digits[4..],
                CultureInfo.InvariantCulture);

            if (firstFour is >= 2000 and <= 2999 &&
                lastTwo is >= 1 and <= 12)
            {
                return $"{firstFour:0000}{lastTwo:00}";
            }
        }

        return string.Empty;
    }

    private static bool DateInFinancialYear(
        DateTime? date,
        string financialYear)
    {
        if (!date.HasValue ||
            financialYear.Length != 7)
        {
            return false;
        }

        var startYear = int.Parse(
            financialYear[..4],
            CultureInfo.InvariantCulture);

        var start = new DateTime(
            startYear,
            4,
            1);

        var end = new DateTime(
            startYear + 1,
            3,
            31);

        return date.Value.Date >= start &&
               date.Value.Date <= end;
    }

    private static DateTime? ParseDate(
        string value)
    {
        var formats = new[]
        {
            "dd-MM-yyyy",
            "dd/MM/yyyy",
            "yyyy-MM-dd",
            "dd-MMM-yyyy",
            "dd-MMM-yy",
            "dd MMM yyyy",
            "dd MMM yy",
            "dd.MM.yyyy"
        };

        return DateTime.TryParseExact(
            value.Trim(),
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed.Date
            : null;
    }

    private static DateTime? EmbeddedTimestamp(
        JsonObject payload)
    {
        var winRequest = Object(
            payload["winreq"]);

        if (winRequest is not null)
        {
            var date = FirstText(
                winRequest,
                "Respdate",
                "respdate");

            var time = FirstText(
                winRequest,
                "resptime",
                "RespTime");

            var parsed = ParseSourceDateTime(
                date,
                time);

            if (parsed.HasValue)
            {
                return parsed;
            }
        }

        var data = Object(
            payload["data"]);

        return ParseSourceDateTime(
            FirstText(
                data ?? payload,
                "gendt"),
            string.Empty);
    }

    private static DateTime? ParseSourceDateTime(
        string date,
        string time)
    {
        var combined = string.Join(
            " ",
            new[]
            {
                date.Trim(),
                time.Trim()
            }.Where(value => value.Length > 0));

        if (combined.Length == 0)
        {
            return null;
        }

        var formats = new[]
        {
            "dd-MMM-yyyy HH:mm:ss",
            "dd-MMM-yyyy HH:mm",
            "dd-MM-yyyy HH:mm:ss",
            "dd-MM-yyyy HH:mm",
            "dd/MM/yyyy HH:mm:ss",
            "dd/MM/yyyy HH:mm",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "dd-MMM-yy HH:mm:ss",
            "dd-MMM-yy HH:mm",
            "dd-MMM-yyyy",
            "dd-MM-yyyy",
            "dd/MM/yyyy",
            "yyyy-MM-dd",
            "dd-MMM-yy"
        };

        return DateTime.TryParseExact(
            combined,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;
    }

    private static string PayloadHash(
        JsonNode payload)
    {
        var canonical = CanonicalJson(
            payload);

        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        canonical)))
            .ToLowerInvariant();
    }

    private static string CanonicalJson(
        JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        if (node is JsonObject obj)
        {
            return "{" +
                   string.Join(
                       ",",
                       obj.OrderBy(
                               pair => pair.Key,
                               StringComparer.Ordinal)
                           .Select(pair =>
                               JsonSerializer.Serialize(
                                   pair.Key) +
                               ":" +
                               CanonicalJson(
                                   pair.Value))) +
                   "}";
        }

        if (node is JsonArray array)
        {
            return "[" +
                   string.Join(
                       ",",
                       array.Select(
                           CanonicalJson)) +
                   "]";
        }

        return node.ToJsonString(
            new JsonSerializerOptions
            {
                WriteIndented = false
            });
    }

    private static JsonObject CloneObject(
        JsonObject source) =>
        JsonNode.Parse(
            source.ToJsonString())
        ?.AsObject()
        ?? new JsonObject();

    private static JsonObject? Object(
        JsonNode? node) =>
        node as JsonObject;

    private static JsonArray? Array(
        JsonNode? node) =>
        node as JsonArray;

    private static string Text(
        JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(
                    out var text))
            {
                return text.Trim();
            }

            if (value.TryGetValue<decimal>(
                    out var decimalValue))
            {
                return decimalValue.ToString(
                    CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<double>(
                    out var doubleValue))
            {
                return doubleValue.ToString(
                    CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<long>(
                    out var longValue))
            {
                return longValue.ToString(
                    CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<bool>(
                    out var boolValue))
            {
                return boolValue
                    ? "true"
                    : "false";
            }
        }

        return node.ToJsonString().Trim('"');
    }

    private static string FirstText(
        JsonObject mapping,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = Text(
                mapping[key]);

            if (value.Length > 0)
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static decimal DecimalValue(
        JsonNode? node)
    {
        var text = Text(node)
            .Replace(
                ",",
                string.Empty,
                StringComparison.Ordinal);

        text = Regex.Replace(
            text,
            "[^0-9.\\-]",
            string.Empty);

        return decimal.TryParse(
            text,
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 0m;
    }

    private static string CanonicalAmount(
        decimal value) =>
        decimal.Round(
                value,
                4,
                MidpointRounding.ToEven)
            .ToString(
                "0.####",
                CultureInfo.InvariantCulture);

    private static string FormatDate(
        DateTime? value) =>
        value?.ToString(
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture)
        ?? string.Empty;

    private static string FormatRate(
        decimal rate) =>
        Math.Abs(
            rate - decimal.Round(
                rate,
                0,
                MidpointRounding.ToEven)) <
        0.0000001m
            ? decimal.Round(
                    rate,
                    0,
                    MidpointRounding.ToEven)
                .ToString(
                    "0",
                    CultureInfo.InvariantCulture)
            : rate.ToString(
                    "0.####",
                    CultureInfo.InvariantCulture)
                .TrimEnd('0')
                .TrimEnd('.');

    private static string ToDisplayName(
        string? value)
    {
        var text = Regex.Replace(
                value ?? string.Empty,
                @"\s+",
                " ")
            .Trim();

        return text.Length == 0
            ? string.Empty
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                text.ToLowerInvariant());
    }

    private static decimal Difference(
        decimal left,
        decimal right) =>
        decimal.Round(
            left - right,
            2,
            MidpointRounding.ToEven);

    private static bool BothPresentAndDifferent(
        string left,
        string right) =>
        NormalizeFlag(left).Length > 0 &&
        NormalizeFlag(right).Length > 0 &&
        NormalizeFlag(left) != NormalizeFlag(right);

    private static void AddDifference(
        ICollection<string> details,
        string label,
        decimal difference)
    {
        if (Math.Abs(difference) <= AmountTolerance)
        {
            return;
        }

        details.Add(
            $"{label} difference {difference:0.00}.");
    }

    private static string DocumentKey(
        string gstin,
        string number,
        DateTime? date)
    {
        if (NormalizeGstin(gstin).Length == 0 ||
            NormalizeDocumentNumber(number).Length == 0 ||
            !date.HasValue)
        {
            return string.Empty;
        }

        return string.Join(
            "|",
            NormalizeGstin(gstin),
            NormalizeDocumentNumber(number),
            FormatDate(date));
    }

    private static string DocumentKeyNoDate(
        string gstin,
        string number)
    {
        if (NormalizeGstin(gstin).Length == 0 ||
            NormalizeDocumentNumber(number).Length == 0)
        {
            return string.Empty;
        }

        return string.Join(
            "|",
            NormalizeGstin(gstin),
            NormalizeDocumentNumber(number));
    }

    private static void AddIndex(
        IDictionary<string, List<ParsedDocument>> index,
        string key,
        ParsedDocument record)
    {
        if (key.Length == 0)
        {
            return;
        }

        if (!index.TryGetValue(
                key,
                out var records))
        {
            records = [];
            index[key] = records;
        }

        records.Add(record);
    }

    private static IReadOnlyList<ParsedDocument> GetCandidates(
        IReadOnlyDictionary<string, List<ParsedDocument>> index,
        string key) =>
        key.Length > 0 &&
        index.TryGetValue(
            key,
            out var records)
            ? records
            : [];

    private static int DestinationOrder(
        string destination) =>
        destination switch
        {
            "Purchase" => 0,
            "RCM" => 1,
            "Debit Note" => 2,
            "Debit Note Reverse" => 3,
            _ => 4
        };

    private sealed record JsonPayload(
        string InternalName,
        JsonObject Payload);

    private sealed record PayloadClassification(
        string SourceType,
        string Gstin,
        string Period);

    private sealed record SourceScope(
        string Scope,
        string MonthKey);

    private sealed record FormResolution(
        List<SourceCandidate> Sources,
        PurchaseFormVerification Verification);

    private sealed record DownloadsFallback(
        List<SourceCandidate> TwoA,
        List<SourceCandidate> TwoB);

    private sealed record SourceComparison(
        bool Matched,
        int AnnualDocumentCount,
        int MonthlyDocumentCount,
        int MissingInMonthlyCount,
        int ExtraInMonthlyCount,
        int DifferentDocumentCount,
        IReadOnlyList<string> MissingInMonthly,
        IReadOnlyList<string> ExtraInMonthly,
        IReadOnlyList<string> DifferentDocuments);

    private sealed record ItemRow(
        decimal Rate,
        bool RateKnown,
        decimal Taxable,
        decimal Igst,
        decimal Cgst,
        decimal Sgst,
        decimal Cess);

    private sealed record TwoBExtraction(
        List<ParsedDocument> Records,
        Dictionary<string, string> NamesByGstin,
        Dictionary<string, string> NameSourcesByGstin);

    private sealed record TwoBIndexes(
        Dictionary<string, List<ParsedDocument>> Current,
        Dictionary<string, List<ParsedDocument>> Original,
        Dictionary<string, List<ParsedDocument>> NoDate);

    private sealed record TwoBMatch(
        ParsedDocument? Document,
        string MatchType);

    private sealed class SourceCandidate
    {
        public required string SourceType { get; init; }
        public required string SourcePath { get; init; }
        public required string SourceName { get; init; }
        public required string InternalName { get; init; }
        public required string Period { get; init; }
        public required JsonObject Payload { get; init; }
        public DateTime ModifiedUtc { get; init; }
        public DateTime? EmbeddedTimestamp { get; init; }
        public DateTime SelectionTimestamp { get; init; }
        public required string PayloadHash { get; init; }
        public required string SourceOrigin { get; init; }
        public required string Branch { get; init; }
        public required string Scope { get; init; }
        public required string MonthKey { get; init; }
        public bool IsAnnualAggregate { get; init; }
        public bool ReadOnly { get; init; }
        public string VerificationStatus { get; set; } = string.Empty;

        public SourceCandidate CopyWith(
            JsonObject payload,
            string scope) =>
            new()
            {
                SourceType = SourceType,
                SourcePath = SourcePath,
                SourceName = SourceName,
                InternalName = InternalName,
                Period = Period,
                Payload = payload,
                ModifiedUtc = ModifiedUtc,
                EmbeddedTimestamp = EmbeddedTimestamp,
                SelectionTimestamp = SelectionTimestamp,
                PayloadHash = PayloadHash,
                SourceOrigin = SourceOrigin,
                Branch = Branch,
                Scope = scope,
                MonthKey = MonthKey,
                IsAnnualAggregate = IsAnnualAggregate,
                ReadOnly = ReadOnly,
                VerificationStatus = VerificationStatus
            };
    }

    private sealed class ParsedDocument
    {
        public required SourceCandidate Source { get; init; }
        public required string SupplierGstin { get; init; }
        public required string SupplierName { get; init; }
        public required string Section { get; init; }
        public bool IsAmendment { get; init; }
        public required string Acceptance { get; init; }
        public required string DocumentType { get; init; }
        public required string CurrentNumber { get; init; }
        public DateTime? CurrentDate { get; init; }
        public required string OriginalNumber { get; init; }
        public DateTime? OriginalDate { get; init; }
        public decimal InvoiceValue { get; init; }
        public decimal Taxable { get; init; }
        public decimal Igst { get; init; }
        public decimal Cgst { get; init; }
        public decimal Sgst { get; init; }
        public decimal Cess { get; init; }
        public required string PlaceOfSupply { get; init; }
        public required string ReverseCharge { get; init; }
        public required string InvoiceType { get; init; }
        public required string ImsStatus { get; init; }
        public required string ItcAvailability { get; init; }
        public required string Reason { get; init; }
        public DateTime? FilingDate { get; init; }
        public required string Period { get; init; }
        public DateTime? GeneratedDate { get; init; }
        public bool IsQuarterlyFiling { get; init; }
        public required List<ItemRow> Items { get; init; }
    }

    private sealed class ReconciliationResult
    {
        public string Applicability { get; init; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string MatchType { get; init; } = string.Empty;
        public string TwoBPeriod { get; init; } = string.Empty;
        public string ImsStatus { get; init; } = string.Empty;
        public string ItcAvailability { get; init; } = string.Empty;
        public string Reason { get; init; } = string.Empty;
        public decimal DifferenceInvoiceValue { get; init; }
        public decimal DifferenceTaxable { get; init; }
        public decimal DifferenceIgst { get; init; }
        public decimal DifferenceCgst { get; init; }
        public decimal DifferenceSgst { get; init; }
        public decimal DifferenceCess { get; init; }
        public string DifferenceDetails { get; set; } = string.Empty;
    }
}
