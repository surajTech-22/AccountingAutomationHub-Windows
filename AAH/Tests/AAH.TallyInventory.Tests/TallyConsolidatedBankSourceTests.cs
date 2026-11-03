using System.Collections;
using System.Reflection;
using System.Xml.Linq;
using AccountingAutomationHub.Application.Banking;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Domain.Masters;
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

        // Use the SAME original BuildRequiredLedgerSpecs Master-first bank
        // grouping as individual Tally Bank import; this is the linkage the
        // old consolidated wrapper skipped (and then falsely required the
        // Ledger to already exist as an explicit branch Master).
        bank.Mappings.Add(new BankLedgerMapping
        {
            ClientId = 19, FinancialYear = client.FinancialYear,
            LedgerName = "Office Expenses",
            MasterGroup = "Indirect Expenses",
            Status = "Mapped",
            UpdatedUtc = new DateTime(2026, 11, 3)
        });
        var masters = DispatchProxy.Create<IMasterRepository, SafeMasterReadOnlyFake>();
        var masterStore = (SafeMasterReadOnlyFake)(object)masters;
        var prepareMethod = typeof(TallyBankVoucherImportService).GetMethod(
            "PrepareOfflineConsolidatedBankVouchers",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Bank original preparation missing");
        var opaquePrepared = prepareMethod.Invoke(null, [repo, client, from, to]);
        var originalMasterMethod = typeof(TallyBankVoucherImportService).GetMethod(
            "ReadOriginalOfflineBankLedgerAuthorities",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "The wrapper does not link the original Bank Ledger Mapping/Group rules");
        var authority = ((IEnumerable)(originalMasterMethod.Invoke(
            null, [repo, masters, client, opaquePrepared])
            ?? throw new InvalidOperationException("Original Bank master authority returned null")))
            .Cast<object>().ToArray();
        var mappedLedger = authority.FirstOrDefault(item =>
            item.GetType().GetField("Item1")?.GetValue(item)?.ToString() == "Office Expenses");
        Check(mappedLedger is not null &&
              mappedLedger.GetType().GetField("Item2")?.GetValue(mappedLedger)?.ToString() ==
                  "Indirect Expenses" &&
              (bool)(mappedLedger.GetType().GetField("Item3")?.GetValue(mappedLedger) ?? false),
            "Original Bank Ledger Mapping with saved Group did not supply permitted Master authority");
        Check(masterStore.Writes == 0 && bank.Writes == 0,
            "Read-only consolidated preview wrote Ledger Mapping, Master or Bank rows");

        // One genuine same-day matched transfer: the normal Bank DR/CR
        // classifier produces two mirrored Contra intents if asked per row.
        // The consolidated wrapper must include the matched BANK sides just
        // once, not double the movement and not infer a transfer by narration.
        bank.Accounts.Add(new BankAccountRecord {
            Id = 29, ClientId = 19, FinancialYear = client.FinancialYear,
            BankName = "Second Branch Bank", AccountType = "Current",
            LedgerName = "Second Bank Account"
        });
        bank.Rows.Add(new BankTransactionRecord {
            Id = 217, ClientId = 19, FinancialYear = client.FinancialYear,
            BankAccountId = 29, Mode = "Receipt",
            LedgerName = "Branch Bank Account",
            TransactionDate = bank.Rows[0].TransactionDate,
            Narration = "Matched bank movement", TransactionId = "BANK-SOURCE-217",
            Credit = 125.50m, RecordStatus = "Active"
        });
        bank.Matches.Add(new BankTransferMatch {
            ClientId = 19, FinancialYear = client.FinancialYear,
            SourceTransactionId = 216, TargetTransactionId = 217,
            MatchDate = bank.Rows[0].TransactionDate,
            Amount = 125.50m, Status = "Matched"
        });
        var matchedContra = Prepare(repo, client, from, to);
        Check(matchedContra.Count == 1, "one proven bank transfer was double-posted in consolidated XML");
        var contra = matchedContra[0].GetType().GetProperty("Expectation")?.GetValue(matchedContra[0]);
        Check(contra?.GetType().GetProperty("VoucherType")?.GetValue(contra)?.ToString() == "Contra",
            "matched bank transfer did not reuse the normal common Contra treatment");
        var contraLines = ((IEnumerable)(contra!.GetType().GetProperty("Lines")!.GetValue(contra)!))
            .Cast<object>().Select(Ledger).ToArray();
        Check(contraLines.Length == 2 &&
              contraLines.Contains("Branch Bank Account") &&
              contraLines.Contains("Second Bank Account"),
            "common Contra did not retain both original bank ledgers");
        Check(bank.Writes == 0, "consolidated Contra preview mutated original Bank transactions");
        bank.Matches.Clear();
        bank.Rows.RemoveAt(1);

        // The real manual Bank COORDINATOR (not ImportAsync's default)
        // explicitly passes allowUnmappedSuspense=true. The 208/296 unmapped
        // Final Ledger rows therefore still have valid Suspense treatment.
        // The source LedgerName remains EMPTY for future same-MASTERID ALTER.
        bank.Rows[0].LedgerName = "";
        var suspenseReady = Prepare(repo, client, from, to);
        Check(suspenseReady.Count == 1 &&
              bank.Rows[0].LedgerName.Length == 0,
            "Unmapped original manual Bank did not use read-only Suspense treatment.");
        var suspense = suspenseReady[0].GetType()
            .GetProperty("Expectation")?.GetValue(suspenseReady[0]);
        var suspenseLines = ((IEnumerable)(suspense?.GetType()
                .GetProperty("Lines")?.GetValue(suspense)
                ?? throw new InvalidOperationException("Suspense Bank DRCR not prepared.")))
            .Cast<object>().ToArray();
        Check(suspenseLines.Any(line => Ledger(line) == "Suspense") &&
              suspenseLines.Any(line => Ledger(line) == "Branch Bank Account"),
            "Existing Suspense Ledger and the original Bank Account were not both present.");
        bank.Rows.Add(new BankTransactionRecord {
            Id = 218, ClientId = 19, FinancialYear = client.FinancialYear,
            BankAccountId = 28, Mode = "Receipt",
            LedgerName = "Customer Ledger",
            TransactionDate = new DateTime(2025, 10, 16),
            Narration = "Correctly mapped independent source",
            ReferenceNumber = "BANK-REF-218",
            TransactionId = "BANK-SOURCE-218", Credit = 79.50m,
            RecordStatus = "Active"
        });
        var mixed = Prepare(repo, client, from, to);
        Check(mixed.Count == 2 &&
              mixed.Any(row => row.GetType().GetProperty("SourceNumber")
                  ?.GetValue(row)?.ToString() == "BANK-218") &&
              mixed.Any(row => row.GetType().GetProperty("SourceNumber")
                  ?.GetValue(row)?.ToString() == "BANK-216"),
            "Original manual Bank Suspense row and correctly mapped row did not both prepare.");
        Check(bank.Writes == 0, "Suspense/manual Bank preview mutated original source records");
        bank.Rows.RemoveAt(1);
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
        // A changed mapped ledger retains the original source anchor but
        // must cease showing the OLD verified destination as up-to-date.
        // UI and import use the SAME original signed-content digest method.
        var previous = XElement.Parse("""
            <VOUCHER ACTION="Create">
              <DATE>20250915</DATE><VOUCHERTYPENAME>Payment</VOUCHERTYPENAME>
              <VOUCHERNUMBER>B-ABCDEF0123456789ABCDEF01-BANK-216</VOUCHERNUMBER>
              <NARRATION>Original bank narration</NARRATION>
              <ALLLEDGERENTRIES.LIST><LEDGERNAME>Office Expenses</LEDGERNAME><AMOUNT>-125.50</AMOUNT></ALLLEDGERENTRIES.LIST>
              <ALLLEDGERENTRIES.LIST><LEDGERNAME>Branch Bank Account</LEDGERNAME><AMOUNT>125.50</AMOUNT></ALLLEDGERENTRIES.LIST>
            </VOUCHER>
            """);
        var changed = new XElement(previous);
        changed.Descendants("LEDGERNAME").First().Value = "Office Expenses Revised";
        var oldHash = TallyConsolidatedVoucherImportService
            .ComputePreparedContentIdentity(previous).ContentHash;
        var newHash = TallyConsolidatedVoucherImportService
            .ComputePreparedContentIdentity(changed).ContentHash;
        Check(oldHash.Length > 0 && newHash.Length > 0 && oldHash != newHash,
            "a changed Bank Ledger did not become a pending previous-to-desired ALTER");
        const string bankScopedNumber = "B-ABCDEF0123456789ABCDEF01-BANK-216";
        var paymentAnchor = TallyConsolidatedVoucherImportService
            .BuildConsolidatedSourceAnchor("Payment", bankScopedNumber);
        var contraAnchor = TallyConsolidatedVoucherImportService
            .BuildConsolidatedSourceAnchor("Contra", bankScopedNumber);
        Check(paymentAnchor.Equals(contraAnchor, StringComparison.Ordinal) &&
              paymentAnchor.Equals("CONSOLIDATED|BANK|" + bankScopedNumber,
                  StringComparison.Ordinal),
            "a Bank mode change abandoned its original bound MASTERID and could CREATE a duplicate");
        Check(TallyConsolidatedVoucherImportService
                  .BuildConsolidatedSourceAnchor("Sales", "B-SALES-1")
                  .Equals("CONSOLIDATED|Sales|B-SALES-1", StringComparison.Ordinal),
            "unrelated original Sales/Purchase source-anchor formats were changed");
        var dashboardSource = File.ReadAllText(Path.Combine(root!.FullName,
            "AAH", "src", "AccountingAutomationHub.Desktop", "ViewModels",
            "DashboardViewModel.cs"));
        var bindingSource = File.ReadAllText(Path.Combine(root.FullName,
            "AAH", "src", "AccountingAutomationHub.Infrastructure", "Tally",
            "SqliteTallyRepository.cs"));
        Check(dashboardSource.Contains("number.Contains(\"-BANK-\"", StringComparison.Ordinal) &&
              dashboardSource.Contains("ComputePreparedContentIdentity(voucher)", StringComparison.Ordinal) &&
              dashboardSource.Contains("LastVerifiedHash", StringComparison.Ordinal) &&
              dashboardSource.Contains("moduleChanged", StringComparison.Ordinal) &&
              bindingSource.Contains("SELECT SourceAnchor, State, LastContentHash", StringComparison.Ordinal),
            "Consolidated panel still classifies Bank Payment/Receipt as Cash or ignores source changes.");


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
              exporter.Contains("originalBankExpectations.Length > 0", StringComparison.Ordinal) &&
              exporter.Contains("ReadOriginalOfflineBankLedgerAuthorities(", StringComparison.Ordinal) &&
              exporter.Contains("AAH-BRANCH-XML-SOURCE-V4-BANK-ORIGINAL-ELIGIBILITY", StringComparison.Ordinal),
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
    public List<BankTransferMatch> Matches { get; set; } = [];
    public List<BankLedgerMapping> Mappings { get; set; } = [];
    public int Writes { get; private set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod?.Name ?? "";
        if (name == "GetTransactions") return Rows;
        if (name == "GetAccounts") return Accounts;
        if (name == "GetTransferMatches") return Matches;
        if (name == "GetLedgerMappings") return Mappings;
        Writes++;
        throw new InvalidOperationException("Offline Bank attempted unexpected operation: " + name);
    }
}

public class SafeMasterReadOnlyFake : DispatchProxy
{
    public List<MasterRecord> Rows { get; set; } = [];
    public int Writes { get; private set; }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == "Get") return Rows;
        Writes++;
        throw new InvalidOperationException(
            "Offline Bank requested an unexpected master operation: " + method?.Name);
    }
}
