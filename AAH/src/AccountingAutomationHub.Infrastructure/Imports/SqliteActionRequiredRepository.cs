using System.Globalization;
using AccountingAutomationHub.Application.Imports;
using AccountingAutomationHub.Domain.Imports;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Imports;

public sealed class SqliteActionRequiredRepository : IActionRequiredRepository
{
    private readonly string _connectionString;

    public SqliteActionRequiredRepository()
    {
        ApplicationDataPaths.EnsureFolders();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ApplicationDataPaths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString();
    }

    public void Initialize() =>

        SqliteSchemaInitializationGate.RunOnce(nameof(SqliteActionRequiredRepository), _connectionString, InitializeCore);


    private void InitializeCore()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS ActionRequired
            (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId      INTEGER NOT NULL,
                FinancialYear TEXT NOT NULL,
                Module        TEXT NOT NULL COLLATE NOCASE,
                ActionType    TEXT NOT NULL DEFAULT '',
                Title         TEXT NOT NULL DEFAULT '',
                Detail        TEXT NOT NULL DEFAULT '',
                SourceKey     TEXT NOT NULL DEFAULT '',
                Status        TEXT NOT NULL DEFAULT 'Open' COLLATE NOCASE,
                CreatedUtc    TEXT NOT NULL,
                UpdatedUtc    TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_ActionRequired_OpenSource
            ON ActionRequired(ClientId, FinancialYear, Module COLLATE NOCASE, SourceKey COLLATE NOCASE)
            WHERE Status='Open' AND SourceKey<>'';
            CREATE INDEX IF NOT EXISTS IX_ActionRequired_ClientModuleStatus
            ON ActionRequired(ClientId, FinancialYear, Module COLLATE NOCASE, Status COLLATE NOCASE);
            """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ActionRequiredItem> GetOpen(long clientId, string financialYear, string? module = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,ClientId,FinancialYear,Module,ActionType,Title,Detail,SourceKey,Status,CreatedUtc,UpdatedUtc
            FROM ActionRequired
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND Status='Open'
              AND ($Module='' OR Module=$Module)
            ORDER BY CreatedUtc,Id;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$Module", module?.Trim() ?? string.Empty);
        using var reader = command.ExecuteReader();
        var result = new List<ActionRequiredItem>();
        while (reader.Read())
            result.Add(ReadItem(reader));
        return result;
    }

    public IReadOnlyList<ActionRequiredItem> GetAllOpen(string? module = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,ClientId,FinancialYear,Module,ActionType,Title,Detail,SourceKey,Status,CreatedUtc,UpdatedUtc
            FROM ActionRequired
            WHERE Status='Open'
              AND ($Module='' OR Module=$Module)
            ORDER BY CreatedUtc,Id;
            """;
        command.Parameters.AddWithValue("$Module", module?.Trim() ?? string.Empty);
        using var reader = command.ExecuteReader();
        var result = new List<ActionRequiredItem>();
        while (reader.Read())
            result.Add(ReadItem(reader));
        return result;
    }

    public int CountOpen(long clientId, string financialYear, string? module = null)
    {
        using var connection=OpenConnection(); using var command=connection.CreateCommand();
        command.CommandText="SELECT COUNT(*) FROM ActionRequired WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND Status='Open' AND ($Module='' OR Module=$Module);";
        command.Parameters.AddWithValue("$ClientId",clientId); command.Parameters.AddWithValue("$FinancialYear",financialYear.Trim()); command.Parameters.AddWithValue("$Module",module?.Trim()??string.Empty);
        return Convert.ToInt32(command.ExecuteScalar(),CultureInfo.InvariantCulture);
    }

    public void Upsert(ActionRequiredItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var now=DateTime.UtcNow.ToString("O");
        using var connection=OpenConnection(); using var command=connection.CreateCommand();
        if(item.SourceKey.Trim().Length>0)
        {
            command.CommandText=
                """
                INSERT INTO ActionRequired(ClientId,FinancialYear,Module,ActionType,Title,Detail,SourceKey,Status,CreatedUtc,UpdatedUtc)
                VALUES($ClientId,$FinancialYear,$Module,$ActionType,$Title,$Detail,$SourceKey,'Open',$Now,$Now)
                ON CONFLICT(ClientId,FinancialYear,Module,SourceKey) WHERE Status='Open' AND SourceKey<>''
                DO UPDATE SET ActionType=excluded.ActionType,Title=excluded.Title,Detail=excluded.Detail,UpdatedUtc=excluded.UpdatedUtc;
                """;
        }
        else
        {
            command.CommandText="INSERT INTO ActionRequired(ClientId,FinancialYear,Module,ActionType,Title,Detail,SourceKey,Status,CreatedUtc,UpdatedUtc) VALUES($ClientId,$FinancialYear,$Module,$ActionType,$Title,$Detail,'','Open',$Now,$Now);";
        }
        command.Parameters.AddWithValue("$ClientId",item.ClientId); command.Parameters.AddWithValue("$FinancialYear",item.FinancialYear.Trim()); command.Parameters.AddWithValue("$Module",item.Module.Trim()); command.Parameters.AddWithValue("$ActionType",item.ActionType.Trim()); command.Parameters.AddWithValue("$Title",item.Title.Trim()); command.Parameters.AddWithValue("$Detail",item.Detail.Trim()); command.Parameters.AddWithValue("$SourceKey",item.SourceKey.Trim()); command.Parameters.AddWithValue("$Now",now); command.ExecuteNonQuery();
    }

    public void Resolve(long persistentId)
    {
        using var connection=OpenConnection(); using var command=connection.CreateCommand();
        command.CommandText="UPDATE ActionRequired SET Status='Resolved',UpdatedUtc=$Now WHERE Id=$Id;"; command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O")); command.Parameters.AddWithValue("$Id",persistentId); command.ExecuteNonQuery();
    }

    public int ResolveAll(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE ActionRequired SET Status='Resolved',UpdatedUtc=$Now WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND Status='Open';";
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        return command.ExecuteNonQuery();
    }

    public void ResolveBySourceKey(long clientId,string financialYear,string module,string sourceKey)
    {
        using var connection=OpenConnection(); using var command=connection.CreateCommand();
        command.CommandText="UPDATE ActionRequired SET Status='Resolved',UpdatedUtc=$Now WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND Module=$Module AND SourceKey=$SourceKey AND Status='Open';"; command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O")); command.Parameters.AddWithValue("$ClientId",clientId); command.Parameters.AddWithValue("$FinancialYear",financialYear.Trim()); command.Parameters.AddWithValue("$Module",module.Trim()); command.Parameters.AddWithValue("$SourceKey",sourceKey.Trim()); command.ExecuteNonQuery();
    }

    private static ActionRequiredItem ReadItem(SqliteDataReader reader) =>
        new()
        {
            PersistentId = reader.GetInt64(0),
            ClientId = reader.GetInt64(1),
            FinancialYear = reader.GetString(2),
            Module = reader.GetString(3),
            ActionType = reader.GetString(4),
            Title = reader.GetString(5),
            Detail = reader.GetString(6),
            SourceKey = reader.GetString(7),
            Status = reader.GetString(8),
            CreatedUtc = ReadDate(reader.GetString(9)),
            UpdatedUtc = ReadDate(reader.GetString(10))
        };

    private static DateTime ReadDate(string value)=>DateTime.TryParse(value,null,DateTimeStyles.RoundtripKind,out var parsed)?parsed:DateTime.MinValue;
    private SqliteConnection OpenConnection(){
        return SqliteConnectionPolicy.Open(_connectionString);
    }
}
