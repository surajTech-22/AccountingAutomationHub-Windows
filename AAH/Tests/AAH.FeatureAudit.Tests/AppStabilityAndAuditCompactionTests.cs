using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Audit;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Clients;
using AccountingAutomationHub.Infrastructure.Finance;
using AccountingAutomationHub.Infrastructure.Governance;
using AccountingAutomationHub.Infrastructure.Imports;
using AccountingAutomationHub.Infrastructure.Persistence;
using AccountingAutomationHub.Infrastructure.Purchases;
using AccountingAutomationHub.Infrastructure.Sales;
using AccountingAutomationHub.Infrastructure.Tally;
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
        RunInvoiceVisionChildUnlinkedContractTest();
        RunCorruptBackupRecoveryTest();
        RunTallyAuthorityGzipCompressionTest();
        RunEmptyRecordJsonStockSnapshotTest();
        RunCrossThreadDispatcherContractTest();
        RunLogRetentionPruningTest();
        Console.WriteLine("PASS AppStabilityAndAuditCompaction: trigger no-op guard, startup audit compaction, non-pooled verified backup + corrupt recovery, branch GST skip, Audit SQLite policy, trace & log retention, Tally XML/JSON gzip compaction, legacy empty RecordJson handling, cross-thread dispatcher marshalling & InvoiceVisionChild unlink.");
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

    private static void RunInvoiceVisionChildUnlinkedContractTest()
    {
        var root = FindRepoRoot();
        var bridge = File.ReadAllText(Path.Combine(root, "AAH", "src", "AccountingAutomationHub.Desktop", "Services", "InvoiceOcrChildBridge.cs"));
        var launcher = File.ReadAllText(Path.Combine(root, "AAH", "src", "AccountingAutomationHub.Desktop", "Services", "InvoiceVisionChildLauncher.cs"));
        var server = File.ReadAllText(Path.Combine(root, "AAH", "src", "AccountingAutomationHub.Desktop", "Services", "SecureLanServerService.cs"));
        var mobileView = File.ReadAllText(Path.Combine(root, "AAH", "src", "AccountingAutomationHub.Desktop", "Views", "MobileLinkView.xaml"));
        var dumpView = File.ReadAllText(Path.Combine(root, "AAH", "src", "AccountingAutomationHub.Desktop", "Views", "UniversalDumpWindow.xaml"));

        Check(bridge.Contains("public const bool IsLinked = false;", StringComparison.Ordinal) &&
              !bridge.Contains("Process.Start", StringComparison.Ordinal),
            "InvoiceOcrChildBridge must be unlinked (IsLinked = false) and must not spawn child processes.");
        Check(launcher.Contains("public const bool IsLinked = false;", StringComparison.Ordinal) &&
              !launcher.Contains("Process.Start", StringComparison.Ordinal),
            "InvoiceVisionChildLauncher must be unlinked (IsLinked = false) and must not spawn child processes.");
        Check(!server.Contains("InvoiceOcrChildBridge.TryStage1Async", StringComparison.Ordinal),
            "SecureLanServerService must not invoke InvoiceOcrChildBridge.TryStage1Async.");
        Check(mobileView.Contains("Visibility=\"Collapsed\"", StringComparison.Ordinal),
            "MobileLinkView must hide Invoice OCR Child buttons and banner.");
        Check(dumpView.Contains("Review in InvoiceVision", StringComparison.Ordinal) &&
              dumpView.Contains("Visibility=\"Collapsed\"", StringComparison.Ordinal),
            "UniversalDumpWindow must hide the Review in InvoiceVision button.");
    }

    private static void RunCorruptBackupRecoveryTest()
    {
        var tempRoot = CreateTempRoot("corrupt_backup");
        try
        {
            ApplicationDataPaths.ConfigureRootOverride(tempRoot);
            ApplicationDataPaths.EnsureFolders();

            var clientRepo = new SqliteClientRepository();
            clientRepo.Initialize();
            clientRepo.Save(new ClientProfile
            {
                ClientCode = "SYNCORR01",
                ClientName = "Synthetic Corrupt Backup Co",
                FinancialYear = "2025-26"
            });
            var auditRepo = new SqliteAuditRepository();
            auditRepo.Initialize();
            var govRepo = new SqliteGovernanceRepository();
            govRepo.Initialize();

            Directory.CreateDirectory(ApplicationDataPaths.VerifiedBackupFolder);
            var poisonName = $"AAH-Daily-{DateTime.Now:yyyyMMdd}-010203.aahbackup";
            var poisonPath = Path.Combine(ApplicationDataPaths.VerifiedBackupFolder, poisonName);
            File.WriteAllBytes(poisonPath, new byte[22]);

            var service = new VerifiedBackupService(govRepo);
            var record = service.RunScheduledBackupAsync().GetAwaiter().GetResult();
            Check(record is not null && record.IsVerified,
                $"Expected corrupt 22-byte backup to be pruned and replaced with a valid Daily backup, got IsVerified={record?.IsVerified}, Integrity={record?.IntegrityResult}.");
            Check(!File.Exists(poisonPath), "Expected 22-byte corrupt backup file to be deleted.");
            Check(File.Exists(record!.BackupPath) && new FileInfo(record.BackupPath).Length >= VerifiedBackupService.MinimumValidBackupBytes,
                $"Expected valid backup >= {VerifiedBackupService.MinimumValidBackupBytes} bytes at {record.BackupPath}.");
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    private static void RunTallyAuthorityGzipCompressionTest()
    {
        var tempRoot = CreateTempRoot("tally_gzip");
        try
        {
            ApplicationDataPaths.ConfigureRootOverride(tempRoot);
            ApplicationDataPaths.EnsureFolders();

            var clientRepo = new SqliteClientRepository();
            clientRepo.Initialize();
            var savedClient = clientRepo.Save(new ClientProfile
            {
                ClientCode = "SYNTLY01",
                ClientName = "Synthetic Tally Co",
                FinancialYear = "2025-26"
            });

            var tallyRepo = new SqliteTallyRepository();
            tallyRepo.Initialize();

            var largeXml = "<VOUCHER REMOTEID=\"syn-1\" VCHTYPE=\"Payment\">" +
                           string.Concat(Enumerable.Repeat("<ALLLEDGERENTRIES.LIST><LEDGERNAME>Synthetic Bank Ledger</LEDGERNAME><AMOUNT>-1250.00</AMOUNT></ALLLEDGERENTRIES.LIST>", 16)) +
                           "</VOUCHER>";
            var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(largeXml)));

            tallyRepo.UpsertVoucherXmlAuthority("COMP_KEY_1", "2025-26",
            [
                new TallyVoucherXmlSnapshot("101", "guid-101", "Payment", new DateTime(2025, 4, 15), largeXml) { AlterId = "10" }
            ]);

            using (var conn = new SqliteConnection($"Data Source={ApplicationDataPaths.DatabasePath};Pooling=False;"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT OriginalXml, ContentHash FROM TallyVoucherXmlAuthority WHERE MasterId='101';";
                using var r = cmd.ExecuteReader();
                Check(r.Read(), "Expected saved voucher XML row.");
                Check(r.GetString(0).StartsWith("AAH_GZIP_V1:", StringComparison.Ordinal),
                    "Expected TallyVoucherXmlAuthority.OriginalXml to be gzip-prefixed.");
                Check(r.GetString(1).Equals(expectedHash, StringComparison.OrdinalIgnoreCase),
                    "Expected ContentHash to remain SHA256 of plain XML.");
            }

            var loadedXml = tallyRepo.GetVoucherXmlAuthorityByMasterIds("COMP_KEY_1", "2025-26", ["101"]).Single();
            Check(loadedXml.OriginalXml == largeXml, "Expected decompressed voucher XML to match plain XML exactly.");

            var snapshot = new TallyReportSnapshot
            {
                DisplayName = "Statistics",
                ReportId = "Statistics",
                FromDate = new DateTime(2025, 4, 1),
                ToDate = new DateTime(2026, 3, 31),
                IsAvailable = true,
                Status = "Ready",
                RawResponse = string.Concat(Enumerable.Repeat("<LEDGER>Synthetic Ledger Line</LEDGER>", 30))
            };
            tallyRepo.UpsertReportAuthority("COMP_KEY_1", "2025-26", 55, snapshot);
            var loadedReport = tallyRepo.GetReportAuthority("COMP_KEY_1", "2025-26", "Statistics", new DateTime(2025, 4, 1), new DateTime(2026, 3, 31), 50);
            Check(loadedReport is not null && loadedReport.RawResponse == snapshot.RawResponse, "Expected decompressed TallyReportSnapshot to round-trip.");

            // Seed legacy uncompressed rows and run startup compaction
            using (var conn = new SqliteConnection($"Data Source={ApplicationDataPaths.DatabasePath};Pooling=False;"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    """
                    INSERT INTO TallyVoucherIndex(CompanyKey,FinancialYear,MasterId,AlterId,Guid,VoucherDate,VoucherType,VoucherNumber,Reference,PartyLedger,DebitLedgers,CreditLedgers,Amount,Narration,ContentHash,LastSeenUtc)
                    VALUES('COMP_KEY_1','2025-26','101','10','guid-101','2025-04-15','Payment','1','','Bank','','',100,'','','2026-01-01T00:00:00Z'),
                          ('COMP_KEY_1','2025-26','102','11','guid-102','2025-04-16','Payment','2','','Bank','','',100,'','','2026-01-01T00:00:00Z');
                    INSERT INTO TallyVoucherXmlAuthority(CompanyKey,FinancialYear,MasterId,AlterId,Guid,VoucherDate,VoucherType,OriginalXml,ContentHash,LastSeenUtc)
                    VALUES('COMP_KEY_1','2025-26','102','11','guid-102','2025-04-16','Payment',$Xml,$Hash,'2026-01-01T00:00:00Z');
                    INSERT INTO TallyReportAuthority(CompanyKey,FinancialYear,ReportName,FromDate,ToDate,AuthorityAlterId,SnapshotJson,RefreshedUtc)
                    VALUES('COMP_KEY_1','2025-26','Day Book','2025-04-01','2026-03-31',55,$Json,'2026-01-01T00:00:00Z');
                    """;
                cmd.Parameters.AddWithValue("$Xml", largeXml);
                cmd.Parameters.AddWithValue("$Hash", expectedHash);
                cmd.Parameters.AddWithValue("$Json", JsonSerializer.Serialize(snapshot));
                cmd.ExecuteNonQuery();
            }

            Check(tallyRepo.HasCompleteVoucherXmlAuthority("COMP_KEY_1", "2025-26", 2),
                "Expected HasCompleteVoucherXmlAuthority true for mixed compressed + plain rows.");

            var firstRun = SqliteTallyRepository.RunStartupAuthorityCompressionIfNeeded(ApplicationDataPaths.DatabasePath);
            Check(firstRun.CompressedVoucherRows == 1 && firstRun.CompressedReportRows == 1,
                $"Expected 1 voucher & 1 report legacy row compressed, got v={firstRun.CompressedVoucherRows}, r={firstRun.CompressedReportRows}. Status={firstRun.Status}");
            Check(tallyRepo.GetVoucherXmlAuthorityByMasterIds("COMP_KEY_1", "2025-26", ["102"]).Single().OriginalXml == largeXml,
                "Expected startup-compressed voucher XML to round-trip identically.");
            var secondRun = SqliteTallyRepository.RunStartupAuthorityCompressionIfNeeded(ApplicationDataPaths.DatabasePath);
            Check(secondRun.CompressedVoucherRows == 0 && secondRun.CompressedReportRows == 0,
                "Startup Tally authority compression must be idempotent.");
            _ = savedClient;
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    private static void RunEmptyRecordJsonStockSnapshotTest()
    {
        var tempRoot = CreateTempRoot("empty_record_json");
        try
        {
            ApplicationDataPaths.ConfigureRootOverride(tempRoot);
            ApplicationDataPaths.EnsureFolders();

            var clientRepo = new SqliteClientRepository();
            clientRepo.Initialize();
            var client = clientRepo.Save(new ClientProfile
            {
                ClientCode = "SYNJSON01",
                ClientName = "Synthetic Stock JSON Co",
                FinancialYear = "2025-26"
            });

            var purchaseRepo = new SqlitePurchaseRecordRepository();
            purchaseRepo.Initialize();
            var salesRepo = new SqliteSalesRecordRepository();
            salesRepo.Initialize();

            using (var conn = new SqliteConnection($"Data Source={ApplicationDataPaths.DatabasePath};Pooling=False;"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    """
                    INSERT INTO PurchaseImportBatches(Id,ClientId,ClientGstin,FinancialYear,ImportMode,Status,CreatedUtc)
                    VALUES(1,$C,'','2025-26','Auto Import','Imported','2026-01-01T00:00:00Z');
                    INSERT INTO PurchaseRecords(BatchId,ClientId,ClientGstin,CompanyName,FinancialYear,ImportMode,Destination,ReconciliationStatus,GstApplicability,SupplierGstin,SupplierName,DocumentType,DocumentNumber,DocumentDate,GstDate,SourceDocumentValue,DocumentValue,Rate,TaxableValue,Igst,Cgst,Sgst,Cess,RoundOff,PurchaseLedger,IgstLedger,CgstLedger,SgstLedger,SourceSection,IsAmendment,OriginalDocumentNumber,OriginalDocumentDate,PlaceOfSupply,ReverseCharge,InvoiceType,ItcAvailability,ImsStatus,TwoBReason,TwoAReturnPeriod,TwoBReturnPeriod,TwoBAcceptance,Source2AFile,Source2APath,Source2BFile,Source2BPath,TwoBInvoiceValue,TwoBTaxableValue,TwoBIgst,TwoBCgst,TwoBSgst,DifferenceInvoiceValue,DifferenceTaxableValue,DifferenceIgst,DifferenceCgst,DifferenceSgst,DifferenceDetails,ItemSequence,CreatedUtc,UpdatedUtc,IsUserEdited,RecordJson)
                    VALUES
                    (1,$C,'','','2025-26','Auto Import','Purchase','','Included in Purchase','','Sup','','INV-LEGACY','2025-04-01','',100,100,18,100,18,0,0,0,0,'Purchase @ 18%','','','','B2B',0,'','','','No','','','','','','','','','','','',0,0,0,0,0,0,0,0,0,0,'',0,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z',0,''),
                    (1,$C,'','','2025-26','Auto Import','Purchase','','Included in Purchase','','Sup','','INV-VALID','2025-04-02','',100,100,18,100,18,0,0,0,0,'Purchase @ 18%','','','','B2B',0,'','','','No','','','','','','','','','','','',0,0,0,0,0,0,0,0,0,0,'',0,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z',0,'{"DocumentNumber":"INV-VALID","CompanyName":"Synthetic"}'),
                    (1,$C,'','','2025-26','Auto Import','Purchase','','Included in Purchase','','Sup','','INV-BAD','2025-04-03','',100,100,18,100,18,0,0,0,0,'Purchase @ 18%','','','','B2B',0,'','','','No','','','','','','','','','','','',0,0,0,0,0,0,0,0,0,0,'',0,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z',0,'{');
                    INSERT INTO SalesImportBatches(Id,ClientId,ClientGstin,FinancialYear,ImportMode,Status,CreatedUtc)
                    VALUES(1,$C,'','2025-26','Auto Import','Imported','2026-01-01T00:00:00Z');
                    INSERT INTO SalesRecords(BatchId,ClientId,ClientGstin,CompanyName,FinancialYear,ImportMode,Destination,CustomerGstin,CustomerName,CustomerNameSource,DocumentType,DocumentNumber,DocumentDate,GstDate,SourceDocumentValue,DocumentValue,Rate,TaxableValue,Igst,Cgst,Sgst,Cess,RoundOff,SalesLedger,IgstLedger,CgstLedger,SgstLedger,SourceSection,IsAmendment,OriginalDocumentNumber,OriginalDocumentDate,PlaceOfSupply,ReturnPeriod,SourceFile,SourcePath,SourceOrigin,SourceBranch,SourceScope,SourceHash,ResponseTimestamp,SalesMasterFile,ItemSequence,CreatedUtc,UpdatedUtc,IsUserEdited,RecordJson)
                    VALUES
                    (1,$C,'','','2025-26','Auto Import','Sales','','Cust','','INV','S-LEGACY','2025-04-01','',100,100,18,100,18,0,0,0,0,'Sales @ 18%','','','','B2B',0,'','','','','','','','','','','','',0,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z',0,''),
                    (1,$C,'','','2025-26','Auto Import','Sales','','Cust','','INV','S-BAD','2025-04-02','',100,100,18,100,18,0,0,0,0,'Sales @ 18%','','','','B2B',0,'','','','','','','','','','','','',0,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z',0,'{');
                    """;
                cmd.Parameters.AddWithValue("$C", client.Id);
                cmd.ExecuteNonQuery();
            }

            var purchaseSnapshots = purchaseRepo.GetStockProjectionSnapshots(client.Id, "2025-26");
            Check(purchaseSnapshots.Count == 2 && purchaseSnapshots.Count(s => s.Record is null) == 1 && purchaseSnapshots.Count(s => s.Record is not null) == 1,
                $"Expected blank Purchase RecordJson skipped and 1 valid + 1 malformed retained, got total={purchaseSnapshots.Count}.");

            var salesSnapshots = salesRepo.GetStockProjectionSnapshots(client.Id, "2025-26");
            Check(salesSnapshots.Count == 1 && salesSnapshots[0].Record is null,
                $"Expected blank Sales RecordJson skipped and 1 malformed retained, got total={salesSnapshots.Count}.");
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    private static void RunCrossThreadDispatcherContractTest()
    {
        var root = FindRepoRoot();
        var viewModelsDir = Path.Combine(root, "AAH", "src", "AccountingAutomationHub.Desktop", "ViewModels");
        foreach (var file in new[] { "DashboardViewModel.cs", "TallyClientViewModel.cs", "FinancialStatementsViewModel.cs", "BankingViewModel.cs" })
        {
            var src = File.ReadAllText(Path.Combine(viewModelsDir, file));
            Check(src.Contains("uiDispatcher.CheckAccess()", StringComparison.Ordinal) &&
                  src.Contains("uiDispatcher.BeginInvoke(", StringComparison.Ordinal),
                $"Expected dispatcher marshalling guard in {file}.");
        }

        var salesVm = File.ReadAllText(Path.Combine(viewModelsDir, "SalesImportViewModel.cs"));
        Check(!salesVm.Contains("SaveB2CCustomerNameFromPanelAsync(sourceKey, customerName).GetAwaiter().GetResult()", StringComparison.Ordinal),
            "Removed sync-over-async SaveB2CCustomerNameFromPanel wrapper must not be reintroduced.");
    }

    private static void RunLogRetentionPruningTest()
    {
        var tempRoot = CreateTempRoot("log_retention");
        try
        {
            ApplicationDataPaths.ConfigureRootOverride(tempRoot);
            ApplicationDataPaths.EnsureFolders();
            var logsDir = Path.Combine(tempRoot, "Logs");
            Directory.CreateDirectory(logsDir);

            var fatalPath = Path.Combine(logsDir, "fatal-error.log");
            File.WriteAllBytes(fatalPath, new byte[2 * 1024 * 1024 + 512]);
            var activeAutoImport = Path.Combine(logsDir, "AutoImport.log");
            File.WriteAllText(activeAutoImport, "active-line");
            var oldRotated = Path.Combine(logsDir, "AutoImport-20250101-000000.log");
            File.WriteAllBytes(oldRotated, new byte[1024 * 1024]);
            File.SetLastWriteTimeUtc(oldRotated, DateTime.UtcNow.AddDays(-20));
            var largeRotated = Path.Combine(logsDir, "performance-trace-20261124-000000.csv");
            File.WriteAllBytes(largeRotated, new byte[41 * 1024 * 1024]);

            ApplicationStorageMaintenanceService.RunSafeMaintenance();

            Check(!File.Exists(fatalPath), "Expected > 2 MB fatal-error.log to be rotated.");
            Check(!File.Exists(oldRotated), "Expected > 14-day rotated log to be pruned.");
            Check(!File.Exists(largeRotated), "Expected > 40 MB rotated log archive to be pruned.");
            Check(File.Exists(activeAutoImport), "Expected active AutoImport.log to be preserved.");
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
