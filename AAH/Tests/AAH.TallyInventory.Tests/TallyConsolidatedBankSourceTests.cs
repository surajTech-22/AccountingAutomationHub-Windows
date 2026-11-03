using System.Collections;
using System.Reflection;
using AccountingAutomationHub.Application.Banking;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Infrastructure.Tally;

internal static class TallyConsolidatedBankSourceTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Consolidated Bank source regression: " + message);
    }

    private static IReadOnlyList<object> Prepare(
        IBankingRepository bank, ClientProfile client, DateTime from, DateTime to)
    {
        var method = typeof(TallyBankVoucherImportService).GetMethod(
            "PrepareOfflineConsolidatedBankVouchers",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Original Bank offline source adapter was removed.");
        var value = method.Invoke(null, [bank, client, from, to])
            ?? throw new InvalidOperationException("Bank offline preparation returned null.");
        return ((IEnumerable)value).Cast<object>().ToArray();
    }

    public static void Run()
    {
        var from = new DateTime(2025, 4, 1);
        var to = new DateTime(2026, 3, 31);
        var client = new ClientProfile { Id = 19, FinancialYear = "2025-26" };
        var repo = DispatchProxy.Create<IBankingRepository, SafeBankReadOnlyFake>();
        var bank = (SafeBankReadOnlyFake)(object)repo;
        bank.Accounts = [
            new BankAccountRecord {
                Id = 28, ClientId = 19, FinancialYear = client.FinancialYear,
                BankName = "Branch Bank", AccountType = "Current",
                LedgerName = "Branch Bank Account"
            }
        ];
        bank.Rows = [
            new BankTransactionRecord {
                Id = 216, ClientId = 19, FinancialYear = client.FinancialYear,
                BankAccountId = 28, Mode = "Payment", LedgerName = "Office Expenses",
                TransactionDate = new DateTime(2025, 9, 15),
                Narration = "Original bank narration", ReferenceNumber = "BANK-REF-19",
                TransactionId = "BANK-SOURCE-216", Debit = 125.50m,
                RecordStatus = "Active"
            }
        ];
        var first = Prepare(repo, client, from, to);
        Check(first.Count == 1, "valid mapped Bank debit missing from source preview");
        var row = first[0];
        Check(row.GetType().GetProperty("SourceNumber")?.GetValue(row)?.ToString() == "BANK-216",
            "Bank source anchor must be deterministic across rebuilds and branch isolation");
        var intent = row.GetType().GetProperty("Expectation")?.GetValue(row)
            ?? throw new InvalidOperationException("Original Bank writer intent not included.");
        Check(intent.GetType().GetProperty("VoucherType")?.GetValue(intent)?.ToString() == "Payment",
            "Original Payment type changed during consolidation");
        var lines = ((IEnumerable)(intent.GetType().GetProperty("Lines")?.GetValue(intent)
            ?? throw new InvalidOperationException("Original Bank DR/CR lines missing.")))
            .Cast<object>().ToArray();
        Check(lines.Length == 2, "Bank writer must supply exactly two ledger sides");
        string Ledger(object item) =>
            item.GetType().GetProperty("LedgerName")?.GetValue(item)?.ToString() ?? "";
        bool Debit(object item) =>
            (bool)(item.GetType().GetProperty("IsDebit")?.GetValue(item) ?? false);
        decimal Amount(object item) =>
            (decimal)(item.GetType().GetProperty("Amount")?.GetValue(item) ?? 0m);
        Check(Ledger(lines[0]) == "Office Expenses" && Debit(lines[0]) &&
              Amount(lines[0]) == 125.50m &&
              Ledger(lines[1]) == "Branch Bank Account" && !Debit(lines[1]) &&
              Amount(lines[1]) == 125.50m,
            "Payment debit/credit or financial-account ledger changed from original Bank writer");
        Check(Prepare(repo, client, from, to).Count == 1,
            "unchanged bank source was not deterministic");
        Check(bank.Writes == 0, "offline bank source mutated bank repository");

        bank.Rows[0].LedgerName = "";
        try
        {
            Prepare(repo, client, from, to);
            throw new InvalidOperationException("unmapped bank source was silently consolidated");
        }
        catch (TargetInvocationException ex) when (
            ex.InnerException is InvalidOperationException actual &&
            actual.Message.Contains("Final Ledger", StringComparison.Ordinal))
        {
            // Correct: no implicit Suspense posting for manual consolidated import.
        }
        Check(bank.Writes == 0, "invalid bank source preparation mutated accounting records");
        bank.Rows[0].LedgerName = "Office Expenses";
        bank.Rows[0].RecordStatus = "Archived";
        Check(Prepare(repo, client, from, to).Count == 0,
            "archived Bank row included as a new voucher");
        bank.Rows[0].RecordStatus = "Active";

        var root = new DirectoryInfo(Environment.CurrentDirectory);
        while (root is not null &&
               !File.Exists(Path.Combine(root.FullName, "AAH", "src",
                   "AccountingAutomationHub.Infrastructure", "Tally",
                   "TallyAccountingVoucherExportService.cs")))
            root = root.Parent;
        Check(root is not null, "source checkout unavailable");
        var exporter = File.ReadAllText(Path.Combine(root!.FullName, "AAH", "src",
            "AccountingAutomationHub.Infrastructure", "Tally",
            "TallyAccountingVoucherExportService.cs"));
        var importer = File.ReadAllText(Path.Combine(root.FullName, "AAH", "src",
            "AccountingAutomationHub.Infrastructure", "Tally",
            "TallyConsolidatedVoucherImportService.cs"));
        Check(exporter.Contains("BankTransactions = Stable(_bankingRepository.GetTransactions(",
                  StringComparison.Ordinal) &&
              exporter.Contains("BankLedgerMappings = Stable(_bankingRepository.GetLedgerMappings(",
                  StringComparison.Ordinal) &&
              exporter.Contains("BankTransferMatches = Stable(_bankingRepository.GetTransferMatches(",
                  StringComparison.Ordinal) &&
              exporter.Contains("PrepareOfflineConsolidatedBankVouchers(",
                  StringComparison.Ordinal) &&
              exporter.Contains("TallyBankCreateAlterXmlBuilder.Build(", StringComparison.Ordinal) &&
              exporter.Contains("TallyBankCreateAlterXmlValidator.Validate(", StringComparison.Ordinal) &&
              exporter.Contains("originalBankExpectations.Length > 0", StringComparison.Ordinal),
            "Bank statement transactions/mappings were omitted from cache or original XML preparation");
        Check(importer.Contains("PotentialExistingUnboundBankVoucher(", StringComparison.Ordinal) &&
              importer.Contains("PreviousConsolidatedVersionMatches(", StringComparison.Ordinal) &&
              importer.Contains("BuildBoundConsolidatedVoucherAlterXml(", StringComparison.Ordinal),
            "Bank wrapper bypasses the normal exact duplicate/MASTERID previous-version ALTER protection");
        Console.WriteLine(
            "PASS: consolidated Bank uses original payment DRCR, source fingerprint and exact Bank XML; " +
            "unmapped and archived source safety and original Tally duplicate/ALTER gates remain.");
    }
}

// Deliberately throws for ALL writes and unrecognized repository operations.
// The offline bank projection must only READ the original AAH branch rows.
public class SafeBankReadOnlyFake : DispatchProxy
{
    public List<BankTransactionRecord> Rows { get; set; } = [];
    public List<BankAccountRecord> Accounts { get; set; } = [];
    public int Writes { get; private set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod?.Name ?? "";
        if (name == "GetTransactions") return Rows;
        if (name == "GetAccounts") return Accounts;
        if (name == "GetTransferMatches") return Array.Empty<BankTransferMatch>();
        Writes++;
        throw new InvalidOperationException("Offline Bank attempted unexpected operation: " + name);
    }
}
