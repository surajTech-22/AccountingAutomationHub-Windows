# Read-only check of AAH installations: registry entries, install folders,
# executable versions, and the path of any running AAH process.
# Does not uninstall, stop, replace or delete anything.
[CmdletBinding()]
param([switch]$NoReport)

$ErrorActionPreference = 'Stop'
$entries = @()
$registryLocations = @(
    'Registry::HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall',
    'Registry::HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Uninstall',
    'Registry::HKEY_LOCAL_MACHINE\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
)
foreach ($parent in $registryLocations) {
    if (-not (Test-Path -LiteralPath $parent)) { continue }
    foreach ($key in @(Get-ChildItem -LiteralPath $parent -ErrorAction SilentlyContinue)) {
        $props = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
        if (-not $props -or -not $props.DisplayName -or
            $props.DisplayName -notmatch '(?i)^(Accounting Automation H|AAH(?:\s|$))') { continue }
        $location = [string]$props.InstallLocation
        if ([string]::IsNullOrWhiteSpace($location)) {
            $uninstaller = [string]$props.UninstallString
            if ($uninstaller -match '^\s*"?([^"]+?\\unins\d*\.exe)"?') {
                $location = Split-Path -Path $Matches[1] -Parent
            } elseif ($uninstaller -match '^\s*"?([^"]+?\\(?:Uninstall|uninstall)[^"]*\.exe)"?') {
                $location = Split-Path -Path $Matches[1] -Parent
            }
        }
        $binary = if ($location) { Join-Path $location 'AccountingAutomationHub.exe' } else { '' }
        $version = if ($binary -and (Test-Path -LiteralPath $binary)) {
            (Get-Item -LiteralPath $binary).VersionInfo.FileVersion
        } else { '' }
        $entries += [pscustomobject]@{
            DisplayName = [string]$props.DisplayName
            DisplayVersion = [string]$props.DisplayVersion
            EstimatedMB = if ($props.EstimatedSize) { [math]::Round([double]$props.EstimatedSize / 1024, 1) } else { $null }
            InstallLocation = $location
            ExecutableVersion = $version
            AppRegistryId = [string]$key.PSChildName
            RegistryPath = [string]$key.Name
            UninstallString = [string]$props.UninstallString
        }
    }
}
$running = @(Get-CimInstance Win32_Process -Filter "Name = 'AccountingAutomationHub.exe'" -ErrorAction SilentlyContinue |
    Select-Object ProcessId, ExecutablePath, CommandLine)
$distinct = @($entries | Where-Object { $_.InstallLocation } |
    Select-Object -ExpandProperty InstallLocation -Unique)
Write-Host ('AAH uninstall records: {0}' -f @($entries).Count)
Write-Host ('Distinct AAH installation directories: {0}' -f $distinct.Count)
$entries | Format-Table DisplayName, DisplayVersion, EstimatedMB, ExecutableVersion, InstallLocation, AppRegistryId -AutoSize | Out-Host
if ($running.Count) {
    Write-Host 'Running AAH processes:'
    $running | Select-Object ProcessId, ExecutablePath | Format-Table -AutoSize | Out-Host
}
if (@($entries).Count -gt 1) {
    Write-Warning 'Multiple AAH entries exist. Do NOT uninstall either copy until their executable and data paths are verified.'
}
if (-not $NoReport) {
    $downloads = Join-Path $env:USERPROFILE 'Downloads'
    if (-not (Test-Path $downloads)) { $downloads = $env:TEMP }
    $output = Join-Path $downloads ('AAH_Installation_Audit_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '.json')
    @{
        CapturedOn = (Get-Date).ToString('o')
        Entries = $entries
        Running = @($running | Select-Object ProcessId, ExecutablePath)
        DistinctInstallationCount = $distinct.Count
        ChangesMade = $false
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $output -Encoding UTF8
    Write-Host ('Read-only audit saved to: ' + $output)
}
