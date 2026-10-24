# AAH automatic release handoff. Must be launched outside the installed application.
# Never creates another AAH installation: only the existing manual updater may replace files.
param([switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$repo = 'surajTech-22/AccountingAutomationHub-Windows'
$root = Join-Path $env:LOCALAPPDATA 'AccountingAutomationHub\UpdateStaging'
New-Item -ItemType Directory -Force -Path $root | Out-Null
$mutex = New-Object System.Threading.Mutex($false, 'Local\AAH.ReleaseUpdateHandoff')
$owns = $false
try {
    $owns = $mutex.WaitOne(0)
    if (-not $owns) { Write-Host 'An AAH update check is already running.'; exit 0 }
    $keys = @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AccountingAutomationHub',
              'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AccountingAutomationHub',
              'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\AccountingAutomationHub')
    $installs = @($keys | ForEach-Object {
        if (Test-Path -LiteralPath $_) { Get-ItemProperty -LiteralPath $_ }
    })
    if ($installs.Count -ne 1) {
        throw "Found $($installs.Count) registered AAH installations. Resolve duplicate installations before automatic replacement; no new copy will be created."
    }
    $installed = $installs[0]
    $installRoot = [string]$installed.InstallLocation
    if ([string]::IsNullOrWhiteSpace($installRoot)) {
        throw 'Existing AAH installation has no registered InstallLocation.'
    }
    $exe = Join-Path $installRoot 'AccountingAutomationHub.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Registered existing AAH executable missing: $exe"
    }
    $localVersion = [version]([System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion)
    $headers = @{'User-Agent'='AAH-Windows-AutoUpdate';Accept='application/vnd.github+json'}
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers $headers -TimeoutSec 25
    if ($release.draft -or $release.prerelease) { throw 'Release is not stable.' }
    $tag = [string]$release.tag_name
    if ($tag -notmatch '^v?(\d+\.\d+\.\d+\.\d+)$') { throw "Invalid release version $tag" }
    $nextVersion = [version]$Matches[1]
    if ($nextVersion -le $localVersion) { Write-Host "AAH current: $localVersion"; exit 0 }
    $assets = @($release.assets | Where-Object { $_.name -match '^AAH_.*UPDATE.*\.zip$' })
    if ($assets.Count -ne 1) { throw 'No uniquely identified AAH manual-updater ZIP release.' }
    $asset = $assets[0]
    if ([string]$asset.digest -notmatch '^sha256:([a-fA-F0-9]{64})$') { throw 'Missing release SHA256 digest.' }
    $expectedHash = $Matches[1].ToUpperInvariant()
    $url = [string]$asset.browser_download_url
    if (-not $url.StartsWith("https://github.com/$repo/releases/download/",[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Release URL is not within the expected repository.'
    }
    if ($CheckOnly) { Write-Host "Update available: $localVersion -> $nextVersion"; exit 0 }
    $stage = Join-Path $root ("v" + $nextVersion.ToString())
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    $zip = Join-Path $stage 'AAH_UPDATE.zip'
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $zip -TimeoutSec 300
    if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $expectedHash) {
        Remove-Item -LiteralPath $zip -Force
        throw 'Update ZIP SHA256 mismatch.'
    }
    $package = Join-Path $stage 'package'
    if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Recurse -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # Reject paths that escape staging before using ExtractToDirectory.
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $base = [System.IO.Path]::GetFullPath($package + [IO.Path]::DirectorySeparatorChar)
        foreach ($entry in $archive.Entries) {
            $dest = [System.IO.Path]::GetFullPath((Join-Path $package $entry.FullName))
            if (-not $dest.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)) {
                throw "Unsafe ZIP entry: $($entry.FullName)"
            }
        }
    } finally { $archive.Dispose() }
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $package)
    $launcher = Join-Path $package 'UPDATE_APP.bat'
    if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
        throw 'Original UPDATE_APP.bat missing from root of ZIP.'
    }
    $installedFile = [System.IO.Path]::GetFullPath($exe)
    $packageFile = [System.IO.Path]::GetFullPath($launcher)
    if ($packageFile.StartsWith([System.IO.Path]::GetFullPath($installRoot),[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to run updater inside application installation.'
    }
    # The original updater controls process shutdown, transactional publish, rollback and launch.
    Start-Process -FilePath $env:ComSpec -WorkingDirectory $package -ArgumentList @('/d','/c',('"' + $launcher + '"'))
    Write-Host "Delegated $nextVersion to the existing manual updater for $installedFile"
} finally {
    if ($owns) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
