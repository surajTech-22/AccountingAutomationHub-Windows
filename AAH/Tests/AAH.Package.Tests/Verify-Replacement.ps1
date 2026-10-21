param([Parameter(Mandatory=$true)][string]$PackageRoot)
$ErrorActionPreference = 'Stop'
. (Join-Path $PackageRoot 'Installer\TransactionalPublish.ps1')
function Assert-Replacement([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
$root = Join-Path $env:TEMP ('AAH-replacement-test-' + [Guid]::NewGuid().ToString('N'))
Write-Host 'Starting native replacement regression (temporary rollback only; no retained app backup).' -ForegroundColor Cyan
$held = $null
try {
    $install = Join-Path $root 'Installed'
    $publish = Join-Path $root 'Publish'
    New-Item -Path $install,$publish -ItemType Directory -Force | Out-Null
    $exe = Join-Path $install 'AccountingAutomationHub.exe'
    [IO.File]::WriteAllText($exe, 'OLD')
    [IO.File]::WriteAllText((Join-Path $publish 'AccountingAutomationHub.exe'), 'NEW')
    [IO.File]::WriteAllText((Join-Path $install 'client-data.txt'), 'KEEP DATA')
    # A retained file object prevents the former exclusive Open and overwrites,
    # but permits rename. Exercise the actual production replacement function.
    $held = [IO.File]::Open($exe, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::Read -bor [IO.FileShare]::Delete))
    $exclusiveFailed = $false
    try { $probe = [IO.File]::Open($exe,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None); $probe.Dispose() } catch { $exclusiveFailed = $true }
    Assert-Replacement $exclusiveFailed 'Fixture did not reproduce the former exclusive-open failure.'
    Install-AAHPublish $publish $install
    Assert-Replacement ([IO.File]::ReadAllText($exe) -eq 'NEW') 'Replacement did not install new bytes.'
    Assert-Replacement ($held.ReadByte() -eq 79) 'Original file object was overwritten.'
    Assert-Replacement ([IO.File]::ReadAllText((Join-Path $install 'client-data.txt')) -eq 'KEEP DATA') 'Non-payload data changed.'
    $held.Dispose(); $held = $null

    # Second file denies rename. Verify the first replacement is reversed and
    # both originals are restored, with no partially installed release.
    [IO.File]::WriteAllText($exe,'OLD AGAIN')
    $blocked = Join-Path $install 'blocked.dll'
    [IO.File]::WriteAllText($blocked,'OLD DLL')
    [IO.File]::WriteAllText((Join-Path $publish 'blocked.dll'),'NEW DLL')
    $held = [IO.File]::Open($blocked,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $failed = $false
    try { Install-AAHPublish $publish $install } catch { $failed = $true }
    Assert-Replacement $failed 'A rename-denying lock was ignored.'
    Assert-Replacement ([IO.File]::ReadAllText($exe) -eq 'OLD AGAIN') 'Earlier file was not rolled back.'
    Assert-Replacement ([IO.File]::ReadAllText($blocked) -eq 'OLD DLL') 'Blocked original was altered.'
    $held.Dispose(); $held = $null
    Install-AAHPublish $publish $install
    Assert-Replacement ([IO.File]::ReadAllText($exe) -eq 'NEW') 'Retry after rollback failed.'
    $completedAfterRetry = @(Get-ChildItem -LiteralPath $install -Directory -Force -Filter '.aah-swap-*' -ErrorAction SilentlyContinue)
    Assert-Replacement ($completedAfterRetry.Count -eq 0) 'Completed replacement/rollback transaction folders were retained after retry.'

    # Simulate interruption after moving an original, before promoting the staged
    # file. The next invocation must recover using its persisted journal.
    $tx = Join-Path $install '.aah-swap-interruption-test'
    New-Item -Path (Join-Path $tx 'old') -ItemType Directory -Force | Out-Null
    $hash = Get-AAHFileHash $exe
    $journal = [pscustomobject]@{ Schema=1; InstallDir=$install; Entries=@([pscustomobject]@{ Relative='AccountingAutomationHub.exe'; Existed=$true; OldHash=$hash; NewHash=$hash }) }
    $journal | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $tx 'journal.json') -Encoding UTF8
    [IO.File]::Move($exe,(Join-Path $tx 'old\AccountingAutomationHub.exe'))
    Install-AAHPublish $publish $install
    Assert-Replacement (-not (Test-Path -LiteralPath $tx)) 'Recovered interrupted transaction folder was retained after successful recovery.'
    Assert-Replacement ([IO.File]::ReadAllText($exe) -eq 'NEW') 'Recovery did not complete installation.'
    Assert-Replacement ([IO.File]::ReadAllText((Join-Path $install 'client-data.txt')) -eq 'KEEP DATA') 'Retry changed client data.'
    $remainingTransactions = @(Get-ChildItem -LiteralPath $install -Directory -Force -Filter '.aah-swap-*' -ErrorAction SilentlyContinue)
    Assert-Replacement ($remainingTransactions.Count -eq 0) 'Successful replacement retained temporary old-application transaction data.'
    Write-Host 'PASS: retained file handle, unchanged original bytes, rename-denying rollback, retry, interrupted recovery, non-payload preservation and no retained completed transaction folders.' -ForegroundColor Green
} catch {
    Write-Host ('Replacement regression FAILED: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
} finally {
    if ($null -ne $held) { $held.Dispose() }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}
exit 0
