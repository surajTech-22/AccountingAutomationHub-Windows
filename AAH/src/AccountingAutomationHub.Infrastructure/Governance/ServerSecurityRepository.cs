using System.Globalization;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Governance;

/// <summary>
/// Server-side security/lease repository. It deliberately uses the existing local SQLite authority;
/// the database itself is never exposed over LAN/WireGuard. Remote clients interact only with the
/// authenticated HTTPS server hosted by the main AAH computer.
/// </summary>
public sealed class ServerSecurityRepository
{
    private readonly string _connectionString;

    public ServerSecurityRepository()
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

        SqliteSchemaInitializationGate.RunOnce(nameof(ServerSecurityRepository), _connectionString, InitializeCore);


    private void InitializeCore()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS NetworkCredentials
            (
                UserName            TEXT PRIMARY KEY COLLATE NOCASE,
                PasswordSalt        TEXT NOT NULL DEFAULT '',
                PasswordHash        TEXT NOT NULL DEFAULT '',
                PasswordIterations  INTEGER NOT NULL DEFAULT 0,
                TotpSecretCipher    TEXT NOT NULL DEFAULT '',
                MfaEnabled          INTEGER NOT NULL DEFAULT 0,
                FailedAttempts      INTEGER NOT NULL DEFAULT 0,
                LockoutUntilUtc     TEXT NOT NULL DEFAULT '',
                UpdatedUtc          TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS NetworkSessions
            (
                SessionHash     TEXT PRIMARY KEY COLLATE NOCASE,
                UserName        TEXT NOT NULL COLLATE NOCASE,
                DeviceId        TEXT NOT NULL COLLATE NOCASE,
                CreatedUtc      TEXT NOT NULL,
                LastSeenUtc     TEXT NOT NULL,
                ExpiresUtc      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_NetworkSessions_UserDevice
            ON NetworkSessions(UserName COLLATE NOCASE,DeviceId COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS NetworkLoginChallenges
            (
                ChallengeId     TEXT PRIMARY KEY COLLATE NOCASE,
                UserName        TEXT NOT NULL COLLATE NOCASE,
                DeviceId        TEXT NOT NULL COLLATE NOCASE,
                DeviceName      TEXT NOT NULL DEFAULT '',
                NetworkScope    TEXT NOT NULL DEFAULT 'LAN',
                Code            TEXT NOT NULL DEFAULT '',
                CreatedUtc      TEXT NOT NULL,
                ExpiresUtc      TEXT NOT NULL,
                UsedUtc         TEXT NOT NULL DEFAULT '',
                IsActive        INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS IX_NetworkLoginChallenges_Pending
            ON NetworkLoginChallenges(IsActive,ExpiresUtc);

            CREATE TABLE IF NOT EXISTS FolderLinks
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                LinkId          TEXT NOT NULL COLLATE NOCASE,
                DeviceId        TEXT NOT NULL COLLATE NOCASE,
                UserName        TEXT NOT NULL COLLATE NOCASE,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                TokenHash       TEXT NOT NULL COLLATE NOCASE,
                LocalLabel      TEXT NOT NULL DEFAULT '',
                IsActive        INTEGER NOT NULL DEFAULT 1,
                CreatedUtc      TEXT NOT NULL,
                UpdatedUtc      TEXT NOT NULL,
                LastSeenUtc     TEXT NOT NULL DEFAULT '',
                ExpiredUtc      TEXT NOT NULL DEFAULT ''
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_FolderLinks_LinkId ON FolderLinks(LinkId COLLATE NOCASE);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_FolderLinks_TokenHash ON FolderLinks(TokenHash COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS IX_FolderLinks_Device ON FolderLinks(DeviceId COLLATE NOCASE,IsActive);

            CREATE TABLE IF NOT EXISTS DistributionTickets
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                TicketHash      TEXT NOT NULL COLLATE NOCASE,
                PackageKind     TEXT NOT NULL COLLATE NOCASE,
                CreatedBy       TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                CreatedUtc      TEXT NOT NULL,
                ExpiresUtc      TEXT NOT NULL,
                UsedUtc         TEXT NOT NULL DEFAULT '',
                IsActive        INTEGER NOT NULL DEFAULT 1
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_DistributionTickets_Hash ON DistributionTickets(TicketHash COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS ServerPackageRegistry
            (
                PackageKind     TEXT PRIMARY KEY COLLATE NOCASE,
                FilePath        TEXT NOT NULL DEFAULT '',
                Sha256          TEXT NOT NULL DEFAULT '',
                Version         TEXT NOT NULL DEFAULT '',
                UpdatedUtc      TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ServerArchivedFiles
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                Kind            TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                OriginalName    TEXT NOT NULL DEFAULT '',
                StoredPath      TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                Sha256          TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                FileSize        INTEGER NOT NULL DEFAULT 0,
                DeviceId        TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                UserName        TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                CreatedUtc      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ServerArchivedFiles_ClientFy ON ServerArchivedFiles(ClientId,FinancialYear COLLATE NOCASE,Kind COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS IX_ServerArchivedFiles_Hash ON ServerArchivedFiles(Sha256 COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS ServerSecurityEvents
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                EventType       TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                UserName        TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                DeviceId        TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                Detail          TEXT NOT NULL DEFAULT '',
                Success         INTEGER NOT NULL DEFAULT 0,
                CreatedUtc      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ServerSecurityEvents_Created ON ServerSecurityEvents(CreatedUtc DESC);
            """;
        command.ExecuteNonQuery();

        EnsureColumn(connection, "DistributionTickets", "PackageSha256", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "DistributionTickets", "PackageVersion", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "LanDevices", "DeviceType", "TEXT NOT NULL DEFAULT 'Windows'");
        EnsureColumn(connection, "LanDevices", "UserName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "LanDevices", "AppVersion", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "LanDevices", "EnrollmentState", "TEXT NOT NULL DEFAULT 'Active'");
        EnsureColumn(connection, "LanDevices", "LastValidatedUtc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "LanDevices", "RegistrationOtpVerifiedUtc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "LanDevices", "MobileMfaVerifiedUtc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "LanDevices", "ExpiredUtc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "LanDevices", "DeviceBindingKey", "TEXT NOT NULL DEFAULT ''");
        using (var bindingIndex = connection.CreateCommand())
        {
            bindingIndex.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS UX_LanDevices_AndroidBinding ON LanDevices(UserName COLLATE NOCASE,DeviceBindingKey COLLATE NOCASE) WHERE DeviceType='Android' AND DeviceBindingKey<>'';";
            bindingIndex.ExecuteNonQuery();
        }
        EnsureColumn(connection, "NetworkLoginChallenges", "NetworkScope", "TEXT NOT NULL DEFAULT 'LAN'");
    }

    public NetworkCredentialRecord? GetCredential(string userName)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT UserName,PasswordSalt,PasswordHash,PasswordIterations,TotpSecretCipher,MfaEnabled,FailedAttempts,LockoutUntilUtc,UpdatedUtc
            FROM NetworkCredentials WHERE UserName=$User COLLATE NOCASE LIMIT 1;
            """;
        command.Parameters.AddWithValue("$User", userName.Trim());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new NetworkCredentialRecord
        {
            UserName = reader.GetString(0), PasswordSalt = reader.GetString(1), PasswordHash = reader.GetString(2),
            PasswordIterations = reader.GetInt32(3), TotpSecretCipher = reader.GetString(4), MfaEnabled = reader.GetInt32(5) == 1,
            FailedAttempts = reader.GetInt32(6), LockoutUntilUtc = ParseNullableDate(reader.GetString(7)), UpdatedUtc = ParseDate(reader.GetString(8))
        };
    }

    public IReadOnlyList<NetworkCredentialRecord> GetCredentials()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT UserName,PasswordSalt,PasswordHash,PasswordIterations,TotpSecretCipher,MfaEnabled,FailedAttempts,LockoutUntilUtc,UpdatedUtc FROM NetworkCredentials ORDER BY UserName COLLATE NOCASE;";
        using var reader = command.ExecuteReader();
        var result = new List<NetworkCredentialRecord>();
        while (reader.Read())
        {
            result.Add(new NetworkCredentialRecord
            {
                UserName = reader.GetString(0), PasswordSalt = reader.GetString(1), PasswordHash = reader.GetString(2),
                PasswordIterations = reader.GetInt32(3), TotpSecretCipher = reader.GetString(4), MfaEnabled = reader.GetInt32(5) == 1,
                FailedAttempts = reader.GetInt32(6), LockoutUntilUtc = ParseNullableDate(reader.GetString(7)), UpdatedUtc = ParseDate(reader.GetString(8))
            });
        }
        return result;
    }

    public void SaveCredential(NetworkCredentialRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO NetworkCredentials(UserName,PasswordSalt,PasswordHash,PasswordIterations,TotpSecretCipher,MfaEnabled,FailedAttempts,LockoutUntilUtc,UpdatedUtc)
            VALUES($User,$Salt,$Hash,$Iterations,$Totp,$Mfa,$Failed,$Lockout,$Updated)
            ON CONFLICT(UserName) DO UPDATE SET PasswordSalt=excluded.PasswordSalt,PasswordHash=excluded.PasswordHash,
                PasswordIterations=excluded.PasswordIterations,TotpSecretCipher=excluded.TotpSecretCipher,MfaEnabled=excluded.MfaEnabled,
                FailedAttempts=excluded.FailedAttempts,LockoutUntilUtc=excluded.LockoutUntilUtc,UpdatedUtc=excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$User", record.UserName.Trim());
        command.Parameters.AddWithValue("$Salt", record.PasswordSalt);
        command.Parameters.AddWithValue("$Hash", record.PasswordHash);
        command.Parameters.AddWithValue("$Iterations", record.PasswordIterations);
        command.Parameters.AddWithValue("$Totp", record.TotpSecretCipher);
        command.Parameters.AddWithValue("$Mfa", record.MfaEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$Failed", record.FailedAttempts);
        command.Parameters.AddWithValue("$Lockout", record.LockoutUntilUtc?.ToString("O") ?? string.Empty);
        command.Parameters.AddWithValue("$Updated", (record.UpdatedUtc == default ? DateTime.UtcNow : record.UpdatedUtc).ToString("O"));
        command.ExecuteNonQuery();
    }

    public void RecordAuthenticationFailure(string userName, int failures, DateTime? lockoutUntilUtc)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE NetworkCredentials SET FailedAttempts=$Failed,LockoutUntilUtc=$Lockout,UpdatedUtc=$Utc WHERE UserName=$User COLLATE NOCASE;";
        command.Parameters.AddWithValue("$Failed", failures);
        command.Parameters.AddWithValue("$Lockout", lockoutUntilUtc?.ToString("O") ?? string.Empty);
        command.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$User", userName.Trim());
        command.ExecuteNonQuery();
    }

    public void ClearAuthenticationFailures(string userName)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE NetworkCredentials SET FailedAttempts=0,LockoutUntilUtc='',UpdatedUtc=$Utc WHERE UserName=$User COLLATE NOCASE;";
        command.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$User", userName.Trim());
        command.ExecuteNonQuery();
    }

    public ServerDeviceRecord RegisterDevice(string deviceName, string deviceType, string userName, string tokenHash, string roleName, long clientId, string financialYear, string appVersion, string deviceBindingKey = "")
    {
        var now = DateTime.UtcNow;
        var deviceId = Guid.NewGuid().ToString("N");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO LanDevices(DeviceId,DeviceName,TokenHash,RoleName,ClientId,FinancialYear,IsActive,CreatedUtc,UpdatedUtc,LastSeenUtc,DeviceType,UserName,AppVersion,EnrollmentState,LastValidatedUtc,RegistrationOtpVerifiedUtc,MobileMfaVerifiedUtc,ExpiredUtc,DeviceBindingKey)
            VALUES($Id,$Name,$Hash,$Role,$Client,$Fy,1,$Now,$Now,$Now,$Type,$User,$Version,'Verification Required',$Now,'','','',$Binding);
            """;
        command.Parameters.AddWithValue("$Id", deviceId);
        command.Parameters.AddWithValue("$Name", string.IsNullOrWhiteSpace(deviceName) ? deviceType + " Device" : deviceName.Trim());
        command.Parameters.AddWithValue("$Hash", tokenHash.Trim());
        command.Parameters.AddWithValue("$Role", roleName.Trim());
        command.Parameters.AddWithValue("$Client", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        command.Parameters.AddWithValue("$Now", now.ToString("O"));
        command.Parameters.AddWithValue("$Type", deviceType.Trim());
        command.Parameters.AddWithValue("$User", userName.Trim());
        command.Parameters.AddWithValue("$Version", appVersion.Trim());
        command.Parameters.AddWithValue("$Binding", deviceBindingKey.Trim());
        command.ExecuteNonQuery();
        using var idCommand = connection.CreateCommand();
        idCommand.CommandText = "SELECT last_insert_rowid();";
        return new ServerDeviceRecord
        {
            Id = Convert.ToInt64(idCommand.ExecuteScalar(), CultureInfo.InvariantCulture), DeviceId = deviceId,
            DeviceName = string.IsNullOrWhiteSpace(deviceName) ? deviceType + " Device" : deviceName.Trim(), DeviceType = deviceType.Trim(),
            UserName = userName.Trim(), TokenHash = tokenHash.Trim(), RoleName = roleName.Trim(), ClientId = clientId,
            FinancialYear = financialYear.Trim(), AppVersion = appVersion.Trim(), DeviceBindingKey = deviceBindingKey.Trim(), IsActive = true,
            EnrollmentState = ServerEnrollmentStates.VerificationRequired, CreatedUtc = now, UpdatedUtc = now, LastSeenUtc = now, LastValidatedUtc = now, RegistrationOtpVerifiedUtc = null, MobileMfaVerifiedUtc = null
        };
    }

    public (ServerDeviceRecord Device, bool Recovered) RegisterOrRecoverAndroidDevice(string deviceName, string userName, string tokenHash, string roleName, string appVersion, string deviceBindingKey)
    {
        var binding = deviceBindingKey.Trim();
        if (binding.Length == 0)
            return (RegisterDevice(deviceName, ServerDeviceTypes.Android, userName, tokenHash, roleName, 0, string.Empty, appVersion), false);
        if (binding.Length != 43 || binding.Any(character => !char.IsLetterOrDigit(character) && character != '-' && character != '_'))
            throw new InvalidOperationException("Android device binding is malformed.");

        var now = DateTime.UtcNow;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        ServerDeviceRecord? existing = null;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = DeviceSelect + " WHERE DeviceType=$Type COLLATE NOCASE AND UserName=$User COLLATE NOCASE AND DeviceBindingKey=$Binding COLLATE NOCASE ORDER BY UpdatedUtc DESC LIMIT 1;";
            select.Parameters.AddWithValue("$Type", ServerDeviceTypes.Android);
            select.Parameters.AddWithValue("$User", userName.Trim());
            select.Parameters.AddWithValue("$Binding", binding);
            using var reader = select.ExecuteReader();
            if (reader.Read()) existing = ReadDevice(reader);
        }

        if (existing is not null)
        {
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE LanDevices SET DeviceName=$Name,TokenHash=$Hash,RoleName=$Role,IsActive=1,UpdatedUtc=$Now,LastSeenUtc=$Now,AppVersion=$Version,EnrollmentState='Active',LastValidatedUtc=$Now,MobileMfaVerifiedUtc=$Now,ExpiredUtc='',DeviceBindingKey=$Binding WHERE Id=$Id;";
                update.Parameters.AddWithValue("$Name", string.IsNullOrWhiteSpace(deviceName) ? "Android device" : deviceName.Trim());
                update.Parameters.AddWithValue("$Hash", tokenHash.Trim());
                update.Parameters.AddWithValue("$Role", roleName.Trim());
                update.Parameters.AddWithValue("$Now", now.ToString("O"));
                update.Parameters.AddWithValue("$Version", appVersion.Trim());
                update.Parameters.AddWithValue("$Binding", binding);
                update.Parameters.AddWithValue("$Id", existing.Id);
                update.ExecuteNonQuery();
            }
            using (var sessions = connection.CreateCommand())
            {
                sessions.Transaction = transaction;
                sessions.CommandText = "DELETE FROM NetworkSessions WHERE DeviceId=$Device COLLATE NOCASE;";
                sessions.Parameters.AddWithValue("$Device", existing.DeviceId);
                sessions.ExecuteNonQuery();
            }
            transaction.Commit();
            existing.DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "Android device" : deviceName.Trim();
            existing.TokenHash = tokenHash.Trim();
            existing.RoleName = roleName.Trim();
            existing.AppVersion = appVersion.Trim();
            existing.DeviceBindingKey = binding;
            existing.IsActive = true;
            existing.EnrollmentState = ServerEnrollmentStates.Active;
            existing.UpdatedUtc = now;
            existing.LastSeenUtc = now;
            existing.LastValidatedUtc = now;
            existing.MobileMfaVerifiedUtc = now;
            existing.ExpiredUtc = null;
            return (existing, true);
        }

        var deviceId = Guid.NewGuid().ToString("N");
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO LanDevices(DeviceId,DeviceName,TokenHash,RoleName,ClientId,FinancialYear,IsActive,CreatedUtc,UpdatedUtc,LastSeenUtc,DeviceType,UserName,AppVersion,EnrollmentState,LastValidatedUtc,RegistrationOtpVerifiedUtc,MobileMfaVerifiedUtc,ExpiredUtc,DeviceBindingKey) VALUES($Id,$Name,$Hash,$Role,0,'',1,$Now,$Now,$Now,$Type,$User,$Version,'Active',$Now,'',$Now,'',$Binding);";
            insert.Parameters.AddWithValue("$Id", deviceId);
            insert.Parameters.AddWithValue("$Name", string.IsNullOrWhiteSpace(deviceName) ? "Android device" : deviceName.Trim());
            insert.Parameters.AddWithValue("$Hash", tokenHash.Trim());
            insert.Parameters.AddWithValue("$Role", roleName.Trim());
            insert.Parameters.AddWithValue("$Now", now.ToString("O"));
            insert.Parameters.AddWithValue("$Type", ServerDeviceTypes.Android);
            insert.Parameters.AddWithValue("$User", userName.Trim());
            insert.Parameters.AddWithValue("$Version", appVersion.Trim());
            insert.Parameters.AddWithValue("$Binding", binding);
            insert.ExecuteNonQuery();
        }
        long id;
        using (var idCommand = connection.CreateCommand())
        {
            idCommand.Transaction = transaction;
            idCommand.CommandText = "SELECT last_insert_rowid();";
            id = Convert.ToInt64(idCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        transaction.Commit();
        return (new ServerDeviceRecord
        {
            Id = id, DeviceId = deviceId, DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "Android device" : deviceName.Trim(),
            DeviceType = ServerDeviceTypes.Android, UserName = userName.Trim(), TokenHash = tokenHash.Trim(), RoleName = roleName.Trim(),
            ClientId = 0, FinancialYear = string.Empty, AppVersion = appVersion.Trim(), DeviceBindingKey = binding, IsActive = true,
            EnrollmentState = ServerEnrollmentStates.Active, CreatedUtc = now, UpdatedUtc = now, LastSeenUtc = now, LastValidatedUtc = now, MobileMfaVerifiedUtc = now
        }, false);
    }

    public ServerDeviceRecord? FindDeviceByTokenHash(string tokenHash)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = DeviceSelect + " WHERE TokenHash=$Hash COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("$Hash", tokenHash.Trim());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadDevice(reader) : null;
    }

    public ServerDeviceRecord? FindDeviceById(string deviceId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = DeviceSelect + " WHERE DeviceId=$Id COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("$Id", deviceId.Trim());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadDevice(reader) : null;
    }

    public void TouchDevice(long id, string appVersion = "", bool validated = false)
    {
        var now = DateTime.UtcNow.ToString("O");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = validated
            ? "UPDATE LanDevices SET LastSeenUtc=$Now,LastValidatedUtc=$Now,UpdatedUtc=$Now,AppVersion=CASE WHEN $Version='' THEN AppVersion ELSE $Version END WHERE Id=$Id;"
            : "UPDATE LanDevices SET LastSeenUtc=$Now,UpdatedUtc=$Now,AppVersion=CASE WHEN $Version='' THEN AppVersion ELSE $Version END WHERE Id=$Id;";
        command.Parameters.AddWithValue("$Now", now);
        command.Parameters.AddWithValue("$Version", appVersion.Trim());
        command.Parameters.AddWithValue("$Id", id);
        command.ExecuteNonQuery();
    }

    public void MarkRegistrationOtpVerified(long id)
    {
        var now = DateTime.UtcNow.ToString("O");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE LanDevices SET RegistrationOtpVerifiedUtc=$Now,EnrollmentState='Active',IsActive=1,ExpiredUtc='',LastValidatedUtc=$Now,LastSeenUtc=$Now,UpdatedUtc=$Now WHERE Id=$Id;";
        command.Parameters.AddWithValue("$Now", now);
        command.Parameters.AddWithValue("$Id", id);
        command.ExecuteNonQuery();
    }

    public void MarkMobileMfaVerified(long id)
    {
        var now = DateTime.UtcNow.ToString("O");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE LanDevices SET MobileMfaVerifiedUtc=$Now,LastValidatedUtc=$Now,LastSeenUtc=$Now,UpdatedUtc=$Now WHERE Id=$Id;";
        command.Parameters.AddWithValue("$Now", now);
        command.Parameters.AddWithValue("$Id", id);
        command.ExecuteNonQuery();
    }

    public void ClearMobileMfaVerificationForUser(string userName)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE LanDevices SET MobileMfaVerifiedUtc='',UpdatedUtc=$Now WHERE DeviceType='Android' COLLATE NOCASE AND UserName=$User COLLATE NOCASE; DELETE FROM NetworkSessions WHERE DeviceId IN (SELECT DeviceId FROM LanDevices WHERE DeviceType='Android' COLLATE NOCASE AND UserName=$User COLLATE NOCASE);";
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$User", userName.Trim());
        command.ExecuteNonQuery();
    }

    public void RequireRegistrationOtp(long id)
    {
        var now = DateTime.UtcNow.ToString("O");
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        string deviceId;
        using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT DeviceId FROM LanDevices WHERE Id=$Id LIMIT 1;";
            lookup.Parameters.AddWithValue("$Id", id);
            deviceId = Convert.ToString(lookup.ExecuteScalar()) ?? string.Empty;
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE LanDevices SET RegistrationOtpVerifiedUtc='',EnrollmentState='Verification Required',IsActive=1,ExpiredUtc='',UpdatedUtc=$Now WHERE Id=$Id;";
            command.Parameters.AddWithValue("$Now", now);
            command.Parameters.AddWithValue("$Id", id);
            command.ExecuteNonQuery();
        }
        if (deviceId.Length > 0)
        {
            using var sessions = connection.CreateCommand();
            sessions.Transaction = transaction;
            sessions.CommandText = "DELETE FROM NetworkSessions WHERE DeviceId=$Device COLLATE NOCASE;";
            sessions.Parameters.AddWithValue("$Device", deviceId);
            sessions.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void ExpireDevice(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE LanDevices SET IsActive=0,EnrollmentState='Expired',ExpiredUtc=$Now,UpdatedUtc=$Now WHERE Id=$Id; DELETE FROM NetworkSessions WHERE DeviceId=(SELECT DeviceId FROM LanDevices WHERE Id=$Id); UPDATE FolderLinks SET IsActive=0,ExpiredUtc=$Now,UpdatedUtc=$Now WHERE DeviceId=(SELECT DeviceId FROM LanDevices WHERE Id=$Id);";
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$Id", id);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ServerDeviceRecord> GetServerDevices()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = DeviceSelect + " ORDER BY UpdatedUtc DESC;";
        using var reader = command.ExecuteReader();
        var list = new List<ServerDeviceRecord>();
        while (reader.Read()) list.Add(ReadDevice(reader));
        return list;
    }


    public void SaveLoginChallenge(NetworkLoginChallengeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO NetworkLoginChallenges(ChallengeId,UserName,DeviceId,DeviceName,NetworkScope,Code,CreatedUtc,ExpiresUtc,UsedUtc,IsActive)
            VALUES($Id,$User,$Device,$DeviceName,$NetworkScope,$Code,$Created,$Expires,'',1)
            ON CONFLICT(ChallengeId) DO UPDATE SET UserName=excluded.UserName,DeviceId=excluded.DeviceId,DeviceName=excluded.DeviceName,
                NetworkScope=excluded.NetworkScope,Code=excluded.Code,CreatedUtc=excluded.CreatedUtc,ExpiresUtc=excluded.ExpiresUtc,UsedUtc='',IsActive=1;
            """;
        command.Parameters.AddWithValue("$Id", record.ChallengeId.Trim());
        command.Parameters.AddWithValue("$User", record.UserName.Trim());
        command.Parameters.AddWithValue("$Device", record.DeviceId.Trim());
        command.Parameters.AddWithValue("$DeviceName", record.DeviceName.Trim());
        command.Parameters.AddWithValue("$NetworkScope", string.IsNullOrWhiteSpace(record.NetworkScope) ? "LAN" : record.NetworkScope.Trim());
        command.Parameters.AddWithValue("$Code", record.Code.Trim());
        command.Parameters.AddWithValue("$Created", record.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$Expires", record.ExpiresUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<NetworkLoginChallengeRecord> GetPendingLoginChallenges()
    {
        PurgeExpiredLoginChallenges();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ChallengeId,UserName,DeviceId,DeviceName,NetworkScope,Code,CreatedUtc,ExpiresUtc,UsedUtc,IsActive
            FROM NetworkLoginChallenges
            WHERE IsActive=1 AND UsedUtc='' AND ExpiresUtc>$Now
            ORDER BY CreatedUtc DESC;
            """;
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        using var reader = command.ExecuteReader();
        var result = new List<NetworkLoginChallengeRecord>();
        while (reader.Read()) result.Add(ReadLoginChallenge(reader));
        return result;
    }

    public NetworkLoginChallengeRecord? FindLoginChallenge(string challengeId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ChallengeId,UserName,DeviceId,DeviceName,NetworkScope,Code,CreatedUtc,ExpiresUtc,UsedUtc,IsActive
            FROM NetworkLoginChallenges WHERE ChallengeId=$Id COLLATE NOCASE LIMIT 1;
            """;
        command.Parameters.AddWithValue("$Id", challengeId.Trim());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadLoginChallenge(reader) : null;
    }

    public void MarkLoginChallengeUsed(string challengeId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE NetworkLoginChallenges SET IsActive=0,UsedUtc=$Now,Code='' WHERE ChallengeId=$Id COLLATE NOCASE;";
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$Id", challengeId.Trim());
        command.ExecuteNonQuery();
    }

    public void PurgeExpiredLoginChallenges()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE NetworkLoginChallenges SET IsActive=0,Code='' WHERE IsActive=1 AND ExpiresUtc<=$Now; DELETE FROM NetworkLoginChallenges WHERE ExpiresUtc<$DeleteBefore;";
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$DeleteBefore", DateTime.UtcNow.AddDays(-1).ToString("O"));
        command.ExecuteNonQuery();
    }

    public string CreateSession(string userName, string deviceId, TimeSpan lifetime)
    {
        var token = NewToken();
        var hash = HashOpaqueToken(token);
        var now = DateTime.UtcNow;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO NetworkSessions(SessionHash,UserName,DeviceId,CreatedUtc,LastSeenUtc,ExpiresUtc)
            VALUES($Hash,$User,$Device,$Now,$Now,$Expires);
            """;
        command.Parameters.AddWithValue("$Hash", hash);
        command.Parameters.AddWithValue("$User", userName.Trim());
        command.Parameters.AddWithValue("$Device", deviceId.Trim());
        command.Parameters.AddWithValue("$Now", now.ToString("O"));
        command.Parameters.AddWithValue("$Expires", now.Add(lifetime).ToString("O"));
        command.ExecuteNonQuery();
        return token;
    }

    /// <summary>
    /// Creates the authenticated network session and consumes the one-use OTP challenge in one SQLite transaction.
    /// If session creation fails, the challenge remains active so a transient server error cannot turn into a false 401 retry.
    /// </summary>
    public string CreateSessionAndConsumeLoginChallenge(string challengeId, string userName, string deviceId, TimeSpan lifetime)
    {
        var token = NewToken();
        var hash = HashOpaqueToken(token);
        var now = DateTime.UtcNow;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var session = connection.CreateCommand())
        {
            session.Transaction = transaction;
            session.CommandText =
                """
                INSERT INTO NetworkSessions(SessionHash,UserName,DeviceId,CreatedUtc,LastSeenUtc,ExpiresUtc)
                VALUES($Hash,$User,$Device,$Now,$Now,$Expires);
                """;
            session.Parameters.AddWithValue("$Hash", hash);
            session.Parameters.AddWithValue("$User", userName.Trim());
            session.Parameters.AddWithValue("$Device", deviceId.Trim());
            session.Parameters.AddWithValue("$Now", now.ToString("O"));
            session.Parameters.AddWithValue("$Expires", now.Add(lifetime).ToString("O"));
            session.ExecuteNonQuery();
        }

        using (var challenge = connection.CreateCommand())
        {
            challenge.Transaction = transaction;
            challenge.CommandText =
                "UPDATE NetworkLoginChallenges SET IsActive=0,UsedUtc=$Now,Code='' WHERE ChallengeId=$Id COLLATE NOCASE AND IsActive=1 AND UsedUtc='';";
            challenge.Parameters.AddWithValue("$Now", now.ToString("O"));
            challenge.Parameters.AddWithValue("$Id", challengeId.Trim());
            if (challenge.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The Main Server OTP challenge is no longer active.");
        }

        transaction.Commit();
        return token;
    }

    public NetworkSessionRecord? FindSession(string rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SessionHash,UserName,DeviceId,CreatedUtc,LastSeenUtc,ExpiresUtc FROM NetworkSessions WHERE SessionHash=$Hash COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("$Hash", HashOpaqueToken(rawToken));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new NetworkSessionRecord
        {
            SessionHash = reader.GetString(0), UserName = reader.GetString(1), DeviceId = reader.GetString(2),
            CreatedUtc = ParseDate(reader.GetString(3)), LastSeenUtc = ParseDate(reader.GetString(4)), ExpiresUtc = ParseDate(reader.GetString(5))
        };
    }

    public IReadOnlyList<NetworkSessionRecord> GetSessions(int limit = 500)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT SessionHash,UserName,DeviceId,CreatedUtc,LastSeenUtc,ExpiresUtc FROM NetworkSessions ORDER BY LastSeenUtc DESC LIMIT $Limit;";
        command.Parameters.AddWithValue("$Limit", Math.Clamp(limit, 1, 5000));
        using var reader = command.ExecuteReader();
        var result = new List<NetworkSessionRecord>();
        while (reader.Read())
        {
            result.Add(new NetworkSessionRecord
            {
                SessionHash = reader.GetString(0), UserName = reader.GetString(1), DeviceId = reader.GetString(2),
                CreatedUtc = ParseDate(reader.GetString(3)), LastSeenUtc = ParseDate(reader.GetString(4)), ExpiresUtc = ParseDate(reader.GetString(5))
            });
        }
        return result;
    }

    public void TouchSession(string sessionHash)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE NetworkSessions SET LastSeenUtc=$Now WHERE SessionHash=$Hash COLLATE NOCASE;";
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$Hash", sessionHash.Trim());
        command.ExecuteNonQuery();
    }

    public DateTime TouchSession(string sessionHash, TimeSpan slidingLifetime)
    {
        var now = DateTime.UtcNow;
        var expires = now.Add(slidingLifetime);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE NetworkSessions SET LastSeenUtc=$Now,ExpiresUtc=$Expires WHERE SessionHash=$Hash COLLATE NOCASE;";
        command.Parameters.AddWithValue("$Now", now.ToString("O"));
        command.Parameters.AddWithValue("$Expires", expires.ToString("O"));
        command.Parameters.AddWithValue("$Hash", sessionHash.Trim());
        command.ExecuteNonQuery();
        return expires;
    }

    public void DeleteSession(string sessionHash)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM NetworkSessions WHERE SessionHash=$Hash COLLATE NOCASE;";
        command.Parameters.AddWithValue("$Hash", sessionHash.Trim());
        command.ExecuteNonQuery();
    }

    public void DeleteSessionsForUser(string userName)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM NetworkSessions WHERE UserName=$User COLLATE NOCASE;";
        command.Parameters.AddWithValue("$User", userName.Trim());
        command.ExecuteNonQuery();
    }

    public void RevokeDevice(long id)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        string deviceId;
        using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT DeviceId FROM LanDevices WHERE Id=$Id LIMIT 1;";
            lookup.Parameters.AddWithValue("$Id", id);
            deviceId = Convert.ToString(lookup.ExecuteScalar()) ?? string.Empty;
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE LanDevices SET IsActive=0,EnrollmentState='Revoked',ExpiredUtc=$Now,UpdatedUtc=$Now WHERE Id=$Id;";
            command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$Id", id);
            command.ExecuteNonQuery();
        }

        if (deviceId.Length > 0)
        {
            using var sessions = connection.CreateCommand();
            sessions.Transaction = transaction;
            sessions.CommandText = "DELETE FROM NetworkSessions WHERE DeviceId=$Device COLLATE NOCASE; UPDATE FolderLinks SET IsActive=0,ExpiredUtc=$Now,UpdatedUtc=$Now WHERE DeviceId=$Device COLLATE NOCASE;";
            sessions.Parameters.AddWithValue("$Device", deviceId);
            sessions.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
            sessions.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void SetDeviceLifecycleState(long id,string state,bool active)
    {
        using var connection=OpenConnection();using var transaction=connection.BeginTransaction();
        string deviceId;using(var lookup=connection.CreateCommand()){lookup.Transaction=transaction;lookup.CommandText="SELECT DeviceId FROM LanDevices WHERE Id=$Id LIMIT 1;";lookup.Parameters.AddWithValue("$Id",id);deviceId=Convert.ToString(lookup.ExecuteScalar())??string.Empty;}
        if(string.IsNullOrWhiteSpace(deviceId))throw new InvalidOperationException("The selected device registration no longer exists.");
        using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText="UPDATE LanDevices SET IsActive=$Active,EnrollmentState=$State,ExpiredUtc=CASE WHEN $Active=1 THEN '' ELSE $Now END,UpdatedUtc=$Now WHERE Id=$Id;";command.Parameters.AddWithValue("$Active",active?1:0);command.Parameters.AddWithValue("$State",state.Trim());command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$Id",id);command.ExecuteNonQuery();}
        if(!active){using var cleanup=connection.CreateCommand();cleanup.Transaction=transaction;cleanup.CommandText="DELETE FROM NetworkSessions WHERE DeviceId=$Device COLLATE NOCASE; UPDATE FolderLinks SET IsActive=0,ExpiredUtc=$Now,UpdatedUtc=$Now WHERE DeviceId=$Device COLLATE NOCASE;";cleanup.Parameters.AddWithValue("$Device",deviceId);cleanup.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));cleanup.ExecuteNonQuery();}
        transaction.Commit();
    }

    public void DeleteDeviceRegistration(long id)
    {
        using var connection=OpenConnection();using var transaction=connection.BeginTransaction();
        string deviceId;using(var lookup=connection.CreateCommand()){lookup.Transaction=transaction;lookup.CommandText="SELECT DeviceId FROM LanDevices WHERE Id=$Id LIMIT 1;";lookup.Parameters.AddWithValue("$Id",id);deviceId=Convert.ToString(lookup.ExecuteScalar())??string.Empty;}
        if(string.IsNullOrWhiteSpace(deviceId))throw new InvalidOperationException("The selected device registration no longer exists.");
        foreach(var sql in new[]{"DELETE FROM NetworkSessions WHERE DeviceId=$Device COLLATE NOCASE;","DELETE FROM NetworkLoginChallenges WHERE DeviceId=$Device COLLATE NOCASE;","DELETE FROM FolderLinks WHERE DeviceId=$Device COLLATE NOCASE;","DELETE FROM DeviceClientAccess WHERE DeviceId=$Device COLLATE NOCASE;","DELETE FROM LanDevices WHERE Id=$Id;"}){using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText=sql;command.Parameters.AddWithValue("$Device",deviceId);command.Parameters.AddWithValue("$Id",id);command.ExecuteNonQuery();}
        transaction.Commit();
    }

    public (FolderLinkRecord Record, string RawToken) CreateFolderLink(ServerDeviceRecord device, long clientId, string financialYear, string localLabel)
    {
        var rawToken = NewToken();
        var now = DateTime.UtcNow;
        var record = new FolderLinkRecord
        {
            LinkId = Guid.NewGuid().ToString("N"), DeviceId = device.DeviceId, UserName = device.UserName,
            ClientId = clientId, FinancialYear = financialYear.Trim(), TokenHash = HashOpaqueToken(rawToken),
            LocalLabel = localLabel?.Trim() ?? string.Empty, IsActive = true, CreatedUtc = now, UpdatedUtc = now, LastSeenUtc = now
        };
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO FolderLinks(LinkId,DeviceId,UserName,ClientId,FinancialYear,TokenHash,LocalLabel,IsActive,CreatedUtc,UpdatedUtc,LastSeenUtc,ExpiredUtc)
            VALUES($Link,$Device,$User,$Client,$Fy,$Hash,$Label,1,$Now,$Now,$Now,'');
            """;
        command.Parameters.AddWithValue("$Link", record.LinkId); command.Parameters.AddWithValue("$Device", record.DeviceId);
        command.Parameters.AddWithValue("$User", record.UserName); command.Parameters.AddWithValue("$Client", record.ClientId);
        command.Parameters.AddWithValue("$Fy", record.FinancialYear); command.Parameters.AddWithValue("$Hash", record.TokenHash);
        command.Parameters.AddWithValue("$Label", record.LocalLabel); command.Parameters.AddWithValue("$Now", now.ToString("O"));
        command.ExecuteNonQuery();
        using var idCommand = connection.CreateCommand(); idCommand.CommandText = "SELECT last_insert_rowid();";
        record.Id = Convert.ToInt64(idCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        return (record, rawToken);
    }

    public FolderLinkRecord? FindFolderLink(string linkId, string rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,LinkId,DeviceId,UserName,ClientId,FinancialYear,TokenHash,LocalLabel,IsActive,CreatedUtc,UpdatedUtc,LastSeenUtc,ExpiredUtc
            FROM FolderLinks WHERE LinkId=$Link COLLATE NOCASE AND TokenHash=$Hash COLLATE NOCASE LIMIT 1;
            """;
        command.Parameters.AddWithValue("$Link", linkId.Trim()); command.Parameters.AddWithValue("$Hash", HashOpaqueToken(rawToken));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadFolderLink(reader) : null;
    }

    public IReadOnlyList<FolderLinkRecord> GetFolderLinks()
    {
        using var connection = OpenConnection(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,LinkId,DeviceId,UserName,ClientId,FinancialYear,TokenHash,LocalLabel,IsActive,CreatedUtc,UpdatedUtc,LastSeenUtc,ExpiredUtc FROM FolderLinks ORDER BY UpdatedUtc DESC;";
        using var reader = command.ExecuteReader(); var result = new List<FolderLinkRecord>();
        while (reader.Read()) result.Add(ReadFolderLink(reader)); return result;
    }

    public void TouchFolderLink(long id)
    {
        using var connection = OpenConnection(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE FolderLinks SET LastSeenUtc=$Now,UpdatedUtc=$Now WHERE Id=$Id;";
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O")); command.Parameters.AddWithValue("$Id", id); command.ExecuteNonQuery();
    }

    public void ExpireFolderLink(long id)
    {
        using var connection = OpenConnection(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE FolderLinks SET IsActive=0,ExpiredUtc=$Now,UpdatedUtc=$Now WHERE Id=$Id;";
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O")); command.Parameters.AddWithValue("$Id", id); command.ExecuteNonQuery();
    }

    public string CreateDistributionTicket(string packageKind, string packageSha256, string packageVersion, string createdBy, TimeSpan lifetime)
    {
        var token = NewToken(); var now = DateTime.UtcNow;
        using var connection = OpenConnection(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO DistributionTickets(TicketHash,PackageKind,PackageSha256,PackageVersion,CreatedBy,CreatedUtc,ExpiresUtc,UsedUtc,IsActive) VALUES($Hash,$Kind,$PackageHash,$PackageVersion,$User,$Now,$Expires,'',1);";
        command.Parameters.AddWithValue("$Hash", HashOpaqueToken(token)); command.Parameters.AddWithValue("$Kind", packageKind.Trim());
        command.Parameters.AddWithValue("$PackageHash", packageSha256.Trim()); command.Parameters.AddWithValue("$PackageVersion", packageVersion.Trim());
        command.Parameters.AddWithValue("$User", createdBy.Trim()); command.Parameters.AddWithValue("$Now", now.ToString("O")); command.Parameters.AddWithValue("$Expires", now.Add(lifetime).ToString("O"));
        command.ExecuteNonQuery(); return token;
    }

    public DistributionTicketRecord? ConsumeDistributionTicket(string rawToken, string packageKind)
    {
        var now = DateTime.UtcNow;
        using var connection = OpenConnection(); using var transaction = connection.BeginTransaction();
        using var select = connection.CreateCommand(); select.Transaction = transaction;
        select.CommandText = "SELECT Id,TicketHash,PackageKind,PackageSha256,PackageVersion,CreatedBy,CreatedUtc,ExpiresUtc,UsedUtc,IsActive FROM DistributionTickets WHERE TicketHash=$Hash COLLATE NOCASE AND PackageKind=$Kind COLLATE NOCASE LIMIT 1;";
        select.Parameters.AddWithValue("$Hash", HashOpaqueToken(rawToken)); select.Parameters.AddWithValue("$Kind", packageKind.Trim());
        using var reader = select.ExecuteReader();
        if (!reader.Read()) { transaction.Rollback(); return null; }
        var record = new DistributionTicketRecord
        {
            Id = reader.GetInt64(0), TicketHash = reader.GetString(1), PackageKind = reader.GetString(2), PackageSha256 = reader.GetString(3), PackageVersion = reader.GetString(4), CreatedBy = reader.GetString(5),
            CreatedUtc = ParseDate(reader.GetString(6)), ExpiresUtc = ParseDate(reader.GetString(7)), UsedUtc = ParseNullableDate(reader.GetString(8)), IsActive = reader.GetInt32(9) == 1
        };
        reader.Close();
        if (!record.IsActive || record.UsedUtc.HasValue || record.ExpiresUtc <= now) { transaction.Rollback(); return null; }
        using var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = "UPDATE DistributionTickets SET IsActive=0,UsedUtc=$Now WHERE Id=$Id;";
        update.Parameters.AddWithValue("$Now", now.ToString("O")); update.Parameters.AddWithValue("$Id", record.Id); update.ExecuteNonQuery();
        transaction.Commit(); record.IsActive = false; record.UsedUtc = now; return record;
    }

    public void SavePackage(ServerPackageRecord package)
    {
        using var connection = OpenConnection(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO ServerPackageRegistry(PackageKind,FilePath,Sha256,Version,UpdatedUtc) VALUES($Kind,$Path,$Hash,$Version,$Utc) ON CONFLICT(PackageKind) DO UPDATE SET FilePath=excluded.FilePath,Sha256=excluded.Sha256,Version=excluded.Version,UpdatedUtc=excluded.UpdatedUtc;";
        command.Parameters.AddWithValue("$Kind", package.PackageKind.Trim()); command.Parameters.AddWithValue("$Path", package.FilePath.Trim());
        command.Parameters.AddWithValue("$Hash", package.Sha256.Trim()); command.Parameters.AddWithValue("$Version", package.Version.Trim()); command.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O")); command.ExecuteNonQuery();
    }

    public void ClearPackage(string packageKind)
    {
        using var connection = OpenConnection(); using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ServerPackageRegistry WHERE PackageKind=$Kind COLLATE NOCASE;";
        command.Parameters.AddWithValue("$Kind", packageKind.Trim()); command.ExecuteNonQuery();
    }

    public ServerPackageRecord? GetPackage(string packageKind)
    {
        using var connection = OpenConnection(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT PackageKind,FilePath,Sha256,Version,UpdatedUtc FROM ServerPackageRegistry WHERE PackageKind=$Kind COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("$Kind", packageKind.Trim()); using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        return new ServerPackageRecord { PackageKind=reader.GetString(0),FilePath=reader.GetString(1),Sha256=reader.GetString(2),Version=reader.GetString(3),UpdatedUtc=ParseDate(reader.GetString(4)) };
    }

    public void SaveArchivedFile(ServerArchivedFileRecord record)
    {
        using var connection = OpenConnection(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO ServerArchivedFiles(ClientId,FinancialYear,Kind,OriginalName,StoredPath,Sha256,FileSize,DeviceId,UserName,CreatedUtc) VALUES($Client,$Fy,$Kind,$Name,$Path,$Hash,$Size,$Device,$User,$Utc);";
        command.Parameters.AddWithValue("$Client", record.ClientId); command.Parameters.AddWithValue("$Fy", record.FinancialYear.Trim()); command.Parameters.AddWithValue("$Kind", record.Kind.Trim());
        command.Parameters.AddWithValue("$Name", record.OriginalName); command.Parameters.AddWithValue("$Path", record.StoredPath); command.Parameters.AddWithValue("$Hash", record.Sha256);
        command.Parameters.AddWithValue("$Size", record.FileSize); command.Parameters.AddWithValue("$Device", record.DeviceId); command.Parameters.AddWithValue("$User", record.UserName); command.Parameters.AddWithValue("$Utc", record.CreatedUtc.ToString("O")); command.ExecuteNonQuery();
    }

    public bool ArchivedHashExists(long clientId, string financialYear, string kind, string sha256)
    {
        using var connection = OpenConnection(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM ServerArchivedFiles WHERE ClientId=$Client AND FinancialYear=$Fy COLLATE NOCASE AND Kind=$Kind COLLATE NOCASE AND Sha256=$Hash COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("$Client", clientId); command.Parameters.AddWithValue("$Fy", financialYear.Trim()); command.Parameters.AddWithValue("$Kind", kind.Trim()); command.Parameters.AddWithValue("$Hash", sha256.Trim());
        return command.ExecuteScalar() is not null;
    }

    public ServerArchivedFileRecord? GetArchivedFile(long clientId, string financialYear, string kind, string sha256)
    {
        using var connection = OpenConnection(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,ClientId,FinancialYear,Kind,OriginalName,StoredPath,Sha256,FileSize,DeviceId,UserName,CreatedUtc FROM ServerArchivedFiles WHERE ClientId=$Client AND FinancialYear=$Fy COLLATE NOCASE AND Kind=$Kind COLLATE NOCASE AND Sha256=$Hash COLLATE NOCASE ORDER BY Id DESC LIMIT 1;";
        command.Parameters.AddWithValue("$Client", clientId); command.Parameters.AddWithValue("$Fy", financialYear.Trim()); command.Parameters.AddWithValue("$Kind", kind.Trim()); command.Parameters.AddWithValue("$Hash", sha256.Trim());
        using var reader=command.ExecuteReader();if(!reader.Read())return null;
        return new ServerArchivedFileRecord{Id=reader.GetInt64(0),ClientId=reader.GetInt64(1),FinancialYear=reader.GetString(2),Kind=reader.GetString(3),OriginalName=reader.GetString(4),StoredPath=reader.GetString(5),Sha256=reader.GetString(6),FileSize=reader.GetInt64(7),DeviceId=reader.GetString(8),UserName=reader.GetString(9),CreatedUtc=ParseDate(reader.GetString(10))};
    }

    public IReadOnlyList<ServerSecurityEventRecord> GetRecentSecurityEvents(int limit = 250)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id,EventType,UserName,DeviceId,Detail,Success,CreatedUtc FROM ServerSecurityEvents ORDER BY Id DESC LIMIT $Limit;";
        command.Parameters.AddWithValue("$Limit", Math.Clamp(limit, 1, 5000));
        using var reader = command.ExecuteReader();
        var result = new List<ServerSecurityEventRecord>();
        while (reader.Read())
        {
            result.Add(new ServerSecurityEventRecord
            {
                Id = reader.GetInt64(0), EventType = reader.GetString(1), UserName = reader.GetString(2), DeviceId = reader.GetString(3),
                Detail = reader.GetString(4), Success = reader.GetInt32(5) == 1, CreatedUtc = ParseDate(reader.GetString(6))
            });
        }
        return result;
    }

    public ServerSecurityEventRecord? GetLatestSecurityEvent(string eventType)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,EventType,UserName,DeviceId,Detail,Success,CreatedUtc
            FROM ServerSecurityEvents WHERE EventType=$Type COLLATE NOCASE
            ORDER BY Id DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$Type", eventType.Trim());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new ServerSecurityEventRecord
        {
            Id=reader.GetInt64(0),EventType=reader.GetString(1),UserName=reader.GetString(2),DeviceId=reader.GetString(3),Detail=reader.GetString(4),
            Success=reader.GetInt32(5)==1,CreatedUtc=ParseDate(reader.GetString(6))
        };
    }

    public void LogEvent(string eventType, string userName, string deviceId, string detail, bool success)
    {
        using var connection = OpenConnection(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO ServerSecurityEvents(EventType,UserName,DeviceId,Detail,Success,CreatedUtc) VALUES($Type,$User,$Device,$Detail,$Success,$Utc);";
        command.Parameters.AddWithValue("$Type", eventType.Trim()); command.Parameters.AddWithValue("$User", userName?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$Device", deviceId?.Trim() ?? string.Empty); command.Parameters.AddWithValue("$Detail", detail?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$Success", success ? 1 : 0); command.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O")); command.ExecuteNonQuery();
    }

    public static string HashOpaqueToken(string rawToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(rawToken.Trim());
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    public static string NewToken()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    }

    private const string DeviceSelect =
        "SELECT Id,DeviceId,DeviceName,TokenHash,RoleName,ClientId,FinancialYear,IsActive,CreatedUtc,UpdatedUtc,LastSeenUtc,DeviceType,UserName,AppVersion,EnrollmentState,LastValidatedUtc,RegistrationOtpVerifiedUtc,MobileMfaVerifiedUtc,ExpiredUtc,DeviceBindingKey FROM LanDevices";

    private static ServerDeviceRecord ReadDevice(SqliteDataReader reader) => new()
    {
        Id=reader.GetInt64(0),DeviceId=reader.GetString(1),DeviceName=reader.GetString(2),TokenHash=reader.GetString(3),RoleName=reader.GetString(4),
        ClientId=reader.GetInt64(5),FinancialYear=reader.GetString(6),IsActive=reader.GetInt32(7)==1,CreatedUtc=ParseDate(reader.GetString(8)),UpdatedUtc=ParseDate(reader.GetString(9)),
        LastSeenUtc=ParseNullableDate(reader.GetString(10)),DeviceType=reader.GetString(11),UserName=reader.GetString(12),AppVersion=reader.GetString(13),EnrollmentState=reader.GetString(14),
        LastValidatedUtc=ParseNullableDate(reader.GetString(15)),RegistrationOtpVerifiedUtc=ParseNullableDate(reader.GetString(16)),MobileMfaVerifiedUtc=ParseNullableDate(reader.GetString(17)),ExpiredUtc=ParseNullableDate(reader.GetString(18)),DeviceBindingKey=reader.GetString(19)
    };


    private static NetworkLoginChallengeRecord ReadLoginChallenge(SqliteDataReader reader) => new()
    {
        ChallengeId=reader.GetString(0),UserName=reader.GetString(1),DeviceId=reader.GetString(2),DeviceName=reader.GetString(3),NetworkScope=reader.GetString(4),Code=reader.GetString(5),
        CreatedUtc=ParseDate(reader.GetString(6)),ExpiresUtc=ParseDate(reader.GetString(7)),UsedUtc=ParseNullableDate(reader.GetString(8)),IsActive=reader.GetInt32(9)==1
    };

    private static FolderLinkRecord ReadFolderLink(SqliteDataReader reader) => new()
    {
        Id=reader.GetInt64(0),LinkId=reader.GetString(1),DeviceId=reader.GetString(2),UserName=reader.GetString(3),ClientId=reader.GetInt64(4),FinancialYear=reader.GetString(5),
        TokenHash=reader.GetString(6),LocalLabel=reader.GetString(7),IsActive=reader.GetInt32(8)==1,CreatedUtc=ParseDate(reader.GetString(9)),UpdatedUtc=ParseDate(reader.GetString(10)),
        LastSeenUtc=ParseNullableDate(reader.GetString(11)),ExpiredUtc=ParseNullableDate(reader.GetString(12))
    };

    private static bool EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var check = connection.CreateCommand(); check.CommandText = $"PRAGMA table_info([{table}]);";
        using var reader = check.ExecuteReader(); while (reader.Read()) if (reader.GetString(1).Equals(column,StringComparison.OrdinalIgnoreCase)) return false;
        reader.Close(); using var alter = connection.CreateCommand(); alter.CommandText = $"ALTER TABLE [{table}] ADD COLUMN [{column}] {definition};"; alter.ExecuteNonQuery(); return true;
    }

    private SqliteConnection OpenConnection()
    {
        return SqliteConnectionPolicy.Open(_connectionString);
    }

    private static DateTime ParseDate(string value) => DateTime.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var parsed) ? parsed : DateTime.MinValue;
    private static DateTime? ParseNullableDate(string value) => string.IsNullOrWhiteSpace(value) ? null : ParseDate(value);
}
