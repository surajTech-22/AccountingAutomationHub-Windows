using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Governance;

/// <summary>
/// Main-Server control-plane state. This deliberately stores no accounting rows or shared file bodies.
/// </summary>
public sealed class EnterpriseControlRepository
{
    private readonly string _connectionString;

    public EnterpriseControlRepository()
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

        SqliteSchemaInitializationGate.RunOnce(nameof(EnterpriseControlRepository), _connectionString, InitializeCore);


    private void InitializeCore()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS EnterprisePolicyRevisions
            (
                Id INTEGER PRIMARY KEY CHECK(Id=1),
                AuthorizationRevision INTEGER NOT NULL DEFAULT 1,
                DevicePolicyRevision INTEGER NOT NULL DEFAULT 1,
                ClientAccessRevision INTEGER NOT NULL DEFAULT 1,
                SecurityPolicyRevision INTEGER NOT NULL DEFAULT 1,
                UpdatedUtc TEXT NOT NULL
            );
            INSERT OR IGNORE INTO EnterprisePolicyRevisions(Id,AuthorizationRevision,DevicePolicyRevision,ClientAccessRevision,SecurityPolicyRevision,UpdatedUtc)
            VALUES(1,1,1,1,1,$Now);

            CREATE TABLE IF NOT EXISTS DeploymentRings
            (
                RingName TEXT PRIMARY KEY COLLATE NOCASE,
                IsEnabled INTEGER NOT NULL DEFAULT 1,
                ForceAfterUtc TEXT NOT NULL DEFAULT '',
                UpdatedBy TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                UpdatedUtc TEXT NOT NULL
            );
            INSERT OR IGNORE INTO DeploymentRings(RingName,IsEnabled,ForceAfterUtc,UpdatedBy,UpdatedUtc) VALUES('Test',1,'','System',$Now);
            INSERT OR IGNORE INTO DeploymentRings(RingName,IsEnabled,ForceAfterUtc,UpdatedBy,UpdatedUtc) VALUES('Pilot',1,'','System',$Now);
            INSERT OR IGNORE INTO DeploymentRings(RingName,IsEnabled,ForceAfterUtc,UpdatedBy,UpdatedUtc) VALUES('Production',1,'','System',$Now);
            CREATE TABLE IF NOT EXISTS DeviceUpdateRings
            (
                DeviceId TEXT PRIMARY KEY COLLATE NOCASE,
                RingName TEXT NOT NULL DEFAULT 'Production' COLLATE NOCASE,
                UpdatedUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS DevicePresence
            (
                DeviceId TEXT PRIMARY KEY COLLATE NOCASE,
                UserName TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                ClientId INTEGER NOT NULL DEFAULT 0,
                FinancialYear TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                Module TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                AppVersion TEXT NOT NULL DEFAULT '',
                LastSeenUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_DevicePresence_User ON DevicePresence(UserName COLLATE NOCASE,LastSeenUtc DESC);

            CREATE TABLE IF NOT EXISTS DevicePosture
            (
                DeviceId TEXT PRIMARY KEY COLLATE NOCASE,
                HealthState TEXT NOT NULL DEFAULT 'Healthy',
                RiskScore INTEGER NOT NULL DEFAULT 0,
                RiskReasons TEXT NOT NULL DEFAULT '',
                AppVersionCurrent INTEGER NOT NULL DEFAULT 1,
                RegistrationTrusted INTEGER NOT NULL DEFAULT 1,
                CertificateValid INTEGER NOT NULL DEFAULT 1,
                SnapshotHealthy INTEGER NOT NULL DEFAULT 1,
                IsQuarantined INTEGER NOT NULL DEFAULT 0,
                QuarantineReason TEXT NOT NULL DEFAULT '',
                EvaluatedUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS DeviceTags
            (
                DeviceId TEXT NOT NULL COLLATE NOCASE,
                Tag TEXT NOT NULL COLLATE NOCASE,
                UpdatedUtc TEXT NOT NULL,
                PRIMARY KEY(DeviceId,Tag)
            );

            CREATE TABLE IF NOT EXISTS EnterpriseSecurityState
            (
                Id INTEGER PRIMARY KEY CHECK(Id=1),
                EmergencyLockdown INTEGER NOT NULL DEFAULT 0,
                LockdownReason TEXT NOT NULL DEFAULT '',
                ChangedBy TEXT NOT NULL DEFAULT '',
                ChangedUtc TEXT NOT NULL
            );
            INSERT OR IGNORE INTO EnterpriseSecurityState(Id,EmergencyLockdown,LockdownReason,ChangedBy,ChangedUtc)
            VALUES(1,0,'','',$Now);

            CREATE TABLE IF NOT EXISTS RemoteAahCommands
            (
                CommandId TEXT PRIMARY KEY COLLATE NOCASE,
                DeviceId TEXT NOT NULL COLLATE NOCASE,
                Action TEXT NOT NULL COLLATE NOCASE,
                ArgumentsJson TEXT NOT NULL DEFAULT '{}',
                RequestedBy TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                RequiredPermission TEXT NOT NULL DEFAULT 'RemoteAah' COLLATE NOCASE,
                State TEXT NOT NULL DEFAULT 'Pending' COLLATE NOCASE,
                ResultDetail TEXT NOT NULL DEFAULT '',
                CreatedUtc TEXT NOT NULL,
                ExpiresUtc TEXT NOT NULL,
                DeliveredUtc TEXT NOT NULL DEFAULT '',
                CompletedUtc TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS IX_RemoteAahCommands_DeviceState ON RemoteAahCommands(DeviceId COLLATE NOCASE,State COLLATE NOCASE,CreatedUtc);

            CREATE TABLE IF NOT EXISTS RemoteAssistSessions
            (
                SessionId TEXT PRIMARY KEY COLLATE NOCASE,
                DeviceId TEXT NOT NULL COLLATE NOCASE,
                RemoteUserName TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                HelperUserName TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                PermissionMode TEXT NOT NULL DEFAULT 'Remote User Permissions',
                GrantedPermissionsJson TEXT NOT NULL DEFAULT '[]',
                Scope TEXT NOT NULL DEFAULT 'AAH Only',
                Reason TEXT NOT NULL DEFAULT '',
                UserConsented INTEGER NOT NULL DEFAULT 0,
                IsActive INTEGER NOT NULL DEFAULT 1,
                CreatedUtc TEXT NOT NULL,
                ExpiresUtc TEXT NOT NULL,
                EndedUtc TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS IX_RemoteAssistSessions_DeviceActive ON RemoteAssistSessions(DeviceId COLLATE NOCASE,IsActive,ExpiresUtc);

            CREATE TABLE IF NOT EXISTS RemoteAssistDevicePolicies
            (
                DeviceId TEXT PRIMARY KEY COLLATE NOCASE,
                AutoAcceptAdministratorAssist INTEGER NOT NULL DEFAULT 0,
                UpdatedBy TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                UpdatedUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS DeviceGroups
            (
                GroupName TEXT PRIMARY KEY COLLATE NOCASE,
                Description TEXT NOT NULL DEFAULT '',
                UpdatedUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS DeviceGroupMembers
            (
                GroupName TEXT NOT NULL COLLATE NOCASE,
                DeviceId TEXT NOT NULL COLLATE NOCASE,
                UpdatedUtc TEXT NOT NULL,
                PRIMARY KEY(GroupName,DeviceId)
            );

            CREATE TABLE IF NOT EXISTS AdminAuditChain
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ActorType TEXT NOT NULL DEFAULT 'User',
                UserName TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                DeviceId TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                Action TEXT NOT NULL DEFAULT '',
                Resource TEXT NOT NULL DEFAULT '',
                Detail TEXT NOT NULL DEFAULT '',
                ReasonCode TEXT NOT NULL DEFAULT '',
                Success INTEGER NOT NULL DEFAULT 0,
                PreviousHash TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                RecordHash TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                CreatedUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_AdminAuditChain_Utc ON AdminAuditChain(CreatedUtc DESC);
            """;
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public SecurityPolicySnapshot GetRevisions()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT AuthorizationRevision,DevicePolicyRevision,ClientAccessRevision,SecurityPolicyRevision,UpdatedUtc FROM EnterprisePolicyRevisions WHERE Id=1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new SecurityPolicySnapshot { UpdatedUtc = DateTime.UtcNow };
        return new SecurityPolicySnapshot
        {
            AuthorizationRevision=reader.GetInt64(0),DevicePolicyRevision=reader.GetInt64(1),ClientAccessRevision=reader.GetInt64(2),SecurityPolicyRevision=reader.GetInt64(3),UpdatedUtc=ParseDate(reader.GetString(4))
        };
    }

    public void BumpRevision(string kind)
    {
        var column = kind.ToLowerInvariant() switch
        {
            "authorization" => "AuthorizationRevision",
            "device" => "DevicePolicyRevision",
            "client" => "ClientAccessRevision",
            _ => "SecurityPolicyRevision"
        };
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE EnterprisePolicyRevisions SET {column}={column}+1,UpdatedUtc=$Utc WHERE Id=1;";
        command.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<DeploymentRingRecord> GetDeploymentRings()
    {
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="SELECT RingName,IsEnabled,ForceAfterUtc,UpdatedBy,UpdatedUtc FROM DeploymentRings ORDER BY CASE RingName WHEN 'Test' THEN 1 WHEN 'Pilot' THEN 2 WHEN 'Production' THEN 3 ELSE 4 END,RingName COLLATE NOCASE;";using var reader=command.ExecuteReader();var rows=new List<DeploymentRingRecord>();while(reader.Read())rows.Add(new DeploymentRingRecord{RingName=reader.GetString(0),IsEnabled=reader.GetInt32(1)==1,ForceAfterUtc=ParseNullable(reader.GetString(2)),UpdatedBy=reader.GetString(3),UpdatedUtc=ParseDate(reader.GetString(4))});return rows;
    }

    public DeploymentRingRecord GetDeploymentRing(string ringName)
    {
        return GetDeploymentRings().FirstOrDefault(x=>x.RingName.Equals(ringName,StringComparison.OrdinalIgnoreCase))??new DeploymentRingRecord{RingName=string.IsNullOrWhiteSpace(ringName)?"Production":ringName.Trim(),IsEnabled=true,UpdatedUtc=DateTime.UtcNow};
    }

    public void SaveDeploymentRing(DeploymentRingRecord ring)
    {
        if(string.IsNullOrWhiteSpace(ring.RingName))throw new InvalidOperationException("Deployment ring name is required.");ring.UpdatedUtc=DateTime.UtcNow;using var connection=Open();using var command=connection.CreateCommand();command.CommandText="INSERT INTO DeploymentRings(RingName,IsEnabled,ForceAfterUtc,UpdatedBy,UpdatedUtc) VALUES($Ring,$Enabled,$Force,$By,$Now) ON CONFLICT(RingName) DO UPDATE SET IsEnabled=excluded.IsEnabled,ForceAfterUtc=excluded.ForceAfterUtc,UpdatedBy=excluded.UpdatedBy,UpdatedUtc=excluded.UpdatedUtc;";command.Parameters.AddWithValue("$Ring",ring.RingName.Trim());command.Parameters.AddWithValue("$Enabled",ring.IsEnabled?1:0);command.Parameters.AddWithValue("$Force",ring.ForceAfterUtc?.ToUniversalTime().ToString("O")??string.Empty);command.Parameters.AddWithValue("$By",ring.UpdatedBy?.Trim()??string.Empty);command.Parameters.AddWithValue("$Now",ring.UpdatedUtc.ToString("O"));command.ExecuteNonQuery();BumpRevision("device");
    }

    public void SetDeviceUpdateRing(string deviceId,string ringName)
    {
        if(string.IsNullOrWhiteSpace(deviceId))throw new InvalidOperationException("Device is required.");
        var ring=GetDeploymentRings().FirstOrDefault(x=>x.RingName.Equals(ringName?.Trim(),StringComparison.OrdinalIgnoreCase))??throw new InvalidOperationException("Choose an existing deployment ring.");
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="INSERT INTO DeviceUpdateRings(DeviceId,RingName,UpdatedUtc) VALUES($Device,$Ring,$Now) ON CONFLICT(DeviceId) DO UPDATE SET RingName=excluded.RingName,UpdatedUtc=excluded.UpdatedUtc;";command.Parameters.AddWithValue("$Device",deviceId.Trim());command.Parameters.AddWithValue("$Ring",ring.RingName);command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));command.ExecuteNonQuery();BumpRevision("device");
    }

    public string GetDeviceUpdateRing(string deviceId)
    {
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="SELECT RingName FROM DeviceUpdateRings WHERE DeviceId=$Device COLLATE NOCASE LIMIT 1;";command.Parameters.AddWithValue("$Device",deviceId.Trim());var value=Convert.ToString(command.ExecuteScalar())?.Trim()??string.Empty;return value.Length>0?value:"Production";
    }

    public void TouchDevicePresence(DevicePresenceRecord record)
    {
        record.LastSeenUtc=record.LastSeenUtc==default?DateTime.UtcNow:record.LastSeenUtc.ToUniversalTime();
        using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="INSERT INTO DevicePresence(DeviceId,UserName,ClientId,FinancialYear,Module,AppVersion,LastSeenUtc) VALUES($Device,$User,$Client,$Fy,$Module,$Version,$Now) ON CONFLICT(DeviceId) DO UPDATE SET UserName=excluded.UserName,ClientId=excluded.ClientId,FinancialYear=excluded.FinancialYear,Module=excluded.Module,AppVersion=excluded.AppVersion,LastSeenUtc=excluded.LastSeenUtc;";
        command.Parameters.AddWithValue("$Device",record.DeviceId.Trim());command.Parameters.AddWithValue("$User",record.UserName.Trim());command.Parameters.AddWithValue("$Client",record.ClientId);command.Parameters.AddWithValue("$Fy",record.FinancialYear.Trim());command.Parameters.AddWithValue("$Module",record.Module.Trim());command.Parameters.AddWithValue("$Version",record.AppVersion.Trim());command.Parameters.AddWithValue("$Now",record.LastSeenUtc.ToString("O"));command.ExecuteNonQuery();
    }

    public IReadOnlyList<DevicePresenceRecord> GetDevicePresence()
    {
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="SELECT DeviceId,UserName,ClientId,FinancialYear,Module,AppVersion,LastSeenUtc FROM DevicePresence ORDER BY LastSeenUtc DESC;";using var reader=command.ExecuteReader();var rows=new List<DevicePresenceRecord>();while(reader.Read())rows.Add(new DevicePresenceRecord{DeviceId=reader.GetString(0),UserName=reader.GetString(1),ClientId=reader.GetInt64(2),FinancialYear=reader.GetString(3),Module=reader.GetString(4),AppVersion=reader.GetString(5),LastSeenUtc=ParseDate(reader.GetString(6))});return rows;
    }

    public void DeleteDevicePresence(string deviceId)
    {
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="DELETE FROM DevicePresence WHERE DeviceId=$Device COLLATE NOCASE;";command.Parameters.AddWithValue("$Device",deviceId.Trim());command.ExecuteNonQuery();
    }

    public DevicePostureRecord GetPosture(string deviceId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DeviceId,HealthState,RiskScore,RiskReasons,AppVersionCurrent,RegistrationTrusted,CertificateValid,SnapshotHealthy,IsQuarantined,QuarantineReason,EvaluatedUtc FROM DevicePosture WHERE DeviceId=$Id COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("$Id", deviceId.Trim());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new DevicePostureRecord { DeviceId=deviceId.Trim(), EvaluatedUtc=DateTime.UtcNow };
        return ReadPosture(reader);
    }

    public void SavePosture(DevicePostureRecord record)
    {
        record.EvaluatedUtc = record.EvaluatedUtc == default ? DateTime.UtcNow : record.EvaluatedUtc;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO DevicePosture(DeviceId,HealthState,RiskScore,RiskReasons,AppVersionCurrent,RegistrationTrusted,CertificateValid,SnapshotHealthy,IsQuarantined,QuarantineReason,EvaluatedUtc)
            VALUES($Id,$State,$Risk,$Reasons,$Version,$Trust,$Certificate,$Snapshot,$Quarantine,$QuarantineReason,$Utc)
            ON CONFLICT(DeviceId) DO UPDATE SET HealthState=excluded.HealthState,RiskScore=excluded.RiskScore,RiskReasons=excluded.RiskReasons,
            AppVersionCurrent=excluded.AppVersionCurrent,RegistrationTrusted=excluded.RegistrationTrusted,CertificateValid=excluded.CertificateValid,
            SnapshotHealthy=excluded.SnapshotHealthy,IsQuarantined=excluded.IsQuarantined,QuarantineReason=excluded.QuarantineReason,EvaluatedUtc=excluded.EvaluatedUtc;
            """;
        command.Parameters.AddWithValue("$Id", record.DeviceId.Trim());
        command.Parameters.AddWithValue("$State", record.HealthState);
        command.Parameters.AddWithValue("$Risk", Math.Clamp(record.RiskScore,0,100));
        command.Parameters.AddWithValue("$Reasons", record.RiskReasons);
        command.Parameters.AddWithValue("$Version", record.AppVersionCurrent?1:0);
        command.Parameters.AddWithValue("$Trust", record.RegistrationTrusted?1:0);
        command.Parameters.AddWithValue("$Certificate", record.CertificateValid?1:0);
        command.Parameters.AddWithValue("$Snapshot", record.SnapshotHealthy?1:0);
        command.Parameters.AddWithValue("$Quarantine", record.IsQuarantined?1:0);
        command.Parameters.AddWithValue("$QuarantineReason", record.QuarantineReason);
        command.Parameters.AddWithValue("$Utc", record.EvaluatedUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<DeviceTagRecord> GetDeviceTags(string deviceId)
    {
        using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="SELECT DeviceId,Tag,UpdatedUtc FROM DeviceTags WHERE DeviceId=$Id COLLATE NOCASE ORDER BY Tag COLLATE NOCASE;";
        command.Parameters.AddWithValue("$Id",deviceId.Trim());using var reader=command.ExecuteReader();var list=new List<DeviceTagRecord>();
        while(reader.Read()) list.Add(new DeviceTagRecord{DeviceId=reader.GetString(0),Tag=reader.GetString(1),UpdatedUtc=ParseDate(reader.GetString(2))});
        return list;
    }

    public void SetDeviceTag(string deviceId,string tag,bool enabled)
    {
        if(string.IsNullOrWhiteSpace(deviceId)||string.IsNullOrWhiteSpace(tag)) throw new InvalidOperationException("Device and tag are required.");
        tag=tag.Trim();if(tag.Length>64)tag=tag[..64];
        using var connection=Open();using var command=connection.CreateCommand();
        if(enabled)
        {
            command.CommandText="INSERT INTO DeviceTags(DeviceId,Tag,UpdatedUtc) VALUES($Id,$Tag,$Utc) ON CONFLICT(DeviceId,Tag) DO UPDATE SET UpdatedUtc=excluded.UpdatedUtc;";
            command.Parameters.AddWithValue("$Utc",DateTime.UtcNow.ToString("O"));
        }
        else command.CommandText="DELETE FROM DeviceTags WHERE DeviceId=$Id COLLATE NOCASE AND Tag=$Tag COLLATE NOCASE;";
        command.Parameters.AddWithValue("$Id",deviceId.Trim());command.Parameters.AddWithValue("$Tag",tag);command.ExecuteNonQuery();BumpRevision("device");
    }

    public EmergencyLockdownRecord GetEmergencyLockdown()
    {
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="SELECT EmergencyLockdown,LockdownReason,ChangedBy,ChangedUtc FROM EnterpriseSecurityState WHERE Id=1;";using var reader=command.ExecuteReader();
        if(!reader.Read()) return new EmergencyLockdownRecord{ChangedUtc=DateTime.UtcNow};
        return new EmergencyLockdownRecord{IsActive=reader.GetInt32(0)==1,Reason=reader.GetString(1),ChangedBy=reader.GetString(2),ChangedUtc=ParseDate(reader.GetString(3))};
    }

    public void SetEmergencyLockdown(bool active,string reason,string changedBy)
    {
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="UPDATE EnterpriseSecurityState SET EmergencyLockdown=$Active,LockdownReason=$Reason,ChangedBy=$User,ChangedUtc=$Utc WHERE Id=1;";
        command.Parameters.AddWithValue("$Active",active?1:0);command.Parameters.AddWithValue("$Reason",reason?.Trim()??string.Empty);command.Parameters.AddWithValue("$User",changedBy?.Trim()??string.Empty);command.Parameters.AddWithValue("$Utc",DateTime.UtcNow.ToString("O"));command.ExecuteNonQuery();BumpRevision("security");
    }

    public RemoteAahCommandRecord QueueRemoteCommand(RemoteAahCommandRecord record)
    {
        if (!RemoteAahActions.Allowed.Contains(record.Action)) throw new InvalidOperationException($"Remote AAH action '{record.Action}' is not allow-listed.");
        record.CommandId = string.IsNullOrWhiteSpace(record.CommandId) ? Guid.NewGuid().ToString("N") : record.CommandId.Trim();
        record.CreatedUtc = record.CreatedUtc == default ? DateTime.UtcNow : record.CreatedUtc;
        record.ExpiresUtc = record.ExpiresUtc == default ? record.CreatedUtc.AddMinutes(10) : record.ExpiresUtc;
        record.State = RemoteAahCommandStates.Pending;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO RemoteAahCommands(CommandId,DeviceId,Action,ArgumentsJson,RequestedBy,RequiredPermission,State,ResultDetail,CreatedUtc,ExpiresUtc,DeliveredUtc,CompletedUtc)
            VALUES($Id,$Device,$Action,$Args,$User,$Permission,$State,'',$Created,$Expires,'','');
            """;
        command.Parameters.AddWithValue("$Id", record.CommandId);
        command.Parameters.AddWithValue("$Device", record.DeviceId.Trim());
        command.Parameters.AddWithValue("$Action", record.Action.Trim());
        command.Parameters.AddWithValue("$Args", string.IsNullOrWhiteSpace(record.ArgumentsJson)?"{}":record.ArgumentsJson);
        command.Parameters.AddWithValue("$User", record.RequestedBy.Trim());
        command.Parameters.AddWithValue("$Permission", record.RequiredPermission.Trim());
        command.Parameters.AddWithValue("$State", record.State);
        command.Parameters.AddWithValue("$Created", record.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$Expires", record.ExpiresUtc.ToString("O"));
        command.ExecuteNonQuery();
        return record;
    }

    public IReadOnlyList<RemoteAahCommandRecord> GetPendingRemoteCommands(string deviceId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var expire = connection.CreateCommand())
        {
            expire.Transaction=transaction;
            expire.CommandText="UPDATE RemoteAahCommands SET State='Expired',CompletedUtc=$Now WHERE DeviceId=$Device COLLATE NOCASE AND State IN ('Pending','Delivered','Running') AND ExpiresUtc<=$Now;";
            expire.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));expire.Parameters.AddWithValue("$Device",deviceId.Trim());expire.ExecuteNonQuery();
        }
        var list = new List<RemoteAahCommandRecord>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction=transaction;
            select.CommandText="SELECT CommandId,DeviceId,Action,ArgumentsJson,RequestedBy,RequiredPermission,State,ResultDetail,CreatedUtc,ExpiresUtc,DeliveredUtc,CompletedUtc FROM RemoteAahCommands WHERE DeviceId=$Device COLLATE NOCASE AND State IN ('Pending','Delivered') AND ExpiresUtc>$Now ORDER BY CreatedUtc LIMIT 20;";
            select.Parameters.AddWithValue("$Device",deviceId.Trim());select.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));
            using var reader=select.ExecuteReader();while(reader.Read())list.Add(ReadCommand(reader));
        }
        foreach(var item in list)
        {
            using var update=connection.CreateCommand();update.Transaction=transaction;update.CommandText="UPDATE RemoteAahCommands SET State='Delivered',DeliveredUtc=COALESCE(NULLIF(DeliveredUtc,''),$Now) WHERE CommandId=$Id AND State='Pending' COLLATE NOCASE;";update.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));update.Parameters.AddWithValue("$Id",item.CommandId);update.ExecuteNonQuery();item.State=RemoteAahCommandStates.Delivered;item.DeliveredUtc=DateTime.UtcNow;
        }
        transaction.Commit();
        return list;
    }

    public void CompleteRemoteCommand(string deviceId,string commandId,bool success,string detail)
    {
        using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="UPDATE RemoteAahCommands SET State=$State,ResultDetail=$Detail,CompletedUtc=$Now WHERE CommandId=$Id COLLATE NOCASE AND DeviceId=$Device COLLATE NOCASE AND State IN ('Delivered','Running','Pending');";
        command.Parameters.AddWithValue("$State",success?RemoteAahCommandStates.Succeeded:RemoteAahCommandStates.Failed);command.Parameters.AddWithValue("$Detail",detail??string.Empty);command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$Id",commandId.Trim());command.Parameters.AddWithValue("$Device",deviceId.Trim());command.ExecuteNonQuery();
    }

    public RemoteAahCommandRecord? GetRemoteCommand(string commandId)
    {
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="SELECT CommandId,DeviceId,Action,ArgumentsJson,RequestedBy,RequiredPermission,State,ResultDetail,CreatedUtc,ExpiresUtc,DeliveredUtc,CompletedUtc FROM RemoteAahCommands WHERE CommandId=$Id COLLATE NOCASE LIMIT 1;";command.Parameters.AddWithValue("$Id",commandId.Trim());using var reader=command.ExecuteReader();return reader.Read()?ReadCommand(reader):null;
    }

    public IReadOnlyList<RemoteAahCommandRecord> GetRecentRemoteCommands(int limit=200)
    {
        using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="SELECT CommandId,DeviceId,Action,ArgumentsJson,RequestedBy,RequiredPermission,State,ResultDetail,CreatedUtc,ExpiresUtc,DeliveredUtc,CompletedUtc FROM RemoteAahCommands ORDER BY CreatedUtc DESC LIMIT $Limit;";command.Parameters.AddWithValue("$Limit",Math.Clamp(limit,1,1000));using var reader=command.ExecuteReader();var list=new List<RemoteAahCommandRecord>();while(reader.Read())list.Add(ReadCommand(reader));return list;
    }

    public RemoteAssistDevicePolicyRecord GetRemoteAssistDevicePolicy(string deviceId)
    {
        using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="SELECT DeviceId,AutoAcceptAdministratorAssist,UpdatedBy,UpdatedUtc FROM RemoteAssistDevicePolicies WHERE DeviceId=$Device COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("$Device",deviceId.Trim());using var reader=command.ExecuteReader();
        if(!reader.Read())return new RemoteAssistDevicePolicyRecord{DeviceId=deviceId.Trim(),UpdatedUtc=DateTime.UtcNow};
        return new RemoteAssistDevicePolicyRecord{DeviceId=reader.GetString(0),AutoAcceptAdministratorAssist=reader.GetInt32(1)==1,UpdatedBy=reader.GetString(2),UpdatedUtc=ParseDate(reader.GetString(3))};
    }

    public void SaveRemoteAssistDevicePolicy(RemoteAssistDevicePolicyRecord record)
    {
        record.UpdatedUtc=DateTime.UtcNow;using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="INSERT INTO RemoteAssistDevicePolicies(DeviceId,AutoAcceptAdministratorAssist,UpdatedBy,UpdatedUtc) VALUES($Device,$Auto,$By,$Now) ON CONFLICT(DeviceId) DO UPDATE SET AutoAcceptAdministratorAssist=excluded.AutoAcceptAdministratorAssist,UpdatedBy=excluded.UpdatedBy,UpdatedUtc=excluded.UpdatedUtc;";
        command.Parameters.AddWithValue("$Device",record.DeviceId.Trim());command.Parameters.AddWithValue("$Auto",record.AutoAcceptAdministratorAssist?1:0);command.Parameters.AddWithValue("$By",record.UpdatedBy?.Trim()??string.Empty);command.Parameters.AddWithValue("$Now",record.UpdatedUtc.ToString("O"));command.ExecuteNonQuery();BumpRevision("device");
    }

    public RemoteAssistSessionRecord CreateRemoteAssistSession(RemoteAssistSessionRecord record)
    {
        record.SessionId = string.IsNullOrWhiteSpace(record.SessionId) ? Guid.NewGuid().ToString("N") : record.SessionId.Trim();
        record.CreatedUtc = record.CreatedUtc == default ? DateTime.UtcNow : record.CreatedUtc;
        record.ExpiresUtc = record.ExpiresUtc == default ? record.CreatedUtc.AddMinutes(30) : record.ExpiresUtc;
        record.IsActive = true;
        using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="INSERT INTO RemoteAssistSessions(SessionId,DeviceId,RemoteUserName,HelperUserName,PermissionMode,GrantedPermissionsJson,Scope,Reason,UserConsented,IsActive,CreatedUtc,ExpiresUtc,EndedUtc) VALUES($Id,$Device,$Remote,$Helper,$Mode,$Permissions,$Scope,$Reason,$Consent,1,$Created,$Expires,'');";
        command.Parameters.AddWithValue("$Id",record.SessionId);command.Parameters.AddWithValue("$Device",record.DeviceId.Trim());command.Parameters.AddWithValue("$Remote",record.RemoteUserName.Trim());command.Parameters.AddWithValue("$Helper",record.HelperUserName.Trim());command.Parameters.AddWithValue("$Mode",record.PermissionMode.Trim());command.Parameters.AddWithValue("$Permissions",record.GrantedPermissionsJson??"[]");command.Parameters.AddWithValue("$Scope",record.Scope??"AAH Only");command.Parameters.AddWithValue("$Reason",record.Reason??string.Empty);command.Parameters.AddWithValue("$Consent",record.UserConsented?1:0);command.Parameters.AddWithValue("$Created",record.CreatedUtc.ToString("O"));command.Parameters.AddWithValue("$Expires",record.ExpiresUtc.ToString("O"));command.ExecuteNonQuery();
        return record;
    }

    public void SetRemoteAssistConsent(string sessionId,bool consented)
    {
        using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="UPDATE RemoteAssistSessions SET UserConsented=$Consent,IsActive=CASE WHEN $Consent=1 THEN IsActive ELSE 0 END,EndedUtc=CASE WHEN $Consent=1 THEN EndedUtc ELSE $Now END WHERE SessionId=$Id COLLATE NOCASE;";
        command.Parameters.AddWithValue("$Consent",consented?1:0);command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$Id",sessionId.Trim());command.ExecuteNonQuery();
    }

    public void EndRemoteAssistSession(string sessionId)
    {
        using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="UPDATE RemoteAssistSessions SET IsActive=0,EndedUtc=$Now WHERE SessionId=$Id COLLATE NOCASE;";command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$Id",sessionId.Trim());command.ExecuteNonQuery();
    }

    public IReadOnlyList<RemoteAssistSessionRecord> GetRemoteAssistSessions(int limit=200)
    {
        using var connection=Open();using(var expire=connection.CreateCommand()){expire.CommandText="UPDATE RemoteAssistSessions SET IsActive=0,EndedUtc=$Now WHERE IsActive=1 AND ExpiresUtc<=$Now;";expire.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));expire.ExecuteNonQuery();}
        using var command=connection.CreateCommand();command.CommandText="SELECT SessionId,DeviceId,RemoteUserName,HelperUserName,PermissionMode,GrantedPermissionsJson,Scope,Reason,UserConsented,IsActive,CreatedUtc,ExpiresUtc,EndedUtc FROM RemoteAssistSessions ORDER BY CreatedUtc DESC LIMIT $Limit;";command.Parameters.AddWithValue("$Limit",Math.Clamp(limit,1,1000));using var reader=command.ExecuteReader();var list=new List<RemoteAssistSessionRecord>();while(reader.Read())list.Add(new RemoteAssistSessionRecord{SessionId=reader.GetString(0),DeviceId=reader.GetString(1),RemoteUserName=reader.GetString(2),HelperUserName=reader.GetString(3),PermissionMode=reader.GetString(4),GrantedPermissionsJson=reader.GetString(5),Scope=reader.GetString(6),Reason=reader.GetString(7),UserConsented=reader.GetInt32(8)==1,IsActive=reader.GetInt32(9)==1,CreatedUtc=ParseDate(reader.GetString(10)),ExpiresUtc=ParseDate(reader.GetString(11)),EndedUtc=ParseNullable(reader.GetString(12))});return list;
    }

    public void DeleteDeviceState(string deviceId)
    {
        using var connection=Open();using var transaction=connection.BeginTransaction();
        foreach(var sql in new[]{"DELETE FROM DeviceUpdateRings WHERE DeviceId=$Device COLLATE NOCASE;","DELETE FROM DevicePresence WHERE DeviceId=$Device COLLATE NOCASE;","DELETE FROM DevicePosture WHERE DeviceId=$Device COLLATE NOCASE;","DELETE FROM DeviceTags WHERE DeviceId=$Device COLLATE NOCASE;","DELETE FROM RemoteAssistDevicePolicies WHERE DeviceId=$Device COLLATE NOCASE;","DELETE FROM DeviceGroupMembers WHERE DeviceId=$Device COLLATE NOCASE;","UPDATE RemoteAahCommands SET State='Rejected',ResultDetail='Device registration permanently deleted',CompletedUtc=$Now WHERE DeviceId=$Device COLLATE NOCASE AND State IN ('Pending','Delivered','Running');","UPDATE RemoteAssistSessions SET IsActive=0,EndedUtc=$Now WHERE DeviceId=$Device COLLATE NOCASE AND IsActive=1;"}){using var c=connection.CreateCommand();c.Transaction=transaction;c.CommandText=sql;c.Parameters.AddWithValue("$Device",deviceId.Trim());c.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));c.ExecuteNonQuery();}
        transaction.Commit();BumpRevision("device");
    }

    public IReadOnlyList<AdminAuditRecord> GetAdminAudit(int limit=500)
    {
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="SELECT Id,ActorType,UserName,DeviceId,Action,Resource,Detail,ReasonCode,Success,PreviousHash,RecordHash,CreatedUtc FROM AdminAuditChain ORDER BY Id DESC LIMIT $Limit;";command.Parameters.AddWithValue("$Limit",Math.Clamp(limit,1,5000));using var reader=command.ExecuteReader();var list=new List<AdminAuditRecord>();while(reader.Read())list.Add(new AdminAuditRecord{Id=reader.GetInt64(0),ActorType=reader.GetString(1),UserName=reader.GetString(2),DeviceId=reader.GetString(3),Action=reader.GetString(4),Resource=reader.GetString(5),Detail=reader.GetString(6),ReasonCode=reader.GetString(7),Success=reader.GetInt32(8)==1,PreviousHash=reader.GetString(9),RecordHash=reader.GetString(10),CreatedUtc=ParseDate(reader.GetString(11))});return list;
    }

    public void SaveDeviceGroup(string groupName,string description)
    {
        if(string.IsNullOrWhiteSpace(groupName))throw new InvalidOperationException("Device group name is required.");using var connection=Open();using var command=connection.CreateCommand();command.CommandText="INSERT INTO DeviceGroups(GroupName,Description,UpdatedUtc) VALUES($Name,$Description,$Now) ON CONFLICT(GroupName) DO UPDATE SET Description=excluded.Description,UpdatedUtc=excluded.UpdatedUtc;";command.Parameters.AddWithValue("$Name",groupName.Trim());command.Parameters.AddWithValue("$Description",description?.Trim()??string.Empty);command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));command.ExecuteNonQuery();BumpRevision("device");
    }

    public void SetDeviceGroupMember(string groupName,string deviceId,bool enabled)
    {
        using var connection=Open();using var command=connection.CreateCommand();if(enabled){command.CommandText="INSERT INTO DeviceGroupMembers(GroupName,DeviceId,UpdatedUtc) VALUES($Group,$Device,$Now) ON CONFLICT(GroupName,DeviceId) DO UPDATE SET UpdatedUtc=excluded.UpdatedUtc;";command.Parameters.AddWithValue("$Now",DateTime.UtcNow.ToString("O"));}else command.CommandText="DELETE FROM DeviceGroupMembers WHERE GroupName=$Group COLLATE NOCASE AND DeviceId=$Device COLLATE NOCASE;";command.Parameters.AddWithValue("$Group",groupName.Trim());command.Parameters.AddWithValue("$Device",deviceId.Trim());command.ExecuteNonQuery();BumpRevision("device");
    }

    public IReadOnlyList<string> GetDeviceGroups()
    {
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="SELECT GroupName FROM DeviceGroups ORDER BY GroupName COLLATE NOCASE;";using var reader=command.ExecuteReader();var list=new List<string>();while(reader.Read())list.Add(reader.GetString(0));return list;
    }

    public IReadOnlyList<string> GetDeviceGroupMembers(string groupName)
    {
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="SELECT DeviceId FROM DeviceGroupMembers WHERE GroupName=$Group COLLATE NOCASE ORDER BY DeviceId COLLATE NOCASE;";command.Parameters.AddWithValue("$Group",groupName.Trim());using var reader=command.ExecuteReader();var list=new List<string>();while(reader.Read())list.Add(reader.GetString(0));return list;
    }

    public string AppendAdminAudit(string actorType,string userName,string deviceId,string action,string resource,string detail,string reasonCode,bool success)
    {
        using var connection=Open();using var transaction=connection.BeginTransaction();
        string previous="";using(var select=connection.CreateCommand()){select.Transaction=transaction;select.CommandText="SELECT RecordHash FROM AdminAuditChain ORDER BY Id DESC LIMIT 1;";previous=Convert.ToString(select.ExecuteScalar())??string.Empty;}
        var utc=DateTime.UtcNow;var material=string.Join("|",previous,actorType,userName,deviceId,action,resource,detail,reasonCode,success?"1":"0",utc.ToString("O"));var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText="INSERT INTO AdminAuditChain(ActorType,UserName,DeviceId,Action,Resource,Detail,ReasonCode,Success,PreviousHash,RecordHash,CreatedUtc) VALUES($Actor,$User,$Device,$Action,$Resource,$Detail,$Reason,$Success,$Previous,$Hash,$Utc);";command.Parameters.AddWithValue("$Actor",actorType);command.Parameters.AddWithValue("$User",userName);command.Parameters.AddWithValue("$Device",deviceId);command.Parameters.AddWithValue("$Action",action);command.Parameters.AddWithValue("$Resource",resource);command.Parameters.AddWithValue("$Detail",detail);command.Parameters.AddWithValue("$Reason",reasonCode);command.Parameters.AddWithValue("$Success",success?1:0);command.Parameters.AddWithValue("$Previous",previous);command.Parameters.AddWithValue("$Hash",hash);command.Parameters.AddWithValue("$Utc",utc.ToString("O"));command.ExecuteNonQuery();}
        transaction.Commit();return hash;
    }

    public bool VerifyAuditChain(out string detail)
    {
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="SELECT Id,ActorType,UserName,DeviceId,Action,Resource,Detail,ReasonCode,Success,PreviousHash,RecordHash,CreatedUtc FROM AdminAuditChain ORDER BY Id;";using var reader=command.ExecuteReader();string previous="";long count=0;while(reader.Read()){var id=reader.GetInt64(0);var utc=ParseDate(reader.GetString(11));var material=string.Join("|",previous,reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetString(7),reader.GetInt32(8)==1?"1":"0",utc.ToString("O"));var expected=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();var storedPrev=reader.GetString(9);var stored=reader.GetString(10);if(!storedPrev.Equals(previous,StringComparison.OrdinalIgnoreCase)||!stored.Equals(expected,StringComparison.OrdinalIgnoreCase)){detail=$"Audit chain failed at record {id}.";return false;}previous=stored;count++;}detail=$"Audit chain verified: {count:N0} record(s).";return true;
    }

    private SqliteConnection Open(){var c=new SqliteConnection(_connectionString);c.Open();using var p=c.CreateCommand();p.CommandText="PRAGMA busy_timeout=8000;";p.ExecuteNonQuery();return c;}
    private static DateTime ParseDate(string v)=>DateTime.TryParse(v,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var d)?d:default;
    private static DateTime? ParseNullable(string v)=>string.IsNullOrWhiteSpace(v)?null:ParseDate(v);
    private static DevicePostureRecord ReadPosture(SqliteDataReader r)=>new(){DeviceId=r.GetString(0),HealthState=r.GetString(1),RiskScore=r.GetInt32(2),RiskReasons=r.GetString(3),AppVersionCurrent=r.GetInt32(4)==1,RegistrationTrusted=r.GetInt32(5)==1,CertificateValid=r.GetInt32(6)==1,SnapshotHealthy=r.GetInt32(7)==1,IsQuarantined=r.GetInt32(8)==1,QuarantineReason=r.GetString(9),EvaluatedUtc=ParseDate(r.GetString(10))};
    private static RemoteAahCommandRecord ReadCommand(SqliteDataReader r)=>new(){CommandId=r.GetString(0),DeviceId=r.GetString(1),Action=r.GetString(2),ArgumentsJson=r.GetString(3),RequestedBy=r.GetString(4),RequiredPermission=r.GetString(5),State=r.GetString(6),ResultDetail=r.GetString(7),CreatedUtc=ParseDate(r.GetString(8)),ExpiresUtc=ParseDate(r.GetString(9)),DeliveredUtc=ParseNullable(r.GetString(10)),CompletedUtc=ParseNullable(r.GetString(11))};
}
