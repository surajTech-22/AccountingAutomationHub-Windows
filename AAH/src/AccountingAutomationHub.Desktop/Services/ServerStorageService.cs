using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Finance;
using AccountingAutomationHub.Infrastructure.Governance;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Main-server retention layer. This is deliberately append/copy based rather than a mirror:
/// deletion of a local source file never deletes a retained server copy.
/// </summary>
public sealed class ServerStorageService
{
    public const string CurrentAndroidReleaseVersion = "4.0.0.398-mobile35";
    private readonly ApplicationPreferenceService _settings;
    private readonly ServerSecurityRepository _security;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _replicationGate = new(1, 1);
    private readonly object _packageCacheGate = new();
    private readonly Dictionary<string, PackageFileCache> _packageCache = new(StringComparer.OrdinalIgnoreCase);

    public ServerStorageService(ApplicationPreferenceService settings, ServerSecurityRepository security)
    {
        _settings = settings;
        _security = security;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = ApplicationDataPaths.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString();
    }

    public string RootFolder
    {
        get
        {
            var configured = _settings.Get(ApplicationSettingKeys.SecurityServerRootFolder).Trim();
            return configured.Length > 0 ? Path.GetFullPath(configured) : Path.Combine(ApplicationDataPaths.RootFolder, "ServerAuthority");
        }
    }

    public string DistributionFolder => Path.Combine(RootFolder, "Distribution");
    public string LocalBackupFolder => Path.Combine(RootFolder, "Backups", "Local");

    public void EnsureServerFolders()
    {
        foreach (var folder in new[]
        {
            RootFolder,
            Path.Combine(RootFolder, "Clients"),
            Path.Combine(RootFolder, "System", "DatabaseSnapshots"),
            Path.Combine(RootFolder, "System", "Audit"),
            DistributionFolder,
            Path.Combine(DistributionFolder, "Windows"),
            Path.Combine(DistributionFolder, "Android"),
            LocalBackupFolder
        }) Directory.CreateDirectory(folder);
    }

    public string GetClientRoot(long clientId, string financialYear)
    {
        var client = GetClientIdentity(clientId);
        var code = SanitizeSegment(client.Code.Length > 0 ? client.Code : $"CL{clientId:0000}");
        return Path.Combine(RootFolder, "Clients", code, SanitizeSegment(financialYear));
    }

    public async Task<ServerArchivedFileRecord> ArchiveUploadAsync(long clientId, string financialYear, string originalFileName, Stream source, string deviceId, string userName, CancellationToken cancellationToken, bool forwardToAutomationFolder = true)
    {
        EnsureServerFolders();
        var clientRoot = GetClientRoot(clientId, financialYear);
        var archiveFolder = Path.Combine(clientRoot, "Uploaded Files");
        Directory.CreateDirectory(archiveFolder);
        var safeName = SanitizeFileName(originalFileName);
        var temp = Path.Combine(archiveFolder, $".upload-{Guid.NewGuid():N}.tmp");
        string hash;
        long length;
        try
        {
            await using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[1024 * 1024];
                int read;
                long total = 0;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > 250L * 1024 * 1024) throw new InvalidDataException("The uploaded file exceeds the 250 MB server limit.");
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                length = total;
            }

            ValidateRemoteAccountingContent(temp, safeName);

            if (_security.ArchivedHashExists(clientId, financialYear, "Upload", hash))
            {
                File.Delete(temp);
                return new ServerArchivedFileRecord
                {
                    ClientId = clientId, FinancialYear = financialYear, Kind = "Upload", OriginalName = safeName,
                    StoredPath = string.Empty, Sha256 = hash, FileSize = length, DeviceId = deviceId, UserName = userName, CreatedUtc = DateTime.UtcNow
                };
            }

            var storedName = $"{DateTime.Now:yyyyMMdd-HHmmssfff}_{hash[..10]}_{safeName}";
            var storedPath = Path.Combine(archiveFolder, storedName);
            File.Move(temp, storedPath);
            var record = new ServerArchivedFileRecord
            {
                ClientId = clientId, FinancialYear = financialYear, Kind = "Upload", OriginalName = safeName, StoredPath = storedPath,
                Sha256 = hash, FileSize = length, DeviceId = deviceId, UserName = userName, CreatedUtc = DateTime.UtcNow
            };
            _security.SaveArchivedFile(record);
            if (forwardToAutomationFolder) CopyIntoAutomationFolder(clientId, financialYear, storedPath, safeName);
            return record;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    public string SaveExport(long clientId, string financialYear, string fileName, byte[] content, string userName, string deviceId)
    {
        EnsureServerFolders();
        var folder = Path.Combine(GetClientRoot(clientId, financialYear), "Exports");
        Directory.CreateDirectory(folder);
        var safeName = SanitizeFileName(fileName);
        var path = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss}_{safeName}");
        File.WriteAllBytes(path, content);
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        _security.SaveArchivedFile(new ServerArchivedFileRecord
        {
            ClientId = clientId, FinancialYear = financialYear, Kind = "Export", OriginalName = safeName,
            StoredPath = path, Sha256 = hash, FileSize = content.LongLength, DeviceId = deviceId, UserName = userName, CreatedUtc = DateTime.UtcNow
        });
        return path;
    }

    public Task<bool> ArchiveAutomationSourceNowAsync(long clientId, string financialYear, string sourcePath, CancellationToken cancellationToken = default)
    {
        if (!_settings.GetBool(ApplicationSettingKeys.SecurityServerMode) || string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return Task.FromResult(false);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureServerFolders();
        try { return Task.FromResult(ArchiveExistingFile(clientId, financialYear, "AutomationSource", sourcePath)); }
        catch { return Task.FromResult(false); }
    }

    public async Task<string> RunReplicationAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.GetBool(ApplicationSettingKeys.SecurityServerMode)) return "Main Server mode is disabled.";
        await _replicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureServerFolders();
            var copied = 0;
            var skipped = 0;

            foreach (var row in ReadSourceEvidence())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(row.Path)) { skipped++; continue; }
                try
                {
                    if (ArchiveExistingFile(row.ClientId, row.FinancialYear, row.Kind, row.Path)) copied++; else skipped++;
                }
                catch { skipped++; }
            }

            var backupService = new ApplicationBackupService();
            Directory.CreateDirectory(LocalBackupFolder);
            var backupPath = Path.Combine(LocalBackupFolder, $"AAH-Server-{DateTime.Now:yyyyMMdd-HHmmss}.aahbackup");
            backupService.CreateBackup(backupPath);
            var backupHash = HashFile(backupPath);
            File.WriteAllText(backupPath + ".sha256", $"{backupHash}  {Path.GetFileName(backupPath)}{Environment.NewLine}");

            var external = _settings.Get(ApplicationSettingKeys.SecurityExternalBackupFolder).Trim();
            if (external.Length > 0)
            {
                Directory.CreateDirectory(external);
                var target = Path.Combine(external, Path.GetFileName(backupPath));
                File.Copy(backupPath, target, true);
                var copiedHash = HashFile(target);
                if (!copiedHash.Equals(backupHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("External backup verification failed: copied SHA-256 does not match the server backup.");
                File.WriteAllText(target + ".sha256", $"{copiedHash}  {Path.GetFileName(target)}{Environment.NewLine}");
            }

            RegisterConfiguredPackages();
            // Phase 1 server backups are append-only by design. Local/accounting deletion must not
            // silently remove the server's retained historical copy. Server purge policy is a separate Admin action.
            _security.LogEvent("ServerReplication", Environment.UserName, "SERVER", $"Copied={copied};Skipped={skipped};Backup={Path.GetFileName(backupPath)}", true);
            return $"Server replication completed. Retained {copied} new source file(s); {skipped} already retained/unavailable. Verified backup: {Path.GetFileName(backupPath)}.";
        }
        catch (Exception exception)
        {
            _security.LogEvent("ServerReplication", Environment.UserName, "SERVER", exception.Message, false);
            throw;
        }
        finally { _replicationGate.Release(); }
    }

    public void RegisterConfiguredPackages(string windowsServerEndpoint = "", string remoteAssistEndpoint = "")
    {
        var windows = _settings.Get(ApplicationSettingKeys.SecurityNetworkClientPackagePath).Trim();
        if (windows.Length == 0)
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "NetworkClient", "AAHNetworkClient.zip");
            if (File.Exists(bundled)) windows = bundled;
        }
        if (windows.Length > 0 && windowsServerEndpoint.Trim().Length > 0)
            windows = PrepareWindowsPackageForServer(windows, windowsServerEndpoint.Trim(), remoteAssistEndpoint.Trim());

        var android = _settings.Get(ApplicationSettingKeys.SecurityAndroidPackagePath).Trim();
        var managedAndroid = GetManagedAndroidPackagePath();
        var bundledAndroid = Path.Combine(AppContext.BaseDirectory, "Mobile", "AAHMobile.apk");
        var bundledVersion = ReadAndroidPackageVersionSidecar(bundledAndroid);

        // A signed APK carried by the current Desktop release is the first authority. This lets a
        // normal Main Server update publish the matching mobile build without an Administrator
        // having to re-select an older download-path APK after every Desktop release.
        if (File.Exists(bundledAndroid) && bundledVersion.Equals(CurrentAndroidReleaseVersion, StringComparison.OrdinalIgnoreCase))
        {
            android = PrepareAndroidPackageForServer(bundledAndroid);
        }
        else if (android.Length > 0 && File.Exists(android))
        {
            android = PrepareAndroidPackageForServer(android);
        }
        else if (File.Exists(managedAndroid))
        {
            // The Main Server keeps its own durable approved APK copy. A removable/download
            // source path disappearing must not break phone updates after publication.
            android = managedAndroid;
        }
        else if (File.Exists(bundledAndroid))
        {
            android = PrepareAndroidPackageForServer(bundledAndroid);
        }
        RegisterPackage("Windows", windows);
        RegisterPackage("Android", android);
    }

    private string GetManagedAndroidPackagePath() =>
        Path.Combine(RootFolder, "System", "Packages", "Android", "AAHMobile.apk");

    private static string ReadAndroidPackageVersionSidecar(string apkPath)
    {
        if (string.IsNullOrWhiteSpace(apkPath)) return string.Empty;
        var sidecar = apkPath + ".version";
        if (!File.Exists(sidecar)) return string.Empty;
        try { return File.ReadAllText(sidecar).Trim(); }
        catch { return string.Empty; }
    }

    private string PrepareAndroidPackageForServer(string sourcePath)
    {
        if (!File.Exists(sourcePath) || !Path.GetExtension(sourcePath).Equals(".apk", StringComparison.OrdinalIgnoreCase))
            return sourcePath;

        var managed = GetManagedAndroidPackagePath();
        Directory.CreateDirectory(Path.GetDirectoryName(managed)!);
        var sourceFull = Path.GetFullPath(sourcePath);
        var managedFull = Path.GetFullPath(managed);
        var sourceHash = HashFile(sourceFull);
        var existingAndroid = _security.GetPackage("Android");
        var publicationVersion =
            existingAndroid is not null &&
            existingAndroid.Sha256.Equals(sourceHash, StringComparison.OrdinalIgnoreCase) &&
            existingAndroid.Version.Trim().Length > 0
                ? existingAndroid.Version.Trim()
                : CurrentAndroidReleaseVersion;

        if (!sourceFull.Equals(managedFull, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(managedFull) ||
            !HashFile(managedFull).Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
        {
            var temp = managedFull + ".tmp";
            File.Copy(sourceFull, temp, true);
            var copiedHash = HashFile(temp);
            if (!copiedHash.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temp);
                throw new InvalidDataException("Main Server Android APK staging failed SHA-256 verification.");
            }
            File.Move(temp, managedFull, true);
        }

        File.WriteAllText(managedFull + ".version", publicationVersion + Environment.NewLine, new UTF8Encoding(false));
        File.WriteAllText(managedFull + ".sha256", sourceHash + "  " + Path.GetFileName(managedFull) + Environment.NewLine, new UTF8Encoding(false));
        return managedFull;
    }

    private static string ResolvePackageVersion(string kind, string fullPath)
    {
        if (!kind.Equals("Android", StringComparison.OrdinalIgnoreCase))
            return typeof(ServerStorageService).Assembly.GetName().Version?.ToString(4) ?? "4.0.0.410";

        var sidecar = fullPath + ".version";
        if (File.Exists(sidecar))
        {
            var text = File.ReadAllText(sidecar).Trim();
            if (text.Length > 0) return text;
        }

        return CurrentAndroidReleaseVersion;
    }

    private string PrepareWindowsPackageForServer(string sourcePath, string endpoint, string remoteAssistEndpoint)
    {
        if (!File.Exists(sourcePath) || !Path.GetExtension(sourcePath).Equals(".zip", StringComparison.OrdinalIgnoreCase)) return sourcePath;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return sourcePath;

        var packageFolder = Path.Combine(RootFolder, "System", "Packages");
        Directory.CreateDirectory(packageFolder);
        var output = Path.Combine(packageFolder, "AAHNetworkClient-MainServer.zip");
        var stamp = output + ".source";
        var sourceHash = HashFile(sourcePath);
        var caPath = Path.Combine(ApplicationDataPaths.RootFolder, "Security", "AAH-LAN-Root-CA.cer");
        if (!File.Exists(caPath))
            throw new InvalidOperationException("Main Server public CA certificate is unavailable. Start the Main Server before sharing the LAN application.");
        var caHash = HashFile(caPath);
        var serverTlsPath = Path.Combine(ApplicationDataPaths.RootFolder, "Security", "AAH-LAN-Server.cer");
        if (!File.Exists(serverTlsPath))
            throw new InvalidOperationException("Main Server HTTPS public certificate is unavailable. Restart the Main Server before sharing the LAN application.");
        var serverTlsHash = HashFile(serverTlsPath);
        var effectiveRemoteAssistEndpoint = Uri.TryCreate(remoteAssistEndpoint, UriKind.Absolute, out var remoteUri) && remoteUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? remoteAssistEndpoint.TrimEnd('/')
            : endpoint.TrimEnd('/');
        var desiredStamp = sourceHash + "|" + endpoint + "|" + effectiveRemoteAssistEndpoint + "|" + caHash + "|" + serverTlsHash;
        if (File.Exists(output) && File.Exists(stamp) && File.ReadAllText(stamp).Equals(desiredStamp, StringComparison.Ordinal)) return output;

        var temp = output + ".tmp";
        File.Copy(sourcePath, temp, true);
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Update))
            {
                archive.GetEntry(InstallationRoleService.MarkerFileName)?.Delete();
                var roleEntry = archive.CreateEntry(InstallationRoleService.MarkerFileName, CompressionLevel.NoCompression);
                using (var roleWriter = new StreamWriter(roleEntry.Open(), new UTF8Encoding(false)))
                    roleWriter.Write(InstallationRoleService.LanClient);

                archive.GetEntry("AAH_MAIN_SERVER_ENDPOINT")?.Delete();
                var entry = archive.CreateEntry("AAH_MAIN_SERVER_ENDPOINT", CompressionLevel.NoCompression);
                using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                    writer.Write(endpoint);

                archive.GetEntry("AAH_REMOTE_ASSIST_ENDPOINT")?.Delete();
                var remoteAssistEntry = archive.CreateEntry("AAH_REMOTE_ASSIST_ENDPOINT", CompressionLevel.NoCompression);
                using (var writer = new StreamWriter(remoteAssistEntry.Open(), new UTF8Encoding(false)))
                    writer.Write(effectiveRemoteAssistEndpoint);

                archive.GetEntry("AAH_MAIN_SERVER_CA.cer")?.Delete();
                var caEntry = archive.CreateEntry("AAH_MAIN_SERVER_CA.cer", CompressionLevel.NoCompression);
                using (var input = File.OpenRead(caPath))
                using (var outputStream = caEntry.Open())
                    input.CopyTo(outputStream);

                archive.GetEntry("AAH_MAIN_SERVER_CA_SHA256")?.Delete();
                var caHashEntry = archive.CreateEntry("AAH_MAIN_SERVER_CA_SHA256", CompressionLevel.NoCompression);
                using (var hashWriter = new StreamWriter(caHashEntry.Open(), new UTF8Encoding(false)))
                    hashWriter.Write(caHash);

                archive.GetEntry("AAH_MAIN_SERVER_TLS.cer")?.Delete();
                var serverTlsEntry = archive.CreateEntry("AAH_MAIN_SERVER_TLS.cer", CompressionLevel.NoCompression);
                using (var input = File.OpenRead(serverTlsPath))
                using (var outputStream = serverTlsEntry.Open())
                    input.CopyTo(outputStream);

                archive.GetEntry("AAH_MAIN_SERVER_TLS_SHA256")?.Delete();
                var serverTlsHashEntry = archive.CreateEntry("AAH_MAIN_SERVER_TLS_SHA256", CompressionLevel.NoCompression);
                using (var hashWriter = new StreamWriter(serverTlsHashEntry.Open(), new UTF8Encoding(false)))
                    hashWriter.Write(serverTlsHash);
            }
            File.Move(temp, output, true);
            File.WriteAllText(stamp, desiredStamp, new UTF8Encoding(false));
            return output;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private void RegisterPackage(string kind, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            lock (_packageCacheGate) _packageCache.Remove(kind);
            _security.ClearPackage(kind);
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        string hash;
        lock (_packageCacheGate)
        {
            if (_packageCache.TryGetValue(kind, out var cached) &&
                cached.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase) &&
                cached.Length == info.Length && cached.LastWriteUtc == info.LastWriteTimeUtc)
            {
                hash = cached.Sha256;
            }
            else
            {
                hash = HashFile(fullPath);
                _packageCache[kind] = new PackageFileCache(fullPath, info.Length, info.LastWriteTimeUtc, hash);
            }
        }

        var version = ResolvePackageVersion(kind, fullPath);
        var existing = _security.GetPackage(kind);
        if (existing is not null && existing.FilePath.Equals(fullPath, StringComparison.OrdinalIgnoreCase) &&
            existing.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase) && existing.Version.Equals(version, StringComparison.OrdinalIgnoreCase))
            return;

        _security.SavePackage(new ServerPackageRecord
        {
            PackageKind = kind, FilePath = fullPath, Sha256 = hash, Version = version, UpdatedUtc = DateTime.UtcNow
        });
    }

    private bool ArchiveExistingFile(long clientId, string financialYear, string kind, string sourcePath)
    {
        var hash = HashFile(sourcePath);
        if (_security.ArchivedHashExists(clientId, financialYear, kind, hash)) return false;
        if (kind.Equals("AutomationSource", StringComparison.OrdinalIgnoreCase) && _security.ArchivedHashExists(clientId, financialYear, "Upload", hash)) return false;
        var folder = Path.Combine(GetClientRoot(clientId, financialYear), kind.Equals("DocumentVault", StringComparison.OrdinalIgnoreCase) ? "Document Vault Archive" : "Automatic Import Archive");
        Directory.CreateDirectory(folder);
        var name = SanitizeFileName(Path.GetFileName(sourcePath));
        var stored = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmssfff}_{hash[..10]}_{name}");
        File.Copy(sourcePath, stored, false);
        var info = new FileInfo(stored);
        _security.SaveArchivedFile(new ServerArchivedFileRecord
        {
            ClientId=clientId,FinancialYear=financialYear,Kind=kind,OriginalName=name,StoredPath=stored,Sha256=hash,FileSize=info.Length,
            DeviceId="SERVER",UserName=Environment.UserName,CreatedUtc=DateTime.UtcNow
        });
        return true;
    }

    private IEnumerable<(long ClientId,string FinancialYear,string Kind,string Path)> ReadSourceEvidence()
    {
        if (!File.Exists(ApplicationDataPaths.DatabasePath)) yield break;
        using var connection = new SqliteConnection(_connectionString); connection.Open();
        if (TableExists(connection, "DocumentVault"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT ClientId,FinancialYear,CASE WHEN TRIM(VaultPath)<>'' THEN VaultPath ELSE OriginalPath END FROM DocumentVault WHERE IsAvailable=1;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) yield return (reader.GetInt64(0),reader.GetString(1),"DocumentVault",reader.GetString(2));
        }
        if (TableExists(connection, "AutomationFileHistory"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT ClientId,FinancialYear,FilePath FROM AutomationFileHistory WHERE TRIM(FilePath)<>'' AND Status IN ('Imported','Duplicate','Needs Action','Failed','Skipped');";
            using var reader = command.ExecuteReader();
            while (reader.Read()) yield return (reader.GetInt64(0),reader.GetString(1),"AutomationSource",reader.GetString(2));
        }
    }

    private void CopyIntoAutomationFolder(long clientId, string financialYear, string retainedPath, string originalName)
    {
        var folder = FindActiveAutomationFolder(clientId, financialYear);
        if (folder.Length == 0 || !Directory.Exists(folder)) return;
        var target = Path.Combine(folder, originalName);
        if (File.Exists(target))
        {
            var existingHash = HashFile(target);
            var retainedHash = HashFile(retainedPath);
            if (existingHash.Equals(retainedHash, StringComparison.OrdinalIgnoreCase)) return;
            target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(originalName)}_{DateTime.Now:yyyyMMdd-HHmmssfff}{Path.GetExtension(originalName)}");
        }
        File.Copy(retainedPath, target, false);
    }

    private string FindActiveAutomationFolder(long clientId, string financialYear)
    {
        using var connection = new SqliteConnection(_connectionString); connection.Open();
        if (!TableExists(connection,"AutomationFolders")) return string.Empty;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT FolderPath FROM AutomationFolders WHERE ClientId=$Client AND FinancialYear=$Fy COLLATE NOCASE AND IsPaused=0 ORDER BY UpdatedUtc DESC LIMIT 1;";
        command.Parameters.AddWithValue("$Client", clientId); command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        return Convert.ToString(command.ExecuteScalar())?.Trim() ?? string.Empty;
    }

    private (string Code,string Name) GetClientIdentity(long clientId)
    {
        using var connection = new SqliteConnection(_connectionString); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT ClientCode,ClientName FROM Clients WHERE Id=$Id LIMIT 1;"; command.Parameters.AddWithValue("$Id", clientId);
        using var reader = command.ExecuteReader(); return reader.Read() ? (reader.GetString(0),reader.GetString(1)) : ($"CL{clientId:0000}",$"Client {clientId}");
    }


    private static void ValidateRemoteAccountingContent(string path, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[16];
        var count = stream.Read(header, 0, header.Length);
        if (count >= 2 && header[0] == (byte)'M' && header[1] == (byte)'Z')
            throw new InvalidDataException("Executable content was detected inside the uploaded accounting file.");
        if (count >= 4 && header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
            throw new InvalidDataException("Executable content was detected inside the uploaded accounting file.");

        bool Starts(params byte[] bytes) => count >= bytes.Length && bytes.Select((value,index) => header[index] == value).All(x => x);
        var valid = extension switch
        {
            ".pdf" => count >= 5 && Encoding.ASCII.GetString(header, 0, 5) == "%PDF-",
            ".png" => Starts(0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A),
            ".jpg" or ".jpeg" => Starts(0xFF,0xD8,0xFF),
            ".webp" => count >= 12 && Encoding.ASCII.GetString(header,0,4)=="RIFF" && Encoding.ASCII.GetString(header,8,4)=="WEBP",
            ".xlsx" or ".xlsm" => Starts(0x50,0x4B,0x03,0x04) || Starts(0x50,0x4B,0x05,0x06) || Starts(0x50,0x4B,0x07,0x08) || Starts(0xD0,0xCF,0x11,0xE0,0xA1,0xB1,0x1A,0xE1),
            ".xls" => Starts(0xD0,0xCF,0x11,0xE0,0xA1,0xB1,0x1A,0xE1),
            ".zip" => ValidateSafeAccountingZip(path),
            _ => !ContainsBinaryNulls(path)
        };
        if (!valid) throw new InvalidDataException($"Uploaded file content does not match the allowed '{extension}' accounting format.");
    }

    private static bool ContainsBinaryNulls(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sample = new byte[Math.Min(8192, (int)Math.Min(stream.Length, 8192))];
        var read = stream.Read(sample,0,sample.Length);
        if (read == 0) return false;
        if (read >= 2 && ((sample[0] == 0xFF && sample[1] == 0xFE) || (sample[0] == 0xFE && sample[1] == 0xFF))) return false; // UTF-16 text
        var nulls = sample.Take(read).Count(x => x == 0);
        return nulls > Math.Max(4, read / 100);
    }

    private static bool ValidateSafeAccountingZip(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count == 0 || archive.Entries.Count > 1000) return false;
            long total = 0;
            var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".exe", ".dll", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".js", ".jse", ".wsf", ".msi", ".scr", ".lnk", ".url", ".hta", ".reg", ".py", ".pyw", ".jar", ".apk" };
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".json", ".csv", ".txt", ".xml", ".xlsx", ".xlsm", ".xls", ".pdf" };
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var normalized = entry.FullName.Replace('\\','/');
                if (normalized.StartsWith('/') || normalized.Contains("../", StringComparison.Ordinal)) return false;
                var extension = Path.GetExtension(entry.Name);
                if (blocked.Contains(extension) || !allowed.Contains(extension)) return false;
                if (entry.Length > 250L * 1024 * 1024) return false;
                total += entry.Length;
                if (total > 500L * 1024 * 1024) return false;
                if (entry.CompressedLength > 0 && entry.Length / Math.Max(1.0, entry.CompressedLength) > 250.0) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private sealed record PackageFileCache(string Path, long Length, DateTime LastWriteUtc, string Sha256);

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$Name LIMIT 1;"; command.Parameters.AddWithValue("$Name", table); return command.ExecuteScalar() is not null;
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024*1024, FileOptions.SequentialScan);
        using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet(); var cleaned = new string((value ?? string.Empty).Where(c => !invalid.Contains(c) && c != Path.DirectorySeparatorChar && c != Path.AltDirectorySeparatorChar).ToArray()).Trim();
        return cleaned.Length == 0 ? "Unknown" : cleaned;
    }

    public static string SanitizeFileName(string value)
    {
        var name = Path.GetFileName(value ?? string.Empty); var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        name = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim(); return name.Length == 0 ? $"upload-{Guid.NewGuid():N}.bin" : name;
    }

    private void TrimLocalBackups(int keep)
    {
        try
        {
            if (!Directory.Exists(LocalBackupFolder)) return;
            var backups = new DirectoryInfo(LocalBackupFolder).GetFiles("*.aahbackup")
                .OrderByDescending(x => x.LastWriteTimeUtc).ToArray();
            foreach (var old in backups.Skip(Math.Max(1, keep)))
            {
                try
                {
                    old.Delete();
                    var hashFile = old.FullName + ".sha256";
                    if (File.Exists(hashFile)) File.Delete(hashFile);
                }
                catch { /* retention cleanup must never invalidate a successful new backup */ }
            }
        }
        catch { }
    }
}
