using System.Collections.Concurrent;
using AccountingAutomationHub.Application.Banking;
using AccountingAutomationHub.Application.Clients;
using AccountingAutomationHub.Application.Journals;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Application.Purchases;
using AccountingAutomationHub.Application.Sales;
using AccountingAutomationHub.Domain.Accounting;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Journals;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Domain.Sales;

namespace AccountingAutomationHub.Infrastructure.Accounting;

/// <summary>
/// Central ledger-total authority for the desktop application.
///
/// The service projects the same accounting shape used by the Tally export paths:
/// Bank + Cash + Purchase/RCM/Debit Note + Sales/Credit Note + Journal.  The complete
/// client/FY projection is cached; typing in a Ledger picker therefore never performs
/// repository queries. Call Invalidate after a committed accounting/master change.
/// </summary>
public sealed class LedgerTotalService
{
    private const decimal Tolerance = 0.005m;

    private readonly IBankingRepository _bankingRepository;
    private readonly IPurchaseRecordRepository _purchaseRepository;
    private readonly ISalesRecordRepository _salesRepository;
    private readonly IJournalRepository _journalRepository;
    private readonly IMasterRepository _masterRepository;
    private readonly IClientPreferenceRepository _clientPreferences;
    private readonly ConcurrentDictionary<string, Lazy<LedgerBookCache>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public LedgerTotalService(
        IBankingRepository bankingRepository,
        IPurchaseRecordRepository purchaseRepository,
        ISalesRecordRepository salesRepository,
        IJournalRepository journalRepository,
        IMasterRepository masterRepository,
        IClientPreferenceRepository clientPreferences)
    {
        _bankingRepository = bankingRepository;
        _purchaseRepository = purchaseRepository;
        _salesRepository = salesRepository;
        _journalRepository = journalRepository;
        _masterRepository = masterRepository;
        _clientPreferences = clientPreferences;
    }

    public void Invalidate(long clientId, string financialYear) =>
        _cache.TryRemove(CacheKey(clientId, financialYear), out _);

    public IReadOnlyList<LedgerTotalSnapshot> GetAll(long clientId, string financialYear)
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear)) return [];
        var book = GetBook(clientId, financialYear);
        return book.Ledgers.Values
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => CreateSnapshot(book, item, book.ToDate))
            .ToArray();
    }

    public IReadOnlyList<LedgerTotalSnapshot> GetAllForPeriod(
        long clientId,
        string financialYear,
        DateTime fromDate,
        DateTime toDate)
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear)) return [];
        var book = GetBook(clientId, financialYear);
        var requestedFrom = fromDate.Date <= toDate.Date ? fromDate.Date : toDate.Date;
        var requestedTo = fromDate.Date <= toDate.Date ? toDate.Date : fromDate.Date;
        if (requestedTo < book.FromDate || requestedFrom > book.ToDate) return [];
        var from = requestedFrom < book.FromDate ? book.FromDate : requestedFrom;
        var to = requestedTo > book.ToDate ? book.ToDate : requestedTo;

        return book.Ledgers.Values
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => CreatePeriodSnapshot(book, item, from, to))
            .ToArray();
    }

    public LedgerTotalSnapshot? Get(
        long clientId,
        string financialYear,
        string? ledgerName,
        DateTime? asOnDate = null)
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear) || string.IsNullOrWhiteSpace(ledgerName))
            return null;

        var book = GetBook(clientId, financialYear);
        var name = CanonicalLedgerName(book, ledgerName);
        if (!book.Ledgers.TryGetValue(name, out var ledger)) return null;
        return CreateSnapshot(book, ledger, asOnDate ?? book.ToDate);
    }

    public DateTime? GetLastMovementDate(long clientId, string financialYear, string? ledgerName)
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear) || string.IsNullOrWhiteSpace(ledgerName)) return null;
        var book = GetBook(clientId, financialYear);
        var name = CanonicalLedgerName(book, ledgerName);
        return book.Ledgers.TryGetValue(name, out var ledger) && ledger.Movements.Count > 0
            ? ledger.Movements.Max(item => item.Date).Date
            : null;
    }

    public IReadOnlyList<LedgerMovementDetail> GetMovements(
        long clientId,
        string financialYear,
        string? ledgerName,
        DateTime? fromDate = null,
        DateTime? toDate = null)
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear) || string.IsNullOrWhiteSpace(ledgerName)) return [];
        var book = GetBook(clientId, financialYear);
        var name = CanonicalLedgerName(book, ledgerName);
        if (!book.Ledgers.TryGetValue(name, out var ledger)) return [];

        var from = (fromDate ?? book.FromDate).Date;
        var to = (toDate ?? book.ToDate).Date;
        if (from < book.FromDate) from = book.FromDate;
        if (to > book.ToDate) to = book.ToDate;
        if (to < from) return [];

        var running = ledger.OpeningBalance;
        var result = new List<LedgerMovementDetail>();
        foreach (var movement in ledger.Movements
                     .OrderBy(item => item.Date)
                     .ThenBy(item => LedgerMovementDisplayPriority(item.SourceKey))
                     .ThenBy(item => item.SourceKey, StringComparer.OrdinalIgnoreCase))
        {
            running = Round(running + movement.Debit - movement.Credit);
            if (movement.Date < from || movement.Date > to) continue;
            result.Add(new LedgerMovementDetail
            {
                Date = movement.Date,
                VoucherType = movement.VoucherType,
                VoucherNumber = movement.VoucherNumber,
                ReferenceNumber = movement.ReferenceNumber,
                Particulars = movement.Particulars,
                Narration = movement.Narration,
                Source = movement.SourceKey,
                Debit = movement.Debit,
                Credit = movement.Credit,
                RunningBalance = running
            });
        }
        return result;
    }

    private static int LedgerMovementDisplayPriority(string? sourceKey)
    {
        var source = (sourceKey ?? string.Empty).Trim();
        // Fix34: when a source voucher and its same-day Advanced Closure reverse share a date,
        // display/apply the source first. Alphabetical CASH-before-SALES ordering made the running
        // balance look as though the receipt/payment happened before the amount arose.
        if (source.StartsWith("SALES|", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("PURCHASE|", StringComparison.OrdinalIgnoreCase)) return 10;
        if (source.StartsWith("BANK|", StringComparison.OrdinalIgnoreCase)) return 20;
        if (source.StartsWith("CASH|ACLOSE-", StringComparison.OrdinalIgnoreCase) ||
            (source.StartsWith("JOURNAL|", StringComparison.OrdinalIgnoreCase) &&
             source.Contains("ACLOSE-", StringComparison.OrdinalIgnoreCase))) return 90;
        if (source.StartsWith("CASH|", StringComparison.OrdinalIgnoreCase)) return 30;
        if (source.StartsWith("JOURNAL|", StringComparison.OrdinalIgnoreCase)) return 40;
        return 50;
    }

    private LedgerBookCache GetBook(long clientId, string financialYear)
    {
        var key = CacheKey(clientId, financialYear);
        var lazy = _cache.GetOrAdd(
            key,
            _ => new Lazy<LedgerBookCache>(
                () => Build(clientId, financialYear),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        catch
        {
            _cache.TryRemove(key, out _);
            throw;
        }
    }

    private LedgerBookCache Build(long clientId, string financialYear)
    {
        var (fromDate, toDate) = FinancialYearRange(financialYear);
        var masters = Safe(() => _masterRepository.Get(clientId, financialYear, false, string.Empty), Array.Empty<MasterRecord>());
        var ledgers = BuildLedgerIndex(masters);
        var book = new LedgerBookCache(clientId, financialYear, fromDate, toDate, ledgers);

        AddBankPostings(book);
        AddCashPostings(book);
        AddPurchasePostings(book);
        AddSalesPostings(book);
        AddJournalPostings(book);

        foreach (var ledger in book.Ledgers.Values)
        {
            ledger.Movements.Sort(static (left, right) =>
            {
                var date = left.Date.CompareTo(right.Date);
                return date != 0 ? date : string.Compare(left.SourceKey, right.SourceKey, StringComparison.OrdinalIgnoreCase);
            });
        }

        return book;
    }

    private Dictionary<string, LedgerAccumulator> BuildLedgerIndex(IReadOnlyList<MasterRecord> masters)
    {
        var result = new Dictionary<string, LedgerAccumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in masters
                     .Where(item => item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                    !item.IsArchived &&
                                    !string.IsNullOrWhiteSpace(item.Name))
                     .GroupBy(item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            // Duplicate Master review remains a Master-Centre responsibility.  For the
            // read-only total projection choose one authority row deterministically and
            // never sum duplicate opening balances.
            var authority = group
                .OrderByDescending(item => (item.Source ?? string.Empty).Contains("Tally", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(item => item.OpeningBalance.HasValue)
                .ThenBy(item => item.PersistentId)
                .First();
            var name = authority.Name.Trim();
            result[name] = new LedgerAccumulator(
                name,
                authority.Parent.Trim(),
                authority.OpeningBalance ?? 0m);
        }
        return result;
    }

    private void AddBankPostings(LedgerBookCache book)
    {
        var accounts = Safe(
                () => _bankingRepository.GetAccounts(book.ClientId, book.FinancialYear),
                Array.Empty<BankAccountRecord>())
            .ToDictionary(item => item.Id);
        var transactions = Safe(
            () => _bankingRepository.GetTransactions(book.ClientId, book.FinancialYear, includeArchived: false),
            Array.Empty<BankTransactionRecord>());
        var defaultEmptyLedger = ReadDefaultEmptyLedger(book.ClientId, book.FinancialYear);

        foreach (var transaction in transactions)
        {
            var date = transaction.TransactionDate.Date;
            if (date < book.FromDate || date > book.ToDate || transaction.Amount <= Tolerance) continue;
            if (!accounts.TryGetValue(transaction.BankAccountId, out var account)) continue;

            var bankLedger = CanonicalLedgerName(book, account.AuthorityName);
            var counterpart = NormalizeCounterpartLedger(transaction.LedgerName, defaultEmptyLedger);
            counterpart = CanonicalLedgerName(book, counterpart);
            if (bankLedger.Length == 0 || counterpart.Length == 0) continue;

            var voucherType = (transaction.Mode ?? string.Empty).Trim();
            var mode = voucherType.ToUpperInvariant();
            var source = $"BANK|{transaction.Id}|{transaction.TransactionId}|{date:yyyyMMdd}";
            switch (mode)
            {
                case "PAYMENT":
                    AddPosting(book, counterpart, date, transaction.Amount, 0m, source + "|COUNTERPART", voucherType, transaction.ReferenceNumber, transaction.ReferenceNumber, bankLedger, transaction.Narration);
                    AddPosting(book, bankLedger, date, 0m, transaction.Amount, source + "|BANK", voucherType, transaction.ReferenceNumber, transaction.ReferenceNumber, counterpart, transaction.Narration);
                    break;
                case "RECEIPT":
                    AddPosting(book, counterpart, date, 0m, transaction.Amount, source + "|COUNTERPART", voucherType, transaction.ReferenceNumber, transaction.ReferenceNumber, bankLedger, transaction.Narration);
                    AddPosting(book, bankLedger, date, transaction.Amount, 0m, source + "|BANK", voucherType, transaction.ReferenceNumber, transaction.ReferenceNumber, counterpart, transaction.Narration);
                    break;
                case "CONTRA" when transaction.Credit > 0m:
                    AddPosting(book, bankLedger, date, transaction.Amount, 0m, source + "|BANK", voucherType, transaction.ReferenceNumber, transaction.ReferenceNumber, counterpart, transaction.Narration);
                    AddPosting(book, counterpart, date, 0m, transaction.Amount, source + "|COUNTERPART", voucherType, transaction.ReferenceNumber, transaction.ReferenceNumber, bankLedger, transaction.Narration);
                    break;
                case "CONTRA":
                    AddPosting(book, counterpart, date, transaction.Amount, 0m, source + "|COUNTERPART", voucherType, transaction.ReferenceNumber, transaction.ReferenceNumber, bankLedger, transaction.Narration);
                    AddPosting(book, bankLedger, date, 0m, transaction.Amount, source + "|BANK", voucherType, transaction.ReferenceNumber, transaction.ReferenceNumber, counterpart, transaction.Narration);
                    break;
            }
        }
    }

    private void AddCashPostings(LedgerBookCache book)
    {
        var records = Safe(
            () => _bankingRepository.GetCashEntries(book.ClientId, book.FinancialYear),
            Array.Empty<CashEntryRecord>());

        foreach (var row in records.Where(item => item.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase)))
        {
            var date = row.EntryDate.Date;
            if (date < book.FromDate || date > book.ToDate) continue;
            var amount = Round(row.Amount);
            if (amount <= Tolerance) continue;

            var transactionLedger = CanonicalLedgerName(
                book,
                (row.MappedMasterLedger.Length > 0 ? row.MappedMasterLedger : row.EntryLedger).Trim());
            var commonLedger = CanonicalLedgerName(book, row.CommonLedger);
            if (transactionLedger.Length == 0 || commonLedger.Length == 0) continue;

            var transactionGroup = ParentGroup(book, transactionLedger);
            var commonGroup = ParentGroup(book, commonLedger);
            if (IsCashOrBankGroup(transactionGroup) && IsCashOrBankGroup(commonGroup)) continue;

            var direction = row.Debit > 0m ? "DR" : row.Credit > 0m ? "CR" : row.SingleDrCr.Trim().ToUpperInvariant();
            if (direction is not ("DR" or "CR")) continue;
            if (ResolveCashVoucherType(commonGroup, direction, row.VoucherType).Length == 0) continue;

            var identity = row.EntryId.Trim().Length > 0 ? row.EntryId.Trim() : row.Id.ToString();
            var source = $"CASH|{identity}|{date:yyyyMMdd}";
            // Fix31: ACLOSE EntryId remains the durable internal/Tally migration identity,
            // while the ledger UI shows the compact human reference (CR-xxxxxx-nn / CP-xxxxxx-nn).
            var visibleVoucherNumber = (row.SourceRunId ?? string.Empty).StartsWith("ADV-CLOSE|", StringComparison.OrdinalIgnoreCase) &&
                                       !string.IsNullOrWhiteSpace(row.ReferenceNumber)
                ? row.ReferenceNumber.Trim()
                : row.EntryId;
            if (direction == "DR")
            {
                AddPosting(book, transactionLedger, date, amount, 0m, source + "|TRANSACTION", row.VoucherType, visibleVoucherNumber, row.ReferenceNumber, commonLedger, row.Narration);
                AddPosting(book, commonLedger, date, 0m, amount, source + "|COMMON", row.VoucherType, visibleVoucherNumber, row.ReferenceNumber, transactionLedger, row.Narration);
            }
            else
            {
                AddPosting(book, transactionLedger, date, 0m, amount, source + "|TRANSACTION", row.VoucherType, visibleVoucherNumber, row.ReferenceNumber, commonLedger, row.Narration);
                AddPosting(book, commonLedger, date, amount, 0m, source + "|COMMON", row.VoucherType, visibleVoucherNumber, row.ReferenceNumber, transactionLedger, row.Narration);
            }
        }
    }

    private void AddPurchasePostings(LedgerBookCache book)
    {
        var records = Safe(
            () => _purchaseRepository.GetRecords(book.ClientId, book.FinancialYear),
            Array.Empty<PurchaseImportRecord>());
        var settings = Safe(
            () => _journalRepository.GetSettings(book.ClientId, book.FinancialYear),
            new JournalGenerationSettings { ClientId = book.ClientId, FinancialYear = book.FinancialYear });

        var groups = records
            .Where(row => row.GstDate is DateTime date && date.Date >= book.FromDate && date.Date <= book.ToDate)
            .GroupBy(row => new
            {
                VoucherType = NormalizePurchaseDestination(row.Destination, row.DocumentType),
                row.DocumentNumber,
                VoucherDate = row.GstDate!.Value.Date,
                ReferenceDate = row.DocumentDate?.Date,
                Party = row.SupplierName.Trim(),
                IsRcm = row.Destination.Equals("RCM", StringComparison.OrdinalIgnoreCase) ||
                        row.ReverseCharge.Equals("Y", StringComparison.OrdinalIgnoreCase) ||
                        row.ReverseCharge.Equals("Yes", StringComparison.OrdinalIgnoreCase)
            });

        foreach (var group in groups)
        {
            var rows = group.ToArray();
            if (group.Key.Party.Length == 0 || group.Key.DocumentNumber.Trim().Length == 0 || group.Key.ReferenceDate is null) continue;
            if (!HasRequiredPurchaseLedgers(rows, group.Key.IsRcm)) continue;

            var entries = BuildPurchaseEntries(book, rows, group.Key.VoucherType, group.Key.Party, group.Key.IsRcm, settings);
            if (entries.Count == 0 || !IsBalanced(entries)) continue;
            var source = $"PURCHASE|{group.Key.VoucherType}|{group.Key.VoucherDate:yyyyMMdd}|{group.Key.Party}|{group.Key.DocumentNumber}";
            AddEntries(book, group.Key.VoucherDate, entries, source, group.Key.VoucherType, group.Key.DocumentNumber, group.Key.DocumentNumber, group.Key.Party, rows.Select(row => row.DifferenceDetails).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? string.Empty);
        }
    }

    private void AddSalesPostings(LedgerBookCache book)
    {
        var records = Safe(
            () => _salesRepository.GetRecords(book.ClientId, book.FinancialYear),
            Array.Empty<SalesImportRecord>());
        var groups = records
            .Where(row => (row.GstDate ?? row.DocumentDate) is DateTime date && date.Date >= book.FromDate && date.Date <= book.ToDate)
            .GroupBy(row => new
            {
                VoucherType = NormalizeSalesDestination(row.Destination, row.DocumentType),
                row.DocumentNumber,
                VoucherDate = (row.GstDate ?? row.DocumentDate)!.Value.Date,
                ReferenceDate = row.DocumentDate?.Date,
                Party = row.CustomerName.Trim()
            });

        foreach (var group in groups)
        {
            var rows = group.ToArray();
            if (group.Key.Party.Length == 0 || group.Key.DocumentNumber.Trim().Length == 0 || group.Key.ReferenceDate is null) continue;
            if (!HasRequiredSalesLedgers(rows)) continue;

            var inventoryEntries = BuildSalesInventoryEntries(book, rows, group.Key.VoucherType);
            var entries = BuildSalesEntries(book, rows, group.Key.VoucherType, group.Key.Party, inventoryEntries);
            if (entries.Count == 0 || !IsBalanced(entries, inventoryEntries, group.Key.VoucherType.Equals("Sales", StringComparison.OrdinalIgnoreCase))) continue;

            var source = $"SALES|{group.Key.VoucherType}|{group.Key.VoucherDate:yyyyMMdd}|{group.Key.Party}|{group.Key.DocumentNumber}";
            AddEntries(book, group.Key.VoucherDate, entries, source, group.Key.VoucherType, group.Key.DocumentNumber, group.Key.DocumentNumber, group.Key.Party, rows.Select(row => row.Narration).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? string.Empty);
            foreach (var inventoryLedger in inventoryEntries
                         .Where(item => item.SalesLedger.Length > 0 && item.AccountingAmount > Tolerance)
                         .GroupBy(item => item.SalesLedger, StringComparer.OrdinalIgnoreCase))
            {
                var amount = Round(inventoryLedger.Sum(item => item.AccountingAmount));
                if (amount <= Tolerance) continue;
                if (group.Key.VoucherType.Equals("Sales", StringComparison.OrdinalIgnoreCase))
                    AddPosting(book, inventoryLedger.Key, group.Key.VoucherDate, 0m, amount, source + "|INVENTORY|" + inventoryLedger.Key, group.Key.VoucherType, group.Key.DocumentNumber, group.Key.DocumentNumber, group.Key.Party, rows.Select(row => row.Narration).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? string.Empty);
                else
                    AddPosting(book, inventoryLedger.Key, group.Key.VoucherDate, amount, 0m, source + "|INVENTORY|" + inventoryLedger.Key, group.Key.VoucherType, group.Key.DocumentNumber, group.Key.DocumentNumber, group.Key.Party, rows.Select(row => row.Narration).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? string.Empty);
            }
        }
    }

    private void AddJournalPostings(LedgerBookCache book)
    {
        var vouchers = Safe(
            () => _journalRepository.GetVouchers(book.ClientId, book.FinancialYear),
            Array.Empty<JournalVoucher>());
        foreach (var voucher in vouchers)
        {
            var date = voucher.VoucherDate.Date;
            if (date < book.FromDate || date > book.ToDate) continue;
            var entries = (voucher.Lines ?? [])
                .Where(line => Math.Abs(line.Amount) > Tolerance && !string.IsNullOrWhiteSpace(line.LedgerName))
                .OrderBy(line => line.LineNumber)
                .Select(line => new PostingEntry(
                    CanonicalLedgerName(book, line.LedgerName),
                    Round(Math.Abs(line.Amount)),
                    line.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase)))
                .Where(line => line.LedgerName.Length > 0)
                .ToArray();
            if (entries.Length == 0 || !IsBalanced(entries)) continue;

            var identity = voucher.GenerationMode.Equals("Automatic", StringComparison.OrdinalIgnoreCase)
                ? $"AUTO|{voucher.PeriodKey}"
                : $"MANUAL|{(voucher.VoucherNumber.Trim().Length > 0 ? voucher.VoucherNumber.Trim() : voucher.PersistentId.ToString())}";
            AddEntries(book, date, entries, $"JOURNAL|{identity}|{date:yyyyMMdd}", voucher.VoucherType, voucher.VoucherNumber, voucher.ReferenceNumber, string.Empty, voucher.Narration);
        }
    }

    private static IReadOnlyList<PostingEntry> BuildPurchaseEntries(
        LedgerBookCache book,
        IReadOnlyCollection<PurchaseImportRecord> rows,
        string voucherType,
        string party,
        bool isRcm,
        JournalGenerationSettings settings)
    {
        if (isRcm && voucherType.Equals("Purchase", StringComparison.OrdinalIgnoreCase))
            return BuildRcmPurchaseEntries(book, rows, party, settings);

        // Debit Note Reverse carries the opposite posting direction from the existing
        // purchase-return Debit Note: Purchase/Input GST Dr and supplier Cr. Tally export
        // uses Credit Note for this destination, while the books direction stays the same.
        var normalPurchase =
            voucherType.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ||
            rows.Any(PurchaseTreatmentPolicy.IsDebitNoteReverse);
        var entries = new List<PostingEntry>();
        AddGrouped(book, entries, rows, row => row.PurchaseLedger, PurchaseTreatmentPolicy.EffectivePurchaseLedgerAmount, normalPurchase);
        AddGrouped(book, entries, rows, row => row.IgstLedger, PurchaseTreatmentPolicy.EffectiveInputIgst, normalPurchase);
        AddGrouped(book, entries, rows, row => row.CgstLedger, PurchaseTreatmentPolicy.EffectiveInputCgst, normalPurchase);
        AddGrouped(book, entries, rows, row => row.SgstLedger, PurchaseTreatmentPolicy.EffectiveInputSgst, normalPurchase);
        AddGrouped(book, entries, rows, _ => "Input Cess", PurchaseTreatmentPolicy.EffectiveInputCess, normalPurchase);
        AddGrouped(book, entries, rows, _ => "Round Off", row => row.RoundOff, normalPurchase);
        var debit = entries.Where(entry => entry.IsDebit).Sum(entry => entry.Amount);
        var credit = entries.Where(entry => !entry.IsDebit).Sum(entry => entry.Amount);
        var partyAmount = normalPurchase ? debit - credit : credit - debit;
        if (partyAmount <= Tolerance) return [];
        entries.Add(new PostingEntry(CanonicalLedgerName(book, party), Round(partyAmount), !normalPurchase));
        return entries;
    }

    private static IReadOnlyList<PostingEntry> BuildRcmPurchaseEntries(
        LedgerBookCache book,
        IReadOnlyCollection<PurchaseImportRecord> rows,
        string party,
        JournalGenerationSettings settings)
    {
        var entries = new List<PostingEntry>();
        AddGrouped(book, entries, rows, row => row.PurchaseLedger, row => row.TaxableValue, true);
        AddGrouped(book, entries, rows, _ => "Round Off", row => row.RoundOff, true);
        var baseDebit = entries.Where(entry => entry.IsDebit).Sum(entry => entry.Amount);
        var baseCredit = entries.Where(entry => !entry.IsDebit).Sum(entry => entry.Amount);
        var partyBase = Round(baseDebit - baseCredit);
        if (partyBase <= Tolerance) return [];

        // Source RCM Purchase posts RCM Input Dr / RCM Payable Cr. Journal contains
        // only later reclassification, so count the source tax pair exactly once here.
        AddRcmTaxPairs(book, entries, rows, "IGST", row => row.IgstLedger, row => row.Igst, row => row.Rate, settings.TemporaryRcmIgstLedger);
        AddRcmTaxPairs(book, entries, rows, "CGST", row => row.CgstLedger, row => row.Cgst, row => row.Rate / 2m, settings.TemporaryRcmCgstLedger);
        AddRcmTaxPairs(book, entries, rows, "SGST", row => row.SgstLedger, row => row.Sgst, row => row.Rate / 2m, settings.TemporaryRcmSgstLedger);
        entries.Add(new PostingEntry(CanonicalLedgerName(book, party), partyBase, false));
        return entries;
    }

    private static void AddRcmTaxPairs(
        LedgerBookCache book,
        ICollection<PostingEntry> target,
        IEnumerable<PurchaseImportRecord> rows,
        string taxType,
        Func<PurchaseImportRecord, string> sourceLedgerSelector,
        Func<PurchaseImportRecord, decimal> amountSelector,
        Func<PurchaseImportRecord, decimal> rateSelector,
        string configuredInputLedger)
    {
        var grouped = rows
            .Select(row => new
            {
                SourceLedger = sourceLedgerSelector(row),
                Amount = amountSelector(row),
                Rate = rateSelector(row)
            })
            .Where(row => Math.Abs(row.Amount) > Tolerance)
            .GroupBy(row => new
            {
                InputLedger = PurchaseTreatmentPolicy.BuildRcmInputLedgerName(
                    row.SourceLedger, configuredInputLedger, taxType, row.Rate),
                PayableLedger = PurchaseTreatmentPolicy.BuildRcmPayableLedgerName(taxType, row.Rate)
            });

        foreach (var group in grouped)
        {
            var amount = Round(group.Sum(row => row.Amount));
            if (Math.Abs(amount) <= Tolerance) continue;
            var positive = amount > 0m;
            target.Add(new PostingEntry(CanonicalLedgerName(book, group.Key.InputLedger), Math.Abs(amount), positive));
            target.Add(new PostingEntry(CanonicalLedgerName(book, group.Key.PayableLedger), Math.Abs(amount), !positive));
        }
    }

    private static IReadOnlyList<SalesInventoryPosting> BuildSalesInventoryEntries(
        LedgerBookCache book,
        IEnumerable<SalesImportRecord> rows,
        string voucherType)
    {
        return rows
            .Where(row => row.HasInventory)
            .Select(row =>
            {
                var amount = row.ItemAmount != 0m ? row.ItemAmount : row.TaxableValue;
                var accountingAmount = row.AccountingAmount != 0m ? row.AccountingAmount : amount;
                var ledger = row.ItemSalesLedger.Trim().Length > 0 ? row.ItemSalesLedger : row.SalesLedger;
                return new SalesInventoryPosting(
                    CanonicalLedgerName(book, ledger),
                    Math.Abs(Round(accountingAmount)),
                    voucherType.Equals("Credit Note", StringComparison.OrdinalIgnoreCase));
            })
            .Where(item => item.SalesLedger.Length > 0 && item.AccountingAmount > Tolerance)
            .ToArray();
    }

    private static IReadOnlyList<PostingEntry> BuildSalesEntries(
        LedgerBookCache book,
        IReadOnlyCollection<SalesImportRecord> rows,
        string voucherType,
        string party,
        IReadOnlyCollection<SalesInventoryPosting> inventoryEntries)
    {
        var normalSales = voucherType.Equals("Sales", StringComparison.OrdinalIgnoreCase);
        var entries = new List<PostingEntry>();
        if (inventoryEntries.Count == 0)
            AddGrouped(book, entries, rows, row => row.SalesLedger, row => row.TaxableValue, !normalSales);
        AddGrouped(book, entries, rows, row => row.IgstLedger, row => row.Igst, !normalSales);
        AddGrouped(book, entries, rows, row => row.CgstLedger, row => row.Cgst, !normalSales);
        AddGrouped(book, entries, rows, row => row.SgstLedger, row => row.Sgst, !normalSales);
        AddGrouped(book, entries, rows, row => row.CessRate > 0m ? $"Output Cess @ {PurchaseTreatmentPolicy.FormatRate(row.CessRate)}%" : "Output Cess", row => row.Cess, !normalSales);
        AddGrouped(book, entries, rows, _ => "Round Off", row => row.RoundOff, !normalSales);

        var debit = entries.Where(entry => entry.IsDebit).Sum(entry => entry.Amount);
        var credit = entries.Where(entry => !entry.IsDebit).Sum(entry => entry.Amount);
        var inventoryAmount = Round(inventoryEntries.Sum(entry => Math.Abs(entry.AccountingAmount)));
        if (inventoryAmount > Tolerance)
        {
            if (normalSales) credit += inventoryAmount;
            else debit += inventoryAmount;
        }
        var partyAmount = normalSales ? credit - debit : debit - credit;
        if (partyAmount <= Tolerance) return [];
        entries.Add(new PostingEntry(CanonicalLedgerName(book, party), Round(partyAmount), normalSales));
        return entries;
    }

    private static void AddGrouped<T>(
        LedgerBookCache book,
        ICollection<PostingEntry> target,
        IEnumerable<T> rows,
        Func<T, string> ledgerSelector,
        Func<T, decimal> amountSelector,
        bool positiveIsDebit)
    {
        foreach (var group in rows
                     .GroupBy(row => CanonicalLedgerName(book, ledgerSelector(row)), StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Key.Length > 0))
        {
            var amount = Round(group.Sum(amountSelector));
            if (Math.Abs(amount) <= Tolerance) continue;
            target.Add(new PostingEntry(group.Key, Math.Abs(amount), positiveIsDebit == (amount > 0m)));
        }
    }

    private static bool HasRequiredPurchaseLedgers(IEnumerable<PurchaseImportRecord> rows, bool isRcm)
    {
        foreach (var row in rows)
        {
            var purchaseAmount = PurchaseTreatmentPolicy.EffectivePurchaseLedgerAmount(row);
            if (Math.Abs(purchaseAmount) > Tolerance && row.PurchaseLedger.Trim().Length == 0) return false;
            if (isRcm || PurchaseTreatmentPolicy.IsGstIncludedInCost(row.GstApplicability)) continue;
            if (Math.Abs(row.Igst) > Tolerance && row.IgstLedger.Trim().Length == 0) return false;
            if (Math.Abs(row.Cgst) > Tolerance && row.CgstLedger.Trim().Length == 0) return false;
            if (Math.Abs(row.Sgst) > Tolerance && row.SgstLedger.Trim().Length == 0) return false;
        }
        return true;
    }

    private static bool HasRequiredSalesLedgers(IEnumerable<SalesImportRecord> rows)
    {
        foreach (var row in rows)
        {
            if (Math.Abs(row.TaxableValue) > Tolerance && row.SalesLedger.Trim().Length == 0) return false;
            if (Math.Abs(row.Igst) > Tolerance && row.IgstLedger.Trim().Length == 0) return false;
            if (Math.Abs(row.Cgst) > Tolerance && row.CgstLedger.Trim().Length == 0) return false;
            if (Math.Abs(row.Sgst) > Tolerance && row.SgstLedger.Trim().Length == 0) return false;
        }
        return true;
    }

    private static bool IsBalanced(IEnumerable<PostingEntry> entries) =>
        Math.Abs(entries.Where(item => item.IsDebit).Sum(item => item.Amount) -
                 entries.Where(item => !item.IsDebit).Sum(item => item.Amount)) <= 0.01m;

    private static bool IsBalanced(
        IEnumerable<PostingEntry> entries,
        IEnumerable<SalesInventoryPosting> inventoryEntries,
        bool normalSales)
    {
        var debit = entries.Where(item => item.IsDebit).Sum(item => item.Amount);
        var credit = entries.Where(item => !item.IsDebit).Sum(item => item.Amount);
        var inventory = inventoryEntries.Sum(item => Math.Abs(item.AccountingAmount));
        if (normalSales) credit += inventory;
        else debit += inventory;
        return Math.Abs(debit - credit) <= 0.01m;
    }

    private static void AddEntries(
        LedgerBookCache book,
        DateTime date,
        IEnumerable<PostingEntry> entries,
        string source,
        string voucherType = "",
        string voucherNumber = "",
        string referenceNumber = "",
        string primaryParticulars = "",
        string narration = "")
    {
        var materialized = entries.Where(entry => entry.LedgerName.Length > 0 && entry.Amount > Tolerance).ToArray();
        var index = 0;
        foreach (var entry in materialized)
        {
            var counterpart = primaryParticulars;
            if (counterpart.Length == 0 || counterpart.Equals(entry.LedgerName, StringComparison.OrdinalIgnoreCase))
                counterpart = string.Join(", ", materialized.Where(other => !other.LedgerName.Equals(entry.LedgerName, StringComparison.OrdinalIgnoreCase)).Select(other => other.LedgerName).Distinct(StringComparer.OrdinalIgnoreCase).Take(3));
            if (entry.IsDebit)
                AddPosting(book, entry.LedgerName, date, entry.Amount, 0m, $"{source}|{index++}", voucherType, voucherNumber, referenceNumber, counterpart, narration);
            else
                AddPosting(book, entry.LedgerName, date, 0m, entry.Amount, $"{source}|{index++}", voucherType, voucherNumber, referenceNumber, counterpart, narration);
        }
    }

    private static void AddPosting(
        LedgerBookCache book,
        string ledgerName,
        DateTime date,
        decimal debit,
        decimal credit,
        string sourceKey,
        string voucherType = "",
        string voucherNumber = "",
        string referenceNumber = "",
        string particulars = "",
        string narration = "")
    {
        ledgerName = CanonicalLedgerName(book, ledgerName);
        if (ledgerName.Length == 0) return;
        debit = Round(Math.Abs(debit));
        credit = Round(Math.Abs(credit));
        if (debit <= Tolerance && credit <= Tolerance) return;

        if (!book.Ledgers.TryGetValue(ledgerName, out var ledger))
        {
            ledger = new LedgerAccumulator(ledgerName, string.Empty, 0m);
            book.Ledgers[ledgerName] = ledger;
            var identity = TallyMasterIdentity.Key(ledgerName);
            if (identity.Length > 0 && !book.LedgerIdentityIndex.ContainsKey(identity))
                book.LedgerIdentityIndex[identity] = ledgerName;
        }

        var dedupeKey = $"{sourceKey}|{ledgerName}|{debit:0.00}|{credit:0.00}";
        if (!book.PostingKeys.Add(dedupeKey)) return;
        ledger.Movements.Add(new LedgerMovement(date.Date, debit, credit, sourceKey, voucherType ?? string.Empty, voucherNumber ?? string.Empty, referenceNumber ?? string.Empty, particulars ?? string.Empty, narration ?? string.Empty));
    }

    private static LedgerTotalSnapshot CreatePeriodSnapshot(
        LedgerBookCache book,
        LedgerAccumulator ledger,
        DateTime fromDate,
        DateTime toDate)
    {
        var opening = ledger.OpeningBalance;
        decimal debit = 0m;
        decimal credit = 0m;
        var count = 0;
        foreach (var movement in ledger.Movements)
        {
            if (movement.Date < fromDate)
            {
                opening += movement.Debit - movement.Credit;
                continue;
            }
            if (movement.Date > toDate) continue;
            debit += movement.Debit;
            credit += movement.Credit;
            count++;
        }
        opening = Round(opening);
        debit = Round(debit);
        credit = Round(credit);
        var closing = Round(opening + debit - credit);
        return new LedgerTotalSnapshot
        {
            ClientId = book.ClientId,
            FinancialYear = book.FinancialYear,
            LedgerName = ledger.Name,
            ParentGroup = ledger.ParentGroup,
            OpeningBalance = opening,
            TotalDebit = debit,
            TotalCredit = credit,
            ClosingBalance = closing,
            AsOnDate = toDate,
            DebitToDate = debit,
            CreditToDate = credit,
            BalanceAsOnDate = closing,
            MovementCount = count,
            MovementCountToDate = count
        };
    }

    private static LedgerTotalSnapshot CreateSnapshot(
        LedgerBookCache book,
        LedgerAccumulator ledger,
        DateTime asOnDate)
    {
        var effectiveDate = asOnDate.Date;
        if (effectiveDate < book.FromDate) effectiveDate = book.FromDate;
        if (effectiveDate > book.ToDate) effectiveDate = book.ToDate;

        decimal totalDebit = 0m;
        decimal totalCredit = 0m;
        decimal debitToDate = 0m;
        decimal creditToDate = 0m;
        var movementCountToDate = 0;
        foreach (var movement in ledger.Movements)
        {
            totalDebit += movement.Debit;
            totalCredit += movement.Credit;
            if (movement.Date > effectiveDate) continue;
            debitToDate += movement.Debit;
            creditToDate += movement.Credit;
            movementCountToDate++;
        }

        return new LedgerTotalSnapshot
        {
            ClientId = book.ClientId,
            FinancialYear = book.FinancialYear,
            LedgerName = ledger.Name,
            ParentGroup = ledger.ParentGroup,
            OpeningBalance = Round(ledger.OpeningBalance),
            TotalDebit = Round(totalDebit),
            TotalCredit = Round(totalCredit),
            ClosingBalance = Round(ledger.OpeningBalance + totalDebit - totalCredit),
            AsOnDate = effectiveDate,
            DebitToDate = Round(debitToDate),
            CreditToDate = Round(creditToDate),
            BalanceAsOnDate = Round(ledger.OpeningBalance + debitToDate - creditToDate),
            MovementCount = ledger.Movements.Count,
            MovementCountToDate = movementCountToDate
        };
    }

    private string ReadDefaultEmptyLedger(long clientId, string financialYear)
    {
        try
        {
            var saved = _clientPreferences.Get(clientId, BankingPreferenceKeys.DefaultEmptyLedger(financialYear));
            var normalized = saved?.Trim() ?? string.Empty;
            if (normalized.Equals(BankingPreferenceKeys.LegacyDefaultEmptyLedgerName, StringComparison.OrdinalIgnoreCase))
                return BankingPreferenceKeys.DefaultEmptyLedgerName;
            if (normalized.Length > 0) return normalized;
        }
        catch
        {
            // Totals remain available even if a preference read fails.
        }
        return BankingPreferenceKeys.DefaultEmptyLedgerName;
    }

    private static string NormalizeCounterpartLedger(string? ledgerName, string defaultEmptyLedger)
    {
        var normalized = ledgerName?.Trim() ?? string.Empty;
        if (normalized.Length == 0 ||
            normalized.Equals("Suspense A/c", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Suspense A/C", StringComparison.OrdinalIgnoreCase))
        {
            var fallback = (defaultEmptyLedger ?? string.Empty).Trim();
            return fallback.Length > 0 ? fallback : BankingPreferenceKeys.DefaultEmptyLedgerName;
        }
        return normalized;
    }

    private static string NormalizePurchaseDestination(string destination, string documentType) =>
        destination.Contains("Debit", StringComparison.OrdinalIgnoreCase) ||
        documentType.Contains("Debit", StringComparison.OrdinalIgnoreCase) ||
        documentType.Contains("Credit Note", StringComparison.OrdinalIgnoreCase)
            ? "Debit Note"
            : "Purchase";

    private static string NormalizeSalesDestination(string destination, string documentType) =>
        destination.Contains("Credit", StringComparison.OrdinalIgnoreCase) ||
        documentType.Contains("Credit", StringComparison.OrdinalIgnoreCase)
            ? "Credit Note"
            : "Sales";

    private static string ResolveCashVoucherType(string commonGroup, string direction, string storedVoucherType)
    {
        if (IsCashOrBankGroup(commonGroup))
            return direction.Equals("DR", StringComparison.OrdinalIgnoreCase) ? "Payment" : "Receipt";
        if (commonGroup.Trim().Length > 0) return "Journal";

        var stored = (storedVoucherType ?? string.Empty).Trim();
        if (stored.Equals("Payment", StringComparison.OrdinalIgnoreCase)) return "Payment";
        if (stored.Equals("Receipt", StringComparison.OrdinalIgnoreCase)) return "Receipt";
        if (stored.Equals("Journal", StringComparison.OrdinalIgnoreCase)) return "Journal";
        return string.Empty;
    }

    private static bool IsCashOrBankGroup(string groupName)
    {
        var key = new string((groupName ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        return key is "CASHINHAND" or "BANKACCOUNTS";
    }

    private static string ParentGroup(LedgerBookCache book, string ledgerName) =>
        book.Ledgers.TryGetValue(CanonicalLedgerName(book, ledgerName), out var item)
            ? item.ParentGroup
            : string.Empty;

    private static string CanonicalLedgerName(LedgerBookCache book, string? value)
    {
        var name = value?.Trim() ?? string.Empty;
        if (name.Length == 0) return string.Empty;
        if (book.Ledgers.TryGetValue(name, out var existing)) return existing.Name;
        var identity = TallyMasterIdentity.Key(name);
        return identity.Length > 0 && book.LedgerIdentityIndex.TryGetValue(identity, out var canonical)
            ? canonical
            : name;
    }

    private static (DateTime FromDate, DateTime ToDate) FinancialYearRange(string financialYear)
    {
        var first = financialYear.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (!int.TryParse(first, out var year) || year < 1900 || year > 9998)
        {
            var today = DateTime.Today;
            year = today.Month >= 4 ? today.Year : today.Year - 1;
        }
        return (new DateTime(year, 4, 1), new DateTime(year + 1, 3, 31));
    }

    private static string CacheKey(long clientId, string financialYear) =>
        $"{clientId}|{financialYear.Trim()}";

    private static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static T Safe<T>(Func<T> getter, T fallback)
    {
        try
        {
            var value = getter();
            return value is null ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }

    private sealed class LedgerBookCache(
        long clientId,
        string financialYear,
        DateTime fromDate,
        DateTime toDate,
        Dictionary<string, LedgerAccumulator> ledgers)
    {
        public long ClientId { get; } = clientId;
        public string FinancialYear { get; } = financialYear;
        public DateTime FromDate { get; } = fromDate;
        public DateTime ToDate { get; } = toDate;
        public Dictionary<string, LedgerAccumulator> Ledgers { get; } = ledgers;
        public Dictionary<string, string> LedgerIdentityIndex { get; } = ledgers.Values
            .Where(item => TallyMasterIdentity.Key(item.Name).Length > 0)
            .GroupBy(item => TallyMasterIdentity.Key(item.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);
        public HashSet<string> PostingKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class LedgerAccumulator(string name, string parentGroup, decimal openingBalance)
    {
        public string Name { get; } = name;
        public string ParentGroup { get; } = parentGroup;
        public decimal OpeningBalance { get; } = openingBalance;
        public List<LedgerMovement> Movements { get; } = [];
    }

    private sealed record LedgerMovement(DateTime Date, decimal Debit, decimal Credit, string SourceKey, string VoucherType, string VoucherNumber, string ReferenceNumber, string Particulars, string Narration);
    private sealed record PostingEntry(string LedgerName, decimal Amount, bool IsDebit);
    private sealed record SalesInventoryPosting(string SalesLedger, decimal AccountingAmount, bool IsCreditNote);
}
