using AccountingAutomationHub.Domain.Journals;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Domain.Sales;
using AccountingAutomationHub.Infrastructure.Purchases;
using AccountingAutomationHub.Infrastructure.Sales;

namespace AccountingAutomationHub.Infrastructure.Journals;

/// <summary>
/// Rebuilds the automatic GST adjustment voucher from the confirmed RCM Purchase:
/// the Purchase already posts rate-wise RCM Input Dr and RCM Payable Cr.
/// Journal treatment is driven only by GST Applicability:
/// Same Month takes credit directly from the same RCM Input ledger;
/// Next Month moves RCM Input to RCM Input Receivable in the source month and takes
/// credit directly from that Receivable in the next month; Reversed moves RCM Input
/// to one shared RCM Input Expenses account. RCM is never moved to normal Input GST.
/// </summary>
public sealed class GstJournalGenerationService(
    SqliteJournalRepository journalRepository,
    SqlitePurchaseRecordRepository purchaseRepository,
    SqliteSalesRecordRepository salesRepository)
{
    public int Generate(long clientId, string financialYear)
    {
        var settings = journalRepository.GetSettings(clientId, financialYear);
        IReadOnlyList<PurchaseImportRecord> purchases;
        IReadOnlyList<SalesImportRecord> sales;

        try { purchases = purchaseRepository.GetRecords(clientId, financialYear); }
        catch { purchases = []; }

        try { sales = salesRepository.GetAutomaticRecords(clientId, financialYear); }
        catch { sales = []; }

        var periods = new Dictionary<string, PeriodTotals>(StringComparer.OrdinalIgnoreCase);

        // Exact GetCommonMonths parity from modgstadjustment:
        // 1) every outward-tax period is compulsory;
        // 2) Purchase/Debit-Note-only periods are included only when the same
        //    period has outward tax OR GST Control Panel marks it Nil Sales;
        // 3) RCM liability/claim/reversal periods remain independent.
        var outwardPeriods = BuildOutwardPeriods(sales, settings.FilingFrequency);
        var nilSalesPeriods = ParsePipePeriodSet(settings.NilSalesPeriods);

        foreach (var row in purchases)
        {
            // Retained GSTR-2B-only rows are reconciliation evidence, not books.
            // Keep this destination gate in addition to the treatment-policy gate so
            // a stale/legacy applicability value can never leak an unpromoted 2B row
            // into Books ITC or an automatic GST Journal voucher.
            if (row.Destination.Equals("2B Only", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Purchase/RCM/Debit Note/Debit Note Reverse accounting period is authoritative from GST Date.
            // Do not fall back to Document Date: deleting or editing a row must affect
            // only the GST reporting period carried by that row's GST Date.
            var sourceDate = row.GstDate;
            if (!sourceDate.HasValue)
            {
                continue;
            }

            var sourcePeriodKey = PeriodKey(sourceDate.Value, settings.FilingFrequency);
            // Existing Debit Note rows are supplier credit notes / purchase returns and
            // reduce Input GST. Debit Note Reverse rows are supplier-issued debit notes
            // from GSTR-1/2B and increase Input GST exactly like a Purchase.
            var sign = PurchaseTreatmentPolicy.NormalInputGstSign(row);

            if (row.Destination.Equals("RCM", StringComparison.OrdinalIgnoreCase))
            {
                var liabilityPeriod = Get(periods, sourcePeriodKey);
                liabilityPeriod.RcmIgst += sign * row.Igst;
                liabilityPeriod.RcmCgst += sign * row.Cgst;
                liabilityPeriod.RcmSgst += sign * row.Sgst;

                var treatment = PurchaseTreatmentPolicy.NormalizeRcmTreatment(row.GstApplicability);
                if (treatment == PurchaseTreatmentPolicy.RcmNextMonth)
                {
                    // Source month: move the existing RCM Input asset to RCM Input
                    // Receivable. The next month takes the claim directly from that
                    // Receivable; it does not restore or use a normal Input GST ledger.
                    AddRcmNextMonthLiability(liabilityPeriod, row, sign, settings);

                    // Workbook rule: move one calendar month first, then map the
                    // resulting month into its monthly/quarterly reporting period.
                    var claimPeriodKey = PeriodKey(
                        sourceDate.Value.AddMonths(1),
                        settings.FilingFrequency);
                    AddRcmNextMonthClaim(Get(periods, claimPeriodKey), row, sign, settings);
                }
                else if (treatment == PurchaseTreatmentPolicy.RcmReversed)
                {
                    AddRcmReverse(liabilityPeriod, row, sign, settings);
                }
                else
                {
                    AddRcmSameMonth(liabilityPeriod, row, sign, settings);
                }
                continue;
            }

            // Excel parity: normal Purchase / Debit Note / Debit Note Reverse input GST enters the
            // GST Journal only when the row is eligible for ITC.  Rows marked
            // Not shown in 2B or GST included in cost stay on the Purchase
            // source but must not flow into GSTR-3B Books ITC.  RCM is handled
            // independently above through its own liability/claim treatment.
            if (!PurchaseTreatmentPolicy.IsNormalInputGstEligible(row.GstApplicability))
            {
                continue;
            }

            if (!outwardPeriods.Contains(sourcePeriodKey) &&
                !nilSalesPeriods.Contains(sourcePeriodKey))
            {
                // Excel leaves an input-only period out until Nil Filing is
                // explicitly selected (or GSTR-3B auto-control selects it).
                continue;
            }

            AddNormalInput(Get(periods, sourcePeriodKey), row, sign);
        }

        foreach (var row in sales)
        {
            var sourceDate = row.GstDate ?? row.DocumentDate;
            if (!sourceDate.HasValue)
            {
                continue;
            }

            var periodKey = PeriodKey(sourceDate.Value, settings.FilingFrequency);
            var sign = row.Destination.Equals("Credit Note", StringComparison.OrdinalIgnoreCase)
                ? -1m
                : 1m;
            var totals = Get(periods, periodKey);

            AddLedgerAmount(
                totals.OutputLines,
                ResolveLedger(row.IgstLedger, "Output IGST"),
                "IGST",
                sign * row.Igst);
            AddLedgerAmount(
                totals.OutputLines,
                ResolveLedger(row.CgstLedger, "Output CGST"),
                "CGST",
                sign * row.Cgst);
            AddLedgerAmount(
                totals.OutputLines,
                ResolveLedger(row.SgstLedger, "Output SGST"),
                "SGST",
                sign * row.Sgst);

            totals.OutputIgst += sign * row.Igst;
            totals.OutputCgst += sign * row.Cgst;
            totals.OutputSgst += sign * row.Sgst;
        }

        var vouchers = periods
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select((pair, index) =>
                BuildVoucher(clientId, financialYear, pair.Key, pair.Value, settings, index + 1))
            .Where(voucher => voucher.Lines.Count > 0)
            .ToArray();

        return journalRepository.ReplaceGenerated(clientId, financialYear, vouchers);
    }

    public IReadOnlySet<string> GetOutwardPeriodKeys(long clientId, string financialYear)
    {
        var settings = journalRepository.GetSettings(clientId, financialYear);
        IReadOnlyList<SalesImportRecord> sales;
        try { sales = salesRepository.GetAutomaticRecords(clientId, financialYear); }
        catch { sales = []; }
        return BuildOutwardPeriods(sales, settings.FilingFrequency);
    }

    public IReadOnlyList<GstNilSalesPeriodOption> GetNilSalesOptions(
        long clientId,
        string financialYear)
    {
        var settings = journalRepository.GetSettings(clientId, financialYear);
        IReadOnlyList<PurchaseImportRecord> purchases;
        IReadOnlyList<SalesImportRecord> sales;
        try { purchases = purchaseRepository.GetRecords(clientId, financialYear); }
        catch { purchases = []; }
        try { sales = salesRepository.GetAutomaticRecords(clientId, financialYear); }
        catch { sales = []; }

        var outward = BuildOutwardPeriods(sales, settings.FilingFrequency);
        var selected = ParsePipePeriodSet(settings.NilSalesPeriods);
        var conflicts = ParsePipePeriodSet(settings.NilSalesConflicts);
        var inputTotals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in purchases)
        {
            // RCM periods are independent of Nil-Sales selection.  For normal
            // Purchase / Debit Note / Debit Note Reverse, only ITC-eligible rows should make a
            // Purchase-only period appear in the Nil Sales control, matching
            // the workbook's GSTJournalSourceRowEligible rule.
            if (row.Destination.Equals("RCM", StringComparison.OrdinalIgnoreCase) ||
                !PurchaseTreatmentPolicy.IsNormalInputGstEligible(row.GstApplicability))
            {
                continue;
            }

            var sourceDate = row.GstDate;
            if (!sourceDate.HasValue || PurchaseTaxTotal(row) <= 0.005m)
            {
                continue;
            }
            var period = PeriodKey(sourceDate.Value, settings.FilingFrequency);
            inputTotals[period] = inputTotals.TryGetValue(period, out var amount)
                ? amount + PurchaseTaxTotal(row)
                : PurchaseTaxTotal(row);
        }

        return inputTotals
            .Where(pair => !outward.Contains(pair.Key) || conflicts.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new GstNilSalesPeriodOption
            {
                PeriodKey = pair.Key,
                PeriodLabel = PeriodLabel(pair.Key, settings.FilingFrequency),
                InputGst = Math.Round(pair.Value, 2, MidpointRounding.AwayFromZero),
                IsSelected = selected.Contains(pair.Key),
                IsConflict = conflicts.Contains(pair.Key),
                Source = conflicts.Contains(pair.Key)
                    ? "GSTR-3B / Sales conflict"
                    : selected.Contains(pair.Key) ? "GST Control Panel" : "Purchase / Debit Note"
            })
            .ToArray();
    }

    public static HashSet<string> ParsePipePeriodSet(string? value) =>
        (value ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(key => key.Length == 6 && key.All(char.IsDigit))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string BuildPipePeriodList(IEnumerable<string> periodKeys)
    {
        var keys = periodKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Where(key => key.Length == 6 && key.All(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        return keys.Length == 0 ? "|" : $"|{string.Join("|", keys)}|";
    }

    private static HashSet<string> BuildOutwardPeriods(
        IReadOnlyCollection<SalesImportRecord> sales,
        string filingFrequency)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in sales)
        {
            var sourceDate = row.GstDate ?? row.DocumentDate;
            if (!sourceDate.HasValue || SalesTaxTotal(row) <= 0.005m)
            {
                continue;
            }
            result.Add(PeriodKey(sourceDate.Value, filingFrequency));
        }
        return result;
    }

    private static decimal PurchaseTaxTotal(PurchaseImportRecord row) =>
        Math.Abs(row.Igst) + Math.Abs(row.Cgst) + Math.Abs(row.Sgst) + Math.Abs(row.Cess);

    private static decimal SalesTaxTotal(SalesImportRecord row) =>
        Math.Abs(row.Igst) + Math.Abs(row.Cgst) + Math.Abs(row.Sgst) + Math.Abs(row.Cess);

    private static JournalVoucher BuildVoucher(
        long clientId,
        string financialYear,
        string periodKey,
        PeriodTotals totals,
        JournalGenerationSettings settings,
        int sequence)
    {
        var normalInputIgst = Round(totals.NormalInputIgst);
        var normalInputCgst = Round(totals.NormalInputCgst);
        var normalInputSgst = Round(totals.NormalInputSgst);
        var rcmClaimIgst = Round(totals.RcmClaimIgst);
        var rcmClaimCgst = Round(totals.RcmClaimCgst);
        var rcmClaimSgst = Round(totals.RcmClaimSgst);

        var voucher = new JournalVoucher
        {
            ClientId = clientId,
            FinancialYear = financialYear,
            PeriodKey = periodKey,
            PeriodLabel = PeriodLabel(periodKey, settings.FilingFrequency),
            VoucherDate = PeriodEndDate(periodKey),
            VoucherNumber = $"GST-{periodKey}-{sequence:00}",
            Narration = $"GST adjustment for {PeriodLabel(periodKey, settings.FilingFrequency)}",
            GenerationMode = "Automatic",
            OutputIgst = Round(totals.OutputIgst),
            OutputCgst = Round(totals.OutputCgst),
            OutputSgst = Round(totals.OutputSgst),
            RcmIgst = Round(totals.RcmIgst),
            RcmCgst = Round(totals.RcmCgst),
            RcmSgst = Round(totals.RcmSgst),
            NormalInputIgst = normalInputIgst,
            NormalInputCgst = normalInputCgst,
            NormalInputSgst = normalInputSgst,
            RcmClaimIgst = rcmClaimIgst,
            RcmClaimCgst = rcmClaimCgst,
            RcmClaimSgst = rcmClaimSgst,
            RcmReverseIgst = Round(totals.RcmReverseIgst),
            RcmReverseCgst = Round(totals.RcmReverseCgst),
            RcmReverseSgst = Round(totals.RcmReverseSgst),
            InputIgst = Round(normalInputIgst + rcmClaimIgst),
            InputCgst = Round(normalInputCgst + rcmClaimCgst),
            InputSgst = Round(normalInputSgst + rcmClaimSgst)
        };

        var lines = new List<JournalLine>();

        // Standard workbook adjustment: Output GST Dr, Input GST Cr.
        AddGroupedLines(lines, totals.OutputLines, "Dr", "Output Tax");
        AddGroupedLines(lines, totals.NormalInputLines, "Cr", "Normal Input Tax");

        // RCM applicability controls only these lines:
        // - Same Month: take credit directly from the same RCM Input ledger (Cr).
        // - Next Month source: RCM Input Receivable Dr / same RCM Input Cr.
        // - Next Month claim: take credit directly from RCM Input Receivable (Cr).
        // - Reversed: one RCM Input Expenses Dr / same RCM Input Cr.
        AddGroupedLines(lines, totals.RcmInputClaimCreditLines, "Cr", "RCM Input Claim");
        AddGroupedLines(lines, totals.RcmReceivableClaimCreditLines, "Cr", "RCM Receivable Claim");
        AddGroupedLines(lines, totals.RcmReceivableDebitLines, "Dr", "RCM Input Receivable");
        AddGroupedLines(lines, totals.RcmReceivableTransferInputCreditLines, "Cr", "RCM Input -> Receivable");
        AddGroupedLines(lines, totals.RcmReverseExpenseLines, "Dr", "RCM Input Expenses");
        AddGroupedLines(lines, totals.RcmReverseInputCreditLines, "Cr", "RCM Input -> Expenses");

        // Same-month and next-month claims participate in GST settlement because the
        // claim is taken directly from RCM Input / RCM Input Receivable respectively.
        // Next-month source reclassification and Reversed remain self-balancing.
        if (settings.SettlementLedgerMode.Equals(
                "Separate Ledgers",
                StringComparison.OrdinalIgnoreCase))
        {
            AddSettlementLine(
                lines,
                GstMasterClassificationPolicy.EffectiveIgstSettlementLedger(settings),
                voucher.OutputIgst - voucher.InputIgst,
                "IGST");
            AddSettlementLine(
                lines,
                GstMasterClassificationPolicy.EffectiveCgstSettlementLedger(settings),
                voucher.OutputCgst - voucher.InputCgst,
                "CGST");
            AddSettlementLine(
                lines,
                GstMasterClassificationPolicy.EffectiveSgstSettlementLedger(settings),
                voucher.OutputSgst - voucher.InputSgst,
                "SGST");
        }
        else
        {
            var normalNet =
                voucher.OutputIgst + voucher.OutputCgst + voucher.OutputSgst -
                voucher.InputIgst - voucher.InputCgst - voucher.InputSgst;
            AddSettlementLine(
                lines,
                GstMasterClassificationPolicy.EffectiveSingleSettlementLedger(settings),
                normalNet,
                "GST");
        }

        for (var index = 0; index < lines.Count; index++)
        {
            lines[index].LineNumber = index + 1;
        }
        voucher.Lines = lines;
        return voucher;
    }

    private static void AddNormalInput(
        PeriodTotals totals,
        PurchaseImportRecord row,
        decimal sign)
    {
        AddLedgerAmount(
            totals.NormalInputLines,
            ResolveLedger(row.IgstLedger, "Input IGST"),
            "IGST",
            sign * row.Igst);
        AddLedgerAmount(
            totals.NormalInputLines,
            ResolveLedger(row.CgstLedger, "Input CGST"),
            "CGST",
            sign * row.Cgst);
        AddLedgerAmount(
            totals.NormalInputLines,
            ResolveLedger(row.SgstLedger, "Input SGST"),
            "SGST",
            sign * row.Sgst);

        totals.NormalInputIgst += sign * row.Igst;
        totals.NormalInputCgst += sign * row.Cgst;
        totals.NormalInputSgst += sign * row.Sgst;
    }

    private static void AddRcmSameMonth(
        PeriodTotals totals,
        PurchaseImportRecord row,
        decimal sign,
        JournalGenerationSettings settings)
    {
        // Same month: take the credit directly from the same RCM Input ledger created
        // by the source Purchase. There is no transfer to a normal Input GST ledger.
        AddRcmClaimFromInput(totals, row, sign, settings);

        totals.RcmClaimIgst += sign * row.Igst;
        totals.RcmClaimCgst += sign * row.Cgst;
        totals.RcmClaimSgst += sign * row.Sgst;
    }

    private static void AddRcmNextMonthLiability(
        PeriodTotals totals,
        PurchaseImportRecord row,
        decimal sign,
        JournalGenerationSettings settings)
    {
        AddLedgerAmount(
            totals.RcmReceivableDebitLines,
            PurchaseTreatmentPolicy.BuildRcmReceivableLedgerName("IGST", row.Rate),
            "IGST",
            sign * row.Igst);
        AddLedgerAmount(
            totals.RcmReceivableDebitLines,
            PurchaseTreatmentPolicy.BuildRcmReceivableLedgerName("CGST", row.Rate / 2m),
            "CGST",
            sign * row.Cgst);
        AddLedgerAmount(
            totals.RcmReceivableDebitLines,
            PurchaseTreatmentPolicy.BuildRcmReceivableLedgerName("SGST", row.Rate / 2m),
            "SGST",
            sign * row.Sgst);

        AddRcmInputCredit(totals.RcmReceivableTransferInputCreditLines, row, sign, settings);
    }

    private static void AddRcmNextMonthClaim(
        PeriodTotals totals,
        PurchaseImportRecord row,
        decimal sign,
        JournalGenerationSettings settings)
    {
        _ = settings;

        // Claim month: take credit directly from the Receivable created in the source
        // month. Do not debit RCM Input again and do not use normal Input GST.
        AddLedgerAmount(
            totals.RcmReceivableClaimCreditLines,
            PurchaseTreatmentPolicy.BuildRcmReceivableLedgerName("IGST", row.Rate),
            "IGST",
            sign * row.Igst);
        AddLedgerAmount(
            totals.RcmReceivableClaimCreditLines,
            PurchaseTreatmentPolicy.BuildRcmReceivableLedgerName("CGST", row.Rate / 2m),
            "CGST",
            sign * row.Cgst);
        AddLedgerAmount(
            totals.RcmReceivableClaimCreditLines,
            PurchaseTreatmentPolicy.BuildRcmReceivableLedgerName("SGST", row.Rate / 2m),
            "SGST",
            sign * row.Sgst);

        totals.RcmClaimIgst += sign * row.Igst;
        totals.RcmClaimCgst += sign * row.Cgst;
        totals.RcmClaimSgst += sign * row.Sgst;
    }

    private static void AddRcmReverse(
        PeriodTotals totals,
        PurchaseImportRecord row,
        decimal sign,
        JournalGenerationSettings settings)
    {
        // One shared expense account for every tax head/rate.
        AddLedgerAmount(
            totals.RcmReverseExpenseLines,
            PurchaseTreatmentPolicy.RcmInputExpensesLedger,
            "RCM",
            sign * (row.Igst + row.Cgst + row.Sgst));

        AddRcmInputCredit(totals.RcmReverseInputCreditLines, row, sign, settings);

        totals.RcmReverseIgst += sign * row.Igst;
        totals.RcmReverseCgst += sign * row.Cgst;
        totals.RcmReverseSgst += sign * row.Sgst;
    }

    private static void AddRcmClaimFromInput(
        PeriodTotals totals,
        PurchaseImportRecord row,
        decimal sign,
        JournalGenerationSettings settings)
    {
        AddLedgerAmount(
            totals.RcmInputClaimCreditLines,
            PurchaseTreatmentPolicy.BuildRcmInputLedgerName(
                row.IgstLedger, settings.TemporaryRcmIgstLedger, "IGST", row.Rate),
            "IGST",
            sign * row.Igst);
        AddLedgerAmount(
            totals.RcmInputClaimCreditLines,
            PurchaseTreatmentPolicy.BuildRcmInputLedgerName(
                row.CgstLedger, settings.TemporaryRcmCgstLedger, "CGST", row.Rate / 2m),
            "CGST",
            sign * row.Cgst);
        AddLedgerAmount(
            totals.RcmInputClaimCreditLines,
            PurchaseTreatmentPolicy.BuildRcmInputLedgerName(
                row.SgstLedger, settings.TemporaryRcmSgstLedger, "SGST", row.Rate / 2m),
            "SGST",
            sign * row.Sgst);
    }

    private static void AddRcmInputCredit(
        IDictionary<LedgerTaxKey, decimal> target,
        PurchaseImportRecord row,
        decimal sign,
        JournalGenerationSettings settings)
    {
        AddLedgerAmount(
            target,
            PurchaseTreatmentPolicy.BuildRcmInputLedgerName(
                row.IgstLedger, settings.TemporaryRcmIgstLedger, "IGST", row.Rate),
            "IGST",
            sign * row.Igst);
        AddLedgerAmount(
            target,
            PurchaseTreatmentPolicy.BuildRcmInputLedgerName(
                row.CgstLedger, settings.TemporaryRcmCgstLedger, "CGST", row.Rate / 2m),
            "CGST",
            sign * row.Cgst);
        AddLedgerAmount(
            target,
            PurchaseTreatmentPolicy.BuildRcmInputLedgerName(
                row.SgstLedger, settings.TemporaryRcmSgstLedger, "SGST", row.Rate / 2m),
            "SGST",
            sign * row.Sgst);
    }

    private static void AddGroupedLines(
        ICollection<JournalLine> lines,
        IReadOnlyDictionary<LedgerTaxKey, decimal> grouped,
        string positiveDirection,
        string role)
    {
        foreach (var pair in grouped
                     .Where(pair => Round(pair.Value) != 0m)
                     .OrderBy(pair => pair.Key.TaxHead, StringComparer.Ordinal)
                     .ThenBy(pair => pair.Key.LedgerName, StringComparer.OrdinalIgnoreCase))
        {
            AddSignedLine(
                lines,
                pair.Key.LedgerName,
                pair.Value,
                positiveDirection,
                pair.Key.TaxHead,
                role);
        }
    }

    private static void AddLedgerAmount(
        IDictionary<LedgerTaxKey, decimal> grouped,
        string ledgerName,
        string taxHead,
        decimal amount)
    {
        amount = Round(amount);
        if (amount == 0m)
        {
            return;
        }

        var key = new LedgerTaxKey(ledgerName.Trim(), taxHead.Trim().ToUpperInvariant());
        grouped[key] = grouped.TryGetValue(key, out var current)
            ? current + amount
            : amount;
    }

    private static void AddSignedLine(
        ICollection<JournalLine> lines,
        string ledger,
        decimal amount,
        string positiveDirection,
        string taxHead,
        string role)
    {
        amount = Round(amount);
        if (amount == 0m)
        {
            return;
        }

        lines.Add(new JournalLine
        {
            LedgerName = ledger,
            Amount = Math.Abs(amount),
            DrCr = amount > 0m ? positiveDirection : Opposite(positiveDirection),
            TaxHead = taxHead,
            LineRole = role
        });
    }

    private static void AddSettlementLine(
        ICollection<JournalLine> lines,
        string ledger,
        decimal payable,
        string taxHead)
    {
        payable = Round(payable);
        if (payable == 0m)
        {
            return;
        }

        lines.Add(new JournalLine
        {
            LedgerName = ledger,
            Amount = Math.Abs(payable),
            DrCr = payable > 0m ? "Cr" : "Dr",
            TaxHead = taxHead,
            LineRole = payable > 0m ? "Settlement Payable" : "Settlement Receivable"
        });
    }

    public static string PeriodKey(DateTime date, string filingFrequency)
    {
        if (filingFrequency.Contains("Quarter", StringComparison.OrdinalIgnoreCase))
        {
            var endMonth = ((date.Month - 1) / 3 + 1) * 3;
            return $"{date.Year:0000}{endMonth:00}";
        }
        return $"{date.Year:0000}{date.Month:00}";
    }

    public static string NextPeriodKey(string periodKey, string filingFrequency)
    {
        var date = PeriodStartDate(periodKey, filingFrequency);
        return PeriodKey(
            filingFrequency.Contains("Quarter", StringComparison.OrdinalIgnoreCase)
                ? date.AddMonths(3)
                : date.AddMonths(1),
            filingFrequency);
    }

    public static DateTime PeriodStartDate(string periodKey, string filingFrequency)
    {
        if (periodKey.Length != 6 ||
            !int.TryParse(periodKey[..4], out var year) ||
            !int.TryParse(periodKey[4..], out var month))
        {
            return DateTime.Today;
        }

        if (filingFrequency.Contains("Quarter", StringComparison.OrdinalIgnoreCase))
        {
            month = Math.Max(1, month - 2);
        }
        return new DateTime(year, month, 1);
    }

    public static string PeriodLabel(string periodKey, string filingFrequency)
    {
        var start = PeriodStartDate(periodKey, filingFrequency);
        if (filingFrequency.Contains("Quarter", StringComparison.OrdinalIgnoreCase))
        {
            var end = start.AddMonths(2);
            return $"{start:MMM-yy} to {end:MMM-yy}";
        }
        return start.ToString("MMM yyyy");
    }

    public static DateTime PeriodEndDate(string periodKey)
    {
        if (periodKey.Length != 6 ||
            !int.TryParse(periodKey[..4], out var year) ||
            !int.TryParse(periodKey[4..], out var month) ||
            month is < 1 or > 12)
        {
            return DateTime.Today;
        }

        return new DateTime(year, month, 1).AddMonths(1).AddDays(-1);
    }

    private static PeriodTotals Get(
        IDictionary<string, PeriodTotals> periods,
        string periodKey)
    {
        if (!periods.TryGetValue(periodKey, out var totals))
        {
            totals = new PeriodTotals();
            periods[periodKey] = totals;
        }
        return totals;
    }

    private static string ResolveLedger(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();


    private static string Opposite(string direction) =>
        direction.Equals("Dr", StringComparison.OrdinalIgnoreCase) ? "Cr" : "Dr";

    private static decimal Round(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private readonly record struct LedgerTaxKey(string LedgerName, string TaxHead);

    private sealed class PeriodTotals
    {
        public Dictionary<LedgerTaxKey, decimal> OutputLines { get; } = [];
        public Dictionary<LedgerTaxKey, decimal> NormalInputLines { get; } = [];
        public Dictionary<LedgerTaxKey, decimal> RcmInputClaimCreditLines { get; } = [];
        public Dictionary<LedgerTaxKey, decimal> RcmReceivableClaimCreditLines { get; } = [];
        public Dictionary<LedgerTaxKey, decimal> RcmReceivableDebitLines { get; } = [];
        public Dictionary<LedgerTaxKey, decimal> RcmReceivableTransferInputCreditLines { get; } = [];
        public Dictionary<LedgerTaxKey, decimal> RcmReverseExpenseLines { get; } = [];
        public Dictionary<LedgerTaxKey, decimal> RcmReverseInputCreditLines { get; } = [];

        public decimal OutputIgst { get; set; }
        public decimal OutputCgst { get; set; }
        public decimal OutputSgst { get; set; }
        public decimal RcmIgst { get; set; }
        public decimal RcmCgst { get; set; }
        public decimal RcmSgst { get; set; }
        public decimal NormalInputIgst { get; set; }
        public decimal NormalInputCgst { get; set; }
        public decimal NormalInputSgst { get; set; }
        public decimal RcmClaimIgst { get; set; }
        public decimal RcmClaimCgst { get; set; }
        public decimal RcmClaimSgst { get; set; }
        public decimal RcmReverseIgst { get; set; }
        public decimal RcmReverseCgst { get; set; }
        public decimal RcmReverseSgst { get; set; }
    }
}
