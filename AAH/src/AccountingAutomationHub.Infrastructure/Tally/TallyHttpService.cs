using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Persistence;

namespace AccountingAutomationHub.Infrastructure.Tally;

public sealed class TallyHttpService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly TimeSpan _defaultRequestTimeout;
    private readonly object _erp9CoreCacheGate = new();
    private string _erp9CoreCacheCompany = string.Empty;
    private string _erp9CoreCacheXml = string.Empty;
    private DateTime _erp9CoreCacheUtc = DateTime.MinValue;
    private int _erp9CoreSnapshotPins;
    private bool _erp9CoreCacheRequiresRefresh;
    private string _erp9LastMasterReadWarning = string.Empty;
    private readonly object _erp9TraceGate = new();
    private readonly Dictionary<string, string> _erp9DiagnosticArtifacts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _runtimeDetectionGate = new();
    private TallyRuntimeFamily _runtimeFamily = TallyRuntimeFamily.Unknown;
    private DateTime _runtimeFamilyCheckedUtc = DateTime.MinValue;
    private string _runtimeDetectionEvidence = string.Empty;
    private readonly object _fullSnapshotCacheGate = new();
    private string _fullSnapshotCompanyKey = string.Empty;
    private string _fullSnapshotFinancialYear = string.Empty;
    private DateTime _fullSnapshotFrom = DateTime.MinValue;
    private DateTime _fullSnapshotTo = DateTime.MinValue;
    private DateTime _fullSnapshotUtc = DateTime.MinValue;
    private string _fullSnapshotXml = string.Empty;
    private IReadOnlyList<TallyVoucherIndexRecord> _fullSnapshotVouchers = Array.Empty<TallyVoucherIndexRecord>();
    private IReadOnlyList<TallyVoucherXmlSnapshot> _fullSnapshotVoucherXml = Array.Empty<TallyVoucherXmlSnapshot>();
    private bool _fullSnapshotValidated;
    private string _lastFullSnapshotValidationError = string.Empty;
    private readonly object _fullSnapshotDiagnosticGate = new();
    private string _lastFullSnapshotRequestXml = string.Empty;
    private string _lastFullSnapshotResponseXml = string.Empty;
    private string _lastFullSnapshotError = string.Empty;
    private const string FullSnapshotCollectionName = "AAHFullCompanySnapshot";
    private static readonly string[] FullSnapshotRequiredGroups =
    [
        "Capital Account",
        "Bank Accounts",
        "Cash-in-Hand",
        "Sundry Debtors",
        "Sundry Creditors",
        "Indirect Expenses",
        "Purchase Accounts",
        "Sales Accounts"
    ];
    private int _activeExportRequests;
    private int _activeImportRequests;

    private enum TallyRuntimeFamily
    {
        Unknown = 0,
        PrimeOrModern = 1,
        Erp9 = 2
    }

    public TallyHttpService(
        string host = "127.0.0.1",
        int port = 9000,
        TimeSpan? timeout = null)
    {
        _endpoint = new Uri($"http://{host}:{port}/", UriKind.Absolute);
        _defaultRequestTimeout = timeout ?? TimeSpan.FromSeconds(20);
        _httpClient = new HttpClient
        {
            // Each Tally operation has its own explicit timeout. A single full-company
            // snapshot or a legitimate voucher import may take longer than the old global
            // 20-second HttpClient timeout and must not be mislabeled as "not responding".
            Timeout = Timeout.InfiniteTimeSpan
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(
                "AccountingAutomationHub",
                "4.0.0"));
    }

    // ERP 9 is exposed to the desktop coordinator so the five-second scheduler can
    // apply its low-priority/non-blocking budget while keeping normal Master, Voucher,
    // report/statistics, import and export processing enabled through ERP 9-safe XML.
    public bool IsLocalErp9Runtime => ShouldUseErp9CompatibilityProbe();
    public bool IsExportInProgress => Volatile.Read(ref _activeExportRequests) > 0;
    public bool IsImportInProgress => Volatile.Read(ref _activeImportRequests) > 0;
    public string ActiveTransportStatus => IsImportInProgress
        ? "Importing to Tally"
        : IsExportInProgress ? "Reading from Tally" : string.Empty;

    /// <summary>
    /// Passive transport heartbeat. Opens and closes only a TCP connection to Tally's HTTP port;
    /// it sends no HTTP/XML payload, does not acquire TallyRequestGate and cannot hold ERP 9's
    /// single-threaded XML request path. Intended for the 5-second ERP 9 liveness indicator only.
    /// </summary>
    public async Task<bool> ProbeEndpointAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromMilliseconds(750));
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(_endpoint.Host, _endpoint.Port, deadline.Token).ConfigureAwait(false);
            return tcp.Connected;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static readonly (string Key, string Collection)[] NceFinancialStatementDetailCollections =
    [
        ("CY_STOCK_ITEM", "TSPL__Sch3__StockItemCY"),
        ("CY_STOCK_LEDGER", "TSPL__Sch3__StockLedgerCY"),
        ("PY_STOCK_ITEM", "TSPL__Sch3__StockItemPY"),
        ("PY_STOCK_LEDGER", "TSPL__Sch3__StockLedgerPY"),
        ("CY_CREDITOR_LEDGER", "TSPL__Sch3__SCLedgerWiseCY"),
        ("CY_CREDITOR_BILL", "TSPL__Sch3__SCBillwiseCY"),
        ("PY_CREDITOR_LEDGER", "TSPL__Sch3__SCLedgerWisePY"),
        ("PY_CREDITOR_BILL", "TSPL__Sch3__SCBillWisePY"),
        ("CY_DEBTOR_LEDGER", "TSPL__Sch3__SDLedgerWiseCY"),
        ("CY_DEBTOR_BILL", "TSPL__Sch3__SDBillwiseCY"),
        ("PY_DEBTOR_LEDGER", "TSPL__Sch3__SDLedgerWisePY"),
        ("PY_DEBTOR_BILL", "TSPL__Sch3__SDBillWisePY")
    ];

    private static readonly string[] NceFinancialStatementFetchFields =
    [
        "Name", "MasterID", "Parent", "_PrimaryGroup", "NatureofGrp", "ReservedGroup",
        "OpeningBalance", "DebitTotals", "CreditTotals", "ClosingBalance",
        "TBalOpening", "TBalDebits", "TBalCredits", "TBalClosing",
        "OpeningValue", "ClosingValue", "LedIsStockLedger", "IsLedgerProfit",
        "IsSundryDebtorCreditor", "TaxType", "MSMEType", "MSMERegistrationDetails.*",
        "IsMSMEParty", "ParentGroupName", "PartyName", "BillDate", "BillCreditPeriod",
        "PartyBillDate", "PartyRefNo", "PartyDueDate", "OnAccountValue", "StockItemName",
        "StockGroup", "StockCategory", "Category", "C1MappingGroup", "C2MappingGroup",
        "C3MappingGroup", "MappingHeadOne", "MappingHeadTwo", "NatureSortIndex",
        "GroupSortIndex", "TBCmpProfit"
    ];

    /// <summary>
    /// Reads the Financial Statements/NCE plug-in through the exact background route proven
    /// by the R53 runtime capture. XMLStatement is the accounting/presentation authority;
    /// the twelve exact non-TB CollNames are supplementary detail/classification only.
    /// This method never calls ListofObjects, SOURCECOLLECTION wrappers or internal/support names.
    /// </summary>
    public async Task<NceFinancialStatementFeed> ExportNceFinancialStatementFeedAsync(
        string companyName,
        string financialYear,
        string previousCompanyName = "",
        CancellationToken cancellationToken = default)
    {
        var requestedCompany = (companyName ?? string.Empty).Trim();
        if (requestedCompany.Length == 0) throw new ArgumentException("Tally company name is required.", nameof(companyName));
        if (!TryParseFinancialYearRange(financialYear, out var fromDate, out var toDate))
            throw new ArgumentException($"Financial Year '{financialYear}' is not in YYYY-YY format.", nameof(financialYear));

        // R53 FIX1 fail-closed readiness: an open TCP port does not prove that Tally's XML
        // report engine is clean after a previous client-side timeout. A tiny built-in direct
        // collection must answer before any NCE request is attempted.
        var readinessXml = await PostAsync(
            BuildNceReadinessRequest(),
            cancellationToken,
            TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var readiness = XDocument.Parse(CleanXml(readinessXml));
        var availableCompanies = readiness.Descendants()
            .Where(x => x.Name.LocalName.Equals("COMPANY", StringComparison.OrdinalIgnoreCase))
            .Select(x => (string?)x.Attribute("NAME") ?? ChildValue(x, "NAME"))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (availableCompanies.Length > 0 &&
            !availableCompanies.Contains(requestedCompany, StringComparer.OrdinalIgnoreCase))
        {
            var normalized = NormalizeCompanyLookup(requestedCompany);
            var match = availableCompanies.FirstOrDefault(x => NormalizeCompanyLookup(x).Equals(normalized, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(match)) requestedCompany = match;
        }

        var previousCompany = string.IsNullOrWhiteSpace(previousCompanyName)
            ? requestedCompany
            : previousCompanyName.Trim();
        var currentYearEnd = toDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var previousYearEnd = toDate.AddYears(-1).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var context = BuildNceFinancialStatementContext(
            requestedCompany,
            fromDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            currentYearEnd,
            currentYearEnd,
            previousYearEnd,
            previousCompany);

        var reportXml = await PostAsync(
            BuildNceXmlStatementRequest(context),
            cancellationToken,
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var report = XDocument.Parse(CleanXml(reportXml));
        var current = ParseNcePeriod(report, "CURRENTCOMPANYDETAILS");
        var previous = ParseNcePeriod(report, "PREVIOUSCOMPANYDETAILS");
        if (current.TrialBalance.Count == 0 && previous.TrialBalance.Count == 0)
            throw new InvalidDataException("The NCE XMLStatement returned no Current-Year or Previous-Year Trial Balance rows.");

        var details = new Dictionary<string, IReadOnlyList<NceFinancialStatementDetailRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, collection) in NceFinancialStatementDetailCollections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detailXml = await PostAsync(
                BuildNceDirectCollectionRequest(context, collection),
                cancellationToken,
                TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            details[key] = ParseNceDetailRows(detailXml, collection);
        }

        return new NceFinancialStatementFeed
        {
            CompanyName = requestedCompany,
            FinancialYear = financialYear,
            CurrentYear = current,
            PreviousYear = previous,
            DetailCollections = details
        };
    }

    private static string BuildNceReadinessRequest() => """
<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>EXPORT</TALLYREQUEST><TYPE>COLLECTION</TYPE><ID>List of Primary Companies</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT></STATICVARIABLES><FETCHLIST><FETCH>Name</FETCH></FETCHLIST></DESC></BODY></ENVELOPE>
""";

    private static string BuildNceFinancialStatementContext(
        string company,
        string fromDate,
        string toDate,
        string currentYear,
        string previousYear,
        string previousCompany)
    {
        static string Tag(string name, string value) => $"<{name}>{System.Security.SecurityElement.Escape(value)}</{name}>";
        return string.Join("\n", new[]
        {
            "<SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>",
            Tag("SVCURRENTCOMPANY", company),
            Tag("SVFROMDATE", fromDate),
            Tag("SVTODATE", toDate),
            "<TSPL__FS__REPORTNAME>NCE</TSPL__FS__REPORTNAME>",
            "<TSPL__SCH3__YEAROPTION>Current Year &amp; Previous Year</TSPL__SCH3__YEAROPTION>",
            Tag("TSPL__SCH3__CURRENTYEAR", currentYear),
            Tag("TSPL__SCH3__PREVYEAR", previousYear),
            Tag("TSPL__SCH3__PREVYEARCMPNAME", previousCompany),
            "<TSPL__SCH3__PREVYEARDIFFDATA>No</TSPL__SCH3__PREVYEARDIFFDATA>",
            "<TSPL__SCH3__ALLOCTYPE>Party-wise</TSPL__SCH3__ALLOCTYPE>",
            "<TSPL__SCH3__STOCKTYPE>Ledger-wise</TSPL__SCH3__STOCKTYPE>",
            "<TSPL__SCH3__INCLBILLS>Yes</TSPL__SCH3__INCLBILLS>",
            "<TSPL__SCH3__INCLSTKSUMM>Yes</TSPL__SCH3__INCLSTKSUMM>",
            "<TSPL__SCH3__INCLLEDBAL>No</TSPL__SCH3__INCLLEDBAL>",
            "<TSPL__SCH3__EXCLSTKZEROBAL>No</TSPL__SCH3__EXCLSTKZEROBAL>"
        });
    }

    private static string BuildNceXmlStatementRequest(string context) => $"""
<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>EXPORT</TALLYREQUEST><TYPE>DATA</TYPE><ID>TSPL__Sch3__XMLStatement</ID></HEADER><BODY><DESC><STATICVARIABLES>
{context}
</STATICVARIABLES></DESC></BODY></ENVELOPE>
""";

    private static string BuildNceDirectCollectionRequest(string context, string collection)
    {
        var fetch = string.Join("\n", NceFinancialStatementFetchFields.Select(x => $"<FETCH>{System.Security.SecurityElement.Escape(x)}</FETCH>"));
        return $"""
<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>EXPORT</TALLYREQUEST><TYPE>COLLECTION</TYPE><ID>{System.Security.SecurityElement.Escape(collection)}</ID></HEADER><BODY><DESC><STATICVARIABLES>
{context}
</STATICVARIABLES><FETCHLIST>
{fetch}
</FETCHLIST></DESC></BODY></ENVELOPE>
""";
    }

    private static NceFinancialStatementPeriod ParseNcePeriod(XDocument document, string sectionName)
    {
        var section = document.Descendants().FirstOrDefault(x => x.Name.LocalName.Equals(sectionName, StringComparison.OrdinalIgnoreCase));
        if (section is null) return new NceFinancialStatementPeriod();

        // R52 FIXED9: section presence is separate from row count. The NCE plug-in legitimately
        // emits an empty Trade Receivables section when the statutory balance is zero. Losing that
        // distinction let a non-authoritative final-detail collection repopulate receivables.
        var stockSection = Child(section, "STOCKSUMMARY");
        var receivableSection = Child(section, "TRADERECEIVABLES");
        var payableSection = Child(section, "TRADEPAYABLES");

        var tb = Child(section, "TRIALBALANCE")?.Elements().Select(ParseNceTrialBalanceRow).Where(x => x.LedgerName.Length > 0).ToArray()
                 ?? Array.Empty<NceTrialBalanceRow>();
        var stock = stockSection?.Elements().Select(ParseNceStockSummaryRow).Where(x => x.StockItemName.Length > 0).ToArray()
                    ?? Array.Empty<NceStockSummaryRow>();
        var ar = receivableSection?.Elements().Select(ParseNcePartyOutstandingRow).Where(x => x.PartyName.Length > 0).ToArray()
                 ?? Array.Empty<NcePartyOutstandingRow>();
        var ap = payableSection?.Elements().Select(ParseNcePartyOutstandingRow).Where(x => x.PartyName.Length > 0).ToArray()
                 ?? Array.Empty<NcePartyOutstandingRow>();
        return new NceFinancialStatementPeriod
        {
            TrialBalance = tb,
            StockSummary = stock,
            TradeReceivables = ar,
            TradePayables = ap,
            HasStockSummaryAuthority = stockSection is not null,
            HasTradeReceivablesAuthority = receivableSection is not null,
            HasTradePayablesAuthority = payableSection is not null
        };
    }

    private static NceTrialBalanceRow ParseNceTrialBalanceRow(XElement x) => new()
    {
        MasterId = ChildValue(x, "MASTERID"), LedgerName = ChildValue(x, "LEDGERNAME"), Parent = ChildValue(x, "PARENT"), PrimaryGroup = ChildValue(x, "PRIMARYGROUP"),
        ReserveGroup = ChildValue(x, "RESERVEGROUP"), NatureOfGroup = ChildValue(x, "NATUREOFGROUP"),
        OpeningBalance = NceDecimal(ChildValue(x, "OPENINGBALANCE")), DebitAmount = NceDecimal(ChildValue(x, "DEBITAMOUNT")),
        CreditAmount = NceDecimal(ChildValue(x, "CREDITAMOUNT")), ClosingBalance = NceDecimal(ChildValue(x, "CLOSINGBALANCE")),
        MappingHeadOne = ChildValue(x, "MAPPINGHEADONE"), MappingHeadTwo = ChildValue(x, "MAPPINGHEADTWO")
    };

    private static NceStockSummaryRow ParseNceStockSummaryRow(XElement x) => new()
    {
        StockItemName = ChildValue(x, "STOCKITEMNAME"), StockGroup = ChildValue(x, "STOCKGROUP"), StockCategory = ChildValue(x, "STOCKCATEGORY"),
        OpeningValue = NceDecimal(ChildValue(x, "OPENINGVALUE")), ClosingValue = NceDecimal(ChildValue(x, "CLOSINGVALUE"))
    };

    private static NcePartyOutstandingRow ParseNcePartyOutstandingRow(XElement x) => new()
    {
        PartyName = ChildValue(x, "PARTYNAME"), BillDate = ChildValue(x, "BILLDATE"), BillRef = ChildValue(x, "BILLREF"),
        PendingAmount = NceDecimal(ChildValue(x, "PENDINGAMOUNT")), OnAccountAmount = NceDecimal(ChildValue(x, "ONACCAMOUNT")),
        MappingHeadOne = ChildValue(x, "MAPPINGHEADONE")
    };

    private static IReadOnlyList<NceFinancialStatementDetailRow> ParseNceDetailRows(string xml, string collection)
    {
        var document = XDocument.Parse(CleanXml(xml));
        var node = document.Descendants().FirstOrDefault(x => x.Name.LocalName.Equals("COLLECTION", StringComparison.OrdinalIgnoreCase));
        if (node is null) return Array.Empty<NceFinancialStatementDetailRow>();
        return node.Elements().Select(x => new NceFinancialStatementDetailRow
        {
            CollectionName = collection,
            MasterId = ChildValue(x, "MASTERID"),
            Name = ((string?)x.Attribute("NAME") ?? ChildValue(x, "NAME")).Trim(),
            Parent = ChildValue(x, "PARENT"), PrimaryGroup = ChildValue(x, "_PRIMARYGROUP"), ParentGroupName = ChildValue(x, "PARENTGROUPNAME"),
            PartyName = ChildValue(x, "PARTYNAME"), PartyRefNo = ChildValue(x, "PARTYREFNO"), BillDate = ChildValue(x, "BILLDATE"),
            ClosingBalance = NceDecimal(ChildValue(x, "CLOSINGBALANCE")), TallyClosingBalance = NceDecimal(ChildValue(x, "TBALCLOSING")),
            OnAccountValue = NceDecimal(ChildValue(x, "ONACCOUNTVALUE")),
            OpeningValue = NceDecimal(ChildValue(x, "OPENINGVALUE")), ClosingValue = NceDecimal(ChildValue(x, "CLOSINGVALUE")),
            StockItemName = ChildValue(x, "STOCKITEMNAME"), StockGroup = ChildValue(x, "STOCKGROUP"), StockCategory = ChildValue(x, "STOCKCATEGORY"),
            C1MappingGroup = ChildValue(x, "C1MAPPINGGROUP"), C2MappingGroup = ChildValue(x, "C2MAPPINGGROUP"), C3MappingGroup = ChildValue(x, "C3MAPPINGGROUP"),
            MappingHeadOne = ChildValue(x, "MAPPINGHEADONE"), MappingHeadTwo = ChildValue(x, "MAPPINGHEADTWO"), MsmeType = ChildValue(x, "MSMETYPE"),
            IsMsmeParty = ChildValue(x, "ISMSMEPARTY").Equals("Yes", StringComparison.OrdinalIgnoreCase)
        }).ToArray();
    }

    private static XElement? Child(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(x => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string ChildValue(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(x => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim() ?? string.Empty;

    private static decimal NceDecimal(string value)
    {
        var normalized = (value ?? string.Empty).Replace(",", string.Empty, StringComparison.Ordinal).Trim();
        return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;
    }

    private static bool TryParseFinancialYearRange(string financialYear, out DateTime from, out DateTime to)
    {
        from = default; to = default;
        var match = Regex.Match(financialYear ?? string.Empty, @"(?<start>20\d{2})\D*(?<end>\d{2,4})");
        if (!match.Success || !int.TryParse(match.Groups["start"].Value, out var startYear)) return false;
        var endToken = match.Groups["end"].Value;
        if (!int.TryParse(endToken, out var endYear)) return false;
        if (endToken.Length == 2) endYear += (startYear / 100) * 100;
        if (endYear < startYear) endYear += 100;
        from = new DateTime(startYear, 4, 1);
        to = new DateTime(endYear, 3, 31);
        return true;
    }

    private static string NormalizeCompanyLookup(string value) =>
        new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public bool HasValidatedFullSnapshot
    {
        get
        {
            lock (_fullSnapshotCacheGate) return _fullSnapshotValidated;
        }
    }

    public string LastFullSnapshotValidationError
    {
        get
        {
            lock (_fullSnapshotCacheGate) return _lastFullSnapshotValidationError;
        }
    }

    public string Erp9LastMasterReadWarning
    {
        get
        {
            lock (_erp9CoreCacheGate) return _erp9LastMasterReadWarning;
        }
    }

    public async Task<IReadOnlyList<TallyCompanyInfo>> DiscoverCompaniesAsync(
        CancellationToken cancellationToken = default,
        bool includeLightweightCounts = true)
    {
        // FIX3 runtime authority: identify the connected Tally product from the live HTTP/TDL
        // endpoint before choosing a compatibility route. Process name alone is never enough
        // to classify ERP 9; current Tally builds may also run from Tally.exe.
        await RefreshRuntimeFamilyAsync(cancellationToken).ConfigureAwait(false);
        var useErp9Compatibility = ShouldUseErp9CompatibilityProbe();

        // Prime keeps its established multi-company discovery. ERP 9 uses the
        // current-company TDL probe FIRST. The previous sequence posted List of Primary
        // Companies before the ERP 9 probe; on the user's older ERP 9 this could occupy
        // the HTTP/UI thread before AAH even reached the compatible request.
        IReadOnlyList<string> names = Array.Empty<string>();
        Exception? discoveryException = null;

        if (useErp9Compatibility)
        {
            try
            {
                var response = await PostAsync(
                    BuildCurrentCompanyCompatibilityProbeRequest(),
                    cancellationToken,
                    Timeout.InfiniteTimeSpan);
                var current = ParseCurrentCompanyCompatibilityProbe(response);
                if (current is not null)
                {
                    await PopulateCompanyBookDatesAsync(current, cancellationToken, useErp9CompatibilityTimeout: true);
                    current.LastSeenUtc = DateTime.UtcNow;
                    current.FinancialYear = TallyCompanyInfo.NormalizeFinancialYear(
                        current.FinancialYear,
                        current.BooksFrom.Length > 0 ? current.BooksFrom : current.StartingFrom);
                    current.CompanyKey = TallyCompanyInfo.BuildCompanyKey(
                        current.Name,
                        current.CompanyNumber,
                        current.Gstin,
                        current.FinancialYear,
                        current.BooksFrom);
                    return new[] { current };
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                discoveryException = exception;
            }

            // Fallback only after the current-company probe has actually returned a
            // failure/no usable company. Fix110 R4 never abandons a pending ERP 9 request
            // merely because elapsed time has crossed an application deadline.
            try
            {
                var response = await PostAsync(
                    BuildDiscoveryRequest(),
                    cancellationToken,
                    Timeout.InfiniteTimeSpan);
                names = ParseDiscoveredCompanyNames(response);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (discoveryException is not null)
                {
                    throw new InvalidOperationException(
                        "Tally.ERP 9 did not answer either the current-company compatibility probe or the fallback company-list probe. " +
                        $"Current company: {discoveryException.Message} Company list: {exception.Message}",
                        exception);
                }
                throw;
            }

            if (names.Count == 0)
                return Array.Empty<TallyCompanyInfo>();
        }
        else
        {
            var response = await PostAsync(BuildDiscoveryRequest(), cancellationToken);
            names = ParseDiscoveredCompanyNames(response);
            if (names.Count == 0)
                return Array.Empty<TallyCompanyInfo>();
        }

        var companies = new List<TallyCompanyInfo>();

        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var detailResponse = await PostAsync(
                    BuildCompanyObjectRequest(name),
                    cancellationToken,
                    useErp9Compatibility ? Timeout.InfiniteTimeSpan : null);

                var company = ParseCompanyObject(name, detailResponse);
                await PopulateCompanyBookDatesAsync(company, cancellationToken, useErp9Compatibility);
                if (includeLightweightCounts && !useErp9Compatibility)
                {
                    try
                    {
                        await PopulateLightweightCountsAsync(company, cancellationToken);
                    }
                    catch (Exception countException) when (countException is not OperationCanceledException)
                    {
                        // Counts/change-token are useful telemetry, not company identity.
                        // Preserve the successfully parsed GSTIN/FY/company number when a
                        // statistics function is temporarily busy instead of replacing the
                        // whole company with a name-only fallback and changing CompanyKey.
                        company.Status = "Loaded - statistics temporarily unavailable";
                        company.Error = countException.Message;
                    }
                }
                else if (includeLightweightCounts && useErp9Compatibility)
                {
                    // ERP 9 LEGACY LIVE LINK: identity/books dates are live, while
                    // Prime-oriented statistics/change-token telemetry remains disabled.
                    // Ledger/Group/opening-balance counts come from the dedicated core
                    // Master refresh performed by the coordinator.
                    company.Status = "Loaded - ERP 9 Legacy Live Link";
                }
                company.LastSeenUtc = DateTime.UtcNow;
                company.CompanyKey = TallyCompanyInfo.BuildCompanyKey(
                    company.Name,
                    company.CompanyNumber,
                    company.Gstin,
                    company.FinancialYear,
                    company.BooksFrom);
                companies.Add(company);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var fallback = new TallyCompanyInfo
                {
                    Name = name,
                    Status = "Loaded - ERP 9 compatible identity",
                    Error = exception.Message,
                    LastSeenUtc = DateTime.UtcNow
                };

                // Even when an older ERP 9 build rejects/holds the Object request,
                // its Company collection/formula paths can still provide the two
                // book dates required by AAH's fail-closed FY coverage check.
                await PopulateCompanyBookDatesAsync(fallback, cancellationToken, useErp9Compatibility);
                fallback.CompanyKey = TallyCompanyInfo.BuildCompanyKey(
                    fallback.Name,
                    fallback.CompanyNumber,
                    fallback.Gstin,
                    fallback.FinancialYear,
                    fallback.BooksFrom);
                companies.Add(fallback);
            }
        }

        return companies;
    }

    public async Task<string> ExportMastersAsync(
        TallyCompanyInfo company,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);

        var erp9Mode = ShouldUseErp9CompatibilityProbe();
        if (erp9Mode)
        {
            // Fix123: ERP 9 keeps its native compatibility transport, but Master
            // functionality is not reduced. The ERP 9 core response contains the
            // supported Ledger/Group plus Stock/Unit/VoucherType/Godown/Cost/Tax
            // Master slices collected independently to avoid one oversized blocking read.
            var cleaned = await GetErp9CoreMastersResponseAsync(company, cancellationToken);

            var builder = new StringBuilder();
            builder.Append("<AAHMASTEREXPORT><AAHCOMPANY>");
            builder.Append(XmlEscape(company.Name));
            builder.Append("</AAHCOMPANY>");
            builder.Append(RemoveXmlDeclaration(cleaned));
            builder.Append("</AAHMASTEREXPORT>");
            return builder.ToString();
        }

        // TallyPrime keeps the established complete live-Master path unchanged.
        var responses = new List<string>();
        await TryAddResponseAsync(
            responses,
            BuildAllMastersRequest(company.Name),
            cancellationToken);

        var requests = new[]
        {
            BuildCollectionRequest(company.Name, "AAH Groups", "Group", "Name,Parent,MasterID,GUID,AlterID,LedgerGSTIN,GSTIN"),
            BuildCollectionRequest(company.Name, "AAH Ledgers", "Ledger", "Name,Parent,OpeningBalance,MasterID,GUID,AlterID"),
            BuildCollectionRequest(company.Name, "AAH Voucher Types", "VoucherType", "Name,Parent,MasterID,GUID,AlterID"),
            BuildCollectionRequest(company.Name, "AAH Units", "Unit", "Name,OriginalName,IsSimpleUnit,DecimalPlaces,BaseUnits,AdditionalUnits,Conversion,Denominator,ReportingUQCName,MasterID,GUID,AlterID"),
            BuildCollectionRequest(company.Name, "AAH States", "State", "Name"),
            BuildCollectionRequest(company.Name, "AAH Currencies", "Currency", "Name"),
            BuildCollectionRequest(company.Name, "AAH Cost Categories", "CostCategory", "Name,Parent,MasterID,GUID,AlterID"),
            BuildCollectionRequest(company.Name, "AAH Cost Centres", "CostCentre", "Name,Parent,Category"),
            BuildCollectionRequest(company.Name, "AAH Stock Categories", "StockCategory", "Name,Parent,MasterID,GUID,AlterID"),
            BuildCollectionRequest(company.Name, "AAH Stock Groups", "StockGroup", "Name,Parent,MasterID,GUID,AlterID"),
            BuildCollectionRequest(company.Name, "AAH Stock Items", "StockItem", "Name,Parent,Category,BaseUnits,AdditionalUnits,Conversion,Denominator,MailingName,HSNCode,GSTTypeOfSupply,CostingMethod,ValuationMethod,IsBatchWiseOn,IsPerishableOn,HasMfgDate,MasterID,GUID,AlterID"),
            BuildCollectionRequest(company.Name, "AAH Godowns", "Godown", "Name,Parent,MasterID,GUID,AlterID"),
            BuildCollectionRequest(company.Name, "AAH Tax Classifications", "TaxClassification", "Name,Parent,MasterID,GUID,AlterID"),
            BuildCollectionRequest(company.Name, "AAH Income Tax Classifications", "IncomeTaxClassification", "Name,Parent,MasterID,GUID,AlterID"),
            BuildCollectionRequest(company.Name, "AAH Tax Units", "TaxUnit", "Name,Parent,MasterID,GUID,AlterID"),
            BuildCollectionRequest(company.Name, "AAH Income Tax Slabs", "IncomeTaxSlab", "Name,Parent,MasterID,GUID,AlterID")
        };

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TryAddResponseAsync(responses, request, cancellationToken);
        }

        if (responses.Count == 0)
            throw new InvalidOperationException("Tally did not return any supported master collection.");

        var primeBuilder = new StringBuilder();
        primeBuilder.Append("<AAHMASTEREXPORT><AAHCOMPANY>");
        primeBuilder.Append(XmlEscape(company.Name));
        primeBuilder.Append("</AAHCOMPANY>");
        foreach (var item in responses)
            primeBuilder.Append(RemoveXmlDeclaration(CleanXml(item)));
        primeBuilder.Append("</AAHMASTEREXPORT>");
        return primeBuilder.ToString();
    }

    // Fix132 explicit Stock Master diagnostic/import path. Unlike ExportMastersAsync,
    // this request is inventory-only: it does not read Ledgers/Groups and never reads
    // vouchers. ERP9 Stock Items still use the fail-closed Collection + List of Accounts
    // authority reader so a zero result is accepted only after Tally proves it.
    public async Task<string> ExportStockMastersAsync(
        TallyCompanyInfo company,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);

        var responses = new List<string>();
        if (ShouldUseErp9CompatibilityProbe())
        {
            var stockItems = await TryReadErp9StockItemMasterSliceAsync(
                company.Name, "Stock Items", "StockItem", "AAH ERP9 Stock Items",
                "Name,Parent,Category,BaseUnits,AdditionalUnits,Conversion,Denominator,MailingName,HSNCode,GSTTypeOfSupply,CostingMethod,ValuationMethod,IsBatchWiseOn,IsPerishableOn,HasMfgDate,MasterID,GUID,AlterID",
                required: false, cancellationToken).ConfigureAwait(false);
            responses.Add(stockItems);

            var optionalSlices = new (string Label, string ObjectType, string Collection, string Fetch)[]
            {
                ("Units", "Unit", "AAH ERP9 Units", "Name,OriginalName,IsSimpleUnit,DecimalPlaces,BaseUnits,AdditionalUnits,Conversion,Denominator,ReportingUQCName,MasterID,GUID,AlterID"),
                ("StockGroups", "StockGroup", "AAH ERP9 Stock Groups", "Name,Parent,MasterID,GUID,AlterID"),
                ("StockCategories", "StockCategory", "AAH ERP9 Stock Categories", "Name,Parent,MasterID,GUID,AlterID"),
                ("Godowns", "Godown", "AAH ERP9 Godowns", "Name,Parent,MasterID,GUID,AlterID")
            };
            foreach (var slice in optionalSlices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = await TryReadErp9OptionalMasterSliceAsync(
                    company.Name, slice.Label, slice.ObjectType, slice.Collection, slice.Fetch, cancellationToken)
                    .ConfigureAwait(false);
                if (response.Length > 0) responses.Add(response);
            }
        }
        else
        {
            var requests = new[]
            {
                BuildCollectionRequest(company.Name, "AAH Units", "Unit", "Name,OriginalName,IsSimpleUnit,DecimalPlaces,BaseUnits,AdditionalUnits,Conversion,Denominator,ReportingUQCName,MasterID,GUID,AlterID"),
                BuildCollectionRequest(company.Name, "AAH Stock Groups", "StockGroup", "Name,Parent,MasterID,GUID,AlterID"),
                BuildCollectionRequest(company.Name, "AAH Stock Categories", "StockCategory", "Name,Parent,MasterID,GUID,AlterID"),
                BuildCollectionRequest(company.Name, "AAH Stock Items", "StockItem", "Name,Parent,Category,BaseUnits,AdditionalUnits,Conversion,Denominator,MailingName,HSNCode,GSTTypeOfSupply,CostingMethod,ValuationMethod,IsBatchWiseOn,IsPerishableOn,HasMfgDate,MasterID,GUID,AlterID"),
                BuildCollectionRequest(company.Name, "AAH Godowns", "Godown", "Name,Parent,MasterID,GUID,AlterID")
            };
            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await TryAddResponseAsync(responses, request, cancellationToken);
            }
        }

        if (responses.Count == 0)
            throw new InvalidOperationException("Tally did not return any Stock Master authority response.");

        var builder = new StringBuilder();
        builder.Append("<AAHMASTEREXPORT><AAHCOMPANY>");
        builder.Append(XmlEscape(company.Name));
        builder.Append("</AAHCOMPANY>");
        foreach (var item in responses)
            builder.Append(RemoveXmlDeclaration(CleanXml(item)));
        builder.Append("</AAHMASTEREXPORT>");
        return builder.ToString();
    }

    /// <summary>
    /// FIX4 single-read authority. After the lightweight company/product probe, one
    /// custom TDL DATA request returns all AAH master families plus the complete FY
    /// voucher index. Parsing, Master reconciliation and voucher classification happen
    /// locally after Tally has returned this one response.
    /// </summary>
    public async Task<TallyFullCompanySnapshot> ExportFullCompanySnapshotAsync(
        TallyCompanyInfo company,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);
        fromDate = fromDate.Date;
        toDate = toDate.Date;
        if (fromDate > toDate) (fromDate, toDate) = (toDate, fromDate);

        lock (_fullSnapshotCacheGate)
        {
            _fullSnapshotValidated = false;
            _lastFullSnapshotValidationError = string.Empty;
        }

        // Fix76 bounded full authority. Keep the documented native Collection path for
        // Company/Masters, but do not ask Tally to materialize the entire FY's nested
        // Voucher object graph in the same response. Voucher authority is read in bounded
        // month slices and combined locally by MASTERID. This keeps the one central read
        // coordinator while releasing Tally between bounded requests and avoids the large
        // ~FY-wide response that can trigger Tally memory-access failures.
        var request = BuildNativeFullCompanySnapshotRequest(company.Name, fromDate, toDate, includeVouchers: false);
        lock (_fullSnapshotDiagnosticGate)
        {
            _lastFullSnapshotRequestXml = request;
            _lastFullSnapshotResponseXml = string.Empty;
            _lastFullSnapshotError = string.Empty;
        }

        string response;
        try
        {
            response = await PostExportAsync(
                request,
                cancellationToken,
                TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            lock (_fullSnapshotDiagnosticGate)
                _lastFullSnapshotResponseXml = response;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_fullSnapshotDiagnosticGate)
                _lastFullSnapshotError = exception.ToString();
            throw;
        }

        TallyFullCompanySnapshot snapshot;
        try
        {
            var masters = ParseAndValidateNativeMasterAuthoritySnapshot(response, company, fromDate, toDate);
            var vouchers = new List<TallyVoucherIndexRecord>();
            var fullVouchers = new List<TallyVoucherXmlSnapshot>();
            var month = new DateTime(fromDate.Year, fromDate.Month, 1);
            var lastMonth = new DateTime(toDate.Year, toDate.Month, 1);
            while (month <= lastMonth)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var monthFrom = month < fromDate ? fromDate : month;
                var monthEnd = month.AddMonths(1).AddDays(-1);
                var monthTo = monthEnd > toDate ? toDate : monthEnd;
                var slice = await ExportVoucherAuthorityFreshAsync(
                    company, monthFrom, monthTo, cancellationToken).ConfigureAwait(false);
                vouchers.AddRange(slice.Index);
                fullVouchers.AddRange(slice.Full);
                month = month.AddMonths(1);
            }

            var voucherByMasterId = vouchers.Where(voucher => voucher.MasterId.Trim().Length > 0)
                .GroupBy(voucher => voucher.MasterId.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(voucher => ParseNumericAlterId(voucher.AlterId)).First())
                .ToArray();
            var fullByMasterId = fullVouchers.Where(voucher => voucher.MasterId.Trim().Length > 0)
                .GroupBy(voucher => voucher.MasterId.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(voucher => voucher.OriginalXml.Length).First())
                .ToArray();
            var indexIds = voucherByMasterId.Select(voucher => voucher.MasterId.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fullIds = fullByMasterId.Select(voucher => voucher.MasterId.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!indexIds.SetEquals(fullIds))
                throw new InvalidOperationException("Bounded FY Voucher authority did not return complete full XML for every indexed MASTERID.");
            if (company.VoucherCount > 0 && voucherByMasterId.Length == 0)
                throw new InvalidOperationException($"Bounded FY Voucher authority is empty although discovery reported {company.VoucherCount:N0} voucher(s).");

            snapshot = new TallyFullCompanySnapshot
            {
                CompanyKey = masters.CompanyKey,
                FinancialYear = masters.FinancialYear,
                FromDate = fromDate,
                ToDate = toDate,
                Xml = masters.Xml,
                Vouchers = voucherByMasterId,
                FullVouchers = fullByMasterId,
                LedgerParents = masters.LedgerParents,
                GroupNames = masters.GroupNames,
                DeclaredGroupCount = masters.DeclaredGroupCount,
                DeclaredLedgerCount = masters.DeclaredLedgerCount,
                DeclaredVoucherCount = voucherByMasterId.Length,
                AuthorityAlterId = Math.Max(masters.AuthorityAlterId,
                    voucherByMasterId.Select(voucher => ParseNumericAlterId(voucher.AlterId)).DefaultIfEmpty(0L).Max()),
                CapturedUtc = DateTime.UtcNow
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_fullSnapshotDiagnosticGate)
                _lastFullSnapshotError = exception.ToString();
            lock (_fullSnapshotCacheGate)
            {
                _fullSnapshotValidated = false;
                _fullSnapshotCompanyKey = string.Empty;
                _fullSnapshotFinancialYear = string.Empty;
                _fullSnapshotFrom = DateTime.MinValue;
                _fullSnapshotTo = DateTime.MinValue;
                _fullSnapshotUtc = DateTime.MinValue;
                _fullSnapshotXml = string.Empty;
                _fullSnapshotVouchers = Array.Empty<TallyVoucherIndexRecord>();
                _fullSnapshotVoucherXml = Array.Empty<TallyVoucherXmlSnapshot>();
                _lastFullSnapshotValidationError = exception.Message;
            }

            throw new InvalidOperationException(
                "Tally returned an incomplete bounded Company/Master/Voucher authority snapshot. " +
                "It was rejected before Ledger/Group/Voucher authority or any import decision was published. " +
                exception.Message,
                exception);
        }

        lock (_fullSnapshotCacheGate)
        {
            _fullSnapshotCompanyKey = snapshot.CompanyKey;
            _fullSnapshotFinancialYear = snapshot.FinancialYear;
            _fullSnapshotFrom = snapshot.FromDate;
            _fullSnapshotTo = snapshot.ToDate;
            _fullSnapshotUtc = snapshot.CapturedUtc;
            _fullSnapshotXml = snapshot.Xml;
            _fullSnapshotVouchers = snapshot.Vouchers.ToArray();
            _fullSnapshotVoucherXml = snapshot.FullVouchers.ToArray();
            _fullSnapshotValidated = true;
            _lastFullSnapshotValidationError = string.Empty;
        }

        return snapshot;
    }

    public IReadOnlyDictionary<string, string> GetFullSnapshotDiagnosticArtifacts()
    {
        lock (_fullSnapshotDiagnosticGate)
        {
            var artifacts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_lastFullSnapshotRequestXml.Length > 0)
                artifacts["Full-Snapshot-Request.xml"] = _lastFullSnapshotRequestXml;
            if (_lastFullSnapshotResponseXml.Length > 0)
                artifacts["Full-Snapshot-Response.xml"] = _lastFullSnapshotResponseXml;
            if (_lastFullSnapshotError.Length > 0)
                artifacts["Full-Snapshot-Error.txt"] = _lastFullSnapshotError;
            return artifacts;
        }
    }

    private static bool IsRecoverableNativeSnapshotVoucherIdentityGap(InvalidOperationException exception)
    {
        var message = exception.Message ?? string.Empty;
        return message.Contains("did not export Voucher MASTERID", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Native snapshot did not return complete Voucher XML for cached MASTERID", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<TallyFullCompanySnapshot> RecoverNativeSnapshotVoucherAuthorityAsync(
        string nativeResponse,
        TallyCompanyInfo company,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        // Validate the Company + Master side independently.  A Voucher serialization
        // omission must never invalidate a complete Ledger/Group identity snapshot.
        var masterAuthority = ParseAndValidateNativeMasterAuthoritySnapshot(
            nativeResponse, company, fromDate, toDate);

        // Fix66 recovery remains fail-closed, but even recovery does not ask once
        // for Voucher MASTERIDs and again for the same full Vouchers. One fresh Voucher
        // authority response supplies both identity/index and full XML.
        var voucherAuthority = await ExportVoucherAuthorityFreshAsync(
            company, fromDate.Date, toDate.Date, cancellationToken).ConfigureAwait(false);
        var vouchers = voucherAuthority.Index;
        var fullVouchers = voucherAuthority.Full;

        if (company.VoucherCount > 0 && vouchers.Count == 0)
            throw new InvalidOperationException(
                $"Dedicated live Voucher authority is empty although discovery reported {company.VoucherCount:N0} voucher(s).");

        return new TallyFullCompanySnapshot
        {
            CompanyKey = masterAuthority.CompanyKey,
            FinancialYear = masterAuthority.FinancialYear,
            FromDate = fromDate.Date,
            ToDate = toDate.Date,
            Xml = masterAuthority.Xml,
            Vouchers = vouchers,
            FullVouchers = fullVouchers,
            LedgerParents = masterAuthority.LedgerParents,
            GroupNames = masterAuthority.GroupNames,
            DeclaredGroupCount = masterAuthority.DeclaredGroupCount,
            DeclaredLedgerCount = masterAuthority.DeclaredLedgerCount,
            DeclaredVoucherCount = vouchers.Count,
            AuthorityAlterId = Math.Max(masterAuthority.AuthorityAlterId,
                vouchers.Select(voucher => long.TryParse(new string(voucher.AlterId.Where(char.IsDigit).ToArray()), out var id) ? id : 0L).DefaultIfEmpty(0L).Max()),
            CapturedUtc = DateTime.UtcNow
        };
    }

    private static TallyFullCompanySnapshot ParseAndValidateNativeMasterAuthoritySnapshot(
        string response,
        TallyCompanyInfo company,
        DateTime fromDate,
        DateTime toDate)
    {
        var cleaned = CleanXml(response);
        var document = XDocument.Parse(cleaned, LoadOptions.None);

        var responseStatus = document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim() ?? string.Empty;
        if (responseStatus == "0" || responseStatus == "-1")
        {
            var statusText = string.Join(" | ", document.Descendants()
                .Where(element => element.Name.LocalName.Equals("DESC", StringComparison.OrdinalIgnoreCase) ||
                                  element.Name.LocalName.Equals("LINEERROR", StringComparison.OrdinalIgnoreCase))
                .Select(element => CleanText(element.Value))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5));
            throw new InvalidOperationException(
                "Tally returned failure status for the native full-company Collection" +
                (statusText.Length > 0 ? ": " + statusText : "."));
        }

        var dataCollection = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName.Equals("COLLECTION", StringComparison.OrdinalIgnoreCase) &&
            element.Parent?.Name.LocalName.Equals("DATA", StringComparison.OrdinalIgnoreCase) == true);
        if (dataCollection is null)
            throw new InvalidOperationException("Native Collection response did not contain BODY/DATA/COLLECTION.");

        var returnedCompanies = ParseDiscoveredCompanyNames(cleaned);
        var expectedCompany = CleanTallyMasterDisplayName(company.Name);
        if (returnedCompanies.Count == 0 || !returnedCompanies.Any(name =>
                CleanTallyMasterDisplayName(name).Equals(expectedCompany, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Native snapshot company identity mismatch. Expected '{expectedCompany}', but the Company object was not returned.");

        var groupNames = ParseGroupNamesFromSnapshot(cleaned);
        var ledgerParents = ParseLedgerParentsFromSnapshot(cleaned);
        if (groupNames.Count == 0)
            throw new InvalidOperationException("Native snapshot returned no Group objects.");
        if (ledgerParents.Count == 0)
            throw new InvalidOperationException("Native snapshot returned no Ledger objects.");

        var missingRequiredGroups = FullSnapshotRequiredGroups
            .Where(required => !groupNames.Contains(required))
            .ToArray();
        if (missingRequiredGroups.Length > 0)
            throw new InvalidOperationException(
                "Native snapshot is missing predefined Tally Group(s): " +
                string.Join(", ", missingRequiredGroups) + ".");

        if (company.GroupCount > 0 && groupNames.Count < company.GroupCount)
            throw new InvalidOperationException(
                $"Native Group authority is incomplete. Discovery reported {company.GroupCount:N0}; snapshot returned {groupNames.Count:N0}.");
        if (company.LedgerCount > 0 && ledgerParents.Count < company.LedgerCount)
            throw new InvalidOperationException(
                $"Native Ledger authority is incomplete. Discovery reported {company.LedgerCount:N0}; snapshot returned {ledgerParents.Count:N0}.");

        return new TallyFullCompanySnapshot
        {
            CompanyKey = company.CompanyKey,
            FinancialYear = company.FinancialYear,
            FromDate = fromDate.Date,
            ToDate = toDate.Date,
            Xml = cleaned,
            LedgerParents = ledgerParents,
            GroupNames = groupNames,
            DeclaredGroupCount = groupNames.Count,
            DeclaredLedgerCount = ledgerParents.Count,
            DeclaredVoucherCount = 0,
            AuthorityAlterId = ParseMaxAlterId(cleaned),
            CapturedUtc = DateTime.UtcNow
        };
    }

    private static TallyFullCompanySnapshot ParseAndValidateNativeFullCompanySnapshot(
        string response,
        TallyCompanyInfo company,
        DateTime fromDate,
        DateTime toDate)
    {
        var cleaned = CleanXml(response);
        var document = XDocument.Parse(cleaned, LoadOptions.None);

        var responseStatus = document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim() ?? string.Empty;
        if (responseStatus == "0" || responseStatus == "-1")
        {
            var statusText = string.Join(" | ", document.Descendants()
                .Where(element => element.Name.LocalName.Equals("DESC", StringComparison.OrdinalIgnoreCase) ||
                                  element.Name.LocalName.Equals("LINEERROR", StringComparison.OrdinalIgnoreCase))
                .Select(element => CleanText(element.Value))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5));
            throw new InvalidOperationException(
                "Tally returned failure status for the native full-company Collection" +
                (statusText.Length > 0 ? ": " + statusText : "."));
        }

        var dataCollection = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName.Equals("COLLECTION", StringComparison.OrdinalIgnoreCase) &&
            element.Parent?.Name.LocalName.Equals("DATA", StringComparison.OrdinalIgnoreCase) == true);
        if (dataCollection is null)
            throw new InvalidOperationException("Native Collection response did not contain BODY/DATA/COLLECTION.");

        var returnedCompanies = ParseDiscoveredCompanyNames(cleaned);
        var expectedCompany = CleanTallyMasterDisplayName(company.Name);
        if (returnedCompanies.Count == 0 || !returnedCompanies.Any(name =>
                CleanTallyMasterDisplayName(name).Equals(expectedCompany, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Native snapshot company identity mismatch. Expected '{expectedCompany}', but the Company object was not returned.");
        }

        var groupNames = ParseGroupNamesFromSnapshot(cleaned);
        var ledgerParents = ParseLedgerParentsFromSnapshot(cleaned);
        var vouchers = ValidateVoucherIndexRange(
            ParseVoucherIndex(cleaned, company.CompanyKey, company.FinancialYear),
            fromDate,
            toDate);
        var masterIdByGuid = vouchers
            .Where(voucher => !string.IsNullOrWhiteSpace(voucher.Guid) && !string.IsNullOrWhiteSpace(voucher.MasterId))
            .GroupBy(voucher => voucher.Guid.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().MasterId.Trim(), StringComparer.OrdinalIgnoreCase);
        var fullVouchers = ParseVoucherXmlSnapshots(cleaned, masterIdByGuid)
            .Where(snapshot => snapshot.VoucherDate is DateTime date && date.Date >= fromDate.Date && date.Date <= toDate.Date)
            .GroupBy(snapshot => snapshot.MasterId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(snapshot => snapshot.OriginalXml.Length).First())
            .ToArray();

        var indexedIds = vouchers.Where(voucher => !string.IsNullOrWhiteSpace(voucher.MasterId))
            .Select(voucher => voucher.MasterId.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fullIds = fullVouchers.Select(voucher => voucher.MasterId.Trim()).Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingFullIds = indexedIds.Where(id => !fullIds.Contains(id)).Take(10).ToArray();
        if (missingFullIds.Length > 0)
            throw new InvalidOperationException(
                $"Native snapshot did not return complete Voucher XML for cached MASTERID(s): {string.Join(", ", missingFullIds)}. Full FY authority was rejected.");

        if (groupNames.Count == 0)
            throw new InvalidOperationException("Native snapshot returned no Group objects.");
        if (ledgerParents.Count == 0)
            throw new InvalidOperationException("Native snapshot returned no Ledger objects.");

        var missingRequiredGroups = FullSnapshotRequiredGroups
            .Where(required => !groupNames.Contains(required))
            .ToArray();
        if (missingRequiredGroups.Length > 0)
            throw new InvalidOperationException(
                "Native snapshot is missing predefined Tally Group(s): " +
                string.Join(", ", missingRequiredGroups) + ".");

        // Discovery counts are an independent lightweight signal. Reject truncation, but
        // do not require artificial server-side $$NumItems formulas or exact voucher
        // counts because the Voucher collection is deliberately restricted to this FY.
        if (company.GroupCount > 0 && groupNames.Count < company.GroupCount)
            throw new InvalidOperationException(
                $"Native Group authority is incomplete. Discovery reported {company.GroupCount:N0}; snapshot returned {groupNames.Count:N0}.");
        if (company.LedgerCount > 0 && ledgerParents.Count < company.LedgerCount)
            throw new InvalidOperationException(
                $"Native Ledger authority is incomplete. Discovery reported {company.LedgerCount:N0}; snapshot returned {ledgerParents.Count:N0}.");
        if (company.VoucherCount > 0 && vouchers.Count == 0)
            throw new InvalidOperationException(
                $"Native Voucher authority is empty although discovery reported {company.VoucherCount:N0} voucher(s).");

        return new TallyFullCompanySnapshot
        {
            CompanyKey = company.CompanyKey,
            FinancialYear = company.FinancialYear,
            FromDate = fromDate,
            ToDate = toDate,
            Xml = cleaned,
            Vouchers = vouchers,
            FullVouchers = fullVouchers,
            LedgerParents = ledgerParents,
            GroupNames = groupNames,
            // Kept for UI/report compatibility; these are now AAH-parsed native-object
            // counts, not fragile server-side declared-count formulas.
            DeclaredGroupCount = groupNames.Count,
            DeclaredLedgerCount = ledgerParents.Count,
            DeclaredVoucherCount = vouchers.Count,
            AuthorityAlterId = ParseMaxAlterId(cleaned),
            CapturedUtc = DateTime.UtcNow
        };
    }

    private bool TryGetCachedFullSnapshot(
        TallyCompanyInfo company,
        DateTime fromDate,
        DateTime toDate,
        out string xml,
        out IReadOnlyList<TallyVoucherIndexRecord> vouchers)
    {
        lock (_fullSnapshotCacheGate)
        {
            var fresh = _fullSnapshotUtc != DateTime.MinValue &&
                        DateTime.UtcNow - _fullSnapshotUtc <= TimeSpan.FromMinutes(5);
            var companyMatch = _fullSnapshotCompanyKey.Equals(
                company.CompanyKey, StringComparison.OrdinalIgnoreCase);
            var fyMatch = _fullSnapshotFinancialYear.Equals(
                company.FinancialYear, StringComparison.OrdinalIgnoreCase);
            var covers = _fullSnapshotFrom.Date <= fromDate.Date &&
                         _fullSnapshotTo.Date >= toDate.Date;
            if (_fullSnapshotValidated && fresh && companyMatch && fyMatch && covers && _fullSnapshotXml.Length > 0)
            {
                xml = _fullSnapshotXml;
                vouchers = _fullSnapshotVouchers
                    .Where(voucher => voucher.VoucherDate is DateTime date &&
                                      date.Date >= fromDate.Date && date.Date <= toDate.Date)
                    .ToArray();
                return true;
            }
        }

        xml = string.Empty;
        vouchers = Array.Empty<TallyVoucherIndexRecord>();
        return false;
    }

    private bool TryGetCachedFullSnapshotXml(TallyCompanyInfo company, out string xml)
    {
        lock (_fullSnapshotCacheGate)
        {
            var fresh = _fullSnapshotUtc != DateTime.MinValue &&
                        DateTime.UtcNow - _fullSnapshotUtc <= TimeSpan.FromMinutes(5);
            var companyMatch = _fullSnapshotCompanyKey.Equals(
                company.CompanyKey, StringComparison.OrdinalIgnoreCase);
            if (_fullSnapshotValidated && fresh && companyMatch && _fullSnapshotXml.Length > 0)
            {
                xml = _fullSnapshotXml;
                return true;
            }
        }
        xml = string.Empty;
        return false;
    }

    private void InvalidateFullSnapshotCache()
    {
        lock (_fullSnapshotCacheGate)
        {
            _fullSnapshotValidated = false;
            _fullSnapshotCompanyKey = string.Empty;
            _fullSnapshotFinancialYear = string.Empty;
            _fullSnapshotFrom = DateTime.MinValue;
            _fullSnapshotTo = DateTime.MinValue;
            _fullSnapshotUtc = DateTime.MinValue;
            _fullSnapshotXml = string.Empty;
            _fullSnapshotVouchers = Array.Empty<TallyVoucherIndexRecord>();
            _fullSnapshotVoucherXml = Array.Empty<TallyVoucherXmlSnapshot>();
        }
    }

    /// <summary>
    /// Returns authority from the most recently completed full-company snapshot without
    /// ever falling back to a Tally HTTP request. This is used only by an operation that
    /// explicitly declares usePreSynchronizedAuthority after SynchronizeLinkedCompanyAsync
    /// has completed. It keeps the one-read contract deterministic even if local parsing
    /// or reconciliation takes longer than the normal five-minute opportunistic cache TTL.
    /// </summary>
    /// <summary>
    /// Fix111 R2 MASTERID execution authority. Returns the exact raw Ledger names from
    /// the already-pinned authority without issuing another Tally request. ERP 9 keeps
    /// its fresh Ledger/Group collection in the dedicated core-Master cache, so that
    /// cache must outrank the normalized full-snapshot/display dictionaries. This keeps
    /// legacy CR/LF/space characters attached to the same live MASTERID through Voucher XML.
    /// </summary>
    public bool TryGetPinnedLedgerNameAuthorities(
        TallyCompanyInfo company,
        out IReadOnlyList<TallyLedgerNameAuthority> ledgers)
    {
        ArgumentNullException.ThrowIfNull(company);

        if (ShouldUseErp9CompatibilityProbe())
        {
            string erp9Xml = string.Empty;
            lock (_erp9CoreCacheGate)
            {
                var sameCompany = _erp9CoreCacheCompany.Equals(
                    (company.Name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
                if (sameCompany && _erp9CoreCacheXml.Length > 0)
                    erp9Xml = _erp9CoreCacheXml;
            }

            if (erp9Xml.Length > 0)
            {
                var parsed = ParseLedgerNameAuthoritiesFromXml(erp9Xml);
                if (parsed.Count > 0)
                {
                    ledgers = parsed;
                    return true;
                }
            }
        }

        if (TryGetPinnedFullSnapshotAuthorities(company, out var fullLedgers, out _))
        {
            ledgers = fullLedgers;
            return ledgers.Count > 0;
        }

        ledgers = Array.Empty<TallyLedgerNameAuthority>();
        return false;
    }

    public bool TryGetPinnedFullSnapshotAuthorities(
        TallyCompanyInfo company,
        out IReadOnlyList<TallyLedgerNameAuthority> ledgers,
        out IReadOnlyDictionary<string, string> groupParents)
    {
        ArgumentNullException.ThrowIfNull(company);
        string xml;
        lock (_fullSnapshotCacheGate)
        {
            var companyMatch = _fullSnapshotCompanyKey.Equals(
                company.CompanyKey, StringComparison.OrdinalIgnoreCase);
            var fyMatch = _fullSnapshotFinancialYear.Equals(
                company.FinancialYear, StringComparison.OrdinalIgnoreCase);
            if (!_fullSnapshotValidated || !companyMatch || !fyMatch || _fullSnapshotXml.Length == 0)
            {
                ledgers = Array.Empty<TallyLedgerNameAuthority>();
                groupParents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return false;
            }
            xml = _fullSnapshotXml;
        }

        var cleaned = CleanXml(xml);
        ledgers = ParseLedgerNameAuthoritiesFromXml(cleaned);
        groupParents = ParseGroupParentsFromXml(cleaned);
        return true;
    }

    /// <summary>
    /// FIX5 destructive-write preflight authority. Reads the requested voucher range
    /// directly from the currently connected Tally process and deliberately bypasses
    /// the opportunistic full-snapshot cache. Inter-Bank Contra ALTER uses this method
    /// immediately before mutation so a stale connection snapshot can never authorize
    /// changing ledger lines on a voucher that the user has since edited in Tally.
    /// </summary>
    public async Task<IReadOnlyList<TallyVoucherIndexRecord>> ExportVoucherIndexFreshAsync(
        TallyCompanyInfo company,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);

        if (ShouldUseErp9CompatibilityProbe())
        {
            return await ExportErp9VoucherCollectionIndexAsync(
                company, fromDate, toDate, cancellationToken).ConfigureAwait(false);
        }

        var attempts = new[]
        {
            BuildVoucherIndexRequest(company.Name, fromDate, toDate),
            BuildLegacyVoucherIndexRequest(company.Name, fromDate, toDate, includeNarration: true),
            BuildLegacyVoucherIndexRequest(company.Name, fromDate, toDate, includeNarration: false)
        };

        var errors = new List<string>();
        for (var index = 0; index < attempts.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await PostAsync(attempts[index], cancellationToken).ConfigureAwait(false);
                return ValidateVoucherIndexRange(
                    ParseVoucherIndex(response, company.CompanyKey, company.FinancialYear),
                    fromDate,
                    toDate);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add($"attempt {index + 1}: {exception.Message}");
                // Fix37: one broad Tally timeout is authoritative for this cycle. Do not
                // immediately hold Tally again with legacy/full retry requests.
                if (exception is TimeoutException) break;
            }
        }

        throw new InvalidOperationException(
            $"Tally did not return a fresh voucher index for {fromDate:dd-MMM-yyyy}: {string.Join(" | ", errors)}");
    }

    /// <summary>
    /// Fix66 explicit-export authority. TallyPrime is asked once for the requested
    /// Voucher slice and that same response supplies both the duplicate index and the
    /// complete Voucher XML. MASTERID/GUID/ALTERID are native methods on the exported
    /// Voucher objects, so the normal path never asks Tally a second time merely to
    /// rediscover identities that were already returned with the Voucher.
    /// ERP 9 keeps its proven compatibility path unchanged.
    /// </summary>
    public async Task<(IReadOnlyList<TallyVoucherIndexRecord> Index, IReadOnlyList<TallyVoucherXmlSnapshot> Full)>
        ExportVoucherAuthorityFreshAsync(
            TallyCompanyInfo company,
            DateTime fromDate,
            DateTime toDate,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);
        fromDate = fromDate.Date;
        toDate = toDate.Date;
        if (fromDate > toDate) (fromDate, toDate) = (toDate, fromDate);

        if (ShouldUseErp9CompatibilityProbe())
        {
            var erp9Index = await ExportErp9VoucherCollectionIndexAsync(
                company, fromDate, toDate, cancellationToken).ConfigureAwait(false);
            var erp9Full = await ExportCompleteVoucherXmlAuthorityAsync(
                company, erp9Index, fromDate, toDate, cancellationToken).ConfigureAwait(false);
            return (erp9Index, erp9Full);
        }

        var request = BuildVoucherAuthorityRequest(company.Name, fromDate, toDate);
        var response = await PostAsync(request, cancellationToken, TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        var index = ValidateVoucherIndexRange(
            ParseVoucherIndex(response, company.CompanyKey, company.FinancialYear),
            fromDate,
            toDate);

        var missingMasterIds = index
            .Where(voucher => string.IsNullOrWhiteSpace(voucher.MasterId))
            .Take(10)
            .ToArray();
        if (missingMasterIds.Length > 0)
            throw new InvalidOperationException(
                $"Fresh Tally Voucher authority returned {missingMasterIds.Length:N0} Voucher row(s) without MASTERID. " +
                "No second MASTERID lookup was attempted and no export decision was authorized.");

        var masterIdByGuid = index
            .Where(voucher => !string.IsNullOrWhiteSpace(voucher.Guid) && !string.IsNullOrWhiteSpace(voucher.MasterId))
            .GroupBy(voucher => voucher.Guid.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().MasterId.Trim(), StringComparer.OrdinalIgnoreCase);
        var full = ParseVoucherXmlSnapshots(response, masterIdByGuid)
            .Where(snapshot => snapshot.VoucherDate is DateTime date &&
                               date.Date >= fromDate && date.Date <= toDate)
            .GroupBy(snapshot => snapshot.MasterId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(snapshot => snapshot.OriginalXml.Length).First())
            .ToArray();

        var indexedIds = index
            .Select(voucher => voucher.MasterId.Trim())
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fullIds = full
            .Select(voucher => voucher.MasterId.Trim())
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingFullIds = indexedIds.Where(id => !fullIds.Contains(id)).Take(10).ToArray();
        var unexpectedFullIds = fullIds.Where(id => !indexedIds.Contains(id)).Take(10).ToArray();
        if (missingFullIds.Length > 0 || unexpectedFullIds.Length > 0 || fullIds.Count != indexedIds.Count)
        {
            var details = new List<string>();
            if (missingFullIds.Length > 0) details.Add("missing full XML: " + string.Join(", ", missingFullIds));
            if (unexpectedFullIds.Length > 0) details.Add("unexpected full XML: " + string.Join(", ", unexpectedFullIds));
            throw new InvalidOperationException(
                "Fresh Tally Voucher authority did not return one complete full Voucher object for every MASTERID in the same response" +
                (details.Count > 0 ? ": " + string.Join(" | ", details) : ".") +
                " No second identity/full-object request was attempted.");
        }

        return (index, full);
    }


    /// <summary>
    /// Fix49/Fix50 compatibility helper retained for inherited callers and source gates.
    /// Fix51 normal connected operation no longer uses this as a pre-write Merge path:
    /// the initial active-FY baseline persists the complete Voucher XML authority and
    /// Merge/Inter-Bank consume that shared cache directly. This method is limited to
    /// compatibility/recovery contexts where no complete synchronized authority was supplied.
    /// </summary>
    public async Task<IReadOnlyList<TallyVoucherXmlSnapshot>> ExportVoucherXmlSnapshotsForLedgerAsync(
        TallyCompanyInfo company,
        string sourceLedgerName,
        DateTime fromDate,
        DateTime toDate,
        IReadOnlyList<TallyVoucherIndexRecord>? synchronizedVoucherIndex = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);
        var source = (sourceLedgerName ?? string.Empty).Trim();
        if (source.Length == 0) throw new InvalidOperationException("FROM Ledger is blank.");
        fromDate = fromDate.Date;
        toDate = toDate.Date;
        if (toDate < fromDate) throw new InvalidOperationException("Merge Voucher FY range is invalid.");

        IReadOnlyList<TallyVoucherXmlSnapshot> snapshots;
        if (synchronizedVoucherIndex is not null)
        {
            snapshots = await ExportVoucherXmlSnapshotsForLedgerFromIndexAsync(
                company, source, fromDate, toDate, synchronizedVoucherIndex, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            try
            {
                var request = BuildLedgerVoucherFullObjectRequest(company.Name, source, fromDate, toDate);
                var response = await PostAsync(request, cancellationToken, TimeSpan.FromSeconds(45)).ConfigureAwait(false);
                snapshots = ParseVoucherXmlSnapshots(response);
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception)
            {
                snapshots = Array.Empty<TallyVoucherXmlSnapshot>();
            }

            // Compatibility only when no synchronized FY index was supplied. Keep the
            // fallback strictly inside the active FY; never fall back to BooksFrom or a
            // whole-company historical scan.
            if (snapshots.Count == 0)
                snapshots = await ExportVoucherXmlSnapshotsForLedgerFallbackAsync(
                    company, source, fromDate, toDate, cancellationToken).ConfigureAwait(false);
        }

        foreach (var snapshot in snapshots)
        {
            var voucher = ParseSingleVoucherElement(snapshot.OriginalXml);
            if (!VoucherXmlUsesLedger(voucher, source))
                throw new InvalidOperationException(
                    $"Tally returned Voucher MASTERID {snapshot.MasterId} for Ledger '{source}', but its full Voucher XML does not contain that Ledger. Merge was not started.");
        }

        return snapshots
            .GroupBy(item => item.MasterId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private async Task<IReadOnlyList<TallyVoucherXmlSnapshot>> ExportVoucherXmlSnapshotsForLedgerFromIndexAsync(
        TallyCompanyInfo company,
        string sourceLedgerName,
        DateTime fromDate,
        DateTime toDate,
        IReadOnlyList<TallyVoucherIndexRecord> synchronizedVoucherIndex,
        CancellationToken cancellationToken)
    {
        var matchingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var masterIdByGuid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in synchronizedVoucherIndex)
        {
            if (row.VoucherDate is not DateTime date || date.Date < fromDate || date.Date > toDate) continue;
            if (!VoucherIndexUsesLedger(row, sourceLedgerName)) continue;
            var id = row.MasterId.Trim();
            if (id.Length == 0)
                throw new InvalidOperationException(
                    $"Synchronized Tally Voucher '{row.VoucherNumber}' uses FROM Ledger '{sourceLedgerName}' but has no MASTERID. Merge cannot alter that Voucher safely.");
            matchingIds.Add(id);
            var guid = row.Guid.Trim();
            if (guid.Length > 0) masterIdByGuid[guid] = id;
        }

        if (matchingIds.Count == 0) return Array.Empty<TallyVoucherXmlSnapshot>();
        return await ExportVoucherXmlSnapshotsByMasterIdsAsync(
            company, matchingIds, masterIdByGuid, fromDate, toDate, cancellationToken).ConfigureAwait(false);
    }

    public void ValidateCachedVoucherSnapshotsForLedger(
        IReadOnlyList<TallyVoucherXmlSnapshot> snapshots,
        string ledgerName)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var ledger = (ledgerName ?? string.Empty).Trim();
        foreach (var snapshot in snapshots)
        {
            var voucher = ParseSingleVoucherElement(snapshot.OriginalXml);
            if (!VoucherXmlUsesLedger(voucher, ledger))
                throw new InvalidOperationException(
                    $"Cached complete Tally Voucher MASTERID {snapshot.MasterId} does not contain Ledger '{ledger}'. The shared authority is inconsistent and Merge was stopped.");
        }
    }

    public void VerifyVoucherLedgerMigrationFromCachedAuthority(
        IReadOnlyList<TallyVoucherXmlSnapshot> originalSnapshots,
        IReadOnlyList<TallyVoucherXmlSnapshot> currentSnapshots,
        string sourceLedgerName,
        string targetLedgerName)
    {
        ArgumentNullException.ThrowIfNull(originalSnapshots);
        ArgumentNullException.ThrowIfNull(currentSnapshots);
        var currentById = currentSnapshots
            .GroupBy(snapshot => snapshot.MasterId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var original in originalSnapshots)
        {
            if (!currentById.TryGetValue(original.MasterId.Trim(), out var current))
                throw new InvalidOperationException(
                    $"Post-write Tally authority did not return Voucher MASTERID {original.MasterId}; Merge verification failed.");
            var voucher = ParseSingleVoucherElement(current.OriginalXml);
            if (VoucherXmlUsesLedger(voucher, sourceLedgerName))
                throw new InvalidOperationException(
                    $"Tally ALTER verification failed: Voucher MASTERID {current.MasterId} still uses FROM Ledger '{sourceLedgerName}'.");
            if (!VoucherXmlUsesLedger(voucher, targetLedgerName))
                throw new InvalidOperationException(
                    $"Tally ALTER verification failed: Voucher MASTERID {current.MasterId} does not use INTO Ledger '{targetLedgerName}'.");
        }
    }

    public async Task VerifyVoucherLedgerMigrationAsync(
        TallyCompanyInfo company,
        IReadOnlyList<TallyVoucherXmlSnapshot> originalSnapshots,
        string sourceLedgerName,
        string targetLedgerName,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(originalSnapshots);
        if (originalSnapshots.Count == 0) return;

        var source = (sourceLedgerName ?? string.Empty).Trim();
        var target = (targetLedgerName ?? string.Empty).Trim();
        var ids = originalSnapshots.Select(item => item.MasterId).Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var idByGuid = originalSnapshots.Where(item => !string.IsNullOrWhiteSpace(item.Guid))
            .GroupBy(item => item.Guid.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().MasterId.Trim(), StringComparer.OrdinalIgnoreCase);
        var current = await ExportVoucherXmlSnapshotsByMasterIdsAsync(
            company, ids, idByGuid, fromDate.Date, toDate.Date, cancellationToken).ConfigureAwait(false);

        foreach (var snapshot in current)
        {
            var voucher = ParseSingleVoucherElement(snapshot.OriginalXml);
            if (VoucherXmlUsesLedger(voucher, source))
                throw new InvalidOperationException(
                    $"Tally ALTER verification failed: Voucher MASTERID {snapshot.MasterId} still uses FROM Ledger '{source}'.");
            if (!VoucherXmlUsesLedger(voucher, target))
                throw new InvalidOperationException(
                    $"Tally ALTER verification failed: Voucher MASTERID {snapshot.MasterId} does not use INTO Ledger '{target}'.");
        }
    }

    public async Task<TallyImportResponse> AlterVoucherLedgerReferencesAsync(
        TallyCompanyInfo company,
        IReadOnlyList<TallyVoucherXmlSnapshot> snapshots,
        string sourceLedgerName,
        string targetLedgerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0) return new TallyImportResponse();
        var source = (sourceLedgerName ?? string.Empty).Trim();
        var target = (targetLedgerName ?? string.Empty).Trim();
        if (source.Length == 0 || target.Length == 0)
            throw new InvalidOperationException("FROM and INTO Ledger names are required for Voucher migration.");
        if (TallyMasterIdentity.Equivalent(source, target))
            throw new InvalidOperationException("FROM and INTO resolve to the same Tally Ledger identity.");

        var total = new TallyImportResponse();
        foreach (var batch in snapshots.Chunk(25))
        {
            var builder = BeginVoucherSnapshotImport(company.Name);
            foreach (var snapshot in batch)
            {
                var voucher = ParseSingleVoucherElement(snapshot.OriginalXml);
                PrepareVoucherForAlter(voucher, snapshot);
                var replacements = ReplaceVoucherLedgerIdentity(voucher, source, target);
                if (replacements == 0)
                    throw new InvalidOperationException(
                        $"Voucher MASTERID {snapshot.MasterId} no longer contains FROM Ledger '{source}'. No Voucher was altered.");
                AppendVoucherSnapshot(builder, voucher);
            }
            EndVoucherSnapshotImport(builder);

            var response = ParseImportResponse(await ImportXmlAsync(builder.ToString(), cancellationToken).ConfigureAwait(false));
            if (response.Errors > 0 || !string.IsNullOrWhiteSpace(response.LineError))
                throw new InvalidOperationException(
                    $"Tally rejected Ledger-reference migration batch: Altered={response.Altered}; Ignored={response.Ignored}; Errors={response.Errors}; {response.LineError}");
            AccumulateImportResponse(total, response);
        }
        return total;
    }

    /// <summary>
    /// Restores the exact full Voucher XML captured before a Fix47 Ledger merge.
    /// This is intentionally snapshot based instead of target-&gt;source text reversal,
    /// because a Voucher may legitimately have contained both Ledgers before merge.
    /// </summary>
    public async Task<TallyImportResponse> RestoreVoucherXmlSnapshotsAsync(
        TallyCompanyInfo company,
        IReadOnlyList<TallyVoucherXmlSnapshot> snapshots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0) return new TallyImportResponse();

        var total = new TallyImportResponse();
        foreach (var batch in snapshots.Chunk(25))
        {
            var builder = BeginVoucherSnapshotImport(company.Name);
            foreach (var snapshot in batch)
            {
                var voucher = ParseSingleVoucherElement(snapshot.OriginalXml);
                PrepareVoucherForAlter(voucher, snapshot);
                AppendVoucherSnapshot(builder, voucher);
            }
            EndVoucherSnapshotImport(builder);

            var response = ParseImportResponse(await ImportXmlAsync(builder.ToString(), cancellationToken).ConfigureAwait(false));
            if (response.Errors > 0 || !string.IsNullOrWhiteSpace(response.LineError))
                throw new InvalidOperationException(
                    $"Tally rejected Voucher rollback batch: Altered={response.Altered}; Ignored={response.Ignored}; Errors={response.Errors}; {response.LineError}");
            AccumulateImportResponse(total, response);
        }
        return total;
    }


    private async Task<IReadOnlyList<TallyVoucherXmlSnapshot>> ExportVoucherXmlSnapshotsForLedgerFallbackAsync(
        TallyCompanyInfo company,
        string sourceLedgerName,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        var matchingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var masterIdByGuid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cursor = fromDate.Date;
        while (cursor <= toDate.Date)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sliceEnd = cursor.AddYears(1).AddDays(-1);
            if (sliceEnd > toDate.Date) sliceEnd = toDate.Date;
            var rows = await ExportVoucherIndexFreshAsync(company, cursor, sliceEnd, cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                if (!VoucherIndexUsesLedger(row, sourceLedgerName)) continue;
                var id = row.MasterId.Trim();
                if (id.Length == 0)
                    throw new InvalidOperationException(
                        $"Tally Voucher '{row.VoucherNumber}' uses FROM Ledger '{sourceLedgerName}' but has no MASTERID. Merge cannot alter that Voucher safely.");
                matchingIds.Add(id);
                var guid = row.Guid.Trim();
                if (guid.Length > 0)
                    masterIdByGuid[guid] = id;
            }
            cursor = sliceEnd.AddDays(1);
        }

        if (matchingIds.Count == 0) return Array.Empty<TallyVoucherXmlSnapshot>();
        return await ExportVoucherXmlSnapshotsByMasterIdsAsync(
            company, matchingIds, masterIdByGuid, fromDate, toDate, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TallyVoucherXmlSnapshot>> ExportVoucherXmlSnapshotsByKnownMasterIdsForVerificationAsync(
        TallyCompanyInfo company,
        IReadOnlyCollection<string> masterIds,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(masterIds);
        var ids = masterIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (ids.Length == 0) return Array.Empty<TallyVoucherXmlSnapshot>();
        return await ExportVoucherXmlSnapshotsByMasterIdsAsync(
            company, ids, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            fromDate.Date, toDate.Date, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<TallyVoucherXmlSnapshot>> ExportVoucherXmlSnapshotsByMasterIdsAsync(
        TallyCompanyInfo company,
        IReadOnlyCollection<string> masterIds,
        IReadOnlyDictionary<string, string> masterIdByGuid,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        var result = new List<TallyVoucherXmlSnapshot>();
        foreach (var batch in masterIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(25))
        {
            IReadOnlyList<TallyVoucherXmlSnapshot> snapshots;
            try
            {
                var request = BuildVoucherFullObjectByMasterIdsRequest(company.Name, batch, fromDate, toDate);
                var response = await PostAsync(request, cancellationToken, TimeSpan.FromSeconds(45)).ConfigureAwait(false);
                snapshots = ParseVoucherXmlSnapshots(response, masterIdByGuid);
            }
            catch (InvalidOperationException exception) when
                (exception.Message.Contains("did not export Voucher MASTERID", StringComparison.OrdinalIgnoreCase))
            {
                // Fix50: the synchronized FY Voucher Index already supplied these exact
                // numeric MASTERIDs. Some Tally builds honor $MasterID in the collection
                // filter but omit both MASTERID and GUID from the exported full Voucher.
                // Do not discard the known identity and do not guess from number/date.
                // Re-fetch each affected MASTERID individually. If that exact filter
                // returns exactly one Voucher object, the requested MASTERID remains the
                // authority even when Tally omits it from the full XML payload.
                snapshots = Array.Empty<TallyVoucherXmlSnapshot>();
            }

            var returnedIds = snapshots.Select(item => item.MasterId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            result.AddRange(snapshots);

            var missing = batch.Where(id => !returnedIds.Contains(id)).ToArray();
            foreach (var masterId in missing)
            {
                var exact = await ExportVoucherXmlSnapshotByExactMasterIdAsync(
                    company, masterId, masterIdByGuid, fromDate, toDate, cancellationToken).ConfigureAwait(false);
                result.Add(exact);
            }
        }

        var expectedIds = masterIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var finalIds = result.Select(item => item.MasterId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stillMissing = expectedIds.Where(id => !finalIds.Contains(id)).ToArray();
        if (stillMissing.Length > 0)
            throw new InvalidOperationException(
                $"Tally did not return complete Voucher XML for merge MASTERID(s): {string.Join(", ", stillMissing)}. No Ledger deletion was attempted.");

        return result
            .GroupBy(item => item.MasterId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private async Task<TallyVoucherXmlSnapshot> ExportVoucherXmlSnapshotByExactMasterIdAsync(
        TallyCompanyInfo company,
        string masterId,
        IReadOnlyDictionary<string, string> masterIdByGuid,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        masterId = (masterId ?? string.Empty).Trim();
        if (!long.TryParse(masterId, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            throw new InvalidOperationException($"Cached Tally Voucher MASTERID '{masterId}' is not numeric.");

        var request = BuildVoucherFullObjectByMasterIdsRequest(company.Name, new[] { masterId }, fromDate, toDate);
        var response = await PostAsync(request, cancellationToken, TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        var snapshots = ParseVoucherXmlSnapshots(response, masterIdByGuid, masterId);
        if (snapshots.Count != 1)
            throw new InvalidOperationException(
                $"Exact Tally Voucher MASTERID {masterId} returned {snapshots.Count} full Voucher object(s); merge cannot continue safely.");
        if (!string.Equals(snapshots[0].MasterId, masterId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Exact Tally Voucher request for MASTERID {masterId} returned MASTERID {snapshots[0].MasterId}; merge cannot continue safely.");
        return snapshots[0];
    }

    private static bool VoucherIndexUsesLedger(TallyVoucherIndexRecord voucher, string ledgerName)
    {
        if (voucher.LedgerPostings.Any(posting => TallyMasterIdentity.Equivalent(posting.LedgerName, ledgerName))) return true;
        if (TallyMasterIdentity.Equivalent(voucher.PartyLedger, ledgerName)) return true;
        return SplitVoucherLedgerList(voucher.DebitLedgers).Any(name => TallyMasterIdentity.Equivalent(name, ledgerName)) ||
               SplitVoucherLedgerList(voucher.CreditLedgers).Any(name => TallyMasterIdentity.Equivalent(name, ledgerName));
    }

    private static IEnumerable<string> SplitVoucherLedgerList(string? value) =>
        (value ?? string.Empty).Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public async Task<IReadOnlyList<TallyVoucherXmlSnapshot>> ExportCompleteVoucherXmlAuthorityAsync(
        TallyCompanyInfo company,
        IReadOnlyList<TallyVoucherIndexRecord> synchronizedVoucherIndex,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(synchronizedVoucherIndex);
        var ids = synchronizedVoucherIndex
            .Where(row => row.VoucherDate is DateTime date && date.Date >= fromDate.Date && date.Date <= toDate.Date)
            .Select(row => row.MasterId.Trim())
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0) return Array.Empty<TallyVoucherXmlSnapshot>();
        var byGuid = synchronizedVoucherIndex
            .Where(row => !string.IsNullOrWhiteSpace(row.Guid) && !string.IsNullOrWhiteSpace(row.MasterId))
            .GroupBy(row => row.Guid.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().MasterId.Trim(), StringComparer.OrdinalIgnoreCase);
        return await ExportVoucherXmlSnapshotsByMasterIdsAsync(
            company, ids, byGuid, fromDate.Date, toDate.Date, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TallyVoucherIndexRecord>> ExportVoucherIndexAsync(
        TallyCompanyInfo company,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);

        if (TryGetCachedFullSnapshot(company, fromDate, toDate, out _, out var cachedVouchers))
            return cachedVouchers;

        if (ShouldUseErp9CompatibilityProbe())
        {
            // ERP 9 uses an explicit date-filtered Voucher collection as the live
            // duplicate authority. Day Book is intentionally not trusted because some
            // ERP 9 builds return only the report/current-date slice for a period.
            return await ExportErp9VoucherCollectionIndexAsync(
                company,
                fromDate,
                toDate,
                cancellationToken).ConfigureAwait(false);
        }

        var attempts = new[]
        {
            BuildVoucherIndexRequest(company.Name, fromDate, toDate),
            BuildLegacyVoucherIndexRequest(company.Name, fromDate, toDate, includeNarration: true),
            BuildLegacyVoucherIndexRequest(company.Name, fromDate, toDate, includeNarration: false)
        };

        var errors = new List<string>();
        for (var index = 0; index < attempts.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await PostAsync(attempts[index], cancellationToken);
                return ValidateVoucherIndexRange(
                    ParseVoucherIndex(response, company.CompanyKey, company.FinancialYear),
                    fromDate,
                    toDate);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add($"attempt {index + 1}: {exception.Message}");
                // Fix37: one broad Tally timeout is authoritative for this cycle. Do not
                // immediately hold Tally again with legacy/full retry requests.
                if (exception is TimeoutException) break;
            }
        }

        throw new InvalidOperationException(
            "Tally did not return a safely readable live voucher index after the normal requests. " +
            "Duplicate checking was not bypassed. " + string.Join(" | ", errors));
    }

    private async Task<IReadOnlyList<TallyVoucherIndexRecord>> ExportErp9VoucherCollectionIndexAsync(
        TallyCompanyInfo company,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();

        // ERP 9 duplicate authority must come from a real Voucher collection, not the
        // built-in Day Book. The user's ERP 9 build returned only the report/current-
        // date slice (often month-end) even when SVFromDate/SVToDate covered a month,
        // so a previously imported full Bank Statement could appear as only a handful
        // of live duplicates. Use an explicit Voucher collection with a $Date filter
        // and fetch the Ledger Entries needed for full Bank transaction identity.
        try
        {
            var request = BuildErp9VoucherSliceRequest(company.Name, fromDate, toDate);
            var response = await PostErp9TraceAsync(
                $"Voucher-Collection-{fromDate:yyyyMMdd}-{toDate:yyyyMMdd}",
                request, cancellationToken).ConfigureAwait(false);
            return ValidateVoucherIndexRange(
                ParseVoucherIndex(response, company.CompanyKey, company.FinancialYear),
                fromDate, toDate);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            errors.Add($"date-filtered Voucher collection {fromDate:dd-MMM-yyyy} to {toDate:dd-MMM-yyyy}: {exception.Message}");
            // Fix110 R4: ERP 9 has no application deadline. This catch is reached only
            // after Tally/HTTP returns an actual failure. Only then may the compatibility
            // retry below run; a still-pending ERP 9 request is never abandoned or overlapped.
        }

        // Older ERP 9 builds can use seven-day slices only after the first authoritative
        // request has actually returned a compatibility/transport failure. This is not a
        // Day Book fallback: every slice has its own TDL $Date filter and is range-
        // validated before any row can be used for duplicate decisions.
        var combined = new List<TallyVoucherIndexRecord>();
        var cursor = fromDate.Date;
        try
        {
            while (cursor <= toDate.Date)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunkEnd = cursor.AddDays(6);
                if (chunkEnd > toDate.Date) chunkEnd = toDate.Date;
                var request = BuildErp9VoucherSliceRequest(company.Name, cursor, chunkEnd);
                var response = await PostErp9TraceAsync(
                    $"Voucher-Slice-{cursor:yyyyMMdd}-{chunkEnd:yyyyMMdd}",
                    request, cancellationToken).ConfigureAwait(false);
                combined.AddRange(ValidateVoucherIndexRange(
                    ParseVoucherIndex(response, company.CompanyKey, company.FinancialYear),
                    cursor, chunkEnd));
                cursor = chunkEnd.AddDays(1);
            }

            return combined
                .GroupBy(Erp9VoucherIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(record => record.VoucherDate)
                .ThenBy(record => record.VoucherType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.VoucherNumber, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            errors.Add($"seven-day date-filtered Voucher collection: {exception.Message}");
        }

        throw new InvalidOperationException(
            "Tally.ERP 9 did not return a safely date-bounded Voucher collection. " +
            "Duplicate checking remains fail-closed and no voucher was sent for this period. " +
            string.Join(" | ", errors));
    }

    private static IReadOnlyList<TallyVoucherIndexRecord> ValidateVoucherIndexRange(
        IReadOnlyList<TallyVoucherIndexRecord> parsed,
        DateTime fromDate,
        DateTime toDate)
    {
        var missingDateCount = parsed.Count(voucher => voucher.VoucherDate is null);
        if (missingDateCount > 0)
            throw new InvalidOperationException(
                $"Tally returned {missingDateCount:N0} voucher(s) without a readable date; this index was rejected for duplicate safety.");

        var outside = parsed.Where(voucher =>
            voucher.VoucherDate!.Value.Date < fromDate.Date ||
            voucher.VoucherDate.Value.Date > toDate.Date).ToArray();
        if (outside.Length > 0)
            throw new InvalidOperationException(
                $"Tally returned {outside.Length:N0} voucher(s) outside the requested period {fromDate:dd-MMM-yyyy} to {toDate:dd-MMM-yyyy}; this index was rejected for duplicate safety.");
        return parsed;
    }

    private static string Erp9VoucherIdentity(TallyVoucherIndexRecord record) =>
        string.Join("|",
            record.MasterId,
            record.AlterId,
            record.Guid,
            record.VoucherDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty,
            record.VoucherType,
            record.VoucherNumber,
            record.Reference,
            record.PartyLedger,
            record.DebitLedgers,
            record.CreditLedgers,
            record.Amount.ToString("0.00", CultureInfo.InvariantCulture));

    private static IReadOnlyList<TallyLedgerNameAuthority> ParseLedgerNameAuthoritiesFromXml(string cleaned)
    {
        var result = new List<TallyLedgerNameAuthority>();
        var document = XDocument.Parse(cleaned, LoadOptions.PreserveWhitespace);
        foreach (var ledger in document.Descendants().Where(element =>
                     element.Name.LocalName.Equals("LEDGER", StringComparison.OrdinalIgnoreCase)))
        {
            var attributeName = ledger.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
            var childName = ledger.Elements().FirstOrDefault(element =>
                element.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
            var exactName = attributeName.Length > 0 ? attributeName : childName;
            if (exactName.Length == 0 || string.IsNullOrWhiteSpace(exactName)) continue;
            var parent = ledger.Elements().FirstOrDefault(element =>
                element.Name.LocalName.Equals("PARENT", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
            var gstin = ledger.DescendantsAndSelf()
                .Where(element => element.Name.LocalName.Contains("GSTIN", StringComparison.OrdinalIgnoreCase))
                .Select(element => new string((element.Value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()))
                .FirstOrDefault(value => value.Length == 15) ?? string.Empty;
            var openingText = ledger.Elements().FirstOrDefault(element =>
                element.Name.LocalName.Equals("OPENINGBALANCE", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
            var openingBalance = ParseTallyOptionalBusinessBalance(openingText);
            var closingText = ledger.Elements().FirstOrDefault(element =>
                element.Name.LocalName.Equals("CLOSINGBALANCE", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
            var closingBalance = ParseTallyOptionalBusinessBalance(closingText);
            var masterId = ledger.Attributes().FirstOrDefault(attribute =>
                               attribute.Name.LocalName.Equals("MASTERID", StringComparison.OrdinalIgnoreCase))?.Value?.Trim()
                           ?? ledger.Elements().FirstOrDefault(element =>
                               element.Name.LocalName.Equals("MASTERID", StringComparison.OrdinalIgnoreCase))?.Value?.Trim()
                           ?? string.Empty;
            var guid = ledger.Attributes().FirstOrDefault(attribute =>
                           attribute.Name.LocalName.Equals("GUID", StringComparison.OrdinalIgnoreCase))?.Value?.Trim()
                       ?? ledger.Elements().FirstOrDefault(element =>
                           element.Name.LocalName.Equals("GUID", StringComparison.OrdinalIgnoreCase))?.Value?.Trim()
                       ?? string.Empty;
            var alterText = ledger.Attributes().FirstOrDefault(attribute =>
                                attribute.Name.LocalName.Equals("ALTERID", StringComparison.OrdinalIgnoreCase))?.Value
                            ?? ledger.Elements().FirstOrDefault(element =>
                                element.Name.LocalName.Equals("ALTERID", StringComparison.OrdinalIgnoreCase))?.Value
                            ?? string.Empty;
            _ = long.TryParse(new string(alterText.Where(char.IsDigit).ToArray()), out var alterId);
            result.Add(new TallyLedgerNameAuthority
            {
                ExactName = exactName,
                DisplayName = CleanTallyMasterDisplayName(exactName),
                Parent = CleanTallyMasterDisplayName(parent),
                Gstin = gstin,
                OpeningBalance = openingBalance,
                ClosingBalance = closingBalance,
                MasterId = masterId,
                Guid = guid,
                AlterId = alterId
            });
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> ParseGroupParentsFromXml(string cleaned)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var document = XDocument.Parse(cleaned, LoadOptions.None);
        foreach (var group in document.Descendants().Where(element =>
                     element.Name.LocalName.Equals("GROUP", StringComparison.OrdinalIgnoreCase)))
        {
            var attributeName = group.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase))?.Value?.Trim() ?? string.Empty;
            var childName = group.Elements().FirstOrDefault(element =>
                element.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase))?.Value?.Trim() ?? string.Empty;
            var name = attributeName.Length > 0 ? attributeName : childName;
            if (name.Length == 0) continue;
            var parent = group.Elements().FirstOrDefault(element =>
                element.Name.LocalName.Equals("PARENT", StringComparison.OrdinalIgnoreCase))?.Value?.Trim() ?? string.Empty;
            result[name] = parent;
        }
        return result;
    }

    public async Task<IReadOnlyList<TallyLedgerNameAuthority>> ExportFreshLedgerNameAuthoritiesAsync(
        TallyCompanyInfo company,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);
        // Compatibility/recovery-only direct Ledger read. Fix66 normal Bank/accounting
        // export no longer calls this method: the initial full Master snapshot and the
        // ordinary ALTERID delta already carry MASTERID/GUID/current Ledger name.
        // Standalone callers may still explicitly request a fresh direct authority.
        InvalidateFullSnapshotCache();
        if (ShouldUseErp9CompatibilityProbe()) InvalidateErp9CoreMastersCache();
        return await ExportLedgerNameAuthoritiesAsync(company, cancellationToken).ConfigureAwait(false);
    }

    private static decimal? ParseTallyBusinessBalance(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return 0m;
        var upper = text.ToUpperInvariant();
        var isCredit = Regex.IsMatch(upper, @"(?:^|\s)CR(?:$|\s)");
        var isDebit = Regex.IsMatch(upper, @"(?:^|\s)DR(?:$|\s)");
        var numeric = Regex.Replace(text, @"[^0-9.\-]", string.Empty);
        if (!decimal.TryParse(numeric, NumberStyles.Number | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
            return null;
        if (isDebit) return Math.Abs(amount);
        if (isCredit) return -Math.Abs(amount);
        // Native Tally OPENINGBALANCE uses negative = Dr and positive = Cr.
        // AAH business balances use positive = Dr and negative = Cr.
        return -amount;
    }

    private static decimal? ParseTallyOptionalBusinessBalance(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 0 ? null : ParseTallyBusinessBalance(text);
    }


    public async Task<IReadOnlyList<TallyLedgerNameAuthority>> ExportLedgerNameAuthoritiesAsync(
        TallyCompanyInfo company,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);

        // The exact Tally Ledger spelling is execution authority. Do not Trim or
        // normalize ExactName: legacy ERP 9 companies can contain a Ledger whose
        // stored name ends in spaces or CR/LF characters. AAH may normalize only
        // DisplayName for matching/UI; voucher XML must address ExactName verbatim.
        var cleaned = TryGetCachedFullSnapshotXml(company, out var cachedSnapshotXml)
            ? CleanXml(cachedSnapshotXml)
            : ShouldUseErp9CompatibilityProbe()
                ? await GetErp9CoreMastersResponseAsync(company, cancellationToken)
                : CleanXml(await PostAsync(
                    BuildCollectionRequest(
                        company.Name,
                        "AAH Live Ledger Existence",
                        "Ledger",
                        "Name,Parent,MasterID,GUID,AlterID"),
                    cancellationToken));
        try
        {
            return ParseLedgerNameAuthoritiesFromXml(cleaned);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Tally returned an unreadable live Ledger collection while verifying dependent Masters.",
                exception);
        }
    }

    /// <summary>
    /// Reads Ledger balance authority for one explicit financial period. This bypasses
    /// any generic Master snapshot so OPENINGBALANCE/CLOSINGBALANCE are evaluated for
    /// the Audit client's selected FY rather than whichever period Tally last displayed.
    /// ERP 9 retains its proven compatibility Master read when the period collection is
    /// not the active transport.
    /// </summary>
    public async Task<IReadOnlyList<TallyLedgerNameAuthority>> ExportLedgerNameAuthoritiesForPeriodAsync(
        TallyCompanyInfo company,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);
        fromDate = fromDate.Date;
        toDate = toDate.Date;
        if (fromDate > toDate) (fromDate, toDate) = (toDate, fromDate);

        if (ShouldUseErp9CompatibilityProbe())
            return await ExportLedgerNameAuthoritiesAsync(company, cancellationToken).ConfigureAwait(false);

        var cleaned = CleanXml(await PostAsync(
            BuildCollectionRequest(
                company.Name,
                "AAH Audit FY Ledger Balance Authority",
                "Ledger",
                "Name,Parent,OpeningBalance,ClosingBalance,MasterID,GUID,AlterID,LedgerGSTIN,GSTIN",
                fromDate,
                toDate),
            cancellationToken).ConfigureAwait(false));
        try
        {
            return ParseLedgerNameAuthoritiesFromXml(cleaned);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Tally returned an unreadable Ledger balance authority for {fromDate:dd-MMM-yyyy} to {toDate:dd-MMM-yyyy}.",
                exception);
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> ExportLedgerParentsAsync(
        TallyCompanyInfo company,
        CancellationToken cancellationToken = default)
    {
        var authorities = await ExportLedgerNameAuthoritiesAsync(company, cancellationToken);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var authority in authorities)
        {
            if (authority.DisplayName.Length == 0) continue;
            result[authority.DisplayName] = authority.Parent;
        }
        return result;
    }

    private static string CleanTallyMasterDisplayName(string? value) =>
        Regex.Replace((value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' '), @"\s+", " ").Trim();

    public async Task<IReadOnlyDictionary<string, string>> ExportGroupParentsAsync(
        TallyCompanyInfo company,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);

        var cleaned = TryGetCachedFullSnapshotXml(company, out var cachedSnapshotXml)
            ? CleanXml(cachedSnapshotXml)
            : ShouldUseErp9CompatibilityProbe()
                ? await GetErp9CoreMastersResponseAsync(company, cancellationToken)
                : CleanXml(await PostAsync(
                    BuildCollectionRequest(
                        company.Name,
                        "AAH Live Group Parent Authority",
                        "Group",
                        "Name,Parent,MasterID,GUID,AlterID"),
                    cancellationToken));
        try
        {
            return ParseGroupParentsFromXml(cleaned);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Tally returned an unreadable live Group collection while classifying Bank/OD/Secured-Loan ledgers.",
                exception);
        }
    }

    public async Task<IReadOnlySet<string>> ExportGroupNamesAsync(
        TallyCompanyInfo company,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);

        var cleaned = TryGetCachedFullSnapshotXml(company, out var cachedSnapshotXml)
            ? CleanXml(cachedSnapshotXml)
            : ShouldUseErp9CompatibilityProbe()
                ? await GetErp9CoreMastersResponseAsync(company, cancellationToken)
                : CleanXml(await PostAsync(
                    BuildCollectionRequest(
                        company.Name,
                        "AAH Live Group Existence",
                        "Group",
                        "Name"),
                    cancellationToken));
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var document = XDocument.Parse(cleaned, LoadOptions.None);
            foreach (var group in document.Descendants().Where(element =>
                         element.Name.LocalName.Equals("GROUP", StringComparison.OrdinalIgnoreCase)))
            {
                var attributeName = group.Attributes().FirstOrDefault(attribute =>
                    attribute.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase))?.Value?.Trim() ?? string.Empty;
                var childName = group.Elements().FirstOrDefault(element =>
                    element.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase))?.Value?.Trim() ?? string.Empty;
                var name = attributeName.Length > 0 ? attributeName : childName;
                if (name.Length > 0) result.Add(name);
            }
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Tally returned an unreadable live Group collection while verifying Ledger parent Groups.",
                exception);
        }

        return result;
    }

    public async Task<bool> LedgerExistsAsync(
        TallyCompanyInfo company,
        string ledgerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);
        ledgerName = (ledgerName ?? string.Empty).Trim();
        if (ledgerName.Length == 0) return false;
        var ledgers = await ExportLedgerParentsAsync(company, cancellationToken);
        return ledgers.ContainsKey(ledgerName);
    }

    public async Task<TallyReportSnapshot> ExportReportAsync(
        TallyCompanyInfo company,
        string displayName,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);
        fromDate = fromDate.Date;
        toDate = toDate.Date;
        if (fromDate > toDate)
        {
            throw new InvalidOperationException(
                "The Tally report From Date cannot be after the To Date.");
        }

        var definition = TallyReportCatalog.Find(displayName)
            ?? new TallyReportDefinition(displayName.Trim(), [displayName.Trim()]);
        var erp9Mode = ShouldUseErp9CompatibilityProbe();
        if (erp9Mode && !TallyReportCatalog.IsErp9Safe(definition.DisplayName))
        {
            return new TallyReportSnapshot
            {
                DisplayName = definition.DisplayName,
                ReportId = definition.ReportIds.FirstOrDefault() ?? definition.DisplayName,
                FromDate = fromDate,
                ToDate = toDate,
                IsAvailable = false,
                Status = "This report is not in the ERP 9 safe on-demand catalog.",
                Error = "Fix37 keeps ERP 9 reports explicit/lazy and permits only the runtime-safe common report catalog.",
                RefreshedUtc = DateTime.UtcNow
            };
        }
        var errors = new List<string>();
        var reportIds = definition.ReportIds;

        foreach (var reportId in reportIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(reportId))
            {
                continue;
            }

            try
            {
                var response = await PostAsync(
                    BuildReportRequest(
                        company.Name,
                        reportId,
                        fromDate,
                        toDate),
                    cancellationToken,
                    erp9Mode ? Timeout.InfiniteTimeSpan : null);

                var cleaned = CleanXml(response);
                var lineError = ReadTag(cleaned, "LINEERROR");
                if (lineError.Length > 0 ||
                    cleaned.Contains("REPORT NOT FOUND", StringComparison.OrdinalIgnoreCase) ||
                    cleaned.Contains("COULD NOT FIND", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(lineError.Length > 0
                        ? $"{reportId}: {lineError}"
                        : $"{reportId}: report was not available.");
                    continue;
                }

                return ParseReportSnapshot(
                    definition.DisplayName,
                    reportId,
                    fromDate,
                    toDate,
                    cleaned);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add($"{reportId}: {exception.Message}");
            }
        }

        return new TallyReportSnapshot
        {
            DisplayName = definition.DisplayName,
            ReportId = definition.ReportIds.FirstOrDefault() ?? definition.DisplayName,
            FromDate = fromDate,
            ToDate = toDate,
            IsAvailable = false,
            Status = "Tally did not return this report for the linked company.",
            Error = string.Join(" | ", errors.Distinct()),
            RefreshedUtc = DateTime.UtcNow
        };
    }

    public async Task<string> ImportXmlAsync(
        string xml,
        CancellationToken cancellationToken = default)
    {
        var response = await PostImportAsync(xml, cancellationToken).ConfigureAwait(false);
        // Any accepted Tally mutation invalidates the pre-import read snapshot. A later
        // verification/read must come from Tally again instead of reusing stale authority.
        InvalidateFullSnapshotCache();
        if (ShouldUseErp9CompatibilityProbe() && ContainsErp9MasterMutation(xml))
            InvalidateErp9CoreMastersCache();
        return response;
    }


    public async Task<TallyImportResponse> DeleteAccountingMasterAsync(
        TallyCompanyInfo company,
        string masterType,
        string masterName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);
        var type = (masterType ?? string.Empty).Trim();
        var name = (masterName ?? string.Empty).Trim();
        if (name.Length == 0) throw new InvalidOperationException("Cannot delete a Tally Master with a blank name.");
        if (!type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
            !type.Equals("Group", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Live Tally Master delete supports Ledger and Group only.");

        var element = type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) ? "LEDGER" : "GROUP";
        var builder = new StringBuilder();
        builder.AppendLine("<ENVELOPE>");
        builder.AppendLine("  <HEADER><TALLYREQUEST>Import Data</TALLYREQUEST></HEADER>");
        builder.AppendLine("  <BODY><IMPORTDATA><REQUESTDESC><REPORTNAME>All Masters</REPORTNAME><STATICVARIABLES>");
        builder.Append("    <SVCURRENTCOMPANY>").Append(XmlEscape(company.Name)).AppendLine("</SVCURRENTCOMPANY>");
        builder.AppendLine("  </STATICVARIABLES></REQUESTDESC><REQUESTDATA>");
        builder.AppendLine("    <TALLYMESSAGE xmlns:UDF=\"TallyUDF\">");
        builder.Append("      <").Append(element).Append(" NAME=\"").Append(XmlAttribute(name))
            .Append("\" RESERVEDNAME=\"\" ACTION=\"Delete\">").AppendLine();
        builder.Append("        <NAME>").Append(XmlEscape(name)).AppendLine("</NAME>");
        builder.Append("      </").Append(element).AppendLine(">");
        builder.AppendLine("    </TALLYMESSAGE>");
        builder.AppendLine("  </REQUESTDATA></IMPORTDATA></BODY>");
        builder.AppendLine("</ENVELOPE>");

        var response = ParseImportResponse(await ImportXmlAsync(builder.ToString(), cancellationToken).ConfigureAwait(false));
        if (response.Errors > 0 || !string.IsNullOrWhiteSpace(response.LineError))
            throw new InvalidOperationException($"Tally rejected Master Delete: Deleted={response.Deleted}; Errors={response.Errors}; {response.LineError}");

        var stillExists = type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)
            ? (await ExportLedgerNameAuthoritiesAsync(company, cancellationToken).ConfigureAwait(false))
                .Any(item => TallyMasterIdentity.Equivalent(item.DisplayName, name))
            : (await ExportGroupNamesAsync(company, cancellationToken).ConfigureAwait(false))
                .Any(item => TallyMasterIdentity.Equivalent(item, name));
        if (stillExists)
            throw new InvalidOperationException($"Tally accepted the {type} delete request but live verification still finds '{name}'.");

        return response;
    }

    public async Task<TallyImportResponse> UpsertAccountingMasterAsync(
        TallyCompanyInfo company,
        MasterRecord master,
        string action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(master);
        var normalizedAction = action.Equals("Alter", StringComparison.OrdinalIgnoreCase) ? "Alter" : "Create";
        if (!master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
            !master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Live Tally Master update supports Ledger and Group only.");

        var builder = new StringBuilder();
        builder.AppendLine("<ENVELOPE>");
        builder.AppendLine("  <HEADER><TALLYREQUEST>Import Data</TALLYREQUEST></HEADER>");
        builder.AppendLine("  <BODY><IMPORTDATA><REQUESTDESC><REPORTNAME>All Masters</REPORTNAME><STATICVARIABLES>");
        builder.Append("    <SVCURRENTCOMPANY>").Append(XmlEscape(company.Name)).AppendLine("</SVCURRENTCOMPANY>");
        builder.AppendLine("  </STATICVARIABLES></REQUESTDESC><REQUESTDATA>");
        builder.AppendLine("    <TALLYMESSAGE xmlns:UDF=\"TallyUDF\">");
        if (master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append("      <LEDGER NAME=\"").Append(XmlAttribute(master.Name)).Append("\" RESERVEDNAME=\"\" ACTION=\"")
                .Append(normalizedAction).AppendLine("\">");
            builder.Append("        <NAME>").Append(XmlEscape(master.Name)).AppendLine("</NAME>");
            var expectedParent = string.IsNullOrWhiteSpace(master.Parent) ? "Primary" : master.Parent.Trim();
            builder.Append("        <PARENT>").Append(XmlEscape(expectedParent)).AppendLine("</PARENT>");
            if (master.OpeningBalance.HasValue)
            {
                // AAH stores +Debit/-Credit; native Tally uses the opposite sign.
                builder.Append("        <OPENINGBALANCE>")
                    .Append((-master.OpeningBalance.Value).ToString("0.################", CultureInfo.InvariantCulture))
                    .AppendLine("</OPENINGBALANCE>");
            }
            builder.AppendLine("      </LEDGER>");
        }
        else
        {
            builder.Append("      <GROUP NAME=\"").Append(XmlAttribute(master.Name)).Append("\" RESERVEDNAME=\"\" ACTION=\"")
                .Append(normalizedAction).AppendLine("\">");
            builder.Append("        <NAME>").Append(XmlEscape(master.Name)).AppendLine("</NAME>");
            builder.Append("        <PARENT>").Append(XmlEscape(string.IsNullOrWhiteSpace(master.Parent) ? "Primary" : master.Parent.Trim())).AppendLine("</PARENT>");
            builder.AppendLine("      </GROUP>");
        }
        builder.AppendLine("    </TALLYMESSAGE>");
        builder.AppendLine("  </REQUESTDATA></IMPORTDATA></BODY>");
        builder.AppendLine("</ENVELOPE>");

        var response = ParseImportResponse(await ImportXmlAsync(builder.ToString(), cancellationToken).ConfigureAwait(false));
        if (response.Errors > 0 || !string.IsNullOrWhiteSpace(response.LineError))
            throw new InvalidOperationException($"Tally rejected Master {normalizedAction}: Created={response.Created}; Altered={response.Altered}; Errors={response.Errors}; {response.LineError}");

        if (master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
        {
            var parents = await ExportLedgerParentsAsync(company, cancellationToken).ConfigureAwait(false);
            var actual = parents.FirstOrDefault(pair => TallyMasterIdentity.Equivalent(pair.Key, master.Name));
            var expectedParent = string.IsNullOrWhiteSpace(master.Parent) ? "Primary" : master.Parent.Trim();
            var parentMatches = expectedParent.Equals("Primary", StringComparison.OrdinalIgnoreCase)
                ? string.IsNullOrWhiteSpace(actual.Value) || TallyMasterIdentity.Equivalent(actual.Value, expectedParent)
                : TallyMasterIdentity.Equivalent(actual.Value, expectedParent);
            if (string.IsNullOrWhiteSpace(actual.Key) || !parentMatches)
                throw new InvalidOperationException($"Tally accepted the Ledger update but live verification did not confirm '{master.Name}' under '{expectedParent}'.");

            if (master.OpeningBalance.HasValue)
            {
                var xml = await ExportMastersAsync(company, cancellationToken).ConfigureAwait(false);
                var foundOpening = TryReadLedgerOpeningBalance(xml, master.Name, out var nativeOpening);
                var verified = foundOpening
                    ? Math.Abs((-nativeOpening) - master.OpeningBalance.Value) <= 0.01m
                    : Math.Abs(master.OpeningBalance.Value) <= 0.01m;
                if (!verified)
                    throw new InvalidOperationException($"Tally accepted the Ledger update but the merged opening balance for '{master.Name}' was not live-verified.");
            }
        }
        else
        {
            var parents = await ExportGroupParentsAsync(company, cancellationToken).ConfigureAwait(false);
            var actual = parents.FirstOrDefault(pair => TallyMasterIdentity.Equivalent(pair.Key, master.Name));
            var expectedParent = string.IsNullOrWhiteSpace(master.Parent) ? "Primary" : master.Parent;
            var parentMatches = expectedParent.Equals("Primary", StringComparison.OrdinalIgnoreCase)
                ? string.IsNullOrWhiteSpace(actual.Value) || TallyMasterIdentity.Equivalent(actual.Value, expectedParent)
                : TallyMasterIdentity.Equivalent(actual.Value, expectedParent);
            if (string.IsNullOrWhiteSpace(actual.Key) || !parentMatches)
                throw new InvalidOperationException($"Tally accepted the Group update but live verification did not confirm '{master.Name}' under '{expectedParent}'.");
        }
        return response;
    }

    private static bool TryReadLedgerOpeningBalance(string xml, string ledgerName, out decimal nativeOpeningBalance)
    {
        nativeOpeningBalance = 0m;
        try
        {
            var document = XDocument.Parse(xml, LoadOptions.None);
            var ledger = document.Descendants().FirstOrDefault(element =>
                element.Name.LocalName.Equals("LEDGER", StringComparison.OrdinalIgnoreCase) &&
                TallyMasterIdentity.Equivalent(
                    element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase))?.Value ??
                    element.Elements().FirstOrDefault(child => child.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty,
                    ledgerName));
            var raw = ledger?.Elements().FirstOrDefault(child => child.Name.LocalName.Equals("OPENINGBALANCE", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
            var numeric = Regex.Replace(raw, @"[^0-9.\-]", string.Empty);
            return decimal.TryParse(numeric, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out nativeOpeningBalance);
        }
        catch
        {
            return false;
        }
    }

    public async Task<int> RepairGeneratedRcmMasterParentsAsync(
        TallyCompanyInfo company,
        IReadOnlyDictionary<string, string> liveLedgerParents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(liveLedgerParents);

        var repairs = liveLedgerParents
            .Select(pair => new
            {
                Name = pair.Key.Trim(),
                CurrentParent = (pair.Value ?? string.Empty).Trim(),
                ExpectedParent = PurchaseTreatmentPolicy.ExpectedGeneratedRcmParent(pair.Key)
            })
            .Where(item => item.Name.Length > 0 && item.ExpectedParent.Length > 0)
            .Where(item => !TallyMasterIdentity.Key(item.CurrentParent).Equals(
                TallyMasterIdentity.Key(item.ExpectedParent),
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (repairs.Length == 0)
        {
            return 0;
        }

        var builder = new StringBuilder();
        builder.AppendLine("<ENVELOPE>");
        builder.AppendLine("  <HEADER>");
        builder.AppendLine("    <TALLYREQUEST>Import Data</TALLYREQUEST>");
        builder.AppendLine("  </HEADER>");
        builder.AppendLine("  <BODY>");
        builder.AppendLine("    <IMPORTDATA>");
        builder.AppendLine("      <REQUESTDESC>");
        builder.AppendLine("        <REPORTNAME>All Masters</REPORTNAME>");
        builder.AppendLine("        <STATICVARIABLES>");
        builder.Append("          <SVCURRENTCOMPANY>")
            .Append(XmlEscape(company.Name))
            .AppendLine("</SVCURRENTCOMPANY>");
        builder.AppendLine("        </STATICVARIABLES>");
        builder.AppendLine("      </REQUESTDESC>");
        builder.AppendLine("      <REQUESTDATA>");
        foreach (var repair in repairs)
        {
            builder.AppendLine("        <TALLYMESSAGE xmlns:UDF=\"TallyUDF\">");
            builder.Append("          <LEDGER NAME=\"")
                .Append(XmlAttribute(repair.Name))
                .AppendLine("\" RESERVEDNAME=\"\" ACTION=\"Alter\">");
            builder.Append("            <NAME>")
                .Append(XmlEscape(repair.Name))
                .AppendLine("</NAME>");
            builder.Append("            <PARENT>")
                .Append(XmlEscape(repair.ExpectedParent))
                .AppendLine("</PARENT>");
            builder.AppendLine("          </LEDGER>");
            builder.AppendLine("        </TALLYMESSAGE>");
        }
        builder.AppendLine("      </REQUESTDATA>");
        builder.AppendLine("    </IMPORTDATA>");
        builder.AppendLine("  </BODY>");
        builder.AppendLine("</ENVELOPE>");

        var response = ParseImportResponse(await ImportXmlAsync(builder.ToString(), cancellationToken));
        if (response.Errors > 0 || !string.IsNullOrWhiteSpace(response.LineError))
        {
            throw new InvalidOperationException(
                $"Generated RCM Master parent repair was rejected by Tally. " +
                $"Altered={response.Altered}; Errors={response.Errors}; {response.LineError}");
        }

        var refreshed = await ExportLedgerParentsAsync(company, cancellationToken);
        var unresolved = repairs
            .Where(repair => !refreshed.TryGetValue(repair.Name, out var actualParent) ||
                !TallyMasterIdentity.Key(actualParent).Equals(
                    TallyMasterIdentity.Key(repair.ExpectedParent),
                    StringComparison.OrdinalIgnoreCase))
            .Select(repair => repair.Name)
            .ToArray();

        if (unresolved.Length > 0)
        {
            throw new InvalidOperationException(
                "Tally accepted the generated RCM Master repair request but the corrected parent " +
                $"was not live-verified for: {string.Join(", ", unresolved)}.");
        }

        return repairs.Length;
    }

    /// <summary>
    /// Lightweight voucher-only change marker used by incremental Bank reconciliation.
    /// It deliberately avoids a full Voucher collection. When Tally exposes LastAlterID,
    /// unchanged client-open/Refresh cycles can return immediately without rescanning FY data.
    /// </summary>
    public async Task<TallyVoucherChangeMarker> GetVoucherChangeMarkerAsync(
        TallyCompanyInfo company,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);
        // Fix37: one tiny pulse carries the current company, ALTERID and object counts.
        // It replaces the old two-request NumVouchers + LastAlterID probe.
        var markerTimeout = ShouldUseErp9CompatibilityProbe() ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(5);
        var response = await PostAsync(
            BuildAuthorityPulseRequest(company.Name), cancellationToken, markerTimeout).ConfigureAwait(false);
        var cleaned = CleanXml(response);

        static int IntTag(string xml, string tag)
        {
            var raw = ReadTag(xml, tag);
            var digits = new string((raw ?? string.Empty).Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var value) ? value : 0;
        }
        static long LongTag(string xml, string tag, out bool ok)
        {
            var raw = ReadTag(xml, tag);
            var digits = new string((raw ?? string.Empty).Where(char.IsDigit).ToArray());
            ok = long.TryParse(digits, out var value);
            return ok ? value : 0L;
        }

        var voucherCount = IntTag(cleaned, "AAHVOUCHERCOUNT");
        var ledgerCount = IntTag(cleaned, "AAHLEDGERCOUNT");
        var groupCount = IntTag(cleaned, "AAHGROUPCOUNT");
        var voucherTypeCount = IntTag(cleaned, "AAHVOUCHERTYPECOUNT");
        var stockItemCount = IntTag(cleaned, "AAHSTOCKITEMCOUNT");
        var unitCount = IntTag(cleaned, "AAHUNITCOUNT");
        var godownCount = IntTag(cleaned, "AAHGODOWNCOUNT");
        var alterId = LongTag(cleaned, "AAHLASTALTERID", out var hasAlter);
        var currentCompany = CleanText(ReadTag(cleaned, "AAHCOMPANY"));
        // Fix124: Master-family counts are part of the authority token. A Stock Item,
        // Unit, Godown or Voucher Type change must wake the same normal background sync
        // even when voucher/ledger/group counts are unchanged.
        var token = string.Join("|", company.CompanyKey, voucherCount, ledgerCount, groupCount,
            voucherTypeCount, stockItemCount, unitCount, godownCount, alterId);
        return new TallyVoucherChangeMarker
        {
            Token = token,
            CompanyName = currentCompany,
            VoucherCount = voucherCount,
            LedgerCount = ledgerCount,
            GroupCount = groupCount,
            VoucherTypeCount = voucherTypeCount,
            StockItemCount = stockItemCount,
            UnitCount = unitCount,
            GodownCount = godownCount,
            LastAlterId = alterId,
            HasReliableAlterId = hasAlter
        };
    }

    public async Task<TallyAuthorityDeltaSnapshot> ExportAuthorityChangesAfterAlterIdAsync(
        TallyCompanyInfo company,
        DateTime fromDate,
        DateTime toDate,
        long afterAlterId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);
        if (afterAlterId < 0) afterAlterId = 0;
        var erp9 = ShouldUseErp9CompatibilityProbe();
        var request = BuildAuthorityDeltaRequest(company.Name, fromDate.Date, toDate.Date, afterAlterId, erp9);
        var response = erp9
            ? await PostErp9TraceAsync($"Authority-Delta-{afterAlterId}", request, cancellationToken).ConfigureAwait(false)
            : await PostAsync(request, cancellationToken).ConfigureAwait(false);
        var cleaned = CleanXml(response);
        var vouchers = ValidateVoucherIndexRange(
            ParseVoucherIndex(cleaned, company.CompanyKey, company.FinancialYear), fromDate.Date, toDate.Date);
        var masterIdByGuid = vouchers
            .Where(voucher => !string.IsNullOrWhiteSpace(voucher.Guid) && !string.IsNullOrWhiteSpace(voucher.MasterId))
            .GroupBy(voucher => voucher.Guid.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().MasterId.Trim(), StringComparer.OrdinalIgnoreCase);
        var fullVouchers = ParseVoucherXmlSnapshots(cleaned, masterIdByGuid)
            .Where(snapshot => snapshot.VoucherDate is DateTime date && date.Date >= fromDate.Date && date.Date <= toDate.Date)
            .GroupBy(snapshot => snapshot.MasterId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(snapshot => snapshot.OriginalXml.Length).First())
            .ToArray();
        var deltaIds = vouchers.Where(voucher => !string.IsNullOrWhiteSpace(voucher.MasterId))
            .Select(voucher => voucher.MasterId.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fullDeltaIds = fullVouchers.Select(voucher => voucher.MasterId.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingFullDelta = deltaIds.Where(id => !fullDeltaIds.Contains(id)).Take(10).ToArray();
        if (missingFullDelta.Length > 0)
            throw new InvalidOperationException(
                $"Tally ALTERID delta omitted complete Voucher XML for MASTERID(s): {string.Join(", ", missingFullDelta)}. The shared authority checkpoint was not advanced.");
        return new TallyAuthorityDeltaSnapshot
        {
            Xml = cleaned,
            Vouchers = vouchers,
            FullVouchers = fullVouchers,
            MaxAlterId = ParseMaxAlterId(cleaned),
            LedgerParents = ParseLedgerParentsFromSnapshot(cleaned),
            GroupNames = ParseGroupNamesFromSnapshot(cleaned)
        };
    }

    /// <summary>
    /// Reads only Voucher objects altered after the supplied Tally ALTERID within the FY.
    /// This is the normal update path after the first Bank cache seed.
    /// </summary>
    public async Task<IReadOnlyList<TallyVoucherIndexRecord>> ExportVoucherChangesAfterAlterIdAsync(
        TallyCompanyInfo company,
        DateTime fromDate,
        DateTime toDate,
        long afterAlterId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);
        if (afterAlterId < 0) afterAlterId = 0;
        var request = BuildVoucherDeltaRequest(company.Name, fromDate, toDate, afterAlterId, ShouldUseErp9CompatibilityProbe());
        var response = ShouldUseErp9CompatibilityProbe()
            ? await PostErp9TraceAsync($"Voucher-Delta-{afterAlterId}", request, cancellationToken).ConfigureAwait(false)
            : await PostAsync(request, cancellationToken).ConfigureAwait(false);
        return ValidateVoucherIndexRange(
            ParseVoucherIndex(response, company.CompanyKey, company.FinancialYear),
            fromDate, toDate);
    }

    public async Task<string> GetChangeTokenAsync(
        TallyCompanyInfo company,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);

        var values = new List<string>
        {
            company.CompanyKey,
            await ReadFunctionValueSafeAsync(company.Name, "$$NumLedgers", cancellationToken),
            await ReadFunctionValueSafeAsync(company.Name, "$$NumStockItems", cancellationToken),
            await ReadFunctionValueSafeAsync(company.Name, "$$NumVouchers", cancellationToken),
            await ReadFunctionValueSafeAsync(company.Name, "$$LastAlterID", cancellationToken)
        };

        return string.Join("|", values);
    }

    public static TallyImportResponse ParseImportResponse(string response)
    {
        var cleaned = CleanXml(response);
        var parsed = new TallyImportResponse
        {
            Created = ReadIntTag(cleaned, "CREATED"),
            Altered = ReadIntTag(cleaned, "ALTERED"),
            Deleted = ReadIntTag(cleaned, "DELETED"),
            Ignored = ReadIntTag(cleaned, "IGNORED"),
            Exceptions = ReadIntTag(cleaned, "EXCEPTIONS"),
            Errors = ReadIntTag(cleaned, "ERRORS"),
            LastVoucherId = ReadTag(cleaned, "LASTVCHID"),
            LastMasterId = ReadTag(cleaned, "LASTMID"),
            LineError = ReadTag(cleaned, "LINEERROR")
        };

        // Tally can reject the protocol envelope before its import engine runs.
        // In that case it may return only <RESPONSE>message</RESPONSE> (for example
        // "Unknown Request, cannot be processed") and none of the normal counters.
        // Never report such a protocol rejection as a clean zero-error import.
        if (parsed.Errors == 0 &&
            parsed.Created == 0 &&
            parsed.Altered == 0 &&
            parsed.Deleted == 0 &&
            parsed.Ignored == 0 &&
            parsed.Exceptions == 0 &&
            string.IsNullOrWhiteSpace(parsed.LineError))
        {
            // A normal clean Tally import response is itself rooted at <RESPONSE> and
            // contains child counters such as <CREATED>, <ALTERED> and <ERRORS>. The old
            // fallback used ReadTag("RESPONSE"), which flattened those child values into
            // text like "0 0 0 0 ..." and incorrectly converted a clean zero-work response
            // (for example an empty Master stage) into an error. Only a text-only RESPONSE
            // with no child elements is a protocol rejection message.
            var responseMessage = ReadTextOnlyResponseMessage(cleaned);
            var status = ReadTag(cleaned, "STATUS");
            var bodyData = ReadTag(cleaned, "DATA");

            if (!string.IsNullOrWhiteSpace(responseMessage))
            {
                parsed.Errors = 1;
                parsed.LineError = responseMessage;
            }
            else if (status.Equals("0", StringComparison.OrdinalIgnoreCase))
            {
                parsed.Errors = 1;
                parsed.LineError = string.IsNullOrWhiteSpace(bodyData)
                    ? "Tally rejected the import request."
                    : bodyData;
            }
        }

        return parsed;
    }


    private static string ReadTextOnlyResponseMessage(string cleaned)
    {
        if (string.IsNullOrWhiteSpace(cleaned)) return string.Empty;
        try
        {
            var root = XDocument.Parse(cleaned, LoadOptions.PreserveWhitespace).Root;
            if (root is null || !root.Name.LocalName.Equals("RESPONSE", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            if (root.Elements().Any()) return string.Empty;
            return (root.Value ?? string.Empty).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private bool ShouldUseErp9CompatibilityProbe()
    {
        // FIX3: ERP 9 is fail-closed. Only positive product evidence may select the legacy
        // route. Unknown/ambiguous Tally.exe stays on the normal Tally path.
        lock (_runtimeDetectionGate)
            return _runtimeFamily == TallyRuntimeFamily.Erp9;
    }

    private async Task RefreshRuntimeFamilyAsync(CancellationToken cancellationToken)
    {
        lock (_runtimeDetectionGate)
        {
            if (_runtimeFamilyCheckedUtc != DateTime.MinValue &&
                DateTime.UtcNow - _runtimeFamilyCheckedUtc < TimeSpan.FromSeconds(10))
                return;
        }

        var detected = TallyRuntimeFamily.Unknown;
        var evidence = string.Empty;

        // Fix110 R4: for a local ERP 9 executable, classify from positive executable
        // metadata before sending any network probe. That lets the very first ERP 9
        // HTTP/XML request inherit the no-application-deadline transport contract.
        if (_endpoint.IsLoopback)
        {
            var localDetected = DetectLocalRuntimeFamilyFromExecutableMetadata(out var localEvidence);
            if (localDetected == TallyRuntimeFamily.Erp9)
            {
                lock (_runtimeDetectionGate)
                {
                    _runtimeFamily = localDetected;
                    _runtimeDetectionEvidence = localEvidence;
                    _runtimeFamilyCheckedUtc = DateTime.UtcNow;
                }
                return;
            }
        }

        try
        {
            // Tally documents $$ProdInfo:ProdMajorVersion/ProdMinorVersion as the product
            // compatibility discriminator: ERP 9 = 1.0; TallyPrime = 1.x where x > 0.
            var response = await PostAsync(
                BuildProductFamilyProbeRequest(),
                cancellationToken,
                TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            detected = ParseProductFamilyProbe(response, out evidence);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            evidence = $"Live product probe unavailable: {exception.Message}";
        }

        // A live probe is authoritative. If it was unavailable or inconclusive, local
        // executable VERSION metadata may provide positive evidence. Merely finding a
        // process named Tally.exe is intentionally NOT ERP 9 evidence.
        if (detected == TallyRuntimeFamily.Unknown && _endpoint.IsLoopback)
        {
            var localDetected = DetectLocalRuntimeFamilyFromExecutableMetadata(out var localEvidence);
            if (localDetected != TallyRuntimeFamily.Unknown)
            {
                detected = localDetected;
                evidence = localEvidence;
            }
            else if (localEvidence.Length > 0)
            {
                evidence = evidence.Length == 0 ? localEvidence : $"{evidence} | {localEvidence}";
            }
        }

        lock (_runtimeDetectionGate)
        {
            _runtimeFamily = detected;
            _runtimeDetectionEvidence = evidence;
            _runtimeFamilyCheckedUtc = DateTime.UtcNow;
        }
    }

    private static TallyRuntimeFamily DetectLocalRuntimeFamilyFromExecutableMetadata(out string evidence)
    {
        evidence = string.Empty;
        var modernEvidence = string.Empty;
        try
        {
            foreach (var processName in new[] { "TallyPrime", "Tally" })
            {
                Process[] processes;
                try { processes = Process.GetProcessesByName(processName); }
                catch { continue; }

                try
                {
                    foreach (var process in processes)
                    {
                        try
                        {
                            if (process.ProcessName.Equals("TallyPrime", StringComparison.OrdinalIgnoreCase))
                                modernEvidence = "Local executable process is TallyPrime.";

                            var executable = process.MainModule?.FileName ?? string.Empty;
                            if (executable.Length == 0) continue;
                            var info = FileVersionInfo.GetVersionInfo(executable);
                            var signature = string.Join(" | ", new[]
                            {
                                info.ProductName ?? string.Empty,
                                info.FileDescription ?? string.Empty,
                                info.OriginalFilename ?? string.Empty
                            }.Where(value => !string.IsNullOrWhiteSpace(value)));

                            if (ContainsErp9ProductMarker(signature))
                            {
                                evidence = $"Local executable metadata identifies ERP 9: {signature}";
                                return TallyRuntimeFamily.Erp9;
                            }
                            if (ContainsPrimeProductMarker(signature))
                                modernEvidence = $"Local executable metadata identifies TallyPrime: {signature}";
                        }
                        catch
                        {
                            // Runtime classification is diagnostic. Access to MainModule can
                            // be denied; ambiguous process presence must never imply ERP 9.
                        }
                    }
                }
                finally
                {
                    foreach (var process in processes) process.Dispose();
                }
            }
        }
        catch
        {
            // Fail closed to Unknown/normal Tally route.
        }

        if (modernEvidence.Length > 0)
        {
            evidence = modernEvidence;
            return TallyRuntimeFamily.PrimeOrModern;
        }

        evidence = "No positive ERP 9 product evidence was found; using normal Tally route.";
        return TallyRuntimeFamily.Unknown;
    }

    private static bool ContainsErp9ProductMarker(string text) =>
        text.Contains("Tally.ERP 9", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Tally ERP 9", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("ERP 9", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsPrimeProductMarker(string text) =>
        text.Contains("TallyPrime", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Tally Prime", StringComparison.OrdinalIgnoreCase);

    private async Task PopulateLightweightCountsAsync(
        TallyCompanyInfo company,
        CancellationToken cancellationToken)
    {
        company.LedgerCount = await ReadFunctionIntSafeAsync(
            company.Name,
            "$$NumLedgers",
            cancellationToken);
        company.StockItemCount = await ReadFunctionIntSafeAsync(
            company.Name,
            "$$NumStockItems",
            cancellationToken);
        company.VoucherCount = await ReadFunctionIntSafeAsync(
            company.Name,
            "$$NumVouchers",
            cancellationToken);
        company.GroupCount = await ReadFunctionIntSafeAsync(
            company.Name,
            "$$NumGroups",
            cancellationToken);
        company.VoucherTypeCount = await ReadFunctionIntSafeAsync(
            company.Name,
            "$$NumVoucherTypes",
            cancellationToken);
        company.UnitCount = await ReadFunctionIntSafeAsync(
            company.Name,
            "$$NumUnits",
            cancellationToken);
        company.GodownCount = await ReadFunctionIntSafeAsync(
            company.Name,
            "$$NumGodowns",
            cancellationToken);
        company.ChangeToken = await GetChangeTokenAsync(
            company,
            cancellationToken);
    }

    private async Task<int> ReadFunctionIntSafeAsync(
        string companyName,
        string functionName,
        CancellationToken cancellationToken)
    {
        var text = await ReadFunctionValueSafeAsync(
            companyName,
            functionName,
            cancellationToken);

        return int.TryParse(
            new string(text.Where(char.IsDigit).ToArray()),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var count)
            ? count
            : 0;
    }

    private async Task<string> ReadFunctionValueSafeAsync(
        string companyName,
        string functionName,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await PostAsync(
                BuildFunctionRequest(companyName, functionName),
                cancellationToken,
                ShouldUseErp9CompatibilityProbe() ? Timeout.InfiniteTimeSpan : null);
            return ReadTag(CleanXml(response), "RESULT");
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<string> GetErp9CoreMastersResponseAsync(
        TallyCompanyInfo company,
        CancellationToken cancellationToken)
    {
        var companyName = (company.Name ?? string.Empty).Trim();
        string staleVerified = string.Empty;
        bool requiresRefresh;
        lock (_erp9CoreCacheGate)
        {
            var sameCompany = _erp9CoreCacheCompany.Equals(companyName, StringComparison.OrdinalIgnoreCase);
            var pinned = _erp9CoreSnapshotPins > 0;
            requiresRefresh = _erp9CoreCacheRequiresRefresh;
            if (_erp9CoreCacheXml.Length > 0 && sameCompany)
            {
                staleVerified = _erp9CoreCacheXml;
                if (!requiresRefresh &&
                    (pinned || DateTime.UtcNow - _erp9CoreCacheUtc <= TimeSpan.FromSeconds(15)))
                {
                    return _erp9CoreCacheXml;
                }
            }
        }

        string ledgerResponse;
        string groupResponse;
        var supportingMasterResponses = new List<string>();
        try
        {
            // Fix123 ERP 9 full Master authority: Ledger/Group remain the mandatory
            // accounting identity slices, but ERP 9 is NOT restricted to those two
            // families. Stock Items, Units, Stock Groups/Categories, Godowns, Voucher
            // Types and other supported Masters are collected normally as independent
            // native collections. This restores the same Stock-Master authority that
            // Prime already feeds into AAH without reintroducing one huge blocking report.
            ledgerResponse = await ReadErp9MasterSliceAsync(
                companyName,
                accountType: "Ledgers",
                objectType: "Ledger",
                collectionName: "AAH ERP9 Ledgers",
                fetchList: "Name,Parent,OpeningBalance,MasterID,GUID,AlterID",
                cancellationToken).ConfigureAwait(false);
            groupResponse = await ReadErp9MasterSliceAsync(
                companyName,
                accountType: "Groups",
                objectType: "Group",
                collectionName: "AAH ERP9 Groups",
                fetchList: "Name,Parent,MasterID,GUID,AlterID",
                cancellationToken).ConfigureAwait(false);

            var supportingSlices = new (string Label, string AccountType, string ObjectType, string Collection, string Fetch, int ExpectedCount)[]
            {
                ("VoucherTypes", "Voucher Types", "VoucherType", "AAH ERP9 Voucher Types", "Name,Parent,MasterID,GUID,AlterID", company.VoucherTypeCount),
                ("Units", "Units", "Unit", "AAH ERP9 Units", "Name,OriginalName,IsSimpleUnit,DecimalPlaces,BaseUnits,AdditionalUnits,Conversion,Denominator,ReportingUQCName,MasterID,GUID,AlterID", company.UnitCount),
                ("StockGroups", "Stock Groups", "StockGroup", "AAH ERP9 Stock Groups", "Name,Parent,MasterID,GUID,AlterID", 0),
                ("StockCategories", "Stock Categories", "StockCategory", "AAH ERP9 Stock Categories", "Name,Parent,MasterID,GUID,AlterID", 0),
                ("StockItems", "Stock Items", "StockItem", "AAH ERP9 Stock Items", "Name,Parent,Category,BaseUnits,AdditionalUnits,Conversion,Denominator,MailingName,HSNCode,GSTTypeOfSupply,CostingMethod,ValuationMethod,IsBatchWiseOn,IsPerishableOn,HasMfgDate,MasterID,GUID,AlterID", company.StockItemCount),
                ("Godowns", "Godowns", "Godown", "AAH ERP9 Godowns", "Name,Parent,MasterID,GUID,AlterID", company.GodownCount),
                ("CostCategories", "Cost Categories", "CostCategory", "AAH ERP9 Cost Categories", "Name,Parent,MasterID,GUID,AlterID", 0),
                ("CostCentres", "Cost Centres", "CostCentre", "AAH ERP9 Cost Centres", "Name,Parent,Category,MasterID,GUID,AlterID", 0),
                ("TaxClassifications", "Tax Classifications", "TaxClassification", "AAH ERP9 Tax Classifications", "Name,Parent,MasterID,GUID,AlterID", 0),
                ("TaxUnits", "Tax Units", "TaxUnit", "AAH ERP9 Tax Units", "Name,Parent,MasterID,GUID,AlterID", 0)
            };
            foreach (var slice in supportingSlices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Fix124: a Master family which the authority pulse says is non-empty is
                // required, not optional. In particular this prevents ERP9 Stock Items
                // from disappearing silently when the optional collection read fails.
                var response = slice.Label.Equals("StockItems", StringComparison.OrdinalIgnoreCase)
                    // Fix125: ERP 9 builds can return 0/unknown from $$NumStockItems even
                    // while Stock Items exist. Always try the native StockItem collection
                    // and then the built-in List of Accounts / Stock Items report before
                    // accepting an empty inventory Master family.
                    ? await TryReadErp9StockItemMasterSliceAsync(
                        companyName, slice.AccountType, slice.ObjectType, slice.Collection, slice.Fetch,
                        required: slice.ExpectedCount > 0, cancellationToken).ConfigureAwait(false)
                    : slice.ExpectedCount > 0
                        ? await ReadErp9MasterSliceAsync(
                            companyName, slice.AccountType, slice.ObjectType, slice.Collection, slice.Fetch, cancellationToken)
                            .ConfigureAwait(false)
                        : await TryReadErp9OptionalMasterSliceAsync(
                            companyName, slice.Label, slice.ObjectType, slice.Collection, slice.Fetch, cancellationToken)
                            .ConfigureAwait(false);
                if (response.Length > 0) supportingMasterResponses.Add(response);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A normal ERP 9 busy/timeout must not destroy a previously verified live
            // authority snapshot. However, after AAH has just created/altered a Master,
            // a fresh read is mandatory so the newly changed Master is truly verified.
            if (!requiresRefresh && staleVerified.Length > 0)
            {
                lock (_erp9CoreCacheGate)
                {
                    _erp9LastMasterReadWarning =
                        $"ERP 9 Master refresh was temporarily unavailable; retained the last verified Ledger/Group snapshot. {exception.Message}";
                }
                RecordErp9Diagnostic("Masters-StaleVerifiedFallback", string.Empty, string.Empty, exception.Message);
                return staleVerified;
            }

            throw;
        }

        var combined = new StringBuilder();
        combined.Append("<AAHERP9CORE>");
        combined.Append(RemoveXmlDeclaration(CleanXml(ledgerResponse)));
        combined.Append(RemoveXmlDeclaration(CleanXml(groupResponse)));
        foreach (var supporting in supportingMasterResponses)
            combined.Append(RemoveXmlDeclaration(CleanXml(supporting)));
        combined.Append("</AAHERP9CORE>");
        var cleaned = combined.ToString();

        var document = XDocument.Parse(cleaned, LoadOptions.None);
        var ledgerCount = document.Descendants().Count(element =>
            element.Name.LocalName.Equals("LEDGER", StringComparison.OrdinalIgnoreCase) &&
            ReadMasterName(element).Length > 0);
        var groupCount = document.Descendants().Count(element =>
            element.Name.LocalName.Equals("GROUP", StringComparison.OrdinalIgnoreCase) &&
            ReadMasterName(element).Length > 0);
        if (ledgerCount == 0 || groupCount == 0)
        {
            throw new InvalidOperationException(
                $"Tally.ERP 9 native Master read was incomplete (Ledgers={ledgerCount:N0}, Groups={groupCount:N0}). " +
                "AAH retained the last verified Ledger/Group authority without archiving missing objects.");
        }

        lock (_erp9CoreCacheGate)
        {
            _erp9CoreCacheCompany = companyName;
            _erp9CoreCacheXml = cleaned;
            _erp9CoreCacheUtc = DateTime.UtcNow;
            _erp9CoreCacheRequiresRefresh = false;
            _erp9LastMasterReadWarning = string.Empty;
        }
        return cleaned;
    }

    private async Task<string> TryReadErp9StockItemMasterSliceAsync(
        string companyName,
        string accountType,
        string objectType,
        string collectionName,
        string fetchList,
        bool required,
        CancellationToken cancellationToken)
    {
        // Fix125: ERP 9 Stock Item authority has its own fail-closed two-stage reader.
        // $$NumStockItems can be zero/unknown even when inventory exists, so a failed
        // native Collection must never be reinterpreted as "the company has no stock".
        // A zero-item authority is accepted only when Tally returned a structurally
        // successful empty response from one of the two supported Stock Item requests.
        var errors = new List<string>();
        var provedEmpty = false;

        try
        {
            var request = BuildCollectionRequest(companyName, collectionName, objectType, fetchList);
            var response = await PostErp9TraceAsync(
                "Masters-StockItems-Collection", request, cancellationToken).ConfigureAwait(false);
            var cleaned = CleanXml(response);
            if (ContainsReadableErp9MasterSlice(cleaned, objectType, requireOpeningBalance: false))
                return cleaned;
            if (IsSuccessfulEmptyErp9MasterSlice(cleaned))
                provedEmpty = true;
            else
                errors.Add("native StockItem Collection returned an unreadable response.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            errors.Add($"native StockItem Collection: {exception.Message}");
        }

        try
        {
            // Official ERP 9 compatibility fallback: Gateway of Tally -> Display ->
            // List of Accounts can export Type of Masters = Stock Items in XML.
            var request = BuildErp9ListOfAccountsRequest(companyName, accountType);
            var response = await PostErp9TraceAsync(
                "Masters-StockItems-ListOfAccounts", request, cancellationToken).ConfigureAwait(false);
            var cleaned = CleanXml(response);
            if (ContainsReadableErp9MasterSlice(cleaned, objectType, requireOpeningBalance: false))
                return cleaned;
            if (IsSuccessfulEmptyErp9MasterSlice(cleaned))
                provedEmpty = true;
            else
                errors.Add("List of Accounts / Stock Items returned an unreadable response.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            errors.Add($"List of Accounts / Stock Items: {exception.Message}");
        }

        if (provedEmpty && !required)
        {
            RecordErp9Diagnostic("Masters-StockItems-ProvenEmpty", string.Empty, string.Empty,
                "ERP 9 returned a valid empty Stock Item authority response.");
            // The marker is deliberately harmless to the normal Master parser but proves
            // that zero Stock Items came from a successful Stock Item authority request.
            return "<AAHERP9STOCKAUTHORITY PROVENEMPTY=\"Yes\" />";
        }

        var detail = errors.Count == 0
            ? "Neither StockItem Collection nor List of Accounts proved Stock Item authority."
            : string.Join(" | ", errors);
        RecordErp9Diagnostic("Masters-StockItems-Unproven", string.Empty, string.Empty, detail);
        throw new InvalidOperationException(
            "Tally.ERP 9 Stock Item authority could not be proven. " + detail +
            " AAH kept the previous Stock Master checkpoint and will retry.");
    }

    private static bool IsSuccessfulEmptyErp9MasterSlice(string xml)
    {
        try
        {
            var document = XDocument.Parse(xml, LoadOptions.None);
            var status = document.Descendants().FirstOrDefault(element =>
                element.Name.LocalName.Equals("STATUS", StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? string.Empty;
            if (status == "0" || status == "-1") return false;

            if (document.Descendants().Any(element =>
                    element.Name.LocalName.Equals("LINEERROR", StringComparison.OrdinalIgnoreCase) &&
                    CleanText(element.Value).Length > 0))
                return false;

            var diagnosticText = string.Join(" ", document.Descendants()
                .Where(element => element.Name.LocalName.Equals("RESPONSE", StringComparison.OrdinalIgnoreCase) ||
                                  element.Name.LocalName.Equals("DESC", StringComparison.OrdinalIgnoreCase))
                .Select(element => CleanText(element.Value)))
                .ToUpperInvariant();
            if (diagnosticText.Contains("UNKNOWN REQUEST", StringComparison.Ordinal) ||
                diagnosticText.Contains("CANNOT BE PROCESSED", StringComparison.Ordinal) ||
                diagnosticText.Contains("COULD NOT BE PROCESSED", StringComparison.Ordinal))
                return false;

            // Require the normal XML export envelope. A random/partial body is not proof
            // that the selected ERP 9 company genuinely contains zero Stock Items.
            return document.Root is not null &&
                   document.Root.Name.LocalName.Equals("ENVELOPE", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> TryReadErp9OptionalMasterSliceAsync(
        string companyName,
        string label,
        string objectType,
        string collectionName,
        string fetchList,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = BuildCollectionRequest(companyName, collectionName, objectType, fetchList);
            var response = await PostErp9TraceAsync(
                $"Masters-{label}-Collection", request, cancellationToken).ConfigureAwait(false);
            var cleaned = CleanXml(response);
            if (ContainsReadableErp9MasterSlice(cleaned, objectType, requireOpeningBalance: false))
                return cleaned;

            // An empty optional Master family is valid (for example no Stock Category
            // or no Cost Centre). Never fail the whole ERP 9 baseline for that case.
            return string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecordErp9Diagnostic($"Masters-{label}-Optional", string.Empty, string.Empty, exception.Message);
            return string.Empty;
        }
    }

    private async Task<string> ReadErp9MasterSliceAsync(
        string companyName,
        string accountType,
        string objectType,
        string collectionName,
        string fetchList,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();

        // Primary path: this is the documented ERP 9 Collection XML contract and
        // returns the native methods AAH actually needs (including OpeningBalance).
        // This is intentionally the same small Ledger/Group shape that older working
        // AAH builds used, without the later Prime/report cascade around it.
        try
        {
            var request = BuildCollectionRequest(companyName, collectionName, objectType, fetchList);
            var response = await PostErp9TraceAsync(
                $"Masters-{accountType}-Collection",
                request,
                cancellationToken).ConfigureAwait(false);
            var cleaned = CleanXml(response);
            if (ContainsReadableErp9MasterSlice(cleaned, objectType, requireOpeningBalance: objectType.Equals("Ledger", StringComparison.OrdinalIgnoreCase))) return cleaned;
            errors.Add($"native Collection returned no readable {objectType} objects.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            errors.Add($"native Collection: {exception.Message}");
        }

        // Compatibility fallback: List of Accounts is a built-in Tally report for
        // Masters. It is never used in addition to a successful Collection response.
        try
        {
            var request = BuildErp9ListOfAccountsRequest(companyName, accountType);
            var response = await PostErp9TraceAsync(
                $"Masters-{accountType}-ListOfAccounts",
                request,
                cancellationToken).ConfigureAwait(false);
            var cleaned = CleanXml(response);
            if (ContainsReadableErp9MasterSlice(cleaned, objectType, requireOpeningBalance: objectType.Equals("Ledger", StringComparison.OrdinalIgnoreCase))) return cleaned;
            errors.Add($"List of Accounts returned no readable {objectType} objects.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            errors.Add($"List of Accounts: {exception.Message}");
        }

        throw new InvalidOperationException(
            $"Tally.ERP 9 did not return the required {objectType} authority. " + string.Join(" | ", errors));
    }

    private static bool ContainsReadableErp9MasterSlice(
        string xml,
        string objectType,
        bool requireOpeningBalance)
    {
        try
        {
            var document = XDocument.Parse(xml, LoadOptions.None);
            var masters = document.Descendants()
                .Where(element =>
                    element.Name.LocalName.Equals(objectType, StringComparison.OrdinalIgnoreCase) &&
                    ReadMasterName(element).Length > 0)
                .ToArray();
            if (masters.Length == 0) return false;
            if (!requireOpeningBalance) return true;

            // A Ledger fallback without the requested OpeningBalance method is not a
            // complete accounting-authority response. Reject it rather than letting
            // missing fields be interpreted as zero and overwrite verified openings.
            return masters.All(ledger => ledger.Elements().Any(child =>
                child.Name.LocalName.Equals("OPENINGBALANCE", StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }

    private static string ReadMasterName(XElement element) =>
        CleanText(
            element.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase))?.Value
            ?? element.Elements().FirstOrDefault(child =>
                child.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase))?.Value);

    /// <summary>
    /// Explicit export safety barrier for Tally.ERP 9. Marks the retained Ledger/Group
    /// snapshot stale so the next Master read must come from the currently linked ERP 9
    /// company. The request itself remains serialized by TallyRequestGate and, per Fix110
    /// R4, has no AAH elapsed-time deadline.
    /// </summary>
    public void RequireFreshErp9MasterAuthorityForExplicitExport()
    {
        if (!ShouldUseErp9CompatibilityProbe()) return;
        InvalidateErp9CoreMastersCache();
    }

    /// <summary>
    /// Fix125: force a fresh ERP 9 core-Master read when the independent Stock Master
    /// checkpoint is missing. This prevents the 15-second Ledger/Group cache from
    /// satisfying a Stock-authority repair cycle with an older no-stock payload.
    /// </summary>
    public void RequireFreshErp9MasterAuthorityForStockBaseline()
    {
        if (!ShouldUseErp9CompatibilityProbe()) return;
        InvalidateErp9CoreMastersCache();
    }

    public IDisposable BeginErp9CoreSnapshotScope(TallyCompanyInfo company)
    {
        ArgumentNullException.ThrowIfNull(company);
        if (!ShouldUseErp9CompatibilityProbe()) return NoopDisposable.Instance;

        lock (_erp9CoreCacheGate)
        {
            _erp9CoreSnapshotPins++;
        }
        return new CallbackDisposable(() =>
        {
            lock (_erp9CoreCacheGate)
            {
                if (_erp9CoreSnapshotPins > 0) _erp9CoreSnapshotPins--;
            }
        });
    }

    public IReadOnlyDictionary<string, string> GetErp9DiagnosticArtifacts()
    {
        lock (_erp9TraceGate)
        {
            return new Dictionary<string, string>(_erp9DiagnosticArtifacts, StringComparer.OrdinalIgnoreCase);
        }
    }


    public void ResetErp9DiagnosticArtifacts()
    {
        lock (_erp9TraceGate)
        {
            _erp9DiagnosticArtifacts.Clear();
        }
    }

    private async Task<string> PostErp9TraceAsync(
        string label,
        string request,
        CancellationToken cancellationToken)
    {
        try
        {
            // Fix110 R4: once an ERP 9 XML request is sent, AAH waits for Tally itself to
            // return success or an actual HTTP/Tally failure. There is no AAH time deadline.
            var response = await PostAsync(request, cancellationToken, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            RecordErp9Diagnostic(label, request, response, string.Empty);
            return response;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecordErp9Diagnostic(label, request, string.Empty, exception.Message);
            throw;
        }
    }

    private void RecordErp9Diagnostic(string label, string request, string response, string error)
    {
        var safe = Regex.Replace(label, "[^A-Za-z0-9._-]+", "-").Trim('-');
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        lock (_erp9TraceGate)
        {
            _erp9DiagnosticArtifacts[$"ERP9-{safe}-Request.xml"] = request;
            if (response.Length > 0)
                _erp9DiagnosticArtifacts[$"ERP9-{safe}-Response.xml"] = response;
            if (error.Length > 0)
                _erp9DiagnosticArtifacts[$"ERP9-{safe}-Error.txt"] = $"{stamp}{Environment.NewLine}{error}";
        }
    }

    private void InvalidateErp9CoreMastersCache()
    {
        lock (_erp9CoreCacheGate)
        {
            // Keep the last verified payload for diagnostics/recovery, but force the
            // next read to go live. A Master CREATE/ALTER must never be verified from
            // the stale payload retained before that mutation.
            _erp9CoreCacheUtc = DateTime.MinValue;
            _erp9CoreCacheRequiresRefresh = true;
            _erp9LastMasterReadWarning = string.Empty;
        }
    }

    private static bool ContainsErp9MasterMutation(string xml) =>
        Regex.IsMatch(
            xml ?? string.Empty,
            @"<(LEDGER|GROUP|STOCKITEM|STOCKGROUP|STOCKCATEGORY|UNIT|GODOWN|VOUCHERTYPE|COSTCATEGORY|COSTCENTRE|TAXCLASSIFICATION|TAXUNIT)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private async Task TryAddResponseAsync(
        ICollection<string> responses,
        string request,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        try
        {
            var response = await PostAsync(request, cancellationToken, requestTimeout);
            if (string.IsNullOrWhiteSpace(response) ||
                response.Contains("<LINEERROR>", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            responses.Add(response);
        }
        catch
        {
            // Individual optional collections may not be available in every
            // Tally release or company configuration. The remaining collections
            // still form a valid live master snapshot.
        }
    }

    // Read/export and write/import are deliberately separate logical transport paths.
    // They still share Tally's single HTTP endpoint/gate so requests never collide, but
    // connection health and UI status can now distinguish a long legitimate import from
    // an unavailable Tally process.
    private async Task<string> PostAsync(
        string xml,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null) =>
        await PostExportAsync(xml, cancellationToken, requestTimeout).ConfigureAwait(false);

    private async Task<string> PostExportAsync(
        string xml,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        Interlocked.Increment(ref _activeExportRequests);
        try
        {
            var effectiveTimeout = ShouldUseErp9CompatibilityProbe()
                ? Timeout.InfiniteTimeSpan
                : requestTimeout ?? _defaultRequestTimeout;
            return await PostTransportCoreAsync(
                xml,
                cancellationToken,
                effectiveTimeout,
                isImport: false).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeExportRequests);
        }
    }

    private async Task<string> PostImportAsync(
        string xml,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        Interlocked.Increment(ref _activeImportRequests);
        try
        {
            var effectiveTimeout = ShouldUseErp9CompatibilityProbe()
                ? Timeout.InfiniteTimeSpan
                : requestTimeout ?? TimeSpan.FromMinutes(3);
            return await PostTransportCoreAsync(
                xml,
                cancellationToken,
                effectiveTimeout,
                isImport: true).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeImportRequests);
        }
    }

    private async Task<string> PostTransportCoreAsync(
        string xml,
        CancellationToken cancellationToken,
        TimeSpan requestTimeout,
        bool isImport)
    {
        var safeXml = xml ?? string.Empty;
        var operation = isImport ? "Import" : "Export";

        using var trace = ApplicationPerformanceTrace.Measure(
            $"Tally.HTTP.{operation}",
            $"Chars={safeXml.Length};Request={DescribeRequest(safeXml)}");
        using var content = new StringContent(
            safeXml,
            Encoding.UTF8,
            "text/xml");

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var hasApplicationDeadline = requestTimeout != Timeout.InfiniteTimeSpan;
        if (hasApplicationDeadline)
            requestCancellation.CancelAfter(requestTimeout);
        var requestToken = requestCancellation.Token;

        byte[] bytes;
        System.Net.HttpStatusCode statusCode;
        string? reasonPhrase;
        IDisposable? tallyLease = await TallyRequestGate.AcquireAsync(requestToken).ConfigureAwait(false);
        try
        {
            using var response = await _httpClient.PostAsync(
                _endpoint,
                content,
                requestToken).ConfigureAwait(false);
            bytes = await response.Content.ReadAsByteArrayAsync(requestToken).ConfigureAwait(false);
            statusCode = response.StatusCode;
            reasonPhrase = response.ReasonPhrase;
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested &&
            hasApplicationDeadline &&
            requestCancellation.IsCancellationRequested)
        {
            var message = isImport
                ? $"Tally import did not complete within {requestTimeout.TotalSeconds:0.#} seconds. The connection is not marked offline; verify the import result before retrying."
                : $"Tally {DescribeRequest(safeXml)} did not complete within {requestTimeout.TotalSeconds:0.#} seconds.";
            throw new TimeoutException(message, exception);
        }
        finally
        {
            // Fix37: Tally owns only the network request/response-byte window. Decode,
            // XML sanitizing, SQLite work and UI processing happen after this release.
            tallyLease?.Dispose();
            tallyLease = null;
        }

        var body = TallyXmlSanitizer.Sanitize(DecodeResponse(bytes));
        if ((int)statusCode < 200 || (int)statusCode >= 300)
        {
            throw new InvalidOperationException(
                $"Tally HTTP {(int)statusCode} {reasonPhrase}. {body[..Math.Min(body.Length, 600)]}");
        }

        return body;
    }

    private static string DescribeRequest(string? xml)
    {
        var text = xml ?? string.Empty;
        if (text.Contains("Import Data", StringComparison.OrdinalIgnoreCase)) return "Import Data";
        if (text.Contains("AAH Voucher Index", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("AAH ERP9 Voucher Index", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("AAH ERP9 Voucher Slice", StringComparison.OrdinalIgnoreCase)) return "Voucher Index";
        if (text.Contains("<ID>DayBook</ID>", StringComparison.OrdinalIgnoreCase)) return "ERP 9 Day Book";
        if (text.Contains("<ID>List of Accounts</ID>", StringComparison.OrdinalIgnoreCase)) return "ERP 9 List of Accounts";
        if (text.Contains("All Masters", StringComparison.OrdinalIgnoreCase)) return "All Masters";
        if (text.Contains("<TYPE>COLLECTION</TYPE>", StringComparison.OrdinalIgnoreCase)) return "Collection";
        if (text.Contains("<TYPE>Data</TYPE>", StringComparison.OrdinalIgnoreCase)) return "Report";
        return "Tally Request";
    }

    private static IReadOnlyList<string> ParseDiscoveredCompanyNames(string response)
    {
        var cleaned = CleanXml(response);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var document = XDocument.Parse(cleaned, LoadOptions.None);
            foreach (var element in document.Descendants()
                         .Where(node => LocalName(node) == "COMPANY"))
            {
                var name = CleanText(
                    element.Attribute("NAME")?.Value
                    ?? element.Elements().FirstOrDefault(child => LocalName(child) == "NAME")?.Value);
                if (name.Length > 0 && seen.Add(name))
                {
                    result.Add(name);
                }
            }
        }
        catch
        {
            // Fall back to tolerant pattern matching below.
        }

        if (result.Count > 0)
        {
            return result;
        }

        var patterns = new[]
        {
            "<COMPANY\\b[^>]*\\bNAME\\s*=\\s*\"([^\"]+)\"",
            "<COMPANY\\b[^>]*>\\s*<NAME\\b[^>]*>(.*?)</NAME>"
        };

        foreach (var pattern in patterns)
        {
            foreach (Match match in Regex.Matches(
                         cleaned,
                         pattern,
                         RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var name = CleanText(Regex.Replace(match.Groups[1].Value, "<[^>]+>", string.Empty));
                if (name.Length > 0 && seen.Add(name))
                {
                    result.Add(name);
                }
            }

            if (result.Count > 0)
            {
                break;
            }
        }

        return result;
    }

    private static TallyRuntimeFamily ParseProductFamilyProbe(string response, out string evidence)
    {
        evidence = string.Empty;
        var cleaned = CleanXml(response);
        try
        {
            var document = XDocument.Parse(cleaned, LoadOptions.None);
            var product = document.Descendants()
                .FirstOrDefault(element => LocalName(element) == "TALLYPRODUCT");
            if (product is null) return TallyRuntimeFamily.Unknown;

            var majorText = CleanText(ChildText(product, "MAJOR"));
            var minorText = CleanText(ChildText(product, "MINOR"));
            if (!int.TryParse(majorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ||
                !int.TryParse(minorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor))
                return TallyRuntimeFamily.Unknown;

            evidence = $"Live Tally $$ProdInfo reports {major}.{minor}.";
            if (major == 1 && minor == 0) return TallyRuntimeFamily.Erp9;
            if (major == 1 && minor > 0) return TallyRuntimeFamily.PrimeOrModern;
            return TallyRuntimeFamily.Unknown;
        }
        catch
        {
            return TallyRuntimeFamily.Unknown;
        }
    }

    private static TallyCompanyInfo? ParseCurrentCompanyCompatibilityProbe(string response)
    {
        var cleaned = CleanXml(response);
        try
        {
            var document = XDocument.Parse(cleaned, LoadOptions.None);
            var profile = document.Descendants()
                .FirstOrDefault(element => LocalName(element) == "CURRENTCOMPANY");
            if (profile is null) return null;

            var name = CleanText(ChildText(profile, "NAME"));
            if (name.Length == 0) return null;

            var booksFrom = CleanText(ChildText(profile, "BOOKSFROM"));
            var startingFrom = CleanText(ChildText(profile, "STARTINGFROM"));
            return new TallyCompanyInfo
            {
                Name = name,
                BooksFrom = booksFrom,
                StartingFrom = startingFrom,
                FinancialYear = TallyCompanyInfo.NormalizeFinancialYear(
                    string.Empty,
                    booksFrom.Length > 0 ? booksFrom : startingFrom),
                StateName = CleanText(ChildText(profile, "STATENAME")),
                Status = "Loaded - Tally.ERP 9 compatibility probe"
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task PopulateCompanyBookDatesAsync(
        TallyCompanyInfo company,
        CancellationToken cancellationToken,
        bool useErp9CompatibilityTimeout)
    {
        if (TallyCompanyInfo.ParseTallyDate(company.BooksFrom) is not null &&
            TallyCompanyInfo.ParseTallyDate(company.StartingFrom) is not null)
            return;

        try
        {
            var response = await PostAsync(
                BuildCollectionRequest(
                    company.Name,
                    "AAH Company Book Dates",
                    "Company",
                    "Name,BooksFrom,StartingFrom"),
                cancellationToken,
                useErp9CompatibilityTimeout ? Timeout.InfiniteTimeSpan : null);
            ApplyCompanyBookDates(company, response);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // ERP 9 releases differ in what Company object/collection methods are
            // serialized over HTTP.  A formula-report fallback below is still tried.
        }

        if (TallyCompanyInfo.ParseTallyDate(company.BooksFrom) is not null &&
            TallyCompanyInfo.ParseTallyDate(company.StartingFrom) is not null)
            return;

        try
        {
            var response = await PostAsync(
                BuildCompanyBookDateFormulaRequest(company.Name),
                cancellationToken,
                useErp9CompatibilityTimeout ? Timeout.InfiniteTimeSpan : null);
            ApplyCompanyBookDates(company, response);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Identity validation remains fail-closed when neither native Company
            // collection nor the ERP 9 formula report can return both dates.
        }

        company.FinancialYear = TallyCompanyInfo.NormalizeFinancialYear(
            company.FinancialYear,
            company.BooksFrom.Length > 0 ? company.BooksFrom : company.StartingFrom);
    }

    private static void ApplyCompanyBookDates(TallyCompanyInfo company, string response)
    {
        var cleaned = CleanXml(response);
        var document = XDocument.Parse(cleaned, LoadOptions.None);
        var candidates = document.Descendants()
            .Where(element => LocalName(element) == "COMPANY" ||
                              LocalName(element) == "COMPANYPROFILE")
            .ToArray();

        var selected = candidates.FirstOrDefault(element =>
        {
            var name = CleanText(element.Attribute("NAME")?.Value ?? ChildText(element, "NAME"));
            return name.Equals(company.Name, StringComparison.OrdinalIgnoreCase);
        }) ?? candidates.FirstOrDefault();

        string FindValue(string fieldName)
        {
            var fromSelected = selected is null ? string.Empty : ChildText(selected, fieldName);
            if (fromSelected.Length > 0) return fromSelected;
            return CleanText(document.Descendants()
                .FirstOrDefault(element => LocalName(element) == fieldName)?.Value ?? string.Empty);
        }

        var books = FindValue("BOOKSFROM");
        var starting = FindValue("STARTINGFROM");
        if (TallyCompanyInfo.ParseTallyDate(books) is not null) company.BooksFrom = books;
        if (TallyCompanyInfo.ParseTallyDate(starting) is not null) company.StartingFrom = starting;
    }

    private static TallyCompanyInfo ParseCompanyObject(
        string requestedName,
        string response)
    {
        var cleaned = CleanXml(response);

        try
        {
            var document = XDocument.Parse(cleaned, LoadOptions.None);
            var companyElement = document.Descendants()
                .FirstOrDefault(node => LocalName(node) == "COMPANY");

            if (companyElement is null)
            {
                throw new InvalidOperationException(
                    "The company object was not returned by Tally.");
            }

            var returnedName = CleanText(
                companyElement.Attribute("NAME")?.Value
                ?? ChildText(companyElement, "NAME"));
            var booksFrom = ChildText(companyElement, "BOOKSFROM");
            var startingFrom = ChildText(companyElement, "STARTINGFROM");
            var gstin = ChildText(
                companyElement,
                "GSTREGNUMBER",
                "GSTIN",
                "GSTREGISTRATIONNUMBER");

            return new TallyCompanyInfo
            {
                Name = returnedName.Length > 0 ? returnedName : requestedName,
                CompanyNumber = ChildText(companyElement, "COMPANYNUMBER"),
                Gstin = TallyCompanyInfo.NormalizeGstin(gstin),
                BooksFrom = booksFrom,
                StartingFrom = startingFrom,
                FinancialYear = TallyCompanyInfo.NormalizeFinancialYear(
                    string.Empty,
                    booksFrom.Length > 0 ? booksFrom : startingFrom),
                StateName = ChildText(companyElement, "STATENAME"),
                Status = returnedName.Equals(requestedName, StringComparison.OrdinalIgnoreCase)
                    ? "Loaded - verified"
                    : "Loaded - name differs"
            };
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Tally returned company details that could not be read.",
                exception);
        }
    }


    private static IReadOnlyList<TallyVoucherXmlSnapshot> ParseVoucherXmlSnapshots(
        string response,
        IReadOnlyDictionary<string, string>? masterIdByGuid = null,
        string? exactRequestedMasterId = null)
    {
        var cleaned = CleanXml(response);
        IReadOnlyList<XElement> vouchers;
        try
        {
            var document = XDocument.Parse(cleaned, LoadOptions.PreserveWhitespace);
            vouchers = SelectExportedVoucherObjects(document)
                .Select(element => new XElement(element))
                .ToArray();
        }
        catch (Exception documentException)
        {
            try
            {
                var fragments = Regex.Matches(
                        cleaned,
                        @"<VOUCHER\b[^>]*>.*?</VOUCHER>",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)
                    .Cast<Match>()
                    .Select(match => match.Value)
                    .ToArray();
                var recovered = new List<XElement>();
                foreach (var fragment in fragments)
                    recovered.Add(ParseSingleVoucherElement(fragment));
                vouchers = recovered;
            }
            catch (Exception fragmentException)
            {
                throw new InvalidOperationException(
                    "Tally returned full Voucher XML that could not be read safely for Master Merge. " +
                    $"XML parser: {documentException.Message} Fragment recovery: {fragmentException.Message}",
                    documentException);
            }
        }

        // Fix67 belt-and-suspenders guard: fragment recovery bypasses the normal
        // BODY/DATA/COLLECTION selector, so remove scalar Voucher count markers here too.
        vouchers = vouchers
            .Where(voucher => !IsStructuralVoucherCollectionMarker(voucher))
            .ToArray();

        var exactId = (exactRequestedMasterId ?? string.Empty).Trim();
        if (exactId.Length > 0 && vouchers.Count != 1)
            throw new InvalidOperationException(
                $"Exact Tally Voucher MASTERID {exactId} returned {vouchers.Count} full Voucher object(s); no Voucher was altered.");

        var result = new List<TallyVoucherXmlSnapshot>();
        foreach (var voucher in vouchers)
        {
            // Fix48/Fix66: collections explicitly export MasterID. Some Tally builds
            // can omit this native identity from a FETCH * serialization even though an
            // exact MASTERID+GUID authority map is already available (from the same
            // Fix66 response or an inherited exact-index path). Preserve only that exact
            // identity by GUID; never guess from voucher number/date. TAGVALUE is accepted
            // only when the Voucher itself explicitly says TAGNAME=MASTER ID.
            var guid = DirectChildText(voucher, "GUID").Trim();
            var masterId = DirectChildText(voucher, "MASTERID").Trim();
            if (masterId.Length == 0 &&
                string.Equals(voucher.Attribute("TAGNAME")?.Value?.Trim(), "MASTER ID", StringComparison.OrdinalIgnoreCase))
                masterId = voucher.Attribute("TAGVALUE")?.Value?.Trim() ?? string.Empty;
            if (masterId.Length == 0 && guid.Length > 0 && masterIdByGuid is not null)
                masterIdByGuid.TryGetValue(guid, out masterId);
            masterId = (masterId ?? string.Empty).Trim();

            // Fix50 exact-filter carry-forward: when this response came from a request
            // containing one and only one numeric $MasterID filter, that requested ID is
            // already authoritative. Tally may omit MASTERID/GUID from the exported full
            // object, but it cannot make AAH forget the exact ID used to select it.
            if (masterId.Length == 0 && exactId.Length > 0)
                masterId = exactId;
            if (masterId.Length == 0)
                throw new InvalidOperationException("Tally did not export Voucher MASTERID in the merge object response and no exact live Voucher-Index MASTERID could be recovered by GUID; no Voucher was altered.");
            if (exactId.Length > 0 && !string.Equals(masterId, exactId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Exact Tally Voucher request for MASTERID {exactId} returned explicit MASTERID {masterId}; no Voucher was altered.");
            if (exactId.Length > 0 && guid.Length > 0 && masterIdByGuid is not null &&
                masterIdByGuid.TryGetValue(guid, out var cachedGuidMasterId) &&
                !string.Equals(cachedGuidMasterId, exactId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Exact Tally Voucher request for MASTERID {exactId} returned GUID '{guid}' belonging to cached MASTERID {cachedGuidMasterId}; no Voucher was altered.");

            var voucherType = DirectChildText(voucher, "VOUCHERTYPENAME").Trim();
            var date = TallyCompanyInfo.ParseTallyDate(DirectChildText(voucher, "DATE"));
            result.Add(new TallyVoucherXmlSnapshot(
                masterId,
                guid,
                voucherType,
                date,
                voucher.ToString(SaveOptions.DisableFormatting))
            {
                AlterId = DirectChildText(voucher, "ALTERID").Trim()
            });
        }
        return result;
    }

    private static XElement ParseSingleVoucherElement(string xml)
    {
        var wrapped = $"<ROOT xmlns:UDF=\"TallyUDF\">{xml}</ROOT>";
        var document = XDocument.Parse(wrapped, LoadOptions.PreserveWhitespace);
        return document.Root?.Elements().FirstOrDefault(element => LocalName(element) == "VOUCHER") is { } voucher
            ? new XElement(voucher)
            : throw new InvalidOperationException("Voucher XML snapshot does not contain a Voucher object.");
    }

    private static bool VoucherXmlUsesLedger(XElement voucher, string ledgerName) =>
        voucher.DescendantsAndSelf().Any(element =>
            IsVoucherLedgerIdentityElement(element) &&
            TallyMasterIdentity.Equivalent(element.Value, ledgerName));

    private static int ReplaceVoucherLedgerIdentity(XElement voucher, string sourceLedgerName, string targetLedgerName)
    {
        var replacements = 0;
        foreach (var element in voucher.DescendantsAndSelf().Where(IsVoucherLedgerIdentityElement))
        {
            if (!TallyMasterIdentity.Equivalent(element.Value, sourceLedgerName)) continue;
            element.Value = targetLedgerName;
            replacements++;
        }
        return replacements;
    }

    private static bool IsVoucherLedgerIdentityElement(XElement element)
    {
        var name = NormalizeName(element.Name.LocalName);
        if (name == "LEDGERNAME") return true;
        if (name is not ("PARTYLEDGERNAME" or "PARTYNAME" or "BASICBASEPARTYNAME")) return false;
        return element.Parent is not null && LocalName(element.Parent) == "VOUCHER";
    }

    private static void PrepareVoucherForAlter(XElement voucher, TallyVoucherXmlSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.MasterId))
            throw new InvalidOperationException("Voucher merge snapshot has no MASTER ID.");
        if (string.IsNullOrWhiteSpace(snapshot.VoucherType))
            throw new InvalidOperationException($"Voucher MASTERID {snapshot.MasterId} has no Voucher Type; it cannot be altered safely.");
        if (snapshot.VoucherDate is not DateTime date)
            throw new InvalidOperationException($"Voucher MASTERID {snapshot.MasterId} has no readable Voucher Date; Tally alteration requires the original date.");

        foreach (var attribute in voucher.Attributes().Where(attribute =>
                     attribute.Name.LocalName.Equals("ACTION", StringComparison.OrdinalIgnoreCase) ||
                     attribute.Name.LocalName.Equals("TAGNAME", StringComparison.OrdinalIgnoreCase) ||
                     attribute.Name.LocalName.Equals("TAGVALUE", StringComparison.OrdinalIgnoreCase) ||
                     attribute.Name.LocalName.Equals("VCHTYPE", StringComparison.OrdinalIgnoreCase) ||
                     attribute.Name.LocalName.Equals("DATE", StringComparison.OrdinalIgnoreCase)).ToArray())
            attribute.Remove();

        // Tally's documented Voucher alteration identity is DATE + VCHTYPE plus a
        // TAGNAME/TAGVALUE identifier. MASTER ID keeps this independent of voucher-number
        // renumbering and duplicate voucher numbers.
        voucher.SetAttributeValue("ACTION", "Alter");
        voucher.SetAttributeValue("TAGNAME", "MASTER ID");
        voucher.SetAttributeValue("TAGVALUE", snapshot.MasterId);
        voucher.SetAttributeValue("VCHTYPE", snapshot.VoucherType);
        voucher.SetAttributeValue("DATE", date.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture));

        // Fix52: use the same ERP 9-safe identity shape already proven by Inter-Bank
        // reconciliation. Tally requires the Voucher DATE as a direct body field as
        // well as the alteration identity attribute. Some full-object exports omit that
        // direct DATE when serialized for cache, so always restore it from the indexed
        // snapshot before sending the ALTER. Preserve every other cached Voucher field.
        SetOrAddDirectVoucherField(voucher, "DATE", date.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        SetOrAddDirectVoucherField(voucher, "VOUCHERTYPENAME", snapshot.VoucherType);
    }

    private static void SetOrAddDirectVoucherField(XElement voucher, string localName, string value)
    {
        var element = voucher.Elements().FirstOrDefault(item =>
            item.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));
        if (element is null) voucher.AddFirst(new XElement(localName, value));
        else element.Value = value;
    }

    private static void AccumulateImportResponse(TallyImportResponse total, TallyImportResponse batch)
    {
        total.Created += batch.Created;
        total.Altered += batch.Altered;
        total.Deleted += batch.Deleted;
        total.Ignored += batch.Ignored;
        total.Exceptions += batch.Exceptions;
        total.Errors += batch.Errors;
        if (!string.IsNullOrWhiteSpace(batch.LastVoucherId)) total.LastVoucherId = batch.LastVoucherId;
        if (!string.IsNullOrWhiteSpace(batch.LastMasterId)) total.LastMasterId = batch.LastMasterId;
        if (!string.IsNullOrWhiteSpace(batch.LineError)) total.LineError = batch.LineError;
    }

    private static StringBuilder BeginVoucherSnapshotImport(string companyName)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<ENVELOPE>");
        builder.AppendLine("  <HEADER><VERSION>1</VERSION><TALLYREQUEST>Import</TALLYREQUEST><TYPE>Data</TYPE><ID>Vouchers</ID></HEADER>");
        builder.AppendLine("  <BODY><DESC><STATICVARIABLES>");
        builder.Append("    <SVCURRENTCOMPANY>").Append(XmlEscape(companyName)).AppendLine("</SVCURRENTCOMPANY>");
        builder.AppendLine("  </STATICVARIABLES></DESC><DATA>");
        return builder;
    }

    private static void AppendVoucherSnapshot(StringBuilder builder, XElement voucher)
    {
        builder.AppendLine("    <TALLYMESSAGE xmlns:UDF=\"TallyUDF\">");
        builder.AppendLine(voucher.ToString(SaveOptions.DisableFormatting));
        builder.AppendLine("    </TALLYMESSAGE>");
    }

    private static void EndVoucherSnapshotImport(StringBuilder builder) =>
        builder.AppendLine("  </DATA></BODY></ENVELOPE>");

    private static bool IsStructuralVoucherCollectionMarker(XElement element)
    {
        // Fix67: Tally may emit a scalar CMPINFO/collection count such as
        // <VOUCHER>3989</VOUCHER>. It is metadata, not an accounting Voucher object.
        // A real exported Voucher carries child fields (MASTERID/GUID/DATE/etc.) or
        // explicit identity attributes. Never let the scalar count enter full-Voucher
        // identity validation or trigger the old Fix48 missing-MASTERID recovery path.
        if (LocalName(element) != "VOUCHER" || element.HasElements || element.HasAttributes)
            return false;

        var scalar = (element.Value ?? string.Empty).Trim();
        return scalar.Length == 0 || scalar.All(char.IsDigit);
    }

    private static IReadOnlyList<XElement> SelectExportedVoucherObjects(XDocument document)
    {
        // Tally can serialize supporting/nested VOUCHER elements and scalar Voucher
        // count markers inside a response. Only real accounting Voucher objects exported
        // by BODY/DATA/COLLECTION are authority. Counting every descendant caused Fix50's
        // exact MASTERID read to see the same logical Voucher twice; accepting scalar
        // count markers caused the misleading missing-MASTERID failure corrected by Fix67.
        var dataCollection = document.Descendants().FirstOrDefault(element =>
            LocalName(element) == "COLLECTION" &&
            element.Parent is not null && LocalName(element.Parent) == "DATA");
        var direct = dataCollection?.Elements()
            .Where(element => LocalName(element) == "VOUCHER" &&
                              !IsStructuralVoucherCollectionMarker(element))
            .ToArray() ?? Array.Empty<XElement>();
        if (direct.Length > 0) return direct;

        return document.Descendants()
            .Where(element => LocalName(element) == "VOUCHER" &&
                              !IsStructuralVoucherCollectionMarker(element) &&
                              !element.Ancestors().Any(ancestor => LocalName(ancestor) == "VOUCHER"))
            .ToArray();
    }

    private static IReadOnlyList<TallyVoucherIndexRecord> ParseVoucherIndex(
        string response,
        string companyKey,
        string financialYear)
    {
        var cleaned = CleanXml(response);

        try
        {
            var document = XDocument.Parse(cleaned, LoadOptions.None);
            return ParseVoucherElements(
                SelectExportedVoucherObjects(document),
                companyKey,
                financialYear);
        }
        catch (Exception documentException)
        {
            // ERP 9 can occasionally return a damaged outer ENVELOPE while the
            // individual VOUCHER fragments remain complete. Recover only when every
            // VOUCHER opening tag has a complete closing fragment and every fragment
            // parses. Never accept a partial live index for duplicate authority.
            try
            {
                var openingCount = Regex.Matches(
                    cleaned,
                    "<VOUCHER\\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;
                var fragments = Regex.Matches(
                        cleaned,
                        "<VOUCHER\\b[^>]*>.*?</VOUCHER>",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)
                    .Cast<Match>()
                    .Select(match => match.Value)
                    .ToArray();

                if (openingCount == 0 || fragments.Length != openingCount)
                {
                    throw new InvalidOperationException(
                        $"The response contained {openingCount} Voucher opening tag(s) but only {fragments.Length} complete Voucher fragment(s).");
                }

                var voucherElements = new List<XElement>(fragments.Length);
                foreach (var fragment in fragments)
                {
                    var wrapped = $"<ROOT xmlns:UDF=\"TallyUDF\">{fragment}</ROOT>";
                    var fragmentDocument = XDocument.Parse(wrapped, LoadOptions.None);
                    var voucher = fragmentDocument.Root?.Elements()
                        .FirstOrDefault(node => LocalName(node) == "VOUCHER");
                    if (voucher is null)
                    {
                        throw new InvalidOperationException("A recovered Voucher fragment did not contain a Voucher element.");
                    }
                    voucherElements.Add(voucher);
                }

                return ParseVoucherElements(voucherElements, companyKey, financialYear);
            }
            catch (Exception fragmentException)
            {
                throw new InvalidOperationException(
                    "Tally returned a voucher report that could not be read safely. Duplicate checking was not continued. " +
                    $"XML parser: {documentException.Message} Fragment recovery: {fragmentException.Message}",
                    documentException);
            }
        }
    }

    private static IReadOnlyList<TallyVoucherIndexRecord> ParseVoucherElements(
        IEnumerable<XElement> voucherElements,
        string companyKey,
        string financialYear)
    {
        var result = new List<TallyVoucherIndexRecord>();
        foreach (var element in voucherElements)
        {
            var ledgerPostings = ReadVoucherLedgerPostings(element);
            var ledgerSides = ReadVoucherLedgerSides(element);
            var record = new TallyVoucherIndexRecord
            {
                CompanyKey = companyKey,
                FinancialYear = financialYear,
                MasterId = ChildText(element, "MASTERID"),
                AlterId = ChildText(element, "ALTERID"),
                Guid = ChildText(element, "GUID"),
                VoucherDate = TallyCompanyInfo.ParseTallyDate(ChildText(element, "DATE")),
                VoucherType = ChildText(element, "VOUCHERTYPENAME"),
                VoucherNumber = ChildText(element, "VOUCHERNUMBER"),
                Reference = ChildText(element, "REFERENCE"),
                PartyLedger = RawVoucherLedgerText(element, "PARTYLEDGERNAME", "PARTYNAME"),
                DebitLedgers = ledgerSides.DebitLedgers,
                CreditLedgers = ledgerSides.CreditLedgers,
                Amount = ReadVoucherAmount(element),
                Narration = ReadVoucherNarration(element),
                LedgerPostings = ledgerPostings,
                LastSeenUtc = DateTime.UtcNow
            };

            // Some empty Tally companies return a structural/placeholder VOUCHER
            // element with no voucher identity at all. Do not turn that XML shell
            // into a fake live-voucher row in duplicate checking or diagnostics.
            if (record.VoucherDate is null &&
                record.VoucherType.Length == 0 &&
                record.VoucherNumber.Length == 0 &&
                record.Reference.Length == 0 &&
                record.PartyLedger.Length == 0 &&
                record.MasterId.Length == 0 &&
                record.AlterId.Length == 0 &&
                record.Guid.Length == 0 &&
                record.Amount == 0m &&
                record.Narration.Length == 0)
            {
                continue;
            }

            record.ContentHash = TallyVoucherFingerprint.BuildContentHash(
                record.VoucherDate,
                record.VoucherType,
                record.Reference,
                record.PartyLedger,
                record.Amount,
                record.Narration);
            result.Add(record);
        }

        return result;
    }

    // Preserve exact Tally Ledger names for Voucher identity (including CR/LF).
    // The general ChildText cleans whitespace and must not be used for identities.
    private static string RawVoucherLedgerText(XElement element, params string[] names)
    {
        var wanted = names.Select(NormalizeName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var raw = element.Elements().FirstOrDefault(child =>
            wanted.Contains(NormalizeName(child.Name.LocalName))) ??
            element.Descendants().FirstOrDefault(child =>
                wanted.Contains(NormalizeName(child.Name.LocalName)));
        return raw?.Value ?? string.Empty;
    }

    private static IReadOnlyList<TallyLedgerPostingRecord> ReadVoucherLedgerPostings(XElement voucher)
    {
        var result = new List<TallyLedgerPostingRecord>();
        var sequence = 0;
        foreach (var entry in VoucherLedgerEntryElements(voucher))
        {
            sequence++;
            var ledger = RawVoucherLedgerText(entry, "LEDGERNAME");
            if (ledger.Length == 0) continue;

            var signedAmount = ParseDecimal(DirectChildText(entry, "AMOUNT"));
            if (signedAmount == 0m) continue;

            var deemedPositive = DirectChildText(entry, "ISDEEMEDPOSITIVE").Trim();
            var isDebit = deemedPositive.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
                          (deemedPositive.Length == 0 && signedAmount < 0m);
            var narration = string.Empty;
            foreach (var name in new[] { "NARRATION", "BASICNARRATION", "VOUCHERNARRATION", "BASICUSERDESCRIPTION" })
            {
                narration = DirectChildText(entry, name).Trim();
                if (narration.Length > 0) break;
            }

            result.Add(new TallyLedgerPostingRecord
            {
                Sequence = sequence,
                LedgerName = ledger,
                SignedAmount = signedAmount,
                IsDebit = isDebit,
                Narration = narration
            });
        }
        return result;
    }

    private static (string DebitLedgers, string CreditLedgers) ReadVoucherLedgerSides(XElement voucher)
    {
        var debit = new List<string>();
        var credit = new List<string>();

        // Tally.ERP 9 and TallyPrime do not always serialize the voucher sub-collection
        // with the same wrapper tag. Treat any descendant that directly owns both a
        // LedgerName and Amount as a Ledger Entry. This covers ALLLEDGERENTRIES.LIST,
        // LEDGERENTRIES.LIST and compatible legacy wrappers without guessing from a
        // descendant amount that belongs to bill/cost-centre allocations.
        foreach (var entry in VoucherLedgerEntryElements(voucher))
        {
            var ledger = RawVoucherLedgerText(entry, "LEDGERNAME");
            if (ledger.Length == 0) continue;

            var amount = ParseDecimal(DirectChildText(entry, "AMOUNT"));
            if (amount == 0m) continue;

            var deemedPositive = DirectChildText(entry, "ISDEEMEDPOSITIVE").Trim();
            var isDebit = deemedPositive.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
                          (deemedPositive.Length == 0 && amount < 0m);
            var target = isDebit ? debit : credit;
            if (!target.Contains(ledger, StringComparer.OrdinalIgnoreCase)) target.Add(ledger);
        }
        return (string.Join(" | ", debit), string.Join(" | ", credit));
    }

    private static decimal ReadVoucherAmount(XElement voucher)
    {
        // Prefer a genuine voucher-level Amount when Tally supplies one.
        var direct = voucher.Elements()
            .Where(element => NormalizeName(element.Name.LocalName) is "AMOUNT" or "AAHAMOUNT" or "VOUCHERAMOUNT")
            .Select(element => ParseDecimal(element.Value))
            .FirstOrDefault(amount => amount != 0m);
        if (direct != 0m) return Math.Abs(direct);

        // ERP 9 commonly exposes the accounting value only on Ledger Entries. Sum
        // the direct ledger-entry amounts by side and use the larger balanced side.
        // VoucherLedgerEntryElements deliberately excludes bill/cost-centre children.
        var ledgerAmounts = VoucherLedgerEntryElements(voucher)
            .Select(entry => ParseDecimal(DirectChildText(entry, "AMOUNT")))
            .Where(amount => amount != 0m)
            .ToArray();
        if (ledgerAmounts.Length == 0) return 0m;

        var debit = ledgerAmounts.Where(amount => amount < 0m).Sum(amount => Math.Abs(amount));
        var credit = ledgerAmounts.Where(amount => amount > 0m).Sum();
        if (debit > 0m || credit > 0m) return Math.Max(debit, credit);

        return ledgerAmounts.Select(Math.Abs).DefaultIfEmpty(0m).Max();
    }

    private static string ReadVoucherNarration(XElement voucher)
    {
        // Prefer voucher-level narration. Some ERP 9 exports expose narration through
        // a legacy/basic tag or only on an individual Ledger Entry, so retain those
        // as safe fallbacks rather than leaving the reconciliation evidence blank.
        foreach (var name in new[] { "NARRATION", "BASICNARRATION", "VOUCHERNARRATION", "BASICUSERDESCRIPTION" })
        {
            var direct = voucher.Elements()
                .FirstOrDefault(element => NormalizeName(element.Name.LocalName).Equals(name, StringComparison.OrdinalIgnoreCase));
            var text = CleanText(direct?.Value);
            if (text.Length > 0) return text;
        }

        var parts = voucher.Descendants()
            .Where(element =>
            {
                var name = NormalizeName(element.Name.LocalName);
                return name.Equals("NARRATION", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("BASICNARRATION", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("VOUCHERNARRATION", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("BASICUSERDESCRIPTION", StringComparison.OrdinalIgnoreCase);
            })
            .Select(element => CleanText(element.Value))
            .Where(text => text.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        return string.Join(" | ", parts);
    }

    private static IEnumerable<XElement> VoucherLedgerEntryElements(XElement voucher) =>
        voucher.Descendants().Where(element =>
            element.Elements().Any(child => NormalizeName(child.Name.LocalName).Equals("LEDGERNAME", StringComparison.OrdinalIgnoreCase)) &&
            element.Elements().Any(child => NormalizeName(child.Name.LocalName).Equals("AMOUNT", StringComparison.OrdinalIgnoreCase)));

    private static string DirectChildText(XElement element, string name)
    {
        var wanted = NormalizeName(name);
        var child = element.Elements().FirstOrDefault(candidate =>
            NormalizeName(candidate.Name.LocalName).Equals(wanted, StringComparison.OrdinalIgnoreCase));
        return child is null ? string.Empty : CleanText(child.Value);
    }

    private static string BuildProductFamilyProbeRequest() =>
        """
        <ENVELOPE>
          <HEADER>
            <VERSION>1</VERSION>
            <TALLYREQUEST>EXPORT</TALLYREQUEST>
            <TYPE>DATA</TYPE>
            <ID>AAH Product Family Probe</ID>
          </HEADER>
          <BODY>
            <DESC>
              <STATICVARIABLES>
                <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
              </STATICVARIABLES>
              <TDL>
                <TDLMESSAGE>
                  <REPORT NAME="AAH Product Family Probe">
                    <FORMS>AAH Product Family Probe Form</FORMS>
                  </REPORT>
                  <FORM NAME="AAH Product Family Probe Form">
                    <PARTS>AAH Product Family Probe Part</PARTS>
                    <XMLTAG>TALLYPRODUCT</XMLTAG>
                  </FORM>
                  <PART NAME="AAH Product Family Probe Part">
                    <LINES>AAH Product Family Probe Line</LINES>
                  </PART>
                  <LINE NAME="AAH Product Family Probe Line">
                    <FIELDS>AAH Product Major, AAH Product Minor</FIELDS>
                  </LINE>
                  <FIELD NAME="AAH Product Major">
                    <SET>$$ProdInfo:ProdMajorVersion</SET>
                    <XMLTAG>MAJOR</XMLTAG>
                  </FIELD>
                  <FIELD NAME="AAH Product Minor">
                    <SET>$$ProdInfo:ProdMinorVersion</SET>
                    <XMLTAG>MINOR</XMLTAG>
                  </FIELD>
                </TDLMESSAGE>
              </TDL>
            </DESC>
          </BODY>
        </ENVELOPE>
        """;

    private static string BuildDiscoveryRequest() =>
        """
        <ENVELOPE>
          <HEADER>
            <VERSION>1</VERSION>
            <TALLYREQUEST>Export</TALLYREQUEST>
            <TYPE>Collection</TYPE>
            <ID>List of Primary Companies</ID>
          </HEADER>
          <BODY>
            <DESC>
              <STATICVARIABLES>
                <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
              </STATICVARIABLES>
            </DESC>
          </BODY>
        </ENVELOPE>
        """;

    private static string BuildCurrentCompanyCompatibilityProbeRequest() =>
        """
        <ENVELOPE>
          <HEADER>
            <VERSION>1</VERSION>
            <TALLYREQUEST>EXPORT</TALLYREQUEST>
            <TYPE>DATA</TYPE>
            <ID>AAH Current Company Compatibility Probe</ID>
          </HEADER>
          <BODY>
            <DESC>
              <STATICVARIABLES>
                <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
              </STATICVARIABLES>
              <TDL>
                <TDLMESSAGE>
                  <REPORT NAME="AAH Current Company Compatibility Probe">
                    <FORMS>AAH Current Company Compatibility Form</FORMS>
                  </REPORT>
                  <FORM NAME="AAH Current Company Compatibility Form">
                    <PARTS>AAH Current Company Compatibility Part</PARTS>
                    <XMLTAG>CURRENTCOMPANY</XMLTAG>
                  </FORM>
                  <PART NAME="AAH Current Company Compatibility Part">
                    <LINES>AAH Current Company Compatibility Line</LINES>
                  </PART>
                  <LINE NAME="AAH Current Company Compatibility Line">
                    <FIELDS>AAH Current Company Name, AAH Current Company Books From, AAH Current Company Starting From, AAH Current Company State</FIELDS>
                  </LINE>
                  <FIELD NAME="AAH Current Company Name">
                    <SET>##SVCurrentCompany</SET>
                    <XMLTAG>NAME</XMLTAG>
                  </FIELD>
                  <FIELD NAME="AAH Current Company Books From">
                    <SET>$BooksFrom:Company:##SVCurrentCompany</SET>
                    <XMLTAG>BOOKSFROM</XMLTAG>
                  </FIELD>
                  <FIELD NAME="AAH Current Company Starting From">
                    <SET>$StartingFrom:Company:##SVCurrentCompany</SET>
                    <XMLTAG>STARTINGFROM</XMLTAG>
                  </FIELD>
                  <FIELD NAME="AAH Current Company State">
                    <SET>$StateName:Company:##SVCurrentCompany</SET>
                    <XMLTAG>STATENAME</XMLTAG>
                  </FIELD>
                </TDLMESSAGE>
              </TDL>
            </DESC>
          </BODY>
        </ENVELOPE>
        """;

    private static string BuildCompanyObjectRequest(string companyName)
    {
        var company = XmlEscape(companyName);
        return $"""
        <ENVELOPE>
          <HEADER>
            <VERSION>1</VERSION>
            <TALLYREQUEST>Export</TALLYREQUEST>
            <TYPE>Object</TYPE>
            <SUBTYPE>Company</SUBTYPE>
            <ID TYPE="Name">{company}</ID>
          </HEADER>
          <BODY>
            <DESC>
              <STATICVARIABLES>
                <SVCURRENTCOMPANY>{company}</SVCURRENTCOMPANY>
                <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
              </STATICVARIABLES>
              <FETCHLIST>
                <FETCH>Name</FETCH>
                <FETCH>CompanyNumber</FETCH>
                <FETCH>BooksFrom</FETCH>
                <FETCH>StartingFrom</FETCH>
                <FETCH>StateName</FETCH>
                <FETCH>GSTRegNumber</FETCH>
              </FETCHLIST>
            </DESC>
          </BODY>
        </ENVELOPE>
        """;
    }

    private static string BuildCompanyBookDateFormulaRequest(string companyName)
    {
        var company = XmlEscape(companyName);
        return $"""
        <ENVELOPE>
          <HEADER>
            <VERSION>1</VERSION>
            <TALLYREQUEST>EXPORT</TALLYREQUEST>
            <TYPE>DATA</TYPE>
            <ID>AAH Company Book Dates Report</ID>
          </HEADER>
          <BODY>
            <DESC>
              <STATICVARIABLES>
                <SVCURRENTCOMPANY>{company}</SVCURRENTCOMPANY>
                <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
              </STATICVARIABLES>
              <TDL>
                <TDLMESSAGE>
                  <REPORT NAME="AAH Company Book Dates Report">
                    <FORMS>AAH Company Book Dates Form</FORMS>
                  </REPORT>
                  <FORM NAME="AAH Company Book Dates Form">
                    <PARTS>AAH Company Book Dates Part</PARTS>
                    <XMLTAG>COMPANYPROFILE</XMLTAG>
                  </FORM>
                  <PART NAME="AAH Company Book Dates Part">
                    <LINES>AAH Company Book Dates Line</LINES>
                  </PART>
                  <LINE NAME="AAH Company Book Dates Line">
                    <FIELDS>AAH Company Name Field, AAH Books From Field, AAH Starting From Field</FIELDS>
                  </LINE>
                  <FIELD NAME="AAH Company Name Field">
                    <SET>##SVCurrentCompany</SET>
                    <XMLTAG>NAME</XMLTAG>
                  </FIELD>
                  <FIELD NAME="AAH Books From Field">
                    <SET>$BooksFrom:Company:##SVCurrentCompany</SET>
                    <XMLTAG>BOOKSFROM</XMLTAG>
                  </FIELD>
                  <FIELD NAME="AAH Starting From Field">
                    <SET>$StartingFrom:Company:##SVCurrentCompany</SET>
                    <XMLTAG>STARTINGFROM</XMLTAG>
                  </FIELD>
                </TDLMESSAGE>
              </TDL>
            </DESC>
          </BODY>
        </ENVELOPE>
        """;
    }

    private static string BuildFunctionRequest(
        string companyName,
        string functionName)
    {
        var company = XmlEscape(companyName);
        var function = XmlEscape(functionName);
        return $"""
        <ENVELOPE>
          <HEADER>
            <VERSION>1</VERSION>
            <TALLYREQUEST>Export</TALLYREQUEST>
            <TYPE>Function</TYPE>
            <ID>{function}</ID>
          </HEADER>
          <BODY>
            <DESC>
              <STATICVARIABLES>
                <SVCURRENTCOMPANY>{company}</SVCURRENTCOMPANY>
                <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
              </STATICVARIABLES>
            </DESC>
          </BODY>
        </ENVELOPE>
        """;
    }

    private static string BuildLedgerObjectRequest(string companyName, string ledgerName) =>
        $"""
        <ENVELOPE>
          <HEADER>
            <VERSION>1</VERSION>
            <TALLYREQUEST>Export</TALLYREQUEST>
            <TYPE>Object</TYPE>
            <SUBTYPE>Ledger</SUBTYPE>
            <ID TYPE="Name">{XmlEscape(ledgerName)}</ID>
          </HEADER>
          <BODY>
            <DESC>
              <STATICVARIABLES>
                <SVCURRENTCOMPANY>{XmlEscape(companyName)}</SVCURRENTCOMPANY>
                <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
              </STATICVARIABLES>
              <FETCHLIST>
                <FETCH>Name</FETCH>
                <FETCH>Parent</FETCH>
              </FETCHLIST>
            </DESC>
          </BODY>
        </ENVELOPE>
        """;

    private static string BuildErp9ListOfAccountsRequest(string companyName, string accountType) =>
        $"""
        <ENVELOPE>
          <HEADER>
            <VERSION>1</VERSION>
            <TALLYREQUEST>EXPORT</TALLYREQUEST>
            <TYPE>DATA</TYPE>
            <ID>List of Accounts</ID>
          </HEADER>
          <BODY>
            <DESC>
              <STATICVARIABLES>
                <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
                <SVCURRENTCOMPANY>{XmlEscape(companyName)}</SVCURRENTCOMPANY>
                <ACCOUNTTYPE>{XmlEscape(accountType)}</ACCOUNTTYPE>
              </STATICVARIABLES>
            </DESC>
          </BODY>
        </ENVELOPE>
        """;

    private static string BuildErp9DayBookRequest(
        string companyName,
        DateTime fromDate,
        DateTime toDate) =>
        $"""
        <ENVELOPE>
          <HEADER>
            <VERSION>1</VERSION>
            <TALLYREQUEST>EXPORT</TALLYREQUEST>
            <TYPE>DATA</TYPE>
            <ID>DayBook</ID>
          </HEADER>
          <BODY>
            <DESC>
              <STATICVARIABLES>
                <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
                <SVCURRENTCOMPANY>{XmlEscape(companyName)}</SVCURRENTCOMPANY>
                <SVFROMDATE TYPE="Date">{fromDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}</SVFROMDATE>
                <SVTODATE TYPE="Date">{toDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}</SVTODATE>
                <SVCURRENTDATE TYPE="Date">{toDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}</SVCURRENTDATE>
              </STATICVARIABLES>
            </DESC>
          </BODY>
        </ENVELOPE>
        """;

    private static string BuildErp9VoucherSliceRequest(
        string companyName,
        DateTime fromDate,
        DateTime toDate)
    {
        const string collectionName = "AAH ERP9 Voucher Slice";
        var nativeMethods = string.Concat(new[]
        {
            "MasterID", "AlterID", "GUID", "Date", "VoucherTypeName",
            "VoucherNumber", "Reference", "PartyLedgerName", "Amount", "Narration"
        }.Select(item => $"<NATIVEMETHOD>{item}</NATIVEMETHOD>"));

        return $"""
        <ENVELOPE>
          <HEADER>
            <VERSION>1</VERSION>
            <TALLYREQUEST>EXPORT</TALLYREQUEST>
            <TYPE>COLLECTION</TYPE>
            <ID>{collectionName}</ID>
          </HEADER>
          <BODY>
            <DESC>
              <STATICVARIABLES>
                <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
                <SVCURRENTCOMPANY>{XmlEscape(companyName)}</SVCURRENTCOMPANY>
                <SVFROMDATE TYPE="Date">{fromDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}</SVFROMDATE>
                <SVTODATE TYPE="Date">{toDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}</SVTODATE>
              </STATICVARIABLES>
              <TDL>
                <TDLMESSAGE>
                  <COLLECTION NAME="{collectionName}" ISINITIALIZE="Yes">
                    <TYPE>Voucher</TYPE>
                    <FILTER>AAHERP9VoucherSlicePeriod</FILTER>
                    {nativeMethods}
                    <FETCH>AllLedgerEntries.*</FETCH>
                    <FETCH>LedgerEntries.*</FETCH>
                    <FETCH>Narration</FETCH>
                    <FETCH>Amount</FETCH>
                  </COLLECTION>
                  <SYSTEM TYPE="Formulae" NAME="AAHERP9VoucherSlicePeriod">$Date &gt;= ##SVFromDate AND $Date &lt;= ##SVToDate</SYSTEM>
                </TDLMESSAGE>
              </TDL>
            </DESC>
          </BODY>
        </ENVELOPE>
        """;
    }

    private static string BuildNativeFullCompanySnapshotRequest(
        string companyName,
        DateTime fromDate,
        DateTime toDate,
        bool includeVouchers = true)
    {
        // FIX7 FROM-SCRATCH TALLY TRANSPORT REWORK.
        // Official Tally integration uses EXPORT + TYPE=COLLECTION + ID=<collection name>.
        // A Union Collection can contain collections of different native object types.
        // This avoids custom Form/Part/Line/XMLTAG contracts and $$NumItems formulas.
        var collections = new List<(string Name, string Type, string[] Methods)>
        {
            ("AAHCompanies", "Company", ["Name", "CompanyNumber", "GSTRegNumber", "StateName", "BooksFrom", "StartingFrom"]),
            ("AAHGroups", "Group", ["Name", "Parent", "MasterID", "GUID", "AlterID"]),
            ("AAHLedgers", "Ledger", ["Name", "Parent", "OpeningBalance", "MasterID", "GUID", "AlterID"]),
            ("AAHVoucherTypes", "VoucherType", ["Name", "Parent", "MasterID", "GUID", "AlterID"]),
            ("AAHUnits", "Unit", ["Name", "OriginalName", "IsSimpleUnit", "DecimalPlaces", "BaseUnits", "AdditionalUnits", "Conversion", "Denominator", "ReportingUQCName", "MasterID", "GUID", "AlterID"]),
            ("AAHStates", "State", ["Name"]),
            ("AAHCurrencies", "Currency", ["Name"]),
            ("AAHCostCategories", "CostCategory", ["Name", "Parent"]),
            ("AAHCostCentres", "CostCentre", ["Name", "Parent", "Category"]),
            ("AAHStockCategories", "StockCategory", ["Name", "Parent", "MasterID", "GUID", "AlterID"]),
            ("AAHStockGroups", "StockGroup", ["Name", "Parent", "MasterID", "GUID", "AlterID"]),
            ("AAHStockItems", "StockItem", ["Name", "Parent", "Category", "BaseUnits", "AdditionalUnits", "Conversion", "Denominator", "MailingName", "HSNCode", "GSTTypeOfSupply", "CostingMethod", "ValuationMethod", "IsBatchWiseOn", "IsPerishableOn", "HasMfgDate", "MasterID", "GUID", "AlterID"]),
            ("AAHGodowns", "Godown", ["Name", "Parent", "MasterID", "GUID", "AlterID"]),
            ("AAHTaxClassifications", "TaxClassification", ["Name", "Parent"]),
            ("AAHIncomeTaxClassifications", "IncomeTaxClassification", ["Name", "Parent"]),
            ("AAHTaxUnits", "TaxUnit", ["Name", "Parent"]),
            ("AAHIncomeTaxSlabs", "IncomeTaxSlab", ["Name", "Parent"])
        };
        if (includeVouchers)
        {
            collections.Add(("AAHVouchers", "Voucher", [
                "MasterID", "AlterID", "GUID", "Date", "VoucherTypeName",
                "VoucherNumber", "Reference", "PartyLedgerName", "Amount", "Narration"
            ]));
        }

        var builder = new StringBuilder();
        builder.AppendLine("<ENVELOPE>");
        builder.AppendLine("  <HEADER>");
        builder.AppendLine("    <VERSION>1</VERSION>");
        builder.AppendLine("    <TALLYREQUEST>EXPORT</TALLYREQUEST>");
        builder.AppendLine("    <TYPE>COLLECTION</TYPE>");
        builder.Append("    <ID>").Append(FullSnapshotCollectionName).AppendLine("</ID>");
        builder.AppendLine("  </HEADER>");
        builder.AppendLine("  <BODY><DESC><STATICVARIABLES>");
        builder.AppendLine("    <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>");
        builder.Append("    <SVCURRENTCOMPANY>").Append(XmlEscape(companyName)).AppendLine("</SVCURRENTCOMPANY>");
        builder.Append("    <SVFROMDATE TYPE=\"Date\">").Append(fromDate.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture)).AppendLine("</SVFROMDATE>");
        builder.Append("    <SVTODATE TYPE=\"Date\">").Append(toDate.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture)).AppendLine("</SVTODATE>");
        builder.AppendLine("  </STATICVARIABLES><TDL><TDLMESSAGE>");

        // Current Tally documentation shows a resultant union collection using the
        // Collections attribute. The XML form is COLLECTIONS and names the discrete
        // native data collections to combine into the one exported result.
        builder.Append("    <COLLECTION NAME=\"").Append(FullSnapshotCollectionName).AppendLine("\" ISINITIALIZE=\"Yes\">");
        builder.Append("      <COLLECTIONS>").Append(string.Join(",", collections.Select(item => item.Name))).AppendLine("</COLLECTIONS>");
        builder.AppendLine("    </COLLECTION>");

        foreach (var collection in collections)
        {
            builder.Append("    <COLLECTION NAME=\"").Append(XmlAttribute(collection.Name)).AppendLine("\" ISINITIALIZE=\"Yes\">");
            builder.Append("      <TYPE>").Append(XmlEscape(collection.Type)).AppendLine("</TYPE>");
            if (collection.Name == "AAHCompanies")
                builder.AppendLine("      <FILTER>AAHCurrentCompanyOnly</FILTER>");
            if (collection.Name == "AAHVouchers")
                builder.AppendLine("      <FILTER>AAHFullSnapshotPeriod</FILTER>");
            foreach (var method in collection.Methods)
                builder.Append("      <NATIVEMETHOD>").Append(XmlEscape(method)).AppendLine("</NATIVEMETHOD>");
            // Fix51 complete initial FY authority: retain the full native object body.
            // Masters are normalized into AAH tables, while the returned raw XML remains
            // available as the baseline authority. Vouchers additionally fetch the
            // nested accounting/inventory/bill/bank/cost-centre structures needed for
            // safe future ALTERs without asking Tally to resend the pre-write object.
            builder.AppendLine("      <FETCH>*</FETCH>");
            if (collection.Name == "AAHVouchers")
            {
                builder.AppendLine("      <FETCH>AllLedgerEntries.*,LedgerEntries.*,InventoryEntries.*,AllInventoryEntries.*,AccountingAllocations.*,BatchAllocations.*,BillAllocations.*,BankAllocations.*,CategoryAllocations.*,CostCentreAllocations.*</FETCH>");
            }
            builder.AppendLine("    </COLLECTION>");
        }

        builder.AppendLine("    <SYSTEM TYPE=\"Formulae\" NAME=\"AAHCurrentCompanyOnly\">$Name = ##SVCurrentCompany</SYSTEM>");
        builder.AppendLine("    <SYSTEM TYPE=\"Formulae\" NAME=\"AAHFullSnapshotPeriod\">$Date &gt;= ##SVFromDate AND $Date &lt;= ##SVToDate</SYSTEM>");
        builder.AppendLine("  </TDLMESSAGE></TDL></DESC></BODY>");
        builder.AppendLine("</ENVELOPE>");
        return builder.ToString();
    }

    private static IReadOnlyDictionary<string, string> ParseLedgerParentsFromSnapshot(string xml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var document = XDocument.Parse(CleanXml(xml), LoadOptions.PreserveWhitespace);
        foreach (var ledger in document.Descendants().Where(element =>
                     element.Name.LocalName.Equals("LEDGER", StringComparison.OrdinalIgnoreCase)))
        {
            var rawName = ledger.Attributes().FirstOrDefault(attribute =>
                              attribute.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase))?.Value
                          ?? ledger.Elements().FirstOrDefault(element =>
                              element.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase))?.Value
                          ?? string.Empty;
            var name = CleanTallyMasterDisplayName(rawName);
            if (name.Length == 0) continue;
            var parent = CleanTallyMasterDisplayName(ledger.Elements().FirstOrDefault(element =>
                element.Name.LocalName.Equals("PARENT", StringComparison.OrdinalIgnoreCase))?.Value);
            result[name] = parent;
        }
        return result;
    }

    private static IReadOnlySet<string> ParseGroupNamesFromSnapshot(string xml)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var document = XDocument.Parse(CleanXml(xml), LoadOptions.None);
        foreach (var group in document.Descendants().Where(element =>
                     element.Name.LocalName.Equals("GROUP", StringComparison.OrdinalIgnoreCase)))
        {
            var rawName = group.Attributes().FirstOrDefault(attribute =>
                              attribute.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase))?.Value
                          ?? group.Elements().FirstOrDefault(element =>
                              element.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase))?.Value
                          ?? string.Empty;
            var name = CleanTallyMasterDisplayName(rawName);
            if (name.Length > 0) result.Add(name);
        }
        return result;
    }


    private static string BuildAllMastersRequest(string companyName)
    {
        var company = XmlEscape(companyName);
        return $"""
        <ENVELOPE>
          <HEADER>
            <VERSION>1</VERSION>
            <TALLYREQUEST>EXPORT</TALLYREQUEST>
            <TYPE>DATA</TYPE>
            <ID>All Masters</ID>
          </HEADER>
          <BODY>
            <DESC>
              <STATICVARIABLES>
                <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
                <SVCURRENTCOMPANY>{company}</SVCURRENTCOMPANY>
              </STATICVARIABLES>
            </DESC>
          </BODY>
        </ENVELOPE>
        """;
    }

    private static string BuildCollectionRequest(
        string companyName,
        string collectionName,
        string objectType,
        string fetchList,
        DateTime fromDate,
        DateTime toDate)
    {
        var nativeMethods = string.Concat(
            fetchList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => $"<NATIVEMETHOD>{XmlEscape(item)}</NATIVEMETHOD>"));

        return $"""
        <ENVELOPE>
          <HEADER>
            <VERSION>1</VERSION>
            <TALLYREQUEST>EXPORT</TALLYREQUEST>
            <TYPE>COLLECTION</TYPE>
            <ID>{XmlEscape(collectionName)}</ID>
          </HEADER>
          <BODY>
            <DESC>
              <STATICVARIABLES>
                <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
                <SVCURRENTCOMPANY>{XmlEscape(companyName)}</SVCURRENTCOMPANY>
                <SVFROMDATE TYPE="Date">{fromDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}</SVFROMDATE>
                <SVTODATE TYPE="Date">{toDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}</SVTODATE>
              </STATICVARIABLES>
              <TDL>
                <TDLMESSAGE>
                  <COLLECTION NAME="{XmlAttribute(collectionName)}" ISINITIALIZE="Yes">
                    <TYPE>{XmlEscape(objectType)}</TYPE>
                    {nativeMethods}
                  </COLLECTION>
                </TDLMESSAGE>
              </TDL>
            </DESC>
          </BODY>
        </ENVELOPE>
        """;
    }

    private static string BuildCollectionRequest(
        string companyName,
        string collectionName,
        string objectType,
        string fetchList)
    {
        var nativeMethods = string.Concat(
            fetchList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => $"<NATIVEMETHOD>{XmlEscape(item)}</NATIVEMETHOD>"));

        return $"""
        <ENVELOPE>
          <HEADER>
            <VERSION>1</VERSION>
            <TALLYREQUEST>EXPORT</TALLYREQUEST>
            <TYPE>COLLECTION</TYPE>
            <ID>{XmlEscape(collectionName)}</ID>
          </HEADER>
          <BODY>
            <DESC>
              <STATICVARIABLES>
                <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
                <SVCURRENTCOMPANY>{XmlEscape(companyName)}</SVCURRENTCOMPANY>
              </STATICVARIABLES>
              <TDL>
                <TDLMESSAGE>
                  <COLLECTION NAME="{XmlAttribute(collectionName)}" ISINITIALIZE="Yes">
                    <TYPE>{XmlEscape(objectType)}</TYPE>
                    {nativeMethods}
                  </COLLECTION>
                </TDLMESSAGE>
              </TDL>
            </DESC>
          </BODY>
        </ENVELOPE>
        """;
    }

    private static string BuildAuthorityPulseRequest(string companyName)
    {
        return $"""
        <ENVELOPE>
          <HEADER><VERSION>1</VERSION><TALLYREQUEST>EXPORT</TALLYREQUEST><TYPE>DATA</TYPE><ID>AAH Authority Pulse</ID></HEADER>
          <BODY><DESC><STATICVARIABLES>
            <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT><SVCURRENTCOMPANY>{XmlEscape(companyName)}</SVCURRENTCOMPANY>
          </STATICVARIABLES><TDL><TDLMESSAGE>
            <REPORT NAME="AAH Authority Pulse"><FORMS>AAH Authority Pulse Form</FORMS></REPORT>
            <FORM NAME="AAH Authority Pulse Form"><PARTS>AAH Authority Pulse Part</PARTS><XMLTAG>AAHAUTHORITYPULSE</XMLTAG></FORM>
            <PART NAME="AAH Authority Pulse Part"><LINES>AAH Authority Pulse Line</LINES></PART>
            <LINE NAME="AAH Authority Pulse Line"><FIELDS>AAHCompany,AAHLastAlter,AAHVouchers,AAHLedgers,AAHGroups,AAHVTypes,AAHStockItems,AAHUnits,AAHGodowns</FIELDS></LINE>
            <FIELD NAME="AAHCompany"><SET>##SVCurrentCompany</SET><XMLTAG>AAHCOMPANY</XMLTAG></FIELD>
            <FIELD NAME="AAHLastAlter"><SET>$$LastAlterID</SET><XMLTAG>AAHLASTALTERID</XMLTAG></FIELD>
            <FIELD NAME="AAHVouchers"><SET>$$NumVouchers</SET><XMLTAG>AAHVOUCHERCOUNT</XMLTAG></FIELD>
            <FIELD NAME="AAHLedgers"><SET>$$NumLedgers</SET><XMLTAG>AAHLEDGERCOUNT</XMLTAG></FIELD>
            <FIELD NAME="AAHGroups"><SET>$$NumGroups</SET><XMLTAG>AAHGROUPCOUNT</XMLTAG></FIELD>
            <FIELD NAME="AAHVTypes"><SET>$$NumVoucherTypes</SET><XMLTAG>AAHVOUCHERTYPECOUNT</XMLTAG></FIELD>
            <FIELD NAME="AAHStockItems"><SET>$$NumStockItems</SET><XMLTAG>AAHSTOCKITEMCOUNT</XMLTAG></FIELD>
            <FIELD NAME="AAHUnits"><SET>$$NumUnits</SET><XMLTAG>AAHUNITCOUNT</XMLTAG></FIELD>
            <FIELD NAME="AAHGodowns"><SET>$$NumGodowns</SET><XMLTAG>AAHGODOWNCOUNT</XMLTAG></FIELD>
          </TDLMESSAGE></TDL></DESC></BODY>
        </ENVELOPE>
        """;
    }

    private static string BuildAuthorityDeltaRequest(
        string companyName, DateTime fromDate, DateTime toDate, long afterAlterId, bool erp9)
    {
        var from = erp9 ? fromDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : fromDate.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture);
        var to = erp9 ? toDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : toDate.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture);
        var collections = new (string Name,string Type,string Methods,bool Voucher)[]
        {
            ("AAHDeltaGroups","Group","Name,Parent,MasterID,GUID,AlterID",false),
            ("AAHDeltaLedgers","Ledger","Name,Parent,OpeningBalance,MasterID,GUID,AlterID",false),
            ("AAHDeltaVoucherTypes","VoucherType","Name,Parent,MasterID,GUID,AlterID",false),
            ("AAHDeltaUnits","Unit","Name,OriginalName,IsSimpleUnit,DecimalPlaces,BaseUnits,AdditionalUnits,Conversion,Denominator,ReportingUQCName,MasterID,GUID,AlterID",false),
            ("AAHDeltaStockGroups","StockGroup","Name,Parent,MasterID,GUID,AlterID",false),
            ("AAHDeltaStockCategories","StockCategory","Name,Parent,MasterID,GUID,AlterID",false),
            ("AAHDeltaStockItems","StockItem","Name,Parent,Category,BaseUnits,AdditionalUnits,Conversion,Denominator,MailingName,HSNCode,GSTTypeOfSupply,CostingMethod,ValuationMethod,IsBatchWiseOn,IsPerishableOn,HasMfgDate,MasterID,GUID,AlterID",false),
            ("AAHDeltaGodowns","Godown","Name,Parent,MasterID,GUID,AlterID",false),
            ("AAHDeltaVouchers","Voucher","MasterID,AlterID,GUID,Date,VoucherTypeName,VoucherNumber,Reference,PartyLedgerName,Amount,Narration",true)
        };
        var b = new StringBuilder();
        b.AppendLine("<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>EXPORT</TALLYREQUEST><TYPE>COLLECTION</TYPE><ID>AAH Authority Delta</ID></HEADER>");
        b.AppendLine("<BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>");
        b.Append("<SVCURRENTCOMPANY>").Append(XmlEscape(companyName)).AppendLine("</SVCURRENTCOMPANY>");
        b.Append("<SVFROMDATE TYPE=\"Date\">").Append(from).AppendLine("</SVFROMDATE>");
        b.Append("<SVTODATE TYPE=\"Date\">").Append(to).AppendLine("</SVTODATE></STATICVARIABLES><TDL><TDLMESSAGE>");
        b.Append("<COLLECTION NAME=\"AAH Authority Delta\" ISINITIALIZE=\"Yes\"><COLLECTIONS>")
            .Append(string.Join(",", collections.Select(x => x.Name))).AppendLine("</COLLECTIONS></COLLECTION>");
        foreach (var c in collections)
        {
            b.Append("<COLLECTION NAME=\"").Append(c.Name).Append("\" ISINITIALIZE=\"Yes\"><TYPE>").Append(c.Type).AppendLine("</TYPE>");
            b.AppendLine(c.Voucher ? "<FILTER>AAHDeltaVoucherFilter</FILTER>" : "<FILTER>AAHDeltaMasterFilter</FILTER>");
            foreach (var method in c.Methods.Split(',')) b.Append("<NATIVEMETHOD>").Append(method).AppendLine("</NATIVEMETHOD>");
            b.AppendLine("<FETCH>*</FETCH>");
            if (c.Voucher)
                b.AppendLine("<FETCH>AllLedgerEntries.*,LedgerEntries.*,InventoryEntries.*,AllInventoryEntries.*,AccountingAllocations.*,BatchAllocations.*,BillAllocations.*,BankAllocations.*,CategoryAllocations.*,CostCentreAllocations.*</FETCH>");
            b.AppendLine("</COLLECTION>");
        }
        b.Append("<SYSTEM TYPE=\"Formulae\" NAME=\"AAHDeltaMasterFilter\">$AlterID &gt; ").Append(afterAlterId.ToString(CultureInfo.InvariantCulture)).AppendLine("</SYSTEM>");
        b.Append("<SYSTEM TYPE=\"Formulae\" NAME=\"AAHDeltaVoucherFilter\">$Date &gt;= ##SVFromDate AND $Date &lt;= ##SVToDate AND $AlterID &gt; ").Append(afterAlterId.ToString(CultureInfo.InvariantCulture)).AppendLine("</SYSTEM>");
        b.AppendLine("</TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>");
        return b.ToString();
    }

    private static long ParseNumericAlterId(string? value) =>
        long.TryParse(new string((value ?? string.Empty).Where(char.IsDigit).ToArray()), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0L;

    private static long ParseMaxAlterId(string xml)
    {
        try
        {
            var document = XDocument.Parse(CleanXml(xml), LoadOptions.None);
            return document.Descendants()
                .Where(element => element.Name.LocalName.Equals("ALTERID", StringComparison.OrdinalIgnoreCase))
                .Select(element => long.TryParse(new string(element.Value.Where(char.IsDigit).ToArray()), out var value) ? value : 0L)
                .DefaultIfEmpty(0L).Max();
        }
        catch { return 0L; }
    }

    private static string BuildVoucherDeltaRequest(
        string companyName,
        DateTime fromDate,
        DateTime toDate,
        long afterAlterId,
        bool erp9)
    {
        const string collectionName = "AAH Voucher Delta";
        var nativeMethods = string.Concat(new[]
        {
            "MasterID", "AlterID", "GUID", "Date", "VoucherTypeName",
            "VoucherNumber", "Reference", "PartyLedgerName", "Amount", "Narration"
        }.Select(item => $"<NATIVEMETHOD>{item}</NATIVEMETHOD>"));
        var from = erp9
            ? fromDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : fromDate.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture);
        var to = erp9
            ? toDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : toDate.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture);
        return $"""
        <ENVELOPE>
          <HEADER>
            <VERSION>1</VERSION>
            <TALLYREQUEST>EXPORT</TALLYREQUEST>
            <TYPE>COLLECTION</TYPE>
            <ID>{collectionName}</ID>
          </HEADER>
          <BODY>
            <DESC>
              <STATICVARIABLES>
                <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
                <SVCURRENTCOMPANY>{XmlEscape(companyName)}</SVCURRENTCOMPANY>
                <SVFROMDATE TYPE="Date">{from}</SVFROMDATE>
                <SVTODATE TYPE="Date">{to}</SVTODATE>
              </STATICVARIABLES>
              <TDL>
                <TDLMESSAGE>
                  <COLLECTION NAME="{collectionName}" ISINITIALIZE="Yes">
                    <TYPE>Voucher</TYPE>
                    <FILTER>AAHVoucherDeltaFilter</FILTER>
                    {nativeMethods}
                    <FETCH>AllLedgerEntries.*</FETCH>
                    <FETCH>LedgerEntries.*</FETCH>
                    <FETCH>Narration</FETCH>
                    <FETCH>Amount</FETCH>
                  </COLLECTION>
                  <SYSTEM TYPE="Formulae" NAME="AAHVoucherDeltaFilter">$Date &gt;= ##SVFromDate AND $Date &lt;= ##SVToDate AND $AlterID &gt; {afterAlterId.ToString(CultureInfo.InvariantCulture)}</SYSTEM>
                </TDLMESSAGE>
              </TDL>
            </DESC>
          </BODY>
        </ENVELOPE>
        """;
    }


    private static string BuildLedgerVoucherFullObjectRequest(
        string companyName,
        string sourceLedgerName,
        DateTime fromDate,
        DateTime toDate)
    {
        const string collectionName = "AAH Ledger Merge Vouchers";
        return $"""
        <ENVELOPE>
          <HEADER>
            <VERSION>1</VERSION>
            <TALLYREQUEST>EXPORT</TALLYREQUEST>
            <TYPE>COLLECTION</TYPE>
            <ID>{collectionName}</ID>
          </HEADER>
          <BODY>
            <DESC>
              <STATICVARIABLES>
                <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
                <SVCURRENTCOMPANY>{XmlEscape(companyName)}</SVCURRENTCOMPANY>
                <SVFROMDATE TYPE="Date">{fromDate.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture)}</SVFROMDATE>
                <SVTODATE TYPE="Date">{toDate.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture)}</SVTODATE>
              </STATICVARIABLES>
              <TDL>
                <TDLMESSAGE>
                  <COLLECTION NAME="{collectionName}" ISINITIALIZE="Yes">
                    <TYPE>Vouchers : Ledger</TYPE>
                    <CHILDOF>{XmlEscape(sourceLedgerName)}</CHILDOF>
                    <NATIVEMETHOD>MasterID</NATIVEMETHOD>
                    <NATIVEMETHOD>AlterID</NATIVEMETHOD>
                    <NATIVEMETHOD>GUID</NATIVEMETHOD>
                    <NATIVEMETHOD>Date</NATIVEMETHOD>
                    <NATIVEMETHOD>VoucherTypeName</NATIVEMETHOD>
                    <NATIVEMETHOD>VoucherNumber</NATIVEMETHOD>
                    <FETCH>*</FETCH>
                  </COLLECTION>
                </TDLMESSAGE>
              </TDL>
            </DESC>
          </BODY>
        </ENVELOPE>
        """;
    }


    private static string BuildVoucherFullObjectByMasterIdsRequest(
        string companyName,
        IEnumerable<string> masterIds,
        DateTime fromDate,
        DateTime toDate)
    {
        const string collectionName = "AAH Merge Voucher Objects";
        var ids = masterIds.Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
        if (ids.Length == 0) throw new InvalidOperationException("No Tally Voucher MASTERIDs were supplied for merge object export.");
        foreach (var id in ids)
            if (!long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                throw new InvalidOperationException($"Tally Voucher MASTERID '{id}' is not numeric and cannot be used for safe merge alteration.");
        var filter = string.Join(" OR ", ids.Select(id => $"($MasterID = {id})"));

        return $"""
        <ENVELOPE>
          <HEADER>
            <VERSION>1</VERSION>
            <TALLYREQUEST>EXPORT</TALLYREQUEST>
            <TYPE>COLLECTION</TYPE>
            <ID>{collectionName}</ID>
          </HEADER>
          <BODY>
            <DESC>
              <STATICVARIABLES>
                <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
                <SVCURRENTCOMPANY>{XmlEscape(companyName)}</SVCURRENTCOMPANY>
                <SVFROMDATE TYPE="Date">{fromDate.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture)}</SVFROMDATE>
                <SVTODATE TYPE="Date">{toDate.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture)}</SVTODATE>
              </STATICVARIABLES>
              <TDL>
                <TDLMESSAGE>
                  <COLLECTION NAME="{collectionName}" ISINITIALIZE="Yes">
                    <TYPE>Voucher</TYPE>
                    <FILTER>AAHMergeVoucherMasterIds</FILTER>
                    <NATIVEMETHOD>MasterID</NATIVEMETHOD>
                    <NATIVEMETHOD>AlterID</NATIVEMETHOD>
                    <NATIVEMETHOD>GUID</NATIVEMETHOD>
                    <NATIVEMETHOD>Date</NATIVEMETHOD>
                    <NATIVEMETHOD>VoucherTypeName</NATIVEMETHOD>
                    <NATIVEMETHOD>VoucherNumber</NATIVEMETHOD>
                    <FETCH>*</FETCH>
                  </COLLECTION>
                  <SYSTEM TYPE="Formulae" NAME="AAHMergeVoucherMasterIds">{filter}</SYSTEM>
                </TDLMESSAGE>
              </TDL>
            </DESC>
          </BODY>
        </ENVELOPE>
        """;
    }

    private static string BuildVoucherAuthorityRequest(
        string companyName,
        DateTime fromDate,
        DateTime toDate)
    {
        const string collectionName = "AAH Voucher Authority";
        var nativeMethods = string.Concat(new[]
        {
            "MasterID", "AlterID", "GUID", "Date", "VoucherTypeName",
            "VoucherNumber", "Reference", "PartyLedgerName", "Amount", "Narration"
        }.Select(item => $"<NATIVEMETHOD>{item}</NATIVEMETHOD>"));

        return $"""
        <ENVELOPE>
          <HEADER>
            <VERSION>1</VERSION>
            <TALLYREQUEST>EXPORT</TALLYREQUEST>
            <TYPE>COLLECTION</TYPE>
            <ID>{collectionName}</ID>
          </HEADER>
          <BODY>
            <DESC>
              <STATICVARIABLES>
                <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
                <SVCURRENTCOMPANY>{XmlEscape(companyName)}</SVCURRENTCOMPANY>
                <SVFROMDATE TYPE="Date">{fromDate.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture)}</SVFROMDATE>
                <SVTODATE TYPE="Date">{toDate.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture)}</SVTODATE>
              </STATICVARIABLES>
              <TDL>
                <TDLMESSAGE>
                  <COLLECTION NAME="{collectionName}" ISINITIALIZE="Yes">
                    <TYPE>Voucher</TYPE>
                    <FILTER>AAHRequestedVoucherAuthorityPeriod</FILTER>
                    {nativeMethods}
                    <FETCH>*</FETCH>
                    <FETCH>AllLedgerEntries.*,LedgerEntries.*,InventoryEntries.*,AllInventoryEntries.*,AccountingAllocations.*,BatchAllocations.*,BillAllocations.*,BankAllocations.*,CategoryAllocations.*,CostCentreAllocations.*</FETCH>
                  </COLLECTION>
                  <SYSTEM TYPE="Formulae" NAME="AAHRequestedVoucherAuthorityPeriod">$Date &gt;= ##SVFromDate AND $Date &lt;= ##SVToDate</SYSTEM>
                </TDLMESSAGE>
              </TDL>
            </DESC>
          </BODY>
        </ENVELOPE>
        """;
    }

    private static string BuildVoucherIndexRequest(
        string companyName,
        DateTime fromDate,
        DateTime toDate)
    {
        const string collectionName = "AAH Voucher Index";
        var nativeMethods = string.Concat(new[]
        {
            "MasterID", "AlterID", "GUID", "Date", "VoucherTypeName",
            "VoucherNumber", "Reference", "PartyLedgerName", "Amount", "Narration"
        }.Select(item => $"<NATIVEMETHOD>{item}</NATIVEMETHOD>"));

        return $"""
        <ENVELOPE>
          <HEADER>
            <VERSION>1</VERSION>
            <TALLYREQUEST>EXPORT</TALLYREQUEST>
            <TYPE>COLLECTION</TYPE>
            <ID>{collectionName}</ID>
          </HEADER>
          <BODY>
            <DESC>
              <STATICVARIABLES>
                <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
                <SVCURRENTCOMPANY>{XmlEscape(companyName)}</SVCURRENTCOMPANY>
                <SVFROMDATE TYPE="Date">{fromDate.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture)}</SVFROMDATE>
                <SVTODATE TYPE="Date">{toDate.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture)}</SVTODATE>
              </STATICVARIABLES>
              <TDL>
                <TDLMESSAGE>
                  <COLLECTION NAME="{collectionName}" ISINITIALIZE="Yes">
                    <TYPE>Voucher</TYPE>
                    <FILTER>AAHRequestedVoucherPeriod</FILTER>
                    {nativeMethods}
                    <FETCH>AllLedgerEntries.*</FETCH>
                    <FETCH>LedgerEntries.*</FETCH>
                    <FETCH>Narration</FETCH>
                    <FETCH>Amount</FETCH>
                  </COLLECTION>
                  <SYSTEM TYPE="Formulae" NAME="AAHRequestedVoucherPeriod">$Date &gt;= ##SVFromDate AND $Date &lt;= ##SVToDate</SYSTEM>
                </TDLMESSAGE>
              </TDL>
            </DESC>
          </BODY>
        </ENVELOPE>
        """;
    }


    private static string BuildLegacyVoucherIndexRequest(
        string companyName,
        DateTime fromDate,
        DateTime toDate,
        bool includeNarration)
    {
        const string collectionName = "AAH ERP9 Voucher Index";
        var methods = new List<string>
        {
            "MasterID", "AlterID", "Date", "VoucherTypeName",
            "VoucherNumber", "Reference", "PartyLedgerName", "Amount"
        };
        if (includeNarration) methods.Add("Narration");

        var nativeMethods = string.Concat(
            methods.Select(item => $"<NATIVEMETHOD>{item}</NATIVEMETHOD>"));
        var invariant = CultureInfo.InvariantCulture;
        var from = fromDate.ToString("d-MMM-yyyy", invariant);
        var to = toDate.ToString("d-MMM-yyyy", invariant);

        return $"""
        <ENVELOPE>
          <HEADER>
            <VERSION>1</VERSION>
            <TALLYREQUEST>EXPORT</TALLYREQUEST>
            <TYPE>COLLECTION</TYPE>
            <ID>{collectionName}</ID>
          </HEADER>
          <BODY>
            <DESC>
              <STATICVARIABLES>
                <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
                <SVCURRENTCOMPANY>{XmlEscape(companyName)}</SVCURRENTCOMPANY>
                <SVFROMDATE TYPE="Date">{from}</SVFROMDATE>
                <SVTODATE TYPE="Date">{to}</SVTODATE>
              </STATICVARIABLES>
              <TDL>
                <TDLMESSAGE>
                  <COLLECTION NAME="{collectionName}" ISINITIALIZE="Yes">
                    <TYPE>Voucher</TYPE>
                    <FILTER>AAHRequestedVoucherPeriod</FILTER>
                    {nativeMethods}
                    <FETCH>AllLedgerEntries.*</FETCH>
                    <FETCH>LedgerEntries.*</FETCH>
                    <FETCH>Narration</FETCH>
                    <FETCH>Amount</FETCH>
                  </COLLECTION>
                  <SYSTEM TYPE="Formulae" NAME="AAHRequestedVoucherPeriod">$Date &gt;= ##SVFromDate AND $Date &lt;= ##SVToDate</SYSTEM>
                </TDLMESSAGE>
              </TDL>
            </DESC>
          </BODY>
        </ENVELOPE>
        """;
    }

    private static string BuildReportRequest(
        string companyName,
        string reportId,
        DateTime fromDate,
        DateTime toDate) =>
        $"""
        <ENVELOPE>
          <HEADER>
            <VERSION>1</VERSION>
            <TALLYREQUEST>Export</TALLYREQUEST>
            <TYPE>Data</TYPE>
            <ID>{XmlEscape(reportId)}</ID>
          </HEADER>
          <BODY>
            <DESC>
              <STATICVARIABLES>
                <SVCURRENTCOMPANY>{XmlEscape(companyName)}</SVCURRENTCOMPANY>
                <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
                <SVFROMDATE TYPE="Date">{fromDate.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture)}</SVFROMDATE>
                <SVTODATE TYPE="Date">{toDate.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture)}</SVTODATE>
                <EXPLODEFLAG>Yes</EXPLODEFLAG>
              </STATICVARIABLES>
            </DESC>
          </BODY>
        </ENVELOPE>
        """;

    private static TallyReportSnapshot ParseReportSnapshot(
        string displayName,
        string reportId,
        DateTime fromDate,
        DateTime toDate,
        string response)
    {
        try
        {
            var document = XDocument.Parse(response, LoadOptions.None);
            var rows = ParseReportRows(document);
            return new TallyReportSnapshot
            {
                DisplayName = displayName,
                ReportId = reportId,
                FromDate = fromDate,
                ToDate = toDate,
                IsAvailable = true,
                Status = rows.Count == 0
                    ? "Report received from Tally. No display rows were returned for the selected period."
                    : $"Report received from Tally with {rows.Count:N0} display row(s).",
                RefreshedUtc = DateTime.UtcNow,
                ResponseCharacters = response.Length,
                RawResponse = response,
                Rows = rows
            };
        }
        catch (Exception exception)
        {
            return new TallyReportSnapshot
            {
                DisplayName = displayName,
                ReportId = reportId,
                FromDate = fromDate,
                ToDate = toDate,
                IsAvailable = false,
                Status = "Tally returned the report, but its XML layout could not be summarized safely.",
                Error = exception.Message,
                RefreshedUtc = DateTime.UtcNow,
                ResponseCharacters = response.Length
            };
        }
    }

    private static IReadOnlyList<TallyReportRow> ParseReportRows(XDocument document)
    {
        var rows = new List<TallyReportRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nameTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DSPDISPNAME", "DSPVCHTYPE", "DSPSTKNAME", "DSPLEDNAME",
            "DSPCLNAME", "DSPACCNAME", "LEDGERNAME", "VOUCHERTYPENAME",
            "STOCKITEMNAME", "GROUPNAME", "PARTICULARS", "NAME"
        };

        foreach (var element in document.Descendants())
        {
            var local = NormalizeName(element.Name.LocalName);
            if (!nameTags.Contains(local))
            {
                continue;
            }

            var particulars = CleanText(
                element.HasElements
                    ? element.DescendantsAndSelf()
                        .FirstOrDefault(child =>
                            NormalizeName(child.Name.LocalName) is "DSPDISPNAME" or "NAME" or "LEDGERNAME" or "VOUCHERTYPENAME" or "STOCKITEMNAME")?.Value
                        ?? element.Value
                    : element.Value);
            if (particulars.Length == 0 || particulars.Length > 240)
            {
                continue;
            }

            var valueContainer = element.ElementsAfterSelf().FirstOrDefault() ?? element.Parent ?? element;
            var debit = 0m;
            var credit = 0m;
            var amount = 0m;
            var details = new List<string>();

            foreach (var valueElement in valueContainer.DescendantsAndSelf())
            {
                var tag = NormalizeName(valueElement.Name.LocalName);
                if (valueElement.HasElements)
                {
                    continue;
                }

                var raw = CleanText(valueElement.Value);
                if (!TryParseReportNumber(raw, out var number))
                {
                    continue;
                }

                if (tag.Contains("DR", StringComparison.OrdinalIgnoreCase) &&
                    (tag.Contains("AMT", StringComparison.OrdinalIgnoreCase) ||
                     tag.Contains("BAL", StringComparison.OrdinalIgnoreCase)))
                {
                    debit += Math.Abs(number);
                }
                else if (tag.Contains("CR", StringComparison.OrdinalIgnoreCase) &&
                         (tag.Contains("AMT", StringComparison.OrdinalIgnoreCase) ||
                          tag.Contains("BAL", StringComparison.OrdinalIgnoreCase)))
                {
                    credit += Math.Abs(number);
                }
                else if (tag.Contains("AMT", StringComparison.OrdinalIgnoreCase) ||
                         tag.Contains("AMOUNT", StringComparison.OrdinalIgnoreCase) ||
                         tag.Contains("BAL", StringComparison.OrdinalIgnoreCase) ||
                         tag.Contains("COUNT", StringComparison.OrdinalIgnoreCase) ||
                         tag.Contains("QTY", StringComparison.OrdinalIgnoreCase))
                {
                    amount += number;
                }

                if (details.Count < 3)
                {
                    details.Add($"{valueElement.Name.LocalName}: {raw}");
                }
            }

            var key = $"{particulars}|{debit:0.####}|{credit:0.####}|{amount:0.####}";
            if (!seen.Add(key))
            {
                continue;
            }

            rows.Add(new TallyReportRow
            {
                Particulars = particulars,
                Debit = debit,
                Credit = credit,
                Amount = amount,
                Detail = string.Join(" • ", details)
            });

            if (rows.Count >= 500)
            {
                break;
            }
        }

        return rows;
    }

    private static bool TryParseReportNumber(string value, out decimal number)
    {
        var normalized = (value ?? string.Empty)
            .Replace("\u00A0", " ", StringComparison.Ordinal)
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("₹", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (normalized.Length == 0)
        {
            number = 0m;
            return false;
        }

        var creditSuffix = normalized.EndsWith(" Cr", StringComparison.OrdinalIgnoreCase) ||
                           normalized.EndsWith("Cr", StringComparison.OrdinalIgnoreCase);
        var debitSuffix = normalized.EndsWith(" Dr", StringComparison.OrdinalIgnoreCase) ||
                          normalized.EndsWith("Dr", StringComparison.OrdinalIgnoreCase);
        if (creditSuffix || debitSuffix)
        {
            normalized = normalized[..^2].TrimEnd();
        }

        var parenthesized = normalized.StartsWith('(') && normalized.EndsWith(')');
        var trailingMinus = normalized.EndsWith("-", StringComparison.Ordinal);
        normalized = normalized.Trim('(', ')', ' ');
        if (trailingMinus)
        {
            normalized = normalized[..^1].TrimEnd();
        }
        if (normalized.Length == 0)
        {
            number = 0m;
            return false;
        }

        var parsed = decimal.TryParse(
            normalized,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out number);
        if (parsed && (parenthesized || trailingMinus || creditSuffix))
        {
            number = -Math.Abs(number);
        }
        else if (parsed && debitSuffix)
        {
            number = Math.Abs(number);
        }
        return parsed;
    }

    private static string DecodeResponse(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        if (bytes.Length >= 2 &&
            ((bytes[0] == 0xFF && bytes[1] == 0xFE) ||
             (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            return Encoding.Unicode.GetString(bytes);
        }

        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        var asciiHead = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 300));
        if (asciiHead.Contains("utf-16", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.Unicode.GetString(bytes);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static string CleanXml(string value) =>
        TallyXmlSanitizer.Sanitize(value);

    private static string RemoveXmlDeclaration(string value) =>
        Regex.Replace(
            value,
            "^\\s*<\\?xml[^>]*\\?>",
            string.Empty,
            RegexOptions.IgnoreCase);

    private static string ChildText(
        XElement element,
        params string[] names)
    {
        var wanted = new HashSet<string>(
            names.Select(NormalizeName),
            StringComparer.OrdinalIgnoreCase);

        var direct = element.Elements().FirstOrDefault(child =>
            wanted.Contains(NormalizeName(child.Name.LocalName)));

        if (direct is not null)
        {
            return CleanText(direct.Value);
        }

        var descendant = element.Descendants().FirstOrDefault(child =>
            wanted.Contains(NormalizeName(child.Name.LocalName)));

        return descendant is null
            ? string.Empty
            : CleanText(descendant.Value);
    }

    private static string ReadTag(
        string text,
        string tagName)
    {
        var match = Regex.Match(
            text,
            $"<{Regex.Escape(tagName)}\\b[^>]*>(.*?)</{Regex.Escape(tagName)}>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        return match.Success
            ? CleanText(Regex.Replace(match.Groups[1].Value, "<[^>]+>", string.Empty))
            : string.Empty;
    }

    private static int ReadIntTag(
        string text,
        string tagName) =>
        int.TryParse(
            ReadTag(text, tagName),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 0;

    private static decimal ParseDecimal(string value)
    {
        var raw = (value ?? string.Empty).Trim();
        if (raw.Length == 0) return 0m;

        var negativeByMarker =
            raw.Contains(" DR", StringComparison.OrdinalIgnoreCase) ||
            raw.EndsWith("DR", StringComparison.OrdinalIgnoreCase) ||
            (raw.StartsWith("(", StringComparison.Ordinal) && raw.EndsWith(")", StringComparison.Ordinal));

        var match = Regex.Match(
            raw.Replace(",", string.Empty, StringComparison.Ordinal),
            @"[-+]?\d+(?:\.\d+)?",
            RegexOptions.CultureInvariant);
        if (!match.Success ||
            !decimal.TryParse(match.Value, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
            return 0m;

        if (negativeByMarker && parsed > 0m) parsed = -parsed;
        return parsed;
    }

    private static string CleanText(string? value) =>
        string.Join(
            " ",
            (value ?? string.Empty)
                .Replace("\u0004", string.Empty, StringComparison.Ordinal)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string LocalName(XElement element) =>
        NormalizeName(element.Name.LocalName);

    private static string NormalizeName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static DateTime FinancialYearFallbackStart(string financialYear)
    {
        var digits = new string((financialYear ?? string.Empty).Where(char.IsDigit).ToArray());
        return digits.Length >= 4 && int.TryParse(digits[..4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            ? new DateTime(year, 4, 1)
            : new DateTime(DateTime.Today.Month >= 4 ? DateTime.Today.Year : DateTime.Today.Year - 1, 4, 1);
    }

    private static string XmlEscape(string value) =>
        System.Security.SecurityElement.Escape(value ?? string.Empty)
        ?? string.Empty;

    private static string XmlAttribute(string value) =>
        XmlEscape(value).Replace("\"", "&quot;", StringComparison.Ordinal);

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        private Action? _callback = callback;
        public void Dispose() => Interlocked.Exchange(ref _callback, null)?.Invoke();
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}

public sealed class TallyFullCompanySnapshot
{
    public string CompanyKey { get; init; } = string.Empty;
    public string FinancialYear { get; init; } = string.Empty;
    public DateTime FromDate { get; init; }
    public DateTime ToDate { get; init; }
    public string Xml { get; init; } = string.Empty;
    public IReadOnlyList<TallyVoucherIndexRecord> Vouchers { get; init; } = Array.Empty<TallyVoucherIndexRecord>();
    public IReadOnlyList<TallyVoucherXmlSnapshot> FullVouchers { get; init; } = Array.Empty<TallyVoucherXmlSnapshot>();
    public IReadOnlyDictionary<string, string> LedgerParents { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> GroupNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public int DeclaredGroupCount { get; init; }
    public int DeclaredLedgerCount { get; init; }
    public int DeclaredVoucherCount { get; init; }
    public long AuthorityAlterId { get; init; }
    public DateTime CapturedUtc { get; init; }
}

public sealed class TallyAuthorityDeltaSnapshot
{
    public string Xml { get; init; } = string.Empty;
    public IReadOnlyList<TallyVoucherIndexRecord> Vouchers { get; init; } = Array.Empty<TallyVoucherIndexRecord>();
    public IReadOnlyList<TallyVoucherXmlSnapshot> FullVouchers { get; init; } = Array.Empty<TallyVoucherXmlSnapshot>();
    public IReadOnlyDictionary<string,string> LedgerParents { get; init; } = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> GroupNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public long MaxAlterId { get; init; }
}

public sealed record TallyVoucherXmlSnapshot(
    string MasterId,
    string Guid,
    string VoucherType,
    DateTime? VoucherDate,
    string OriginalXml)
{
    public string AlterId { get; init; } = string.Empty;
}

public sealed class TallyImportResponse
{
    public int Created { get; set; }
    public int Altered { get; set; }
    public int Deleted { get; set; }
    public int Ignored { get; set; }
    public int Exceptions { get; set; }
    public int Errors { get; set; }
    public string LastVoucherId { get; set; } = string.Empty;
    public string LastMasterId { get; set; } = string.Empty;
    public string LineError { get; set; } = string.Empty;
}
