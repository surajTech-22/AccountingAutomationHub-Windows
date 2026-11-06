# AAH one-time ORIGINAL in-place transactional update: 4.0.0.618/619 -> 4.0.0.620.
# Preview is read-only. -Apply requires full verified backup and reuses
# ci/legacy/TransactionalPublish.ps1 Install-AAHPublish; NEVER launches Setup.exe.
# Neither uninstall registration nor Tally/company data is modified.
[CmdletBinding()]
param([switch]$Apply)

$ErrorActionPreference = 'Stop'
$repo = 'surajTech-22/AccountingAutomationHub-Windows'
$sourceCommit = 'b4368340a8fd1f4f9b50e04916b50ff694c4c2c3'
$nativeGitBlob = '323d2a572d036815b2b8377add6b0682cd489263'
$tag = 'v4.0.0.620'
$expectedZip = 'D961A0274B4398775A9D63AD6ACA2290CF77D077C388E4AB44BD8D36284D80A3'
$install = 'C:\Accounting Automation Hub'
$exe = Join-Path $install 'AccountingAutomationHub.exe'
$backupBase = Join-Path (Join-Path $env:USERPROFILE 'Downloads') ('AAH_SAFE_BACKUP_' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
$work = Join-Path $env:TEMP ('AAH-original-inplace-' + [guid]::NewGuid().ToString('N'))
$mutexName = 'Local\AccountingAutomationHub.GitHubUpdate.v1'

function Assert-AAH([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Get-Version([string]$File) {
    if (-not (Test-Path -LiteralPath $File -PathType Leaf)) { return '' }
    return [string](Get-Item -LiteralPath $File).VersionInfo.FileVersion
}
function Get-GitBlobSha1([string]$File) {
    [byte[]]$bytes = [IO.File]::ReadAllBytes($File)
    [byte[]]$prefix = [Text.Encoding]::ASCII.GetBytes(("blob " + $bytes.Length + [char]0))
    [byte[]]$all = New-Object byte[] ($prefix.Length + $bytes.Length)
    [Array]::Copy($prefix,0,$all,0,$prefix.Length)
    [Array]::Copy($bytes,0,$all,$prefix.Length,$bytes.Length)
    $sha = [Security.Cryptography.SHA1]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($all)).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

# Only the ORIGINAL registered installation may be updated; ignore, but never
# remove or modify, a separate bootstrapper registration.
Assert-AAH (Test-Path -LiteralPath $exe -PathType Leaf) 'Original C:\ AAH executable missing.'
$oldVersion = Get-Version $exe
Assert-AAH ($oldVersion -in @('4.0.0.618','4.0.0.619','4.0.0.620')) ('Unexpected original AAH version: ' + $oldVersion)
$registered = @()
foreach ($parent in @(
    'Registry::HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall',
    'Registry::HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Uninstall',
    'Registry::HKEY_LOCAL_MACHINE\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
)) {
    if (-not (Test-Path -LiteralPath $parent)) { continue }
    foreach ($key in @(Get-ChildItem -LiteralPath $parent -ErrorAction SilentlyContinue)) {
        if ($key.PSChildName -ne 'AccountingAutomationHub') { continue }
        $entry = Get-ItemProperty -LiteralPath $key.PSPath
        if (-not $entry.InstallLocation) { continue }
        if ([IO.Path]::GetFullPath([string]$entry.InstallLocation).TrimEnd('\') -ine $install) { continue }
        if ([string]$entry.DisplayName -notmatch '(?i)^Accounting Automation Hub$') { continue }
        $registered += $key.Name
    }
}
Assert-AAH ($registered.Count -eq 1) 'Original AAH installer registration/path not uniquely verified. Do not use Setup.exe or delete registry entries.'
Write-Host ('Original registered installation: ' + $install)
Write-Host ('Installed version: ' + $oldVersion + '; target: 4.0.0.620')
Write-Host 'Existing installer identity and Tally links will be preserved.'
if ($oldVersion -eq '4.0.0.620') { Write-Host 'Version 620 already installed; no changes needed.'; return }
if (-not $Apply) {
    Write-Host 'READ-ONLY PREVIEW. Re-run with -Apply after closing AAH and saving Tally work.'
    Write-Host 'Apply mode backs up the complete original install and AAH AppData before the native transactional update.'
    return
}

# Check the actual native state; WMI/CIM may retain already-exited Windows PIDs.
# A managed HasExited=True is insufficient if a native process handle is NOT signaled.
if (-not ('AAHRecoveryKernel' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AAHRecoveryKernel {
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);
}
'@
}
function Assert-NoActiveAahOrInstaller {
    foreach ($candidate in @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
        $_.Name -in @('AccountingAutomationHub.exe','AAH_Update.exe') -or
        ($_.Name -in @('powershell.exe','pwsh.exe') -and
         $_.CommandLine -match 'Install-AAHPublish')
    })) {
        $handle = [AAHRecoveryKernel]::OpenProcess(0x00100000,$false,[uint32]$candidate.ProcessId)
        if ($handle -eq [IntPtr]::Zero) {
            $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            Assert-AAH ($errorCode -eq 87) ('Installer PID ' + $candidate.ProcessId + ' cannot be verified (Windows error ' + $errorCode + ').')
        } else {
            try {
                $waitState = [AAHRecoveryKernel]::WaitForSingleObject($handle,0)
                Assert-AAH ($waitState -eq 0) ('AAH/installer PID ' + $candidate.ProcessId + ' is not kernel-confirmed exited. Close it normally; if stuck, restart Windows before retrying.')
            } finally { [void][AAHRecoveryKernel]::CloseHandle($handle) }
        }
    }
}
Assert-NoActiveAahOrInstaller
$journals = @(Get-ChildItem -LiteralPath $install -Directory -Force |
    Where-Object { $_.Name -like '.aah-swap-*' -or $_.Name -like '.aah-github-swap-*' } |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'journal.json') })
Assert-AAH ($journals.Count -eq 0) 'A prior transaction journal exists; original recovery must complete first. Nothing was changed.'

$mutex = [Threading.Mutex]::new($false,$mutexName)
$owned = $false
$success = $false
try {
    try { $owned = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $owned = $true }
    Assert-AAH $owned 'A real updater still owns the installation lock; refusing concurrent installation.'

    # Full backup of the original program tree and all relevant AAH data roots.
    # No uninstall; no deletion from the source; no backup is overwritten.
    $sources = @(
        [pscustomobject]@{ Name='OriginalInstall'; Path=$install },
        [pscustomobject]@{ Name='LocalAppData'; Path=(Join-Path $env:LOCALAPPDATA 'AccountingAutomationHub') },
        [pscustomobject]@{ Name='RoamingAppData'; Path=(Join-Path $env:APPDATA 'AccountingAutomationHub') },
        [pscustomobject]@{ Name='ProgramData'; Path=(Join-Path $env:PROGRAMDATA 'AccountingAutomationHub') }
    ) | Where-Object { Test-Path -LiteralPath $_.Path -PathType Container }
    $bytes = [long]0
    foreach ($src in $sources) {
        $links = @(Get-ChildItem -LiteralPath $src.Path -Recurse -Force -ErrorAction Stop |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
        Assert-AAH ($links.Count -eq 0) ('Backup source contains a link/junction requiring separate review: ' + $src.Path)
        $items = @(Get-ChildItem -LiteralPath $src.Path -File -Recurse -Force -ErrorAction Stop)
        $bytes += [long](($items | Measure-Object -Property Length -Sum).Sum)
    }
    $driveLetter = ([IO.Path]::GetPathRoot($backupBase)).Substring(0,1)
    $free = (Get-PSDrive -Name $driveLetter).Free
    Assert-AAH ($free -gt ($bytes + 2147483648L)) 'Not enough free space for complete AAH backup and temporary release extraction.'
    New-Item -ItemType Directory -Path $backupBase -ErrorAction Stop | Out-Null
    foreach ($src in $sources) {
        $destination = Join-Path $backupBase $src.Name
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        & robocopy.exe $src.Path $destination /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /XJ /NP /NFL /NDL /NJH /NJS | Out-Null
        $exit = $LASTEXITCODE
        Assert-AAH ($exit -lt 8) ('Backup failed (' + $exit + '): ' + $src.Path)
        $originalFiles = @(Get-ChildItem -LiteralPath $src.Path -File -Recurse -Force)
        $savedFiles = @(Get-ChildItem -LiteralPath $destination -File -Recurse -Force)
        Assert-AAH ($savedFiles.Count -eq $originalFiles.Count) ('Backup file count mismatch: ' + $src.Path)
        $savedBytes = [long](($savedFiles | Measure-Object -Property Length -Sum).Sum)
        $originalBytes = [long](($originalFiles | Measure-Object -Property Length -Sum).Sum)
        Assert-AAH ($savedBytes -eq $originalBytes) ('Backup byte count mismatch: ' + $src.Path)
        Write-Host ('BACKUP VERIFIED: ' + $src.Path + ' -> ' + $destination)
    }
    $sources | Select-Object Name,Path | ConvertTo-Json -Depth 3 |
        Set-Content -LiteralPath (Join-Path $backupBase 'BackedUpSources.json') -Encoding UTF8

    New-Item -ItemType Directory -Path $work -ErrorAction Stop | Out-Null
    $archivePath = Join-Path $work 'AAH_App-win-x64.zip'
    $nativePath = Join-Path $work 'TransactionalPublish.ps1'
    $baseUrl = 'https://github.com/' + $repo + '/releases/download/' + $tag + '/'
    Write-Host 'Downloading the existing verified 4.0.0.620 application ZIP (NOT Setup.exe).'
    Invoke-WebRequest -Uri ($baseUrl+'AAH_App-win-x64.zip') -OutFile $archivePath -UseBasicParsing
    Assert-AAH ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -eq $expectedZip) 'Official application ZIP SHA256 mismatch.'
    $nativeUrl = 'https://raw.githubusercontent.com/' + $repo + '/' + $sourceCommit + '/ci/legacy/TransactionalPublish.ps1'
    Invoke-WebRequest -Uri $nativeUrl -OutFile $nativePath -UseBasicParsing
    Assert-AAH ((Get-GitBlobSha1 $nativePath) -eq $nativeGitBlob) 'Original SHA-pinned native updater source mismatch.'

    $publish = Join-Path $work 'publish'
    New-Item -ItemType Directory -Path $publish -ErrorAction Stop | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        Assert-AAH ($zip.Entries.Count -le 2500) 'Unexpected archive entry count.'
        $seen = @{}
        $root = [IO.Path]::GetFullPath($publish).TrimEnd('\') + '\'
        foreach ($entry in $zip.Entries) {
            $rel = $entry.FullName
            if ($rel.EndsWith('/')) { continue }
            Assert-AAH (-not [IO.Path]::IsPathRooted($rel) -and $rel -notmatch '(^|/)\.\.?(/|$)|:|\\|//' -and
                $rel -notmatch '(?i)\.(db|sqlite|sqlite3|bak|pfx|key|pem|py|pyc|pyd)$') ('Unsafe release payload: ' + $rel)
            Assert-AAH (-not $seen.ContainsKey($rel.ToLowerInvariant())) ('Duplicate payload path: ' + $rel)
            $seen[$rel.ToLowerInvariant()] = $true
            Assert-AAH ((($entry.ExternalAttributes -shr 16) -band 0xF000) -ne 0xA000) 'Release archive contains a symlink.'
            $out = [IO.Path]::GetFullPath((Join-Path $publish ($rel.Replace('/','\'))))
            Assert-AAH ($out.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)) 'Release archive path escapes staging.'
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($out)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$out,$false)
        }
    } finally { $zip.Dispose() }
    Assert-AAH ((Get-Version (Join-Path $publish 'AccountingAutomationHub.exe')) -eq '4.0.0.620') 'Extracted AAH binary version is not 4.0.0.620.'
    Assert-AAH (@(Get-ChildItem -LiteralPath $install -Directory -Force |
        Where-Object { $_.Name -like '.aah-swap-*' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'journal.json')) }).Count -eq 0) 'New transaction journal found; refusing.'
    # The original AAH may have been reopened during backup/download.
    # Re-check the native kernel process states immediately before any file mutation.
    Assert-NoActiveAahOrInstaller
    Write-Host 'Starting the SAME original Install-AAHPublish transactional procedure.'
    . $nativePath
    Install-AAHPublish $publish $install
    Assert-AAH ((Get-Version $exe) -eq '4.0.0.620') 'In-place update version verification failed; preserve backups and the original journal.'
    $success = $true
    ('SUCCESS: original AAH updated in place at ' + $install + ' on ' + (Get-Date -Format o)) |
        Set-Content -LiteralPath (Join-Path $backupBase 'RecoveryReceipt.txt') -Encoding UTF8
    Write-Host ('SUCCESS: AAH 4.0.0.620 installed in original directory. Backup retained: ' + $backupBase)
    Write-Host 'No uninstall registration, linked Tally company or accounting-data directory was removed.'
} finally {
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
    if ($success -and (Test-Path -LiteralPath $work)) {
        try { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Stop }
        catch { Write-Warning ('Temporary staged files remain at ' + $work) }
    } elseif (Test-Path -LiteralPath $work) {
        Write-Warning ('Failed attempt: temporary evidence retained at ' + $work)
    }
    if (Test-Path -LiteralPath $backupBase) {
        Write-Host ('Original/data backups retained at: ' + $backupBase)
    }
}
