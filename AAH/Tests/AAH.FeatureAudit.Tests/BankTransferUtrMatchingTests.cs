using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Masters;
using Microsoft.Data.Sqlite;

internal static class BankTransferUtrMatchingTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Run()
    {
        RunCanonicalUtrTests();
        RunNewlineCleanupTests();
        RunEndToEndReconcileTest();
        RunBankingViewLedgerColumnContractTest();
        Console.WriteLine("PASS BankTransferUtrMatching: canonical RBI RTGS/NEFT UTR matching, escaped newline cleanup, SQLite reconcile & Ledger selector contract.");
    }

    private static void RunCanonicalUtrTests()
    {
        var txDate = new DateTime(2025, 11, 5);
        var sourceRefs = BankNarrationParser.ExtractReferenceGroups(
            "RTGS/ SYNTR52025110500987654/ ALPHA TRADING 000111",
            "RTGS",
            500000m,
            txDate);
        var targetRefs = BankNarrationParser.ExtractReferenceGroups(
            "DEP TFR\\nRTGS ALPHA TRADING SYNTH25309987654\\nTRF FR INTERNAL ACCOUNT",
            "RTGS",
            500000m,
            txDate);

        var shared = BankNarrationParser.SelectSharedReference(sourceRefs, targetRefs, "RTGS", txDate);
        Check(shared.Equals("SYNTR52025110500987654", StringComparison.OrdinalIgnoreCase),
            $"Expected 22-char UTR display token SYNTR52025110500987654, got [{shared}].");

        // Leap year Julian day 366 (2024-12-31)
        var leapDate = new DateTime(2024, 12, 31);
        var neft22 = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SYNTN52024123100456789" };
        var neft16 = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SYNTH24366456789" };
        var leapShared = BankNarrationParser.SelectSharedReference(neft22, neft16, "NEFT", leapDate);
        Check(leapShared.Equals("SYNTN52024123100456789", StringComparison.OrdinalIgnoreCase),
            $"Expected leap-year NEFT match SYNTN52024123100456789, got [{leapShared}].");

        // Exact match still wins over canonical fallback
        var exactAndCanonicalSource = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SYNTR52025110500987654",
            "COMMONREF987654"
        };
        var exactAndCanonicalTarget = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SYNTH25309987654",
            "COMMONREF987654"
        };
        Check(BankNarrationParser.SelectSharedReference(exactAndCanonicalSource, exactAndCanonicalTarget, "RTGS", txDate)
            .Equals("COMMONREF987654", StringComparison.OrdinalIgnoreCase),
            "Exact reference intersection should take priority over canonical UTR fallback.");

        // Negative cases
        Check(!BankNarrationParser.TryBuildCanonicalRbiUtrKey("SYNTH25366987654", new DateTime(2025, 12, 31), out _),
            "Day 366 in non-leap year 2025 must fail.");
        Check(!BankNarrationParser.TryBuildCanonicalRbiUtrKey("SYNTH25000987654", new DateTime(2025, 1, 1), out _),
            "Day 000 must fail.");
        Check(!BankNarrationParser.TryBuildCanonicalRbiUtrKey("SYNTH253090000123", txDate, out _),
            "Sequence shorter than 4 significant digits after trimming zeroes must fail.");
        Check(BankNarrationParser.SelectSharedReference(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SYNTR52025110500987654" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OTHRH25309987654" },
                "RTGS",
                txDate).Length == 0,
            "Different bank code prefix must not match.");
        Check(BankNarrationParser.SelectSharedReference(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SYNTR52025110500987654" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SYNTH25309987654" },
                "RTGS",
                new DateTime(2025, 11, 20)).Length == 0,
            "Transaction date more than 3 days away from UTR date must not match.");
        Check(BankNarrationParser.SelectSharedReference(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SYNTR52025110500987654" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SYNTH25309987654" },
                "UPI",
                txDate).Length == 0,
            "UPI method must not use RBI RTGS/NEFT canonical fallback.");
    }

    private static void RunNewlineCleanupTests()
    {
        var normalized = BankNarrationParser.Normalize(
            "DEP TFR\\nRTGS ALPHA TRADING SYNTH25309987654\\nTRF FR INTERNAL ACCOUNT");
        Check(normalized == "DEP TFR RTGS ALPHA TRADING SYNTH25309987654 TRF FR INTERNAL ACCOUNT",
            $"Literal \\n was not normalized cleanly: [{normalized}].");
        Check(BankNarrationParser.DetectExplicitMethod(normalized) == "RTGS",
            "DetectExplicitMethod should identify RTGS after \\n cleanup.");

        var mixed = BankNarrationParser.Normalize("CSH DEP\\nDeposit by TEST\\r\\nBRANCH");
        Check(mixed == "CSH DEP DEPOSIT BY TEST BRANCH",
            $"Mixed case literal \\n/\\r\\n cleanup failed: [{mixed}].");

        // Uppercase backslash-delimited bank narrations must preserve leading R/N (no false \r/\n strip)
        Check(BankNarrationParser.Normalize(@"NEFT\RAJESH\12345") == "NEFT RAJESH 12345",
            "Uppercase backslash path NEFT\\RAJESH\\12345 lost its leading R.");
        Check(BankNarrationParser.Normalize(@"UPI\NARESH\999") == "UPI NARESH 999",
            "Uppercase backslash path UPI\\NARESH\\999 lost its leading N.");
    }

    private static void RunEndToEndReconcileTest()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "aah_utr_match_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "test.db");
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        try
        {
            using (var bootstrap = new SqliteConnection(connStr))
            {
                bootstrap.Open();
                using var command = bootstrap.CreateCommand();
                command.CommandText = "CREATE TABLE Clients(Id INTEGER PRIMARY KEY); INSERT INTO Clients(Id) VALUES(101);";
                command.ExecuteNonQuery();
            }

            var bankingRepo = new SqliteBankingRepository(connStr);
            bankingRepo.Initialize();
            var masterRepo = new SqliteMasterRepository(connStr);
            masterRepo.Initialize();

            const long clientId = 101;
            const string fy = "2025-26";
            var bankA = bankingRepo.UpsertAccount(new BankAccountRecord
            {
                ClientId = clientId,
                FinancialYear = fy,
                BankName = "SYNTHETIC BANK ONE",
                AccountNumber = "900000100001",
                AccountType = "CA",
                LedgerName = "Synthetic Bank One CA"
            });
            var bankB = bankingRepo.UpsertAccount(new BankAccountRecord
            {
                ClientId = clientId,
                FinancialYear = fy,
                BankName = "SYNTHETIC CO-OP BANK",
                AccountNumber = "900000200002",
                AccountType = "SB",
                LedgerName = "Synthetic Co-op Bank SB"
            });

            var txDate = new DateTime(2025, 11, 5);
            var stmtA = bankingRepo.SaveStatement(new BankStatementRecord
            {
                ClientId = clientId,
                FinancialYear = fy,
                BankAccountId = bankA.Id,
                SourceFile = "synthetic_bank_a.pdf",
                SourceSheet = "Statement",
                FileHash = "hash-utr-stmt-a",
                PeriodFrom = txDate,
                PeriodTo = txDate,
                OpeningBalance = 500000m,
                OpeningBalanceSource = "SOURCE",
                SourceOpeningBalance = 500000m,
                ClosingBalance = 0m,
                ClosingBalanceSource = "SOURCE",
                SourceClosingBalance = 0m,
                TotalDebit = 500000m,
                TotalCredit = 0m,
                TransactionCount = 1,
                ReconciliationStatus = "Reconciled"
            });
            var stmtB = bankingRepo.SaveStatement(new BankStatementRecord
            {
                ClientId = clientId,
                FinancialYear = fy,
                BankAccountId = bankB.Id,
                SourceFile = "synthetic_bank_b.pdf",
                SourceSheet = "Bank Statement",
                FileHash = "hash-utr-stmt-b",
                PeriodFrom = txDate,
                PeriodTo = txDate,
                OpeningBalance = -1000m,
                OpeningBalanceSource = "SOURCE",
                SourceOpeningBalance = -1000m,
                ClosingBalance = -501000m,
                ClosingBalanceSource = "SOURCE",
                SourceClosingBalance = -501000m,
                TotalDebit = 0m,
                TotalCredit = 500000m,
                TransactionCount = 1,
                ReconciliationStatus = "Reconciled"
            });

            bankingRepo.InsertTransactions(
            [
                new BankTransactionRecord
                {
                    ClientId = clientId,
                    FinancialYear = fy,
                    StatementId = stmtA,
                    BankAccountId = bankA.Id,
                    TransactionDate = txDate,
                    Narration = "RTGS/ SYNTR52025110500987654/ ALPHA TRADING 000111",
                    NormalizedNarration = BankNarrationParser.Normalize("RTGS/ SYNTR52025110500987654/ ALPHA TRADING 000111"),
                    ReferenceNumber = "SYNTR52025110500987654",
                    Debit = 500000m,
                    Credit = 0m,
                    Mode = "Payment",
                    Fingerprint = "fp-utr-debit-1",
                    RecordStatus = "Active"
                },
                new BankTransactionRecord
                {
                    ClientId = clientId,
                    FinancialYear = fy,
                    StatementId = stmtB,
                    BankAccountId = bankB.Id,
                    TransactionDate = txDate,
                    Narration = "DEP TFR\\nRTGS ALPHA TRADING SYNTH25309987654\\nTRF FR INTERNAL ACCOUNT",
                    NormalizedNarration = BankNarrationParser.Normalize("DEP TFR\\nRTGS ALPHA TRADING SYNTH25309987654\\nTRF FR INTERNAL ACCOUNT"),
                    ReferenceNumber = "SYNTH25309987654",
                    Debit = 0m,
                    Credit = 500000m,
                    Mode = "Receipt",
                    Fingerprint = "fp-utr-credit-1",
                    RecordStatus = "Active"
                }
            ]);

            var service = new BankTransferReconciliationService(bankingRepo, masterRepo);
            var matches = service.Reconcile(clientId, fy);
            Check(matches.Count == 1, $"Expected 1 inter-bank match, got {matches.Count}.");
            Check(matches[0].Status == "Matched", $"Expected Matched status, got {matches[0].Status}.");
            Check(matches[0].ReferenceNumber.Equals("SYNTR52025110500987654", StringComparison.OrdinalIgnoreCase),
                $"Expected matched ReferenceNumber SYNTR52025110500987654, got {matches[0].ReferenceNumber}.");
            Check(matches[0].Amount == 500000m, $"Expected matched amount 500000, got {matches[0].Amount}.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static void RunBankingViewLedgerColumnContractTest()
    {
        var root = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "AAH", "src", "AccountingAutomationHub.Desktop", "Views", "BankingView.xaml"));
        var marker = "<DataGridTemplateColumn Header=\"Ledger\"";
        var start = xaml.IndexOf(marker, StringComparison.Ordinal);
        Check(start >= 0, "Could not find BankStatementGrid Ledger column in BankingView.xaml.");
        var end = xaml.IndexOf("</DataGridTemplateColumn>", start, StringComparison.Ordinal);
        Check(end > start, "Could not find closing </DataGridTemplateColumn> for Ledger column.");
        var ledgerBlock = xaml[start..end];

        Check(ledgerBlock.Contains("IsReadOnly=\"True\"", StringComparison.Ordinal),
            "Ledger column must be IsReadOnly=\"True\" with SearchableComboBox in CellTemplate.");
        Check(!ledgerBlock.Contains("CellEditingTemplate", StringComparison.Ordinal),
            "Ledger column must not hide SearchableComboBox inside CellEditingTemplate.");
        Check(ledgerBlock.Contains("<DataGridTemplateColumn.CellTemplate>", StringComparison.Ordinal) &&
              ledgerBlock.Contains("<controls:SearchableComboBox", StringComparison.Ordinal),
            "Ledger column must render SearchableComboBox inside CellTemplate.");
        foreach (var requiredAttr in new[]
        {
            "UpdateSourceTrigger=Explicit",
            "AllowCustomText=\"False\"",
            "SelectFirstVisibleOnNoMatch=\"False\"",
            "UpdateSourceWhileTyping=\"False\"",
            "HandleEnterKey=\"False\"",
            "OpenOnFocus=\"True\"",
            "EnableMouseWheelSelection=\"False\""
        })
        {
            Check(ledgerBlock.Contains(requiredAttr, StringComparison.Ordinal),
                $"BankStatementGrid Ledger SearchableComboBox missing {requiredAttr}.");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "AAH", "src")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate AAH repository root.");
    }
}
