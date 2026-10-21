param([Parameter(Mandatory=$true)][string]$PackageRoot)
$ErrorActionPreference = 'Stop'
$fixture = Join-Path $env:TEMP ('AAH-startup-test-' + [Guid]::NewGuid().ToString('N'))
try {
    $app = [IO.File]::ReadAllText((Join-Path $PackageRoot 'src\AccountingAutomationHub.Desktop\App.xaml.cs')).Replace("`r`n", "`n")
    $paint = $app.IndexOf('ShowWorkspaceSelectorInHost(initial: true);', [System.StringComparison]::Ordinal)
    if ($paint -lt 0) { throw 'Workspace Selector paint marker is missing.' }
    $prompt = $app.IndexOf('_ = Dispatcher.BeginInvoke(new Action(HandlePendingPortalCredentialPrompt), DispatcherPriority.Loaded);', $paint, [System.StringComparison]::Ordinal)
    $preload = $app.IndexOf('QueueDeferredRuntimeWarmup();', $paint, [System.StringComparison]::Ordinal)
    $ready = $app.IndexOf('if (host.IsVisible) ConfirmStartupReady();', $paint, [System.StringComparison]::Ordinal)
    if ($prompt -lt 0 -or $preload -lt 0 -or $ready -lt 0) { throw 'Workspace secure-prompt / preload / readiness markers are incomplete.' }
    if ($prompt -lt $paint -or $prompt -gt $preload) { throw 'Secure linked-folder prompt scheduling must remain after Workspace paint and before preload.' }
    if ($preload -lt $paint -or $ready -lt $preload) { throw 'Preload must be scheduled after Workspace paint and before updater readiness.' }
    New-Item -ItemType Directory -Path $fixture -Force | Out-Null
    $source = @'
using System;
using System.IO;
using System.Threading;
public static class StartupFixture {
    public static void Main(string[] args) {
        if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "exit.txt"))) return;
        string token = args[0].Substring("--update-ready=".Length);
        using (var ready = EventWaitHandle.OpenExisting(@"Local\AccountingAutomationHub.Ready." + token)) {
            ready.Set();
            Thread.Sleep(2000);
        }
    }
}
'@
    $exe = Join-Path $fixture 'StartupFixture.exe'
    Add-Type -TypeDefinition $source -OutputAssembly $exe -OutputType ConsoleApplication
    $helper = Join-Path $PackageRoot 'Installer\Start-AAHVerified.ps1'
    $hostExe = Join-Path $PSHOME 'powershell.exe'
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File $helper -InstalledExe $exe -LogPath (Join-Path $fixture 'startup.log')
    if ($LASTEXITCODE -ne 0) { throw 'Ready startup was rejected.' }
    Set-Content -LiteralPath (Join-Path $fixture 'exit.txt') -Value 'exit'
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File $helper -InstalledExe $exe -LogPath (Join-Path $fixture 'startup.log') -ExpectedFailure
    if ($LASTEXITCODE -eq 0) { throw 'A process exiting before readiness was reported as successful.' }
    Write-Host 'Startup readiness and early-exit regressions passed.' -ForegroundColor Green
    exit 0
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
} finally {
    # The disposable fixture exits itself after two seconds; it has no app data or workers.
    Start-Sleep -Seconds 2
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
