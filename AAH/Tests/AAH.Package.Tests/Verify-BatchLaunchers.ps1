# Accounting Automation Hub - read-only package regression suite.
# Runs against the exact production batch contract, not a second implementation.
param([string]$PackageRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference = 'Stop'
function Assert-AAH([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Replace-AAHBatchText([byte[]]$Bytes, [string]$Old, [string]$New) {
    $text = [System.Text.Encoding]::ASCII.GetString($Bytes)
    Assert-AAH (-not [regex]::IsMatch(($Old + $New), '[^\x00-\x7F]')) 'ASCII mutation helper cannot encode non-ASCII text.'
    Assert-AAH ($text.Contains($Old)) ('Negative test mutation target is missing: ' + $Old)
    $changed = $text.Replace($Old, $New)
    Assert-AAH ($changed -cne $text) 'Negative test did not change the input.'
    return ,([System.Text.Encoding]::ASCII.GetBytes($changed))
}
try {
    $Root = (Resolve-Path -LiteralPath $PackageRoot).Path
    . (Join-Path $Root 'Installer\BatchContract.ps1')
    $manifest = [System.IO.File]::ReadAllText((Join-Path $Root 'manifest.json'), (New-Object -TypeName System.Text.UTF8Encoding -ArgumentList $false,$true)) | ConvertFrom-Json
    $manifestRelease = [string]$manifest.release
    $release = Get-AAHReleaseLabel $manifestRelease
    $delimiter = $manifestRelease.IndexOf(' - ', [StringComparison]::Ordinal)
    Assert-AAH ($delimiter -gt 3) 'Manifest release description delimiter is missing.'
    $manifestPrefix = $manifestRelease.Substring(0, $delimiter)
    Assert-AAH ($manifestPrefix.StartsWith('Fix', [StringComparison]::Ordinal)) 'Manifest release prefix is invalid.'
    $expectedRelease = $manifestPrefix.Substring(3)
    Assert-AAH ([regex]::IsMatch($expectedRelease, '^[0-9]+(?: R[0-9]+)*$')) 'Manifest release prefix contains an invalid label.'
    Assert-AAH ($release -ceq $expectedRelease) 'Manifest release parser did not return the exact current manifest label.'
    $ambiguousReleaseRejected = $false
    try { [void](Get-AAHReleaseLabel ('Fix' + $release + ' R999 ambiguous description')) } catch { $ambiguousReleaseRejected = $true }
    Assert-AAH $ambiguousReleaseRejected 'Manifest release parser accepted an ambiguous R-number description without the required delimiter.'
    $files = @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force)
    $pythonPayload = @($files | Where-Object { $_.Extension -in @('.py','.pyc','.pyo','.pyd') })
    Assert-AAH ($pythonPayload.Count -eq 0) ('App-only package contains Python payload: ' + (($pythonPayload | ForEach-Object FullName) -join ', '))
    Assert-AAH (-not [bool]$manifest.external_python_packaged) 'Manifest permits an external Python payload.'
    $batchPaths = @($files | Where-Object { $_.Extension -in @('.bat','.cmd') } | Sort-Object FullName)
    foreach ($item in $batchPaths) {
        $failure = Get-AAHBatchContractFailure ([System.IO.File]::ReadAllBytes($item.FullName))
        Assert-AAH ([string]::IsNullOrEmpty($failure)) ('Batch contract failed: ' + $item.FullName + ': ' + $failure)
    }
    [byte[]]$launcher = [System.IO.File]::ReadAllBytes((Join-Path $Root 'UPDATE_APP.bat'))
    $failure = Get-AAHBatchContractFailure $launcher $release $true
    Assert-AAH ([string]::IsNullOrEmpty($failure)) ('Main launcher contract failed: ' + $failure)

    # These two mutations MUST preserve the invalid bytes. Encoding.ASCII.GetBytes
    # replaces non-ASCII characters with '?', so it is deliberately not used here.
    [byte[]]$nonAscii = [byte[]]$launcher.Clone()
    $nonAscii[$nonAscii.Length - 1] = [byte]128
    Assert-AAH ($nonAscii[$nonAscii.Length - 1] -eq 128) 'Non-ASCII mutation was lost.'
    [byte[]]$utf8Bom = New-Object byte[] ($launcher.Length + 3)
    $utf8Bom[0] = 239; $utf8Bom[1] = 187; $utf8Bom[2] = 191
    [System.Array]::Copy($launcher, 0, $utf8Bom, 3, $launcher.Length)
    [byte[]]$utf16 = [System.Text.Encoding]::Unicode.GetBytes([System.Text.Encoding]::ASCII.GetString($launcher))
    [byte[]]$badUtf16 = New-Object byte[] ($utf16.Length + 2)
    $badUtf16[0] = 255; $badUtf16[1] = 254
    [System.Array]::Copy($utf16, 0, $badUtf16, 2, $utf16.Length)
    $bad = @(
        [pscustomobject]@{ Name='UTF-8 BOM'; Bytes=$utf8Bom; Expected='UTF-8 BOM.' }
        [pscustomobject]@{ Name='UTF-16'; Bytes=$badUtf16; Expected='Non-ASCII or NUL byte.' }
        [pscustomobject]@{ Name='Non-ASCII byte'; Bytes=$nonAscii; Expected='Non-ASCII or NUL byte.' }
        [pscustomobject]@{ Name='LF-only main launcher'; Bytes=(Replace-AAHBatchText $launcher "`r`n" "`n"); Expected='Main launcher must use CRLF.' }
        [pscustomobject]@{ Name='Mismatched release'; Bytes=(Replace-AAHBatchText $launcher ('echo Fix ' + $release + ' completed.') 'echo Fix 136 completed.'); Expected='Matching completion status is missing.' }
        [pscustomobject]@{ Name='Mismatched heading'; Bytes=(Replace-AAHBatchText $launcher ('echo Accounting Automation Hub - Fix ' + $release) 'echo Accounting Automation Hub - Fix 136'); Expected='Release heading does not match the manifest.' }
        [pscustomobject]@{ Name='Missing failure guard'; Bytes=(Replace-AAHBatchText $launcher 'if errorlevel 1 goto :failed' 'rem missing failure guard'); Expected='Both stages must propagate failures.' }
        [pscustomobject]@{ Name='Missing preflight'; Bytes=(Replace-AAHBatchText $launcher 'Installer\Verify-Installer.ps1' 'Installer\Missing.ps1'); Expected='Preflight command must occur exactly once.' }
        [pscustomobject]@{ Name='Incorrect failure exit'; Bytes=(Replace-AAHBatchText $launcher 'exit /b 1' 'exit /b 0'); Expected='Exit status propagation is missing.' }
        [pscustomobject]@{ Name='Escaped log path'; Bytes=(Replace-AAHBatchText $launcher '%TEMP%' '%%TEMP%%'); Expected='Escaped log environment variable.' },
        [pscustomobject]@{ Name='Unprefixed release note'; Bytes=(Replace-AAHBatchText $launcher 'echo - Retains the Fix133 R3 PowerShell 5.1 parser repair and full preflight.' '- Retains the Fix133 R3 PowerShell 5.1 parser repair and full preflight.'); Expected='Release banner has an unprefixed command.' }
    )
    foreach ($case in $bad) {
        [byte[]]$bytes = $case.Bytes
        Assert-AAH (-not (([Convert]::ToBase64String($launcher)) -ceq ([Convert]::ToBase64String($bytes)))) ('Negative test did not mutate: ' + $case.Name)
        $rejected = Get-AAHBatchContractFailure $bytes $release $true
        Assert-AAH ($rejected -ceq $case.Expected) ('Negative test ' + $case.Name + ' returned ' + $rejected + '; expected ' + $case.Expected)
    }
    $updaterSource = [System.IO.File]::ReadAllText((Join-Path $Root 'Installer\Update-App.ps1'), (New-Object -TypeName System.Text.UTF8Encoding -ArgumentList $false,$true))
    Assert-AAH ($updaterSource.Contains('Python file found inside update package:')) 'Inherited app-only source guard was removed.'
    Assert-AAH ($updaterSource.Contains('Get-ChildItem $Root -File -Recurse -Filter *.py')) 'Inherited Python payload scan was removed.'
    $preflightSource = [IO.File]::ReadAllText((Join-Path $Root 'Installer\Verify-Installer.ps1'))
    Assert-AAH ($preflightSource.Contains('AAH Fix ' + $release + ': validating the complete source package.')) 'Preflight release heading differs from manifest.'
    Assert-AAH ($preflightSource.Contains('AAH Fix ' + $release + ' preflight FAILED:')) 'Preflight failure label differs from manifest.'
    Assert-AAH ($updaterSource.Contains('$CurrentReleaseLabel = "Fix ' + $release + ' - ')) 'Updater release label differs from manifest.'
    $updaterBytes = [System.IO.File]::ReadAllBytes((Join-Path $Root 'Installer\Update-App.ps1'))
    Assert-AAH ($updaterBytes.Length -ge 3 -and $updaterBytes[0] -eq 239 -and $updaterBytes[1] -eq 187 -and $updaterBytes[2] -eq 191) 'PowerShell 5.1 updater BOM is missing.'
    Write-Host ('Package regression passed: ' + $batchPaths.Count + ' launchers, ' + $bad.Count + ' negative cases, no Python payload.') -ForegroundColor Green
    Write-Host 'Production batch contract, inherited app-only gate and PowerShell 5.1 updater encoding retained.' -ForegroundColor Green
    Write-Host 'These source tests do not replace mandatory .NET, WPF or live Tally acceptance.'
    exit 0
} catch {
    Write-Host ('Package regression FAILED: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
