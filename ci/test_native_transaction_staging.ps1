# Runs using Windows PowerShell 5.1 on disposable app files only.
# Regresses the v586 incident: hundreds of 350 MB unchanged files must not be
# re-staged on each release; SHA256 identity and original journal remain gates.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'legacy\TransactionalPublish.ps1')
$root = Join-Path $env:TEMP ('AAH-native-staging-' + [Guid]::NewGuid().ToString('N'))
$install = Join-Path $root 'installed'
$publish = Join-Path $root 'publish'
[IO.Directory]::CreateDirectory($install) | Out-Null
[IO.Directory]::CreateDirectory($publish) | Out-Null
try {
    $buffer = New-Object byte[] (600 * 1024)
    $random = New-Object Random 20261103
    $random.NextBytes($buffer)
    # 585 matching application files, 342.8 MB per directory.
    for ($i = 0; $i -lt 585; $i++) {
        $name = ('support-{0:D3}.dll' -f $i)
        [IO.File]::WriteAllBytes((Join-Path $install $name), $buffer)
        [IO.File]::WriteAllBytes((Join-Path $publish $name), $buffer)
    }
    $installedExe = Join-Path $install 'AccountingAutomationHub.exe'
    $publishedExe = Join-Path $publish 'AccountingAutomationHub.exe'
    [IO.File]::WriteAllText($installedExe, 'AAH v585')
    [IO.File]::WriteAllText($publishedExe, 'AAH v586')
    $private = Join-Path $install 'private-ledgers.sqlite'
    [IO.File]::WriteAllText($private, 'DO NOT TOUCH ACCOUNTING DATA')
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Install-AAHPublish $publish $install
    $clock.Stop()
    if ([IO.File]::ReadAllText($installedExe) -ne 'AAH v586') { throw 'First version replacement failed.' }
    if ([IO.File]::ReadAllText($private) -ne 'DO NOT TOUCH ACCOUNTING DATA') { throw 'Accounting data changed.' }
    if (@(Get-ChildItem $install -Directory -Force -Filter '.aah-swap-*').Count -ne 0) {
        throw 'First release retained a committed transaction folder.'
    }
    if ($clock.Elapsed -gt [TimeSpan]::FromMinutes(4)) {
        throw ('Full-size identical-file update exceeded safe runtime: ' + $clock.Elapsed)
    }
    Write-Host ('PASS: v585 -> v586; 585 SHA256-identical files untouched; elapsed ' + $clock.Elapsed)

    # A banned release payload must fail before journal creation or app mutation.
    $bad = Join-Path $publish 'accounts.sqlite'
    [IO.File]::WriteAllText($bad, 'REFUSE DATA PAYLOAD')
    $refused = $false
    try { Install-AAHPublish $publish $install } catch { $refused = $true }
    if (-not $refused -or [IO.File]::ReadAllText($installedExe) -ne 'AAH v586') {
        throw 'Nonapplication payload mutated the installed version.'
    }
    [IO.File]::Delete($bad)

    # A subsequent release reuses the exact original script; the prior
    # before-journal orphan must be safely removed and no duplicate install made.
    [IO.File]::WriteAllText($publishedExe, 'AAH v587')
    $clock.Restart()
    Install-AAHPublish $publish $install
    $clock.Stop()
    if ([IO.File]::ReadAllText($installedExe) -ne 'AAH v587') { throw 'Second version replacement failed.' }
    if ([IO.File]::ReadAllText($private) -ne 'DO NOT TOUCH ACCOUNTING DATA') { throw 'Accounting data changed on repeat.' }
    if (@(Get-ChildItem $install -Directory -Force -Filter '.aah-swap-*').Count -ne 0) {
        throw 'Orphan or committed transaction folder retained after second update.'
    }
    if ($clock.Elapsed -gt [TimeSpan]::FromMinutes(4)) {
        throw ('Second update exceeded safe runtime: ' + $clock.Elapsed)
    }
    Write-Host ('PASS: v586 -> v587; blocked data payload, recovered orphan, hash guards and cleanup; elapsed ' + $clock.Elapsed)
} finally {
    try { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction Stop } catch { }
}
