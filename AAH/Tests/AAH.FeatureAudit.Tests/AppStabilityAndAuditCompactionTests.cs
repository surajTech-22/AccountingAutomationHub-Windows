using System.Globalization;
using System.IO.Compression;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Infrastructure.Audit;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Clients;
using AccountingAutomationHub.Infrastructure.Finance;
using AccountingAutomationHub.Infrastructure.Governance;
using AccountingAutomationHub.Infrastructure.Imports;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

internal static class AppStabilityAndAuditCompactionTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Run()
    {
        RunAuditTriggerAndCompactionTest();
        RunVerifiedBackupFileLockTest();
        RunBranchChildGstProfileSkipTest();
        RunAuditRepositoryPolicyTest();
        RunPerformanceTraceRotationTest();
        Console.WriteLine("PASS AppStabilityAndAuditCompaction: trigger no-op guard, startup audit compaction, non-pooled verified backup, branch GST skip, Audit SQLite policy & trace rotation.");
    }

    private static void RunAuditTriggerAndCompactionTest()
    {
        var tempRoot = CreateTempRoot("audit_compact");
        try
        {
            ApplicationDataPaths.ConfigureRootOverride(tempRoot);
            ApplicationDataPaths.EnsureFolders();

            var clientRepo = new SqliteClientRepository();
            clientRepo.Initialize();
            var savedClient = clientRepo.Save(new ClientProfile
            {
                ClientCode = "SYNAUDIT01",
                ClientName = "Synthetic Audit Co",
                FinancialYear = "2025-26",
                Gstin = "29SYNTH1234A1Z5"
            });

            var bankingRepo = new SqliteBankingRepository();
            bankingRepo.Initialize();
            var financeRepo = new SqliteFinanceControlRepository();
            financeRepo.Initialize();

            var bank = bankingRepo.UpsertAccount(new BankAccountRecord
            {
                ClientId = savedClient.Id,
                FinancialYear = "2025-26",
                BankName = "SYNTHETIC BANK",
                AccountNumber = "900000888001",
                AccountType = "CA"
            });
            var stmtId = bankingRepo.SaveStatement(new BankStatementRecord
            {
                ClientId = savedClient.Id,
                FinancialYear = "2025-26",
                BankAccountId = bank.Id,
                SourceFile = "synthetic.pdf",
                SourceSheet = "Sheet1",
                FileHash = "hash-audit-compact-1",
                PeriodFrom = new DateTime(2025, 4, 1),
                PeriodTo = new DateTime(2025, 4, 30)
            });

            bankingRepo.InsertTransactions(
            [
                new BankTransactionRecord
                {
                    ClientId = savedClient.Id,
                    FinancialYear = "2025-26",
                    StatementId = stmtId,
                    BankAccountId = bank.Id,
                    TransactionDate = new DateTime(2025, 4, 10),
                    Narration = "SYNTHETIC PAYMENT",
                    NormalizedNarration = "SYNTHETIC PAYMENT",
                    Debit = 1000m,
                    Mode = "Payment",
                    LedgerName = "Bank Charges",
                    MappingStatus = "Mapped",
                    Fingerprint = "fp-audit-compact-1",
                    RecordStatus = "Active"
                }
            ]);

            var row = bankingRepo.GetTransactions(savedClient.Id, "2025-26").Single();

            // 1. Trigger WHEN guard must suppress identical no-op updates
            var eventsBeforeNoop = CountAuditEvents(ApplicationDataPaths.DatabasePath);
            using (var conn = new SqliteConnection($"Data Source={ApplicationDataPaths.DatabasePath};Pooling=False;"))
            {
                conn.Open();
                using var upd = conn.CreateCommand();
                upd.CommandText = "UPDATE BankTransactions SET Narration='SYNTHETIC PAYMENT' WHERE Id=$Id;";
                upd.Parameters.AddWithValue("$Id", row.Id);
                upd.ExecuteNonQuery();
            }
            var eventsAfterNoop = CountAuditEvents(ApplicationDataPaths.DatabasePath);
            Check(eventsAfterNoop == eventsBeforeNoop,
                $"Expected 0 audit rows on no-op BankTransactions UPDATE, got +{eventsAfterNoop - eventsBeforeNoop}.");

            // Real change to LedgerName must still record 1 Update row
            row.LedgerName = "Office Expenses";
            bankingRepo.SaveTransaction(row);
            var eventsAfterReal = CountAuditEvents(ApplicationDataPaths.DatabasePath);
            Check(eventsAfterReal == eventsAfterNoop + 1,
                $"Expected +1 audit row on real LedgerName change, got +{eventsAfterReal - eventsAfterNoop}.");

            // 2. Seed historical legacy no-op bloat & clear compaction marker to verify startup compaction
            using (var conn = new SqliteConnection($"Data Source={ApplicationDataPaths.DatabasePath};Pooling=False;"))
            {
                conn.Open();
                using var tx = conn.BeginTransaction();
                for (var i = 0; i < 25; i++)
                {
                    using var ins = conn.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText =
                        "INSERT INTO FinanceAuditEvents(ClientId,FinancialYear,Module,EntityType,EntityKey,Operation,OldValue,NewValue,ChangedUtc) " +
                        "VALUES($C,'2025-26','Banking','BankTransactions','K','Update','Bank Charges|Mapped','Bank Charges|Mapped','2026-08-01T00:00:00Z');";
                    ins.Parameters.AddWithValue("$C", savedClient.Id);
                    ins.ExecuteNonQuery();
                }
                using var reset = conn.CreateCommand();
                reset.Transaction = tx;
                reset.CommandText = "DELETE FROM FinanceMaintenanceState WHERE RepairKey IN ('FIX-FINANCE-AUDIT-NOOP-COMPACT-V1','FIX-FINANCE-AUDIT-VACUUM-V1');";
                reset.ExecuteNonQuery();
                tx.Commit();
            }

            var compaction = SqliteFinanceControlRepository.RunStartupAuditCompactionIfNeeded(ApplicationDataPaths.DatabasePath);
            Check(compaction.DeletedOrCompactedRows == 25,
                $"Expected 25 legacy no-op audit rows compacted, got {compaction.DeletedOrCompactedRows}. Status={compaction.Status}");

            // Genuine Insert + real Update rows must still exist, zero no-op rows left
            var remainingNoop = QueryScalarLong(ApplicationDataPaths.DatabasePath,
                "SELECT COUNT(*) FROM FinanceAuditEvents WHERE Operation='Update' AND OldValue=NewValue;");
            var remainingReal = QueryScalarLong(ApplicationDataPaths.DatabasePath,
                "SELECT COUNT(*) FROM FinanceAuditEvents WHERE NOT (Operation='Update' AND OldValue=NewValue);");
            Check(remainingNoop == 0, $"Expected 0 no-op audit rows after compaction, found {remainingNoop}.");
            Check(remainingReal == eventsAfterReal, $"Expected {eventsAfterReal} genuine audit rows preserved, found {remainingReal}.");

            var secondRun = SqliteFinanceControlRepository.RunStartupAuditCompactionIfNeeded(ApplicationDataPaths.DatabasePath);
            Check(secondRun.DeletedOrCompactedRows == 0, "Startup audit compaction must be idempotent.");
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    private static void RunVerifiedBackupFileLockTest()
    {
        var tempRoot = CreateTempRoot("verified_backup");
        try
        {
            ApplicationDataPaths.ConfigureRootOverride(tempRoot);
            ApplicationDataPaths.EnsureFolders();

            var clientRepo = new SqliteClientRepository();
            clientRepo.Initialize();
            clientRepo.Save(new ClientProfile
            {
                ClientCode = "SYNBKP01",
                ClientName = "Synthetic Backup Client",
                FinancialYear = "2025-26"
            });
            var auditRepo = new SqliteAuditRepository();
            auditRepo.Initialize();
            var govRepo = new SqliteGovernanceRepository();
            govRepo.Initialize();

            var service = new VerifiedBackupService(govRepo);
            var record = service.CreateVerifiedBackup("Manual");
            Check(record.IsVerified && record.IntegrityResult.Equals("ok", StringComparison.OrdinalIgnoreCase),
                $"Expected verified backup ok, got IsVerified={record.IsVerified}, Integrity={record.IntegrityResult}.");
            Check(File.Exists(record.BackupPath), $"Verified backup file missing: {record.BackupPath}");

            using (var zip = ZipFile.OpenRead(record.BackupPath))
            {
                Check(zip.GetEntry("Data/accounting-hub.db") is not null, "Backup zip missing Data/accounting-hub.db.");
                Check(zip.GetEntry("Data/audit-hub.db") is not null, "Backup zip missing Data/audit-hub.db.");
            }
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    private static void RunBranchChildGstProfileSkipTest()
    {
        var tempRoot = CreateTempRoot("branch_gst_skip");
        try
        {
            ApplicationDataPaths.ConfigureRootOverride(tempRoot);
            ApplicationDataPaths.EnsureFolders();

            var clientRepo = new SqliteClientRepository();
            clientRepo.Initialize();
            var parent = clientRepo.Save(new ClientProfile
            {
                ClientCode = "SYNPAR01",
                ClientName = "Synthetic Parent Co",
                FinancialYear = "2025-26",
                Gstin = "29SYNTH5678B1Z2",
                Pan = "SYNTH5678B"
            });

            var branchRepo = new SqliteInternalBranchAccountRepository(ApplicationDataPaths.DatabasePath);
            var child = branchRepo.GetOrCreate(parent.Id, "branch-ka", "Karnataka Branch", parent.Gstin);

            // Must NOT throw "Branch accounting Client profiles are maintained by the parent Branch workspace."
            var returned = clientRepo.ApplyImportedGstProfile(child.Id, new GstinProfileLookupResult
            {
                Gstin = child.Gstin,
                LegalName = "Updated Legal Name",
                TradeName = "Updated Trade Name"
            });
            Check(returned.Id == child.Id && returned.ClientName == child.ClientName,
                "Private branch child GST profile update should be skipped without throwing or mutating child.");
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    private static void RunAuditRepositoryPolicyTest()
    {
        var tempRoot = CreateTempRoot("audit_policy");
        try
        {
            ApplicationDataPaths.ConfigureRootOverride(tempRoot);
            ApplicationDataPaths.EnsureFolders();

            var auditRepo = new SqliteAuditRepository();
            auditRepo.Initialize();

            // Verify Audit DB has WAL + foreign_keys=ON via SqliteConnectionPolicy
            using var conn = SqliteConnectionPolicy.Open($"Data Source={ApplicationDataPaths.AuditDatabasePath};Cache=Private;");
            using var timeoutCmd = conn.CreateCommand();
            timeoutCmd.CommandText = "PRAGMA busy_timeout;";
            var busyTimeout = Convert.ToInt32(timeoutCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            using var fkCmd = conn.CreateCommand();
            fkCmd.CommandText = "PRAGMA foreign_keys;";
            var fk = Convert.ToInt32(fkCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            Check(busyTimeout == SqliteConnectionPolicy.BusyTimeoutMilliseconds, $"Expected busy_timeout={SqliteConnectionPolicy.BusyTimeoutMilliseconds}, got {busyTimeout}.");
            Check(fk == 1, $"Expected foreign_keys=1, got {fk}.");
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    private static void RunPerformanceTraceRotationTest()
    {
        var tempRoot = CreateTempRoot("perf_rotate");
        try
        {
            var folder = Path.Combine(tempRoot, "Logs");
            Directory.CreateDirectory(folder);
            var logPath = Path.Combine(folder, "performance-trace.csv");
            File.WriteAllBytes(logPath, new byte[5 * 1024 * 1024 + 64]);

            ApplicationPerformanceTrace.RotateIfNeeded(folder, logPath);
            Check(!File.Exists(logPath), "Expected oversized performance-trace.csv to be rotated.");
            Check(Directory.GetFiles(folder, "performance-trace-*.csv").Length == 1,
                "Expected 1 rotated archive after RotateIfNeeded.");
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    private static long CountAuditEvents(string dbPath) =>
        QueryScalarLong(dbPath, "SELECT COUNT(*) FROM FinanceAuditEvents;");

    private static long QueryScalarLong(string dbPath, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False;");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string CreateTempRoot(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aah_{suffix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CleanupTempRoot(string path)
    {
        ApplicationDataPaths.ConfigureRootOverride(null);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(path, true); } catch { }
    }
}
