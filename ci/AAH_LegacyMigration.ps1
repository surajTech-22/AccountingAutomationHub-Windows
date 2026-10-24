<#
.SYNOPSIS
Migrate AAH's original C:\Accounting Automation Hub installation in place.
.DESCRIPTION
Read-only by default. The explicit -Apply mode uses the EXACT original AAH
Install-AAHPublish algorithm recovered from SHA256-pinned CL017 R14 source.
It does not install a second program, remove uninstall registrations, change
Tally links, delete accounting databases or uninstall either AAH copy.
Release v4.0.0.503 is pinned by the verified immutable asset digests below.
Execute only after reviewing the read-only preview and backing up accounting data.
#>
[CmdletBinding()]
param([switch]$Apply)

$ErrorActionPreference = 'Stop'
$Repo = 'surajTech-22/AccountingAutomationHub-Windows'
$Release = 'v4.0.0.503'
$ExpectedCurrent = '4.0.0.502'
$ExpectedNew = '4.0.0.503'
$Root = 'C:\Accounting Automation Hub'
$SecondRoot = Join-Path $env:LOCALAPPDATA 'Programs\AccountingAutomationHub'
$OriginalRegistration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AccountingAutomationHub'
$SecondRegistration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{D8E4758C-09F1-45BF-831A-59C22B79DC04}_is1'
$ExpectedAppHash = '0220D2C0DF7AF648B5CE91C39F720464BE008B878D7848B6E81AD798E3340B9A'
$ExpectedUpdaterHash = 'B7EA630E250818E4444C36ED1A9BC8D31ACDD3188C99A378746C98C6A0C260BA'
$OriginalExe = Join-Path $Root 'AccountingAutomationHub.exe'
$SecondExe = Join-Path $SecondRoot 'AccountingAutomationHub.exe'
$Native = Join-Path $PSScriptRoot 'legacy\TransactionalPublish.ps1'

function Assert-AAH([bool]$Condition,[string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Get-AAHVersion([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return '' }
    return [string](Get-Item -LiteralPath $Path).VersionInfo.FileVersion
}
function Assert-FileDigest([string]$File, [string]$Expected) {
    $actual = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash
    Assert-AAH ($actual -eq $Expected) ("Release SHA256 mismatch for: " + $File)
}
function Assert-OriginalIdentity {
    Assert-AAH (Test-Path -LiteralPath $OriginalRegistration) 'The original AAH Windows uninstall registration is missing.'
    Assert-AAH (Test-Path -LiteralPath $SecondRegistration) 'The secondary GitHub AAH Windows uninstall registration is missing.'
    $original = Get-ItemProperty -LiteralPath $OriginalRegistration
    $secondary = Get-ItemProperty -LiteralPath $SecondRegistration
    Assert-AAH ($original.DisplayName -eq 'Accounting Automation Hub') 'Unexpected original AAH registration name.'
    Assert-AAH ($original.InstallLocation.TrimEnd('\') -ieq $Root) 'Original AAH registration points to a different directory.'
    Assert-AAH ($secondary.InstallLocation.TrimEnd('\') -ieq $SecondRoot) 'GitHub AAH registration points to a different directory.'
    Assert-AAH ($original.UninstallString -match '(?i)C:\\Accounting Automation Hub\\Uninstall-App\.ps1') 'Original manual AAH uninstaller identity changed.'
    Assert-AAH ((Get-AAHVersion $SecondExe) -eq $ExpectedNew) 'Secondary install is not the verified bootstrap version.'
    $actual = Get-AAHVersion $OriginalExe
    Assert-AAH ($actual -eq $ExpectedCurrent -or $actual -eq $ExpectedNew) ('Unexpected original AAH version: ' + $actual)
    return $actual
}

$running = @(Get-CimInstance Win32_Process -Filter "Name='AccountingAutomationHub.exe'" -ErrorAction SilentlyContinue |
    Select-Object ProcessId, ExecutablePath)
$oldVersion = Assert-OriginalIdentity
Assert-AAH (Test-Path -LiteralPath $Native -PathType Leaf) 'Original AAH Install-AAHPublish transactional source missing from the migration bundle.'
$source = Get-Content -LiteralPath $Native -Raw -Encoding UTF8
Assert-AAH ($source -match '(?m)^function Install-AAHPublish\(' -and
    $source -match '(?m)^function Undo-AAHPublish\(') 'Manual updater does not contain the known rollback contract.'
if ($oldVersion -eq $ExpectedNew) {
    Write-Host 'Original C:\ AAH already has v4.0.0.503; no files will be replaced.'
    Write-Host 'The secondary registration remains separate. Do not uninstall it before verifying runtime/data ownership.'
    exit 0
}

Write-Host 'AAH legacy migration evidence:'
Write-Host (' Original executable: ' + $OriginalExe)
Write-Host (' Original version:    ' + $oldVersion)
Write-Host (' Secondary folder:   ' + $SecondRoot)
Write-Host (' Secondary version:  ' + (Get-AAHVersion $SecondExe))
Write-Host (' Target version:     ' + $ExpectedNew)
Write-Host (' Running processes:  ' + @($running).Count)
if ($running.Count) {
    $running | Format-Table -AutoSize | Out-Host
}
Write-Host 'No uninstall registrations or accounting files will be deleted by this migration.'
if (-not $Apply) {
    Write-Host ''
    Write-Host 'DRY RUN ONLY: The C:\ original will be updated with the original manual replacement routine.'
    Write-Host 'To apply: take an accounting data backup, close AAH normally, then re-run the same script with -Apply.'
    exit 0
}

Assert-AAH ($running.Count -eq 0) 'Close ALL AAH instances normally first. Do not force-end ongoing Tally imports.'
Assert-AAH (-not ((Get-Item -LiteralPath $Root).Attributes -band [IO.FileAttributes]::ReparsePoint)) 'Original installation is a reparse point; refusing.'
Assert-AAH (-not ((Get-Item -LiteralPath $SecondRoot).Attributes -band [IO.FileAttributes]::ReparsePoint)) 'Secondary installation is a reparse point; refusing.'
$drive = [IO.Path]::GetPathRoot($Root)
$free = (Get-PSDrive -Name ($drive.Substring(0,1))).Free
Assert-AAH ($free -gt 3000000000) 'At least 3GB of available disk space is required for the temporary rollback proof.'

# This short-lived migration folder is NOT an additional Windows installation.
$work = Join-Path $env:TEMP ('AAH-One-Time-Migration-' + [Guid]::NewGuid().ToString('N'))
$downloads = Join-Path $work 'Downloads'
$publish = Join-Path $work 'Publish'
$backup = Join-Path $work 'OriginalFileRollback'
$receipt = Join-Path $env:USERPROFILE ('Downloads\AAH_MIGRATION_RECEIPT_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '.txt')
$applied = $false
$rollbackComplete = $false
New-Item -Path $downloads,$publish,$backup -ItemType Directory -Force | Out-Null
try {
    $url = 'https://github.com/' + $Repo + '/releases/download/' + $Release + '/'
    $zip = Join-Path $downloads 'AAH_App-win-x64.zip'
    $updater = Join-Path $downloads 'AAH_Update.exe'
    $manifestFile = Join-Path $downloads 'AAH_AutoUpdate.json'
    Invoke-WebRequest -Uri ($url + 'AAH_App-win-x64.zip') -OutFile $zip -UseBasicParsing
    Invoke-WebRequest -Uri ($url + 'AAH_Update.exe') -OutFile $updater -UseBasicParsing
    Invoke-WebRequest -Uri ($url + 'AAH_AutoUpdate.json') -OutFile $manifestFile -UseBasicParsing
    Assert-FileDigest $zip $ExpectedAppHash
    Assert-FileDigest $updater $ExpectedUpdaterHash
    $manifest=Get-Content $manifestFile -Raw | ConvertFrom-Json
    Assert-AAH ($manifest.release -eq $Release -and $manifest.repository -eq $Repo -and
        $manifest.app_sha256 -eq $ExpectedAppHash -and $manifest.sha256 -eq $ExpectedUpdaterHash -and
        $manifest.app_asset -eq 'AAH_App-win-x64.zip' -and
        $manifest.automatic_install_allowed -eq $true) 'GitHub auto-update release manifest rejected.'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        Assert-AAH ($archive.Entries.Count -le 2500) 'Unexpected number of application files in release.'
        $names = @{}
        foreach ($entry in $archive.Entries) {
            $relative = $entry.FullName.Replace('\','/')
            if ($relative.EndsWith('/')) { continue }
            Assert-AAH (-not [IO.Path]::IsPathRooted($relative) -and $relative -notmatch ':' -and
                $relative -notmatch '(^|/)\.\.(/|$)' -and $relative -notmatch '(^|/)\.(/|$)' -and
                $relative -notmatch '\\' -and $relative -notmatch '//') 'Unsafe path in AAH publish ZIP.'
            Assert-AAH (-not $names.ContainsKey($relative.ToLowerInvariant())) 'Duplicate application path in ZIP.'
            $names[$relative.ToLowerInvariant()]=$true
            Assert-AAH ($relative -notmatch '(?i)\.(db|sqlite|sqlite3|bak|pfx|key|pem)$') 'Accounting or private data unexpectedly included in application publish.'
            Assert-AAH ((($entry.ExternalAttributes -shr 16) -band 0xF000) -ne 0xA000) 'Archive contains a symbolic link.'
            $dest = [IO.Path]::GetFullPath((Join-Path $publish $relative.Replace('/',[IO.Path]::DirectorySeparatorChar)))
            Assert-AAH ($dest.StartsWith(([IO.Path]::GetFullPath($publish).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) 'ZIP traversal was rejected.'
            $parent = Split-Path -Parent $dest
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $false)
        }
    }
    finally { $archive.Dispose() }
    Assert-AAH ((Get-AAHVersion (Join-Path $publish 'AccountingAutomationHub.exe')) -eq $ExpectedNew) 'Downloaded release does not contain expected AAH binary version.'
    Copy-Item -LiteralPath $updater -Destination (Join-Path $publish 'AAH_Update.exe') -ErrorAction Stop

    # Preserve original files until application post-launch verification.  The
    # manual native updater itself keeps only its in-flight transaction.
    foreach ($incoming in Get-ChildItem -LiteralPath $publish -File -Recurse) {
        $relative = $incoming.FullName.Substring($publish.TrimEnd('\').Length).TrimStart('\')
        $current = Join-Path $Root $relative
        if (-not (Test-Path -LiteralPath $current -PathType Leaf)) { continue }
        $oldCopy = Join-Path $backup $relative
        New-Item -Path (Split-Path -Parent $oldCopy) -ItemType Directory -Force | Out-Null
        Copy-Item -LiteralPath $current -Destination $oldCopy -ErrorAction Stop
        Assert-AAH ((Get-FileHash $current -Algorithm SHA256).Hash -eq
            (Get-FileHash $oldCopy -Algorithm SHA256).Hash) ('Original file rollback proof failed: ' + $relative)
    }
    Assert-AAH (@(Get-CimInstance Win32_Process -Filter "Name='AccountingAutomationHub.exe'" -ErrorAction SilentlyContinue).Count -eq 0) 'AAH started again; migration cancelled.'
    # The original, production-tested manual import/update file replacement.
    . $Native
    Install-AAHPublish $publish $Root
    $applied = $true
    Assert-AAH ((Get-AAHVersion $OriginalExe) -eq $ExpectedNew) 'Original installation executable readback has wrong version.'
    Assert-FileDigest (Join-Path $Root 'AAH_Update.exe') $ExpectedUpdaterHash
    $launched = Start-Process -FilePath $OriginalExe -WorkingDirectory $Root -PassThru
    Start-Sleep -Seconds 12
    $launched.Refresh()
    Assert-AAH (-not $launched.HasExited) 'Migrated AAH exited early; startup was not verified.'
    # Keep the original AAH uninstall identity and path, only update its version.
    Set-ItemProperty -LiteralPath $OriginalRegistration -Name DisplayVersion -Value $ExpectedNew
    @(
        'SUCCESS: AAH original installation updated in place.'
        ('OriginalPath=' + $Root)
        ('OriginalRegistryId=AccountingAutomationHub')
        ('OriginalVersion=' + $ExpectedCurrent)
        ('NewVersion=' + $ExpectedNew)
        ('UpdaterSHA256=' + $ExpectedUpdaterHash)
        ('NewProcessId=' + $launched.Id)
        'SecondaryInstallationWasNOTRemoved=true'
        'AccountDataWasNOTDeleted=true'
    ) | Set-Content -LiteralPath $receipt -Encoding UTF8
    Write-Host 'AAH legacy original updated in place. Original uninstall registration retained.'
    Write-Host ('Migration receipt: ' + $receipt)
    Write-Host 'The secondary GitHub installation has NOT been removed; verify accounting/Tally data first.'
}
catch {
    $reason = $_.Exception.Message
    if ($applied -and (Test-Path -LiteralPath $backup 'AccountingAutomationHub.exe')) {
        try {
            $active=@(Get-CimInstance Win32_Process -Filter "Name='AccountingAutomationHub.exe'" -ErrorAction SilentlyContinue)
            if ($active.Count -eq 0) {
                . $Native
                Install-AAHPublish $backup $Root
                $rollbackComplete = $true
            }
        } catch {
            Write-Warning ('Rollback needs manual recovery, original backups retained in ' + $backup + ' : ' + $_.Exception.Message)
        }
    }
    if (-not $rollbackComplete -and $applied) {
        Write-Warning ('Original rollback copies are retained at: ' + $backup)
    }
    throw ('AAH in-place migration FAILED: ' + $reason +
           '. DO NOT UNINSTALL EITHER AAH COPY. Rollback completed=' + $rollbackComplete)
}
finally {
    # Do not delete recovery copies when a migration was applied but
    # original bytes were not verifiably restored.
    if (-not $applied -or $rollbackComplete -or (Test-Path -LiteralPath $receipt)) {
        try { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Stop }
        catch { Write-Warning ('Temporary migration files remain: ' + $work) }
    }
}
