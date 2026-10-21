using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Preserves machine-local Automation Folder registrations/history across a refreshed Main Server
/// accounting snapshot. Folder paths are workstation-local and must never be taken from or written
/// over by the server snapshot itself.
/// </summary>
public static class NetworkLocalAutomationStateService
{
    public static NetworkLocalAutomationState Capture()
    {
        var folders=new List<NetworkLocalAutomationFolderRow>();
        var history=new List<NetworkLocalAutomationHistoryRow>();
        if(!NetworkClientRuntime.IsNetworkClient||!File.Exists(ApplicationDataPaths.DatabasePath))return new(folders,history);
        try
        {
            using var connection=Open(SqliteOpenMode.ReadOnly);connection.Open();
            if(TableExists(connection,"AutomationFolders"))
            {
                using var command=connection.CreateCommand();
                command.CommandText="SELECT FolderId,ClientId,FinancialYear,FolderPath,FolderName,IsPaused,LastImportUtc,LastStatus,CreatedUtc,UpdatedUtc FROM AutomationFolders WHERE FolderPath<>'' ORDER BY UpdatedUtc DESC LIMIT 1000;";
                using var reader=command.ExecuteReader();while(reader.Read())folders.Add(new(
                    Text(reader,0),reader.GetInt64(1),Text(reader,2),Text(reader,3),Text(reader,4),reader.GetInt32(5)!=0,
                    Text(reader,6),Text(reader,7),Text(reader,8),Text(reader,9)));
            }
            if(TableExists(connection,"AutomationFileHistory"))
            {
                using var command=connection.CreateCommand();
                command.CommandText="SELECT FolderId,ClientId,FinancialYear,FilePath,FileHash,FileType,Status,Detail,RecordCount,ProcessedUtc FROM AutomationFileHistory ORDER BY ProcessedUtc DESC LIMIT 20000;";
                using var reader=command.ExecuteReader();while(reader.Read())history.Add(new(
                    Text(reader,0),reader.GetInt64(1),Text(reader,2),Text(reader,3),Text(reader,4),Text(reader,5),Text(reader,6),Text(reader,7),reader.GetInt32(8),Text(reader,9)));
            }
        }
        catch { }
        return new(folders,history);
    }

    public static void Restore(NetworkLocalAutomationState state)
    {
        if(!NetworkClientRuntime.IsNetworkClient||!File.Exists(ApplicationDataPaths.DatabasePath))return;
        try
        {
            using var connection=Open(SqliteOpenMode.ReadWrite);connection.Open();
            if(!TableExists(connection,"AutomationFolders"))return;
            using var transaction=connection.BeginTransaction();
            using(var deleteHistory=connection.CreateCommand()){deleteHistory.Transaction=transaction;deleteHistory.CommandText="DELETE FROM AutomationFileHistory;";try{deleteHistory.ExecuteNonQuery();}catch{}}
            using(var deleteFolders=connection.CreateCommand()){deleteFolders.Transaction=transaction;deleteFolders.CommandText="DELETE FROM AutomationFolders;";deleteFolders.ExecuteNonQuery();}
            var allowedFolderIds=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var row in state.Folders)
            {
                if(!ClientExists(connection,transaction,row.ClientId))continue;
                using var command=connection.CreateCommand();command.Transaction=transaction;
                command.CommandText="INSERT INTO AutomationFolders(FolderId,ClientId,FinancialYear,FolderPath,FolderName,IsPaused,LastImportUtc,LastStatus,CreatedUtc,UpdatedUtc) VALUES($FolderId,$Client,$Fy,$Path,$Name,$Paused,$LastUtc,$Status,$Created,$Updated);";
                command.Parameters.AddWithValue("$FolderId",row.FolderId);command.Parameters.AddWithValue("$Client",row.ClientId);command.Parameters.AddWithValue("$Fy",row.FinancialYear);command.Parameters.AddWithValue("$Path",row.FolderPath);command.Parameters.AddWithValue("$Name",row.FolderName);command.Parameters.AddWithValue("$Paused",row.IsPaused?1:0);command.Parameters.AddWithValue("$LastUtc",row.LastImportUtc);command.Parameters.AddWithValue("$Status",row.LastStatus);command.Parameters.AddWithValue("$Created",row.CreatedUtc);command.Parameters.AddWithValue("$Updated",row.UpdatedUtc);command.ExecuteNonQuery();
                allowedFolderIds.Add(row.FolderId);
            }
            if(TableExists(connection,"AutomationFileHistory"))
            {
                foreach(var row in state.History.Where(x=>allowedFolderIds.Contains(x.FolderId)).OrderBy(x=>x.ProcessedUtc))
                {
                    using var command=connection.CreateCommand();command.Transaction=transaction;
                    command.CommandText="INSERT OR IGNORE INTO AutomationFileHistory(FolderId,ClientId,FinancialYear,FilePath,FileHash,FileType,Status,Detail,RecordCount,ProcessedUtc) VALUES($FolderId,$Client,$Fy,$Path,$Hash,$Type,$Status,$Detail,$Count,$Utc);";
                    command.Parameters.AddWithValue("$FolderId",row.FolderId);command.Parameters.AddWithValue("$Client",row.ClientId);command.Parameters.AddWithValue("$Fy",row.FinancialYear);command.Parameters.AddWithValue("$Path",row.FilePath);command.Parameters.AddWithValue("$Hash",row.FileHash);command.Parameters.AddWithValue("$Type",row.FileType);command.Parameters.AddWithValue("$Status",row.Status);command.Parameters.AddWithValue("$Detail",row.Detail);command.Parameters.AddWithValue("$Count",row.RecordCount);command.Parameters.AddWithValue("$Utc",row.ProcessedUtc);command.ExecuteNonQuery();
                }
            }
            transaction.Commit();
        }
        catch { }
    }

    private static string Text(SqliteDataReader reader,int ordinal)=>reader.IsDBNull(ordinal)?string.Empty:reader.GetString(ordinal);
    private static SqliteConnection Open(SqliteOpenMode mode)=>new(new SqliteConnectionStringBuilder{DataSource=ApplicationDataPaths.DatabasePath,Mode=mode,Pooling=false}.ToString());
    private static bool TableExists(SqliteConnection connection,string table){using var command=connection.CreateCommand();command.CommandText="SELECT 1 FROM sqlite_master WHERE type='table' AND name=$Name COLLATE NOCASE LIMIT 1;";command.Parameters.AddWithValue("$Name",table);return command.ExecuteScalar() is not null;}
    private static bool ClientExists(SqliteConnection connection,SqliteTransaction transaction,long clientId){using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="SELECT 1 FROM Clients WHERE Id=$Id LIMIT 1;";command.Parameters.AddWithValue("$Id",clientId);return command.ExecuteScalar() is not null;}
}

public sealed record NetworkLocalAutomationState(IReadOnlyList<NetworkLocalAutomationFolderRow> Folders,IReadOnlyList<NetworkLocalAutomationHistoryRow> History);
public sealed record NetworkLocalAutomationFolderRow(string FolderId,long ClientId,string FinancialYear,string FolderPath,string FolderName,bool IsPaused,string LastImportUtc,string LastStatus,string CreatedUtc,string UpdatedUtc);
public sealed record NetworkLocalAutomationHistoryRow(string FolderId,long ClientId,string FinancialYear,string FilePath,string FileHash,string FileType,string Status,string Detail,int RecordCount,string ProcessedUtc);
