using System.Globalization;
using System.Text;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Read-only projection used by LAN/Android clients. It never returns database paths,
/// Tally endpoints, OCR/Python paths, password material or internal security tables.
/// </summary>
public sealed class ServerReadModelService
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = ApplicationDataPaths.DatabasePath,
        Mode = SqliteOpenMode.ReadOnly,
        Cache = SqliteCacheMode.Private
    }.ToString();

    public IReadOnlyList<ClientRow> GetClientsForUser(string userName, Func<string,long,string,bool> accessCheck)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,ClientCode,ClientName,LegalName,TradeName,Gstin,State,FinancialYear FROM Clients WHERE IsArchived=0 ORDER BY ClientCode COLLATE NOCASE,ClientName COLLATE NOCASE;";
        using var reader = command.ExecuteReader();
        var rows = new List<ClientRow>();
        while (reader.Read())
        {
            var id=reader.GetInt64(0); var fy=reader.GetString(7);
            if (!accessCheck(userName,id,fy)) continue;
            rows.Add(new ClientRow(id,reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),fy));
        }
        return rows;
    }

    public ClientSummary? GetClientSummary(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var client = connection.CreateCommand();
        client.CommandText = "SELECT ClientCode,ClientName,LegalName,TradeName,Gstin,State,FinancialYear FROM Clients WHERE Id=$Id AND IsArchived=0 LIMIT 1;";
        client.Parameters.AddWithValue("$Id",clientId);
        using var reader=client.ExecuteReader();
        if(!reader.Read()) return null;
        var fy = string.IsNullOrWhiteSpace(financialYear) ? reader.GetString(6) : financialYear.Trim();
        var row = new ClientRow(clientId,reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6));
        reader.Close();
        var bankCount=Count(connection,"BankTransactions",clientId,fy,"AND (RecordStatus='' OR RecordStatus NOT LIKE 'Archived%')");
        var purchaseCount=Count(connection,"PurchaseRecords",clientId,fy,string.Empty);
        var salesCount=Count(connection,"SalesRecords",clientId,fy,string.Empty);
        var journalCount=Count(connection,"JournalVouchers",clientId,fy,string.Empty);
        var cashCount=Count(connection,"CashEntries",clientId,fy,string.Empty);
        var masterCount=Count(connection,"Masters",clientId,fy,"AND IsArchived=0");
        var bankTotals=BankTotals(connection,clientId,fy);
        return new ClientSummary(row,bankCount,purchaseCount,salesCount,journalCount,cashCount,masterCount,bankTotals.Debit,bankTotals.Credit,DateTime.UtcNow);
    }

    public byte[] BuildCsvExport(long clientId,string financialYear,string kind)
    {
        using var connection=OpenConnection();
        var normalized=(kind??string.Empty).Trim().ToLowerInvariant();
        var query=normalized switch
        {
            "bank" => "SELECT TransactionDate,Narration,Debit,Credit,Mode,LedgerName,ReferenceNumber,TransactionId FROM BankTransactions WHERE ClientId=$Client AND FinancialYear=$Fy COLLATE NOCASE AND (RecordStatus='' OR RecordStatus NOT LIKE 'Archived%') ORDER BY TransactionDate,Id",
            "purchase" => "SELECT SupplierGstin,SupplierName,DocumentType,DocumentNumber,DocumentDate,GstDate,DocumentValue,TaxableValue,Igst,Cgst,Sgst,Cess,PurchaseLedger,GstApplicability,ReverseCharge FROM PurchaseRecords WHERE ClientId=$Client AND FinancialYear=$Fy COLLATE NOCASE ORDER BY DocumentDate,Id",
            "sales" => "SELECT CustomerGstin,CustomerName,DocumentType,DocumentNumber,DocumentDate,GstDate,DocumentValue,TaxableValue,Igst,Cgst,Sgst,Cess,SalesLedger FROM SalesRecords WHERE ClientId=$Client AND FinancialYear=$Fy COLLATE NOCASE ORDER BY DocumentDate,Id",
            "journal" => "SELECT VoucherDate,VoucherNumber,VoucherType,Narration,GenerationMode,OutputIgst,OutputCgst,OutputSgst,InputIgst,InputCgst,InputSgst,RcmIgst,RcmCgst,RcmSgst FROM JournalVouchers WHERE ClientId=$Client AND FinancialYear=$Fy COLLATE NOCASE ORDER BY VoucherDate,Id",
            _ => throw new InvalidOperationException("Allowed basic exports are: bank, purchase, sales and journal.")
        };
        using var command=connection.CreateCommand(); command.CommandText=query; command.Parameters.AddWithValue("$Client",clientId); command.Parameters.AddWithValue("$Fy",financialYear.Trim());
        using var reader=command.ExecuteReader(); var sb=new StringBuilder();
        for(var i=0;i<reader.FieldCount;i++){if(i>0)sb.Append(',');sb.Append(Csv(reader.GetName(i)));} sb.AppendLine();
        while(reader.Read())
        {
            for(var i=0;i<reader.FieldCount;i++){if(i>0)sb.Append(',');var value=reader.IsDBNull(i)?string.Empty:Convert.ToString(reader.GetValue(i),CultureInfo.InvariantCulture)??string.Empty;sb.Append(Csv(value));} sb.AppendLine();
        }
        return new UTF8Encoding(true).GetBytes(sb.ToString());
    }

    private static (decimal Debit,decimal Credit) BankTotals(SqliteConnection connection,long clientId,string fy)
    {
        if(!TableExists(connection,"BankTransactions"))return(0,0);
        using var command=connection.CreateCommand();
        command.CommandText="SELECT COALESCE(SUM(Debit),0),COALESCE(SUM(Credit),0) FROM BankTransactions WHERE ClientId=$Client AND FinancialYear=$Fy COLLATE NOCASE AND (RecordStatus='' OR RecordStatus NOT LIKE 'Archived%');";
        command.Parameters.AddWithValue("$Client",clientId);command.Parameters.AddWithValue("$Fy",fy);
        using var reader=command.ExecuteReader();return reader.Read()?(Convert.ToDecimal(reader.GetDouble(0),CultureInfo.InvariantCulture),Convert.ToDecimal(reader.GetDouble(1),CultureInfo.InvariantCulture)):(0,0);
    }

    private static int Count(SqliteConnection connection,string table,long clientId,string fy,string suffix)
    {
        if(!TableExists(connection,table))return 0;
        var columns=Columns(connection,table); if(!columns.Contains("ClientId"))return 0;
        using var command=connection.CreateCommand();
        command.CommandText=columns.Contains("FinancialYear")?$"SELECT COUNT(*) FROM [{table}] WHERE ClientId=$Client AND FinancialYear=$Fy COLLATE NOCASE {suffix};":$"SELECT COUNT(*) FROM [{table}] WHERE ClientId=$Client {suffix};";
        command.Parameters.AddWithValue("$Client",clientId);if(columns.Contains("FinancialYear"))command.Parameters.AddWithValue("$Fy",fy);
        return Convert.ToInt32(command.ExecuteScalar()??0,CultureInfo.InvariantCulture);
    }

    private SqliteConnection OpenConnection(){var c=new SqliteConnection(_connectionString);c.Open();using var p=c.CreateCommand();p.CommandText="PRAGMA query_only=ON; PRAGMA busy_timeout=5000;";p.ExecuteNonQuery();return c;}
    private static bool TableExists(SqliteConnection c,string table){using var q=c.CreateCommand();q.CommandText="SELECT 1 FROM sqlite_master WHERE type='table' AND name=$Name LIMIT 1;";q.Parameters.AddWithValue("$Name",table);return q.ExecuteScalar() is not null;}
    private static HashSet<string> Columns(SqliteConnection c,string table){using var q=c.CreateCommand();q.CommandText=$"PRAGMA table_info([{table}]);";using var r=q.ExecuteReader();var s=new HashSet<string>(StringComparer.OrdinalIgnoreCase);while(r.Read())s.Add(r.GetString(1));return s;}
    private static string Csv(string value){var v=value??string.Empty;return v.IndexOfAny([',','"','\r','\n'])>=0?'"'+v.Replace("\"","\"\"")+'"':v;}

    public sealed record ClientRow(long Id,string ClientCode,string ClientName,string LegalName,string TradeName,string Gstin,string State,string FinancialYear);
    public sealed record ClientSummary(ClientRow Client,int BankTransactions,int PurchaseRows,int SalesRows,int JournalVouchers,int CashEntries,int Masters,decimal BankDebits,decimal BankCredits,DateTime GeneratedUtc);
}
