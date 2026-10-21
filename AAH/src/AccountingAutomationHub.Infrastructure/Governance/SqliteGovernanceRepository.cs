using System.Globalization;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Governance;

public sealed class SqliteGovernanceRepository
{
    private readonly string _connectionString;

    public SqliteGovernanceRepository()
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

        SqliteSchemaInitializationGate.RunOnce(nameof(SqliteGovernanceRepository), _connectionString, InitializeCore);


    private void InitializeCore()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS AppUsers
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                WindowsUserName TEXT NOT NULL COLLATE NOCASE,
                DisplayName     TEXT NOT NULL DEFAULT '',
                RoleName        TEXT NOT NULL DEFAULT 'Read Only',
                IsActive        INTEGER NOT NULL DEFAULT 1,
                CreatedUtc      TEXT NOT NULL,
                UpdatedUtc      TEXT NOT NULL,
                LastSeenUtc     TEXT NOT NULL DEFAULT ''
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_AppUsers_WindowsUserName
            ON AppUsers(WindowsUserName COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS UserClientAccess
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                WindowsUserName TEXT NOT NULL COLLATE NOCASE,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                IsAllowed       INTEGER NOT NULL DEFAULT 1,
                UpdatedUtc      TEXT NOT NULL,
                ExpiresUtc      TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_UserClientAccess_Key
            ON UserClientAccess(WindowsUserName COLLATE NOCASE,ClientId,FinancialYear COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS ClientLanAvailability
            (
                ClientId        INTEGER PRIMARY KEY,
                IsEnabled       INTEGER NOT NULL DEFAULT 0,
                UpdatedUtc      TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS DeviceClientAccess
            (
                DeviceId        TEXT NOT NULL COLLATE NOCASE,
                ClientId        INTEGER NOT NULL,
                IsAllowed       INTEGER NOT NULL DEFAULT 1,
                UpdatedUtc      TEXT NOT NULL,
                PRIMARY KEY(DeviceId,ClientId),
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_DeviceClientAccess_Client
            ON DeviceClientAccess(ClientId,IsAllowed);

            CREATE TABLE IF NOT EXISTS LanDevices
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                DeviceId        TEXT NOT NULL COLLATE NOCASE,
                DeviceName      TEXT NOT NULL DEFAULT '',
                TokenHash       TEXT NOT NULL COLLATE NOCASE,
                RoleName        TEXT NOT NULL DEFAULT 'Read Only',
                ClientId        INTEGER NOT NULL DEFAULT 0,
                FinancialYear   TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                IsActive        INTEGER NOT NULL DEFAULT 1,
                CreatedUtc      TEXT NOT NULL,
                UpdatedUtc      TEXT NOT NULL,
                LastSeenUtc     TEXT NOT NULL DEFAULT ''
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_LanDevices_DeviceId ON LanDevices(DeviceId COLLATE NOCASE);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_LanDevices_TokenHash ON LanDevices(TokenHash COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS RolePermissions
            (
                RoleName        TEXT NOT NULL COLLATE NOCASE,
                PermissionName  TEXT NOT NULL COLLATE NOCASE,
                IsAllowed       INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(RoleName, PermissionName)
            );

            CREATE TABLE IF NOT EXISTS ImportRuns
            (
                RunId           TEXT PRIMARY KEY COLLATE NOCASE,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL,
                SourceKind      TEXT NOT NULL DEFAULT '',
                RequestedBy     TEXT NOT NULL DEFAULT '',
                Status          TEXT NOT NULL DEFAULT 'Detected',
                TotalFiles      INTEGER NOT NULL DEFAULT 0,
                ImportedFiles   INTEGER NOT NULL DEFAULT 0,
                DuplicateFiles  INTEGER NOT NULL DEFAULT 0,
                ReviewFiles     INTEGER NOT NULL DEFAULT 0,
                FailedFiles     INTEGER NOT NULL DEFAULT 0,
                RecordCount     INTEGER NOT NULL DEFAULT 0,
                IsUndoable      INTEGER NOT NULL DEFAULT 0,
                StartedUtc      TEXT NOT NULL,
                UpdatedUtc      TEXT NOT NULL,
                CompletedUtc    TEXT NOT NULL DEFAULT '',
                Summary         TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_ImportRuns_ClientFyUtc
            ON ImportRuns(ClientId,FinancialYear,StartedUtc DESC);

            CREATE TABLE IF NOT EXISTS ImportJobs
            (
                JobId           TEXT PRIMARY KEY COLLATE NOCASE,
                RunId           TEXT NOT NULL COLLATE NOCASE,
                FolderId        TEXT NOT NULL DEFAULT '',
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL,
                OriginalPath    TEXT NOT NULL DEFAULT '',
                WorkingPath     TEXT NOT NULL DEFAULT '',
                VaultPath       TEXT NOT NULL DEFAULT '',
                FileName        TEXT NOT NULL DEFAULT '',
                FileHash        TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                FileSize        INTEGER NOT NULL DEFAULT 0,
                DetectedModule  TEXT NOT NULL DEFAULT '',
                Confidence      REAL NOT NULL DEFAULT 0,
                State           TEXT NOT NULL DEFAULT 'Detected',
                Detail          TEXT NOT NULL DEFAULT '',
                RecordCount     INTEGER NOT NULL DEFAULT 0,
                AttemptCount    INTEGER NOT NULL DEFAULT 0,
                IsUndoable      INTEGER NOT NULL DEFAULT 0,
                UndoStatus      TEXT NOT NULL DEFAULT '',
                CreatedUtc      TEXT NOT NULL,
                UpdatedUtc      TEXT NOT NULL,
                FOREIGN KEY (RunId) REFERENCES ImportRuns(RunId) ON DELETE CASCADE,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_ImportJobs_ClientFyUtc
            ON ImportJobs(ClientId,FinancialYear,UpdatedUtc DESC);
            CREATE INDEX IF NOT EXISTS IX_ImportJobs_WorkingPath
            ON ImportJobs(WorkingPath COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS IX_ImportJobs_Hash
            ON ImportJobs(ClientId,FinancialYear,FileHash COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS ImportJobAttempts
            (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                JobId       TEXT NOT NULL COLLATE NOCASE,
                AttemptNo   INTEGER NOT NULL DEFAULT 1,
                State       TEXT NOT NULL DEFAULT '',
                Detail      TEXT NOT NULL DEFAULT '',
                AttemptUtc  TEXT NOT NULL,
                FOREIGN KEY (JobId) REFERENCES ImportJobs(JobId) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS ImportJobEntities
            (
                JobId       TEXT NOT NULL COLLATE NOCASE,
                EntityType  TEXT NOT NULL COLLATE NOCASE,
                EntityKey   TEXT NOT NULL COLLATE NOCASE,
                CapturedUtc TEXT NOT NULL,
                PRIMARY KEY(JobId,EntityType,EntityKey),
                FOREIGN KEY (JobId) REFERENCES ImportJobs(JobId) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_ImportJobEntities_Job
            ON ImportJobEntities(JobId,EntityType);

            CREATE TABLE IF NOT EXISTS DocumentVault
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL,
                RunId           TEXT NOT NULL DEFAULT '',
                JobId           TEXT NOT NULL DEFAULT '',
                Module          TEXT NOT NULL DEFAULT '',
                FileName        TEXT NOT NULL DEFAULT '',
                OriginalPath    TEXT NOT NULL DEFAULT '',
                VaultPath       TEXT NOT NULL DEFAULT '',
                FileHash        TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                FileSize        INTEGER NOT NULL DEFAULT 0,
                MimeHint        TEXT NOT NULL DEFAULT '',
                AddedUtc        TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_DocumentVault_ClientFyUtc
            ON DocumentVault(ClientId,FinancialYear,AddedUtc DESC);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_DocumentVault_ClientFyHashPath
            ON DocumentVault(ClientId,FinancialYear,FileHash COLLATE NOCASE,VaultPath COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS EntityVersions
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL,
                Module          TEXT NOT NULL DEFAULT '',
                EntityType      TEXT NOT NULL DEFAULT '',
                EntityKey       TEXT NOT NULL DEFAULT '',
                Operation       TEXT NOT NULL DEFAULT '',
                BeforeJson      TEXT NOT NULL DEFAULT '',
                AfterJson       TEXT NOT NULL DEFAULT '',
                ChangedBy       TEXT NOT NULL DEFAULT '',
                RunId           TEXT NOT NULL DEFAULT '',
                Reason          TEXT NOT NULL DEFAULT '',
                ChangedUtc      TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_EntityVersions_ClientFyUtc
            ON EntityVersions(ClientId,FinancialYear,ChangedUtc DESC);
            CREATE INDEX IF NOT EXISTS IX_EntityVersions_Entity
            ON EntityVersions(ClientId,FinancialYear,Module,EntityType,EntityKey,ChangedUtc DESC);

            CREATE TABLE IF NOT EXISTS VerifiedBackups
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                BackupPath      TEXT NOT NULL COLLATE NOCASE,
                Sha256          TEXT NOT NULL DEFAULT '',
                FileSize        INTEGER NOT NULL DEFAULT 0,
                IntegrityResult TEXT NOT NULL DEFAULT '',
                ScheduleClass   TEXT NOT NULL DEFAULT '',
                CreatedUtc      TEXT NOT NULL,
                VerifiedUtc     TEXT NOT NULL DEFAULT '',
                IsVerified      INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_VerifiedBackups_Path
            ON VerifiedBackups(BackupPath COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS RecurringTransactionProfiles
            (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId            INTEGER NOT NULL,
                FinancialYear       TEXT NOT NULL,
                Module              TEXT NOT NULL,
                ProfileName         TEXT NOT NULL,
                Frequency           TEXT NOT NULL DEFAULT 'Monthly',
                IntervalCount       INTEGER NOT NULL DEFAULT 1,
                StartDate           TEXT NOT NULL,
                EndDate             TEXT NOT NULL DEFAULT '',
                NextRunDate         TEXT NOT NULL,
                AutoCreateAsDraft   INTEGER NOT NULL DEFAULT 1,
                TemplateJson        TEXT NOT NULL DEFAULT '',
                IsActive            INTEGER NOT NULL DEFAULT 1,
                CreatedBy           TEXT NOT NULL DEFAULT '',
                CreatedUtc          TEXT NOT NULL,
                UpdatedUtc          TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_RecurringProfiles_ClientFyModule
            ON RecurringTransactionProfiles(ClientId,FinancialYear,Module,IsActive,NextRunDate);

            CREATE TABLE IF NOT EXISTS TransactionRelationships
            (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId            INTEGER NOT NULL,
                FinancialYear       TEXT NOT NULL,
                Module              TEXT NOT NULL,
                SourceEntityKey     TEXT NOT NULL,
                TargetEntityKey     TEXT NOT NULL,
                RelationshipType    TEXT NOT NULL,
                ProfileId           TEXT NOT NULL DEFAULT '',
                CreatedUtc          TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_TransactionRelationships_Source
            ON TransactionRelationships(ClientId,FinancialYear,Module,SourceEntityKey);

            CREATE TABLE IF NOT EXISTS LedgerSuggestionLearning
            (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId            INTEGER NOT NULL,
                FinancialYear       TEXT NOT NULL,
                SummaryKey          TEXT NOT NULL COLLATE NOCASE,
                LedgerName          TEXT NOT NULL DEFAULT '',
                LedgerGroup         TEXT NOT NULL DEFAULT '',
                AcceptCount         INTEGER NOT NULL DEFAULT 0,
                RejectCount         INTEGER NOT NULL DEFAULT 0,
                LastReason          TEXT NOT NULL DEFAULT '',
                UpdatedUtc          TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE,
                UNIQUE(ClientId,FinancialYear,SummaryKey COLLATE NOCASE,LedgerName COLLATE NOCASE)
            );

            CREATE TABLE IF NOT EXISTS AdvancedReconciliationMatches
            (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                MatchId             TEXT NOT NULL COLLATE NOCASE,
                ClientId            INTEGER NOT NULL,
                FinancialYear       TEXT NOT NULL,
                MatchType           TEXT NOT NULL,
                BankTransactionIds  TEXT NOT NULL DEFAULT '',
                BookReferences      TEXT NOT NULL DEFAULT '',
                BankAmount          REAL NOT NULL DEFAULT 0,
                BookAmount          REAL NOT NULL DEFAULT 0,
                AdjustmentAmount    REAL NOT NULL DEFAULT 0,
                AdjustmentLedger    TEXT NOT NULL DEFAULT '',
                Status              TEXT NOT NULL DEFAULT 'Preview',
                Detail              TEXT NOT NULL DEFAULT '',
                CreatedBy           TEXT NOT NULL DEFAULT '',
                CreatedUtc          TEXT NOT NULL,
                UpdatedUtc          TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_AdvancedReconciliation_MatchId
            ON AdvancedReconciliationMatches(MatchId COLLATE NOCASE);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "AppUsers", "MobileVerificationIntervalDays", "INTEGER NOT NULL DEFAULT 30");
        EnsureColumn(connection, "UserClientAccess", "ExpiresUtc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "DeviceClientAccess", "ExpiresUtc", "TEXT NOT NULL DEFAULT ''");
        MigrateDeviceClientAccessToUsers(connection);
        SeedRolePermissions(connection);
        EnsureCurrentWindowsUser(connection);
    }

    public ApplicationUserRecord GetCurrentUser()
    {
        var windowsUser = Environment.UserName.Trim();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,WindowsUserName,DisplayName,RoleName,IsActive,CreatedUtc,UpdatedUtc,LastSeenUtc,MobileVerificationIntervalDays
            FROM AppUsers WHERE WindowsUserName=$User COLLATE NOCASE LIMIT 1;
            """;
        command.Parameters.AddWithValue("$User", windowsUser);
        using var reader = command.ExecuteReader();
        if (reader.Read()) return ReadUser(reader);
        return new ApplicationUserRecord
        {
            WindowsUserName = windowsUser,
            DisplayName = windowsUser,
            RoleName = GovernanceRoles.ReadOnly,
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
    }

    public IReadOnlyList<ApplicationUserRecord> GetUsers()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,WindowsUserName,DisplayName,RoleName,IsActive,CreatedUtc,UpdatedUtc,LastSeenUtc,MobileVerificationIntervalDays
            FROM AppUsers ORDER BY IsActive DESC,DisplayName COLLATE NOCASE,WindowsUserName COLLATE NOCASE;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<ApplicationUserRecord>();
        while (reader.Read()) result.Add(ReadUser(reader));
        return result;
    }

    public void SaveUser(ApplicationUserRecord user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var windowsName = user.WindowsUserName.Trim();
        if (windowsName.Length == 0) throw new InvalidOperationException("Windows user name is required.");
        if (!GovernanceRoles.All.Contains(user.RoleName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select a valid application role.");
        var now = DateTime.UtcNow.ToString("O");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO AppUsers(WindowsUserName,DisplayName,RoleName,IsActive,CreatedUtc,UpdatedUtc,LastSeenUtc,MobileVerificationIntervalDays)
            VALUES($User,$Display,$Role,$Active,$Now,$Now,'',$MobileDays)
            ON CONFLICT(WindowsUserName) DO UPDATE SET
                DisplayName=excluded.DisplayName,RoleName=excluded.RoleName,IsActive=excluded.IsActive,MobileVerificationIntervalDays=excluded.MobileVerificationIntervalDays,UpdatedUtc=excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$User", windowsName);
        command.Parameters.AddWithValue("$Display", string.IsNullOrWhiteSpace(user.DisplayName) ? windowsName : user.DisplayName.Trim());
        command.Parameters.AddWithValue("$Role", user.RoleName.Trim());
        command.Parameters.AddWithValue("$Active", user.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$MobileDays", Math.Clamp(user.MobileVerificationIntervalDays <= 0 ? 30 : user.MobileVerificationIntervalDays, 1, 365));
        command.Parameters.AddWithValue("$Now", now);
        command.ExecuteNonQuery();
    }

    public bool HasPermission(string? windowsUserName, string permission)
    {
        var userName = string.IsNullOrWhiteSpace(windowsUserName) ? Environment.UserName : windowsUserName.Trim();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COALESCE(rp.IsAllowed,0)
            FROM AppUsers u
            LEFT JOIN RolePermissions rp ON rp.RoleName=u.RoleName COLLATE NOCASE AND rp.PermissionName=$Permission COLLATE NOCASE
            WHERE u.WindowsUserName=$User COLLATE NOCASE AND u.IsActive=1
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$User", userName);
        command.Parameters.AddWithValue("$Permission", permission.Trim());
        return Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) == 1;
    }

    public IReadOnlyList<RolePermissionRecord> GetRolePermissions(string roleName)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT PermissionName,IsAllowed FROM RolePermissions WHERE RoleName=$Role COLLATE NOCASE ORDER BY PermissionName COLLATE NOCASE;";
        command.Parameters.AddWithValue("$Role", roleName.Trim());
        var map = new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
        using (var reader = command.ExecuteReader())
            while (reader.Read()) map[reader.GetString(0)] = reader.GetInt32(1) == 1;
        return GovernancePermissions.All.Select(permission => new RolePermissionRecord
        {
            RoleName = roleName.Trim(),
            PermissionName = permission,
            IsAllowed = map.TryGetValue(permission,out var allowed) && allowed
        }).ToArray();
    }

    public void SetRolePermission(string roleName,string permission,bool allowed)
    {
        if (!GovernanceRoles.All.Contains(roleName,StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Select a valid application role.");
        if (!GovernancePermissions.All.Contains(permission,StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Select a valid permission.");
        if (GovernanceRoles.IsFixedProfile(roleName))
            throw new InvalidOperationException($"'{roleName}' is a fixed built-in role profile. Assign the Custom role when you need an explicit permission mix.");
        using var connection=OpenConnection();using var command=connection.CreateCommand();
        command.CommandText="INSERT INTO RolePermissions(RoleName,PermissionName,IsAllowed) VALUES($Role,$Permission,$Allowed) ON CONFLICT(RoleName,PermissionName) DO UPDATE SET IsAllowed=excluded.IsAllowed;";
        command.Parameters.AddWithValue("$Role",roleName.Trim());command.Parameters.AddWithValue("$Permission",permission.Trim());command.Parameters.AddWithValue("$Allowed",allowed?1:0);command.ExecuteNonQuery();
    }

    public void SetRolePermissions(string roleName,IEnumerable<RolePermissionRecord> permissions)
    {
        if (GovernanceRoles.IsFixedProfile(roleName)) throw new InvalidOperationException($"'{roleName}' is a fixed built-in role profile. Assign the Custom role when you need an explicit permission mix.");
        foreach(var permission in permissions) SetRolePermission(roleName,permission.PermissionName,permission.IsAllowed);
    }

    public void TouchCurrentUser()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE AppUsers SET LastSeenUtc=$Now,UpdatedUtc=$Now WHERE WindowsUserName=$User COLLATE NOCASE;";
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$User", Environment.UserName.Trim());
        command.ExecuteNonQuery();
    }

    public bool HasClientAccess(string windowsUserName, long clientId, string financialYear)
    {
        var user = GetUserByWindowsName(windowsUserName);
        if (user is null || !user.IsActive) return false;
        if (user.RoleName.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase)) return true;

        using var connection = OpenConnection();
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM UserClientAccess WHERE WindowsUserName=$User COLLATE NOCASE;";
        countCommand.Parameters.AddWithValue("$User", windowsUserName.Trim());
        var scopeCount = Convert.ToInt32(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (scopeCount == 0) return true; // preserve unrestricted local installations until an Administrator creates explicit user scopes.

        return HasExplicitClientAccess(connection, windowsUserName, clientId, financialYear);
    }

    public bool HasExplicitClientAccess(string userName, long clientId, string financialYear)
    {
        if (string.IsNullOrWhiteSpace(userName) || clientId <= 0) return false;
        using var connection = OpenConnection();
        return HasExplicitClientAccess(connection, userName, clientId, financialYear);
    }

    private static bool HasExplicitClientAccess(SqliteConnection connection, string userName, long clientId, string financialYear)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT IsAllowed FROM UserClientAccess
            WHERE WindowsUserName=$User COLLATE NOCASE
              AND ClientId=$ClientId
              AND (FinancialYear=$Fy COLLATE NOCASE OR FinancialYear='')
              AND (ExpiresUtc='' OR ExpiresUtc>$Now)
            ORDER BY CASE WHEN FinancialYear=$Fy COLLATE NOCASE THEN 0 ELSE 1 END
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$User", userName.Trim());
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", (financialYear ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        var value = command.ExecuteScalar();
        return value is not null && value != DBNull.Value && Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
    }

    public IReadOnlyList<UserClientAccessRecord> GetClientAccess(string windowsUserName)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id,WindowsUserName,ClientId,FinancialYear,IsAllowed,UpdatedUtc,ExpiresUtc
            FROM UserClientAccess WHERE WindowsUserName=$User COLLATE NOCASE
            ORDER BY ClientId,FinancialYear;
            """;
        command.Parameters.AddWithValue("$User", windowsUserName.Trim());
        using var reader = command.ExecuteReader();
        var result = new List<UserClientAccessRecord>();
        while (reader.Read()) result.Add(new UserClientAccessRecord
        {
            Id = reader.GetInt64(0), WindowsUserName = reader.GetString(1), ClientId = reader.GetInt64(2), FinancialYear = reader.GetString(3),
            IsAllowed = reader.GetInt32(4) == 1, UpdatedUtc = ParseDate(reader.GetString(5)),
            ExpiresUtc = string.IsNullOrWhiteSpace(reader.GetString(6)) ? null : ParseNullableDate(reader.GetString(6))
        });
        return result;
    }

    public void SetUserClientAccess(string userName, long clientId, string financialYear, bool isAllowed, DateTime? expiresUtc = null)
    {
        if (string.IsNullOrWhiteSpace(userName)) throw new ArgumentException("User ID is required.", nameof(userName));
        if (clientId <= 0) throw new ArgumentOutOfRangeException(nameof(clientId));
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO UserClientAccess(WindowsUserName,ClientId,FinancialYear,IsAllowed,UpdatedUtc,ExpiresUtc)
            VALUES($User,$ClientId,$Fy,$Allowed,$Utc,$ExpiresUtc)
            ON CONFLICT(WindowsUserName,ClientId,FinancialYear) DO UPDATE SET
                IsAllowed=excluded.IsAllowed,
                UpdatedUtc=excluded.UpdatedUtc,
                ExpiresUtc=excluded.ExpiresUtc;
            """;
        command.Parameters.AddWithValue("$User", userName.Trim());
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", (financialYear ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$Allowed", isAllowed ? 1 : 0);
        command.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$ExpiresUtc", isAllowed && expiresUtc.HasValue ? expiresUtc.Value.ToUniversalTime().ToString("O") : string.Empty);
        command.ExecuteNonQuery();
    }

    public DateTime? GetUserClientAccessExpiry(string userName, long clientId, string financialYear)
    {
        if (string.IsNullOrWhiteSpace(userName) || clientId <= 0) return null;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ExpiresUtc FROM UserClientAccess
            WHERE WindowsUserName=$User COLLATE NOCASE
              AND ClientId=$ClientId
              AND (FinancialYear=$Fy COLLATE NOCASE OR FinancialYear='')
            ORDER BY CASE WHEN FinancialYear=$Fy COLLATE NOCASE THEN 0 ELSE 1 END
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$User", userName.Trim());
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", (financialYear ?? string.Empty).Trim());
        var value = Convert.ToString(command.ExecuteScalar());
        return string.IsNullOrWhiteSpace(value) ? null : ParseNullableDate(value);
    }

    public void CleanupExpiredUserClientAccess()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE UserClientAccess
            SET IsAllowed=0, UpdatedUtc=$Now, ExpiresUtc=''
            WHERE IsAllowed=1 AND ExpiresUtc<>'' AND ExpiresUtc<=$Now;
            """;
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public bool IsClientLanAvailable(long clientId)
    {
        if (clientId <= 0) return false;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT IsEnabled FROM ClientLanAvailability WHERE ClientId=$ClientId LIMIT 1;";
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        var value = command.ExecuteScalar();
        return value is not null && value != DBNull.Value && Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
    }

    public void SetClientLanAvailable(long clientId, bool isEnabled)
    {
        if (clientId <= 0) throw new ArgumentOutOfRangeException(nameof(clientId));
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ClientLanAvailability(ClientId,IsEnabled,UpdatedUtc)
            VALUES($ClientId,$Enabled,$Utc)
            ON CONFLICT(ClientId) DO UPDATE SET IsEnabled=excluded.IsEnabled,UpdatedUtc=excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Enabled", isEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public bool HasDeviceClientAccess(string deviceId, long clientId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || clientId <= 0) return false;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT IsAllowed FROM DeviceClientAccess
            WHERE DeviceId=$DeviceId COLLATE NOCASE AND ClientId=$ClientId
              AND (ExpiresUtc='' OR ExpiresUtc>$Now)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$DeviceId", deviceId.Trim());
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        var value = command.ExecuteScalar();
        return value is not null && value != DBNull.Value && Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
    }

    public void SetDeviceClientAccess(string deviceId, long clientId, bool isAllowed, DateTime? expiresUtc = null)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("Device ID is required.", nameof(deviceId));
        if (clientId <= 0) throw new ArgumentOutOfRangeException(nameof(clientId));
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        if (isAllowed)
        {
            command.CommandText =
                """
                INSERT INTO DeviceClientAccess(DeviceId,ClientId,IsAllowed,UpdatedUtc,ExpiresUtc)
                VALUES($DeviceId,$ClientId,1,$Utc,$Expires)
                ON CONFLICT(DeviceId,ClientId) DO UPDATE SET IsAllowed=1,UpdatedUtc=excluded.UpdatedUtc,ExpiresUtc=excluded.ExpiresUtc;
                """;
            command.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$Expires", expiresUtc?.ToUniversalTime().ToString("O") ?? string.Empty);
        }
        else
        {
            command.CommandText = "DELETE FROM DeviceClientAccess WHERE DeviceId=$DeviceId COLLATE NOCASE AND ClientId=$ClientId;";
        }
        command.Parameters.AddWithValue("$DeviceId", deviceId.Trim());
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.ExecuteNonQuery();
    }

    public DateTime? GetDeviceClientAccessExpiry(string deviceId,long clientId)
    {
        if(string.IsNullOrWhiteSpace(deviceId)||clientId<=0)return null;using var connection=OpenConnection();using var command=connection.CreateCommand();command.CommandText="SELECT ExpiresUtc FROM DeviceClientAccess WHERE DeviceId=$DeviceId COLLATE NOCASE AND ClientId=$ClientId LIMIT 1;";command.Parameters.AddWithValue("$DeviceId",deviceId.Trim());command.Parameters.AddWithValue("$ClientId",clientId);var value=Convert.ToString(command.ExecuteScalar());return string.IsNullOrWhiteSpace(value)?null:ParseNullableDate(value);
    }

    public void CleanupExpiredDeviceClientAccess()
    {
        using var connection=OpenConnection();using var command=connection.CreateCommand();command.CommandText="DELETE FROM DeviceClientAccess WHERE ExpiresUtc<>'' AND ExpiresUtc<=$Now;";command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));command.ExecuteNonQuery();
    }

    public void AllowClientAccess(string windowsUserName, long clientId, string financialYear)
        => SetUserClientAccess(windowsUserName, clientId, financialYear, true);

    public void ClearClientAccess(string windowsUserName)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM UserClientAccess WHERE WindowsUserName=$User COLLATE NOCASE;";
        command.Parameters.AddWithValue("$User", windowsUserName.Trim());
        command.ExecuteNonQuery();
    }

    public LanDeviceRecord RegisterLanDevice(string deviceName, string tokenHash, long clientId, string financialYear, string roleName = GovernanceRoles.ReadOnly)
    {
        var now = DateTime.UtcNow;
        var record = new LanDeviceRecord
        {
            DeviceId = Guid.NewGuid().ToString("N"), DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "LAN Device" : deviceName.Trim(),
            TokenHash = tokenHash.Trim(), ClientId = clientId, FinancialYear = financialYear.Trim(), RoleName = roleName,
            IsActive = true, CreatedUtc = now, UpdatedUtc = now
        };
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO LanDevices(DeviceId,DeviceName,TokenHash,RoleName,ClientId,FinancialYear,IsActive,CreatedUtc,UpdatedUtc,LastSeenUtc)
            VALUES($DeviceId,$DeviceName,$TokenHash,$Role,$ClientId,$Fy,1,$Created,$Updated,'');
            """;
        command.Parameters.AddWithValue("$DeviceId", record.DeviceId);
        command.Parameters.AddWithValue("$DeviceName", record.DeviceName);
        command.Parameters.AddWithValue("$TokenHash", record.TokenHash);
        command.Parameters.AddWithValue("$Role", record.RoleName);
        command.Parameters.AddWithValue("$ClientId", record.ClientId);
        command.Parameters.AddWithValue("$Fy", record.FinancialYear);
        command.Parameters.AddWithValue("$Created", record.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$Updated", record.UpdatedUtc.ToString("O"));
        command.ExecuteNonQuery();
        using var idCommand = connection.CreateCommand();
        idCommand.CommandText = "SELECT last_insert_rowid();";
        record.Id = Convert.ToInt64(idCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        return record;
    }

    public LanDeviceRecord? FindLanDeviceByTokenHash(string tokenHash)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id,DeviceId,DeviceName,TokenHash,RoleName,ClientId,FinancialYear,IsActive,CreatedUtc,UpdatedUtc,LastSeenUtc,DeviceType,UserName,AppVersion,EnrollmentState,LastValidatedUtc,ExpiredUtc
            FROM LanDevices WHERE TokenHash=$Hash COLLATE NOCASE LIMIT 1;
            """;
        command.Parameters.AddWithValue("$Hash", tokenHash.Trim());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadLanDevice(reader) : null;
    }

    public IReadOnlyList<LanDeviceRecord> GetLanDevices()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id,DeviceId,DeviceName,TokenHash,RoleName,ClientId,FinancialYear,IsActive,CreatedUtc,UpdatedUtc,LastSeenUtc,DeviceType,UserName,AppVersion,EnrollmentState,LastValidatedUtc,ExpiredUtc
            FROM LanDevices ORDER BY UpdatedUtc DESC;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<LanDeviceRecord>();
        while (reader.Read()) result.Add(ReadLanDevice(reader));
        return result;
    }

    public void TouchLanDevice(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE LanDevices SET LastSeenUtc=$Utc,UpdatedUtc=$Utc WHERE Id=$Id;";
        command.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$Id", id);
        command.ExecuteNonQuery();
    }

    public void SetLanDeviceActive(long id, bool isActive)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE LanDevices SET IsActive=$Active,UpdatedUtc=$Utc WHERE Id=$Id;";
        command.Parameters.AddWithValue("$Active", isActive ? 1 : 0);
        command.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$Id", id);
        command.ExecuteNonQuery();
    }

    private ApplicationUserRecord? GetUserByWindowsName(string windowsUserName)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,WindowsUserName,DisplayName,RoleName,IsActive,CreatedUtc,UpdatedUtc,LastSeenUtc,MobileVerificationIntervalDays FROM AppUsers WHERE WindowsUserName=$User COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("$User", windowsUserName.Trim());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
    }

    private static LanDeviceRecord ReadLanDevice(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0), DeviceId = reader.GetString(1), DeviceName = reader.GetString(2), TokenHash = reader.GetString(3),
        RoleName = reader.GetString(4), ClientId = reader.GetInt64(5), FinancialYear = reader.GetString(6), IsActive = reader.GetInt32(7) == 1,
        CreatedUtc = ParseDate(reader.GetString(8)), UpdatedUtc = ParseDate(reader.GetString(9)), LastSeenUtc = ParseNullableDate(reader.GetString(10)),
        DeviceType = reader.FieldCount > 11 ? reader.GetString(11) : ServerDeviceTypes.Windows, UserName = reader.FieldCount > 12 ? reader.GetString(12) : string.Empty,
        AppVersion = reader.FieldCount > 13 ? reader.GetString(13) : string.Empty, EnrollmentState = reader.FieldCount > 14 ? reader.GetString(14) : ServerEnrollmentStates.Active,
        LastValidatedUtc = reader.FieldCount > 15 ? ParseNullableDate(reader.GetString(15)) : null, ExpiredUtc = reader.FieldCount > 16 ? ParseNullableDate(reader.GetString(16)) : null
    };

    public ImportRunRecord CreateRun(long clientId, string financialYear, string sourceKind, int totalFiles)
    {
        var run = new ImportRunRecord
        {
            RunId = Guid.NewGuid().ToString("N"),
            ClientId = clientId,
            FinancialYear = financialYear.Trim(),
            SourceKind = sourceKind.Trim(),
            RequestedBy = Environment.UserName,
            Status = ImportJobStates.Detected,
            TotalFiles = Math.Max(0, totalFiles),
            StartedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ImportRuns(RunId,ClientId,FinancialYear,SourceKind,RequestedBy,Status,TotalFiles,ImportedFiles,DuplicateFiles,ReviewFiles,FailedFiles,RecordCount,IsUndoable,StartedUtc,UpdatedUtc,CompletedUtc,Summary)
            VALUES($RunId,$ClientId,$Fy,$Source,$User,$Status,$Total,0,0,0,0,0,0,$Started,$Updated,'','');
            """;
        command.Parameters.AddWithValue("$RunId", run.RunId);
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", run.FinancialYear);
        command.Parameters.AddWithValue("$Source", run.SourceKind);
        command.Parameters.AddWithValue("$User", run.RequestedBy);
        command.Parameters.AddWithValue("$Status", run.Status);
        command.Parameters.AddWithValue("$Total", run.TotalFiles);
        command.Parameters.AddWithValue("$Started", run.StartedUtc.ToString("O"));
        command.Parameters.AddWithValue("$Updated", run.UpdatedUtc.ToString("O"));
        command.ExecuteNonQuery();
        return run;
    }

    public ImportJobRecord CreateJob(ImportJobRecord job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.JobId.Length == 0) job.JobId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        if (job.CreatedUtc == default) job.CreatedUtc = now;
        job.UpdatedUtc = now;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ImportJobs(JobId,RunId,FolderId,ClientId,FinancialYear,OriginalPath,WorkingPath,VaultPath,FileName,FileHash,FileSize,DetectedModule,Confidence,State,Detail,RecordCount,AttemptCount,IsUndoable,UndoStatus,CreatedUtc,UpdatedUtc)
            VALUES($JobId,$RunId,$FolderId,$ClientId,$Fy,$Original,$Working,$Vault,$FileName,$Hash,$Size,$Module,$Confidence,$State,$Detail,$Records,$Attempts,$Undoable,$UndoStatus,$Created,$Updated);
            """;
        AddJobParameters(command, job);
        command.ExecuteNonQuery();
        RefreshRun(job.RunId, connection);
        return job;
    }

    public ImportJobRecord? FindJobByWorkingPath(long clientId, string financialYear, string workingPath)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT JobId,RunId,FolderId,ClientId,FinancialYear,OriginalPath,WorkingPath,VaultPath,FileName,FileHash,FileSize,DetectedModule,Confidence,State,Detail,RecordCount,AttemptCount,IsUndoable,UndoStatus,CreatedUtc,UpdatedUtc
            FROM ImportJobs WHERE ClientId=$ClientId AND FinancialYear=$Fy AND WorkingPath=$Path COLLATE NOCASE ORDER BY UpdatedUtc DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        command.Parameters.AddWithValue("$Path", NormalizePath(workingPath));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    public ImportJobRecord EnsureAutomationJob(long clientId, string financialYear, string folderId, string path, string hash, string fileType)
    {
        var existing = FindJobByWorkingPath(clientId, financialYear, path);
        if (existing is not null) return existing;
        var run = CreateRun(clientId, financialYear, folderId.StartsWith("SYSTEM-DUMP-", StringComparison.OrdinalIgnoreCase) ? "Universal Dump" : "Auto Import", 1);
        return CreateJob(new ImportJobRecord
        {
            RunId = run.RunId,
            FolderId = folderId,
            ClientId = clientId,
            FinancialYear = financialYear,
            OriginalPath = path,
            WorkingPath = NormalizePath(path),
            FileName = Path.GetFileName(path),
            FileHash = hash,
            FileSize = File.Exists(path) ? new FileInfo(path).Length : 0,
            DetectedModule = fileType,
            State = ImportJobStates.Detected,
            Detail = "Observed by Auto Import.",
            CreatedUtc = DateTime.UtcNow
        });
    }

    public void UpdateJobFromAutomation(long clientId, string financialYear, string folderId, string path, string hash, string fileType, string status, string detail, int recordCount)
    {
        var job = FindJobByWorkingPath(clientId, financialYear, path)
                  ?? EnsureAutomationJob(clientId, financialYear, folderId, path, hash, fileType);
        var state = MapAutomationState(status, fileType);
        var undoable = state == ImportJobStates.Verified && IsUndoableModule(fileType);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE ImportJobs SET FileHash=CASE WHEN $Hash='' THEN FileHash ELSE $Hash END,
                    DetectedModule=CASE WHEN $Type='' THEN DetectedModule ELSE $Type END,
                    State=CASE WHEN State='Verified' AND $State='Duplicate' THEN State ELSE $State END,
                    Detail=CASE WHEN State='Verified' AND $State='Duplicate' THEN Detail ELSE $Detail END,
                    RecordCount=CASE WHEN State='Verified' AND $State='Duplicate' THEN RecordCount ELSE $Records END,
                    AttemptCount=AttemptCount+1,
                    IsUndoable=CASE WHEN State='Verified' AND $State='Duplicate' THEN IsUndoable ELSE $Undoable END,
                    UpdatedUtc=$Now WHERE JobId=$JobId;
                """;
            update.Parameters.AddWithValue("$Hash", hash);
            update.Parameters.AddWithValue("$Type", fileType);
            update.Parameters.AddWithValue("$State", state);
            update.Parameters.AddWithValue("$Detail", detail ?? string.Empty);
            update.Parameters.AddWithValue("$Records", Math.Max(0, recordCount));
            update.Parameters.AddWithValue("$Undoable", undoable ? 1 : 0);
            update.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$JobId", job.JobId);
            update.ExecuteNonQuery();
        }
        using (var attempt = connection.CreateCommand())
        {
            attempt.Transaction = transaction;
            attempt.CommandText =
                """
                INSERT INTO ImportJobAttempts(JobId,AttemptNo,State,Detail,AttemptUtc)
                SELECT JobId,AttemptCount,State,Detail,$Now FROM ImportJobs WHERE JobId=$JobId;
                """;
            attempt.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
            attempt.Parameters.AddWithValue("$JobId", job.JobId);
            attempt.ExecuteNonQuery();
        }
        transaction.Commit();
        try
        {
            var sourcePath = string.IsNullOrWhiteSpace(job.VaultPath) ? path : job.VaultPath;
            AddDocument(new DocumentVaultRecord
            {
                ClientId = clientId,
                FinancialYear = financialYear,
                RunId = job.RunId,
                JobId = job.JobId,
                Module = string.IsNullOrWhiteSpace(fileType) ? job.DetectedModule : fileType,
                FileName = Path.GetFileName(sourcePath),
                OriginalPath = string.IsNullOrWhiteSpace(job.OriginalPath) ? path : job.OriginalPath,
                VaultPath = sourcePath,
                FileHash = hash,
                FileSize = File.Exists(sourcePath) ? new FileInfo(sourcePath).Length : 0,
                MimeHint = Path.GetExtension(sourcePath),
                AddedUtc = DateTime.UtcNow
            });
        }
        catch { /* Document evidence registration must never invalidate a completed accounting import. */ }
        if (state == ImportJobStates.Verified)
        {
            try { CaptureImportJobEntities(job.JobId, job.RunId, clientId, financialYear, fileType, hash, path, job.OriginalPath); }
            catch { /* Undo evidence capture is best-effort; an uncaptured job is not marked undoable. */ }
        }
        RefreshRun(job.RunId);
    }

    public void SetJobClassification(string jobId, string module, double confidence, string detail)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ImportJobs SET DetectedModule=$Module,Confidence=$Confidence,State=$State,Detail=$Detail,UpdatedUtc=$Now WHERE JobId=$JobId;";
        command.Parameters.AddWithValue("$Module", module.Trim());
        command.Parameters.AddWithValue("$Confidence", Math.Clamp(confidence, 0d, 1d));
        command.Parameters.AddWithValue("$State", confidence >= 0.80 ? ImportJobStates.Ready : ImportJobStates.ReviewRequired);
        command.Parameters.AddWithValue("$Detail", detail ?? string.Empty);
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$JobId", jobId);
        command.ExecuteNonQuery();
        using var documentUpdate = connection.CreateCommand();
        documentUpdate.CommandText = "UPDATE DocumentVault SET Module=$Module WHERE JobId=$JobId;";
        documentUpdate.Parameters.AddWithValue("$Module", module.Trim());
        documentUpdate.Parameters.AddWithValue("$JobId", jobId);
        documentUpdate.ExecuteNonQuery();
    }

    public IReadOnlyList<ImportRunRecord> GetRuns(long clientId, string financialYear, int limit = 250)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RunId,ClientId,FinancialYear,SourceKind,RequestedBy,Status,TotalFiles,ImportedFiles,DuplicateFiles,ReviewFiles,FailedFiles,RecordCount,IsUndoable,StartedUtc,UpdatedUtc,CompletedUtc,Summary
            FROM ImportRuns WHERE ClientId=$ClientId AND FinancialYear=$Fy ORDER BY StartedUtc DESC LIMIT $Limit;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        command.Parameters.AddWithValue("$Limit", Math.Clamp(limit, 1, 5000));
        using var reader = command.ExecuteReader();
        var result = new List<ImportRunRecord>();
        while (reader.Read()) result.Add(ReadRun(reader));
        return result;
    }

    public IReadOnlyList<ImportJobRecord> GetJobs(long clientId, string financialYear, int limit = 1000)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT JobId,RunId,FolderId,ClientId,FinancialYear,OriginalPath,WorkingPath,VaultPath,FileName,FileHash,FileSize,DetectedModule,Confidence,State,Detail,RecordCount,AttemptCount,IsUndoable,UndoStatus,CreatedUtc,UpdatedUtc
            FROM ImportJobs WHERE ClientId=$ClientId AND FinancialYear=$Fy ORDER BY UpdatedUtc DESC LIMIT $Limit;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        command.Parameters.AddWithValue("$Limit", Math.Clamp(limit, 1, 10000));
        using var reader = command.ExecuteReader();
        var result = new List<ImportJobRecord>();
        while (reader.Read()) result.Add(ReadJob(reader));
        return result;
    }

    public void AddDocument(DocumentVaultRecord document)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO DocumentVault(ClientId,FinancialYear,RunId,JobId,Module,FileName,OriginalPath,VaultPath,FileHash,FileSize,MimeHint,AddedUtc)
            VALUES($ClientId,$Fy,$RunId,$JobId,$Module,$FileName,$Original,$Vault,$Hash,$Size,$Mime,$Added);
            """;
        command.Parameters.AddWithValue("$ClientId", document.ClientId);
        command.Parameters.AddWithValue("$Fy", document.FinancialYear.Trim());
        command.Parameters.AddWithValue("$RunId", document.RunId);
        command.Parameters.AddWithValue("$JobId", document.JobId);
        command.Parameters.AddWithValue("$Module", document.Module);
        command.Parameters.AddWithValue("$FileName", document.FileName);
        command.Parameters.AddWithValue("$Original", document.OriginalPath);
        command.Parameters.AddWithValue("$Vault", document.VaultPath);
        command.Parameters.AddWithValue("$Hash", document.FileHash);
        command.Parameters.AddWithValue("$Size", document.FileSize);
        command.Parameters.AddWithValue("$Mime", document.MimeHint);
        command.Parameters.AddWithValue("$Added", (document.AddedUtc == default ? DateTime.UtcNow : document.AddedUtc).ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<DocumentVaultRecord> GetDocuments(long clientId, string financialYear, int limit = 1000)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,ClientId,FinancialYear,RunId,JobId,Module,FileName,OriginalPath,VaultPath,FileHash,FileSize,MimeHint,AddedUtc
            FROM DocumentVault WHERE ClientId=$ClientId AND FinancialYear=$Fy ORDER BY AddedUtc DESC LIMIT $Limit;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        command.Parameters.AddWithValue("$Limit", Math.Clamp(limit, 1, 5000));
        using var reader = command.ExecuteReader();
        var result = new List<DocumentVaultRecord>();
        while (reader.Read())
        {
            var path = reader.GetString(8);
            result.Add(new DocumentVaultRecord
            {
                Id=reader.GetInt64(0),ClientId=reader.GetInt64(1),FinancialYear=reader.GetString(2),RunId=reader.GetString(3),JobId=reader.GetString(4),Module=reader.GetString(5),FileName=reader.GetString(6),OriginalPath=reader.GetString(7),VaultPath=path,FileHash=reader.GetString(9),FileSize=reader.GetInt64(10),MimeHint=reader.GetString(11),AddedUtc=ParseDate(reader.GetString(12)),IsAvailable=File.Exists(path)
            });
        }
        return result;
    }

    public void RecordVersion(EntityVersionRecord version)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO EntityVersions(ClientId,FinancialYear,Module,EntityType,EntityKey,Operation,BeforeJson,AfterJson,ChangedBy,RunId,Reason,ChangedUtc)
            VALUES($ClientId,$Fy,$Module,$Type,$Key,$Operation,$Before,$After,$User,$RunId,$Reason,$Utc);
            """;
        command.Parameters.AddWithValue("$ClientId", version.ClientId);
        command.Parameters.AddWithValue("$Fy", version.FinancialYear.Trim());
        command.Parameters.AddWithValue("$Module", version.Module);
        command.Parameters.AddWithValue("$Type", version.EntityType);
        command.Parameters.AddWithValue("$Key", version.EntityKey);
        command.Parameters.AddWithValue("$Operation", version.Operation);
        command.Parameters.AddWithValue("$Before", version.BeforeJson ?? string.Empty);
        command.Parameters.AddWithValue("$After", version.AfterJson ?? string.Empty);
        command.Parameters.AddWithValue("$User", string.IsNullOrWhiteSpace(version.ChangedBy) ? Environment.UserName : version.ChangedBy);
        command.Parameters.AddWithValue("$RunId", version.RunId ?? string.Empty);
        command.Parameters.AddWithValue("$Reason", version.Reason ?? string.Empty);
        command.Parameters.AddWithValue("$Utc", (version.ChangedUtc == default ? DateTime.UtcNow : version.ChangedUtc).ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<EntityVersionRecord> GetVersions(long clientId, string financialYear, int limit = 1000)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,ClientId,FinancialYear,Module,EntityType,EntityKey,Operation,BeforeJson,AfterJson,ChangedBy,RunId,Reason,ChangedUtc
            FROM EntityVersions WHERE ClientId=$ClientId AND FinancialYear=$Fy ORDER BY ChangedUtc DESC,Id DESC LIMIT $Limit;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        command.Parameters.AddWithValue("$Limit", Math.Clamp(limit, 1, 10000));
        using var reader = command.ExecuteReader();
        var result = new List<EntityVersionRecord>();
        while (reader.Read()) result.Add(new EntityVersionRecord
        {
            Id=reader.GetInt64(0),ClientId=reader.GetInt64(1),FinancialYear=reader.GetString(2),Module=reader.GetString(3),EntityType=reader.GetString(4),EntityKey=reader.GetString(5),Operation=reader.GetString(6),BeforeJson=reader.GetString(7),AfterJson=reader.GetString(8),ChangedBy=reader.GetString(9),RunId=reader.GetString(10),Reason=reader.GetString(11),ChangedUtc=ParseDate(reader.GetString(12))
        });
        return result;
    }

    public void SaveBackupRecord(VerifiedBackupRecord backup)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO VerifiedBackups(BackupPath,Sha256,FileSize,IntegrityResult,ScheduleClass,CreatedUtc,VerifiedUtc,IsVerified)
            VALUES($Path,$Hash,$Size,$Integrity,$Class,$Created,$Verified,$IsVerified)
            ON CONFLICT(BackupPath) DO UPDATE SET Sha256=excluded.Sha256,FileSize=excluded.FileSize,IntegrityResult=excluded.IntegrityResult,ScheduleClass=excluded.ScheduleClass,VerifiedUtc=excluded.VerifiedUtc,IsVerified=excluded.IsVerified;
            """;
        command.Parameters.AddWithValue("$Path", backup.BackupPath);
        command.Parameters.AddWithValue("$Hash", backup.Sha256);
        command.Parameters.AddWithValue("$Size", backup.FileSize);
        command.Parameters.AddWithValue("$Integrity", backup.IntegrityResult);
        command.Parameters.AddWithValue("$Class", backup.ScheduleClass);
        command.Parameters.AddWithValue("$Created", backup.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$Verified", backup.VerifiedUtc?.ToString("O") ?? string.Empty);
        command.Parameters.AddWithValue("$IsVerified", backup.IsVerified ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public void DeleteBackupRecordsByPath(IEnumerable<string> backupPaths)
    {
        var paths = backupPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0) return;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var path in paths)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM VerifiedBackups WHERE BackupPath = $Path COLLATE NOCASE;";
            command.Parameters.AddWithValue("$Path", path);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<VerifiedBackupRecord> GetBackups(int limit = 100)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,BackupPath,Sha256,FileSize,IntegrityResult,ScheduleClass,CreatedUtc,VerifiedUtc,IsVerified FROM VerifiedBackups ORDER BY CreatedUtc DESC LIMIT $Limit;";
        command.Parameters.AddWithValue("$Limit", Math.Clamp(limit, 1, 1000));
        using var reader = command.ExecuteReader();
        var result = new List<VerifiedBackupRecord>();
        while (reader.Read()) result.Add(new VerifiedBackupRecord
        {
            Id=reader.GetInt64(0),BackupPath=reader.GetString(1),Sha256=reader.GetString(2),FileSize=reader.GetInt64(3),IntegrityResult=reader.GetString(4),ScheduleClass=reader.GetString(5),CreatedUtc=ParseDate(reader.GetString(6)),VerifiedUtc=ParseNullableDate(reader.GetString(7)),IsVerified=reader.GetInt32(8)==1
        });
        return result;
    }

    public void SaveRecurringProfile(RecurringTransactionProfile profile)
    {
        var now = DateTime.UtcNow;
        if (profile.Id <= 0)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO RecurringTransactionProfiles(ClientId,FinancialYear,Module,ProfileName,Frequency,IntervalCount,StartDate,EndDate,NextRunDate,AutoCreateAsDraft,TemplateJson,IsActive,CreatedBy,CreatedUtc,UpdatedUtc)
                VALUES($ClientId,$Fy,$Module,$Name,$Frequency,$Interval,$Start,$End,$Next,$Draft,$Template,$Active,$User,$Now,$Now);
                SELECT last_insert_rowid();
                """;
            AddRecurringParameters(command, profile, now);
            profile.Id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            return;
        }
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                UPDATE RecurringTransactionProfiles SET ProfileName=$Name,Frequency=$Frequency,IntervalCount=$Interval,StartDate=$Start,EndDate=$End,NextRunDate=$Next,AutoCreateAsDraft=$Draft,TemplateJson=$Template,IsActive=$Active,UpdatedUtc=$Now WHERE Id=$Id;
                """;
            AddRecurringParameters(command, profile, now);
            command.Parameters.AddWithValue("$Id", profile.Id);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<RecurringTransactionProfile> GetRecurringProfiles(long clientId,string financialYear,string module)
    {
        using var connection=OpenConnection();using var command=connection.CreateCommand();
        command.CommandText="SELECT Id,ClientId,FinancialYear,Module,ProfileName,Frequency,IntervalCount,StartDate,EndDate,NextRunDate,AutoCreateAsDraft,TemplateJson,IsActive,CreatedBy,CreatedUtc,UpdatedUtc FROM RecurringTransactionProfiles WHERE ClientId=$ClientId AND FinancialYear=$Fy AND Module=$Module COLLATE NOCASE ORDER BY IsActive DESC,NextRunDate,ProfileName COLLATE NOCASE;";
        command.Parameters.AddWithValue("$ClientId",clientId);command.Parameters.AddWithValue("$Fy",financialYear.Trim());command.Parameters.AddWithValue("$Module",module.Trim());
        using var reader=command.ExecuteReader();var result=new List<RecurringTransactionProfile>();while(reader.Read()) result.Add(ReadRecurring(reader));return result;
    }

    public IReadOnlyList<RecurringTransactionProfile> GetDueRecurringProfiles(DateTime throughDate)
    {
        using var connection=OpenConnection();using var command=connection.CreateCommand();
        command.CommandText="SELECT Id,ClientId,FinancialYear,Module,ProfileName,Frequency,IntervalCount,StartDate,EndDate,NextRunDate,AutoCreateAsDraft,TemplateJson,IsActive,CreatedBy,CreatedUtc,UpdatedUtc FROM RecurringTransactionProfiles WHERE IsActive=1 AND NextRunDate<=$Through AND (EndDate='' OR EndDate>=$Through) ORDER BY NextRunDate,Id;";
        command.Parameters.AddWithValue("$Through",throughDate.Date.ToString("yyyy-MM-dd"));using var reader=command.ExecuteReader();var result=new List<RecurringTransactionProfile>();while(reader.Read()) result.Add(ReadRecurring(reader));return result;
    }

    public void AdvanceRecurringProfile(long profileId, DateTime nextRunDate)
    {
        using var connection=OpenConnection();using var command=connection.CreateCommand();command.CommandText="UPDATE RecurringTransactionProfiles SET NextRunDate=$Next,UpdatedUtc=$Now WHERE Id=$Id;";command.Parameters.AddWithValue("$Next",nextRunDate.Date.ToString("yyyy-MM-dd"));command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$Id",profileId);command.ExecuteNonQuery();
    }

    public void AddRelationship(TransactionRelationship relation)
    {
        using var connection=OpenConnection();using var command=connection.CreateCommand();command.CommandText="INSERT INTO TransactionRelationships(ClientId,FinancialYear,Module,SourceEntityKey,TargetEntityKey,RelationshipType,ProfileId,CreatedUtc) VALUES($ClientId,$Fy,$Module,$Source,$Target,$Type,$Profile,$Utc);";command.Parameters.AddWithValue("$ClientId",relation.ClientId);command.Parameters.AddWithValue("$Fy",relation.FinancialYear.Trim());command.Parameters.AddWithValue("$Module",relation.Module);command.Parameters.AddWithValue("$Source",relation.SourceEntityKey);command.Parameters.AddWithValue("$Target",relation.TargetEntityKey);command.Parameters.AddWithValue("$Type",relation.RelationshipType);command.Parameters.AddWithValue("$Profile",relation.ProfileId);command.Parameters.AddWithValue("$Utc",(relation.CreatedUtc==default?DateTime.UtcNow:relation.CreatedUtc).ToString("O"));command.ExecuteNonQuery();
    }

    public void RecordLedgerSuggestionDecision(long clientId,string financialYear,string summaryKey,string ledgerName,string ledgerGroup,bool accepted,string reason)
    {
        using var connection=OpenConnection();using var command=connection.CreateCommand();command.CommandText=
            """
            INSERT INTO LedgerSuggestionLearning(ClientId,FinancialYear,SummaryKey,LedgerName,LedgerGroup,AcceptCount,RejectCount,LastReason,UpdatedUtc)
            VALUES($ClientId,$Fy,$Summary,$Ledger,$Group,$Accept,$Reject,$Reason,$Now)
            ON CONFLICT(ClientId,FinancialYear,SummaryKey,LedgerName) DO UPDATE SET
              LedgerGroup=excluded.LedgerGroup,AcceptCount=LedgerSuggestionLearning.AcceptCount+excluded.AcceptCount,
              RejectCount=LedgerSuggestionLearning.RejectCount+excluded.RejectCount,LastReason=excluded.LastReason,UpdatedUtc=excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$ClientId",clientId);command.Parameters.AddWithValue("$Fy",financialYear.Trim());command.Parameters.AddWithValue("$Summary",summaryKey.Trim());command.Parameters.AddWithValue("$Ledger",ledgerName.Trim());command.Parameters.AddWithValue("$Group",ledgerGroup.Trim());command.Parameters.AddWithValue("$Accept",accepted?1:0);command.Parameters.AddWithValue("$Reject",accepted?0:1);command.Parameters.AddWithValue("$Reason",reason??string.Empty);command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));command.ExecuteNonQuery();
    }

    public (string Ledger,string Group,int AcceptCount)? GetLearnedLedger(long clientId,string financialYear,string summaryKey)
    {
        using var connection=OpenConnection();using var command=connection.CreateCommand();command.CommandText="SELECT LedgerName,LedgerGroup,AcceptCount FROM LedgerSuggestionLearning WHERE ClientId=$ClientId AND FinancialYear=$Fy AND SummaryKey=$Summary COLLATE NOCASE AND AcceptCount>RejectCount ORDER BY AcceptCount DESC,UpdatedUtc DESC LIMIT 1;";command.Parameters.AddWithValue("$ClientId",clientId);command.Parameters.AddWithValue("$Fy",financialYear.Trim());command.Parameters.AddWithValue("$Summary",summaryKey.Trim());using var reader=command.ExecuteReader();return reader.Read()?(reader.GetString(0),reader.GetString(1),reader.GetInt32(2)):null;
    }

    public bool IsLedgerSuggestionRejected(long clientId,string financialYear,string summaryKey,string ledgerName)
    {
        if(clientId<=0||string.IsNullOrWhiteSpace(summaryKey)||string.IsNullOrWhiteSpace(ledgerName))return false;
        using var connection=OpenConnection();using var command=connection.CreateCommand();
        command.CommandText="SELECT AcceptCount,RejectCount FROM LedgerSuggestionLearning WHERE ClientId=$ClientId AND FinancialYear=$Fy AND SummaryKey=$Summary COLLATE NOCASE AND LedgerName=$Ledger COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("$ClientId",clientId);command.Parameters.AddWithValue("$Fy",financialYear.Trim());command.Parameters.AddWithValue("$Summary",summaryKey.Trim());command.Parameters.AddWithValue("$Ledger",ledgerName.Trim());
        using var reader=command.ExecuteReader();if(!reader.Read())return false;var accepted=reader.GetInt32(0);var rejected=reader.GetInt32(1);return rejected>0&&rejected>=accepted;
    }

    public void SaveAdvancedReconciliation(long clientId,string financialYear,AdvancedReconciliationPreview preview)
    {
        using var connection=OpenConnection();using var command=connection.CreateCommand();command.CommandText=
            """
            INSERT INTO AdvancedReconciliationMatches(MatchId,ClientId,FinancialYear,MatchType,BankTransactionIds,BookReferences,BankAmount,BookAmount,AdjustmentAmount,AdjustmentLedger,Status,Detail,CreatedBy,CreatedUtc,UpdatedUtc)
            VALUES($MatchId,$ClientId,$Fy,$Type,$BankIds,$Refs,$Bank,$Book,$Adjustment,$Ledger,$Status,$Detail,$User,$Now,$Now)
            ON CONFLICT(MatchId) DO UPDATE SET MatchType=excluded.MatchType,BankTransactionIds=excluded.BankTransactionIds,BookReferences=excluded.BookReferences,BankAmount=excluded.BankAmount,BookAmount=excluded.BookAmount,AdjustmentAmount=excluded.AdjustmentAmount,AdjustmentLedger=excluded.AdjustmentLedger,Status=excluded.Status,Detail=excluded.Detail,UpdatedUtc=excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$MatchId",preview.MatchId);command.Parameters.AddWithValue("$ClientId",clientId);command.Parameters.AddWithValue("$Fy",financialYear.Trim());command.Parameters.AddWithValue("$Type",preview.MatchType);command.Parameters.AddWithValue("$BankIds",string.Join(',',preview.BankTransactionIds));command.Parameters.AddWithValue("$Refs",string.Join('|',preview.BookReferences));command.Parameters.AddWithValue("$Bank",(double)preview.BankAmount);command.Parameters.AddWithValue("$Book",(double)preview.BookAmount);command.Parameters.AddWithValue("$Adjustment",(double)preview.AdjustmentAmount);command.Parameters.AddWithValue("$Ledger",preview.AdjustmentLedger);command.Parameters.AddWithValue("$Status",preview.Status);command.Parameters.AddWithValue("$Detail",preview.Detail);command.Parameters.AddWithValue("$User",Environment.UserName);command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));command.ExecuteNonQuery();
    }

    public IReadOnlyList<AdvancedReconciliationPreview> GetAdvancedReconciliations(long clientId,string financialYear,int limit=250)
    {
        using var connection=OpenConnection();using var command=connection.CreateCommand();command.CommandText="SELECT MatchId,MatchType,BankTransactionIds,BookReferences,BankAmount,BookAmount,AdjustmentAmount,AdjustmentLedger,Status,Detail FROM AdvancedReconciliationMatches WHERE ClientId=$ClientId AND FinancialYear=$Fy ORDER BY Id DESC LIMIT $Limit;";command.Parameters.AddWithValue("$ClientId",clientId);command.Parameters.AddWithValue("$Fy",financialYear.Trim());command.Parameters.AddWithValue("$Limit",Math.Clamp(limit,1,5000));using var reader=command.ExecuteReader();var result=new List<AdvancedReconciliationPreview>();while(reader.Read()){var ids=(reader.IsDBNull(2)?string.Empty:reader.GetString(2)).Split(',',StringSplitOptions.RemoveEmptyEntries).Select(x=>long.TryParse(x,out var id)?id:0).Where(x=>x>0).ToArray();var refs=(reader.IsDBNull(3)?string.Empty:reader.GetString(3)).Split('|',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);result.Add(new AdvancedReconciliationPreview{MatchId=reader.GetString(0),MatchType=reader.GetString(1),BankTransactionIds=ids,BookReferences=refs,BankAmount=Convert.ToDecimal(reader.GetDouble(4)),BookAmount=Convert.ToDecimal(reader.GetDouble(5)),AdjustmentAmount=Convert.ToDecimal(reader.GetDouble(6)),AdjustmentLedger=reader.GetString(7),Status=reader.GetString(8),Detail=reader.GetString(9)});}return result;
    }

    public void MarkRunRolledBack(string runId, string detail)
    {
        using var connection=OpenConnection();using var transaction=connection.BeginTransaction();
        using(var jobs=connection.CreateCommand()){jobs.Transaction=transaction;jobs.CommandText="UPDATE ImportJobs SET State=$State,UndoStatus=$Detail,IsUndoable=0,UpdatedUtc=$Now WHERE RunId=$RunId AND State=$Verified;";jobs.Parameters.AddWithValue("$State",ImportJobStates.RolledBack);jobs.Parameters.AddWithValue("$Detail",detail);jobs.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));jobs.Parameters.AddWithValue("$RunId",runId);jobs.Parameters.AddWithValue("$Verified",ImportJobStates.Verified);jobs.ExecuteNonQuery();}
        using(var run=connection.CreateCommand()){run.Transaction=transaction;run.CommandText="UPDATE ImportRuns SET Status=$Status,IsUndoable=0,CompletedUtc=$Now,UpdatedUtc=$Now,Summary=$Summary WHERE RunId=$RunId;";run.Parameters.AddWithValue("$Status",ImportJobStates.RolledBack);run.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));run.Parameters.AddWithValue("$Summary",detail);run.Parameters.AddWithValue("$RunId",runId);run.ExecuteNonQuery();}
        transaction.Commit();
    }

    private void RefreshRun(string runId)
    {
        using var connection = OpenConnection();
        RefreshRun(runId, connection);
    }

    private static void RefreshRun(string runId, SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE ImportRuns SET
                TotalFiles=(SELECT COUNT(*) FROM ImportJobs WHERE RunId=$RunId),
                ImportedFiles=(SELECT COUNT(*) FROM ImportJobs WHERE RunId=$RunId AND State IN ('Imported','Verified')),
                DuplicateFiles=(SELECT COUNT(*) FROM ImportJobs WHERE RunId=$RunId AND State='Duplicate'),
                ReviewFiles=(SELECT COUNT(*) FROM ImportJobs WHERE RunId=$RunId AND State='Review Required'),
                FailedFiles=(SELECT COUNT(*) FROM ImportJobs WHERE RunId=$RunId AND State='Failed'),
                RecordCount=(SELECT COALESCE(SUM(RecordCount),0) FROM ImportJobs WHERE RunId=$RunId AND State IN ('Imported','Verified')),
                IsUndoable=CASE WHEN EXISTS(SELECT 1 FROM ImportJobs WHERE RunId=$RunId AND IsUndoable=1) THEN 1 ELSE 0 END,
                Status=CASE
                    WHEN EXISTS(SELECT 1 FROM ImportJobs WHERE RunId=$RunId AND State='Failed') THEN 'Failed'
                    WHEN EXISTS(SELECT 1 FROM ImportJobs WHERE RunId=$RunId AND State='Review Required') THEN 'Review Required'
                    WHEN EXISTS(SELECT 1 FROM ImportJobs WHERE RunId=$RunId AND State NOT IN ('Verified','Duplicate','Failed','Ignored','Rolled Back')) THEN 'Importing'
                    WHEN EXISTS(SELECT 1 FROM ImportJobs WHERE RunId=$RunId AND State='Verified') THEN 'Verified'
                    WHEN EXISTS(SELECT 1 FROM ImportJobs WHERE RunId=$RunId AND State='Duplicate') THEN 'Duplicate'
                    ELSE Status END,
                CompletedUtc=CASE WHEN NOT EXISTS(SELECT 1 FROM ImportJobs WHERE RunId=$RunId AND State NOT IN ('Verified','Duplicate','Failed','Ignored','Rolled Back')) THEN $Now ELSE CompletedUtc END,
                UpdatedUtc=$Now,
                Summary=(SELECT printf('%d verified • %d duplicate • %d review • %d failed • %d records',
                    SUM(CASE WHEN State='Verified' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN State='Duplicate' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN State='Review Required' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN State='Failed' THEN 1 ELSE 0 END),
                    COALESCE(SUM(CASE WHEN State IN ('Imported','Verified') THEN RecordCount ELSE 0 END),0))
                    FROM ImportJobs WHERE RunId=$RunId)
            WHERE RunId=$RunId;
            """;
        command.Parameters.AddWithValue("$RunId",runId);command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));command.ExecuteNonQuery();
    }

    private void CaptureImportJobEntities(string jobId,string runId,long clientId,string financialYear,string fileType,string hash,string workingPath,string originalPath)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM ImportJobEntities WHERE JobId=$JobId;";
            clear.Parameters.AddWithValue("$JobId", jobId);
            clear.ExecuteNonQuery();
        }

        string entityType;
        string selectSql;
        if (fileType.StartsWith("Bank", StringComparison.OrdinalIgnoreCase)
            || fileType.Equals("MT940",StringComparison.OrdinalIgnoreCase)
            || fileType.Contains("OFX",StringComparison.OrdinalIgnoreCase)
            || fileType.Contains("QIF",StringComparison.OrdinalIgnoreCase)
            || fileType.Contains("camt",StringComparison.OrdinalIgnoreCase))
        {
            entityType = "BankStatement";
            selectSql = "SELECT Id FROM BankStatements WHERE ClientId=$ClientId AND FinancialYear=$Fy AND (($Hash<>'' AND FileHash=$Hash COLLATE NOCASE) OR ($Hash='' AND (SourcePath=$Working COLLATE NOCASE OR SourcePath=$Original COLLATE NOCASE)));";
        }
        else if (fileType.Equals("Sales",StringComparison.OrdinalIgnoreCase))
        {
            entityType = "SalesRecord";
            selectSql = "SELECT Id FROM SalesRecords WHERE ClientId=$ClientId AND FinancialYear=$Fy AND (($Hash<>'' AND SourceHash=$Hash COLLATE NOCASE) OR ($Hash='' AND (SourcePath=$Working COLLATE NOCASE OR SourcePath=$Original COLLATE NOCASE)));";
        }
        else if (fileType.Equals("Purchase",StringComparison.OrdinalIgnoreCase))
        {
            entityType = "PurchaseRecord";
            selectSql = "SELECT Id FROM PurchaseRecords WHERE ClientId=$ClientId AND FinancialYear=$Fy AND (Source2APath=$Working COLLATE NOCASE OR Source2APath=$Original COLLATE NOCASE OR Source2BPath=$Working COLLATE NOCASE OR Source2BPath=$Original COLLATE NOCASE);";
        }
        else if (fileType.Equals("Cash",StringComparison.OrdinalIgnoreCase))
        {
            entityType = "CashEntry";
            selectSql = "SELECT Id FROM CashEntries WHERE ClientId=$ClientId AND FinancialYear=$Fy AND SourceJobId=$JobId COLLATE NOCASE;";
        }
        else if (fileType.Equals("Journal",StringComparison.OrdinalIgnoreCase))
        {
            entityType = "JournalVoucher";
            selectSql = "SELECT Id FROM JournalVouchers WHERE ClientId=$ClientId AND FinancialYear=$Fy AND SourceJobId=$JobId COLLATE NOCASE;";
        }
        else
        {
            using var disable = connection.CreateCommand();
            disable.Transaction = transaction;
            disable.CommandText = "UPDATE ImportJobs SET IsUndoable=0 WHERE JobId=$JobId;";
            disable.Parameters.AddWithValue("$JobId", jobId);
            disable.ExecuteNonQuery();
            transaction.Commit();
            return;
        }

        var keys = new List<string>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = selectSql;
            select.Parameters.AddWithValue("$ClientId",clientId);
            select.Parameters.AddWithValue("$Fy",financialYear.Trim());
            select.Parameters.AddWithValue("$Hash",hash??string.Empty);
            select.Parameters.AddWithValue("$Working",NormalizePath(workingPath));
            select.Parameters.AddWithValue("$Original",NormalizePath(originalPath));
            select.Parameters.AddWithValue("$JobId",jobId);
            using var reader = select.ExecuteReader();
            while (reader.Read()) keys.Add(Convert.ToString(reader.GetValue(0),CultureInfo.InvariantCulture) ?? string.Empty);
        }
        foreach (var key in keys.Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO ImportJobEntities(JobId,EntityType,EntityKey,CapturedUtc) VALUES($JobId,$Type,$Key,$Now);";
            insert.Parameters.AddWithValue("$JobId",jobId);
            insert.Parameters.AddWithValue("$Type",entityType);
            insert.Parameters.AddWithValue("$Key",key);
            insert.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }
        using (var flag = connection.CreateCommand())
        {
            flag.Transaction = transaction;
            flag.CommandText = "UPDATE ImportJobs SET IsUndoable=$Undoable WHERE JobId=$JobId;";
            flag.Parameters.AddWithValue("$Undoable",keys.Count>0?1:0);
            flag.Parameters.AddWithValue("$JobId",jobId);
            flag.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static string MapAutomationState(string status,string fileType)
    {
        if(status.Equals("Processing",StringComparison.OrdinalIgnoreCase)) return ImportJobStates.Importing;
        if(status.Equals("Imported",StringComparison.OrdinalIgnoreCase)||status.Equals("Applied",StringComparison.OrdinalIgnoreCase)) return ImportJobStates.Verified;
        if(status.Equals("Duplicate",StringComparison.OrdinalIgnoreCase)) return ImportJobStates.Duplicate;
        if(status.Equals("Needs Action",StringComparison.OrdinalIgnoreCase)) return ImportJobStates.ReviewRequired;
        if(status.Equals("Failed",StringComparison.OrdinalIgnoreCase)) return ImportJobStates.Failed;
        if(status.Equals("Skipped",StringComparison.OrdinalIgnoreCase)) return ImportJobStates.Ignored;
        if(fileType.Equals("Pending Classification",StringComparison.OrdinalIgnoreCase)) return ImportJobStates.Validating;
        return status;
    }

    private static bool IsUndoableModule(string fileType) =>
        fileType.StartsWith("Bank",StringComparison.OrdinalIgnoreCase)
        || fileType.Equals("Sales",StringComparison.OrdinalIgnoreCase)
        || fileType.Equals("Purchase",StringComparison.OrdinalIgnoreCase)
        || fileType.Equals("Cash",StringComparison.OrdinalIgnoreCase)
        || fileType.Equals("Journal",StringComparison.OrdinalIgnoreCase)
        || fileType.Equals("MT940",StringComparison.OrdinalIgnoreCase)
        || fileType.Contains("OFX",StringComparison.OrdinalIgnoreCase)
        || fileType.Contains("QIF",StringComparison.OrdinalIgnoreCase)
        || fileType.Contains("camt",StringComparison.OrdinalIgnoreCase);

    private static void AddJobParameters(SqliteCommand command,ImportJobRecord job)
    {
        command.Parameters.AddWithValue("$JobId",job.JobId);command.Parameters.AddWithValue("$RunId",job.RunId);command.Parameters.AddWithValue("$FolderId",job.FolderId);command.Parameters.AddWithValue("$ClientId",job.ClientId);command.Parameters.AddWithValue("$Fy",job.FinancialYear.Trim());command.Parameters.AddWithValue("$Original",NormalizePath(job.OriginalPath));command.Parameters.AddWithValue("$Working",NormalizePath(job.WorkingPath));command.Parameters.AddWithValue("$Vault",NormalizePath(job.VaultPath));command.Parameters.AddWithValue("$FileName",job.FileName);command.Parameters.AddWithValue("$Hash",job.FileHash);command.Parameters.AddWithValue("$Size",job.FileSize);command.Parameters.AddWithValue("$Module",job.DetectedModule);command.Parameters.AddWithValue("$Confidence",job.Confidence);command.Parameters.AddWithValue("$State",job.State);command.Parameters.AddWithValue("$Detail",job.Detail);command.Parameters.AddWithValue("$Records",job.RecordCount);command.Parameters.AddWithValue("$Attempts",job.AttemptCount);command.Parameters.AddWithValue("$Undoable",job.IsUndoable?1:0);command.Parameters.AddWithValue("$UndoStatus",job.UndoStatus);command.Parameters.AddWithValue("$Created",job.CreatedUtc.ToString("O"));command.Parameters.AddWithValue("$Updated",job.UpdatedUtc.ToString("O"));
    }

    private static void AddRecurringParameters(SqliteCommand command,RecurringTransactionProfile profile,DateTime now)
    {
        command.Parameters.AddWithValue("$ClientId",profile.ClientId);command.Parameters.AddWithValue("$Fy",profile.FinancialYear.Trim());command.Parameters.AddWithValue("$Module",profile.Module.Trim());command.Parameters.AddWithValue("$Name",profile.ProfileName.Trim());command.Parameters.AddWithValue("$Frequency",profile.Frequency.Trim());command.Parameters.AddWithValue("$Interval",Math.Max(1,profile.Interval));command.Parameters.AddWithValue("$Start",profile.StartDate.Date.ToString("yyyy-MM-dd"));command.Parameters.AddWithValue("$End",profile.EndDate?.Date.ToString("yyyy-MM-dd")??string.Empty);command.Parameters.AddWithValue("$Next",profile.NextRunDate.Date.ToString("yyyy-MM-dd"));command.Parameters.AddWithValue("$Draft",profile.AutoCreateAsDraft?1:0);command.Parameters.AddWithValue("$Template",profile.TemplateJson??string.Empty);command.Parameters.AddWithValue("$Active",profile.IsActive?1:0);command.Parameters.AddWithValue("$User",string.IsNullOrWhiteSpace(profile.CreatedBy)?Environment.UserName:profile.CreatedBy);command.Parameters.AddWithValue("$Now",now.ToString("O"));
    }

    private static void SeedRolePermissions(SqliteConnection connection)
    {
        // A LAN workstation receives only the authenticated user's effective permissions from
        // the Main Server. Do not overwrite that server-authoritative projection with the local
        // default role map when the disposable network database is initialized.
        if (string.Equals(Environment.GetEnvironmentVariable("AAH_NETWORK_CLIENT_ACTIVE"), "1", StringComparison.OrdinalIgnoreCase))
            return;

        var map=new Dictionary<string,IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [GovernanceRoles.Administrator]=GovernancePermissions.All,
            [GovernanceRoles.User]=new[]{GovernancePermissions.ReadData,GovernancePermissions.ExportData,GovernancePermissions.UploadFiles,GovernancePermissions.Import,GovernancePermissions.ViewAudit,GovernancePermissions.MessagesSend,GovernancePermissions.FilesShare,GovernancePermissions.TasksManage,GovernancePermissions.LedgerMappingView,GovernancePermissions.BankView,GovernancePermissions.BankExport,GovernancePermissions.PurchaseView,GovernancePermissions.PurchaseExport,GovernancePermissions.SalesView,GovernancePermissions.JournalView,GovernancePermissions.MasterView,GovernancePermissions.StockView,GovernancePermissions.GstView,GovernancePermissions.GstImport,GovernancePermissions.TallyView},
            [GovernanceRoles.ImportOnly]=new[]{GovernancePermissions.UploadFiles,GovernancePermissions.Import,GovernancePermissions.GstImport,GovernancePermissions.MessagesSend,GovernancePermissions.FilesShare,GovernancePermissions.TasksManage},
            [GovernanceRoles.Accountant]=new[]{GovernancePermissions.ReadData,GovernancePermissions.ExportData,GovernancePermissions.UploadFiles,GovernancePermissions.Import,GovernancePermissions.UndoImport,GovernancePermissions.EditAccounting,GovernancePermissions.LedgerMapping,GovernancePermissions.LedgerMappingView,GovernancePermissions.LedgerMappingEdit,GovernancePermissions.CreateMaster,GovernancePermissions.TallyExport,GovernancePermissions.Approve,GovernancePermissions.ViewAudit,GovernancePermissions.MessagesSend,GovernancePermissions.FilesShare,GovernancePermissions.TasksManage,GovernancePermissions.ApprovalsManage,GovernancePermissions.BankView,GovernancePermissions.BankMap,GovernancePermissions.BankExport,GovernancePermissions.PurchaseView,GovernancePermissions.PurchaseEdit,GovernancePermissions.PurchaseExport,GovernancePermissions.SalesView,GovernancePermissions.SalesEdit,GovernancePermissions.JournalView,GovernancePermissions.JournalCreate,GovernancePermissions.JournalApprove,GovernancePermissions.MasterView,GovernancePermissions.StockView,GovernancePermissions.StockEdit,GovernancePermissions.GstView,GovernancePermissions.GstImport,GovernancePermissions.GstControl,GovernancePermissions.TallyView},
            [GovernanceRoles.Reviewer]=new[]{GovernancePermissions.ReadData,GovernancePermissions.ExportData,GovernancePermissions.Approve,GovernancePermissions.ViewAudit,GovernancePermissions.MessagesSend,GovernancePermissions.FilesShare,GovernancePermissions.TasksManage,GovernancePermissions.ApprovalsManage,GovernancePermissions.LedgerMappingView,GovernancePermissions.BankView,GovernancePermissions.BankExport,GovernancePermissions.PurchaseView,GovernancePermissions.PurchaseExport,GovernancePermissions.SalesView,GovernancePermissions.JournalView,GovernancePermissions.MasterView,GovernancePermissions.StockView,GovernancePermissions.GstView,GovernancePermissions.TallyView},
            [GovernanceRoles.DataEntry]=new[]{GovernancePermissions.ReadData,GovernancePermissions.UploadFiles,GovernancePermissions.Import,GovernancePermissions.EditAccounting,GovernancePermissions.LedgerMapping,GovernancePermissions.LedgerMappingView,GovernancePermissions.LedgerMappingEdit,GovernancePermissions.MessagesSend,GovernancePermissions.FilesShare,GovernancePermissions.TasksManage,GovernancePermissions.BankView,GovernancePermissions.BankMap,GovernancePermissions.PurchaseView,GovernancePermissions.PurchaseEdit,GovernancePermissions.SalesView,GovernancePermissions.SalesEdit,GovernancePermissions.JournalView,GovernancePermissions.JournalCreate,GovernancePermissions.MasterView,GovernancePermissions.StockView,GovernancePermissions.StockEdit,GovernancePermissions.GstView,GovernancePermissions.GstImport,GovernancePermissions.GstControl},
            [GovernanceRoles.ReadOnly]=new[]{GovernancePermissions.ReadData,GovernancePermissions.ExportData,GovernancePermissions.ViewAudit,GovernancePermissions.MessagesSend,GovernancePermissions.FilesShare,GovernancePermissions.LedgerMappingView,GovernancePermissions.BankView,GovernancePermissions.BankExport,GovernancePermissions.PurchaseView,GovernancePermissions.PurchaseExport,GovernancePermissions.SalesView,GovernancePermissions.JournalView,GovernancePermissions.MasterView,GovernancePermissions.StockView,GovernancePermissions.GstView,GovernancePermissions.TallyView},
            [GovernanceRoles.SecurityAdministrator]=new[]{GovernancePermissions.ManageServer,GovernancePermissions.ManageDevices,GovernancePermissions.ResetDeviceTrust,GovernancePermissions.ManageRoles,GovernancePermissions.ViewAudit,GovernancePermissions.RemoteAah,GovernancePermissions.RemoteAssist},
            [GovernanceRoles.UserAdministrator]=new[]{GovernancePermissions.ManageUsers,GovernancePermissions.ManageRoles,GovernancePermissions.ManageClientAccess,GovernancePermissions.ViewAudit},
            [GovernanceRoles.DeviceAdministrator]=new[]{GovernancePermissions.ManageDevices,GovernancePermissions.ResetDeviceTrust,GovernancePermissions.ManageClientAccess,GovernancePermissions.RemoteAah,GovernancePermissions.RemoteAssist,GovernancePermissions.ViewAudit},
            [GovernanceRoles.ClientAccessAdministrator]=new[]{GovernancePermissions.ManageClientAccess,GovernancePermissions.ManageUsers,GovernancePermissions.ViewAudit},
            [GovernanceRoles.UpdateAdministrator]=new[]{GovernancePermissions.ManageLanUpdates,GovernancePermissions.RemoteAah,GovernancePermissions.ManageDevices,GovernancePermissions.ViewAudit},
            [GovernanceRoles.BackupAdministrator]=new[]{GovernancePermissions.ManageBackup,GovernancePermissions.RestoreBackup,GovernancePermissions.ViewAudit},
            [GovernanceRoles.Auditor]=new[]{GovernancePermissions.ReadData,GovernancePermissions.ExportData,GovernancePermissions.ViewAudit,GovernancePermissions.BankView,GovernancePermissions.BankExport,GovernancePermissions.PurchaseView,GovernancePermissions.PurchaseExport,GovernancePermissions.SalesView,GovernancePermissions.JournalView,GovernancePermissions.MasterView,GovernancePermissions.StockView,GovernancePermissions.GstView,GovernancePermissions.TallyView},
            [GovernanceRoles.HelpDesk]=new[]{GovernancePermissions.ManageDevices,GovernancePermissions.ResetDeviceTrust,GovernancePermissions.RemoteAah,GovernancePermissions.RemoteAssist,GovernancePermissions.ViewAudit},
            [GovernanceRoles.Custom]=Array.Empty<string>()
        };
        using var transaction=connection.BeginTransaction();
        foreach(var role in GovernanceRoles.All) foreach(var permission in GovernancePermissions.All)
        {
            using var command=connection.CreateCommand();command.Transaction=transaction;
            command.CommandText=role.Equals(GovernanceRoles.Custom,StringComparison.OrdinalIgnoreCase)
                ? "INSERT OR IGNORE INTO RolePermissions(RoleName,PermissionName,IsAllowed) VALUES($Role,$Permission,$Allowed);"
                : "INSERT INTO RolePermissions(RoleName,PermissionName,IsAllowed) VALUES($Role,$Permission,$Allowed) ON CONFLICT(RoleName,PermissionName) DO UPDATE SET IsAllowed=excluded.IsAllowed;";
            command.Parameters.AddWithValue("$Role",role);command.Parameters.AddWithValue("$Permission",permission);command.Parameters.AddWithValue("$Allowed",map.TryGetValue(role,out var allowed)&&allowed.Contains(permission,StringComparer.OrdinalIgnoreCase)?1:0);command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void EnsureCurrentWindowsUser(SqliteConnection connection)
    {
        using var count=connection.CreateCommand();count.CommandText="SELECT COUNT(*) FROM AppUsers;";var users=Convert.ToInt32(count.ExecuteScalar()??0,CultureInfo.InvariantCulture);var now=DateTime.UtcNow.ToString("O");
        if(users==0)
        {
            var fallbackRole = string.Equals(Environment.GetEnvironmentVariable("AAH_NETWORK_CLIENT_ACTIVE"), "1", StringComparison.OrdinalIgnoreCase)
                ? GovernanceRoles.ReadOnly
                : GovernanceRoles.Administrator;
            using var insert=connection.CreateCommand();insert.CommandText="INSERT INTO AppUsers(WindowsUserName,DisplayName,RoleName,IsActive,CreatedUtc,UpdatedUtc,LastSeenUtc) VALUES($User,$Display,$Role,1,$Now,$Now,$Now);";insert.Parameters.AddWithValue("$User",Environment.UserName.Trim());insert.Parameters.AddWithValue("$Display",Environment.UserName.Trim());insert.Parameters.AddWithValue("$Role",fallbackRole);insert.Parameters.AddWithValue("$Now",now);insert.ExecuteNonQuery();return;
        }
        using var touch=connection.CreateCommand();touch.CommandText="UPDATE AppUsers SET LastSeenUtc=$Now WHERE WindowsUserName=$User COLLATE NOCASE;";touch.Parameters.AddWithValue("$Now",now);touch.Parameters.AddWithValue("$User",Environment.UserName.Trim());touch.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection(){
        return SqliteConnectionPolicy.Open(_connectionString);
    }
    private static string NormalizePath(string? path){if(string.IsNullOrWhiteSpace(path))return string.Empty;try{return Path.GetFullPath(path);}catch{return path.Trim();}}
    private static DateTime ParseDate(string value)=>DateTime.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var parsed)?parsed:DateTime.MinValue;
    private static DateTime? ParseNullableDate(string value)=>string.IsNullOrWhiteSpace(value)?null:ParseDate(value);
    private static void MigrateDeviceClientAccessToUsers(SqliteConnection connection)
    {
        // Completion 19: Client/FY authority belongs to the AAH user, not to a Windows/Android device.
        // Preserve existing allowed device assignments by promoting them once to user scope.
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO UserClientAccess(WindowsUserName,ClientId,FinancialYear,IsAllowed,UpdatedUtc,ExpiresUtc)
            SELECT d.UserName,
                   a.ClientId,
                   IFNULL(c.FinancialYear,''),
                   1,
                   $Now,
                   CASE
                       WHEN SUM(CASE WHEN IFNULL(a.ExpiresUtc,'')='' THEN 1 ELSE 0 END)>0 THEN ''
                       ELSE MAX(IFNULL(a.ExpiresUtc,''))
                   END
            FROM DeviceClientAccess a
            INNER JOIN LanDevices d ON d.DeviceId=a.DeviceId COLLATE NOCASE
            INNER JOIN Clients c ON c.Id=a.ClientId
            WHERE a.IsAllowed=1
              AND d.IsActive=1
              AND TRIM(IFNULL(d.UserName,''))<>''
              AND (IFNULL(a.ExpiresUtc,'')='' OR a.ExpiresUtc>$Now)
            GROUP BY d.UserName,a.ClientId,c.FinancialYear
            ON CONFLICT(WindowsUserName,ClientId,FinancialYear) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection,string table,string column,string definition)
    {
        SqliteSchemaColumnCache.EnsureColumn(connection, table, column, definition);
    }

    private static ApplicationUserRecord ReadUser(SqliteDataReader r)=>new(){Id=r.GetInt64(0),WindowsUserName=r.GetString(1),DisplayName=r.GetString(2),RoleName=r.GetString(3),IsActive=r.GetInt32(4)==1,CreatedUtc=ParseDate(r.GetString(5)),UpdatedUtc=ParseDate(r.GetString(6)),LastSeenUtc=ParseNullableDate(r.GetString(7)),MobileVerificationIntervalDays=r.FieldCount>8?Math.Clamp(r.GetInt32(8),1,365):30};
    private static ImportRunRecord ReadRun(SqliteDataReader r)=>new(){RunId=r.GetString(0),ClientId=r.GetInt64(1),FinancialYear=r.GetString(2),SourceKind=r.GetString(3),RequestedBy=r.GetString(4),Status=r.GetString(5),TotalFiles=r.GetInt32(6),ImportedFiles=r.GetInt32(7),DuplicateFiles=r.GetInt32(8),ReviewFiles=r.GetInt32(9),FailedFiles=r.GetInt32(10),RecordCount=r.GetInt32(11),IsUndoable=r.GetInt32(12)==1,StartedUtc=ParseDate(r.GetString(13)),UpdatedUtc=ParseDate(r.GetString(14)),CompletedUtc=ParseNullableDate(r.GetString(15)),Summary=r.GetString(16)};
    private static ImportJobRecord ReadJob(SqliteDataReader r)=>new(){JobId=r.GetString(0),RunId=r.GetString(1),FolderId=r.GetString(2),ClientId=r.GetInt64(3),FinancialYear=r.GetString(4),OriginalPath=r.GetString(5),WorkingPath=r.GetString(6),VaultPath=r.GetString(7),FileName=r.GetString(8),FileHash=r.GetString(9),FileSize=r.GetInt64(10),DetectedModule=r.GetString(11),Confidence=r.GetDouble(12),State=r.GetString(13),Detail=r.GetString(14),RecordCount=r.GetInt32(15),AttemptCount=r.GetInt32(16),IsUndoable=r.GetInt32(17)==1,UndoStatus=r.GetString(18),CreatedUtc=ParseDate(r.GetString(19)),UpdatedUtc=ParseDate(r.GetString(20))};
    private static RecurringTransactionProfile ReadRecurring(SqliteDataReader r)=>new(){Id=r.GetInt64(0),ClientId=r.GetInt64(1),FinancialYear=r.GetString(2),Module=r.GetString(3),ProfileName=r.GetString(4),Frequency=r.GetString(5),Interval=r.GetInt32(6),StartDate=ParseDate(r.GetString(7)),EndDate=ParseNullableDate(r.GetString(8)),NextRunDate=ParseDate(r.GetString(9)),AutoCreateAsDraft=r.GetInt32(10)==1,TemplateJson=r.GetString(11),IsActive=r.GetInt32(12)==1,CreatedBy=r.GetString(13),CreatedUtc=ParseDate(r.GetString(14)),UpdatedUtc=ParseDate(r.GetString(15))};
}
