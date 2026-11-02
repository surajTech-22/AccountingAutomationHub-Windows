using System.Globalization;
using System.Text.Json;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Domain.Sales;
using AccountingAutomationHub.Infrastructure.Clients;
using AccountingAutomationHub.Infrastructure.Imports;
using AccountingAutomationHub.Infrastructure.Purchases;
using AccountingAutomationHub.Infrastructure.Sales;
using Microsoft.Data.Sqlite;

internal static class GstinLatestPartyNameTests
{
    private const string First = "36CKVPR6310K1ZH";
    private const string Other = "24AAMCG2274M1Z9";
    private static void Assert(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("GSTIN latest party name: " + reason);
    }

    public static void Run()
    {
        var purchaseRows = new List<PurchaseImportRecord>
        {
            new() { PersistentId = 1, SupplierGstin = First, SupplierName = "Kreativ And Creative",
                    DocumentDate = new DateTime(2025, 12, 19), DocumentNumber = "KC1002170", DocumentValue = 98320 },
            new() { PersistentId = 2, SupplierGstin = First, SupplierName = "Kreativ Creative",
                    DocumentDate = new DateTime(2026, 1, 21), DocumentNumber = "KC1002427", DocumentValue = 27280 },
            new() { PersistentId = 3, SupplierGstin = First, SupplierName = "Kreativ And Creative",
                    DocumentDate = new DateTime(2025, 12, 20), DocumentNumber = "KC1002176", DocumentValue = 77170 },
            new() { PersistentId = 4, SupplierGstin = Other, SupplierName = "Other Supplier",
                    DocumentDate = new DateTime(2026, 2, 14) },
            new() { PersistentId = 5, SupplierGstin = First, SupplierName = "Supplier - " + First,
                    DocumentDate = new DateTime(2026, 2, 28) }
        };
        var changed = GstinLatestPartyNameConsistency.NormalizePurchase(purchaseRows);
        Assert(changed.Count == 3 && purchaseRows.Where(x => x.SupplierGstin == First)
            .All(x => x.SupplierName == "Kreativ Creative"), "Latest named invoice did not rename every matching GSTIN row.");
        Assert(purchaseRows[0].DocumentValue == 98320 && purchaseRows[2].DocumentValue == 77170 &&
               purchaseRows[3].SupplierName == "Other Supplier", "Non-name source accounting was changed.");
        Assert(GstinLatestPartyNameConsistency.NormalizePurchase(purchaseRows).Count == 0,
            "Idempotent refresh must not repeat database updates.");

        var sales = new List<SalesImportRecord>
        {
            new() { PersistentId=1, CustomerGstin=First, CustomerName="Kreativ And Creative",
                    DocumentDate=new DateTime(2025,12,19), OriginalCustomerName="Kreativ And Creative" },
            new() { PersistentId=2, CustomerGstin=First, CustomerName="Kreativ Creative",
                    DocumentDate=new DateTime(2026,1,5), OriginalCustomerName="Kreativ Creative" }
        };
        Assert(GstinLatestPartyNameConsistency.NormalizeSales(sales).Count == 1 &&
               sales.All(x => x.CustomerName == "Kreativ Creative") &&
               sales[0].OriginalCustomerName == "Kreativ And Creative",
               "Sales GSTIN name canonicalization overwrote immutable original source name.");
        Assert(GstinLatestPartyNameConsistency.NormalizeSales(sales,
            new Dictionary<string,string> { [First] = "Approved Manual B2B Name" }).Count == 2 &&
            sales.All(x => x.CustomerName=="Approved Manual B2B Name" &&
                x.CustomerNameSource == "Manual B2B Panel"),
            "Manually approved B2B customer name must win over latest source spelling.");

        var dir = Path.Combine(Path.GetTempPath(), "AAH-LATEST-GSTIN-NAME-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var db = Path.Combine(dir, "accounting.sqlite");
            var clients = new SqliteClientRepository(db);
            clients.Initialize();
            var client = clients.Save(new ClientProfile {
                ClientCode="NAME-TEST", ClientName="Karnataka Demo",
                FinancialYear="2025-26", Pan="CKVPR6310K",
                Gstin="29AAQCM7513B1ZC"
            });
            var purchase = new SqlitePurchaseRecordRepository(db);
            var customerSales = new SqliteSalesRecordRepository(db);
            purchase.Initialize();
            customerSales.Initialize();
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
                       DataSource=db, Mode=SqliteOpenMode.ReadWrite, Cache=SqliteCacheMode.Private
                   }.ToString()))
            {
                connection.Open();
                void Sql(string sql)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText=sql.Replace("@CLIENT@",client.Id.ToString(CultureInfo.InvariantCulture));
                    command.ExecuteNonQuery();
                }
                Sql("""
                    INSERT INTO PurchaseImportBatches (Id,ClientId,FinancialYear,ImportMode,Status,CreatedUtc)
                    VALUES (1,@CLIENT@,'2025-26','Auto Import','Success','2026-11-02');
                    INSERT INTO SalesImportBatches (Id,ClientId,FinancialYear,ImportMode,Status,CreatedUtc)
                    VALUES (1,@CLIENT@,'2025-26','Auto Import','Success','2026-11-02');
                    """);
                Sql($"""
                    INSERT INTO PurchaseRecords (BatchId,ClientId,ClientGstin,FinancialYear,SupplierGstin,
                        SupplierName,DocumentDate,DocumentNumber,DocumentValue,CreatedUtc)
                    VALUES (1,@CLIENT@,'29AAQCM7513B1ZC','2025-26','{First}',
                        'Kreativ And Creative','2025-12-19','KC1002170',98320,'2026-11-02');
                    INSERT INTO PurchaseRecords (BatchId,ClientId,ClientGstin,FinancialYear,SupplierGstin,
                        SupplierName,DocumentDate,DocumentNumber,DocumentValue,CreatedUtc)
                    VALUES (1,@CLIENT@,'29AAQCM7513B1ZC','2025-26','{First}',
                        'Kreativ Creative','2026-01-21','KC1002427',27280,'2026-11-02');
                    INSERT INTO SalesRecords (BatchId,ClientId,ClientGstin,FinancialYear,CustomerGstin,
                        CustomerName,CustomerNameSource,DocumentDate,DocumentNumber,DocumentValue,CreatedUtc)
                    VALUES (1,@CLIENT@,'29AAQCM7513B1ZC','2025-26','{First}',
                        'Kreativ And Creative','GSTR-1','2025-12-19','S-1',98320,'2026-11-02');
                    INSERT INTO SalesRecords (BatchId,ClientId,ClientGstin,FinancialYear,CustomerGstin,
                        CustomerName,CustomerNameSource,DocumentDate,DocumentNumber,DocumentValue,CreatedUtc)
                    VALUES (1,@CLIENT@,'29AAQCM7513B1ZC','2025-26','{First}',
                        'Kreativ Creative','GSTR-1','2026-01-21','S-2',27280,'2026-11-02');
                    """);
            }
            var allPurchase = purchase.GetRecords(client.Id, "2025-26");
            var allSales = customerSales.GetRecords(client.Id, "2025-26");
            Assert(allPurchase.Count == 2 && allPurchase.All(x=>x.SupplierName=="Kreativ Creative") &&
                   allSales.Count == 2 && allSales.All(x=>x.CustomerName=="Kreativ Creative"),
                   "Existing SQLite rows were not renamed on reload.");

            using var verify = new SqliteConnection($"Data Source={db}");
            verify.Open();
            void VerifyTable(string table, string column)
            {
                using var q = verify.CreateCommand();
                q.CommandText=$"""
                    SELECT {column},RecordJson,DocumentValue,IsUserEdited FROM {table}
                    WHERE ClientId=$C ORDER BY Id;
                    """;
                q.Parameters.AddWithValue("$C",client.Id);
                using var reader=q.ExecuteReader();
                var count=0;
                while(reader.Read())
                {
                    var name=reader.GetString(0);
                    using var json=JsonDocument.Parse(reader.GetString(1));
                    Assert(name=="Kreativ Creative" &&
                           json.RootElement.GetProperty(column).GetString()==name &&
                           reader.GetInt32(3)==0, "Name column, JSON or user-edited flag diverged.");
                    Assert(Convert.ToDecimal(reader.GetValue(2),CultureInfo.InvariantCulture)==
                        (count==0 ? 98320 : 27280), "GSTIN name fix changed invoice amount.");
                    count++;
                }
                Assert(count==2,"Unexpected fixture row count.");
            }
            VerifyTable("PurchaseRecords","SupplierName");
            VerifyTable("SalesRecords","CustomerName");
            Assert(purchase.GetRecords(client.Id,"2025-26").All(x=>x.SupplierName=="Kreativ Creative"),
                "Purchase normalization did not persist after a second reload.");
        }
        finally
        {
            try { Directory.Delete(dir,recursive:true); } catch { /* temporary fixture cleanup */ }
        }
    }
}
