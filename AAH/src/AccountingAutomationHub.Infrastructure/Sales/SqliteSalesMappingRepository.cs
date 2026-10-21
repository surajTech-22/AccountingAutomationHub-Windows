using System.Text.Json;
using AccountingAutomationHub.Application.Sales;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Sales;

public sealed class SqliteSalesMappingRepository : ISalesMappingRepository
{
    private readonly string _connectionString;
    public SqliteSalesMappingRepository()
    {
        ApplicationDataPaths.EnsureFolders();
        _connectionString=new SqliteConnectionStringBuilder{DataSource=ApplicationDataPaths.DatabasePath,Mode=SqliteOpenMode.ReadWriteCreate,Cache=SqliteCacheMode.Private}.ToString();
    }
    public void Initialize() =>
        SqliteSchemaInitializationGate.RunOnce(nameof(SqliteSalesMappingRepository), _connectionString, InitializeCore);

    private void InitializeCore(){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="""
        CREATE TABLE IF NOT EXISTS SalesHeaderMappings
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ClientId INTEGER NOT NULL,
            FinancialYear TEXT NOT NULL,
            HeaderSignature TEXT NOT NULL COLLATE NOCASE,
            MappingJson TEXT NOT NULL,
            GstTreatment TEXT NOT NULL DEFAULT 'LOCAL',
            TaxColumnMode TEXT NOT NULL DEFAULT 'SINGLE',
            UpdatedUtc TEXT NOT NULL,
            FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX IF NOT EXISTS UX_SalesHeaderMappings_ClientFySignature
        ON SalesHeaderMappings(ClientId,FinancialYear,HeaderSignature COLLATE NOCASE);
        """;cmd.ExecuteNonQuery();}
    public IReadOnlyDictionary<string,string>? Get(long clientId,string financialYear,string headerSignature){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT MappingJson FROM SalesHeaderMappings WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND HeaderSignature=$Signature LIMIT 1;";Scope(cmd,clientId,financialYear,headerSignature);var json=Convert.ToString(cmd.ExecuteScalar())??string.Empty;if(json.Length==0)return null;try{return JsonSerializer.Deserialize<Dictionary<string,string>>(json);}catch{return null;}}
    public void Save(long clientId,string financialYear,string headerSignature,IReadOnlyDictionary<string,string> mapping,string gstTreatment,string taxColumnMode){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO SalesHeaderMappings(ClientId,FinancialYear,HeaderSignature,MappingJson,GstTreatment,TaxColumnMode,UpdatedUtc) VALUES($ClientId,$FinancialYear,$Signature,$Json,$Gst,$Tax,$Now) ON CONFLICT(ClientId,FinancialYear,HeaderSignature) DO UPDATE SET MappingJson=excluded.MappingJson,GstTreatment=excluded.GstTreatment,TaxColumnMode=excluded.TaxColumnMode,UpdatedUtc=excluded.UpdatedUtc;";Scope(cmd,clientId,financialYear,headerSignature);cmd.Parameters.AddWithValue("$Json",JsonSerializer.Serialize(mapping));cmd.Parameters.AddWithValue("$Gst",gstTreatment.Trim().ToUpperInvariant());cmd.Parameters.AddWithValue("$Tax",taxColumnMode.Trim().ToUpperInvariant());cmd.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));cmd.ExecuteNonQuery();}
    public void Delete(long clientId,string financialYear,string headerSignature){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="DELETE FROM SalesHeaderMappings WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND HeaderSignature=$Signature;";Scope(cmd,clientId,financialYear,headerSignature);cmd.ExecuteNonQuery();}
    public (string GstTreatment,string TaxColumnMode) GetOptions(long clientId,string financialYear,string headerSignature){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT GstTreatment,TaxColumnMode FROM SalesHeaderMappings WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND HeaderSignature=$Signature LIMIT 1;";Scope(cmd,clientId,financialYear,headerSignature);using var r=cmd.ExecuteReader();return r.Read()?(r.GetString(0),r.GetString(1)):("LOCAL","SINGLE");}
    private static void Scope(SqliteCommand cmd,long id,string fy,string signature){cmd.Parameters.AddWithValue("$ClientId",id);cmd.Parameters.AddWithValue("$FinancialYear",fy.Trim());cmd.Parameters.AddWithValue("$Signature",signature.Trim());}
    private SqliteConnection Open(){
        return SqliteConnectionPolicy.Open(_connectionString);
    }
}
