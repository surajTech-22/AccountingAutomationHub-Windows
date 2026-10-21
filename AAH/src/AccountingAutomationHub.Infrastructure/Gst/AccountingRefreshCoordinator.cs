using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Infrastructure.Journals;
using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Infrastructure.Persistence;

namespace AccountingAutomationHub.Infrastructure.Gst;

public sealed class AccountingRefreshCoordinator(
    MasterSynchronizationService masterSynchronizationService,
    GstJournalGenerationService journalGenerationService,
    GstReconciliationService reconciliationService,
    SqliteGstRepository gstRepository,
    SqliteJournalRepository journalRepository)
{
    private readonly Dictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _requestedGeneration = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gateSync = new();

    public event EventHandler? Refreshed;

    public async Task<string> RefreshAsync(
        ClientProfile client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var refreshKey = RefreshKey(client.Id, client.FinancialYear);
        var generation = NextGeneration(refreshKey);

        // Live WPF edits can publish more than one notification in the same input
        // gesture (for example source-change + activity-change).  Give those burst
        // requests a very short coalescing window before taking this Client/FY's accounting
        // gate so only the newest request performs the expensive rebuild. Different
        // Client/FY pairs use different gates and therefore never queue behind each other.
        await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken)
            .ConfigureAwait(false);

        if (!IsLatest(refreshKey, generation))
        {
            return "A newer refresh request replaced this request.";
        }

        var gate = GateFor(refreshKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsLatest(refreshKey, generation))
            {
                return "A newer refresh request replaced this request.";
            }

            var outcome = await Task.Run(
                () => RefreshCore(client, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            // If source data changed while the rebuild was running, do not fan out a
            // stale UI/XML refresh.  The newest queued request will run immediately
            // after this gate is released.
            if (!IsLatest(refreshKey, generation))
            {
                return "Accounting changed again while refresh was running; the newest refresh is queued.";
            }

            Refreshed?.Invoke(this, EventArgs.Empty);
            return outcome;
        }
        finally
        {
            gate.Release();
        }
    }

    private string RefreshCore(
        ClientProfile client,
        CancellationToken cancellationToken)
    {
        using var totalTrace = ApplicationPerformanceTrace.Measure("Accounting.Refresh.Total", $"Client={client.ClientCode};FY={client.FinancialYear}");
        // One-way ownership: source repositories -> Journal -> Masters -> GST.
        // This entire database/accounting pass intentionally runs on a worker thread;
        // no WPF collection or control is touched here.
        var detectedFrequency = ApplyDetectedFilingFrequency(client);
        // Nil-return / Nil-Sales controls are an accounting prerequisite, not a GST-page
        // paint concern. Rebuild them from verified GSTR-3B before every Journal generation
        // so background imports, Sales changes and the Generate Journal command all use the
        // same current Nil-period authority.
        ApplyAutomaticNilSalesControls(client);
        var company = string.IsNullOrWhiteSpace(client.LegalName)
            ? client.ClientName
            : client.LegalName;

        cancellationToken.ThrowIfCancellationRequested();
        int vouchers;
        using (ApplicationPerformanceTrace.Measure("Accounting.Journal.Generate", $"Client={client.ClientCode};FY={client.FinancialYear}"))
            vouchers = journalGenerationService.Generate(client.Id, client.FinancialYear);

        cancellationToken.ThrowIfCancellationRequested();
        int masters;
        using (ApplicationPerformanceTrace.Measure("Accounting.Master.Synchronize", $"Client={client.ClientCode};FY={client.FinancialYear}"))
            masters = masterSynchronizationService.Synchronize(client.Id, client.FinancialYear, company);

        cancellationToken.ThrowIfCancellationRequested();
        int periods;
        using (ApplicationPerformanceTrace.Measure("Accounting.GST.Reconcile", $"Client={client.ClientCode};FY={client.FinancialYear}"))
            periods = reconciliationService.Reconcile(client.Id, client.FinancialYear);

        var frequencyText = detectedFrequency.Length > 0
            ? $", filing frequency {detectedFrequency} from GSTR-3B"
            : string.Empty;
        return $"Refresh completed: {masters:N0} master updates, " +
               $"{vouchers:N0} generated journal periods, " +
               $"{periods:N0} GST reconciliation periods{frequencyText}.";
    }

    public string DetectFilingFrequency(long clientId, string financialYear)
    {
        var returns = gstRepository
            .GetReturns(clientId, financialYear)
            .Where(record => record.ParseValid)
            .ToArray();
        if (returns.Any(record =>
                record.FilingFrequency.Contains("Quarter", StringComparison.OrdinalIgnoreCase) ||
                IsQuarterlyPeriodLabel(record.PeriodLabel) ||
                record.RawFields.TryGetValue("period", out var rawPeriod) &&
                IsQuarterlyPeriodLabel(rawPeriod)) ||
            LooksLikeQuarterlyCadence(returns))
        {
            return "Quarterly";
        }
        return DetectFromReturns(returns);
    }

    public string DetectFilingFrequency(ClientProfile client)
    {
        ArgumentNullException.ThrowIfNull(client);

        var returns = gstRepository
            .GetReturns(client.Id, client.FinancialYear)
            .Where(record => record.ParseValid)
            .ToArray();

        // Explicit quarterly metadata from an imported return is strongest.
        if (returns.Any(record =>
                record.FilingFrequency.Contains("Quarter", StringComparison.OrdinalIgnoreCase) ||
                IsQuarterlyPeriodLabel(record.PeriodLabel) ||
                record.RawFields.TryGetValue("period", out var rawPeriod) &&
                IsQuarterlyPeriodLabel(rawPeriod)))
        {
            return "Quarterly";
        }

        // The client profile is populated from the latest GSTR-3B PDF. This
        // repairs older cached return rows that were previously labelled Monthly.
        if (IsQuarterlyPeriodLabel(client.LatestGstr3bPeriod))
        {
            return "Quarterly";
        }

        // QRMP returns may be stored only under June, September, December and
        // March even when older imports lost the range label. Two or more valid
        // quarter-end filings in one FY are sufficient evidence of quarterly filing.
        if (LooksLikeQuarterlyCadence(returns))
        {
            return "Quarterly";
        }

        return DetectFromReturns(returns);
    }

    private static string DetectFromReturns(
        IReadOnlyCollection<AccountingAutomationHub.Domain.Gst.Gstr3bReturnRecord> returns)
    {
        var frequencies = returns
            .Select(record => record.FilingFrequency.Trim())
            .Where(value => value.Length > 0)
            .ToArray();

        if (frequencies.Any(value =>
                value.Contains("Quarter", StringComparison.OrdinalIgnoreCase)))
        {
            return "Quarterly";
        }
        return frequencies.Any(value =>
            value.Contains("Month", StringComparison.OrdinalIgnoreCase))
                ? "Monthly"
                : string.Empty;
    }

    private static bool LooksLikeQuarterlyCadence(
        IReadOnlyCollection<AccountingAutomationHub.Domain.Gst.Gstr3bReturnRecord> returns)
    {
        var keys = returns
            .Select(record => record.PeriodKey)
            .Where(key => key.Length == 6 && int.TryParse(key.AsSpan(4, 2), out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        if (keys.Length is < 2 or > 4)
        {
            return false;
        }

        return keys.All(key =>
        {
            var month = int.Parse(key.AsSpan(4, 2));
            return month is 3 or 6 or 9 or 12;
        });
    }

    private static bool IsQuarterlyPeriodLabel(string value)
    {
        var normalized = (value ?? string.Empty)
            .Replace('–', '-')
            .Replace('—', '-')
            .Replace('/', '-')
            .Trim();
        if (normalized.Length == 0)
        {
            return false;
        }

        var parts = normalized
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return normalized.Contains(" to ", StringComparison.OrdinalIgnoreCase);
        }

        var start = MonthNumber(parts[0]);
        var end = MonthNumber(parts[1]);
        return (start, end) is (4, 6) or (7, 9) or (10, 12) or (1, 3);
    }

    private static int MonthNumber(string value)
    {
        var token = new string((value ?? string.Empty)
            .Where(char.IsLetter)
            .Take(3)
            .ToArray())
            .ToUpperInvariant();
        return token switch
        {
            "JAN" => 1,
            "FEB" => 2,
            "MAR" => 3,
            "APR" => 4,
            "MAY" => 5,
            "JUN" => 6,
            "JUL" => 7,
            "AUG" => 8,
            "SEP" => 9,
            "OCT" => 10,
            "NOV" => 11,
            "DEC" => 12,
            _ => 0
        };
    }

    public void ApplyAutomaticNilSalesControls(ClientProfile client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var settings = journalRepository.GetSettings(client.Id, client.FinancialYear);
        var existingSelected = GstJournalGenerationService.ParsePipePeriodSet(settings.NilSalesPeriods);
        var closingKey = FinancialYearClosingKey(client.FinancialYear);
        var rows = gstRepository.GetReturns(client.Id, client.FinancialYear)
            .Select(record => new
            {
                Record = record,
                PeriodEndKey = Raw(record, "period_end_key", record.PeriodKey),
                Frequency = NormalizeFrequency(Raw(record, "filing_frequency", record.FilingFrequency)),
                ParseValid = record.ParseValid || IsYes(Raw(record, "parse_valid", string.Empty)),
                LegalNilSource = Raw(record, "nil_detection_source", string.Empty),
                NilSales = IsYes(Raw(record, "is_nil_sales_period", record.IsNilReturn ? "YES" : "NO")),
                NilSalesSource = Raw(record, "nil_sales_detection_source", record.IsNilReturn ? "EXPLICIT NIL RETURN" : string.Empty)
            })
            .Where(row => ValidAutomaticPeriod(row.PeriodEndKey, row.Frequency, closingKey))
            .ToArray();

        var valid = rows
            .Where(row => row.ParseValid || row.LegalNilSource.Equals("EXPLICIT TEXT", StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.PeriodEndKey, StringComparer.Ordinal)
            .ToArray();
        if (valid.Length == 0)
        {
            return;
        }

        var latestFrequency = valid[^1].Frequency;
        if (latestFrequency.Length == 0)
        {
            return;
        }

        // Fix44: GSTR-3B is automatic authority wherever a valid return exists for the
        // period. Manual Nil selections are retained only for periods for which there is
        // no valid GSTR-3B authority yet. This prevents RefreshAsync from erasing a manual
        // fallback selection immediately after the user clicks it, while still allowing a
        // later GSTR-3B to automatically correct/classify that same period.
        var authoritativePeriods = valid
            .Where(row => row.Frequency.Equals(latestFrequency, StringComparison.OrdinalIgnoreCase))
            .Select(row => row.PeriodEndKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        settings.FilingFrequency = latestFrequency;
        journalRepository.SaveSettings(settings);
        var outwardPeriods = journalGenerationService.GetOutwardPeriodKeys(client.Id, client.FinancialYear);
        var nil = existingSelected
            .Where(period => !authoritativePeriods.Contains(period))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in valid)
        {
            if (!row.Frequency.Equals(latestFrequency, StringComparison.OrdinalIgnoreCase) || !row.NilSales)
            {
                continue;
            }
            if (!row.ParseValid && !row.NilSalesSource.Equals("EXPLICIT NIL RETURN", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (outwardPeriods.Contains(row.PeriodEndKey))
            {
                conflicts.Add(row.PeriodEndKey);
                nil.Remove(row.PeriodEndKey);
            }
            else
            {
                nil.Add(row.PeriodEndKey);
            }
        }

        settings = journalRepository.GetSettings(client.Id, client.FinancialYear);
        settings.FilingFrequency = latestFrequency;
        settings.NilSalesPeriods = GstJournalGenerationService.BuildPipePeriodList(nil);
        settings.NilSalesConflicts = GstJournalGenerationService.BuildPipePeriodList(conflicts);
        journalRepository.SaveSettings(settings);
    }

    public IReadOnlyList<AccountingAutomationHub.Domain.Journals.GstNilSalesPeriodOption> GetNilSalesOptions(
        ClientProfile client)
    {
        ArgumentNullException.ThrowIfNull(client);

        var options = journalGenerationService
            .GetNilSalesOptions(client.Id, client.FinancialYear)
            .ToDictionary(option => option.PeriodKey, StringComparer.OrdinalIgnoreCase);
        var settings = journalRepository.GetSettings(client.Id, client.FinancialYear);
        var selected = GstJournalGenerationService.ParsePipePeriodSet(settings.NilSalesPeriods);
        var conflicts = GstJournalGenerationService.ParsePipePeriodSet(settings.NilSalesConflicts);
        var currentFrequency = NormalizeFrequency(settings.FilingFrequency);
        var closingKey = FinancialYearClosingKey(client.FinancialYear);

        // Fix44: a Nil Return/Nil-Sales classification coming from GSTR-3B must be
        // visible even when that month has no Purchase/Input-GST row. Previously the
        // control grid was purchase-driven, so valid portal Nil periods could be saved
        // automatically but remain invisible to the user.
        foreach (var record in gstRepository.GetReturns(client.Id, client.FinancialYear))
        {
            var periodEndKey = Raw(record, "period_end_key", record.PeriodKey);
            var frequency = NormalizeFrequency(Raw(record, "filing_frequency", record.FilingFrequency));
            var parseValid = record.ParseValid || IsYes(Raw(record, "parse_valid", string.Empty));
            var explicitNil = record.IsNilReturn ||
                Raw(record, "nil_detection_source", string.Empty)
                    .Equals("EXPLICIT TEXT", StringComparison.OrdinalIgnoreCase);
            var nilSales = IsYes(Raw(record, "is_nil_sales_period", explicitNil ? "YES" : "NO"));

            if (!nilSales ||
                !ValidAutomaticPeriod(periodEndKey, frequency, closingKey) ||
                currentFrequency.Length > 0 && !frequency.Equals(currentFrequency, StringComparison.OrdinalIgnoreCase) ||
                !parseValid && !explicitNil)
            {
                continue;
            }

            var source = explicitNil
                ? "GSTR-3B Nil Return · automatic"
                : "GSTR-3B Nil Sales · automatic";
            if (options.TryGetValue(periodEndKey, out var option))
            {
                option.IsSelected = selected.Contains(periodEndKey) && !conflicts.Contains(periodEndKey);
                option.IsConflict = conflicts.Contains(periodEndKey);
                option.IsAutomatic = true;
                option.Source = option.IsConflict ? "GSTR-3B / Sales conflict" : source;
                continue;
            }

            options[periodEndKey] = new AccountingAutomationHub.Domain.Journals.GstNilSalesPeriodOption
            {
                PeriodKey = periodEndKey,
                PeriodLabel = GstJournalGenerationService.PeriodLabel(periodEndKey, frequency),
                InputGst = 0m,
                IsSelected = selected.Contains(periodEndKey) && !conflicts.Contains(periodEndKey),
                IsConflict = conflicts.Contains(periodEndKey),
                IsAutomatic = true,
                Source = conflicts.Contains(periodEndKey) ? "GSTR-3B / Sales conflict" : source
            };
        }

        return options.Values
            .OrderBy(option => option.PeriodKey, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<string> SaveNilSalesSelectionsAsync(
        ClientProfile client,
        IEnumerable<string> selectedPeriodKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var settings = journalRepository.GetSettings(client.Id, client.FinancialYear);
        settings.NilSalesPeriods = GstJournalGenerationService.BuildPipePeriodList(selectedPeriodKeys);
        // A manual selection cannot clear a portal-vs-books conflict. Preserve the
        // conflict mirror exactly like the Control Panel registry does.
        journalRepository.SaveSettings(settings);
        return await RefreshAsync(client, cancellationToken);
    }

    private static string Raw(
        AccountingAutomationHub.Domain.Gst.Gstr3bReturnRecord record,
        string key,
        string fallback) =>
        record.RawFields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback.Trim();

    private static bool IsYes(string value) =>
        value.Equals("YES", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("1", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeFrequency(string value)
    {
        if (value.Contains("Quarter", StringComparison.OrdinalIgnoreCase)) return "Quarterly";
        if (value.Contains("Month", StringComparison.OrdinalIgnoreCase)) return "Monthly";
        return string.Empty;
    }

    private static bool ValidAutomaticPeriod(string periodEndKey, string frequency, string closingKey) =>
        periodEndKey.Length == 6 && periodEndKey.All(char.IsDigit) &&
        (frequency.Equals("Monthly", StringComparison.OrdinalIgnoreCase) ||
         frequency.Equals("Quarterly", StringComparison.OrdinalIgnoreCase)) &&
        string.CompareOrdinal(periodEndKey, closingKey) <= 0;

    private static string FinancialYearClosingKey(string financialYear)
    {
        var digits = new string((financialYear ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length >= 6 && int.TryParse(digits[..4], out var startYear))
        {
            var endText = digits[4..];
            var endYear = endText.Length >= 4 && int.TryParse(endText[..4], out var fullEnd)
                ? fullEnd
                : endText.Length >= 2 && int.TryParse(endText[..2], out var shortEnd)
                    ? startYear / 100 * 100 + shortEnd
                    : startYear + 1;
            if (endYear <= startYear) endYear += 100;
            return $"{endYear:0000}03";
        }
        return "999912";
    }

    private string ApplyDetectedFilingFrequency(ClientProfile client)
    {
        var detected = DetectFilingFrequency(client);
        if (detected.Length == 0)
        {
            return string.Empty;
        }

        var settings = journalRepository.GetSettings(client.Id, client.FinancialYear);
        if (!settings.FilingFrequency.Equals(detected, StringComparison.OrdinalIgnoreCase))
        {
            settings.FilingFrequency = detected;
            journalRepository.SaveSettings(settings);
        }
        return detected;
    }

    private SemaphoreSlim GateFor(string refreshKey)
    {
        lock (_gateSync)
        {
            if (_gates.TryGetValue(refreshKey, out var gate)) return gate;
            gate = new SemaphoreSlim(1, 1);
            _gates[refreshKey] = gate;
            return gate;
        }
    }

    private long NextGeneration(string refreshKey)
    {
        lock (_requestedGeneration)
        {
            var next = _requestedGeneration.TryGetValue(refreshKey, out var current)
                ? current + 1
                : 1;
            _requestedGeneration[refreshKey] = next;
            return next;
        }
    }

    private bool IsLatest(string refreshKey, long generation)
    {
        lock (_requestedGeneration)
        {
            return _requestedGeneration.TryGetValue(refreshKey, out var current) &&
                   current == generation;
        }
    }

    private static string RefreshKey(long clientId, string financialYear) =>
        $"{clientId}\u001f{financialYear.Trim().ToUpperInvariant()}";
}
