using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Infrastructure.Clients;
using AccountingAutomationHub.Infrastructure.Persistence;
using AccountingAutomationHub.Infrastructure.Purchases;
using AccountingAutomationHub.Infrastructure.Sales;
using AccountingAutomationHub.Infrastructure.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

internal static class Program
{
    private static void Check(bool cond, string msg)
    {
        if (!cond) throw new InvalidOperationException("ASSERTION FAILED: " + msg);
    }

    private static string Sha256Hex(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    private static string BuildPayloadJson(
        string sellerGstin,
        string buyerGstin,
        string invNo,
        string invDate,
        decimal total,
        string[] shaList)
    {
        return JsonSerializer.Serialize(new
        {
            ocrEngine = "AAH-INVOICEVISION-V2",
            ocrSourceSha256 = shaList[0],
            ocrSourceSha256List = shaList,
            ocrBlockingFlags = Array.Empty<string>(),
            direction = "Purchase",
            documentType = "Tax Invoice",
            invoiceNumber = invNo,
            invoiceDate = invDate,
            invoiceTotal = total.ToString("0.00"),
            taxableTotal = total.ToString("0.00"),
            cgstTotal = "0.00",
            sgstTotal = "0.00",
            igstTotal = "0.00",
            cessTotal = "",
            roundOff = "0.00",
            seller = new { name = "ALPHA FOODS PVT LTD", gstin = sellerGstin, pan = sellerGstin[2..12] },
            buyer = new { name = "BALAJI TRADING CO", gstin = buyerGstin, pan = buyerGstin[2..12] },
            counterparty = new { name = "ALPHA FOODS PVT LTD", gstin = sellerGstin, pan = sellerGstin[2..12] },
            rows = Array.Empty<object>()
        });
    }

    private static async Task<int> Main(string[] args)
    {
        if (args.Length >= 2 && args[0].Equals("--verify-5-photos", StringComparison.OrdinalIgnoreCase))
            return await VerifyRealPhotosEndToEndAsync(args[1]);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"aah_mobilelink_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            ApplicationDataPaths.ConfigureRootOverride(tempRoot);
            ApplicationDataPaths.EnsureFolders();

            var clientRepo = new SqliteClientRepository();
            clientRepo.Initialize();
            var client = clientRepo.Save(new ClientProfile
            {
                ClientCode = "SYNMOB01",
                ClientName = "BALAJI TRADING CO",
                Gstin = "29ABZPB5530G1ZE",
                FinancialYear = "2026-27"
            });

            var prefs = new ApplicationPreferenceService(new SqliteApplicationPreferenceRepository());
            using var server = new SecureLanServerService(prefs);
            server.EnsureInitializedForUi();

            var sampleDir = Path.Combine(tempRoot, "samples");
            Directory.CreateDirectory(sampleDir);
            var imgP1 = Path.Combine(sampleDir, "p1.jpg");
            var imgP1Dup = Path.Combine(sampleDir, "p1_dup.jpg");
            var imgP2 = Path.Combine(sampleDir, "p2.jpg");
            File.WriteAllBytes(imgP1, Encoding.UTF8.GetBytes("SYNTHETIC-JPEG-PAGE-1-CONTENT"));
            File.WriteAllBytes(imgP1Dup, Encoding.UTF8.GetBytes("SYNTHETIC-JPEG-PAGE-1-CONTENT"));
            File.WriteAllBytes(imgP2, Encoding.UTF8.GetBytes("SYNTHETIC-JPEG-PAGE-2-DIFFERENT-BYTES"));

            var sellerGstin = "29ABACA5056C1ZY";
            var buyerGstin = "29ABZPB5530G1ZE";
            var sha1 = Sha256Hex(imgP1);
            var sha2 = Sha256Hex(imgP2);

            // 1. Test Exact Photo Duplicate Detection + RemoveDuplicateMobileScan
            var p1Json = BuildPayloadJson(sellerGstin, buyerGstin, "INV-MULTI-1", "2026-11-20", 0m, [sha1]);
            var scan1 = server.RegisterQueuedMobileScanForTest(client.Id, client.FinancialYear, "SCAN-1", [imgP1], p1Json, "Page 1 of 2\nCarry Forward", "Ready", DateTime.UtcNow);
            var scanDup = server.RegisterQueuedMobileScanForTest(client.Id, client.FinancialYear, "SCAN-DUP", [imgP1Dup], p1Json, "Page 1 of 2", "Ready", DateTime.UtcNow.AddSeconds(5));
            Check(scanDup.DuplicateKind == "ExactPhotoInQueue" && scanDup.IsDuplicate, $"Expected ExactPhotoInQueue, got {scanDup.DuplicateKind}");
            server.RemoveDuplicateMobileScan(client.Id, client.FinancialYear, "SCAN-DUP");
            Check(server.GetMobileInvoiceQueue(client.Id, client.FinancialYear).Count == 1, "Expected duplicate scan removed from queue.");

            // 2. Test Multi-Page Detection + Link + Unlink
            var p2Json = BuildPayloadJson(sellerGstin, buyerGstin, "INV-MULTI-1", "2026-11-20", 2170m, [sha2]);
            var scan2 = server.RegisterQueuedMobileScanForTest(client.Id, client.FinancialYear, "SCAN-2", [imgP2], p2Json, "Page 2 of 2\nBrought Forward\nNet Payable 2170.00", "Ready", DateTime.UtcNow.AddSeconds(10));
            Check(scan1.HasMultiPageHint && scan2.HasMultiPageHint, "Expected both pages flagged as MultiPageCandidate.");
            var linkMsg = server.AutoLinkMultiPageCandidates(client.Id, client.FinancialYear, triggerOcr: false);
            var afterLink = server.GetMobileInvoiceQueue(client.Id, client.FinancialYear);
            Check(afterLink.Count == 1 && afterLink[0].ImagePaths.Count == 2 && afterLink[0].IsMultiPage, $"Expected 1 linked scan with 2 pages ({linkMsg}).");

            var unlinkMsg = server.UnlinkMobileInvoiceScan(client.Id, client.FinancialYear, afterLink[0].ScanId, triggerOcr: false);
            var afterUnlink = server.GetMobileInvoiceQueue(client.Id, client.FinancialYear);
            Check(afterUnlink.Count == 2 && afterUnlink.All(x => x.ImagePaths.Count == 1), $"Expected 2 unlinked 1-page scans ({unlinkMsg}).");

            // 3. Relink, simulate multi-page OCR payload, verify tampered Page 2 fails, then confirm cleanly
            server.LinkMobileInvoiceScans(client.Id, client.FinancialYear, [afterUnlink[0].ScanId, afterUnlink[1].ScanId], triggerOcr: false);
            var linked = server.GetMobileInvoiceQueue(client.Id, client.FinancialYear).Single();
            var h1 = Sha256Hex(linked.ImagePaths[0]);
            var h2 = Sha256Hex(linked.ImagePaths[1]);
            linked.PayloadJson = BuildPayloadJson(sellerGstin, buyerGstin, "INV-MULTI-1", "2026-11-20", 2170m, [h1, h2]);
            linked.DocumentOcrState = "Ready";
            linked.InvoiceNumber = "INV-MULTI-1";
            linked.InvoiceTotal = "2170.00";
            server.RecomputeQueueLinkAndDuplicateFlags(client.Id, client.FinancialYear);

            // Tamper test: backup byte of page 2, mutate page 2, verify CompleteMobileInvoiceReview rejects
            var origPage2Bytes = File.ReadAllBytes(linked.ImagePaths[1]);
            File.WriteAllBytes(linked.ImagePaths[1], Encoding.UTF8.GetBytes("TAMPERED"));
            bool tamperBlocked = false;
            var mapped = new MobileInvoiceMappedResult
            {
                Direction = "Purchase",
                DocumentType = "Tax Invoice",
                InvoiceNumber = "INV-MULTI-1",
                InvoiceDate = new DateTime(2026, 11, 20),
                CounterpartyName = "ALPHA FOODS PVT LTD",
                CounterpartyGstin = sellerGstin,
                InvoiceTotal = 2170m,
                TaxableTotal = 2170m,
                SourceImageSha256 = h1,
                SourceImageSha256List = [h1, h2]
            };
            try { server.CompleteMobileInvoiceReview(client.Id, client.FinancialYear, linked.ScanId, true, mapped); }
            catch (InvalidOperationException) { tamperBlocked = true; }
            Check(tamperBlocked, "Expected tampered Page 2 in multi-page scan to block confirmation.");
            File.WriteAllBytes(linked.ImagePaths[1], origPage2Bytes);

            // Clean confirm succeeds and writes to SQLite PurchaseRecords
            server.CompleteMobileInvoiceReview(client.Id, client.FinancialYear, linked.ScanId, true, mapped);
            Check(server.GetMobileInvoiceQueue(client.Id, client.FinancialYear).Count == 0, "Expected confirmed scan removed from queue.");

            // 4. Semantic Duplicate vs AlreadyImported SQLite DB
            var imgP3 = Path.Combine(sampleDir, "p3.jpg");
            File.WriteAllBytes(imgP3, Encoding.UTF8.GetBytes("DIFFERENT-PHOTO-SAME-INVOICE-INV-MULTI-1"));
            var sha3 = Sha256Hex(imgP3);
            var p3Json = BuildPayloadJson(sellerGstin, buyerGstin, "INV-MULTI-1", "2026-11-20", 2170m, [sha3]);
            var scanReupload = server.RegisterQueuedMobileScanForTest(client.Id, client.FinancialYear, "SCAN-REUP", [imgP3], p3Json, "Net Payable 2170.00", "Ready");
            Check(scanReupload.DuplicateKind == "AlreadyImported" && scanReupload.IsDuplicate, $"Expected AlreadyImported, got {scanReupload.DuplicateKind}: {scanReupload.DuplicateDetail}");

            var mappedDup = new MobileInvoiceMappedResult
            {
                Direction = "Purchase",
                DocumentType = "Tax Invoice",
                InvoiceNumber = "INV-MULTI-1",
                InvoiceDate = new DateTime(2026, 11, 20),
                CounterpartyName = "ALPHA FOODS PVT LTD",
                CounterpartyGstin = sellerGstin,
                InvoiceTotal = 2170m,
                TaxableTotal = 2170m,
                SourceImageSha256 = sha3,
                DuplicateOverrideAcknowledged = false
            };
            bool dupBlocked = false;
            try { server.CompleteMobileInvoiceReview(client.Id, client.FinancialYear, scanReupload.ScanId, true, mappedDup); }
            catch (InvalidOperationException) { dupBlocked = true; }
            Check(dupBlocked, "Expected AlreadyImported duplicate to be blocked unless DuplicateOverrideAcknowledged = true.");

            mappedDup.DuplicateOverrideAcknowledged = true;
            server.CompleteMobileInvoiceReview(client.Id, client.FinancialYear, scanReupload.ScanId, true, mappedDup);

            // 4b. Live hub bidirectional sync & Purchase ClearModule/Delete clears stale duplicates
            var liveHub = new AccountingLiveChangeHub();
            var publishedAreas = new System.Collections.Generic.List<(AccountingLiveArea Area, string Reason)>();
            liveHub.Changed += (_, ev) => publishedAreas.Add((ev.Area, ev.Reason));
            server.AttachLiveChangeHub(liveHub);

            var purRepoSync = new SqlitePurchaseRecordRepository();
            purRepoSync.Initialize();
            purRepoSync.ClearModule(client.Id, client.FinancialYear);
            liveHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Purchase, "Purchase module cleared");

            var scanSamePhotoAfterDelete = server.RegisterQueuedMobileScanForTest(
                client.Id, client.FinancialYear, "SCAN-AFTER-DEL-PHOTO", [imgP1Dup],
                BuildPayloadJson(sellerGstin, buyerGstin, "INV-MULTI-1", "2026-11-20", 2170m, [sha1]),
                "Net Payable 2170.00", "Ready");
            Check(scanSamePhotoAfterDelete.DuplicateKind == "None" && !scanSamePhotoAfterDelete.IsDuplicate,
                $"Expected deleted Purchase bill photo NOT to flag duplicate, got {scanSamePhotoAfterDelete.DuplicateKind}: {scanSamePhotoAfterDelete.DuplicateDetail}");

            var mappedAfterDelete = new MobileInvoiceMappedResult
            {
                Direction = "Purchase",
                DocumentType = "Tax Invoice",
                InvoiceNumber = "INV-MULTI-1",
                InvoiceDate = new DateTime(2026, 11, 20),
                CounterpartyName = "ALPHA FOODS PVT LTD",
                CounterpartyGstin = sellerGstin,
                InvoiceTotal = 2170m,
                TaxableTotal = 2170m,
                SourceImageSha256 = sha1,
                DuplicateOverrideAcknowledged = false
            };
            server.CompleteMobileInvoiceReview(client.Id, client.FinancialYear, scanSamePhotoAfterDelete.ScanId, true, mappedAfterDelete);
            Check(publishedAreas.Any(x => x.Area == AccountingLiveArea.Purchase && x.Reason.Contains("Mobile Link confirmed", StringComparison.OrdinalIgnoreCase)),
                "Expected confirming a Mobile Link Purchase invoice to publish AccountingLiveArea.Purchase.");
            var scanDupAfterReconfirm = server.RegisterQueuedMobileScanForTest(
                client.Id, client.FinancialYear, "SCAN-RECONF-DUP", [imgP1Dup],
                BuildPayloadJson(sellerGstin, buyerGstin, "INV-MULTI-1", "2026-11-20", 2170m, [sha1]),
                "Net Payable 2170.00", "Ready");
            Check(scanDupAfterReconfirm.DuplicateKind == "ExactPhotoConfirmed" && scanDupAfterReconfirm.IsDuplicate,
                $"Expected re-confirmed Purchase bill photo to flag ExactPhotoConfirmed, got {scanDupAfterReconfirm.DuplicateKind}");
            server.RemoveDuplicateMobileScan(client.Id, client.FinancialYear, scanDupAfterReconfirm.ScanId);

            // 5. Batch Mixed Scans + Cont. Page 2 Auto-Link Isolation (only matching invoice auto-links, unrelated scan stays separate)
            var imgV1 = Path.Combine(sampleDir, "v1.jpg");
            var imgV2 = Path.Combine(sampleDir, "v2.jpg");
            var imgOther = Path.Combine(sampleDir, "other.jpg");
            File.WriteAllBytes(imgV1, Encoding.UTF8.GetBytes("VENKATESHWARA-PAGE-1"));
            File.WriteAllBytes(imgV2, Encoding.UTF8.GetBytes("VENKATESHWARA-PAGE-2"));
            File.WriteAllBytes(imgOther, Encoding.UTF8.GetBytes("SRI-RAMA-SEPARATE-BILL"));
            var vSeller = "29AGFPL5940F1ZA";
            var rSeller = "29DEZPR3879Q2ZN";
            var sv1 = server.RegisterQueuedMobileScanForTest(client.Id, client.FinancialYear, "BATCH-P01", [imgV1],
                BuildPayloadJson(vSeller, buyerGstin, "INV 12222", "2026-11-13", 0m, [Sha256Hex(imgV1)]), "Cont. Page 2", "Ready", DateTime.UtcNow);
            // Simulate Page 2 where only client's Buyer GSTIN appeared at top of page 2
            var sv2 = server.RegisterQueuedMobileScanForTest(client.Id, client.FinancialYear, "BATCH-P02", [imgV2],
                BuildPayloadJson(buyerGstin, buyerGstin, "INV 12222", "2026-11-13", 9315m, [Sha256Hex(imgV2)]), "NET AMT 9315.00", "Ready", DateTime.UtcNow.AddMilliseconds(10));
            var sOther = server.RegisterQueuedMobileScanForTest(client.Id, client.FinancialYear, "BATCH-P03", [imgOther],
                BuildPayloadJson(rSeller, buyerGstin, "A001532", "2026-10-29", 3210m, [Sha256Hex(imgOther)]), "GRAND TOTAL 3210.00", "Ready", DateTime.UtcNow.AddMilliseconds(20));

            server.TryAutoLinkReadyInvoiceGroups(client.Id, client.FinancialYear, triggerOcr: false);
            var batchAfter = server.GetMobileInvoiceQueue(client.Id, client.FinancialYear);
            Check(batchAfter.Count == 2, $"Expected 2 scans after auto-linking INV 12222 (got {batchAfter.Count})");
            Check(batchAfter.Any(x => x.InvoiceNumber == "INV 12222" && x.ImagePaths.Count == 2), "Expected INV 12222 to have 2 linked pages.");
            Check(batchAfter.Any(x => x.InvoiceNumber == "A001532" && x.ImagePaths.Count == 1), "Expected A001532 to stay unlinked (1 page).");

            // 6. Multi-Photo Ingest Split + Immediate Exact Duplicate Detection (no file-lock crash)
            var b1 = Encoding.UTF8.GetBytes("BATCH-UPLOAD-BILL-1");
            var b2 = Encoding.UTF8.GetBytes("BATCH-UPLOAD-BILL-2");
            await using var ms1 = new MemoryStream(b1);
            await using var ms2 = new MemoryStream(b2);
            await using var ms3 = new MemoryStream(b1);
            var formFiles = new IFormFile[]
            {
                new FormFile(ms1, 0, b1.Length, "page1", "bill1.jpg"),
                new FormFile(ms2, 0, b2.Length, "page2", "bill2.jpg"),
                new FormFile(ms3, 0, b1.Length, "page3", "bill1_dup.jpg")
            };
            var (added, anyDup) = await server.IngestMobileInvoiceUploadFilesAsync(
                client,
                "welcome",
                "test-device",
                "SCAN-BATCH-UPLOAD",
                $"{{\"clientId\":{client.Id},\"financialYear\":\"{client.FinancialYear}\",\"direction\":\"Auto\"}}",
                string.Empty,
                "{}",
                "{}",
                formFiles,
                triggerOcr: false,
                CancellationToken.None);
            Check(added.Count == 3 && anyDup, $"Expected 3 split rows and anyDup=true, got count={added.Count} anyDup={anyDup}");
            Check(added.Select(x => x.ScanId).SequenceEqual(new[] { "SCAN-BATCH-UPLOAD-P01", "SCAN-BATCH-UPLOAD-P02", "SCAN-BATCH-UPLOAD-P03" }),
                "Unexpected split scan IDs: " + string.Join(",", added.Select(x => x.ScanId)));
            Check(added[0].DocumentOcrState == "Processing" && !added[0].IsDuplicate && added[0].PageSha256List.Count == 1 && !string.IsNullOrWhiteSpace(added[0].PageSha256List[0]) && File.Exists(added[0].ImagePaths[0]),
                "Expected -P01 cleanly written, hashed, and queued for OCR.");
            Check(added[1].DocumentOcrState == "Processing" && !added[1].IsDuplicate && added[1].PageSha256List.Count == 1 && !string.IsNullOrWhiteSpace(added[1].PageSha256List[0]) && File.Exists(added[1].ImagePaths[0]),
                "Expected -P02 cleanly written, hashed, and queued for OCR.");
            Check(added[2].IsDuplicate && added[2].DuplicateKind == "ExactPhotoInQueue" && added[2].DocumentOcrState == "NeedsAction",
                $"Expected -P03 flagged ExactPhotoInQueue with NeedsAction, got kind={added[2].DuplicateKind} state={added[2].DocumentOcrState}");

            // 7. Smart Page Ordering When Page 2 Is Uploaded Before Page 1 ("Cont. Page 2")
            var revP2Img = Path.Combine(sampleDir, "rev_p2.jpg");
            var revP1Img = Path.Combine(sampleDir, "rev_p1.jpg");
            File.WriteAllBytes(revP2Img, Encoding.UTF8.GetBytes("REV-ORDER-PAGE-2"));
            File.WriteAllBytes(revP1Img, Encoding.UTF8.GetBytes("REV-ORDER-PAGE-1"));
            var shaRev2 = Sha256Hex(revP2Img);
            var shaRev1 = Sha256Hex(revP1Img);
            var revP2Scan = server.RegisterQueuedMobileScanForTest(client.Id, client.FinancialYear, "REV-P01", [revP2Img],
                BuildPayloadJson(vSeller, buyerGstin, "INV 99999", "2026-11-22", 5000m, [shaRev2]), "Brought Forward\nNet Payable 5000.00", "Ready", DateTime.UtcNow.AddSeconds(30));
            var revP1Scan = server.RegisterQueuedMobileScanForTest(client.Id, client.FinancialYear, "REV-P02", [revP1Img],
                BuildPayloadJson(vSeller, buyerGstin, "INV 99999", "2026-11-22", 0m, [shaRev1]), "Cont. Page 2\nCarry Forward", "Ready", DateTime.UtcNow.AddSeconds(35));
            server.LinkMobileInvoiceScans(client.Id, client.FinancialYear, [revP2Scan.ScanId, revP1Scan.ScanId], triggerOcr: false);
            var revLinked = server.GetMobileInvoiceQueue(client.Id, client.FinancialYear).Single(x => x.InvoiceNumber == "INV 99999");
            Check(revLinked.LinkedScanIds.SequenceEqual(new[] { "REV-P02", "REV-P01" }),
                "Expected Cont. Page 2 (REV-P02) ordered before Page 2 (REV-P01), got: " + string.Join(",", revLinked.LinkedScanIds));
            Check(Sha256Hex(revLinked.ImagePaths[0]) == shaRev1 && Sha256Hex(revLinked.ImagePaths[1]) == shaRev2,
                "Expected ImagePaths[0] == Page 1 and ImagePaths[1] == Page 2.");

            // 8. Accounting Invoice vs Item Invoice Mode Import Proof (Purchase + Sales)
            var imgAcc = Path.Combine(sampleDir, "acc_mode.jpg");
            var imgAccSales = Path.Combine(sampleDir, "acc_sales_mode.jpg");
            var imgItem = Path.Combine(sampleDir, "item_mode.jpg");
            File.WriteAllBytes(imgAcc, Encoding.UTF8.GetBytes("ACCOUNTING-MODE-PURCHASE-BILL"));
            File.WriteAllBytes(imgAccSales, Encoding.UTF8.GetBytes("ACCOUNTING-MODE-SALES-BILL"));
            File.WriteAllBytes(imgItem, Encoding.UTF8.GetBytes("ITEM-MODE-PURCHASE-BILL"));

            var accLines = new[]
            {
                new MobileInvoiceMappedLine { Description = "BISCUITS PACK A", HsnSac = "19053100", Quantity = 10m, Unit = "PCS", Rate = 10m, Taxable = 100m, GstRate = 5m, Cgst = 2.5m, Sgst = 2.5m, LineTotal = 105m },
                new MobileInvoiceMappedLine { Description = "BISCUITS PACK B", HsnSac = "19053100", Quantity = 5m, Unit = "PCS", Rate = 20m, Taxable = 100m, GstRate = 5m, Cgst = 2.5m, Sgst = 2.5m, LineTotal = 105m },
                new MobileInvoiceMappedLine { Description = "SHAMPOO BOTTLE", HsnSac = "33051090", Quantity = 2m, Unit = "BTL", Rate = 100m, Taxable = 200m, GstRate = 18m, Cgst = 18m, Sgst = 18m, LineTotal = 236m }
            };

            var accPurchaseScan = server.RegisterQueuedMobileScanForTest(client.Id, client.FinancialYear, "ACC-PUR-1", [imgAcc],
                BuildPayloadJson(sellerGstin, buyerGstin, "INV-ACC-01", "2026-11-24", 446m, [Sha256Hex(imgAcc)]), "Accounting Purchase", "Ready", DateTime.UtcNow.AddSeconds(40));
            server.CompleteMobileInvoiceReview(client.Id, client.FinancialYear, accPurchaseScan.ScanId, true, new MobileInvoiceMappedResult
            {
                Direction = "Purchase",
                RecognitionMode = "Accounting Invoice",
                DocumentType = "Tax Invoice",
                InvoiceNumber = "INV-ACC-01",
                InvoiceDate = new DateTime(2026, 11, 24),
                CounterpartyName = "ALPHA FOODS PVT LTD",
                CounterpartyGstin = sellerGstin,
                TaxableTotal = 400m,
                CgstTotal = 23m,
                SgstTotal = 23m,
                InvoiceTotal = 446m,
                SourceImageSha256 = Sha256Hex(imgAcc),
                Lines = accLines.ToList()
            });

            var purRepo = new SqlitePurchaseRecordRepository();
            purRepo.Initialize();
            var accPurRows = purRepo.GetRecords(client.Id, client.FinancialYear).Where(x => x.DocumentNumber == "INV-ACC-01").OrderBy(x => x.Rate).ToArray();
            Check(accPurRows.Length == 2, $"Accounting Invoice Purchase must collapse 3 items into 2 GST-slab rows, got {accPurRows.Length}.");
            Check(accPurRows.All(r => !r.HasInventory && string.IsNullOrEmpty(r.ItemName) && string.IsNullOrEmpty(r.HsnCode) && string.IsNullOrEmpty(r.Unit) && r.ActualQuantity == 0m && r.BilledQuantity == 0m && r.ItemRate == 0m && r.ItemAmount == 0m),
                "Accounting Invoice Purchase rows must have ZERO inventory fields.");
            Check(accPurRows[0].Rate == 5m && accPurRows[0].TaxableValue == 200m && accPurRows[0].Cgst == 5m && accPurRows[0].Sgst == 5m,
                $"Unexpected 5% slab sums: {accPurRows[0].TaxableValue} / {accPurRows[0].Cgst}");
            Check(accPurRows[1].Rate == 18m && accPurRows[1].TaxableValue == 200m && accPurRows[1].Cgst == 18m && accPurRows[1].Sgst == 18m,
                $"Unexpected 18% slab sums: {accPurRows[1].TaxableValue} / {accPurRows[1].Cgst}");

            var accSalesScan = server.RegisterQueuedMobileScanForTest(client.Id, client.FinancialYear, "ACC-SLS-1", [imgAccSales],
                BuildPayloadJson(buyerGstin, sellerGstin, "SLS-ACC-01", "2026-11-24", 446m, [Sha256Hex(imgAccSales)]), "Accounting Sales", "Ready", DateTime.UtcNow.AddSeconds(41));
            server.CompleteMobileInvoiceReview(client.Id, client.FinancialYear, accSalesScan.ScanId, true, new MobileInvoiceMappedResult
            {
                Direction = "Sales",
                RecognitionMode = "Accounting Invoice",
                DocumentType = "Tax Invoice",
                InvoiceNumber = "SLS-ACC-01",
                InvoiceDate = new DateTime(2026, 11, 24),
                CounterpartyName = "ALPHA FOODS PVT LTD",
                CounterpartyGstin = sellerGstin,
                TaxableTotal = 400m,
                CgstTotal = 23m,
                SgstTotal = 23m,
                InvoiceTotal = 446m,
                SourceImageSha256 = Sha256Hex(imgAccSales),
                Lines = accLines.ToList()
            });
            var slsRepo = new SqliteSalesRecordRepository();
            slsRepo.Initialize();
            var accSlsRows = slsRepo.GetRecords(client.Id, client.FinancialYear).Where(x => x.DocumentNumber == "SLS-ACC-01").OrderBy(x => x.Rate).ToArray();
            Check(accSlsRows.Length == 2 && accSlsRows.All(r => !r.HasInventory && string.IsNullOrEmpty(r.ItemName) && r.ActualQuantity == 0m && r.ImportMode == "Accounting Invoice"),
                "Accounting Invoice Sales must also emit slab-only non-inventory rows.");

            var itemPurScan = server.RegisterQueuedMobileScanForTest(client.Id, client.FinancialYear, "ITEM-PUR-1", [imgItem],
                BuildPayloadJson(sellerGstin, buyerGstin, "INV-ITEM-01", "2026-11-24", 446m, [Sha256Hex(imgItem)]), "Item Purchase", "Ready", DateTime.UtcNow.AddSeconds(42));
            server.CompleteMobileInvoiceReview(client.Id, client.FinancialYear, itemPurScan.ScanId, true, new MobileInvoiceMappedResult
            {
                Direction = "Purchase",
                RecognitionMode = "Item Invoice",
                DocumentType = "Tax Invoice",
                InvoiceNumber = "INV-ITEM-01",
                InvoiceDate = new DateTime(2026, 11, 24),
                CounterpartyName = "ALPHA FOODS PVT LTD",
                CounterpartyGstin = sellerGstin,
                TaxableTotal = 400m,
                CgstTotal = 23m,
                SgstTotal = 23m,
                InvoiceTotal = 446m,
                SourceImageSha256 = Sha256Hex(imgItem),
                Lines = accLines.ToList()
            });
            var itemPurRows = purRepo.GetRecords(client.Id, client.FinancialYear).Where(x => x.DocumentNumber == "INV-ITEM-01").ToArray();
            Check(itemPurRows.Length == 3 && itemPurRows.All(r => r.ItemName.Length > 0 && r.ActualQuantity > 0m && r.Unit.Length > 0),
                $"Item Invoice mode must preserve all 3 inventory rows with ItemName/Qty/Unit (got {itemPurRows.Length}).");

            // 9. Offline Self-Learning Supplier Memory Auto-Training Verification
            var memInfo = MobileSupplierMemoryStore.GetSupplierSummary(sellerGstin);
            Check(memInfo.Found && memInfo.ConfirmedCount >= 1 && memInfo.HsnRateCount >= 2,
                $"Expected supplier_memory.db auto-learned after confirm: Found={memInfo.Found}, Count={memInfo.ConfirmedCount}, HSN={memInfo.HsnRateCount}");

            // 10. AutoImportInbox Local Photo Ingestion & Archival Verification
            var inboxDir = SecureLanServerService.MobileAutoImportInboxFolder(client.Id, client.FinancialYear);
            Directory.CreateDirectory(inboxDir);
            var inboxImg = Path.Combine(inboxDir, "inbox_sample.jpg");
            File.WriteAllBytes(inboxImg, Encoding.UTF8.GetBytes("AUTO-IMPORT-INBOX-SAMPLE-JPG"));
            var swept = server.SweepLocalAutoImportInbox(client.Id, client.FinancialYear, triggerOcr: false);
            Check(swept == 1 && !File.Exists(inboxImg), $"Expected 1 file swept and archived from AutoImportInbox (swept={swept}).");
            var inboxQueue = server.GetMobileInvoiceQueue(client.Id, client.FinancialYear).Where(x => x.ScanId.StartsWith("LOCAL-", StringComparison.Ordinal)).ToArray();
            Check(inboxQueue.Length == 1 && inboxQueue[0].RecognitionMode == "Accounting Invoice",
                $"Expected queued item in Accounting Invoice mode, got {inboxQueue.Length}.");
            var reswept = server.SweepLocalAutoImportInbox(client.Id, client.FinancialYear, triggerOcr: false);
            Check(reswept == 0, "Second AutoImportInbox sweep must be an idempotent no-op.");

            // 11. Strict Intra-State CGST == SGST Server Gate
            var imgCgSg = Path.Combine(sampleDir, "cg_sg_gate.jpg");
            File.WriteAllBytes(imgCgSg, Encoding.UTF8.GetBytes("CGST-SGST-EQUALITY-GATE-TEST"));
            var shaCgSg = Sha256Hex(imgCgSg);
            var scanCgSg = server.RegisterQueuedMobileScanForTest(client.Id, client.FinancialYear, "CG-SG-1", [imgCgSg],
                BuildPayloadJson(sellerGstin, buyerGstin, "INV-CGSG-01", "2026-11-26", 2750.00m, [shaCgSg]), "CGST 65.48 SGST 65.47", "Ready", DateTime.UtcNow.AddSeconds(50));
            var unequalMapped = new MobileInvoiceMappedResult
            {
                Direction = "Purchase", RecognitionMode = "Accounting Invoice", DocumentType = "Tax Invoice",
                InvoiceNumber = "INV-CGSG-01", InvoiceDate = new DateTime(2026, 11, 26),
                CounterpartyName = "ALPHA FOODS PVT LTD", CounterpartyGstin = sellerGstin,
                TaxableTotal = 2619.05m, CgstTotal = 65.48m, SgstTotal = 65.47m, RoundOff = 0.00m, InvoiceTotal = 2750.00m,
                SourceImageSha256 = shaCgSg,
                Lines = [new MobileInvoiceMappedLine { Description = "ITEM", HsnSac = "090422", Taxable = 2619.05m, GstRate = 5m, Cgst = 65.48m, Sgst = 65.47m, LineTotal = 2750.00m }]
            };
            bool cgsgBlocked = false;
            try { server.CompleteMobileInvoiceReview(client.Id, client.FinancialYear, scanCgSg.ScanId, true, unequalMapped); }
            catch (InvalidOperationException) { cgsgBlocked = true; }
            Check(cgsgBlocked, "Expected unequal CGST (65.48) != SGST (65.47) to be blocked by server gate.");

            unequalMapped.CgstTotal = 65.48m;
            unequalMapped.SgstTotal = 65.48m;
            unequalMapped.RoundOff = -0.01m;
            unequalMapped.Lines[0].Cgst = 65.48m;
            unequalMapped.Lines[0].Sgst = 65.48m;
            unequalMapped.Lines[0].LineTotal = 2750.01m;
            server.CompleteMobileInvoiceReview(client.Id, client.FinancialYear, scanCgSg.ScanId, true, unequalMapped);
            var savedCgSg = purRepo.GetRecords(client.Id, client.FinancialYear).Single(x => x.DocumentNumber == "INV-CGSG-01");
            Check(savedCgSg.Cgst == 65.48m && savedCgSg.Sgst == 65.48m, "Expected equalized CGST=65.48 and SGST=65.48 persisted.");

            // 12. Mobile Link Purchase default GST Treatment = Shown in 2B, visible selector, ClientGstin populated & self-healing
            Check(savedCgSg.GstApplicability == PurchaseTreatmentPolicy.ShownInTwoB,
                $"Mobile Link Purchase row must default to 'Shown in 2B', got '{savedCgSg.GstApplicability}'.");
            Check(savedCgSg.CgstLedger == "Input CGST @ 2.5%" && savedCgSg.SgstLedger == "Input SGST @ 2.5%",
                $"Mobile Link Purchase row must use claimable tax ledgers without '- Not shown in 2B', got CGST='{savedCgSg.CgstLedger}', SGST='{savedCgSg.SgstLedger}'.");
            Check(savedCgSg.ClientGstin == client.Gstin && savedCgSg.CompanyName == client.ClientName,
                $"Mobile Link Purchase row must carry ClientGstin and CompanyName, got GSTIN='{savedCgSg.ClientGstin}', Company='{savedCgSg.CompanyName}'.");
            Check(!savedCgSg.HideAccountingGstTreatmentSelector,
                "Mobile Link Purchase row must NOT hide the GST Treatment selector.");
            Check(savedCgSg.AccountingGstTreatmentOptions.Contains(PurchaseTreatmentPolicy.ShownInTwoB) &&
                  savedCgSg.AccountingGstTreatmentOptions.Contains(PurchaseTreatmentPolicy.NotShownInTwoB) &&
                  savedCgSg.AccountingGstTreatmentOptions.Contains(PurchaseTreatmentPolicy.GstIncludedInCost),
                "Mobile Link Purchase row selector must offer Shown in 2B, Not shown in 2B, and GST included in cost.");

            // User override persists via UpdateRecords and is NOT overwritten on reload
            savedCgSg.GstApplicability = PurchaseTreatmentPolicy.NotShownInTwoB;
            PurchaseTreatmentPolicy.ApplyAccountingShape(savedCgSg);
            var updatedRows = purRepo.UpdateRecords(client.Id, [savedCgSg]);
            Check(updatedRows == 1, $"Expected UpdateRecords to update Mobile Link row, updated={updatedRows}.");
            var reloadedEdited = purRepo.GetRecords(client.Id, client.FinancialYear).Single(x => x.DocumentNumber == "INV-CGSG-01");
            Check(reloadedEdited.IsUserEdited && reloadedEdited.GstApplicability == PurchaseTreatmentPolicy.NotShownInTwoB &&
                  reloadedEdited.CgstLedger == "Input CGST @ 2.5% - Not shown in 2B",
                $"User-edited Mobile Link treatment must persist on reload, got IsUserEdited={reloadedEdited.IsUserEdited}, Treatment='{reloadedEdited.GstApplicability}', CgstLedger='{reloadedEdited.CgstLedger}'.");

            // Legacy unedited Mobile Link row with 'Not shown in 2B' and blank ClientGstin self-heals on read while keeping IsUserEdited=false
            using (var healConn = new SqliteConnection($"Data Source={ApplicationDataPaths.DatabasePath}"))
            {
                healConn.Open();
                using var cmd = healConn.CreateCommand();
                cmd.CommandText =
                    """
                    UPDATE PurchaseRecords
                    SET ClientGstin='', CompanyName='', GstApplicability='Not shown in 2B',
                        CgstLedger='Input CGST @ 2.5% - Not shown in 2B', SgstLedger='Input SGST @ 2.5% - Not shown in 2B',
                        IsUserEdited=0, RecordJson=''
                    WHERE Id=$Id;
                    """;
                cmd.Parameters.AddWithValue("$Id", savedCgSg.PersistentId);
                cmd.ExecuteNonQuery();
            }
            var selfHealed = purRepo.GetRecords(client.Id, client.FinancialYear).Single(x => x.DocumentNumber == "INV-CGSG-01");
            Check(!selfHealed.IsUserEdited &&
                  selfHealed.GstApplicability == PurchaseTreatmentPolicy.ShownInTwoB &&
                  selfHealed.CgstLedger == "Input CGST @ 2.5%" &&
                  selfHealed.ClientGstin == client.Gstin,
                $"Expected unedited legacy Mobile Link row to self-heal to Shown in 2B + ClientGstin, got Treatment='{selfHealed.GstApplicability}', Cgst='{selfHealed.CgstLedger}', ClientGstin='{selfHealed.ClientGstin}'.");

            // Non-Mobile-Link Manual Purchase Register rows keep Fix116 R1 blank/hidden GST Treatment
            var manualBooksRow = new PurchaseImportRecord
            {
                ImportMode = "Manual Import",
                SourceSection = "Manual Purchase Register • B2B",
                ReconciliationStatus = "Manual Purchase Register - Books",
                Rate = 18m,
                TaxableValue = 1000m,
                Cgst = 90m,
                Sgst = 90m,
                GstApplicability = PurchaseTreatmentPolicy.ShownInTwoB
            };
            PurchaseTreatmentPolicy.ApplyAccountingShape(manualBooksRow);
            Check(PurchaseTreatmentPolicy.IsManualBooksImport(manualBooksRow) &&
                  manualBooksRow.HideAccountingGstTreatmentSelector &&
                  manualBooksRow.GstApplicability == string.Empty &&
                  manualBooksRow.CgstLedger == "Input CGST @ 9%",
                "Non-Mobile-Link Manual Purchase Register must preserve Fix116 R1 blank hidden GST Treatment.");

            Console.WriteLine("PASS AAH.MobileLink.Tests: exact photo duplicate, auto multi-page link/unlink, page-2 SHA-256 tamper gate, AlreadyImported DB gate, Cont. Page 2 isolated auto-link, multi-photo ingest split, reverse-upload ordering, Accounting-vs-Item invoice posting, supplier self-learning memory, AutoImportInbox, strict CGST==SGST equality gate, and Mobile Link Shown in 2B + ClientGstin.");
            return 0;
        }
        finally
        {
            ApplicationDataPaths.ConfigureRootOverride(null);
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static async Task<int> VerifyRealPhotosEndToEndAsync(string fixturesDir)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"aah_real5_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            ApplicationDataPaths.ConfigureRootOverride(tempRoot);
            ApplicationDataPaths.EnsureFolders();
            var clientRepo = new SqliteClientRepository();
            clientRepo.Initialize();
            var client = clientRepo.Save(new ClientProfile
            {
                ClientCode = "CL015",
                ClientName = "Balaji Trading Co",
                Gstin = "29ABZPB5530G1ZE",
                FinancialYear = "2026-27"
            });

            var prefs = new ApplicationPreferenceService(new SqliteApplicationPreferenceRepository());
            using var server = new SecureLanServerService(prefs);
            server.EnsureInitializedForUi();

            var photos = Directory.EnumerateFiles(fixturesDir, "*.jpg", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Check(photos.Length == 5, $"Expected 5 .jpg photos in {fixturesDir}, found {photos.Length}.");

            var inbox = SecureLanServerService.MobileAutoImportInboxFolder(client.Id, client.FinancialYear);
            Directory.CreateDirectory(inbox);
            foreach (var p in photos)
                File.Copy(p, Path.Combine(inbox, Path.GetFileName(p)), overwrite: true);

            var swept = server.SweepLocalAutoImportInbox(client.Id, client.FinancialYear, triggerOcr: false);
            Check(swept == 5, $"Expected 5 files swept from AutoImportInbox, got {swept}.");
            var queue = server.GetMobileInvoiceQueue(client.Id, client.FinancialYear);
            Check(queue.Count == 5, $"Expected 5 queued scans, got {queue.Count}.");

            var purRepo = new SqlitePurchaseRecordRepository();
            purRepo.Initialize();
            var totalPurchaseRows = 0;

            foreach (var scan in queue)
            {
                var (status, docRes) = await InvoiceOcrChildBridge.ExtractForPhoneLinkAsync(
                    scan.ImagePaths, client, scan.ScanId, scan.EvidenceFolder, scan.PayloadJson, "", null, CancellationToken.None);
                Check(status.Stage1Succeeded && docRes is { WasSuccessful: true },
                    $"InvoiceVision extraction failed for {scan.ScanId}: {status.Message}");

                server.ApplyPhoneLinkOcrResultForTest(scan, docRes!);
                using var doc = JsonDocument.Parse(docRes!.PayloadJson);
                var r = doc.RootElement;
                decimal D(string prop) => r.TryGetProperty(prop, out var v) && decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;
                var lines = new System.Collections.Generic.List<MobileInvoiceMappedLine>();
                if (r.TryGetProperty("rows", out var rowsEl) && rowsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rowEl in rowsEl.EnumerateArray())
                    {
                        decimal RD(string p) => rowEl.TryGetProperty(p, out var v) && decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;
                        string RS(string p) => rowEl.TryGetProperty(p, out var v) ? (v.GetString() ?? "") : "";
                        lines.Add(new MobileInvoiceMappedLine
                        {
                            Description = RS("description"), HsnSac = RS("hsnSac"),
                            Quantity = RD("quantity"), Unit = RS("unit"), Rate = RD("rate"),
                            Taxable = RD("taxable"), GstRate = RD("gstRate"),
                            Cgst = RD("cgst"), Sgst = RD("sgst"), Igst = RD("igst"), Cess = RD("cess"), LineTotal = RD("lineTotal")
                        });
                    }
                }
                var mapped = new MobileInvoiceMappedResult
                {
                    Direction = docRes.Direction,
                    RecognitionMode = "Accounting Invoice",
                    DocumentType = r.TryGetProperty("documentType", out var dtEl) ? (dtEl.GetString() ?? "Tax Invoice") : "Tax Invoice",
                    InvoiceNumber = docRes.InvoiceNumber,
                    InvoiceDate = DateTime.TryParseExact(docRes.InvoiceDate, new[] { "dd-MM-yyyy", "yyyy-MM-dd", "dd/MM/yyyy" }, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt) ? dt : new DateTime(2026, 10, 29),
                    CounterpartyName = docRes.CounterpartyName,
                    CounterpartyGstin = docRes.CounterpartyGstin,
                    TaxableTotal = D("taxableTotal"),
                    CgstTotal = D("cgstTotal"),
                    SgstTotal = D("sgstTotal"),
                    IgstTotal = D("igstTotal"),
                    CessTotal = D("cessTotal"),
                    RoundOff = D("roundOff"),
                    InvoiceTotal = D("invoiceTotal"),
                    SourceDiscrepanciesReviewed = true,
                    SourceImageSha256 = r.GetProperty("ocrSourceSha256").GetString() ?? "",
                    Lines = lines
                };

                server.CompleteMobileInvoiceReview(client.Id, client.FinancialYear, scan.ScanId, true, mapped);
                var saved = purRepo.GetRecords(client.Id, client.FinancialYear).Where(x => x.DocumentNumber == mapped.InvoiceNumber).ToArray();
                totalPurchaseRows += saved.Length;
                var slabSum = saved.Sum(x => x.TaxableValue + x.Cgst + x.Sgst + x.Igst + x.Cess + x.RoundOff);
                Check(saved.Length >= 1 && saved.All(x => !x.HasInventory && string.IsNullOrEmpty(x.ItemName) && x.ActualQuantity == 0m && string.IsNullOrEmpty(x.Unit) && x.ItemRate == 0m && (x.Igst > 0m || Math.Abs(x.Cgst - x.Sgst) <= 0.005m)),
                    $"Accounting Invoice / CGST==SGST check failed for {mapped.InvoiceNumber}: rows={saved.Length}");
                Check(Math.Abs(slabSum - mapped.InvoiceTotal) <= 0.25m,
                    $"Slab sum mismatch for {mapped.InvoiceNumber}: {slabSum:0.00} vs {mapped.InvoiceTotal:0.00}");
                var mem = MobileSupplierMemoryStore.GetSupplierSummary(mapped.CounterpartyGstin);
                Check(mem.Found && mem.ConfirmedCount >= 1, $"Supplier memory missing for {mapped.CounterpartyGstin}");
                Console.WriteLine($"[5-PHOTO PASS] {mapped.InvoiceNumber} | {mapped.CounterpartyName} | Items={lines.Count} -> AccountingRows={saved.Length} | Total={mapped.InvoiceTotal:0.00} | InvFields=0 | Memory=ON");
            }
            Console.WriteLine($"ALL 5 REAL PHOTOS PASSED END-TO-END (Total Accounting Rows={totalPurchaseRows}, Zero Inventory Rows)");
            return 0;
        }
        finally
        {
            ApplicationDataPaths.ConfigureRootOverride(null);
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }
}
