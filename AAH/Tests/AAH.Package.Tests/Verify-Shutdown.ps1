# Runs the real helper against disposable processes, never the installed application.
param([Parameter(Mandatory=$true)][string]$PackageRoot)
$ErrorActionPreference = 'Stop'
$fixture = Join-Path $env:TEMP ('AAH-shutdown-regression-' + [Guid]::NewGuid().ToString('N'))
$diagnostic = Join-Path $env:TEMP 'AccountingAutomationHub_shutdown_regression_last.log'
$owned = New-Object 'System.Collections.Generic.List[System.Diagnostics.Process]'
function Assert-Shutdown([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Write-Diagnostic([string]$Message) {
    try { Add-Content -LiteralPath $diagnostic -Value ('[' + [DateTime]::Now.ToString('HH:mm:ss.fff') + '] ' + $Message) -Encoding UTF8 } catch { }
}
function Start-Fixture([string]$Executable, [string]$Mode, [string]$Ready) {
    $process = Start-Process -FilePath $Executable -ArgumentList ($Mode + ' "' + $Ready + '"') -WindowStyle Hidden -PassThru
    $owned.Add($process)
    [void]$process.Handle
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $Ready)) {
        if ($process.HasExited -or [DateTime]::UtcNow -ge $deadline) { throw 'Disposable shutdown fixture did not start.' }
        Start-Sleep -Milliseconds 100
    }
    return $process
}
try {
    Remove-Item -LiteralPath $diagnostic -Force -ErrorAction SilentlyContinue
    Write-Diagnostic 'R9 FIXED16 R6 native shutdown regression started.'
    $installed = Join-Path $fixture 'Installed\AccountingAutomationHub.exe'
    $other = Join-Path $fixture 'Other\AccountingAutomationHub.exe'
    New-Item -ItemType Directory -Path (Split-Path -Parent $installed) -Force | Out-Null
    New-Item -ItemType Directory -Path (Split-Path -Parent $other) -Force | Out-Null
    $source = @'
using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
public static class AahShutdownFixture {
    public static void Main(string[] args) {
        if (args[0] == "graceful") {
            using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset,
                @"Local\AccountingAutomationHub.UpdateShutdown.v1." + Process.GetCurrentProcess().Id)) {
                File.WriteAllText(args[1], "ready");
                signal.WaitOne();
            }
        } else {
            File.WriteAllText(args[1], "ready");
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
'@
    Add-Type -TypeDefinition $source -OutputAssembly $installed -OutputType ConsoleApplication
    Copy-Item -LiteralPath $installed -Destination $other
    $unrelated = Start-Fixture $other 'legacy' (Join-Path $fixture 'other.ready')
    Add-Type -Path (Join-Path $PackageRoot 'Installer\AahFileLockQuery.cs')
    $helper = Join-Path $PackageRoot 'Installer\Stop-InstalledAAH.ps1'
    $helperText = [IO.File]::ReadAllText($helper)
    Assert-Shutdown ($helperText.Contains('Get-ExactProcessPath') -and $helperText.Contains('Process]::GetProcessById')) 'R9 FIXED16 R6 verified MainModule fallback is missing.'
    Assert-Shutdown ($helperText.Contains("Source='RestartManager+ExactPathProof'") -and $helperText.Contains('Unverified file-lock owner PID')) 'R9 FIXED16 R6 Restart Manager owner isolation is missing.'
    Assert-Shutdown (-not $helperText.Contains('foreach ($ownerId in [AahFileLockQuery]::GetProcessIds($InstalledExePath)) { [void]$ids.Add($ownerId) }')) 'R9 FIXED16 R6 still promotes unverified Restart Manager owners directly.'
    $hostExe = Join-Path $PSHOME 'powershell.exe'
    $log = Join-Path $fixture 'shutdown.log'

    $graceful = Start-Fixture $installed 'graceful' (Join-Path $fixture 'graceful.ready')
    Assert-Shutdown (@([AahFileLockQuery]::GetProcessIds($installed)) -contains $graceful.Id) 'Windows file-lock query missed the running fixture executable.'
    Write-Diagnostic ('Graceful fixture PID ' + $graceful.Id + ' is a confirmed lock owner; invoking production helper.')
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File $helper -InstalledExePath $installed -LogPath $log
    Assert-Shutdown ($LASTEXITCODE -eq 0) 'Explicit update exit failed.'
    Assert-Shutdown ($graceful.WaitForExit(2000)) 'Graceful fixture remained alive.'
    Assert-Shutdown (-not $unrelated.HasExited) 'Another installation was stopped.'
    Assert-Shutdown (-not ([IO.File]::ReadAllText($log).Contains('Termination requested once'))) 'Graceful exit used forced termination.'
    Write-Diagnostic 'Graceful exact-path shutdown passed.'

    # Keep the original process handle open: exited-process bookkeeping must not
    # become the R29 false still-running / second-termination failure.
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File $helper -InstalledExePath $installed -LogPath $log
    Assert-Shutdown ($LASTEXITCODE -eq 0) 'Exited process with retained handle blocked retry.'
    Write-Diagnostic 'Retained exited-handle retry passed.'

    $legacy = Start-Fixture $installed 'legacy' (Join-Path $fixture 'legacy.ready')
    Assert-Shutdown (@([AahFileLockQuery]::GetProcessIds($installed)) -contains $legacy.Id) 'Windows file-lock query missed the legacy fixture executable.'
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File $helper -InstalledExePath $installed -LogPath $log
    Assert-Shutdown ($LASTEXITCODE -eq 0) 'Legacy background-process fallback failed.'
    Assert-Shutdown ($legacy.WaitForExit(2000)) 'Legacy fixture remained alive.'
    Assert-Shutdown (-not $unrelated.HasExited) 'Fallback stopped an unrelated installation.'
    $requests = [regex]::Matches([IO.File]::ReadAllText($log), 'Termination requested once for PID ' + $legacy.Id + ';').Count
    Assert-Shutdown ($requests -eq 1) 'Termination was repeated for the same process.'
    Write-Diagnostic 'Legacy exact-path fallback and unrelated-process isolation passed.'

    $app = [IO.File]::ReadAllText((Join-Path $PackageRoot 'src\AccountingAutomationHub.Desktop\App.xaml.cs'))
    $vm = [IO.File]::ReadAllText((Join-Path $PackageRoot 'src\AccountingAutomationHub.Desktop\ViewModels\MainWindowViewModel.cs'))
    $cleanup = $app.Substring($app.IndexOf('    private void CleanupRuntime()'))
    Assert-Shutdown ($cleanup.IndexOf('_mainViewModel?.Dispose()') -ge 0 -and $cleanup.IndexOf('_mainViewModel?.Dispose()') -lt $cleanup.IndexOf('SqliteDatabaseOptimizer.CheckpointAndTruncateWal()')) 'Accounting shutdown must precede checkpointing.'
    Assert-Shutdown ($vm.Contains('Interlocked.Exchange(ref _shutdownStarted, 1)')) 'Workspace disposal must be idempotent.'
    $dispose = $vm.Substring($vm.IndexOf('    public void Dispose()'))
    Assert-Shutdown ($dispose.IndexOf('_tallyCoordinator.Dispose()') -lt $dispose.IndexOf('_workspaceStateCache.Dispose()')) 'Tally must stop before cache drainage.'
    Assert-Shutdown ($app.Contains('StartUpdateShutdownListener();') -and $app.Contains('Dispatcher.BeginInvoke(new Action(ExitApplication))')) 'Explicit update shutdown listener is missing.'
    Write-Diagnostic 'R9 FIXED16 R6 native shutdown regression PASS.'
    Write-Host 'PASS: verified Win32/MainModule exact-path identity, Restart Manager owner isolation, graceful exit, legacy fallback, retained exited handle, single termination and unrelated-process isolation.' -ForegroundColor Green
} catch {
    $message = 'Shutdown regression FAILED: ' + $_.Exception.Message
    Write-Diagnostic $message
    Write-Output $message
    Write-Host $message -ForegroundColor Red
    exit 1
} finally {
    foreach ($process in $owned) {
        try { if (-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(2000) } } catch { }
        $process.Dispose()
    }
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue }
}
exit 0
