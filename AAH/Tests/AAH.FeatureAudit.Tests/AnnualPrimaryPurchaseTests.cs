using System.Text.Json;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Infrastructure.Imports;
using AccountingAutomationHub.Infrastructure.Purchases;

internal static class AnnualPrimaryPurchaseTests
{
    private const string Primary = "29ABCDE1234F1Z5";
    private const string Secondary = "24ABCDE1234F1Z5";
    private const string Fy = "2025-26";

    private static string TwoA(string gstin, string invoiceNumber, string formName)
    {
        return JsonSerializer.Serialize(new
        {
            winreq = new {
                reqid = "GSTR2A - Get All Invoices", gstin, retnprd = "032026",
                FormName = formName, Respdate = "30-Oct-2026", resptime = "10:12:41"
            },
            resp = new {
                b2b = new[] {
                    new {
                        ctin = "36CKVPR6310K1ZH", fldtr1 = "11-May-25", flprdr1 = "Apr-25",
                        inv = new[] {
                            new {
                                inum = invoiceNumber, idt = "29-04-2025", val = 118m,
                                txval = 100m, iamt = 18m, camt = 0m, samt = 0m,
                                pos = "29", inv_typ = "R", rchrg = "N",
                                itms = new[] { new { num = 1, itm_det =
                                    new { rt = 18m, txval = 100m, iamt = 18m, camt = 0m, samt = 0m } } }
                            }
                        }
                    }
                }
            }
        });
    }

    private static string TwoB(string gstin, string invoiceNumber, string formName)
    {
        return JsonSerializer.Serialize(new
        {
            winreq = new {
                reqid = "GSTR2B - Get All Invoices", gstin,
                retnprd = formName.EndsWith("ANNUAL", StringComparison.OrdinalIgnoreCase)
                    ? "032026" : "042025",
                FormName = formName, Respdate = "30-Oct-2026", resptime = "10:12:41"
            },
            resp = new {
                data = new {
                    gendt = "30-Oct-2026",
                    docdata = new {
                        b2b = new[] {
                            new {
                                ctin = "36CKVPR6310K1ZH", trdnm = "SUPPLIER TEST",
                                supfildt = "11-May-2025", supprd = "042025",
                                inv = new[] {
                                    new {
                                        inum = invoiceNumber, idt = "29-04-2025", val = 118m,
                                        txval = 100m, iamt = 18m, camt = 0m, samt = 0m,
                                        pos = "29", inv_typ = "R", rchrg = "N",
                                        itms = new[] { new { num = 1, itm_det =
                                            new { rt = 18m, txval = 100m, iamt = 18m, camt = 0m, samt = 0m } } }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        });
    }

    private static PurchaseAutoImportRequest Request(string root) => new()
    {
        ClientId = 1,
        ClientGstin = Primary,
        FinancialYear = Fy,
        CompanyName = "ANNUAL TEST CLIENT",
        EReturnsRootPath = root,
        SearchWindowsDownloads = false
    };

    private static void Require(bool condition, string error)
    {
        if (!condition) throw new Exception("Annual Purchase regression: " + error);
    }

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAH-AnnualPrimary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var annual = Path.Combine(root, "2025-2026", "imported", Primary, "Annual");
            Directory.CreateDirectory(annual);
            File.WriteAllText(Path.Combine(annual, "two-a.json"), TwoA(Primary, "INV-001", "GSTR2A_ANNUAL"));
            File.WriteAllText(Path.Combine(annual, "two-b.json"), TwoB(Primary, "INV-001", "GSTR2B_ANNUAL"));

            var svc = new PurchaseAutoImportService();
            var annualOnly = svc.ImportAsync(Request(root)).GetAwaiter().GetResult();
            Require(!annualOnly.WasBlocked, "Annual-only files were blocked.");
            Require(annualOnly.Verification.TwoA.AnnualSource.Length > 0 &&
                    annualOnly.Verification.TwoB.AnnualSource.Length > 0,
                "Annual sources were not selected as primary.");
            Require(annualOnly.Verification.TwoA.MonthlySources.Count == 0 &&
                    annualOnly.Verification.TwoB.MonthlySources.Count == 0,
                "Missing monthly files were treated as required.");
            Require(annualOnly.Records.Count > 0, "Valid annual 2A/2B did not produce Purchase rows.");

            var april = Path.Combine(root, "2025-2026", "imported", Primary, "April");
            Directory.CreateDirectory(april);
            File.WriteAllText(Path.Combine(april, "two-a-monthly.json"),
                TwoA(Primary, "UNRELATED-MONTHLY", "GSTR2A_MONTHLY"));
            File.WriteAllText(Path.Combine(april, "two-b-monthly.json"),
                TwoB(Primary, "UNRELATED-MONTHLY", "GSTR2B_MONTHLY"));

            var withMismatch = svc.ImportAsync(Request(root)).GetAwaiter().GetResult();
            Require(!withMismatch.WasBlocked, "Optional monthly discrepancy blocked annual import.");
            Require(withMismatch.Verification.TwoA.Status.Contains("Review Only") &&
                    withMismatch.Verification.TwoB.Status.Contains("Review Only"),
                "Optional discrepancies not reported independently for 2A and 2B.");
            Require(withMismatch.Verification.TwoA.MissingInMonthlyCount > 0 &&
                    withMismatch.Verification.TwoB.ExtraInMonthlyCount > 0,
                "Expected missing/extra reconciliation evidence was discarded.");
            Require(withMismatch.Records.Count == annualOnly.Records.Count &&
                    withMismatch.SourceManifest.All(s => s.SourcePath.Contains("Annual", StringComparison.OrdinalIgnoreCase)),
                "Optional monthly data overrode annual Purchase source.");

            var migratedRoot = Path.Combine(root, "misplaced-winman");
            var misplaced = Path.Combine(migratedRoot, "2025-2026", "imported", Primary, "March");
            Directory.CreateDirectory(misplaced);
            File.WriteAllText(Path.Combine(misplaced, "gstr2a.json"), TwoA(Primary, "INV-001", "GSTR2A_ANNUAL"));
            File.WriteAllText(Path.Combine(misplaced, "gstr2b.json"), TwoB(Primary, "INV-001", "GSTR2B_ANNUAL"));
            var recognized = svc.ImportAsync(Request(migratedRoot)).GetAwaiter().GetResult();
            Require(!recognized.WasBlocked &&
                    recognized.Verification.TwoA.AnnualSource.Contains("March") &&
                    recognized.Verification.TwoB.AnnualSource.Contains("March"),
                "Winman annual metadata in month-named folder was misclassified.");

            // The multi-GSTIN change must never select a sibling registration's
            // purchase exports for the primary registration.
            var secondary = Path.Combine(root, "2025-2026", "imported", Secondary, "Annual");
            Directory.CreateDirectory(secondary);
            File.WriteAllText(Path.Combine(secondary, "two-a.json"), TwoA(Secondary, "OTHER-REGISTRATION", "GSTR2A_ANNUAL"));
            var isolated = svc.ImportAsync(Request(root)).GetAwaiter().GetResult();
            Require(!isolated.WasBlocked &&
                    isolated.Records.Count == annualOnly.Records.Count &&
                    isolated.SourceManifest.All(x => !x.SourcePath.Contains(Secondary, StringComparison.OrdinalIgnoreCase)),
                "Another GSTIN contaminated the primary Purchase import.");

            // Exercise the global shared-cache path too: the filename and folder
            // deliberately do not say ANNUAL. Only Winman FormName proves scope.
            var cachedRoot = Path.Combine(root, "shared-source-cache");
            Directory.CreateDirectory(cachedRoot);
            var cache2A = Path.Combine(cachedRoot, "two-a.json");
            var cache2B = Path.Combine(cachedRoot, "two-b.json");
            File.WriteAllText(cache2A, TwoA(Primary, "INV-001", "GSTR2A_ANNUAL"));
            File.WriteAllText(cache2B, TwoB(Primary, "INV-001", "GSTR2B_ANNUAL"));
            foreach (var path in new[] { cache2A, cache2B })
                SalesPurchaseSourceCache.Record(path, "Purchase", Primary, Fy, "", new FileInfo(path).Length,
                    File.GetLastWriteTimeUtc(path));
            var cachedPaths = SalesPurchaseSourceCache.GetPaths("Purchase", Primary, Fy);
            var cacheResult = svc.ImportAsync(Request("")).GetAwaiter().GetResult();
            Require(!cacheResult.WasBlocked &&
                    cacheResult.Verification.TwoA.AnnualSource.Length > 0 &&
                    cacheResult.Verification.TwoB.AnnualSource.Length > 0 &&
                    cacheResult.Verification.TwoA.MonthlySources.Count == 0 &&
                    cacheResult.Verification.TwoB.MonthlySources.Count == 0,
                $"GSTR2A_ANNUAL / GSTR2B_ANNUAL cache classification failed. " +
                $"paths={cachedPaths.Count}, blocked={cacheResult.WasBlocked}, " +
                $"2A={cacheResult.Verification.TwoA.Status}; A={cacheResult.Verification.TwoA.AnnualSource}; M={cacheResult.Verification.TwoA.MonthlySources.Count}; " +
                $"2B={cacheResult.Verification.TwoB.Status}; A={cacheResult.Verification.TwoB.AnnualSource}; M={cacheResult.Verification.TwoB.MonthlySources.Count}");
            Console.WriteLine("PASS: Annual primary 2A/2B, monthly-optional mismatch, Winman relocated Annual, multi-GSTIN isolation, shared source cache.");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch { /* CI-only disposable regression fixtures */ }
        }
    }
}
