using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AccountingAutomationHub.Application.Sales;
using AccountingAutomationHub.Domain.Sales;
using AccountingAutomationHub.Infrastructure.Purchases;
using AccountingAutomationHub.Infrastructure.Imports;
using AccountingAutomationHub.Infrastructure.Spreadsheets;

namespace AccountingAutomationHub.Infrastructure.Sales;

public sealed class SalesAutoImportService :
    ISalesAutoImportService
{
    private static readonly string[] SalesSections =
    [
        "b2b",
        "b2ba",
        "b2cl",
        "b2cla",
        "b2cs",
        "b2csa",
        "cdnr",
        "cdnra",
        "cdnur",
        "cdnura",
        "exp",
        "expa"
    ];

    private static readonly HashSet<string> TwoBSections =
        new(
            [
                "b2b",
                "b2ba",
                "cdn",
                "cdnr",
                "cdnra",
                "cdna"
            ],
            StringComparer.OrdinalIgnoreCase);

    public Task<SalesAutoImportResult> ImportAsync(
        SalesAutoImportRequest request,
        IProgress<SalesImportProgress>? progress = null,
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

    private SalesAutoImportResult Import(
        SalesAutoImportRequest request,
        IProgress<SalesImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var gstin = NormalizeGstin(
            request.ClientGstin);

        if (gstin.Length != 15)
        {
            throw new InvalidOperationException(
                "The active client must have a valid 15-character GSTIN before automatic Sales import.");
        }

        var financialYear = NormalizeFinancialYear(
            request.FinancialYear);

        if (financialYear.Length == 0)
        {
            throw new InvalidOperationException(
                "The active client must have a valid Financial Year such as 2025-26.");
        }

        var automationRoots = request.AutomationFolderPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => new DirectoryInfo(path.Trim()))
            .Where(directory => directory.Exists)
            .GroupBy(directory => directory.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        DirectoryInfo? root = null;
        if (!string.IsNullOrWhiteSpace(request.EReturnsRootPath))
        {
            var candidate = new DirectoryInfo(request.EReturnsRootPath.Trim());
            if (candidate.Exists) root = candidate;
        }

        if (root is null && automationRoots.Length == 0)
        {
            throw new DirectoryNotFoundException(
                "Neither the saved E-returns root nor a linked Automation Folder is available for automatic Sales import.");
        }

        var issues = new List<SalesImportIssue>();

        progress?.Report(
            new SalesImportProgress(
                8,
                "Resolving monthly GSTR-1 sources"));

        // Fix38: the shared Sales/Purchase watcher indexes GSTIN/FY sources once. Monthly
        // Sales import consumes those cached paths first; directory traversal remains a
        // fail-safe fallback when the source index is not ready yet.
        var cachedSalesSources = ReadCachedSalesSources(
            SalesPurchaseSourceCache.GetPaths("Sales", gstin, financialYear),
            gstin,
            financialYear,
            issues,
            cancellationToken);

        var sourceResolution = cachedSalesSources.Count > 0
            ? new SalesSourceResolution(
                LatestPerPeriod(cachedSalesSources),
                new SalesSourceVerification
                {
                    EReturnsRoot = root?.FullName ?? request.EReturnsRootPath,
                    SourceOrigin = "GSTIN Cache",
                    SourceBranch = "Shared Sales/Purchase Watcher",
                    Status = "Cached Monthly GSTR-1 Selected",
                    SelectedSources = cachedSalesSources.Select(source => source.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    SelectedPeriods = cachedSalesSources.Select(source => source.Period).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToArray()
                })
            : root is null
                ? new SalesSourceResolution(
                    [],
                    new SalesSourceVerification
                    {
                        EReturnsRoot = request.EReturnsRootPath,
                        SourceOrigin = "E-returns",
                        Status = "E-returns Not Available"
                    })
                : ResolveSalesSources(
                    root,
                    gstin,
                    financialYear,
                    issues,
                    cancellationToken);

        var selectedSources = sourceResolution.Sources;

        if (selectedSources.Count == 0 && automationRoots.Length > 0)
        {
            progress?.Report(new SalesImportProgress(18, "Checking linked Automation Folder GSTR-1 sources"));
            selectedSources = ReadAutomationFolderSalesSources(
                automationRoots, gstin, financialYear, issues, cancellationToken);
            if (selectedSources.Count > 0)
            {
                sourceResolution = new SalesSourceResolution(
                    selectedSources,
                    new SalesSourceVerification
                    {
                        EReturnsRoot = root?.FullName ?? request.EReturnsRootPath,
                        SourceOrigin = "Automation Folder",
                        SourceBranch = "Linked Client/FY",
                        ClientRoot = string.Join("; ", automationRoots.Select(item => item.FullName)),
                        Status = "Linked Monthly GSTR-1 Selected",
                        SelectedSources = selectedSources.Select(source => source.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                        SelectedPeriods = selectedSources.Select(source => source.Period).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToArray()
                    });
            }
        }

        if (selectedSources.Count == 0 &&
            request.SearchWindowsDownloads)
        {
            progress?.Report(
                new SalesImportProgress(
                    23,
                    "Checking Windows Downloads fallback"));

            selectedSources = ReadDownloadsSalesSources(
                gstin,
                financialYear,
                issues,
                cancellationToken);

            if (selectedSources.Count > 0)
            {
                sourceResolution =
                    new SalesSourceResolution(
                        selectedSources,
                        new SalesSourceVerification
                        {
                            EReturnsRoot = root?.FullName ?? request.EReturnsRootPath,
                            SourceOrigin = "Legacy",
                            SourceBranch = "Windows Downloads",
                            Status = "Legacy Fallback",
                            SelectedSources = selectedSources
                                .Select(source => source.SourcePath)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray(),
                            SelectedPeriods = selectedSources
                                .Select(source => source.Period)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(value => value)
                                .ToArray()
                        });
            }
        }

        progress?.Report(
            new SalesImportProgress(
                38,
                "Resolving GST recipient master names"));

        var customerMaster = ResolveCustomerMaster(
            root,
            automationRoots,
            gstin,
            financialYear,
            request.SearchWindowsDownloads,
            issues,
            cancellationToken);

        progress?.Report(
            new SalesImportProgress(
                51,
                "Reading GSTR-2B customer-name fallback"));

        var twoBNames = ResolveTwoBNames(
            root,
            automationRoots,
            gstin,
            financialYear,
            request.SearchWindowsDownloads,
            issues,
            cancellationToken);

        var verification = sourceResolution.Verification;
        verification.CustomerMasterStatus = customerMaster.Status;
        verification.CustomerMasterSourceOrigin =
            customerMaster.SourceOrigin;
        verification.CustomerMasterSourceBranch =
            customerMaster.SourceBranch;
        verification.CustomerMasterSourcePath =
            customerMaster.SourcePath;
        verification.CustomerMasterSourceHash =
            customerMaster.SourceHash;
        verification.CustomerMasterRecords =
            customerMaster.Names.Count;

        progress?.Report(
            new SalesImportProgress(
                66,
                "Building Sales and Credit Note records"));

        var companyName = request.CompanyName.Trim();

        var records = BuildSalesRecords(
            gstin,
            companyName,
            financialYear,
            selectedSources,
            customerMaster,
            twoBNames,
            issues,
            cancellationToken);

        var manifest = BuildManifest(
            selectedSources,
            customerMaster);

        var summary = BuildSummary(
            records,
            issues.Count);

        var warnings = BuildWarnings(
            request,
            selectedSources,
            customerMaster,
            twoBNames,
            summary);

        progress?.Report(
            new SalesImportProgress(
                100,
                "Automatic Sales import completed"));

        return new SalesAutoImportResult
        {
            CompanyName = companyName,
            ClientGstin = gstin,
            FinancialYear = financialYear,
            Records = records,
            Issues = issues,
            Warnings = warnings,
            Summary = summary,
            Verification = verification,
            SourceManifest = manifest
        };
    }

    private static SalesSourceResolution ResolveSalesSources(
        DirectoryInfo eReturnsRoot,
        string gstin,
        string financialYear,
        List<SalesImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var financialYearRoot = eReturnsRoot is null
            ? null
            : FindFinancialYearRoot(
                eReturnsRoot,
                financialYear);

        if (financialYearRoot is null)
        {
            return new SalesSourceResolution(
                [],
                new SalesSourceVerification
                {
                    EReturnsRoot = eReturnsRoot?.FullName ?? string.Empty,
                    SourceOrigin = "E-returns",
                    Status = "Financial Year Folder Not Found"
                });
        }

        foreach (var branchName in new[]
                 {
                     "imported",
                     "downloaded"
                 })
        {
            var branchRoot = FindChildDirectory(
                financialYearRoot,
                branchName);

            var clientRoot = branchRoot is null
                ? null
                : FindChildDirectory(
                    branchRoot,
                    gstin);

            if (clientRoot is null)
            {
                continue;
            }

            var candidates = CollectSalesCandidates(
                clientRoot,
                gstin,
                financialYear,
                branchName,
                issues,
                cancellationToken);

            if (candidates.Count == 0)
            {
                continue;
            }

            var selected = LatestPerPeriod(
                candidates);

            return new SalesSourceResolution(
                selected,
                new SalesSourceVerification
                {
                    EReturnsRoot = eReturnsRoot?.FullName ?? string.Empty,
                    SourceOrigin = "E-returns",
                    SourceBranch = branchName,
                    ClientRoot = clientRoot.FullName,
                    Status = "Monthly GSTR-1 Selected",
                    SelectedSources = selected
                        .Select(source => source.SourcePath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    SelectedPeriods = selected
                        .Select(source => source.Period)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(value => value)
                        .ToArray()
                });
        }

        return new SalesSourceResolution(
            [],
            new SalesSourceVerification
            {
                EReturnsRoot = eReturnsRoot?.FullName ?? string.Empty,
                SourceOrigin = "E-returns",
                Status = "No GSTR-1 Found in E-returns"
            });
    }

    private static List<SalesSourceCandidate> CollectSalesCandidates(
        DirectoryInfo clientRoot,
        string gstin,
        string financialYear,
        string branchName,
        List<SalesImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var candidates =
            new List<SalesSourceCandidate>();

        foreach (var file in EnumerateSourceFiles(
                     clientRoot,
                     SearchOption.AllDirectories,
                     issues))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var payload in ReadJsonPayloads(
                         file,
                         issues))
            {
                var classification = ClassifySalesPayload(
                    payload.Payload);

                if (classification.Form != "GSTR1" ||
                    classification.Gstin != gstin ||
                    !MonthInFinancialYear(
                        classification.Period,
                        financialYear))
                {
                    continue;
                }

                var embedded = EmbeddedTimestamp(
                    payload.Payload);

                candidates.Add(
                    new SalesSourceCandidate
                    {
                        SourcePath = file.FullName,
                        SourceName = file.Name,
                        InternalName = payload.InternalName,
                        Period = classification.Period,
                        Payload = classification.NormalizedPayload,
                        RawPayload = payload.Payload,
                        ModifiedUtc = file.LastWriteTimeUtc,
                        EmbeddedTimestamp = embedded,
                        SelectionTimestamp =
                            embedded ?? file.LastWriteTimeUtc,
                        PayloadHash = PayloadHash(
                            payload.Payload),
                        SourceOrigin = "E-returns",
                        Branch = branchName,
                        Scope = "Monthly",
                        ReadOnly = true,
                        VerificationStatus =
                            "Latest Monthly GSTR-1"
                    });
            }
        }

        return candidates;
    }

    private static List<SalesSourceCandidate> ReadCachedSalesSources(
        IReadOnlyList<string> paths,
        string gstin,
        string financialYear,
        List<SalesImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var candidates = new List<SalesSourceCandidate>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path)) continue;
            var file = new FileInfo(path);
            foreach (var payload in ReadJsonPayloads(file, issues))
            {
                var classification = ClassifySalesPayload(payload.Payload);
                if (classification.Form != "GSTR1" || classification.Gstin != gstin ||
                    !MonthInFinancialYear(classification.Period, financialYear)) continue;
                var embedded = EmbeddedTimestamp(payload.Payload);
                candidates.Add(new SalesSourceCandidate
                {
                    SourcePath = file.FullName,
                    SourceName = file.Name,
                    InternalName = payload.InternalName,
                    Period = classification.Period,
                    Payload = classification.NormalizedPayload,
                    RawPayload = payload.Payload,
                    ModifiedUtc = file.LastWriteTimeUtc,
                    EmbeddedTimestamp = embedded,
                    SelectionTimestamp = embedded ?? file.LastWriteTimeUtc,
                    PayloadHash = PayloadHash(payload.Payload),
                    SourceOrigin = "GSTIN Cache",
                    Branch = "Shared Sales/Purchase Watcher",
                    Scope = "Monthly",
                    ReadOnly = true,
                    VerificationStatus = "Cached Monthly GSTR-1"
                });
            }
        }
        return LatestPerPeriod(candidates);
    }

    private static List<SalesSourceCandidate> ReadAutomationFolderSalesSources(
        IReadOnlyList<DirectoryInfo> automationRoots,
        string gstin,
        string financialYear,
        List<SalesImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var candidates = new List<SalesSourceCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in automationRoots)
        {
            foreach (var file in EnumerateSourceFiles(root, SearchOption.AllDirectories, issues))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var payload in ReadJsonPayloads(file, issues))
                {
                    var classification = ClassifySalesPayload(payload.Payload);
                    if (classification.Form != "GSTR1" || classification.Gstin != gstin ||
                        !MonthInFinancialYear(classification.Period, financialYear)) continue;

                    var hash = PayloadHash(payload.Payload);
                    if (!seen.Add(classification.Period + "|" + hash)) continue;
                    var embedded = EmbeddedTimestamp(payload.Payload);
                    candidates.Add(new SalesSourceCandidate
                    {
                        SourcePath = file.FullName,
                        SourceName = file.Name,
                        InternalName = payload.InternalName,
                        Period = classification.Period,
                        Payload = classification.NormalizedPayload,
                        RawPayload = payload.Payload,
                        ModifiedUtc = file.LastWriteTimeUtc,
                        EmbeddedTimestamp = embedded,
                        SelectionTimestamp = embedded ?? file.LastWriteTimeUtc,
                        PayloadHash = hash,
                        SourceOrigin = "Automation Folder",
                        Branch = "Linked Client/FY",
                        Scope = "Linked",
                        ReadOnly = true,
                        VerificationStatus = "Linked Monthly GSTR-1"
                    });
                }
            }
        }
        return LatestPerPeriod(candidates);
    }

    private static List<SalesSourceCandidate> ReadDownloadsSalesSources(
        string gstin,
        string financialYear,
        List<SalesImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var downloads = new DirectoryInfo(
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
                "Downloads"));

        if (!downloads.Exists)
        {
            return [];
        }

        var candidates =
            new List<SalesSourceCandidate>();

        var seen =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var file in EnumerateSourceFiles(
                     downloads,
                     SearchOption.AllDirectories,
                     issues))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var payload in ReadJsonPayloads(
                         file,
                         issues))
            {
                var classification = ClassifySalesPayload(
                    payload.Payload);

                if (classification.Form != "GSTR1" ||
                    classification.Gstin != gstin ||
                    !MonthInFinancialYear(
                        classification.Period,
                        financialYear))
                {
                    continue;
                }

                var hash = PayloadHash(
                    payload.Payload);

                if (!seen.Add(
                        classification.Period +
                        "|" +
                        hash))
                {
                    continue;
                }

                var embedded = EmbeddedTimestamp(
                    payload.Payload);

                candidates.Add(
                    new SalesSourceCandidate
                    {
                        SourcePath = file.FullName,
                        SourceName = file.Name,
                        InternalName = payload.InternalName,
                        Period = classification.Period,
                        Payload = classification.NormalizedPayload,
                        RawPayload = payload.Payload,
                        ModifiedUtc = file.LastWriteTimeUtc,
                        EmbeddedTimestamp = embedded,
                        SelectionTimestamp =
                            embedded ?? file.LastWriteTimeUtc,
                        PayloadHash = hash,
                        SourceOrigin = "Windows Downloads",
                        Branch = string.Empty,
                        Scope = "Legacy",
                        ReadOnly = true,
                        VerificationStatus = "Legacy Fallback"
                    });
            }
        }

        return LatestPerPeriod(
            candidates);
    }

    private static List<SalesSourceCandidate> LatestPerPeriod(
        IEnumerable<SalesSourceCandidate> candidates) =>
        candidates
            .GroupBy(
                source => source.Period,
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key)
            .Select(group =>
                group
                    .OrderByDescending(source =>
                        source.SelectionTimestamp)
                    .ThenByDescending(source =>
                        source.ModifiedUtc)
                    .ThenByDescending(source =>
                        source.SourcePath,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(source =>
                        source.InternalName,
                        StringComparer.OrdinalIgnoreCase)
                    .First())
            .ToList();

    private static CustomerMasterResolution ResolveCustomerMaster(
        DirectoryInfo? eReturnsRoot,
        IReadOnlyList<DirectoryInfo> automationRoots,
        string gstin,
        string financialYear,
        bool searchDownloads,
        List<SalesImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var financialYearRoot = eReturnsRoot is null
            ? null
            : FindFinancialYearRoot(
                eReturnsRoot,
                financialYear);

        if (financialYearRoot is not null)
        {
            foreach (var branchName in new[]
                     {
                         "imported",
                         "downloaded"
                     })
            {
                var branchRoot = FindChildDirectory(
                    financialYearRoot,
                    branchName);

                var clientRoot = branchRoot is null
                    ? null
                    : FindChildDirectory(
                        branchRoot,
                        gstin);

                if (clientRoot is null)
                {
                    continue;
                }

                var selected = SelectLatestCustomerMaster(
                    clientRoot,
                    gstin,
                    SearchOption.AllDirectories,
                    "E-returns",
                    branchName,
                    issues,
                    cancellationToken);

                if (selected.Status == "Loaded")
                {
                    return selected;
                }
            }
        }

        foreach (var automationRoot in automationRoots)
        {
            var selected = SelectLatestCustomerMaster(
                automationRoot,
                gstin,
                SearchOption.AllDirectories,
                "Automation Folder",
                "Sales\\Customer Master",
                issues,
                cancellationToken);
            if (selected.Status == "Loaded") return selected;
        }

        if (!searchDownloads)
        {
            return CustomerMasterResolution.NotFound();
        }

        var downloads = new DirectoryInfo(
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
                "Downloads"));

        return downloads.Exists
            ? SelectLatestCustomerMaster(
                downloads,
                gstin,
                SearchOption.TopDirectoryOnly,
                "Windows Downloads",
                string.Empty,
                issues,
                cancellationToken)
            : CustomerMasterResolution.NotFound();
    }

    private static CustomerMasterResolution SelectLatestCustomerMaster(
        DirectoryInfo folder,
        string gstin,
        SearchOption searchOption,
        string sourceOrigin,
        string sourceBranch,
        List<SalesImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var candidates =
            new List<CustomerMasterResolution>();

        IEnumerable<FileInfo> files;

        try
        {
            files = folder
                .EnumerateFiles("*", searchOption)
                .Where(file =>
                    (file.Extension.Equals(
                         ".xlsx",
                         StringComparison.OrdinalIgnoreCase) ||
                     file.Extension.Equals(
                         ".xlsm",
                         StringComparison.OrdinalIgnoreCase)) &&
                    !file.Name.StartsWith(
                        "~$",
                        StringComparison.Ordinal) &&
                    file.Name.Contains(
                        "MASTER",
                        StringComparison.OrdinalIgnoreCase) &&
                    NormalizeGstin(file.Name)
                        .Contains(
                            gstin,
                            StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception exception)
        {
            issues.Add(
                NewIssue(
                    folder.FullName,
                    "GST Recipient Master",
                    exception.Message));

            return CustomerMasterResolution.NotFound();
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var read = ReadCustomerMasterNames(
                    file,
                    cancellationToken);

                if (read.Names.Count == 0)
                {
                    continue;
                }

                candidates.Add(
                    new CustomerMasterResolution(
                        read.Names,
                        "Loaded",
                        sourceOrigin,
                        sourceBranch,
                        file.FullName,
                        FileHash(file.FullName),
                        read.SheetName,
                        read.HeaderRow,
                        MasterSelectionTimestamp(file)));
            }
            catch (Exception exception)
            {
                issues.Add(
                    NewIssue(
                        file.FullName,
                        "GST Recipient Master",
                        exception.Message));
            }
        }

        return candidates
            .OrderByDescending(item =>
                item.SelectionTimestamp)
            .ThenByDescending(item =>
                item.SourcePath,
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?? CustomerMasterResolution.NotFound();
    }

    public static IReadOnlyDictionary<string, string> ReadCustomerMasterNamesForBackfill(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return ReadCustomerMasterNames(new FileInfo(path), cancellationToken).Names;
    }

    public static IReadOnlyDictionary<string, SalesCustomerMasterNameDetail> ReadCustomerMasterNameDetailsForB2BPanel(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new Dictionary<string, SalesCustomerMasterNameDetail>(StringComparer.OrdinalIgnoreCase);
        return ReadCustomerMasterNames(new FileInfo(path), cancellationToken).Details;
    }

    private static CustomerMasterReadResult ReadCustomerMasterNames(
        FileInfo file,
        CancellationToken cancellationToken)
    {
        var workbook = new OpenXmlWorkbookReader()
            .Read(
                file.FullName,
                cancellationToken);

        foreach (var worksheet in workbook.Worksheets.Values)
        {
            var headerRow = 0;
            var headerMap =
                new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);

            for (var row = 1;
                 row <= Math.Min(
                     worksheet.MaxRow,
                     25);
                 row++)
            {
                var current =
                    new Dictionary<string, int>(
                        StringComparer.OrdinalIgnoreCase);

                for (var column = 1;
                     column <= 80;
                     column++)
                {
                    var key = NormalizeHeader(
                        worksheet.GetCell(
                            row,
                            column));

                    if (key.Length > 0 &&
                        !current.ContainsKey(key))
                    {
                        current[key] = column;
                    }
                }

                var hasGstin = current.ContainsKey("GSTIN") ||
                               current.ContainsKey("GSTINUIN") ||
                               current.ContainsKey("GSTINNUMBER") ||
                               current.ContainsKey("GSTINUINNUMBER");
                var hasName = current.ContainsKey("TRADELEGALNAME") ||
                              current.ContainsKey("TRADELEGALNAMEIFANY") ||
                              current.ContainsKey("SUPPLIERRECIPIENTNAME") ||
                              current.ContainsKey("SUPPLIERRECIPIENT") ||
                              current.ContainsKey("RECIPIENTNAME") ||
                              current.ContainsKey("CUSTOMERNAME");
                if (hasGstin && hasName)
                {
                    headerRow = row;
                    headerMap = current;
                    break;
                }
            }

            if (headerRow == 0)
            {
                continue;
            }

            var names =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
            var details =
                new Dictionary<string, SalesCustomerMasterNameDetail>(
                    StringComparer.OrdinalIgnoreCase);

            var gstinColumn = FirstHeaderColumn(
                headerMap,
                "GSTIN",
                "GSTINUIN",
                "GSTINNUMBER",
                "GSTINUINNUMBER");
            var tradeColumn = FirstHeaderColumn(
                headerMap,
                "TRADELEGALNAME",
                "TRADELEGALNAMEIFANY");
            var commonNameColumn = FirstHeaderColumn(
                headerMap,
                "SUPPLIERRECIPIENTNAME",
                "SUPPLIERRECIPIENT",
                "RECIPIENTNAME",
                "CUSTOMERNAME");

            var recipientColumn = headerMap.TryGetValue(
                "RECIPIENT",
                out var recipient)
                ? recipient
                : 0;

            for (var row = headerRow + 1;
                 row <= worksheet.MaxRow;
                 row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var customerGstin = NormalizeGstin(
                    worksheet.GetCell(
                        row,
                        gstinColumn));

                if (customerGstin.Length != 15)
                {
                    continue;
                }

                if (recipientColumn > 0)
                {
                    var recipientFlag = NormalizeFlag(
                        worksheet.GetCell(
                            row,
                            recipientColumn));

                    if (recipientFlag is not
                        ("Y" or "YES" or "TRUE" or "1"))
                    {
                        continue;
                    }
                }

                var tradeName = tradeColumn > 0
                    ? worksheet.GetCell(
                        row,
                        tradeColumn)
                    : string.Empty;

                var commonName = commonNameColumn > 0
                    ? worksheet.GetCell(
                        row,
                        commonNameColumn)
                    : string.Empty;

                var commonCandidate = CleanCustomerMasterName(commonName, customerGstin);
                var tradeCandidate = CleanCustomerMasterName(tradeName, customerGstin);
                // The GST portal's current Master Details workbook is headed
                // "Supplier/Recipient name | GSTIN | Trade/ Legal name | ... | Recipient".
                // Prefer the recipient name when it is a real name; some rows put the GSTIN
                // itself in that column, in which case Trade/ Legal name is the useful value.
                var name = commonCandidate.Length > 0
                    ? commonCandidate
                    : tradeCandidate;

                if (commonCandidate.Length > 0 || tradeCandidate.Length > 0)
                {
                    var conflict = string.Empty;
                    if (details.TryGetValue(customerGstin, out var existingDetail))
                    {
                        var conflicts = new List<string>();
                        if (existingDetail.RecipientName.Length > 0 && commonCandidate.Length > 0 &&
                            !CanonicalCustomerMasterName(existingDetail.RecipientName).Equals(CanonicalCustomerMasterName(commonCandidate), StringComparison.Ordinal))
                            conflicts.Add($"Recipient Name: {existingDetail.RecipientName} / {commonCandidate}");
                        if (existingDetail.TradeName.Length > 0 && tradeCandidate.Length > 0 &&
                            !CanonicalCustomerMasterName(existingDetail.TradeName).Equals(CanonicalCustomerMasterName(tradeCandidate), StringComparison.Ordinal))
                            conflicts.Add($"Trade Name: {existingDetail.TradeName} / {tradeCandidate}");
                        var existingConflict = existingDetail.SourceConflict.StartsWith("GSTIN source conflict - ", StringComparison.Ordinal)
                            ? existingDetail.SourceConflict[24..]
                            : existingDetail.SourceConflict;
                        conflict = string.Join("; ", new[] { existingConflict }.Where(value => value.Length > 0).Concat(conflicts));
                    }

                    details[customerGstin] = new SalesCustomerMasterNameDetail
                    {
                        Gstin = customerGstin,
                        RecipientName = commonCandidate,
                        TradeName = tradeCandidate,
                        PreferredName = name,
                        SourceConflict = conflict.Length == 0 ? string.Empty : $"GSTIN source conflict - {conflict}"
                    };
                }

                if (name.Length > 0)
                {
                    names[customerGstin] = name;
                }
            }

            if (names.Count > 0)
            {
                return new CustomerMasterReadResult(
                    names,
                    details,
                    worksheet.Name,
                    headerRow);
            }
        }

        return new CustomerMasterReadResult(
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SalesCustomerMasterNameDetail>(
                StringComparer.OrdinalIgnoreCase),
            string.Empty,
            0);
    }

    private static int FirstHeaderColumn(
        IReadOnlyDictionary<string, int> headers,
        params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            if (headers.TryGetValue(alias, out var column) && column > 0) return column;
        }
        return 0;
    }

    private static string CleanCustomerMasterName(string value, string customerGstin)
    {
        var name = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        if (name.Length == 0) return string.Empty;
        var normalizedName = NormalizeGstin(name);
        if (normalizedName.Equals(customerGstin, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (Regex.IsMatch(name, @"^\d{2}[A-Z]{5}\d{4}[A-Z][A-Z0-9]Z[A-Z0-9]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return string.Empty;
        return name;
    }

    private static string CanonicalCustomerMasterName(string? value) =>
        new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    private static Dictionary<string, string> ResolveTwoBNames(
        DirectoryInfo? eReturnsRoot,
        IReadOnlyList<DirectoryInfo> automationRoots,
        string gstin,
        string financialYear,
        bool searchDownloads,
        List<SalesImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var candidates =
            new List<TwoBSourceCandidate>();

        var financialYearRoot = eReturnsRoot is null
            ? null
            : FindFinancialYearRoot(
                eReturnsRoot,
                financialYear);

        if (financialYearRoot is not null)
        {
            foreach (var branchName in new[]
                     {
                         "imported",
                         "downloaded"
                     })
            {
                var branchRoot = FindChildDirectory(
                    financialYearRoot,
                    branchName);

                var clientRoot = branchRoot is null
                    ? null
                    : FindChildDirectory(
                        branchRoot,
                        gstin);

                if (clientRoot is null)
                {
                    continue;
                }

                candidates = CollectTwoBCandidates(
                    clientRoot,
                    gstin,
                    financialYear,
                    issues,
                    cancellationToken);

                if (candidates.Count > 0)
                {
                    break;
                }
            }
        }

        if (candidates.Count == 0)
        {
            foreach (var automationRoot in automationRoots)
            {
                candidates = CollectTwoBCandidates(
                    automationRoot,
                    gstin,
                    financialYear,
                    issues,
                    cancellationToken);
                if (candidates.Count > 0) break;
            }
        }

        if (candidates.Count == 0 &&
            searchDownloads)
        {
            var downloads = new DirectoryInfo(
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.UserProfile),
                    "Downloads"));

            if (downloads.Exists)
            {
                candidates = CollectTwoBCandidates(
                    downloads,
                    gstin,
                    financialYear,
                    issues,
                    cancellationToken);
            }
        }

        var names =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var source in candidates
                     .OrderBy(item =>
                         item.SelectionTimestamp))
        {
            cancellationToken.ThrowIfCancellationRequested();

            ExtractTwoBNames(
                source.Payload,
                names);
        }

        return names;
    }

    private static List<TwoBSourceCandidate> CollectTwoBCandidates(
        DirectoryInfo folder,
        string gstin,
        string financialYear,
        List<SalesImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var candidates =
            new List<TwoBSourceCandidate>();

        foreach (var file in EnumerateSourceFiles(
                     folder,
                     SearchOption.AllDirectories,
                     issues))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var payload in ReadJsonPayloads(
                         file,
                         issues))
            {
                var classification = ClassifyTwoBPayload(
                    payload.Payload);

                if (classification.Gstin != gstin ||
                    !MonthInFinancialYear(
                        classification.Period,
                        financialYear))
                {
                    continue;
                }

                var embedded = EmbeddedTimestamp(
                    payload.Payload);

                candidates.Add(
                    new TwoBSourceCandidate(
                        classification.NormalizedPayload,
                        embedded ??
                        file.LastWriteTimeUtc));
            }
        }

        return candidates;
    }

    private static void ExtractTwoBNames(
        JsonObject payload,
        IDictionary<string, string> names)
    {
        var data = Object(
            payload["data"]);

        if (data is null)
        {
            return;
        }

        foreach (var containerName in new[]
                 {
                     "docdata",
                     "docRejdata",
                     "cpsumm",
                     "cpSummRej"
                 })
        {
            var container = Object(
                data[containerName]);

            if (container is null)
            {
                continue;
            }

            foreach (var section in container)
            {
                if (!TwoBSections.Contains(
                        section.Key))
                {
                    continue;
                }

                var suppliers = Array(
                    section.Value);

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

                    var customerGstin = NormalizeGstin(
                        Text(supplier["ctin"]));

                    var name = Text(
                        supplier["trdnm"]);

                    if (customerGstin.Length == 15 &&
                        name.Length > 0)
                    {
                        names[customerGstin] = name;
                    }
                }
            }
        }
    }

    private static List<SalesImportRecord> BuildSalesRecords(
        string gstin,
        string companyName,
        string financialYear,
        IReadOnlyList<SalesSourceCandidate> sources,
        CustomerMasterResolution customerMaster,
        IReadOnlyDictionary<string, string> twoBNames,
        List<SalesImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var records =
            new List<SalesImportRecord>();

        foreach (var source in sources
                     .OrderBy(item => item.Period)
                     .ThenBy(item =>
                         item.SelectionTimestamp))
        {
            cancellationToken.ThrowIfCancellationRequested();

            BuildRegisteredSales(
                source,
                gstin,
                companyName,
                financialYear,
                customerMaster,
                twoBNames,
                records,
                issues,
                cancellationToken);

            BuildUnregisteredSales(
                source,
                gstin,
                companyName,
                financialYear,
                records,
                issues,
                cancellationToken);

            BuildSalesNotes(
                source,
                gstin,
                companyName,
                financialYear,
                customerMaster,
                twoBNames,
                records,
                issues,
                cancellationToken);
        }

        return records
            .OrderBy(record =>
                record.GstDate)
            .ThenBy(record =>
                record.DocumentDate)
            .ThenBy(record =>
                record.DocumentNumber,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(record =>
                record.Rate)
            .ToList();
    }

    private static void BuildRegisteredSales(
        SalesSourceCandidate source,
        string gstin,
        string companyName,
        string financialYear,
        CustomerMasterResolution customerMaster,
        IReadOnlyDictionary<string, string> twoBNames,
        ICollection<SalesImportRecord> records,
        ICollection<SalesImportIssue> issues,
        CancellationToken cancellationToken)
    {
        foreach (var sectionName in new[]
                 {
                     "b2b",
                     "b2ba"
                 })
        {
            var groups = Array(
                source.Payload[sectionName]);

            if (groups is null)
            {
                continue;
            }

            foreach (var groupNode in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var group = Object(
                    groupNode);

                if (group is null)
                {
                    continue;
                }

                var customerGstin = NormalizeGstin(
                    Text(group["ctin"]));

                var customer = ResolveCustomerName(
                    customerGstin,
                    customerMaster.Names,
                    twoBNames,
                    "Registered Customer");

                var invoices = Array(
                    group["inv"]);

                if (invoices is null)
                {
                    continue;
                }

                foreach (var invoiceNode in invoices)
                {
                    var invoice = Object(
                        invoiceNode);

                    if (invoice is null)
                    {
                        continue;
                    }

                    var documentDate = ParseDate(
                        FirstText(
                            invoice,
                            "idt",
                            "dt"));

                    if (!DateInFinancialYear(
                            documentDate,
                            financialYear))
                    {
                        continue;
                    }

                    var items = SalesItems(
                        invoice);

                    var sourceDocumentValue = Number(
                        invoice["val"]);

                    var roundOff = CalculateRoundOff(
                        sourceDocumentValue,
                        items);

                    AddSalesItemRecords(
                        records,
                        source,
                        gstin,
                        companyName,
                        customer,
                        "Sales",
                        "Invoice",
                        Text(invoice["inum"]),
                        documentDate,
                        sourceDocumentValue,
                        source.Period,
                        sectionName.ToUpperInvariant(),
                        sectionName.EndsWith(
                            "a",
                            StringComparison.OrdinalIgnoreCase),
                        FirstText(
                            invoice,
                            "oinum",
                            "oinv_num"),
                        ParseDate(
                            FirstText(
                                invoice,
                                "oidt",
                                "oinv_dt")),
                        Text(invoice["pos"]),
                        items,
                        roundOff,
                        customerMaster.SourcePath);
                }
            }
        }
    }

    private static void BuildUnregisteredSales(
        SalesSourceCandidate source,
        string gstin,
        string companyName,
        string financialYear,
        ICollection<SalesImportRecord> records,
        ICollection<SalesImportIssue> issues,
        CancellationToken cancellationToken)
    {
        foreach (var sectionName in new[]
                 {
                     "b2cs",
                     "b2csa",
                     "b2cl",
                     "b2cla",
                     "exp",
                     "expa"
                 })
        {
            var groups = Array(
                source.Payload[sectionName]);

            if (groups is null)
            {
                continue;
            }

            var sequence = 0;

            foreach (var groupNode in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                sequence++;

                var document = Object(
                    groupNode);

                if (document is null)
                {
                    continue;
                }

                var items = SalesItems(
                    document);

                var documentDate = ParseDate(
                    FirstText(
                        document,
                        "idt",
                        "dt"));

                documentDate ??= PeriodLastDate(
                    source.Period);

                if (!DateInFinancialYear(
                        documentDate,
                        financialYear))
                {
                    continue;
                }

                var placeOfSupply = Text(
                    document["pos"]);

                var sourceDocumentValue = Number(
                    document["val"]);

                if (sourceDocumentValue == 0m)
                {
                    sourceDocumentValue =
                        items.Sum(item =>
                            item.Taxable +
                            item.Igst +
                            item.Cgst +
                            item.Sgst +
                            item.Cess);
                }

                for (var index = 0;
                     index < items.Count;
                     index++)
                {
                    var item = items[index];

                    var documentNumber = Text(
                        document["inum"]);

                    if (documentNumber.Length == 0)
                    {
                        documentNumber =
                            $"{sectionName.ToUpperInvariant()}-" +
                            $"{source.Period}-" +
                            $"{(placeOfSupply.Length == 0 ? "NA" : placeOfSupply)}-" +
                            $"{PurchaseImportUtilities.FormatRate(item.Rate)}-" +
                            $"{sequence}";
                    }

                    var ledgers = BuildSalesLedgers(
                        item.Rate,
                        item.Igst);

                    records.Add(
                        CreateRecord(
                            source,
                            gstin,
                            companyName,
                            new CustomerNameResolution(
                                string.Empty,
                                $"B2C Sales - {(placeOfSupply.Length == 0 ? "Unregistered" : placeOfSupply)}",
                                "Generic Fallback"),
                            "Sales",
                            sectionName.StartsWith(
                                "b2cs",
                                StringComparison.OrdinalIgnoreCase)
                                ? "B2C Aggregate"
                                : sectionName.ToUpperInvariant(),
                            documentNumber,
                            documentDate,
                            sourceDocumentValue,
                            sourceDocumentValue,
                            source.Period,
                            sectionName.ToUpperInvariant(),
                            sectionName.EndsWith(
                                "a",
                                StringComparison.OrdinalIgnoreCase),
                            string.Empty,
                            null,
                            placeOfSupply,
                            item,
                            0m,
                            ledgers,
                            index + 1,
                            string.Empty));
                }
            }
        }
    }

    private static void BuildSalesNotes(
        SalesSourceCandidate source,
        string gstin,
        string companyName,
        string financialYear,
        CustomerMasterResolution customerMaster,
        IReadOnlyDictionary<string, string> twoBNames,
        ICollection<SalesImportRecord> records,
        ICollection<SalesImportIssue> issues,
        CancellationToken cancellationToken)
    {
        foreach (var sectionName in new[]
                 {
                     "cdnr",
                     "cdnra",
                     "cdnur",
                     "cdnura"
                 })
        {
            var groups = Array(
                source.Payload[sectionName]);

            if (groups is null)
            {
                continue;
            }

            foreach (var groupNode in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var group = Object(
                    groupNode);

                if (group is null)
                {
                    continue;
                }

                var customerGstin = NormalizeGstin(
                    Text(group["ctin"]));

                var customer = ResolveCustomerName(
                    customerGstin,
                    customerMaster.Names,
                    twoBNames,
                    "Unregistered Customer");

                var notes = Array(
                    group["nt"]);

                if (notes is null)
                {
                    continue;
                }

                foreach (var noteNode in notes)
                {
                    var note = Object(
                        noteNode);

                    if (note is null)
                    {
                        continue;
                    }

                    var noteDate = ParseDate(
                        FirstText(
                            note,
                            "nt_dt",
                            "dt"));

                    if (!DateInFinancialYear(
                            noteDate,
                            financialYear))
                    {
                        continue;
                    }

                    var items = SalesItems(
                        note);

                    var sourceDocumentValue = Number(
                        note["val"]);

                    var roundOff = CalculateRoundOff(
                        sourceDocumentValue,
                        items);

                    var noteType = NormalizeFlag(
                        FirstText(
                            note,
                            "ntty",
                            "typ"));

                    var destination = noteType == "C"
                        ? "Credit Note"
                        : "Sales";

                    var documentType = noteType == "C"
                        ? "Credit Note"
                        : "Debit Note";

                    AddSalesItemRecords(
                        records,
                        source,
                        gstin,
                        companyName,
                        customer,
                        destination,
                        documentType,
                        FirstText(
                            note,
                            "nt_num",
                            "ntnum"),
                        noteDate,
                        sourceDocumentValue,
                        source.Period,
                        sectionName.ToUpperInvariant(),
                        sectionName.EndsWith(
                            "a",
                            StringComparison.OrdinalIgnoreCase),
                        FirstText(
                            note,
                            "ont_num",
                            "ontnum"),
                        ParseDate(
                            FirstText(
                                note,
                                "ont_dt",
                                "ontdt")),
                        Text(note["pos"]),
                        items,
                        roundOff,
                        customerMaster.SourcePath);
                }
            }
        }
    }

    private static void AddSalesItemRecords(
        ICollection<SalesImportRecord> records,
        SalesSourceCandidate source,
        string gstin,
        string companyName,
        CustomerNameResolution customer,
        string destination,
        string documentType,
        string documentNumber,
        DateTime? documentDate,
        decimal sourceDocumentValue,
        string returnPeriod,
        string sourceSection,
        bool isAmendment,
        string originalDocumentNumber,
        DateTime? originalDocumentDate,
        string placeOfSupply,
        IReadOnlyList<SalesItem> items,
        decimal roundOff,
        string salesMasterFile)
    {
        for (var index = 0;
             index < items.Count;
             index++)
        {
            var item = items[index];

            var ledgers = BuildSalesLedgers(
                item.Rate,
                item.Igst);

            records.Add(
                CreateRecord(
                    source,
                    gstin,
                    companyName,
                    customer,
                    destination,
                    documentType,
                    documentNumber,
                    documentDate,
                    sourceDocumentValue,
                    sourceDocumentValue,
                    returnPeriod,
                    sourceSection,
                    isAmendment,
                    originalDocumentNumber,
                    originalDocumentDate,
                    placeOfSupply,
                    item,
                    index == items.Count - 1
                        ? roundOff
                        : 0m,
                    ledgers,
                    index + 1,
                    salesMasterFile));
        }
    }

    private static SalesImportRecord CreateRecord(
        SalesSourceCandidate source,
        string gstin,
        string companyName,
        CustomerNameResolution customer,
        string destination,
        string documentType,
        string documentNumber,
        DateTime? documentDate,
        decimal sourceDocumentValue,
        decimal documentValue,
        string returnPeriod,
        string sourceSection,
        bool isAmendment,
        string originalDocumentNumber,
        DateTime? originalDocumentDate,
        string placeOfSupply,
        SalesItem item,
        decimal roundOff,
        SalesLedgerSet ledgers,
        int itemSequence,
        string salesMasterFile) =>
        new()
        {
            Destination = destination,
            CustomerGstin = customer.Gstin,
            CustomerName =
                PurchaseImportUtilities.ToDisplayName(
                    customer.Name),
            CustomerNameSource = customer.Source,
            DocumentType = documentType,
            DocumentNumber = documentNumber.Trim(),
            DocumentDate = documentDate,
            GstDate = CalculateSalesGstDate(
                documentDate,
                returnPeriod),
            SourceDocumentValue = sourceDocumentValue,
            DocumentValue = documentValue,
            Rate = item.Rate,
            TaxableValue = item.Taxable,
            Igst = item.Igst,
            Cgst = item.Cgst,
            Sgst = item.Sgst,
            Cess = item.Cess,
            RoundOff = roundOff,
            SalesLedger = ledgers.Sales,
            IgstLedger = ledgers.Igst,
            CgstLedger = ledgers.Cgst,
            SgstLedger = ledgers.Sgst,
            SourceSection = sourceSection,
            IsAmendment = isAmendment,
            OriginalDocumentNumber =
                originalDocumentNumber.Trim(),
            OriginalDocumentDate =
                originalDocumentDate,
            PlaceOfSupply =
                placeOfSupply.Trim(),
            ReturnPeriod = returnPeriod,
            SourceFile = source.SourceName,
            SourcePath = source.SourcePath,
            SourceOrigin = source.SourceOrigin,
            SourceBranch = source.Branch,
            SourceScope = source.Scope,
            SourceHash = source.PayloadHash,
            ResponseTimestamp =
                source.EmbeddedTimestamp?.ToString(
                    "s",
                    CultureInfo.InvariantCulture)
                ?? string.Empty,
            SalesMasterFile = salesMasterFile,
            ClientGstin = gstin,
            CompanyName = companyName,
            ImportMode = "Auto Import",
            ItemSequence = itemSequence
        };

    private static SalesImportSummary BuildSummary(
        IReadOnlyCollection<SalesImportRecord> records,
        int invalidRows) =>
        new()
        {
            SalesRows = records.Count(record =>
                record.Destination == "Sales"),
            CreditNoteRows = records.Count(record =>
                record.Destination == "Credit Note"),
            B2BRows = records.Count(record =>
                record.SourceSection is
                    "B2B" or "B2BA"),
            B2CRows = records.Count(record =>
                record.SourceSection.StartsWith(
                    "B2C",
                    StringComparison.OrdinalIgnoreCase)),
            ExportRows = records.Count(record =>
                record.SourceSection.StartsWith(
                    "EXP",
                    StringComparison.OrdinalIgnoreCase)),
            AmendmentRows = records.Count(record =>
                record.IsAmendment),
            InvalidRowsSkipped = invalidRows
        };

    private static List<string> BuildWarnings(
        SalesAutoImportRequest request,
        IReadOnlyCollection<SalesSourceCandidate> sources,
        CustomerMasterResolution customerMaster,
        IReadOnlyDictionary<string, string> twoBNames,
        SalesImportSummary summary)
    {
        var warnings = new List<string>();

        if (sources.Count == 0)
        {
            warnings.Add(
                "No valid GSTR-1 JSON or ZIP source was found for the selected GSTIN and Financial Year.");
        }
        else if (summary.TotalOutputRows == 0)
        {
            warnings.Add(
                "GSTR-1 sources were found, but no supported Sales documents belonged to the selected Financial Year.");
        }

        if (customerMaster.Status != "Loaded")
        {
            warnings.Add(
                twoBNames.Count > 0
                    ? "GST Recipient Master was not found. Registered customer names used GSTR-2B or GSTIN fallback values."
                    : "GST Recipient Master and GSTR-2B customer names were not found. Registered customers used GSTIN fallback names.");
        }

        if (request.SaveSourceFiles)
        {
            warnings.Add(
                "Save Files is reserved for the future storage plan and did not perform any file operation.");
        }

        return warnings;
    }

    private static IReadOnlyList<SalesSourceManifestItem> BuildManifest(
        IReadOnlyCollection<SalesSourceCandidate> sources,
        CustomerMasterResolution customerMaster)
    {
        var rows = sources
            .Select(source =>
                new SalesSourceManifestItem
                {
                    FileType = "GSTR1",
                    Usage = "Sales Source",
                    ReturnPeriod = source.Period,
                    SourceFile = source.SourceName,
                    SourcePath = source.SourcePath,
                    InternalJsonName = source.InternalName,
                    SourceOrigin = source.SourceOrigin,
                    SourceBranch = source.Branch,
                    SourceScope = source.Scope,
                    VerificationStatus =
                        source.VerificationStatus,
                    EmbeddedTimestamp =
                        source.EmbeddedTimestamp?.ToString(
                            "s",
                            CultureInfo.InvariantCulture)
                        ?? string.Empty,
                    PayloadHash = source.PayloadHash,
                    ReadOnlySource = true
                })
            .ToList();

        if (customerMaster.Status == "Loaded")
        {
            rows.Add(
                new SalesSourceManifestItem
                {
                    FileType = "GST Recipient Master",
                    Usage = "Sales Customer Name Lookup",
                    SourceFile = Path.GetFileName(
                        customerMaster.SourcePath)
                        ?? string.Empty,
                    SourcePath = customerMaster.SourcePath,
                    SourceOrigin =
                        customerMaster.SourceOrigin,
                    SourceBranch =
                        customerMaster.SourceBranch,
                    SourceScope = "Master",
                    VerificationStatus = "Loaded",
                    PayloadHash =
                        customerMaster.SourceHash,
                    ReadOnlySource = true
                });
        }

        return rows
            .OrderBy(row =>
                row.FileType,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(row =>
                row.ReturnPeriod,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(row =>
                row.SourceFile,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static CustomerNameResolution ResolveCustomerName(
        string customerGstin,
        IReadOnlyDictionary<string, string> masterNames,
        IReadOnlyDictionary<string, string> twoBNames,
        string blankFallback)
    {
        var normalized = NormalizeGstin(
            customerGstin);

        if (masterNames.TryGetValue(
                normalized,
                out var masterName) &&
            !string.IsNullOrWhiteSpace(masterName))
        {
            return new CustomerNameResolution(
                normalized,
                masterName,
                "GST Recipient Master");
        }

        if (twoBNames.TryGetValue(
                normalized,
                out var twoBName) &&
            !string.IsNullOrWhiteSpace(twoBName))
        {
            return new CustomerNameResolution(
                normalized,
                twoBName,
                "GSTR-2B Fallback");
        }

        if (normalized.Length > 0)
        {
            return new CustomerNameResolution(
                normalized,
                $"Customer - {normalized}",
                "GSTIN Fallback");
        }

        return new CustomerNameResolution(
            string.Empty,
            blankFallback,
            "Generic Fallback");
    }

    private static IReadOnlyList<SalesItem> SalesItems(
        JsonObject document)
    {
        var items = Array(
            document["itms"]);

        if (items is null ||
            items.Count == 0)
        {
            return
            [
                ApplyZeroRateFallback(
                    new SalesItem(
                        Number(document["rt"]),
                        Number(document["txval"]),
                        FirstNumber(
                            document,
                            "iamt",
                            "igst"),
                        FirstNumber(
                            document,
                            "camt",
                            "cgst"),
                        FirstNumber(
                            document,
                            "samt",
                            "sgst"),
                        FirstNumber(
                            document,
                            "csamt",
                            "cess")))
            ];
        }

        var result = new List<SalesItem>();

        foreach (var itemNode in items)
        {
            var item = Object(
                itemNode);

            if (item is null)
            {
                continue;
            }

            var detail = Object(
                item["itm_det"])
                ?? item;

            var parsed = ApplyZeroRateFallback(
                new SalesItem(
                    Number(detail["rt"]),
                    Number(detail["txval"]),
                    FirstNumber(detail, "iamt", "igst"),
                    FirstNumber(detail, "camt", "cgst"),
                    FirstNumber(detail, "samt", "sgst"),
                    FirstNumber(detail, "csamt", "cess")));

            // GST portal GSTR-1 JSON can carry a complete rate template on every invoice
            // (0, .1, .25, 1, 1.5, 3, 5, 6, 7.5, 12, 18, 28, 40...) while only one
            // bucket has values.  Template-only buckets are schema placeholders, not sales.
            if (Math.Abs(parsed.Taxable) <= 0.005m &&
                Math.Abs(parsed.Igst) <= 0.005m &&
                Math.Abs(parsed.Cgst) <= 0.005m &&
                Math.Abs(parsed.Sgst) <= 0.005m &&
                Math.Abs(parsed.Cess) <= 0.005m)
            {
                continue;
            }

            result.Add(parsed);
        }

        return result.Count > 0
            ? result
            :
            [
                new SalesItem(
                    0m,
                    0m,
                    0m,
                    0m,
                    0m,
                    0m)
            ];
    }

    private static SalesItem ApplyZeroRateFallback(SalesItem item)
    {
        if (item.Rate != 0m) return item;
        return PurchaseImportUtilities.TryDeriveSupportedRate(
            item.Taxable, item.Igst, item.Cgst, item.Sgst, out var derivedRate)
            ? item with { Rate = derivedRate }
            : item;
    }

    private static SalesLedgerSet BuildSalesLedgers(
        decimal rate,
        decimal igst)
    {
        var rateText =
            PurchaseImportUtilities.FormatRate(
                rate);

        var salesLedger = rate == 0m
            ? "Exempted Sales"
            : $"Sales @ {rateText}%";

        // A genuine 0% / exempt row must never create Output IGST/CGST/SGST @ 0%.
        // Only the Exempted Sales ledger is applicable.
        if (rate == 0m)
        {
            return new SalesLedgerSet(
                salesLedger,
                string.Empty,
                string.Empty,
                string.Empty);
        }

        if (Math.Abs(igst) > 0.005m)
        {
            return new SalesLedgerSet(
                salesLedger,
                $"Output IGST @ {rateText}%",
                string.Empty,
                string.Empty);
        }

        var halfRate =
            PurchaseImportUtilities.FormatRate(
                rate / 2m);

        return new SalesLedgerSet(
            salesLedger,
            string.Empty,
            $"Output CGST @ {halfRate}%",
            $"Output SGST @ {halfRate}%");
    }

    private static decimal CalculateRoundOff(
        decimal documentValue,
        IReadOnlyCollection<SalesItem> items)
    {
        var calculated = items.Sum(item =>
            item.Taxable +
            item.Igst +
            item.Cgst +
            item.Sgst +
            item.Cess);

        var roundOff = decimal.Round(
            documentValue - calculated,
            2,
            MidpointRounding.ToEven);

        return Math.Abs(roundOff) < 0.005m
            ? 0m
            : roundOff;
    }

    private static DateTime? CalculateSalesGstDate(
        DateTime? documentDate,
        string returnPeriod)
    {
        if (documentDate.HasValue &&
            returnPeriod.Length == 6 &&
            documentDate.Value.ToString(
                "yyyyMM",
                CultureInfo.InvariantCulture) ==
            returnPeriod)
        {
            return documentDate.Value.Date;
        }

        return PeriodFirstDate(
                   returnPeriod)
               ?? documentDate?.Date;
    }

    private static SalesPayloadClassification ClassifySalesPayload(
        JsonObject payload)
    {
        var winRequest = Object(
            payload["winreq"]);

        var response = Object(
            payload["resp"]);

        if (response is not null)
        {
            var requestId = FirstText(
                    winRequest,
                    "reqid",
                    "FormName",
                    "formname")
                .ToUpperInvariant();

            if (requestId.Contains(
                    "GSTR1",
                    StringComparison.Ordinal) ||
                requestId.Contains(
                    "GSTR-1",
                    StringComparison.Ordinal))
            {
                var gstin = NormalizeGstin(
                    FirstText(
                        response,
                        "gstin"));

                if (gstin.Length == 0)
                {
                    gstin = NormalizeGstin(
                        FirstText(
                            winRequest,
                            "gstin"));
                }

                var period = NormalizePeriod(
                    FirstText(
                        response,
                        "fp"));

                if (period.Length == 0)
                {
                    period = NormalizePeriod(
                        FirstText(
                            winRequest,
                            "retnprd",
                            "rtnprd"));
                }

                if (gstin.Length == 15 &&
                    period.Length == 6)
                {
                    return new SalesPayloadClassification(
                        "GSTR1",
                        gstin,
                        period,
                        CloneObject(response));
                }
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
            SalesSections.Any(section =>
                payload[section] is JsonArray or JsonObject))
        {
            return new SalesPayloadClassification(
                "GSTR1",
                monthlyGstin,
                monthlyPeriod,
                CloneObject(payload));
        }

        return SalesPayloadClassification.Empty();
    }

    private static TwoBPayloadClassification ClassifyTwoBPayload(
        JsonObject payload)
    {
        var winRequest = Object(
            payload["winreq"]);

        var response = Object(
            payload["resp"]);

        if (winRequest is not null &&
            response is not null)
        {
            var requestId = FirstText(
                    winRequest,
                    "reqid",
                    "FormName",
                    "formname")
                .ToUpperInvariant();

            var data = Object(
                response["data"]);

            if ((requestId.Contains(
                     "GSTR2B",
                     StringComparison.Ordinal) ||
                 requestId.Contains(
                     "GSTR-2B",
                     StringComparison.Ordinal)) &&
                data is not null)
            {
                var gstin = NormalizeGstin(
                    FirstText(
                        data,
                        "gstin"));

                if (gstin.Length == 0)
                {
                    gstin = NormalizeGstin(
                        FirstText(
                            winRequest,
                            "gstin"));
                }

                var period = NormalizePeriod(
                    FirstText(
                        data,
                        "rtnprd"));

                if (period.Length == 0)
                {
                    period = NormalizePeriod(
                        FirstText(
                            winRequest,
                            "retnprd",
                            "rtnprd"));
                }

                var normalizedData = CloneObject(
                    data);

                normalizedData["gstin"] = gstin;
                normalizedData["rtnprd"] = period;

                return new TwoBPayloadClassification(
                    gstin,
                    period,
                    new JsonObject
                    {
                        ["data"] = normalizedData
                    });
            }
        }

        var directData = Object(
            payload["data"]);

        if (directData is not null)
        {
            var gstin = NormalizeGstin(
                Text(directData["gstin"]));

            var period = NormalizePeriod(
                Text(directData["rtnprd"]));

            if (gstin.Length == 15 &&
                period.Length == 6)
            {
                return new TwoBPayloadClassification(
                    gstin,
                    period,
                    CloneObject(payload));
            }
        }

        return new TwoBPayloadClassification(
            string.Empty,
            string.Empty,
            new JsonObject());
    }

    private static IReadOnlyList<JsonPayload> ReadJsonPayloads(
        FileInfo file,
        ICollection<SalesImportIssue> issues)
    {
        var rows = new List<JsonPayload>();

        try
        {
            if (file.Extension.Equals(
                    ".json",
                    StringComparison.OrdinalIgnoreCase))
            {
                using var stream = new FileStream(
                    file.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite |
                    FileShare.Delete);

                var node = JsonNode.Parse(
                    stream);

                if (node is JsonObject payload)
                {
                    rows.Add(
                        new JsonPayload(
                            file.Name,
                            payload));
                }

                return rows;
            }

            if (!file.Extension.Equals(
                    ".zip",
                    StringComparison.OrdinalIgnoreCase))
            {
                return rows;
            }

            using var fileStream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite |
                FileShare.Delete);

            using var archive = new ZipArchive(
                fileStream,
                ZipArchiveMode.Read,
                leaveOpen: false);

            foreach (var entry in archive.Entries
                         .Where(entry =>
                             !string.IsNullOrWhiteSpace(
                                 entry.Name) &&
                             entry.FullName.EndsWith(
                                 ".json",
                                 StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    using var stream = entry.Open();
                    var node = JsonNode.Parse(
                        stream);

                    if (node is JsonObject payload)
                    {
                        rows.Add(
                            new JsonPayload(
                                entry.FullName,
                                payload));
                    }
                }
                catch (Exception exception)
                {
                    issues.Add(
                        NewIssue(
                            file.FullName,
                            "ZIP JSON",
                            $"{entry.FullName}: {exception.Message}"));
                }
            }
        }
        catch (Exception exception)
        {
            issues.Add(
                NewIssue(
                    file.FullName,
                    "JSON/ZIP",
                    exception.Message));
        }

        return rows;
    }

    private static IEnumerable<FileInfo> EnumerateSourceFiles(
        DirectoryInfo folder,
        SearchOption searchOption,
        ICollection<SalesImportIssue> issues)
    {
        try
        {
            return folder
                .EnumerateFiles("*", searchOption)
                .Where(file =>
                    file.Extension.Equals(
                        ".json",
                        StringComparison.OrdinalIgnoreCase) ||
                    file.Extension.Equals(
                        ".zip",
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(file =>
                    file.DirectoryName,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(file =>
                    file.Name,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception)
        {
            issues.Add(
                NewIssue(
                    folder.FullName,
                    "Search",
                    exception.Message));

            return [];
        }
    }

    private static DirectoryInfo? FindFinancialYearRoot(
        DirectoryInfo root,
        string financialYear)
    {
        var range = FinancialYearRange(
            financialYear);

        if (range is null)
        {
            return null;
        }

        var names = new[]
        {
            $"{range.Value.Start:0000}-{range.Value.End:0000}",
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
            var child = FindChildDirectory(
                root,
                name);

            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private static DirectoryInfo? FindChildDirectory(
        DirectoryInfo parent,
        string name)
    {
        try
        {
            return parent
                .EnumerateDirectories()
                .FirstOrDefault(directory =>
                    string.Equals(
                        directory.Name,
                        name,
                        StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static DateTime? EmbeddedTimestamp(
        JsonObject payload)
    {
        var winRequest = Object(
            payload["winreq"]);

        var response = Object(
            payload["resp"]);

        var dateText = FirstText(
            winRequest,
            "Respdate",
            "respdate");

        var timeText = FirstText(
            winRequest,
            "resptime",
            "RespTime");

        var combined = string.Join(
            " ",
            new[]
            {
                dateText,
                timeText
            }.Where(value =>
                !string.IsNullOrWhiteSpace(value)));

        var parsed = ParseTimestamp(
            combined);

        if (parsed.HasValue)
        {
            return parsed.Value;
        }

        foreach (var candidate in new[]
                 {
                     FirstText(response, "gendt"),
                     FirstText(payload, "gendt"),
                     FirstText(Object(payload["data"]), "gendt")
                 })
        {
            parsed = ParseTimestamp(
                candidate);

            if (parsed.HasValue)
            {
                return parsed.Value;
            }
        }

        return null;
    }

    private static DateTime? ParseTimestamp(
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] formats =
        [
            "dd/MM/yyyy HH:mm:ss",
            "dd/MM/yyyy HH:mm",
            "dd-MM-yyyy HH:mm:ss",
            "dd-MM-yyyy HH:mm",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss",
            "dd/MM/yyyy",
            "dd-MM-yyyy",
            "yyyy-MM-dd"
        ];

        if (DateTime.TryParseExact(
                value.Trim(),
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var exact))
        {
            return exact;
        }

        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var parsed)
            ? parsed
            : null;
    }

    private static DateTime? ParseDate(
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] formats =
        [
            "dd-MM-yyyy",
            "dd/MM/yyyy",
            "yyyy-MM-dd",
            "d-M-yyyy",
            "d/M/yyyy"
        ];

        if (DateTime.TryParseExact(
                value.Trim(),
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var exact))
        {
            return exact.Date;
        }

        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed.Date
            : null;
    }

    private static bool DateInFinancialYear(
        DateTime? value,
        string financialYear)
    {
        var range = FinancialYearDateRange(
            financialYear);

        return value.HasValue &&
               range.HasValue &&
               value.Value.Date >=
               range.Value.Start &&
               value.Value.Date <=
               range.Value.End;
    }

    private static bool MonthInFinancialYear(
        string monthKey,
        string financialYear)
    {
        var month = PeriodFirstDate(
            monthKey);

        return DateInFinancialYear(
            month,
            financialYear);
    }

    private static DateTime? PeriodFirstDate(
        string monthKey)
    {
        if (monthKey.Length != 6 ||
            !int.TryParse(
                monthKey[..4],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var year) ||
            !int.TryParse(
                monthKey[4..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var month) ||
            month is < 1 or > 12)
        {
            return null;
        }

        return new DateTime(
            year,
            month,
            1);
    }

    private static DateTime? PeriodLastDate(
        string monthKey)
    {
        var first = PeriodFirstDate(
            monthKey);

        return first?.AddMonths(1)
            .AddDays(-1);
    }

    private static string NormalizeFinancialYear(
        string value)
    {
        var match = Regex.Match(
            value.Trim(),
            @"(?<start>\d{2,4})\s*[-/]\s*(?<end>\d{2,4})",
            RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            var digits = string.Concat(
                value.Where(char.IsDigit));

            if (digits.Length == 4)
            {
                match = Regex.Match(
                    digits,
                    @"(?<start>\d{2})(?<end>\d{2})",
                    RegexOptions.CultureInvariant);
            }
        }

        if (!match.Success)
        {
            return string.Empty;
        }

        var startText =
            match.Groups["start"].Value;

        var endText =
            match.Groups["end"].Value;

        var start = int.Parse(
            startText,
            CultureInfo.InvariantCulture);

        if (startText.Length == 2)
        {
            start += 2000;
        }

        var end = int.Parse(
            endText,
            CultureInfo.InvariantCulture);

        if (endText.Length == 2)
        {
            end += 2000;
        }

        if (end != start + 1)
        {
            return string.Empty;
        }

        return
            $"{start:0000}-{end % 100:00}";
    }

    private static (int Start, int End)? FinancialYearRange(
        string financialYear)
    {
        var normalized = NormalizeFinancialYear(
            financialYear);

        if (normalized.Length == 0)
        {
            return null;
        }

        var start = int.Parse(
            normalized[..4],
            CultureInfo.InvariantCulture);

        return (start, start + 1);
    }

    private static (DateTime Start, DateTime End)? FinancialYearDateRange(
        string financialYear)
    {
        var range = FinancialYearRange(
            financialYear);

        return range.HasValue
            ? (
                new DateTime(
                    range.Value.Start,
                    4,
                    1),
                new DateTime(
                    range.Value.End,
                    3,
                    31))
            : null;
    }

    private static string NormalizePeriod(
        string value)
    {
        var digits = string.Concat(
            value.Where(char.IsDigit));

        if (digits.Length == 6)
        {
            var firstFour = int.Parse(
                digits[..4],
                CultureInfo.InvariantCulture);

            var lastTwo = int.Parse(
                digits[4..],
                CultureInfo.InvariantCulture);

            if (firstFour is >= 2000 and <= 2200 &&
                lastTwo is >= 1 and <= 12)
            {
                return
                    $"{firstFour:0000}{lastTwo:00}";
            }

            var firstTwo = int.Parse(
                digits[..2],
                CultureInfo.InvariantCulture);

            var lastFour = int.Parse(
                digits[2..],
                CultureInfo.InvariantCulture);

            if (firstTwo is >= 1 and <= 12 &&
                lastFour is >= 2000 and <= 2200)
            {
                return
                    $"{lastFour:0000}{firstTwo:00}";
            }
        }

        return string.Empty;
    }

    private static string NormalizeGstin(
        string? value) =>
        PurchaseImportUtilities.NormalizeGstin(
            value);

    private static string NormalizeHeader(
        string value) =>
        PurchaseImportUtilities.NormalizeHeader(
            value);

    private static string NormalizeFlag(
        string value) =>
        string.Concat(
                value.Where(char.IsLetterOrDigit))
            .ToUpperInvariant();

    private static string PayloadHash(
        JsonObject payload)
    {
        var json = payload.ToJsonString(
            new JsonSerializerOptions
            {
                WriteIndented = false
            });

        return Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    json)));
    }

    private static string FileHash(
        string filePath)
    {
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite |
            FileShare.Delete);

        return Convert.ToHexString(
            SHA256.HashData(
                stream));
    }

    private static double MasterSelectionTimestamp(
        FileInfo file)
    {
        var matches = Regex.Matches(
            Path.GetFileNameWithoutExtension(
                file.Name)
                ?? string.Empty,
            @"(?<!\d)(\d{8})(?!\d)",
            RegexOptions.CultureInvariant);

        for (var index = matches.Count - 1;
             index >= 0;
             index--)
        {
            if (DateTime.TryParseExact(
                    matches[index].Groups[1].Value,
                    "ddMMyyyy",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var embedded))
            {
                return embedded
                    .Add(
                        file.LastWriteTime.TimeOfDay)
                    .ToOADate();
            }
        }

        return file.LastWriteTimeUtc.ToOADate();
    }

    private static decimal Number(
        JsonNode? node)
    {
        if (node is null)
        {
            return 0m;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<decimal>(
                    out var decimalValue))
            {
                return decimalValue;
            }

            if (value.TryGetValue<double>(
                    out var doubleValue))
            {
                return Convert.ToDecimal(
                    doubleValue,
                    CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<string>(
                    out var text))
            {
                return PurchaseImportUtilities.ParseDecimal(
                    text);
            }
        }

        return PurchaseImportUtilities.ParseDecimal(
            node.ToString());
    }

    private static decimal FirstNumber(
        JsonObject value,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (value[name] is not null)
            {
                return Number(
                    value[name]);
            }
        }

        return 0m;
    }

    private static string Text(
        JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value &&
            value.TryGetValue<string>(
                out var text))
        {
            return text.Trim();
        }

        return node.ToString().Trim();
    }

    private static string FirstText(
        JsonObject? value,
        params string[] names)
    {
        if (value is null)
        {
            return string.Empty;
        }

        foreach (var name in names)
        {
            var text = Text(
                value[name]);

            if (text.Length > 0)
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static JsonObject? Object(
        JsonNode? node) =>
        node as JsonObject;

    private static JsonArray? Array(
        JsonNode? node) =>
        node as JsonArray;

    private static JsonObject CloneObject(
        JsonObject value) =>
        JsonNode.Parse(
            value.ToJsonString()) as JsonObject
        ?? new JsonObject();

    private static SalesImportIssue NewIssue(
        string sourceFile,
        string section,
        string message) =>
        new()
        {
            SourceFile = Path.GetFileName(
                sourceFile)
                ?? string.Empty,
            Section = section,
            RowNumber = 0,
            Message = message
        };

    private sealed class SalesSourceCandidate
    {
        public required string SourcePath { get; init; }
        public required string SourceName { get; init; }
        public required string InternalName { get; init; }
        public required string Period { get; init; }
        public required JsonObject Payload { get; init; }
        public required JsonObject RawPayload { get; init; }
        public required DateTime ModifiedUtc { get; init; }
        public DateTime? EmbeddedTimestamp { get; init; }
        public required DateTime SelectionTimestamp { get; init; }
        public required string PayloadHash { get; init; }
        public required string SourceOrigin { get; init; }
        public required string Branch { get; init; }
        public required string Scope { get; init; }
        public bool ReadOnly { get; init; }
        public required string VerificationStatus { get; init; }
    }

    private sealed record SalesSourceResolution(
        List<SalesSourceCandidate> Sources,
        SalesSourceVerification Verification);

    private sealed record JsonPayload(
        string InternalName,
        JsonObject Payload);

    private sealed record SalesPayloadClassification(
        string Form,
        string Gstin,
        string Period,
        JsonObject NormalizedPayload)
    {
        public static SalesPayloadClassification Empty() =>
            new(
                string.Empty,
                string.Empty,
                string.Empty,
                new JsonObject());
    }

    private sealed record TwoBPayloadClassification(
        string Gstin,
        string Period,
        JsonObject NormalizedPayload);

    private sealed record TwoBSourceCandidate(
        JsonObject Payload,
        DateTime SelectionTimestamp);

    private sealed record SalesItem(
        decimal Rate,
        decimal Taxable,
        decimal Igst,
        decimal Cgst,
        decimal Sgst,
        decimal Cess);

    private sealed record SalesLedgerSet(
        string Sales,
        string Igst,
        string Cgst,
        string Sgst);

    private sealed record CustomerNameResolution(
        string Gstin,
        string Name,
        string Source);

    private sealed record CustomerMasterReadResult(
        Dictionary<string, string> Names,
        Dictionary<string, SalesCustomerMasterNameDetail> Details,
        string SheetName,
        int HeaderRow);

    private sealed record CustomerMasterResolution(
        Dictionary<string, string> Names,
        string Status,
        string SourceOrigin,
        string SourceBranch,
        string SourcePath,
        string SourceHash,
        string SheetName,
        int HeaderRow,
        double SelectionTimestamp)
    {
        public static CustomerMasterResolution NotFound() =>
            new(
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase),
                "Not Found",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                0,
                0d);
    }
}
