using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Keeps the disposable LAN SQLite copy aligned with the authenticated Main Server identity.
/// The authoritative server user/security tables are never copied to the LAN machine; only the
/// current Windows user's effective network identity and permission rows are projected locally.
/// </summary>
public static class NetworkLocalIdentityService
{
    public static void ApplyToDisposableSnapshot()
    {
        if (!NetworkClientRuntime.IsNetworkClient || string.IsNullOrWhiteSpace(NetworkClientRuntime.RoleName)) return;
        var path = ApplicationDataPaths.DatabasePath;
        if (!File.Exists(path)) return;

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        using (var connection = new SqliteConnection(builder.ToString()))
        {
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys=OFF; PRAGMA busy_timeout=5000;";
                pragma.ExecuteNonQuery();
            }

            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction,
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
                CREATE TABLE IF NOT EXISTS RolePermissions
                (
                    RoleName        TEXT NOT NULL COLLATE NOCASE,
                    PermissionName  TEXT NOT NULL COLLATE NOCASE,
                    IsAllowed       INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY(RoleName, PermissionName)
                );
                """);

            Execute(connection, transaction, "DELETE FROM AppUsers;");
            Execute(connection, transaction, "DELETE FROM RolePermissions;");

            using (var user = connection.CreateCommand())
            {
                user.Transaction = transaction;
                user.CommandText =
                    """
                    INSERT INTO AppUsers(WindowsUserName,DisplayName,RoleName,IsActive,CreatedUtc,UpdatedUtc,LastSeenUtc)
                    VALUES($Windows,$Display,$Role,1,$Now,$Now,$Now);
                    """;
                user.Parameters.AddWithValue("$Windows", Environment.UserName.Trim());
                user.Parameters.AddWithValue("$Display", string.IsNullOrWhiteSpace(NetworkClientRuntime.DisplayName) ? NetworkClientRuntime.UserName : NetworkClientRuntime.DisplayName);
                user.Parameters.AddWithValue("$Role", NetworkClientRuntime.RoleName);
                user.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O"));
                user.ExecuteNonQuery();
            }

            foreach (var permission in NetworkClientRuntime.Permissions)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT OR REPLACE INTO RolePermissions(RoleName,PermissionName,IsAllowed) VALUES($Role,$Permission,1);";
                command.Parameters.AddWithValue("$Role", NetworkClientRuntime.RoleName);
                command.Parameters.AddWithValue("$Permission", permission);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        SqliteConnection.ClearAllPools();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
