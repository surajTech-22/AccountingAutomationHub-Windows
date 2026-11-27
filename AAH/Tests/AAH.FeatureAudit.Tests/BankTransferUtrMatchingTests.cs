using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Masters;
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
        RunFlexibleFilterMatcherTests();
        RunContraPolicyAndMaintenanceTests();
        RunEndToEndReconcileTest();
        RunBankingViewLedgerColumnContractTest();
        Console.WriteLine("PASS BankTransferUtrMatching: canonical RBI RTGS/NEFT UTR matching, escaped newline cleanup, flexible date/amount filters, Contra policy & DB maintenance.");
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

        var cleanedDisplay = BankNarrationParser.CleanLiteralEscapedNewlines("DEP TFR\\nRTGS TEST BUYER\\nTRF FR INTERNAL ACCOUNT");
        Check(cleanedDisplay == "DEP TFR RTGS TEST BUYER TRF FR INTERNAL ACCOUNT",
            $"CleanLiteralEscapedNewlines failed: [{cleanedDisplay}].");
    }

    private static void RunFlexibleFilterMatcherTests()
    {
        var nov5 = new DateTime(2025, 11, 5);
        var nov15 = new DateTime(2025, 11, 15);
        var nov25 = new DateTime(2025, 11, 25);

        foreach (var matchingDateQuery in new[] { "5-11-25", "05-11-25", "5.11.25", "5/11/25", "5-11-2025", "05.11.2025", "2025-11-05", "5-11", "05-11", "5-", "5-Nov-25" })
        {
            Check(FlexibleFilterMatcher.MatchesDate(nov5, matchingDateQuery),
                $"Expected query '{matchingDateQuery}' to match 05.11.2025.");
        }

        foreach (var nonMatchingForNov15 in new[] { "5-11-25", "5.11.25", "5-11", "5-" })
        {
            Check(!FlexibleFilterMatcher.MatchesDate(nov15, nonMatchingForNov15),
                $"Query '{nonMatchingForNov15}' must not falsely match 15.11.2025.");
            Check(!FlexibleFilterMatcher.MatchesDate(nov25, nonMatchingForNov15),
                $"Query '{nonMatchingForNov15}' must not falsely match 25.11.2025.");
        }

        Check(FlexibleFilterMatcher.MatchesDate(nov5, "5-11-25", exactOnly: true),
            "Exact date match should succeed for 5-11-25 vs 05.11.2025.");
        Check(!FlexibleFilterMatcher.MatchesDate(nov5, "5-11", exactOnly: true),
            "Partial date 5-11 should not satisfy exactOnly=true.");

        foreach (var matchingAmountQuery in new[] { "1000000", "1,000,000", "10,00,000", "1000000.00", "10000", "INR 1000000", "1000000 Dr" })
        {
            Check(FlexibleFilterMatcher.MatchesAmount(1000000m, matchingAmountQuery),
                $"Expected amount query '{matchingAmountQuery}' to match 1,000,000.00.");
        }

        Check(FlexibleFilterMatcher.MatchesAmount(1000000m, "1000000", exactOnly: true),
            "Exact amount match should succeed for 1000000.");
        Check(!FlexibleFilterMatcher.MatchesAmount(1000000m, "10000", exactOnly: true),
            "Partial amount 10000 should not satisfy exactOnly=true.");
        Check(!FlexibleFilterMatcher.TryExtractAmountQuery("TRANSFER", out _, out _),
            "Plain narration words must not parse as numeric amount queries.");

        // Numeric & date comparison operators (>, >=, <, <=, !=, comma-AND ranges)
        Check(FlexibleFilterMatcher.MatchesAmount(500m, ">20"), ">20 must match 500.");
        Check(!FlexibleFilterMatcher.MatchesAmount(20m, ">20"), ">20 must exclude exact 20.");
        Check(FlexibleFilterMatcher.MatchesAmount(20m, ">=20"), ">=20 must include 20.");
        Check(FlexibleFilterMatcher.MatchesAmount(15m, "<20"), "<20 must match 15.");
        Check(!FlexibleFilterMatcher.MatchesAmount(20m, "<20"), "<20 must exclude exact 20.");
        Check(FlexibleFilterMatcher.MatchesAmount(20m, "<=20"), "<=20 must include 20.");
        Check(FlexibleFilterMatcher.MatchesAmount(5000m, ">=1000, <=10000"), "Comma-AND range >=1000, <=10000 must match 5000.");
        Check(!FlexibleFilterMatcher.MatchesAmount(500m, ">=1000, <=10000"), "Comma-AND range >=1000, <=10000 must reject 500.");
        Check(!FlexibleFilterMatcher.MatchesAmount(0m, "<5000", ignoreZeroForLessThan: true),
            "Empty 0.00 Debit/Credit cell must not falsely match <5000 when ignoreZeroForLessThan is enabled.");
        Check(FlexibleFilterMatcher.MatchesAmount(0m, "<=0", ignoreZeroForLessThan: true),
            "Explicit <=0 must still match 0.00.");
        Check(FlexibleFilterMatcher.MatchesDate(nov15, ">05-11-2025"), ">05-11-2025 must match 15-11-2025.");
        Check(!FlexibleFilterMatcher.MatchesDate(nov5, ">05-11-2025"), ">05-11-2025 must exclude 05-11-2025.");
        Check(FlexibleFilterMatcher.MatchesDate(nov15, ">=05-11-2025, <=20-11-2025"), "Date range must match 15-11-2025.");
        Check(FlexibleFilterMatcher.DescribeComparisonExpression(">20").Contains("Greater than 20", StringComparison.Ordinal),
            "DescribeComparisonExpression should spell out >20 in plain English.");
    }

    private static void RunContraPolicyAndMaintenanceTests()
    {
        var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Secured Loan From Banks"] = "Secured Loans"
        };
        var ledgers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Synthetic Bank OD - 6810"] = "Secured Loan From Banks",
            ["Cash"] = "Cash-in-Hand",
            ["Bank Charges"] = "Indirect Expenses"
        };

        Check(BankContraLedgerPolicy.ResolveModeForLedger("Payment", "Synthetic Bank OD - 6810", 32000m, 0m, ledgers, groups) == "Contra",
            "Mapping a Payment row to a Secured Loan From Banks ledger must switch Mode to Contra.");
        Check(BankContraLedgerPolicy.ResolveModeForLedger("Receipt", "Cash", 0m, 1000m, ledgers, groups) == "Contra",
            "Mapping a Receipt row to Cash-in-Hand must switch Mode to Contra.");
        Check(BankContraLedgerPolicy.ResolveModeForLedger("Contra", "Bank Charges", 20m, 0m, ledgers, groups) == "Payment",
            "Re-mapping a Contra debit row to Indirect Expenses must switch Mode back to Payment.");
        Check(!BankContraLedgerPolicy.IsBankOrOverdraftGroup("Cash-in-Hand"),
            "IsBankOrOverdraftGroup must exclude Cash-in-Hand so Bank rename/list selectors stay bank-only.");
        Check(BankContraLedgerPolicy.IsBankOrOverdraftGroup("Secured Loan From Banks") &&
              BankContraLedgerPolicy.IsBankOrOverdraftGroup("Cash Credit"),
            "IsBankOrOverdraftGroup must match Secured Loan From Banks and Cash Credit.");

        var tempRoot = Path.Combine(Path.GetTempPath(), "aah_maint_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "maint.db");
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
                using var cmd = bootstrap.CreateCommand();
                cmd.CommandText = "CREATE TABLE Clients(Id INTEGER PRIMARY KEY); INSERT INTO Clients(Id) VALUES(202);";
                cmd.ExecuteNonQuery();
            }

            var bankingRepo = new SqliteBankingRepository(connStr);
            bankingRepo.Initialize();
            var masterRepo = new SqliteMasterRepository(connStr);
            masterRepo.Initialize();

            const long clientId = 202;
            const string fy = "2025-26";
            masterRepo.Save(new MasterRecord
            {
                ClientId = clientId,
                FinancialYear = fy,
                Type = "Group",
                Name = "Secured Loan From Banks",
                Parent = "Secured Loans"
            });
            masterRepo.Save(new MasterRecord
            {
                ClientId = clientId,
                FinancialYear = fy,
                Type = "Ledger",
                Name = "Synthetic Bank OD - 6810",
                Parent = "Secured Loan From Banks"
            });
            masterRepo.Save(new MasterRecord
            {
                ClientId = clientId,
                FinancialYear = fy,
                Type = "Ledger",
                Name = "Bank Charges",
                Parent = "Indirect Expenses"
            });

            var bank = bankingRepo.UpsertAccount(new BankAccountRecord
            {
                ClientId = clientId,
                FinancialYear = fy,
                BankName = "SYNTHETIC CO-OP BANK",
                AccountNumber = "900000300003",
                AccountType = "SB"
            });
            var stmtId = bankingRepo.SaveStatement(new BankStatementRecord
            {
                ClientId = clientId,
                FinancialYear = fy,
                BankAccountId = bank.Id,
                SourceFile = "synthetic_stmt.pdf",
                SourceSheet = "Bank Statement",
                FileHash = "hash-maint-stmt",
                PeriodFrom = new DateTime(2025, 5, 15),
                PeriodTo = new DateTime(2025, 11, 5)
            });

            bankingRepo.InsertTransactions(
            [
                new BankTransactionRecord
                {
                    ClientId = clientId,
                    FinancialYear = fy,
                    StatementId = stmtId,
                    BankAccountId = bank.Id,
                    TransactionDate = new DateTime(2025, 5, 15),
                    Narration = "WDL TFR\\nINT\\nTRF TO 9000006810",
                    NormalizedNarration = "WDL TFR INT TRF TO 9000006810",
                    Debit = 32000m,
                    Mode = "Payment",
                    LedgerName = "Synthetic Bank OD - 6810",
                    MappingStatus = "Mapped",
                    Fingerprint = "fp-maint-1",
                    RecordStatus = "Active"
                },
                new BankTransactionRecord
                {
                    ClientId = clientId,
                    FinancialYear = fy,
                    StatementId = stmtId,
                    BankAccountId = bank.Id,
                    TransactionDate = new DateTime(2025, 6, 30),
                    Narration = "SMS CHARGE",
                    NormalizedNarration = "SMS CHARGE",
                    Debit = 20m,
                    Mode = "Payment",
                    LedgerName = "Bank Charges",
                    MappingStatus = "Mapped",
                    Fingerprint = "fp-maint-2",
                    RecordStatus = "Active"
                },
                new BankTransactionRecord
                {
                    ClientId = clientId,
                    FinancialYear = fy,
                    StatementId = stmtId,
                    BankAccountId = bank.Id,
                    TransactionDate = new DateTime(2025, 11, 5),
                    Narration = "WDL TFR\\nOD TRANSFER",
                    NormalizedNarration = "WDL TFR OD TRANSFER",
                    Debit = 5000m,
                    Mode = "Payment",
                    MappingStatus = "Mapping Required",
                    Fingerprint = "fp-maint-3",
                    RecordStatus = "Active"
                }
            ]);

            // Reset the two v4.0.0.647 maintenance markers to simulate an existing DB upgrading to v4.0.0.647
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var del = conn.CreateCommand();
                del.CommandText =
                    "DELETE FROM BankingMaintenanceState WHERE RepairKey IN ('FIX-BANK-ESCAPED-NEWLINES-V1','FIX-BANK-CONTRA-MODE-FOR-BANK-OD-CASH-V1');";
                del.ExecuteNonQuery();
            }

            // Use a distinct connection string to simulate a fresh app process restart upgrading
            // an existing database past SqliteSchemaInitializationGate's in-process gate cache.
            var upgradedConnStr = new SqliteConnectionStringBuilder(connStr)
            {
                DefaultTimeout = 31
            }.ToString();
            var upgradedRepo = new SqliteBankingRepository(upgradedConnStr);
            upgradedRepo.Initialize();

            var afterUpgrade = upgradedRepo.GetTransactions(clientId, fy, includeArchived: false).OrderBy(item => item.Id).ToArray();
            Check(afterUpgrade[0].Narration == "WDL TFR INT TRF TO 9000006810",
                $"Startup maintenance should strip \\n, got [{afterUpgrade[0].Narration}].");
            Check(afterUpgrade[0].Mode == "Contra",
                $"Startup maintenance should switch Bank OD row Mode to Contra, got [{afterUpgrade[0].Mode}].");
            Check(afterUpgrade[1].Mode == "Payment",
                $"Non-bank Bank Charges row should remain Payment, got [{afterUpgrade[1].Mode}].");

            // ApplyLedgerMapping batch assignment to a Secured Loan From Banks ledger must set Mode = Contra
            upgradedRepo.ApplyLedgerMapping(clientId, fy,
                new BankLedgerMapping
                {
                    ClientId = clientId,
                    FinancialYear = fy,
                    BankFilter = bank.AuthorityName,
                    VoucherType = "Payment",
                    SummaryName = "WDL TFR OD TRANSFER",
                    LedgerName = "Synthetic Bank OD - 6810",
                    TransactionIds = [afterUpgrade[2].Id],
                    Status = "Mapped"
                });

            var afterMapping = upgradedRepo.GetTransactions(clientId, fy, includeArchived: false)
                .Single(item => item.Id == afterUpgrade[2].Id);
            Check(afterMapping.LedgerName == "Synthetic Bank OD - 6810" && afterMapping.Mode == "Contra",
                $"ApplyLedgerMapping to Secured Loan From Banks must set Mode=Contra, got Ledger=[{afterMapping.LedgerName}] Mode=[{afterMapping.Mode}].");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempRoot, true); } catch { }
        }
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
