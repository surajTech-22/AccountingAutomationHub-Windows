using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Journals;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Journals;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Main-Server accounting actions exposed to the native Android bridge. Android renders controls;
/// the existing Windows repositories remain the accounting authority.
/// </summary>
public sealed class MobileAccountingActionService
{
    private readonly SqliteBankingRepository _banking = new();
    private readonly SqliteJournalRepository _journal = new();

    public MobileAccountingActionService()
    {
        // Repository schema setup is deferred until the Main Server starts after the WPF shell is visible.
    }

    public void Initialize()
    {
        _banking.Initialize();
        _journal.Initialize();
    }

    public IReadOnlyList<BankLedgerMapping> GetLedgerMappings(long clientId, string financialYear, bool pendingOnly) =>
        _banking.GetLedgerMappings(clientId, financialYear, pendingOnly);

    public BankLedgerMapping SaveLedgerMapping(long clientId, string financialYear, long mappingId, string ledgerName, string masterGroup)
    {
        var existing = _banking.GetLedgerMappings(clientId, financialYear, pendingOnly: false).FirstOrDefault(x => x.Id == mappingId)
            ?? throw new InvalidOperationException("The selected Ledger Mapping no longer exists.");
        var ledger = (ledgerName ?? string.Empty).Trim();
        if (ledger.Length == 0) throw new InvalidOperationException("Choose the final Ledger before saving the mapping.");
        existing.LedgerName = ledger;
        existing.MasterGroup = (masterGroup ?? string.Empty).Trim();
        existing.Status = "Mapped";
        existing.UpdatedUtc = DateTime.UtcNow;
        _banking.UpsertLedgerMapping(existing);
        _banking.ApplyLedgerMapping(clientId, financialYear, existing);
        return existing;
    }

    public IReadOnlyList<CashEntryRecord> GetCashEntries(long clientId, string financialYear) => _banking.GetCashEntries(clientId, financialYear);

    public CashEntryRecord SaveManualCash(long clientId, string financialYear, DateTime entryDate, string entryLedger, string drCr, decimal amount, string commonLedger, string narration, string userName)
    {
        if (entryDate == default) throw new InvalidOperationException("Cash entry date is required.");
        var ledger = (entryLedger ?? string.Empty).Trim();
        var counterpart = (commonLedger ?? string.Empty).Trim();
        if (ledger.Length == 0 || counterpart.Length == 0) throw new InvalidOperationException("Cash Ledger and counterpart Ledger are required.");
        if (amount <= 0m) throw new InvalidOperationException("Cash amount must be greater than zero.");
        var debit = drCr.StartsWith("D", StringComparison.OrdinalIgnoreCase) ? amount : 0m;
        var credit = debit == 0m ? amount : 0m;
        var row = new CashEntryRecord
        {
            ClientId = clientId,
            FinancialYear = financialYear,
            EntryDate = entryDate.Date,
            EntryLedger = ledger,
            Debit = debit,
            Credit = credit,
            Narration = (narration ?? string.Empty).Trim(),
            CommonLedger = counterpart,
            MappedMasterLedger = counterpart,
            VoucherType = debit > 0m ? "Receipt" : "Payment",
            Status = "Mapped",
            WorkflowStatus = "Draft",
            CreatedBy = userName,
            IsUserEdited = true,
            UpdatedUtc = DateTime.UtcNow
        };
        _banking.SaveCashEntry(row);
        return row;
    }

    public IReadOnlyList<JournalVoucher> GetJournalVouchers(long clientId, string financialYear) => _journal.GetVouchers(clientId, financialYear);

    public JournalVoucher SaveManualJournal(long clientId, string financialYear, DateTime voucherDate, string debitLedger, string creditLedger, decimal amount, string narration, string reference, string userName)
    {
        var debit = (debitLedger ?? string.Empty).Trim();
        var credit = (creditLedger ?? string.Empty).Trim();
        if (voucherDate == default) throw new InvalidOperationException("Journal date is required.");
        if (debit.Length == 0 || credit.Length == 0) throw new InvalidOperationException("Debit and Credit Ledgers are required.");
        if (debit.Equals(credit, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Debit and Credit Ledgers must be different.");
        if (amount <= 0m) throw new InvalidOperationException("Journal amount must be greater than zero.");
        var voucher = new JournalVoucher
        {
            ClientId = clientId,
            FinancialYear = financialYear,
            PeriodKey = voucherDate.ToString("yyyy-MM"),
            PeriodLabel = voucherDate.ToString("MMM yyyy"),
            VoucherDate = voucherDate.Date,
            VoucherNumber = $"MJ-{voucherDate:yyyyMMdd}-{Guid.NewGuid():N}"[..25],
            VoucherType = "Journal",
            Narration = (narration ?? string.Empty).Trim(),
            GenerationMode = "Manual",
            WorkflowStatus = "Draft",
            ReferenceNumber = (reference ?? string.Empty).Trim(),
            CreatedBy = userName,
            IsUserEdited = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            Lines =
            [
                new JournalLine { LineNumber = 1, LedgerName = debit, Amount = amount, DrCr = "Dr", LineRole = "Manual" },
                new JournalLine { LineNumber = 2, LedgerName = credit, Amount = amount, DrCr = "Cr", LineRole = "Manual" }
            ]
        };
        _journal.SaveVoucher(voucher);
        return voucher;
    }

    public (string FileName, byte[] Bytes, string ContentType) ExportSuspense(ClientProfile client, string format)
    {
        var rows = _banking.GetTransactions(client.Id, client.FinancialYear, includeArchived: false)
            .Where(row => IsSuspense(row.LedgerName)).OrderBy(row => row.TransactionDate).ThenBy(row => row.Id).ToArray();
        if (rows.Length == 0) throw new InvalidOperationException("There are no active Suspense / empty-ledger Bank Statement rows to export.");
        var accounts = _banking.GetAccounts(client.Id, client.FinancialYear);
        var normalized = (format ?? string.Empty).Trim().ToLowerInvariant();
        var extension = normalized == "pdf" ? ".pdf" : ".xlsx";
        var fileName = $"AAH_BANK_SUSPENSE_{Safe(client.ClientCode)}_{Safe(client.FinancialYear)}_{DateTime.Now:yyyyMMdd-HHmmss}{extension}";
        var tempFolder = Path.Combine(Path.GetTempPath(), "AccountingAutomationHub", "MobileAction");
        Directory.CreateDirectory(tempFolder);
        var temp = Path.Combine(tempFolder, $"AAH-{Guid.NewGuid():N}{extension}");
        try
        {
            if (normalized == "pdf")
            {
                var accountNames = accounts.ToDictionary(x => x.Id, x => x.AuthorityName);
                var projection = rows.Select(row => new SuspensePdfRow(
                    row.TransactionId, row.TransactionDate, row.Narration, row.Debit, row.Credit,
                    accountNames.TryGetValue(row.BankAccountId, out var name) ? name : row.BankAccountName,
                    row.LedgerName)).Cast<object>().ToArray();
                AccountingReportExportService.ExportPdf(temp, $"AAH Bank Suspense · {client.ClientCode} · FY {client.FinancialYear}", projection,
                [
                    new("Transaction ID", nameof(SuspensePdfRow.TransactionId)), new("Date", nameof(SuspensePdfRow.Date)),
                    new("Narration", nameof(SuspensePdfRow.Narration)), new("Debit", nameof(SuspensePdfRow.Debit)),
                    new("Credit", nameof(SuspensePdfRow.Credit)), new("Bank", nameof(SuspensePdfRow.Bank)), new("Ledger", nameof(SuspensePdfRow.Ledger))
                ]);
                return (fileName, File.ReadAllBytes(temp), "application/pdf");
            }

            BankingViewModel.WriteSuspenseWorkbook(temp, client, rows, accounts);
            return (fileName, File.ReadAllBytes(temp), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private static bool IsSuspense(string? value)
    {
        var ledger = (value ?? string.Empty).Trim();
        return ledger.Length == 0 || ledger.Equals("Suspense", StringComparison.OrdinalIgnoreCase) ||
               ledger.Equals("Suspense A/c", StringComparison.OrdinalIgnoreCase) || ledger.Equals("Suspense A/C", StringComparison.OrdinalIgnoreCase);
    }
    private static string Safe(string value) => new string((value ?? string.Empty).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
    private sealed record SuspensePdfRow(string TransactionId, DateTime Date, string Narration, decimal Debit, decimal Credit, string Bank, string Ledger);
}
