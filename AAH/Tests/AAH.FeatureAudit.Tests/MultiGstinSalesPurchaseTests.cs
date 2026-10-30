using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Sales;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Infrastructure.Clients;
using AccountingAutomationHub.Infrastructure.Gst;
using AccountingAutomationHub.Infrastructure.Sales;
using AccountingAutomationHub.Infrastructure.Purchases;

internal static class MultiGstinSalesPurchaseTests
{
    private const string A = "29ABCDE1234F1Z5";
    private const string B = "27ABCDE1234F1Z5";
    private const string Fy = "2025-26";
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException("MultiGST Sales/Purchase: " + message);
    }

    private static SalesAutoImportResult Sales(string gstin, string fingerprint, string document)
        => new()
        {
            ClientGstin = gstin, FinancialYear = Fy, CompanyName = "EXAMPLE CLIENT",
            SourceFingerprint = fingerprint,
            Records = [new SalesImportRecord
            {
                ClientGstin = gstin, CompanyName = "EXAMPLE CLIENT",
                Destination = "Sales", DocumentNumber = document,
                DocumentDate = new DateTime(2025, 4, 12),
                CustomerGstin = "36ABCDE1234F1Z5", CustomerName = "EXAMPLE CUSTOMER",
                TaxableValue = 100m, Igst = 18m, AccountingAmount = 100m
            }]
        };

    private static PurchaseAutoImportResult Purchase(string gstin, string fingerprint, string document)
        => new()
        {
            ClientGstin = gstin, FinancialYear = Fy, CompanyName = "EXAMPLE CLIENT",
            SourceFingerprint = fingerprint, Records = [new PurchaseImportRecord
            {
                ClientGstin = gstin, CompanyName = "EXAMPLE CLIENT",
                Destination = "Purchase", DocumentNumber = document,
                DocumentDate = new DateTime(2025, 4, 12),
                SupplierGstin = "36ABCDE1234F1Z5", SupplierName = "EXAMPLE SUPPLIER",
                TaxableValue = 100m, Igst = 18m
            }],
            Issues = [], Warnings = [], Summary = new(), Verification = new PurchaseSourceVerification
            {
                TwoA = new PurchaseFormVerification { Form = "2A" },
                TwoB = new PurchaseFormVerification { Form = "2B" }
            }, SourceManifest = []
        };

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAH-MultiGstAccounting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "example.sqlite");
        try
        {
            var clients = new SqliteClientRepository(database);
            clients.Initialize();
            var sales = new SqliteSalesRecordRepository(database);
            sales.Initialize();
            var purchase = new SqlitePurchaseRecordRepository(database);
            purchase.Initialize();
            var gst = new SqliteGstRepository(database);
            gst.Initialize();

            var client = clients.Save(new ClientProfile
            {
                ClientCode = "DEMO-01", ClientName = "EXAMPLE CLIENT",
                Gstin = A, Pan = "ABCDE1234F", AdditionalGstins = [B],
                FinancialYear = Fy
            });

            // Before detaching, the same GSTIN cannot become a second Client
            // Profile in the same financial year (no double ownership/import).
            var duplicateOwnerRejected = false;
            try
            {
                clients.Save(new ClientProfile
                {
                    ClientCode = "DEMO-DUPLICATE", ClientName = "INVALID SECOND OWNER",
                    Gstin = B, Pan = "ABCDE1234F", FinancialYear = Fy
                });
            }
            catch (InvalidOperationException)
            {
                duplicateOwnerRejected = true;
            }
            Check(duplicateOwnerRejected, "A second client claimed an already-linked GSTIN.");

            // Same invoice number, customer and amount in two state registrations
            // must not be considered a duplicate across those GSTINs.
            Check(sales.ReplaceAutomaticImport(client.Id, Fy, Sales(A, "SA-1", "INV-1")) == 1,
                "Primary Sales import produced no row.");
            Check(sales.ReplaceAutomaticImport(client.Id, Fy, Sales(B, "SB-1", "INV-1")) == 1,
                "Secondary Sales was wrongly deduplicated.");
            Check(purchase.ReplaceAutomaticImport(client.Id, Fy, Purchase(A, "PA-1", "INV-1")) == 1,
                "Primary Purchase import produced no row.");
            Check(purchase.ReplaceAutomaticImport(client.Id, Fy, Purchase(B, "PB-1", "INV-1")) == 1,
                "Secondary Purchase was wrongly deduplicated.");
            Check(sales.GetAutomaticRecords(client.Id, Fy).Count == 2 &&
                purchase.GetAutomaticRecords(client.Id, Fy).Count == 2,
                "Single module did not show both registrations.");
            Check(sales.IsAutomaticImportCurrent(client.Id, Fy, "SA-1", A) &&
                sales.IsAutomaticImportCurrent(client.Id, Fy, "SB-1", B) &&
                purchase.IsAutomaticImportCurrent(client.Id, Fy, "PA-1", A) &&
                purchase.IsAutomaticImportCurrent(client.Id, Fy, "PB-1", B),
                "GSTIN-specific import state was overwritten by the other registration.");

            Check(sales.ReplaceAutomaticImport(client.Id, Fy, Sales(B, "SB-2", "INV-2")) == 1 &&
                purchase.ReplaceAutomaticImport(client.Id, Fy, Purchase(B, "PB-2", "INV-2")) == 1,
                "Secondary reimport failed.");
            Check(sales.GetAutomaticRecords(client.Id, Fy).Count(x => x.ClientGstin == A) == 1 &&
                purchase.GetAutomaticRecords(client.Id, Fy).Count(x => x.ClientGstin == A) == 1,
                "Reimport of secondary GSTIN deleted primary GSTIN records.");

            // Clearing the module for one registration must not touch the
            // second registration; reimport must still use the same parser path.
            Check(sales.ClearModule(client.Id, Fy, B) == 1 &&
                purchase.ClearModule(client.Id, Fy, B) == 1,
                "Registration-selective Clear did not remove its own rows.");
            Check(sales.GetAutomaticRecords(client.Id, Fy).Count == 1 &&
                purchase.GetAutomaticRecords(client.Id, Fy).Count == 1,
                "Registration-selective Clear destroyed records of another GSTIN.");
            sales.ReplaceAutomaticImport(client.Id, Fy, Sales(B, "SB-3", "INV-3"));
            purchase.ReplaceAutomaticImport(client.Id, Fy, Purchase(B, "PB-3", "INV-3"));
            Check(sales.GetAutomaticRecords(client.Id, Fy).Count == 2 &&
                purchase.GetAutomaticRecords(client.Id, Fy).Count == 2,
                "GSTIN-specific reimport after Clear did not restore combined module.");

            var oldEvidence = Path.Combine(root, "OldClient", "GST", B, "GSTR-3B");
            Directory.CreateDirectory(oldEvidence);
            var pdfPath = Path.Combine(oldEvidence, "Original-GSTR3B.pdf");
            File.WriteAllText(pdfPath, "TEST-SENTINEL: deletion must preserve physical file.");
            var edited = clients.GetById(client.Id)!;
            edited.AdditionalGstins = [];
            clients.Save(edited);
            Check(clients.GetById(client.Id)!.AdditionalGstins!.Count == 0,
                "Additional GSTIN remained on Client Profile.");
            Check(sales.GetRecords(client.Id, Fy).Count(x => x.ClientGstin == B) == 0 &&
                purchase.GetRecords(client.Id, Fy).Count(x => x.ClientGstin == B) == 0,
                "Detached registration's Sales/Purchase rows not removed.");
            Check(sales.GetRecords(client.Id, Fy).Count(x => x.ClientGstin == A) == 1 &&
                purchase.GetRecords(client.Id, Fy).Count(x => x.ClientGstin == A) == 1,
                "Deleting detached GSTIN destroyed the primary registration.");
            Check(File.Exists(pdfPath) && File.ReadAllText(pdfPath).Contains("TEST-SENTINEL"),
                "Detached GSTR3B physical evidence was deleted.");
            var newClient = clients.Save(new ClientProfile
            {
                ClientCode = "DEMO-02", ClientName = "EXAMPLE CLIENT NEW",
                Gstin = B, Pan = "ABCDE1234F", FinancialYear = Fy
            });
            Check(newClient.Id != client.Id && clients.GetById(newClient.Id)!.Gstin == B,
                "Removed registration could not be created as separate client.");
            Console.WriteLine("PASS: 2 GSTIN Sales/Purchase duplicates, per-registration reimport/currentness, deletion and physical GSTR3B retention.");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch { /* isolated temporary test data */ }
        }
    }
}
