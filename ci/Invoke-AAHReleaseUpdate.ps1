# AAH: GitHub release discovery and manual-updater handoff.
# This helper does NOT replace program files; the existing UPDATE_APP.bat owns
# every protected preflight, process-exit, transactional replacement and rollback.
param([switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$repo = 'surajTech-22/AccountingAutomationHub-Windows'
$headers = @{ 'User-Agent'='AAH-Windows-AutoUpdate'; Accept='application/vnd.github+json' }
$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers $headers -TimeoutSec 20
if ($release.draft -or $release.prerelease) { throw 'Latest release is not a stable published release.' }
$tag = [string]$release.tag_name
if ($tag -notmatch '^v?(\d+\.\d+\.\d+\.\d+)$') { throw "Unsupported release version: $tag" }
$remoteVersion = [version]$Matches[1]
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AccountingAutomationHub'
$installed = Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction Stop
$installedVersion = [version]([string]$installed.DisplayVersion)
if ($remoteVersion -le $installedVersion) {
    Write-Host "AAH already current ($installedVersion)."
    exit 0
}
$assets = @($release.assets | Where-Object { $_.name -match '^AAH_.*UPDATE.*\.zip$' })
if ($assets.Count -ne 1) { throw 'Release must have exactly one AAH update ZIP asset.' }
$asset = $assets[0]
$digest = [string]$asset.digest
if ($digest -notmatch '^sha256:([0-9a-fA-F]{64})$') { throw 'Release asset lacks its GitHub SHA256 digest.' }
$expectedHash = $Matches[1].ToUpperInvariant()
if ([string]$asset.browser_download_url -notlike "https://github.com/$repo/releases/download/*") {
    throw 'Update asset is not hosted in the expected repository release.'
}
if ($CheckOnly) {
    Write-Host "AAH update available: $installedVersion -> $remoteVersion"
    exit 0
}
# Keep staging separate from the existing install and its accounting data.
$stagingRoot = Join-Path $env:LOCALAPPDATA 'AccountingAutomationHub\UpdateStaging'
New-Item -Path $stagingRoot -ItemType Directory -Force | Out-Null
$stage = Join-Path $stagingRoot ('v' + $remoteVersion.ToString())
New-Item -Path $stage -ItemType Directory -Force | Out-Null
$archive = Join-Path $stage 'update.zip'
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archive -UseBasicParsing -TimeoutSec 300
$actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if ($actual -ne $expectedHash) { Remove-Item -LiteralPath $archive -Force; throw 'Downloaded release hash mismatch.' }
$expanded = Join-Path $stage 'package'
if (Test-Path -LiteralPath $expanded) { Remove-Item -LiteralPath $expanded -Recurse -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($archive, $expanded)
$launcher = Join-Path $expanded 'UPDATE_APP.bat'
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
    throw 'Expected root UPDATE_APP.bat missing. Refusing automatic update.'
}
# Run exactly the established manual update entry point, without bypassing its gates.
# cmd.exe /c is launched outside the in-app process; the original installer
# is responsible for verified shutdown, backup, replacement and restart.
Start-Process -FilePath "$env:ComSpec" -WorkingDirectory $expanded -ArgumentList @('/d','/c', ('"' + $launcher + '"'))
Write-Host "AAH handoff to original manual updater started for $remoteVersion."
