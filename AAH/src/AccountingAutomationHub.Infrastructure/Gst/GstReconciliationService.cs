using AccountingAutomationHub.Domain.Gst;
using AccountingAutomationHub.Domain.Journals;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Infrastructure.Journals;
using AccountingAutomationHub.Infrastructure.Purchases;

namespace AccountingAutomationHub.Infrastructure.Gst;

public sealed class GstReconciliationService(
    SqliteGstRepository gstRepository,
    SqliteJournalRepository journalRepository,
    SqlitePurchaseRecordRepository purchaseRepository)
{
    private const decimal Tolerance = 0.005m;

    public int Reconcile(long clientId, string financialYear)
    {
        var settings = journalRepository.GetSettings(clientId, financialYear);
        var filingFrequency = string.IsNullOrWhiteSpace(settings.FilingFrequency)
            ? "Monthly"
            : settings.FilingFrequency;
        var rawReturns = gstRepository.GetReturns(clientId, financialYear);
        var rawLedgers = gstRepository.GetLedgerPeriods(clientId, financialYear);
        var aggregatedReturns = AggregateReturns(rawReturns, filingFrequency);
        var aggregatedLedgers = AggregateLedgerPeriods(rawLedgers, filingFrequency);

        IReadOnlyList<PurchaseImportRecord> purchases;
        try
        {
            purchases = purchaseRepository.GetRecords(clientId, financialYear);
        }
        catch
        {
            purchases = [];
        }

        var returns = aggregatedReturns.ToDictionary(
            row => row.PeriodKey,
            StringComparer.OrdinalIgnoreCase);
        var vouchers = journalRepository.GetVouchers(clientId, financialYear)
            .GroupBy(
                voucher => NormalizeReportingKey(
                    string.IsNullOrWhiteSpace(voucher.PeriodKey)
                        ? GstJournalGenerationService.PeriodKey(voucher.VoucherDate, filingFrequency)
                        : voucher.PeriodKey,
                    filingFrequency),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var rcmBooks = BuildRcmBooks(purchases, filingFrequency);

        var credit = aggregatedLedgers
            .Where(row => row.LedgerType.Contains("Credit", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(row => row.PeriodKey, StringComparer.OrdinalIgnoreCase);
        var cash = aggregatedLedgers
            .Where(row => row.LedgerType.Contains("Cash", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(row => row.PeriodKey, StringComparer.OrdinalIgnoreCase);

        var periods = returns.Keys
            .Concat(vouchers.Keys)
            .Concat(rcmBooks.Keys)
            .Concat(credit.Keys)
            .Concat(cash.Keys)
            .Where(key => key.Length == 6)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        var rows = new List<GstReconciliationRow>();
        var booksNetWithOpening = settings.OpeningBooksBalance;

        foreach (var period in periods)
        {
            returns.TryGetValue(period, out var portal);
            vouchers.TryGetValue(period, out var journalRows);
            rcmBooks.TryGetValue(period, out var rcm);
            credit.TryGetValue(period, out var creditRow);
            cash.TryGetValue(period, out var cashRow);
            journalRows ??= [];
            rcm ??= new RcmBooksPeriod();

            // The workbook keeps normal outward liability, RCM liability and ITC separate.
            var booksOutput = journalRows.Sum(voucher =>
                voucher.OutputIgst + voucher.OutputCgst + voucher.OutputSgst);
            var booksItc = journalRows.Sum(voucher =>
                voucher.InputIgst + voucher.InputCgst + voucher.InputSgst);
            var booksRcmTaxable = rcm.Taxable;
            var booksRcmGst = rcm.Gst;

            // Journal input already includes only RCM ITC claimed in this reporting period.
            var periodBooksNet = booksItc - booksOutput - booksRcmGst;
            booksNetWithOpening += periodBooksNet;

            var portalOutput = portal?.OutwardGst ?? 0m;
            var portalRcmTaxable = portal?.RcmTaxable ?? 0m;
            var portalRcmGst = portal?.RcmGst ?? 0m;
            var portalItc = portal?.NetItc ?? 0m;
            var portalNet = portalItc - portalOutput - portalRcmGst;

            var portalOpening =
                (creditRow?.OpeningTotal ?? 0m) -
                (cashRow?.OpeningTotal ?? 0m);
            var portalLedgerBalance =
                (creditRow?.ClosingTotal ?? 0m) -
                (cashRow?.ClosingTotal ?? 0m);

            // Main workbook reconciliation includes all Table 6.1 tax heads,
            // including Cess. The visible Table 6.1 summary still shows the
            // workbook's IGST + CGST + SGST total columns.
            var taxPayable = portal?.AllTaxPayableTotal ?? 0m;
            var itcUsed = portal?.AllCreditUsedTotal ?? 0m;
            var cashPaid = portal?.AllCashPaidTotal ?? 0m;
            var interest = portal?.PaymentInterest ?? 0m;
            var lateFee = portal?.PaymentLateFee ?? 0m;
            var rcmCash = portal?.RcmCash ?? 0m;
            var offsetTotal = portal?.OffsetTotal ?? 0m;
            var paymentDifference =
                taxPayable + interest + lateFee - offsetTotal;

            // GSTR-3B books matching and portal-ledger matching are two separate
            // controls. Missing Credit/Cash ledger files must not turn an otherwise
            // correct 3B-vs-books period into a false 3B failure. Only compare the
            // running books balance with portal ledgers when both ledger periods are
            // actually present.
            var gstr3bDifference =
                Math.Abs(booksOutput - portalOutput) +
                Math.Abs(booksRcmTaxable - portalRcmTaxable) +
                Math.Abs(booksRcmGst - portalRcmGst) +
                Math.Abs(booksItc - portalItc) +
                Math.Abs(paymentDifference);
            var hasCreditLedger = creditRow is not null;
            var hasCashLedger = cashRow is not null;
            var ledgerDifference = hasCreditLedger && hasCashLedger
                ? Math.Abs(booksNetWithOpening - portalLedgerBalance)
                : 0m;
            var finalDifference = gstr3bDifference + ledgerDifference;

            var status = DetermineStatus(
                portal,
                hasCreditLedger,
                hasCashLedger,
                gstr3bDifference,
                ledgerDifference);

            rows.Add(new GstReconciliationRow
            {
                ClientId = clientId,
                FinancialYear = financialYear,
                PeriodKey = period,
                PeriodLabel = GstJournalGenerationService.PeriodLabel(period, filingFrequency),
                BooksOutput = Round(booksOutput),
                PortalOutput = Round(portalOutput),
                BooksRcmTaxable = Round(booksRcmTaxable),
                PortalRcmTaxable = Round(portalRcmTaxable),
                BooksRcmLiabilityGst = Round(booksRcmGst),
                PortalRcmGst = Round(portalRcmGst),
                BooksItc = Round(booksItc),
                PortalItc = Round(portalItc),
                PortalNet = Round(portalNet),
                BooksNetWithOpening = Round(booksNetWithOpening),
                PortalOpening = Round(portalOpening),
                PortalLedgerBalance = Round(portalLedgerBalance),
                BooksPosition = BooksPosition(booksNetWithOpening),
                TaxPayable = Round(taxPayable),
                ItcUsed = Round(itcUsed),
                CashPaid = Round(cashPaid),
                Interest = Round(interest),
                LateFee = Round(lateFee),
                RcmCash = Round(rcmCash),
                OffsetTotal = Round(offsetTotal),
                PaymentDifference = Round(paymentDifference),
                Status = status,
                HasJournal = journalRows.Length > 0,
                HasReturn = portal is not null,
                HasCreditLedger = creditRow is not null,
                HasCashLedger = cashRow is not null,
                ReviewNotes = BuildNotes(
                    portal,
                    creditRow,
                    cashRow,
                    filingFrequency,
                    rcm,
                    finalDifference),
                RefreshedUtc = DateTime.UtcNow
            });
        }

        gstRepository.ReplaceReconciliation(clientId, financialYear, rows);
        return rows.Count;
    }

    public static IReadOnlyList<Gstr3bReturnRecord> AggregateReturns(
        IReadOnlyList<Gstr3bReturnRecord> source,
        string filingFrequency)
    {
        return source
            .Where(row => row.PeriodKey.Length == 6)
            .GroupBy(
                row => NormalizeReportingKey(row.PeriodKey, filingFrequency),
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                // Apply the quarterly-over-monthly preference *inside each
                // registration*, not across GSTINs. One quarterly registration
                // must never suppress another registration's monthly return.
                IReadOnlyList<Gstr3bReturnRecord> selected = group
                    .GroupBy(row => row.Gstin, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(registration =>
                    {
                        var values = registration.OrderBy(row => row.ImportedUtc).ToArray();
                        if (filingFrequency.Contains("Quarter", StringComparison.OrdinalIgnoreCase))
                        {
                            var quarterly = values.Where(row =>
                                row.FilingFrequency.Contains("Quarter", StringComparison.OrdinalIgnoreCase))
                                .ToArray();
                            if (quarterly.Length > 0) return quarterly;
                        }
                        return values;
                    })
                    .OrderBy(row => row.ImportedUtc)
                    .ToArray();

                var identity = selected.Last();
                var periodKey = group.Key;
                return new Gstr3bReturnRecord
                {
                    ClientId = identity.ClientId,
                    FinancialYear = identity.FinancialYear,
                    Gstin = identity.Gstin,
                    LegalName = identity.LegalName,
                    TradeName = identity.TradeName,
                    PeriodKey = periodKey,
                    PeriodLabel = GstJournalGenerationService.PeriodLabel(periodKey, filingFrequency),
                    FilingFrequency = filingFrequency,
                    Arn = string.Join(
                        "; ",
                        selected.Select(row => row.Arn)
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Distinct(StringComparer.OrdinalIgnoreCase)),
                    ArnDate = selected.Select(row => row.ArnDate).LastOrDefault(value => value.HasValue),
                    OutwardTaxable = selected.Sum(row => row.OutwardTaxable),
                    OutwardIgst = selected.Sum(row => row.OutwardIgst),
                    OutwardCgst = selected.Sum(row => row.OutwardCgst),
                    OutwardSgst = selected.Sum(row => row.OutwardSgst),
                    OutwardCess = selected.Sum(row => row.OutwardCess),
                    RcmTaxable = selected.Sum(row => row.RcmTaxable),
                    RcmIgst = selected.Sum(row => row.RcmIgst),
                    RcmCgst = selected.Sum(row => row.RcmCgst),
                    RcmSgst = selected.Sum(row => row.RcmSgst),
                    RcmCess = selected.Sum(row => row.RcmCess),
                    NetItcIgst = selected.Sum(row => row.NetItcIgst),
                    NetItcCgst = selected.Sum(row => row.NetItcCgst),
                    NetItcSgst = selected.Sum(row => row.NetItcSgst),
                    NetItcCess = selected.Sum(row => row.NetItcCess),
                    CompositionExemptNilInterState = selected.Sum(row => row.CompositionExemptNilInterState),
                    CompositionExemptNilIntraState = selected.Sum(row => row.CompositionExemptNilIntraState),
                    NonGstInterState = selected.Sum(row => row.NonGstInterState),
                    NonGstIntraState = selected.Sum(row => row.NonGstIntraState),
                    SystemInterestIgst = selected.Sum(row => row.SystemInterestIgst),
                    SystemInterestCgst = selected.Sum(row => row.SystemInterestCgst),
                    SystemInterestSgst = selected.Sum(row => row.SystemInterestSgst),
                    SystemInterestCess = selected.Sum(row => row.SystemInterestCess),
                    InterestPaidIgst = selected.Sum(row => row.InterestPaidIgst),
                    InterestPaidCgst = selected.Sum(row => row.InterestPaidCgst),
                    InterestPaidSgst = selected.Sum(row => row.InterestPaidSgst),
                    InterestPaidCess = selected.Sum(row => row.InterestPaidCess),
                    LateFeeIgst = selected.Sum(row => row.LateFeeIgst),
                    LateFeeCgst = selected.Sum(row => row.LateFeeCgst),
                    LateFeeSgst = selected.Sum(row => row.LateFeeSgst),
                    LateFeeCess = selected.Sum(row => row.LateFeeCess),
                    PaymentRows = AggregatePaymentRows(selected.SelectMany(row => row.PaymentRows)),
                    LiabilityBreakup = AggregateLiabilityBreakup(
                        selected.SelectMany(row => row.LiabilityBreakup)),
                    RawFields = AggregateRawFields(selected, periodKey,
                        GstJournalGenerationService.PeriodLabel(periodKey, filingFrequency)),
                    IsNilReturn = selected.All(row => row.IsNilReturn),
                    ParseValid = selected.All(row => row.ParseValid),
                    ParseNotes = string.Join(
                        " ",
                        selected.Select(row => row.ParseNotes)
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Distinct(StringComparer.OrdinalIgnoreCase)),
                    SourcePath = string.Join(
                        "; ",
                        selected.Select(row => row.SourcePath)
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Distinct(StringComparer.OrdinalIgnoreCase)),
                    RawText = string.Join(
                        "\n\n",
                        selected.Select(row =>
                            $"===== {row.SourceFileName} =====\n{row.RawText}")),
                    ImportedUtc = selected.Max(row => row.ImportedUtc),
                    LastEditedUtc = selected.Max(row => row.LastEditedUtc)
                };
            })
            .OrderBy(row => row.PeriodKey, StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<GstLedgerPeriodBalance> AggregateLedgerPeriods(
        IReadOnlyList<GstLedgerPeriodBalance> source,
        string filingFrequency)
    {
        return source
            .Where(row => row.PeriodKey.Length == 6)
            .GroupBy(
                row => $"{row.LedgerType}|{NormalizeReportingKey(row.PeriodKey, filingFrequency)}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group
                    .OrderBy(row => row.PeriodKey, StringComparer.Ordinal)
                    .ThenBy(row => row.ImportedUtc)
                    .ToArray();
                var first = ordered.First();
                var last = ordered.Last();
                // Each GSTIN has an independent opening and closing ledger
                // position. Summing the first registration's opening with the
                // last registration's closing would silently lose balances.
                var eachRegistration = ordered
                    .GroupBy(row => row.Gstin, StringComparer.OrdinalIgnoreCase)
                    .Select(registration =>
                    {
                        var sorted = registration.OrderBy(row => row.PeriodKey, StringComparer.Ordinal)
                            .ThenBy(row => row.ImportedUtc).ToArray();
                        return (First: sorted.First(), Last: sorted.Last());
                    })
                    .ToArray();
                var reportingKey = NormalizeReportingKey(last.PeriodKey, filingFrequency);
                return new GstLedgerPeriodBalance
                {
                    ClientId = first.ClientId,
                    FinancialYear = first.FinancialYear,
                    LedgerType = first.LedgerType,
                    PeriodKey = reportingKey,
                    PeriodLabel = GstJournalGenerationService.PeriodLabel(reportingKey, filingFrequency),
                    OpeningIgst = eachRegistration.Sum(registration => registration.First.OpeningIgst),
                    OpeningCgst = eachRegistration.Sum(registration => registration.First.OpeningCgst),
                    OpeningSgst = eachRegistration.Sum(registration => registration.First.OpeningSgst),
                    OpeningCess = eachRegistration.Sum(registration => registration.First.OpeningCess),
                    ClosingIgst = eachRegistration.Sum(registration => registration.Last.ClosingIgst),
                    ClosingCgst = eachRegistration.Sum(registration => registration.Last.ClosingCgst),
                    ClosingSgst = eachRegistration.Sum(registration => registration.Last.ClosingSgst),
                    ClosingCess = eachRegistration.Sum(registration => registration.Last.ClosingCess),
                    SourcePath = string.Join(
                        "; ",
                        ordered.Select(row => row.SourcePath)
                            .Where(path => !string.IsNullOrWhiteSpace(path))
                            .Distinct(StringComparer.OrdinalIgnoreCase)),
                    ImportedUtc = ordered.Max(row => row.ImportedUtc)
                };
            })
            .OrderBy(row => row.PeriodKey, StringComparer.Ordinal)
            .ThenBy(row => row.LedgerType, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static List<Gstr3bLiabilityBreakupRow> AggregateLiabilityBreakup(
        IEnumerable<Gstr3bLiabilityBreakupRow> rows) =>
        rows.GroupBy(row => row.Period, StringComparer.OrdinalIgnoreCase)
            .Select(group => new Gstr3bLiabilityBreakupRow
            {
                Period = group.Key,
                Igst = group.Sum(row => row.Igst),
                Cgst = group.Sum(row => row.Cgst),
                Sgst = group.Sum(row => row.Sgst),
                Cess = group.Sum(row => row.Cess)
            })
            .OrderBy(row => LiabilityPeriodKey(row.Period), StringComparer.Ordinal)
            .ToList();

    private static Dictionary<string, string> AggregateRawFields(
        IReadOnlyList<Gstr3bReturnRecord> rows,
        string periodKey,
        string periodLabel)
    {
        var keys = rows.SelectMany(row => row.RawFields.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            var values = rows
                .Select(row => row.RawFields.TryGetValue(key, out var value) ? value : string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            if (values.Length == 0)
            {
                result[key] = string.Empty;
                continue;
            }

            if (IsRawIdentityList(key))
            {
                result[key] = string.Join(
                    "; ",
                    values.Distinct(StringComparer.OrdinalIgnoreCase));
                continue;
            }

            if (values.All(value => decimal.TryParse(
                    value,
                    System.Globalization.NumberStyles.Number |
                    System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out _)))
            {
                var total = values.Sum(value => decimal.Parse(
                    value,
                    System.Globalization.NumberStyles.Number |
                    System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture));
                result[key] = total.ToString(
                    "0.00########",
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                result[key] = values.Last();
            }
        }

        result["period"] = periodLabel;
        result["month_key"] = periodKey;
        return result;
    }

    private static bool IsRawIdentityList(string key) =>
        key.Equals("file_name", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("file_path", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("arn", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("arn_date", StringComparison.OrdinalIgnoreCase);

    private static string LiabilityPeriodKey(string value)
    {
        if (DateTime.TryParseExact(
                value,
                ["MMMM yyyy", "MMM yyyy"],
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var date))
        {
            return date.ToString("yyyyMM");
        }
        return value;
    }

    private static List<Gstr3bPaymentRow> AggregatePaymentRows(
        IEnumerable<Gstr3bPaymentRow> rows) =>
        rows.GroupBy(
                row => $"{row.Section}|{row.TaxHead}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new Gstr3bPaymentRow
            {
                Section = group.First().Section,
                TaxHead = group.First().TaxHead,
                TaxPayable = group.Sum(row => row.TaxPayable),
                NegativeLiabilityAdjustment = group.Sum(row => row.NegativeLiabilityAdjustment),
                NetTaxPayable = group.Sum(row => row.NetTaxPayable),
                ItcIntegrated = group.Sum(row => row.ItcIntegrated),
                ItcCentral = group.Sum(row => row.ItcCentral),
                ItcState = group.Sum(row => row.ItcState),
                ItcCess = group.Sum(row => row.ItcCess),
                PaidCash = group.Sum(row => row.PaidCash),
                InterestPaidCash = group.Sum(row => row.InterestPaidCash),
                LateFeePaidCash = group.Sum(row => row.LateFeePaidCash)
            })
            .OrderBy(row => row.Section, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => TaxHeadOrder(row.TaxHead))
            .ToList();

    private static Dictionary<string, RcmBooksPeriod> BuildRcmBooks(
        IReadOnlyList<PurchaseImportRecord> purchases,
        string filingFrequency)
    {
        var result = new Dictionary<string, RcmBooksPeriod>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in purchases.Where(row =>
                     row.Destination.Equals("RCM", StringComparison.OrdinalIgnoreCase)))
        {
            // RCM books liability/claim follows the Purchase GST Date only.
            var sourceDate = row.GstDate;
            if (!sourceDate.HasValue)
            {
                continue;
            }

            var sign = row.DocumentType.Contains("Debit Note", StringComparison.OrdinalIgnoreCase)
                ? -1m
                : 1m;
            var liabilityKey = GstJournalGenerationService.PeriodKey(
                sourceDate.Value,
                filingFrequency);
            var liability = GetRcmPeriod(result, liabilityKey);
            liability.Taxable += sign * row.TaxableValue;
            liability.Igst += sign * row.Igst;
            liability.Cgst += sign * row.Cgst;
            liability.Sgst += sign * row.Sgst;

            var treatment = PurchaseTreatmentPolicy.NormalizeRcmTreatment(row.GstApplicability);
            if (treatment == PurchaseTreatmentPolicy.RcmReversed)
            {
                continue;
            }
            var claimDate = treatment == PurchaseTreatmentPolicy.RcmNextMonth
                ? sourceDate.Value.AddMonths(1)
                : sourceDate.Value;
            var claimKey = GstJournalGenerationService.PeriodKey(claimDate, filingFrequency);
            var claim = GetRcmPeriod(result, claimKey);
            claim.ItcIgst += sign * row.Igst;
            claim.ItcCgst += sign * row.Cgst;
            claim.ItcSgst += sign * row.Sgst;
        }
        return result;
    }

    private static RcmBooksPeriod GetRcmPeriod(
        IDictionary<string, RcmBooksPeriod> periods,
        string key)
    {
        if (!periods.TryGetValue(key, out var period))
        {
            period = new RcmBooksPeriod();
            periods[key] = period;
        }
        return period;
    }

    private static string NormalizeReportingKey(string periodKey, string filingFrequency)
    {
        if (periodKey.Length != 6 ||
            !int.TryParse(periodKey[..4], out var year) ||
            !int.TryParse(periodKey[4..], out var month) ||
            month is < 1 or > 12)
        {
            return periodKey;
        }
        return GstJournalGenerationService.PeriodKey(
            new DateTime(year, month, 1),
            filingFrequency);
    }

    private static string DetermineStatus(
        Gstr3bReturnRecord? portal,
        bool hasCreditLedger,
        bool hasCashLedger,
        decimal gstr3bDifference,
        decimal ledgerDifference)
    {
        if (portal is null)
        {
            return "3B Missing";
        }
        if (gstr3bDifference > Tolerance)
        {
            return "Mismatch";
        }
        if (!hasCreditLedger || !hasCashLedger)
        {
            return "Matched / Ledger Missing";
        }
        return ledgerDifference <= Tolerance ? "Matched" : "Mismatch";
    }

    private static string BuildNotes(
        Gstr3bReturnRecord? portal,
        GstLedgerPeriodBalance? credit,
        GstLedgerPeriodBalance? cash,
        string filingFrequency,
        RcmBooksPeriod rcm,
        decimal finalDifference)
    {
        var notes = new List<string>();
        if (portal is null)
        {
            notes.Add("GSTR-3B return is not imported for this reporting period.");
        }
        else if (!portal.ParseValid)
        {
            notes.Add(portal.ParseNotes);
        }
        if (credit is null)
        {
            notes.Add("Electronic Credit Ledger period is not available.");
        }
        if (cash is null)
        {
            notes.Add("Electronic Cash Ledger period is not available.");
        }
        if (rcm.Taxable != 0m || rcm.Gst != 0m || rcm.ItcGst != 0m)
        {
            notes.Add(
                $"Books RCM: taxable {rcm.Taxable:N2}, liability {rcm.Gst:N2}, " +
                $"claimed ITC {rcm.ItcGst:N2}.");
        }
        if (filingFrequency.Contains("Quarter", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("All three source months are grouped to the quarter-end reporting key.");
        }
        if (finalDifference > Tolerance)
        {
            notes.Add($"Combined workbook difference: {finalDifference:N2}.");
        }
        return string.Join(" ", notes.Where(note => !string.IsNullOrWhiteSpace(note)));
    }

    private static string BooksPosition(decimal balance)
    {
        if (Math.Abs(balance) <= Tolerance)
        {
            return "Nil";
        }
        return balance < 0m
            ? $"Payable {Math.Abs(balance):N2}"
            : $"Receivable {balance:N2}";
    }

    private static int TaxHeadOrder(string taxHead) =>
        taxHead.ToUpperInvariant() switch
        {
            "IGST" => 1,
            "CGST" => 2,
            "SGST" => 3,
            "CESS" => 4,
            _ => 9
        };

    private static decimal Round(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private sealed class RcmBooksPeriod
    {
        public decimal Taxable { get; set; }
        public decimal Igst { get; set; }
        public decimal Cgst { get; set; }
        public decimal Sgst { get; set; }
        public decimal ItcIgst { get; set; }
        public decimal ItcCgst { get; set; }
        public decimal ItcSgst { get; set; }
        public decimal Gst => Igst + Cgst + Sgst;
        public decimal ItcGst => ItcIgst + ItcCgst + ItcSgst;
    }
}
