using AccountingAutomationHub.Domain.Common;
using System.Globalization;
using AccountingAutomationHub.Domain.Automation;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Automation;

public sealed class SqliteAutomationRepository
{
    private readonly string _connectionString;

    public SqliteAutomationRepository()
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

        SqliteSchemaInitializationGate.RunOnce(nameof(SqliteAutomationRepository), _connectionString, InitializeCore);


    private void InitializeCore()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS AutomationFolders
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                FolderId        TEXT NOT NULL COLLATE NOCASE,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL,
                FolderPath      TEXT NOT NULL COLLATE NOCASE,
                FolderName      TEXT NOT NULL DEFAULT '',
                IsPaused        INTEGER NOT NULL DEFAULT 0,
                LastImportUtc   TEXT NOT NULL DEFAULT '',
                LastStatus      TEXT NOT NULL DEFAULT '',
                CreatedUtc      TEXT NOT NULL,
                UpdatedUtc      TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_AutomationFolders_FolderId
            ON AutomationFolders(FolderId COLLATE NOCASE);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_AutomationFolders_Path
            ON AutomationFolders(FolderPath COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS IX_AutomationFolders_ClientFy
            ON AutomationFolders(ClientId,FinancialYear,IsPaused);

            CREATE TABLE IF NOT EXISTS AutomationFileHistory
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                FolderId        TEXT NOT NULL COLLATE NOCASE,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL,
                FilePath        TEXT NOT NULL DEFAULT '',
                FileHash        TEXT NOT NULL COLLATE NOCASE,
                FileType        TEXT NOT NULL DEFAULT '',
                Status          TEXT NOT NULL DEFAULT '',
                Detail          TEXT NOT NULL DEFAULT '',
                RecordCount     INTEGER NOT NULL DEFAULT 0,
                FileSize        INTEGER NOT NULL DEFAULT 0,
                LastWriteUtc    TEXT NOT NULL DEFAULT '',
                ProcessedUtc    TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            DROP INDEX IF EXISTS UX_AutomationFileHistory_FolderHash;
            CREATE UNIQUE INDEX IF NOT EXISTS UX_AutomationFileHistory_FolderClientHash
            ON AutomationFileHistory(FolderId COLLATE NOCASE,ClientId,FileHash COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS IX_AutomationFileHistory_ClientFy
            ON AutomationFileHistory(ClientId,FinancialYear,ProcessedUtc);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "AutomationFileHistory", "RecordCount", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "AutomationFileHistory", "FileSize", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "AutomationFileHistory", "LastWriteUtc", "TEXT NOT NULL DEFAULT ''");
        using (var pathIndex = connection.CreateCommand())
        {
            pathIndex.CommandText = "CREATE INDEX IF NOT EXISTS IX_AutomationFileHistory_FolderClientPath ON AutomationFileHistory(FolderId COLLATE NOCASE,ClientId,FilePath COLLATE NOCASE,ProcessedUtc);";
            pathIndex.ExecuteNonQuery();
        }
    }

    public void UpdateFileRegistryMetadata(long historyId, string filePath, long fileSize, DateTime lastWriteUtc)
    {
        if (historyId <= 0) return;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE AutomationFileHistory
            SET FilePath=$FilePath,FileSize=$FileSize,LastWriteUtc=$LastWriteUtc
            WHERE Id=$Id;
            """;
        command.Parameters.AddWithValue("$FilePath", NormalizePath(filePath));
        command.Parameters.AddWithValue("$FileSize", fileSize);
        command.Parameters.AddWithValue("$LastWriteUtc", lastWriteUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$Id", historyId);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<AutomationFolderRegistration> GetFolders()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.Id,f.FolderId,f.ClientId,c.ClientName,c.Gstin,f.FinancialYear,
                   f.FolderPath,f.FolderName,f.IsPaused,f.CreatedUtc,f.UpdatedUtc,
                   f.LastImportUtc,f.LastStatus
            FROM AutomationFolders f
            JOIN Clients c ON c.Id=f.ClientId
            ORDER BY c.ClientName COLLATE NOCASE,f.FinancialYear,f.FolderPath COLLATE NOCASE;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<AutomationFolderRegistration>();
        while (reader.Read()) result.Add(ReadFolder(reader));
        return result;
    }

    public AutomationFolderRegistration? GetByFolderId(string folderId) =>
        GetOne("f.FolderId=$Value", folderId);

    public AutomationFolderRegistration? GetByPath(string folderPath) =>
        GetOne("f.FolderPath=$Value", NormalizePath(folderPath));

    public AutomationFolderRegistration Register(
        string folderId,
        long clientId,
        string financialYear,
        string folderPath,
        string folderName)
    {
        var now = DateTime.UtcNow.ToString("O");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO AutomationFolders
            (FolderId,ClientId,FinancialYear,FolderPath,FolderName,IsPaused,LastStatus,CreatedUtc,UpdatedUtc)
            VALUES($FolderId,$ClientId,$FinancialYear,$FolderPath,$FolderName,0,'Linked',$Now,$Now)
            ON CONFLICT(FolderId)
            DO UPDATE SET ClientId=excluded.ClientId,FinancialYear=excluded.FinancialYear,
                          FolderPath=excluded.FolderPath,FolderName=excluded.FolderName,
                          LastStatus='Linked',UpdatedUtc=excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$FolderId", folderId.Trim());
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$FolderPath", NormalizePath(folderPath));
        command.Parameters.AddWithValue("$FolderName", folderName.Trim());
        command.Parameters.AddWithValue("$Now", now);
        command.ExecuteNonQuery();
        return GetByFolderId(folderId) ?? throw new InvalidOperationException("The Automation Folder could not be registered.");
    }

    public void UpdatePath(string folderId, string folderPath, string folderName)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE AutomationFolders SET FolderPath=$Path,FolderName=$Name,UpdatedUtc=$Now WHERE FolderId=$FolderId;";
        command.Parameters.AddWithValue("$Path", NormalizePath(folderPath));
        command.Parameters.AddWithValue("$Name", folderName.Trim());
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$FolderId", folderId.Trim());
        command.ExecuteNonQuery();
    }

    public void RebaseFolderPaths(
        IReadOnlyList<(string FolderId, string OldFolderPath, string NewFolderPath, string FolderName)> rebases)
    {
        if (rebases.Count == 0) return;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");

        foreach (var item in rebases)
        {
            var folderId = item.FolderId.Trim();
            var oldRoot = NormalizePath(item.OldFolderPath);
            var newRoot = NormalizePath(item.NewFolderPath);

            using (var folderCommand = connection.CreateCommand())
            {
                folderCommand.Transaction = transaction;
                folderCommand.CommandText =
                    "UPDATE AutomationFolders SET FolderPath=$Path,FolderName=$Name,UpdatedUtc=$Now WHERE FolderId=$FolderId;";
                folderCommand.Parameters.AddWithValue("$Path", newRoot);
                folderCommand.Parameters.AddWithValue("$Name", item.FolderName.Trim());
                folderCommand.Parameters.AddWithValue("$Now", now);
                folderCommand.Parameters.AddWithValue("$FolderId", folderId);
                if (folderCommand.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException($"Automation Folder {folderId} could not be rebound to its moved path.");
            }

            var historyPaths = new List<(long Id, string Path)>();
            using (var readHistory = connection.CreateCommand())
            {
                readHistory.Transaction = transaction;
                readHistory.CommandText = "SELECT Id,FilePath FROM AutomationFileHistory WHERE FolderId=$FolderId AND FilePath<>'';";
                readHistory.Parameters.AddWithValue("$FolderId", folderId);
                using var reader = readHistory.ExecuteReader();
                while (reader.Read()) historyPaths.Add((reader.GetInt64(0), reader.GetString(1)));
            }

            foreach (var history in historyPaths)
            {
                if (!TryRebaseChildPath(history.Path, oldRoot, newRoot, out var rebasedPath)) continue;
                using var updateHistory = connection.CreateCommand();
                updateHistory.Transaction = transaction;
                updateHistory.CommandText = "UPDATE AutomationFileHistory SET FilePath=$Path WHERE Id=$Id;";
                updateHistory.Parameters.AddWithValue("$Path", rebasedPath);
                updateHistory.Parameters.AddWithValue("$Id", history.Id);
                updateHistory.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    private static bool TryRebaseChildPath(string candidate, string oldRoot, string newRoot, out string rebased)
    {
        rebased = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        string full;
        try { full = NormalizePath(candidate); }
        catch { return false; }

        if (full.Equals(oldRoot, StringComparison.OrdinalIgnoreCase))
        {
            rebased = newRoot;
            return true;
        }

        var prefix = oldRoot.EndsWith(Path.DirectorySeparatorChar) || oldRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? oldRoot
            : oldRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var relative = full[prefix.Length..];
        rebased = NormalizePath(Path.Combine(newRoot, relative));
        return true;
    }


    public AutomationFolderRegistration Relink(
        string folderId,
        long clientId,
        string financialYear,
        string folderPath,
        string folderName)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE AutomationFolders
            SET ClientId=$ClientId,
                FinancialYear=$FinancialYear,
                FolderPath=$Path,
                FolderName=$Name,
                IsPaused=0,
                LastStatus='Relinked',
                UpdatedUtc=$Now
            WHERE FolderId=$FolderId;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$Path", NormalizePath(folderPath));
        command.Parameters.AddWithValue("$Name", folderName.Trim());
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$FolderId", folderId.Trim());
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The existing Automation Folder link could not be updated.");
        return GetByFolderId(folderId)
            ?? throw new InvalidOperationException("The relinked Automation Folder could not be reloaded.");
    }

    public void SetPaused(string folderId, bool paused)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE AutomationFolders SET IsPaused=$Paused,UpdatedUtc=$Now WHERE FolderId=$FolderId;";
        command.Parameters.AddWithValue("$Paused", paused ? 1 : 0);
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$FolderId", folderId.Trim());
        command.ExecuteNonQuery();
    }

    public void Unlink(string folderId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AutomationFolders WHERE FolderId=$FolderId;";
        command.Parameters.AddWithValue("$FolderId", folderId.Trim());
        command.ExecuteNonQuery();
    }

    public bool IsFileProcessed(string folderId, long clientId, string fileHash)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM AutomationFileHistory WHERE FolderId=$FolderId AND ClientId=$ClientId AND FileHash=$Hash AND Status IN ('Imported','Duplicate'));";
        command.Parameters.AddWithValue("$FolderId", folderId.Trim());
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Hash", fileHash.Trim());
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    public AutomationFileHistoryRecord? GetProcessedFile(string folderId, long clientId, string fileHash)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,FolderId,ClientId,FinancialYear,FilePath,FileHash,FileType,Status,Detail,RecordCount,FileSize,LastWriteUtc,ProcessedUtc
            FROM AutomationFileHistory
            WHERE ClientId=$ClientId AND FileHash=$Hash
              AND FinancialYear=COALESCE((SELECT FinancialYear FROM AutomationFolders WHERE FolderId=$FolderId LIMIT 1), FinancialYear)
              AND Status IN ('Imported','Duplicate')
            ORDER BY ProcessedUtc DESC,Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$FolderId", folderId.Trim());
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Hash", fileHash.Trim());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new AutomationFileHistoryRecord
        {
            PersistentId = reader.GetInt64(0),
            FolderId = reader.GetString(1),
            ClientId = reader.GetInt64(2),
            FinancialYear = reader.GetString(3),
            FilePath = reader.GetString(4),
            FileHash = reader.GetString(5),
            FileType = reader.GetString(6),
            Status = reader.GetString(7),
            Detail = reader.GetString(8),
            RecordCount = reader.GetInt32(9),
            FileSize = reader.IsDBNull(10) ? 0 : reader.GetInt64(10),
            LastWriteUtc = reader.IsDBNull(11) ? DateTime.MinValue : ParseDate(reader.GetString(11)),
            ProcessedUtc = ParseDate(reader.GetString(12))
        };
    }

    public IReadOnlyList<AutomationFileHistoryRecord> GetHistory(long clientId, string financialYear, int limit = 100)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,FolderId,ClientId,FinancialYear,FilePath,FileHash,FileType,Status,Detail,RecordCount,FileSize,LastWriteUtc,ProcessedUtc
            FROM AutomationFileHistory
            WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear
            ORDER BY ProcessedUtc DESC,Id DESC
            LIMIT $Limit;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$Limit", Math.Max(1, Math.Min(5000, limit)));
        using var reader = command.ExecuteReader();
        var result = new List<AutomationFileHistoryRecord>();
        while (reader.Read())
        {
            result.Add(new AutomationFileHistoryRecord
            {
                PersistentId = reader.GetInt64(0),
                FolderId = reader.GetString(1),
                ClientId = reader.GetInt64(2),
                FinancialYear = reader.GetString(3),
                FilePath = reader.GetString(4),
                FileHash = reader.GetString(5),
                FileType = reader.GetString(6),
                Status = reader.GetString(7),
                Detail = reader.GetString(8),
                RecordCount = reader.GetInt32(9),
                FileSize = reader.IsDBNull(10) ? 0 : reader.GetInt64(10),
                LastWriteUtc = reader.IsDBNull(11) ? DateTime.MinValue : ParseDate(reader.GetString(11)),
                ProcessedUtc = ParseDate(reader.GetString(12))
            });
        }
        return result;
    }

    public IReadOnlyDictionary<string, DateTime> GetProcessedPathWatermarks(string folderId, long clientId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT FilePath,MAX(ProcessedUtc)
            FROM AutomationFileHistory
            WHERE FolderId=$FolderId
              AND ClientId=$ClientId
              AND FilePath<>''
              AND Status IN ('Imported','Duplicate')
            GROUP BY FilePath COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$FolderId", folderId.Trim());
        command.Parameters.AddWithValue("$ClientId", clientId);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var rawPath = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            if (string.IsNullOrWhiteSpace(rawPath)) continue;
            string normalized;
            try { normalized = NormalizePath(rawPath); }
            catch { normalized = rawPath.Trim(); }
            var processedUtc = reader.IsDBNull(1) ? DateTime.MinValue : ParseDate(reader.GetString(1));
            if (processedUtc == DateTime.MinValue) continue;
            if (!result.TryGetValue(normalized, out var existing) || processedUtc > existing)
                result[normalized] = processedUtc;
        }
        return result;
    }

    public AutomationFileHistoryRecord? GetFileRegistryByPath(string folderId, long clientId, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        string normalized;
        try { normalized = NormalizePath(filePath); }
        catch { normalized = filePath.Trim(); }
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,FolderId,ClientId,FinancialYear,FilePath,FileHash,FileType,Status,Detail,RecordCount,FileSize,LastWriteUtc,ProcessedUtc
            FROM AutomationFileHistory
            WHERE FolderId=$FolderId AND ClientId=$ClientId AND FilePath=$FilePath COLLATE NOCASE
              AND Status IN ('Imported','Duplicate','Needs Action','Failed','Skipped')
            ORDER BY ProcessedUtc DESC,Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$FolderId", folderId.Trim());
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FilePath", normalized);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new AutomationFileHistoryRecord
        {
            PersistentId = reader.GetInt64(0),
            FolderId = reader.GetString(1),
            ClientId = reader.GetInt64(2),
            FinancialYear = reader.GetString(3),
            FilePath = reader.GetString(4),
            FileHash = reader.GetString(5),
            FileType = reader.GetString(6),
            Status = reader.GetString(7),
            Detail = reader.GetString(8),
            RecordCount = reader.GetInt32(9),
            FileSize = reader.IsDBNull(10) ? 0 : reader.GetInt64(10),
            LastWriteUtc = reader.IsDBNull(11) ? DateTime.MinValue : ParseDate(reader.GetString(11)),
            ProcessedUtc = reader.IsDBNull(12) ? DateTime.MinValue : ParseDate(reader.GetString(12))
        };
    }

    public AutomationFileHistoryRecord? GetLatestImportedFileByPath(string folderId, long clientId, string filePath, string fileType)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(fileType)) return null;
        string normalized;
        try { normalized = NormalizePath(filePath); }
        catch { normalized = filePath.Trim(); }
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,FolderId,ClientId,FinancialYear,FilePath,FileHash,FileType,Status,Detail,RecordCount,FileSize,LastWriteUtc,ProcessedUtc
            FROM AutomationFileHistory
            WHERE FolderId=$FolderId AND ClientId=$ClientId AND FilePath=$FilePath COLLATE NOCASE
              AND Status='Imported' AND FileType=$FileType COLLATE NOCASE
            ORDER BY ProcessedUtc DESC,Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$FolderId", folderId.Trim());
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FilePath", normalized);
        command.Parameters.AddWithValue("$FileType", fileType.Trim());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new AutomationFileHistoryRecord
        {
            PersistentId = reader.GetInt64(0),
            FolderId = reader.GetString(1),
            ClientId = reader.GetInt64(2),
            FinancialYear = reader.GetString(3),
            FilePath = reader.GetString(4),
            FileHash = reader.GetString(5),
            FileType = reader.GetString(6),
            Status = reader.GetString(7),
            Detail = reader.GetString(8),
            RecordCount = reader.GetInt32(9),
            FileSize = reader.IsDBNull(10) ? 0 : reader.GetInt64(10),
            LastWriteUtc = reader.IsDBNull(11) ? DateTime.MinValue : ParseDate(reader.GetString(11)),
            ProcessedUtc = reader.IsDBNull(12) ? DateTime.MinValue : ParseDate(reader.GetString(12))
        };
    }

    public IReadOnlyDictionary<string, AutomationFileHistoryRecord> GetFileRegistry(string folderId, long clientId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,FolderId,ClientId,FinancialYear,FilePath,FileHash,FileType,Status,Detail,RecordCount,FileSize,LastWriteUtc,ProcessedUtc
            FROM AutomationFileHistory
            WHERE FolderId=$FolderId AND ClientId=$ClientId AND FilePath<>''
              AND Status IN ('Imported','Duplicate','Needs Action','Failed','Skipped')
            ORDER BY ProcessedUtc DESC,Id DESC;
            """;
        command.Parameters.AddWithValue("$FolderId", folderId.Trim());
        command.Parameters.AddWithValue("$ClientId", clientId);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, AutomationFileHistoryRecord>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var rawPath = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
            if (string.IsNullOrWhiteSpace(rawPath)) continue;
            string normalized;
            try { normalized = NormalizePath(rawPath); }
            catch { normalized = rawPath.Trim(); }
            if (result.ContainsKey(normalized)) continue;
            result[normalized] = new AutomationFileHistoryRecord
            {
                PersistentId = reader.GetInt64(0),
                FolderId = reader.GetString(1),
                ClientId = reader.GetInt64(2),
                FinancialYear = reader.GetString(3),
                FilePath = reader.GetString(4),
                FileHash = reader.GetString(5),
                FileType = reader.GetString(6),
                Status = reader.GetString(7),
                Detail = reader.GetString(8),
                RecordCount = reader.GetInt32(9),
                FileSize = reader.IsDBNull(10) ? 0 : reader.GetInt64(10),
                LastWriteUtc = reader.IsDBNull(11) ? DateTime.MinValue : ParseDate(reader.GetString(11)),
                ProcessedUtc = reader.IsDBNull(12) ? DateTime.MinValue : ParseDate(reader.GetString(12))
            };
        }
        return result;
    }

    public int DeleteProcessedFile(string folderId, long clientId, string fileHash)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM AutomationFileHistory WHERE ClientId=$ClientId AND FileHash=$Hash AND FinancialYear=COALESCE((SELECT FinancialYear FROM AutomationFolders WHERE FolderId=$FolderId LIMIT 1), FinancialYear) AND Status IN ('Imported','Duplicate');";
        command.Parameters.AddWithValue("$FolderId", folderId.Trim());
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Hash", fileHash.Trim());
        return command.ExecuteNonQuery();
    }

    public int DeleteHistoryForSource(long clientId, string financialYear, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return 0;
        string normalized;
        try { normalized = NormalizePath(sourcePath); }
        catch { normalized = sourcePath.Trim(); }
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM AutomationFileHistory WHERE ClientId=$ClientId AND FinancialYear=$FinancialYear AND FilePath=$FilePath COLLATE NOCASE;";
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$FilePath", normalized);
        return command.ExecuteNonQuery();
    }

    public void RecordFile(AutomationFileHistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO AutomationFileHistory
                (FolderId,ClientId,FinancialYear,FilePath,FileHash,FileType,Status,Detail,RecordCount,FileSize,LastWriteUtc,ProcessedUtc)
                VALUES($FolderId,$ClientId,$FinancialYear,$FilePath,$FileHash,$FileType,$Status,$Detail,$RecordCount,$FileSize,$LastWriteUtc,$Now)
                ON CONFLICT(FolderId,ClientId,FileHash)
                DO UPDATE SET
                    FilePath=CASE
                        WHEN AutomationFileHistory.Status='Imported' COLLATE NOCASE
                         AND excluded.Status='Duplicate' COLLATE NOCASE
                        THEN AutomationFileHistory.FilePath ELSE excluded.FilePath END,
                    FileType=CASE
                        WHEN AutomationFileHistory.Status='Imported' COLLATE NOCASE
                         AND excluded.Status='Duplicate' COLLATE NOCASE
                        THEN AutomationFileHistory.FileType ELSE excluded.FileType END,
                    Status=CASE
                        WHEN AutomationFileHistory.Status='Imported' COLLATE NOCASE
                         AND excluded.Status='Duplicate' COLLATE NOCASE
                        THEN AutomationFileHistory.Status ELSE excluded.Status END,
                    Detail=CASE
                        WHEN AutomationFileHistory.Status='Imported' COLLATE NOCASE
                         AND excluded.Status='Duplicate' COLLATE NOCASE
                        THEN AutomationFileHistory.Detail ELSE excluded.Detail END,
                    RecordCount=CASE
                        WHEN AutomationFileHistory.Status='Imported' COLLATE NOCASE
                         AND excluded.Status='Duplicate' COLLATE NOCASE
                        THEN AutomationFileHistory.RecordCount ELSE excluded.RecordCount END,
                    FileSize=CASE
                        WHEN excluded.FileSize>0 THEN excluded.FileSize ELSE AutomationFileHistory.FileSize END,
                    LastWriteUtc=CASE
                        WHEN excluded.LastWriteUtc<>'' THEN excluded.LastWriteUtc ELSE AutomationFileHistory.LastWriteUtc END,
                    ProcessedUtc=CASE
                        WHEN AutomationFileHistory.Status='Imported' COLLATE NOCASE
                         AND excluded.Status='Duplicate' COLLATE NOCASE
                        THEN AutomationFileHistory.ProcessedUtc ELSE excluded.ProcessedUtc END;
                """;
            command.Parameters.AddWithValue("$FolderId", record.FolderId.Trim());
            command.Parameters.AddWithValue("$ClientId", record.ClientId);
            command.Parameters.AddWithValue("$FinancialYear", record.FinancialYear.Trim());
            command.Parameters.AddWithValue("$FilePath", record.FilePath.Trim());
            command.Parameters.AddWithValue("$FileHash", record.FileHash.Trim());
            command.Parameters.AddWithValue("$FileType", record.FileType.Trim());
            command.Parameters.AddWithValue("$Status", record.Status.Trim());
            command.Parameters.AddWithValue("$Detail", record.Detail.Trim());
            command.Parameters.AddWithValue("$RecordCount", Math.Max(0, record.RecordCount));
            command.Parameters.AddWithValue("$FileSize", Math.Max(0L, record.FileSize));
            command.Parameters.AddWithValue("$LastWriteUtc", record.LastWriteUtc == DateTime.MinValue ? string.Empty : record.LastWriteUtc.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE AutomationFolders SET LastImportUtc=$Now,LastStatus=$Status,UpdatedUtc=$Now WHERE FolderId=$FolderId;";
            update.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$Status", record.Status.Trim());
            update.Parameters.AddWithValue("$FolderId", record.FolderId.Trim());
            update.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private AutomationFolderRegistration? GetOne(string whereClause, string value)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT f.Id,f.FolderId,f.ClientId,c.ClientName,c.Gstin,f.FinancialYear,
                   f.FolderPath,f.FolderName,f.IsPaused,f.CreatedUtc,f.UpdatedUtc,
                   f.LastImportUtc,f.LastStatus
            FROM AutomationFolders f
            JOIN Clients c ON c.Id=f.ClientId
            WHERE {whereClause}
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$Value", value.Trim());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadFolder(reader) : null;
    }

    private static AutomationFolderRegistration ReadFolder(SqliteDataReader reader) => new()
    {
        PersistentId = reader.GetInt64(0),
        FolderId = reader.GetString(1),
        ClientId = reader.GetInt64(2),
        ClientName = reader.GetString(3),
        Gstin = reader.GetString(4),
        FinancialYear = reader.GetString(5),
        FolderPath = reader.GetString(6),
        FolderName = reader.GetString(7),
        IsPaused = reader.GetInt32(8) == 1,
        CreatedUtc = ParseDate(reader.GetString(9)),
        UpdatedUtc = ParseDate(reader.GetString(10)),
        LastImportUtc = ParseDate(reader.GetString(11)),
        LastStatus = reader.GetString(12)
    };

    private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string definition)
    {
        SqliteSchemaColumnCache.EnsureColumn(connection, tableName, columnName, definition);
    }

    private SqliteConnection OpenConnection()
    {
        return SqliteConnectionPolicy.Open(_connectionString);
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static DateTime ParseDate(string value) =>
        DateTime.TryParse(value, null, DateTimeStyles.RoundtripKind, out var parsed) ? AppTime.NormalizeUtc(parsed) : DateTime.MinValue;
}
