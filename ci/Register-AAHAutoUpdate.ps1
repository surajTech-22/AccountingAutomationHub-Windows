# Run once on the installed Windows account to enroll the existing AAH installation.
# No second application, service or administrator privilege is required.
param([switch]$Remove)
$ErrorActionPreference='Stop'
$taskName='AAH Existing Installation Auto Update'
if ($Remove) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host 'AAH automatic update task removed.'
    exit 0
}
$source = Join-Path $PSScriptRoot 'Invoke-AAHReleaseUpdate.ps1'
if (-not (Test-Path -LiteralPath $source)) { throw "Updater helper missing: $source" }
$existing = @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AccountingAutomationHub',
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AccountingAutomationHub',
    'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\AccountingAutomationHub'
    | ForEach-Object { if (Test-Path -LiteralPath $_) { Get-ItemProperty -LiteralPath $_ } }
)
# Exactly one registered existing installation only.
$registered = @()
foreach ($key in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AccountingAutomationHub',
                   'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AccountingAutomationHub',
                   'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\AccountingAutomationHub')) {
    if (Test-Path -LiteralPath $key) { $registered += Get-ItemProperty -LiteralPath $key }
}
if ($registered.Count -ne 1) { throw "Expected one existing registered AAH installation; found $($registered.Count). Refusing to create another installation." }
if (-not (Test-Path -LiteralPath (Join-Path ([string]$registered[0].InstallLocation) 'AccountingAutomationHub.exe'))) {
    throw 'Registered installed AAH executable not found.'
}
$destination = Join-Path $env:LOCALAPPDATA 'AccountingAutomationHub\Updater'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$updater = Join-Path $destination 'Invoke-AAHReleaseUpdate.ps1'
Copy-Item -LiteralPath $source -Destination $updater -Force
$ps = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$action = New-ScheduledTaskAction -Execute $ps -Argument ('-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $updater + '"')
$triggerLogon = New-ScheduledTaskTrigger -AtLogOn -User ([Security.Principal.WindowsIdentity]::GetCurrent().Name)
$triggerHourly = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(5) -RepetitionInterval (New-TimeSpan -Hours 1) -RepetitionDuration (New-TimeSpan -Days 3650)
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 20) -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger @($triggerLogon,$triggerHourly) -Settings $settings -Description 'Updates the one registered AAH installation through its original manual updater.' -Force | Out-Null
Write-Host 'AAH release auto-check registered for logon and hourly checks. No duplicate application installed.'
