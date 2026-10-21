using System.Globalization;
using System.Security.Cryptography;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Governance;

/// <summary>
/// Separate Main Server collaboration store. Messages, shared-file metadata, work tasks and approvals
/// never become BLOBs/rows inside the authoritative accounting database.
/// </summary>
public sealed class CollaborationRepository
{
    private readonly string _root;
    private readonly string _connectionString;

    public CollaborationRepository()
    {
        _root = Path.Combine(ApplicationDataPaths.RootFolder, "Collaboration");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "Files"));
        var database = Path.Combine(_root, "collaboration.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true
        }.ToString();
    }

    public string RootFolder => _root;
    public string DatabasePath => new SqliteConnectionStringBuilder(_connectionString).DataSource;

    public void Initialize() =>

        SqliteSchemaInitializationGate.RunOnce(nameof(CollaborationRepository), _connectionString, InitializeCore);


    private void InitializeCore()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS Messages
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ConversationKey TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                ConversationType TEXT NOT NULL DEFAULT 'Private',
                SenderUserName TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                RecipientUserName TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                ClientId INTEGER NOT NULL DEFAULT 0,
                FinancialYear TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                Module TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                EntityKey TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                Body TEXT NOT NULL DEFAULT '',
                Priority TEXT NOT NULL DEFAULT 'Normal',
                CreatedUtc TEXT NOT NULL,
                ReadUtc TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS IX_Messages_ConversationUtc ON Messages(ConversationKey COLLATE NOCASE,CreatedUtc DESC);
            CREATE INDEX IF NOT EXISTS IX_Messages_RecipientUnread ON Messages(RecipientUserName COLLATE NOCASE,ReadUtc,CreatedUtc DESC);
            CREATE INDEX IF NOT EXISTS IX_Messages_ClientFy ON Messages(ClientId,FinancialYear COLLATE NOCASE,CreatedUtc DESC);

            CREATE TABLE IF NOT EXISTS SharedFiles
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MessageId INTEGER NOT NULL DEFAULT 0,
                FileId TEXT NOT NULL COLLATE NOCASE,
                OriginalName TEXT NOT NULL DEFAULT '',
                StoredPath TEXT NOT NULL DEFAULT '',
                Sha256 TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                FileSize INTEGER NOT NULL DEFAULT 0,
                MimeType TEXT NOT NULL DEFAULT '',
                UploadedBy TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                ClientId INTEGER NOT NULL DEFAULT 0,
                FinancialYear TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                CreatedUtc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_SharedFiles_FileId ON SharedFiles(FileId COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS IX_SharedFiles_Message ON SharedFiles(MessageId);

            CREATE TABLE IF NOT EXISTS WorkTasks
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ItemType TEXT NOT NULL DEFAULT 'Task' COLLATE NOCASE,
                Title TEXT NOT NULL DEFAULT '',
                Description TEXT NOT NULL DEFAULT '',
                AssignedTo TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                CreatedBy TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                ClientId INTEGER NOT NULL DEFAULT 0,
                FinancialYear TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                Module TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                EntityKey TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                Priority TEXT NOT NULL DEFAULT 'Normal',
                Status TEXT NOT NULL DEFAULT 'Open',
                DueUtc TEXT NOT NULL DEFAULT '',
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_WorkTasks_AssigneeStatus ON WorkTasks(AssignedTo COLLATE NOCASE,Status COLLATE NOCASE,UpdatedUtc DESC);

            CREATE TABLE IF NOT EXISTS ApprovalRequests
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RequestType TEXT NOT NULL DEFAULT '',
                ResourceKey TEXT NOT NULL DEFAULT '',
                RequestedBy TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                ApproverRole TEXT NOT NULL DEFAULT '',
                Status TEXT NOT NULL DEFAULT 'Pending',
                Reason TEXT NOT NULL DEFAULT '',
                PayloadHash TEXT NOT NULL DEFAULT '',
                ApprovedBy TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                CreatedUtc TEXT NOT NULL,
                DecidedUtc TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS IX_ApprovalRequests_Status ON ApprovalRequests(Status COLLATE NOCASE,CreatedUtc DESC);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "WorkTasks", "ItemType", "TEXT NOT NULL DEFAULT 'Task' COLLATE NOCASE");
        EnsureColumn(connection, "Messages", "ReplyToMessageId", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "Messages", "EditedUtc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Messages", "DeletedUtc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Messages", "IsPinned", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "Messages", "PinnedBy", "TEXT NOT NULL DEFAULT '' COLLATE NOCASE");
        using var collaborationUpgrade = connection.CreateCommand();
        collaborationUpgrade.CommandText =
            """
            CREATE TABLE IF NOT EXISTS MessageReactions
            (
                MessageId INTEGER NOT NULL,
                UserName TEXT NOT NULL COLLATE NOCASE,
                Reaction TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL,
                PRIMARY KEY(MessageId,UserName,Reaction)
            );
            CREATE INDEX IF NOT EXISTS IX_MessageReactions_Message ON MessageReactions(MessageId);

            CREATE TABLE IF NOT EXISTS ConversationReads
            (
                UserName TEXT NOT NULL COLLATE NOCASE,
                ConversationKey TEXT NOT NULL COLLATE NOCASE,
                LastReadUtc TEXT NOT NULL,
                PRIMARY KEY(UserName,ConversationKey)
            );

            CREATE TABLE IF NOT EXISTS UserChatStatus
            (
                UserName TEXT PRIMARY KEY COLLATE NOCASE,
                Availability TEXT NOT NULL DEFAULT 'Available',
                StatusMessage TEXT NOT NULL DEFAULT '',
                UpdatedUtc TEXT NOT NULL
            );

            UPDATE Messages SET ConversationType='Global',ConversationKey='global:all' WHERE ConversationType='Team' COLLATE NOCASE OR ConversationKey='team:all' COLLATE NOCASE;
            UPDATE Messages SET ConversationType='Private' WHERE ConversationType='Direct' COLLATE NOCASE;
            """;
        collaborationUpgrade.ExecuteNonQuery();
        NormalizeExistingConversationKeys(connection);
    }

    public CollaborationMessageRecord AddMessage(CollaborationMessageRecord record)
    {
        record.CreatedUtc = record.CreatedUtc == default ? DateTime.UtcNow : record.CreatedUtc;
        if (string.IsNullOrWhiteSpace(record.ConversationKey))
            record.ConversationKey = GetConversationKey(record);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Messages(ConversationKey,ConversationType,SenderUserName,RecipientUserName,ClientId,FinancialYear,Module,EntityKey,Body,Priority,ReplyToMessageId,CreatedUtc,ReadUtc,EditedUtc,DeletedUtc,IsPinned,PinnedBy)
            VALUES($Key,$Type,$Sender,$Recipient,$Client,$Fy,$Module,$Entity,$Body,$Priority,$ReplyTo,$Created,'','','',0,'');
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$Key", record.ConversationKey.Trim());
        command.Parameters.AddWithValue("$Type", record.ConversationType.Trim());
        command.Parameters.AddWithValue("$Sender", record.SenderUserName.Trim());
        command.Parameters.AddWithValue("$Recipient", record.RecipientUserName.Trim());
        command.Parameters.AddWithValue("$Client", record.ClientId);
        command.Parameters.AddWithValue("$Fy", record.FinancialYear.Trim());
        command.Parameters.AddWithValue("$Module", record.Module.Trim());
        command.Parameters.AddWithValue("$Entity", record.EntityKey.Trim());
        command.Parameters.AddWithValue("$Body", record.Body.Trim());
        command.Parameters.AddWithValue("$Priority", record.Priority.Trim());
        command.Parameters.AddWithValue("$ReplyTo", record.ReplyToMessageId);
        command.Parameters.AddWithValue("$Created", record.CreatedUtc.ToString("O"));
        record.Id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return record;
    }

    public IReadOnlyList<CollaborationMessageRecord> GetInbox(string userName, int limit = 200)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,ConversationKey,ConversationType,SenderUserName,RecipientUserName,ClientId,FinancialYear,Module,EntityKey,Body,Priority,ReplyToMessageId,CreatedUtc,ReadUtc,EditedUtc,DeletedUtc,IsPinned,PinnedBy
            FROM Messages
            WHERE RecipientUserName='' OR RecipientUserName=$User COLLATE NOCASE OR SenderUserName=$User COLLATE NOCASE
            ORDER BY IsPinned DESC,CreatedUtc DESC LIMIT $Limit;
            """;
        command.Parameters.AddWithValue("$User", userName.Trim());
        command.Parameters.AddWithValue("$Limit", Math.Clamp(limit, 1, 2000));
        using var reader = command.ExecuteReader();
        var result = new List<CollaborationMessageRecord>();
        while (reader.Read()) result.Add(ReadMessage(reader));
        reader.Close();
        DecorateMessages(connection,result,userName.Trim());
        return result;
    }

    public CollaborationMessageRecord? GetMessage(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,ConversationKey,ConversationType,SenderUserName,RecipientUserName,ClientId,FinancialYear,Module,EntityKey,Body,Priority,ReplyToMessageId,CreatedUtc,ReadUtc,EditedUtc,DeletedUtc,IsPinned,PinnedBy FROM Messages WHERE Id=$Id LIMIT 1;";
        command.Parameters.AddWithValue("$Id", id);
        using var reader = command.ExecuteReader();
        var message = reader.Read() ? ReadMessage(reader) : null;
        reader.Close();
        if (message is not null) DecorateMessages(connection,[message],string.Empty);
        return message;
    }

    public void MarkConversationRead(string userName, string conversationKey)
    {
        using var connection = Open();
        var now=DateTime.UtcNow.ToString("O");
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE Messages SET ReadUtc=$Now WHERE ConversationKey=$Key COLLATE NOCASE AND RecipientUserName=$User COLLATE NOCASE AND ReadUtc='';";
            command.Parameters.AddWithValue("$Now", now);
            command.Parameters.AddWithValue("$Key", conversationKey.Trim());
            command.Parameters.AddWithValue("$User", userName.Trim());
            command.ExecuteNonQuery();
        }
        using var state=connection.CreateCommand();
        state.CommandText="INSERT INTO ConversationReads(UserName,ConversationKey,LastReadUtc) VALUES($User,$Key,$Now) ON CONFLICT(UserName,ConversationKey) DO UPDATE SET LastReadUtc=excluded.LastReadUtc;";
        state.Parameters.AddWithValue("$User",userName.Trim());state.Parameters.AddWithValue("$Key",conversationKey.Trim());state.Parameters.AddWithValue("$Now",now);state.ExecuteNonQuery();
    }

    public CollaborationMessageRecord EditMessage(long id,string userName,string body)
    {
        var clean=(body??string.Empty).Trim();if(clean.Length==0)throw new InvalidOperationException("Message text is required.");if(clean.Length>8000)clean=clean[..8000];
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="UPDATE Messages SET Body=$Body,EditedUtc=$Now WHERE Id=$Id AND SenderUserName=$User COLLATE NOCASE AND DeletedUtc='';";command.Parameters.AddWithValue("$Body",clean);command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$Id",id);command.Parameters.AddWithValue("$User",userName.Trim());if(command.ExecuteNonQuery()!=1)throw new InvalidOperationException("Only the sender can edit an active message.");return GetMessage(id)??throw new InvalidOperationException("Message could not be read after editing.");
    }

    public CollaborationMessageRecord DeleteMessage(long id,string userName,bool administrativeDelete=false)
    {
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText=administrativeDelete?"UPDATE Messages SET Body='',DeletedUtc=$Now WHERE Id=$Id AND DeletedUtc='';":"UPDATE Messages SET Body='',DeletedUtc=$Now WHERE Id=$Id AND SenderUserName=$User COLLATE NOCASE AND DeletedUtc='';";command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$Id",id);command.Parameters.AddWithValue("$User",userName.Trim());if(command.ExecuteNonQuery()!=1)throw new InvalidOperationException("Message cannot be deleted or is already deleted.");using(var reactions=connection.CreateCommand()){reactions.CommandText="DELETE FROM MessageReactions WHERE MessageId=$Id;";reactions.Parameters.AddWithValue("$Id",id);reactions.ExecuteNonQuery();}return GetMessage(id)??throw new InvalidOperationException("Message could not be read after deletion.");
    }

    public CollaborationMessageRecord SetMessagePinned(long id,string userName,bool pinned)
    {
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="UPDATE Messages SET IsPinned=$Pinned,PinnedBy=$User WHERE Id=$Id AND DeletedUtc='';";command.Parameters.AddWithValue("$Pinned",pinned?1:0);command.Parameters.AddWithValue("$User",pinned?userName.Trim():string.Empty);command.Parameters.AddWithValue("$Id",id);if(command.ExecuteNonQuery()!=1)throw new InvalidOperationException("Message is unavailable for pinning.");return GetMessage(id)??throw new InvalidOperationException("Message could not be read after pinning.");
    }

    public CollaborationMessageRecord ToggleReaction(long id,string userName,string reaction)
    {
        var value=(reaction??string.Empty).Trim();if(value.Length==0||value.Length>16)throw new InvalidOperationException("Select a valid reaction.");
        using var connection=Open();using var exists=connection.CreateCommand();exists.CommandText="SELECT COUNT(*) FROM Messages WHERE Id=$Id AND DeletedUtc='';";exists.Parameters.AddWithValue("$Id",id);if(Convert.ToInt32(exists.ExecuteScalar()??0,CultureInfo.InvariantCulture)!=1)throw new InvalidOperationException("Message is unavailable.");
        using var check=connection.CreateCommand();check.CommandText="SELECT COUNT(*) FROM MessageReactions WHERE MessageId=$Id AND UserName=$User COLLATE NOCASE AND Reaction=$Reaction;";check.Parameters.AddWithValue("$Id",id);check.Parameters.AddWithValue("$User",userName.Trim());check.Parameters.AddWithValue("$Reaction",value);var present=Convert.ToInt32(check.ExecuteScalar()??0,CultureInfo.InvariantCulture)>0;
        using var change=connection.CreateCommand();change.CommandText=present?"DELETE FROM MessageReactions WHERE MessageId=$Id AND UserName=$User COLLATE NOCASE AND Reaction=$Reaction;":"INSERT INTO MessageReactions(MessageId,UserName,Reaction,CreatedUtc) VALUES($Id,$User,$Reaction,$Now);";change.Parameters.AddWithValue("$Id",id);change.Parameters.AddWithValue("$User",userName.Trim());change.Parameters.AddWithValue("$Reaction",value);change.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));change.ExecuteNonQuery();return GetMessage(id)??throw new InvalidOperationException("Message could not be read after reaction update.");
    }

    public IReadOnlyList<UserChatStatusRecord> GetUserChatStatuses()
    {
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="SELECT UserName,Availability,StatusMessage,UpdatedUtc FROM UserChatStatus ORDER BY UserName COLLATE NOCASE;";using var reader=command.ExecuteReader();var rows=new List<UserChatStatusRecord>();while(reader.Read())rows.Add(new UserChatStatusRecord{UserName=reader.GetString(0),Availability=reader.GetString(1),StatusMessage=reader.GetString(2),UpdatedUtc=ParseDate(reader.GetString(3))});return rows;
    }

    public UserChatStatusRecord SetUserChatStatus(string userName,string availability,string statusMessage)
    {
        var allowed=new[]{"Available","Busy","Do Not Disturb","Away"};var selected=allowed.Contains(availability,StringComparer.OrdinalIgnoreCase)?allowed.First(x=>x.Equals(availability,StringComparison.OrdinalIgnoreCase)):"Available";var message=(statusMessage??string.Empty).Trim();if(message.Length>160)message=message[..160];var now=DateTime.UtcNow;using var connection=Open();using var command=connection.CreateCommand();command.CommandText="INSERT INTO UserChatStatus(UserName,Availability,StatusMessage,UpdatedUtc) VALUES($User,$Availability,$Message,$Now) ON CONFLICT(UserName) DO UPDATE SET Availability=excluded.Availability,StatusMessage=excluded.StatusMessage,UpdatedUtc=excluded.UpdatedUtc;";command.Parameters.AddWithValue("$User",userName.Trim());command.Parameters.AddWithValue("$Availability",selected);command.Parameters.AddWithValue("$Message",message);command.Parameters.AddWithValue("$Now",now.ToString("O"));command.ExecuteNonQuery();return new UserChatStatusRecord{UserName=userName.Trim(),Availability=selected,StatusMessage=message,UpdatedUtc=now};
    }

    public CollaborationFileRecord SaveFile(Stream source, string originalName, string uploadedBy, long clientId, string financialYear, long messageId = 0, string mimeType = "application/octet-stream")
    {
        var safeName = SanitizeFileName(originalName);
        var fileId = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(_root, "Files", DateTime.UtcNow.ToString("yyyy"), DateTime.UtcNow.ToString("MM"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileId + "_" + safeName);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 81920, FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            output.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
            size += read;
        }
        output.Flush(true);
        var sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        var record = new CollaborationFileRecord
        {
            MessageId = messageId,
            FileId = fileId,
            OriginalName = safeName,
            StoredPath = path,
            Sha256 = sha,
            FileSize = size,
            MimeType = mimeType,
            UploadedBy = uploadedBy,
            ClientId = clientId,
            FinancialYear = financialYear,
            CreatedUtc = DateTime.UtcNow
        };
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO SharedFiles(MessageId,FileId,OriginalName,StoredPath,Sha256,FileSize,MimeType,UploadedBy,ClientId,FinancialYear,CreatedUtc)
            VALUES($Message,$FileId,$Name,$Path,$Hash,$Size,$Mime,$User,$Client,$Fy,$Utc);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$Message", messageId);
        command.Parameters.AddWithValue("$FileId", fileId);
        command.Parameters.AddWithValue("$Name", safeName);
        command.Parameters.AddWithValue("$Path", path);
        command.Parameters.AddWithValue("$Hash", sha);
        command.Parameters.AddWithValue("$Size", size);
        command.Parameters.AddWithValue("$Mime", mimeType);
        command.Parameters.AddWithValue("$User", uploadedBy.Trim());
        command.Parameters.AddWithValue("$Client", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        command.Parameters.AddWithValue("$Utc", record.CreatedUtc.ToString("O"));
        record.Id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return record;
    }

    public IReadOnlyList<CollaborationFileRecord> GetRecentFiles(int limit=300)
    {
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="SELECT Id,MessageId,FileId,OriginalName,StoredPath,Sha256,FileSize,MimeType,UploadedBy,ClientId,FinancialYear,CreatedUtc FROM SharedFiles ORDER BY CreatedUtc DESC LIMIT $Limit;";command.Parameters.AddWithValue("$Limit",Math.Clamp(limit,1,1000));using var reader=command.ExecuteReader();var list=new List<CollaborationFileRecord>();while(reader.Read())list.Add(ReadFile(reader));return list;
    }

    public CollaborationFileRecord? GetFile(string fileId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,MessageId,FileId,OriginalName,StoredPath,Sha256,FileSize,MimeType,UploadedBy,ClientId,FinancialYear,CreatedUtc FROM SharedFiles WHERE FileId=$Id COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("$Id", fileId.Trim());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadFile(reader) : null;
    }

    public WorkTaskRecord AddTask(WorkTaskRecord record)
    {
        var now = DateTime.UtcNow;
        record.CreatedUtc = record.CreatedUtc == default ? now : record.CreatedUtc;
        record.UpdatedUtc = now;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO WorkTasks(ItemType,Title,Description,AssignedTo,CreatedBy,ClientId,FinancialYear,Module,EntityKey,Priority,Status,DueUtc,CreatedUtc,UpdatedUtc)
            VALUES($ItemType,$Title,$Description,$Assigned,$CreatedBy,$Client,$Fy,$Module,$Entity,$Priority,$Status,$Due,$Created,$Updated);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$ItemType", string.IsNullOrWhiteSpace(record.ItemType)?"Task":record.ItemType.Trim());
        command.Parameters.AddWithValue("$Title", record.Title.Trim());
        command.Parameters.AddWithValue("$Description", record.Description.Trim());
        command.Parameters.AddWithValue("$Assigned", record.AssignedTo.Trim());
        command.Parameters.AddWithValue("$CreatedBy", record.CreatedBy.Trim());
        command.Parameters.AddWithValue("$Client", record.ClientId);
        command.Parameters.AddWithValue("$Fy", record.FinancialYear.Trim());
        command.Parameters.AddWithValue("$Module", record.Module.Trim());
        command.Parameters.AddWithValue("$Entity", record.EntityKey.Trim());
        command.Parameters.AddWithValue("$Priority", record.Priority.Trim());
        command.Parameters.AddWithValue("$Status", record.Status.Trim());
        command.Parameters.AddWithValue("$Due", record.DueUtc?.ToString("O") ?? string.Empty);
        command.Parameters.AddWithValue("$Created", record.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$Updated", record.UpdatedUtc.ToString("O"));
        record.Id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return record;
    }

    public WorkTaskRecord UpdateTask(long id,string actor,string assignedTo,string status,string priority,DateTime? dueUtc,string description)
    {
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="UPDATE WorkTasks SET AssignedTo=$Assigned,Status=$Status,Priority=$Priority,DueUtc=$Due,Description=$Description,UpdatedUtc=$Now WHERE Id=$Id;";command.Parameters.AddWithValue("$Assigned",assignedTo?.Trim()??string.Empty);command.Parameters.AddWithValue("$Status",status?.Trim()??"Open");command.Parameters.AddWithValue("$Priority",priority?.Trim()??"Normal");command.Parameters.AddWithValue("$Due",dueUtc?.ToUniversalTime().ToString("O")??string.Empty);command.Parameters.AddWithValue("$Description",description??string.Empty);command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$Id",id);if(command.ExecuteNonQuery()!=1)throw new InvalidOperationException("The selected work item no longer exists.");using var read=connection.CreateCommand();read.CommandText="SELECT Id,ItemType,Title,Description,AssignedTo,CreatedBy,ClientId,FinancialYear,Module,EntityKey,Priority,Status,DueUtc,CreatedUtc,UpdatedUtc FROM WorkTasks WHERE Id=$Id LIMIT 1;";read.Parameters.AddWithValue("$Id",id);using var reader=read.ExecuteReader();if(!reader.Read())throw new InvalidOperationException("Work item update could not be read back.");return ReadTask(reader);
    }

    public WorkTaskRecord? GetTask(long id)
    {
        using var connection=Open();
        using var command=connection.CreateCommand();
        command.CommandText="SELECT Id,ItemType,Title,Description,AssignedTo,CreatedBy,ClientId,FinancialYear,Module,EntityKey,Priority,Status,DueUtc,CreatedUtc,UpdatedUtc FROM WorkTasks WHERE Id=$Id LIMIT 1;";
        command.Parameters.AddWithValue("$Id",id);
        using var reader=command.ExecuteReader();
        return reader.Read()?ReadTask(reader):null;
    }

    public IReadOnlyList<WorkTaskRecord> GetTasks(string userName, bool allVisible)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = allVisible
            ? "SELECT Id,ItemType,Title,Description,AssignedTo,CreatedBy,ClientId,FinancialYear,Module,EntityKey,Priority,Status,DueUtc,CreatedUtc,UpdatedUtc FROM WorkTasks ORDER BY UpdatedUtc DESC LIMIT 500;"
            : "SELECT Id,ItemType,Title,Description,AssignedTo,CreatedBy,ClientId,FinancialYear,Module,EntityKey,Priority,Status,DueUtc,CreatedUtc,UpdatedUtc FROM WorkTasks WHERE AssignedTo=$User COLLATE NOCASE OR CreatedBy=$User COLLATE NOCASE ORDER BY UpdatedUtc DESC LIMIT 500;";
        if (!allVisible) command.Parameters.AddWithValue("$User", userName.Trim());
        using var reader = command.ExecuteReader();
        var list = new List<WorkTaskRecord>();
        while (reader.Read()) list.Add(ReadTask(reader));
        return list;
    }

    public ApprovalRequestRecord AddApproval(ApprovalRequestRecord record)
    {
        record.CreatedUtc = record.CreatedUtc == default ? DateTime.UtcNow : record.CreatedUtc;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ApprovalRequests(RequestType,ResourceKey,RequestedBy,ApproverRole,Status,Reason,PayloadHash,ApprovedBy,CreatedUtc,DecidedUtc)
            VALUES($Type,$Resource,$User,$Role,$Status,$Reason,$Hash,'',$Created,'');
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$Type", record.RequestType.Trim());
        command.Parameters.AddWithValue("$Resource", record.ResourceKey.Trim());
        command.Parameters.AddWithValue("$User", record.RequestedBy.Trim());
        command.Parameters.AddWithValue("$Role", record.ApproverRole.Trim());
        command.Parameters.AddWithValue("$Status", record.Status.Trim());
        command.Parameters.AddWithValue("$Reason", record.Reason.Trim());
        command.Parameters.AddWithValue("$Hash", record.PayloadHash.Trim());
        command.Parameters.AddWithValue("$Created", record.CreatedUtc.ToString("O"));
        record.Id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return record;
    }

    public IReadOnlyList<ApprovalRequestRecord> GetApprovals(string userName, bool canApprove)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = canApprove
            ? "SELECT Id,RequestType,ResourceKey,RequestedBy,ApproverRole,Status,Reason,PayloadHash,ApprovedBy,CreatedUtc,DecidedUtc FROM ApprovalRequests ORDER BY CreatedUtc DESC LIMIT 500;"
            : "SELECT Id,RequestType,ResourceKey,RequestedBy,ApproverRole,Status,Reason,PayloadHash,ApprovedBy,CreatedUtc,DecidedUtc FROM ApprovalRequests WHERE RequestedBy=$User COLLATE NOCASE ORDER BY CreatedUtc DESC LIMIT 500;";
        if (!canApprove) command.Parameters.AddWithValue("$User", userName.Trim());
        using var reader = command.ExecuteReader();
        var list = new List<ApprovalRequestRecord>();
        while (reader.Read()) list.Add(ReadApproval(reader));
        return list;
    }

    public ApprovalRequestRecord? GetApproval(long id)
    {
        using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="SELECT Id,RequestType,ResourceKey,RequestedBy,ApproverRole,Status,Reason,PayloadHash,ApprovedBy,CreatedUtc,DecidedUtc FROM ApprovalRequests WHERE Id=$Id LIMIT 1;";
        command.Parameters.AddWithValue("$Id",id);using var reader=command.ExecuteReader();return reader.Read()?ReadApproval(reader):null;
    }

    public void DecideApproval(long id, string approver, bool approve, string reason)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ApprovalRequests SET Status=$Status,ApprovedBy=$User,Reason=CASE WHEN $Reason='' THEN Reason ELSE $Reason END,DecidedUtc=$Utc WHERE Id=$Id AND Status='Pending' COLLATE NOCASE AND RequestedBy<>$User COLLATE NOCASE;";
        command.Parameters.AddWithValue("$Status", approve ? "Approved" : "Rejected");
        command.Parameters.AddWithValue("$User", approver.Trim());
        command.Parameters.AddWithValue("$Reason", reason.Trim());
        command.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$Id", id);
        if (command.ExecuteNonQuery() == 0) throw new InvalidOperationException("Approval could not be completed. The maker and checker must be different users and the request must still be pending.");
    }

    private static void EnsureColumn(SqliteConnection connection,string table,string column,string definition)
    {
        SqliteSchemaColumnCache.EnsureColumn(connection, table, column, definition);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=8000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void NormalizeExistingConversationKeys(SqliteConnection connection)
    {
        var rows = new List<(long Id,string OldKey,string Type,string Sender,string Recipient,long ClientId,string Fy)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT Id,ConversationKey,ConversationType,SenderUserName,RecipientUserName,ClientId,FinancialYear FROM Messages;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetInt64(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetInt64(5),reader.GetString(6)));
        }
        var keyMigrations = new HashSet<(string OldKey,string NewKey)>();
        foreach (var row in rows)
        {
            var type = row.Type.Equals("Direct",StringComparison.OrdinalIgnoreCase) ? "Private"
                : row.Type.Equals("Team",StringComparison.OrdinalIgnoreCase) ? "Global" : row.Type;
            var key = GetConversationKey(new CollaborationMessageRecord
            {
                ConversationType=type,SenderUserName=row.Sender,RecipientUserName=row.Recipient,ClientId=row.ClientId,FinancialYear=row.Fy
            });
            if (!row.OldKey.Equals(key,StringComparison.OrdinalIgnoreCase))
                keyMigrations.Add((row.OldKey,key));
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE Messages SET ConversationType=$Type,ConversationKey=$Key WHERE Id=$Id;";
            update.Parameters.AddWithValue("$Type",type);update.Parameters.AddWithValue("$Key",key);update.Parameters.AddWithValue("$Id",row.Id);update.ExecuteNonQuery();
        }
        foreach (var migration in keyMigrations)
        {
            using var migrateReads = connection.CreateCommand();
            migrateReads.CommandText =
                """
                INSERT INTO ConversationReads(UserName,ConversationKey,LastReadUtc)
                SELECT UserName,$NewKey,LastReadUtc FROM ConversationReads WHERE ConversationKey=$OldKey COLLATE NOCASE
                ON CONFLICT(UserName,ConversationKey) DO UPDATE SET
                    LastReadUtc=CASE WHEN excluded.LastReadUtc>ConversationReads.LastReadUtc THEN excluded.LastReadUtc ELSE ConversationReads.LastReadUtc END;
                DELETE FROM ConversationReads WHERE ConversationKey=$OldKey COLLATE NOCASE;
                """;
            migrateReads.Parameters.AddWithValue("$OldKey",migration.OldKey);
            migrateReads.Parameters.AddWithValue("$NewKey",migration.NewKey);
            migrateReads.ExecuteNonQuery();
        }
    }

    public static string GetConversationKey(CollaborationMessageRecord r)
    {
        if (r.ClientId > 0 || r.ConversationType.Equals("Client",StringComparison.OrdinalIgnoreCase)) return $"client:{r.ClientId}:{r.FinancialYear.Trim().ToUpperInvariant()}";
        if (r.ConversationType.Equals("Announcement",StringComparison.OrdinalIgnoreCase)) return "announcement:all";
        if (r.ConversationType.Equals("Global",StringComparison.OrdinalIgnoreCase) || r.ConversationType.Equals("Team",StringComparison.OrdinalIgnoreCase)) return "global:all";
        var users = new[] { r.SenderUserName.Trim(), r.RecipientUserName.Trim() }.Where(x => x.Length > 0).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        return "private:" + string.Join("|", users).ToLowerInvariant();
    }

    private static void DecorateMessages(SqliteConnection connection,List<CollaborationMessageRecord> messages,string currentUser)
    {
        if(messages.Count==0)return;
        var byId=messages.ToDictionary(x=>x.Id);
        foreach(var message in messages.Where(x=>x.ReplyToMessageId>0))
            if(byId.TryGetValue(message.ReplyToMessageId,out var parent))
            {
                var body=parent.IsDeleted?"[Message deleted]":parent.Body;
                message.ReplyPreview=$"{parent.SenderUserName}: {(body.Length>100?body[..100]+"…":body)}";
            }

        var reactionMap=new Dictionary<long,List<string>>();
        using(var reactions=connection.CreateCommand())
        {
            reactions.CommandText="SELECT MessageId,Reaction,COUNT(*) FROM MessageReactions GROUP BY MessageId,Reaction ORDER BY MessageId,Reaction;";using var reader=reactions.ExecuteReader();while(reader.Read()){var id=reader.GetInt64(0);if(!reactionMap.TryGetValue(id,out var list)){list=[];reactionMap[id]=list;}list.Add($"{reader.GetString(1)} {reader.GetInt32(2)}");}
        }
        foreach(var message in messages)if(reactionMap.TryGetValue(message.Id,out var parts))message.ReactionSummary=string.Join("  ",parts);

        if(currentUser.Length==0)return;
        var reads=new Dictionary<string,DateTime>(StringComparer.OrdinalIgnoreCase);
        using(var state=connection.CreateCommand())
        {
            state.CommandText="SELECT ConversationKey,LastReadUtc FROM ConversationReads WHERE UserName=$User COLLATE NOCASE;";state.Parameters.AddWithValue("$User",currentUser);using var reader=state.ExecuteReader();while(reader.Read())reads[reader.GetString(0)]=ParseDate(reader.GetString(1));
        }
        foreach(var message in messages)message.IsUnreadForCurrentUser=!message.SenderUserName.Equals(currentUser,StringComparison.OrdinalIgnoreCase)&&(!reads.TryGetValue(message.ConversationKey,out var readUtc)||message.CreatedUtc>readUtc);
    }

    private static string SanitizeFileName(string value)
    {
        var name = Path.GetFileName(value ?? string.Empty).Trim();
        if (name.Length == 0) name = "shared-file";
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return name.Length > 160 ? name[..160] : name;
    }

    private static DateTime ParseDate(string value) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : default;
    private static DateTime? ParseNullable(string value) => string.IsNullOrWhiteSpace(value) ? null : ParseDate(value);

    private static CollaborationMessageRecord ReadMessage(SqliteDataReader r) => new()
    {
        Id=r.GetInt64(0),ConversationKey=r.GetString(1),ConversationType=r.GetString(2),SenderUserName=r.GetString(3),RecipientUserName=r.GetString(4),ClientId=r.GetInt64(5),FinancialYear=r.GetString(6),Module=r.GetString(7),EntityKey=r.GetString(8),Body=r.GetString(9),Priority=r.GetString(10),ReplyToMessageId=r.GetInt64(11),CreatedUtc=ParseDate(r.GetString(12)),ReadUtc=ParseNullable(r.GetString(13)),EditedUtc=ParseNullable(r.GetString(14)),DeletedUtc=ParseNullable(r.GetString(15)),IsPinned=r.GetInt32(16)==1,PinnedBy=r.GetString(17)
    };
    private static CollaborationFileRecord ReadFile(SqliteDataReader r) => new()
    {
        Id=r.GetInt64(0),MessageId=r.GetInt64(1),FileId=r.GetString(2),OriginalName=r.GetString(3),StoredPath=r.GetString(4),Sha256=r.GetString(5),FileSize=r.GetInt64(6),MimeType=r.GetString(7),UploadedBy=r.GetString(8),ClientId=r.GetInt64(9),FinancialYear=r.GetString(10),CreatedUtc=ParseDate(r.GetString(11))
    };
    private static WorkTaskRecord ReadTask(SqliteDataReader r) => new()
    {
        Id=r.GetInt64(0),ItemType=r.GetString(1),Title=r.GetString(2),Description=r.GetString(3),AssignedTo=r.GetString(4),CreatedBy=r.GetString(5),ClientId=r.GetInt64(6),FinancialYear=r.GetString(7),Module=r.GetString(8),EntityKey=r.GetString(9),Priority=r.GetString(10),Status=r.GetString(11),DueUtc=ParseNullable(r.GetString(12)),CreatedUtc=ParseDate(r.GetString(13)),UpdatedUtc=ParseDate(r.GetString(14))
    };
    private static ApprovalRequestRecord ReadApproval(SqliteDataReader r) => new()
    {
        Id=r.GetInt64(0),RequestType=r.GetString(1),ResourceKey=r.GetString(2),RequestedBy=r.GetString(3),ApproverRole=r.GetString(4),Status=r.GetString(5),Reason=r.GetString(6),PayloadHash=r.GetString(7),ApprovedBy=r.GetString(8),CreatedUtc=ParseDate(r.GetString(9)),DecidedUtc=ParseNullable(r.GetString(10))
    };
}
