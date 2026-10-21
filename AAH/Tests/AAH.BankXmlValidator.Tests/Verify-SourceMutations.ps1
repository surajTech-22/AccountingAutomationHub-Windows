# Fix137 R5 protected-source mutation regressions; Windows PowerShell 5.1.
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $Root 'Installer\Verify-ProtectedBankValidator.ps1') -Library
function Assert-AAH([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Reject-AAH([string]$Name, [scriptblock]$Action) {
    try { & $Action | Out-Null }
    catch { Write-Host ('PASS: rejected ' + $Name); return }
    throw ('Unsafe mutation was accepted: ' + $Name)
}
try {
    $source = [System.IO.File]::ReadAllText((Join-Path $Root 'src\AccountingAutomationHub.Infrastructure\Tally\TallyBankXmlContractValidator.cs'), $Utf8)
    $config = [System.IO.File]::ReadAllText((Join-Path $Root 'src\AccountingAutomationHub.Infrastructure\Tally\.editorconfig'), $Utf8)
    [void](Assert-AAHProtectedValidatorText $source $config)
    Reject-AAH 'missing DoesNotReturn' { Assert-AAHProtectedValidatorText ($source.Replace($Annotation, $OriginalMethod)) $config }
    Reject-AAH 'missing compiler-flow using' { Assert-AAHProtectedValidatorText ($source.Replace($Prefix, '')) $config }
    Reject-AAH 'changed throwing helper' { Assert-AAHProtectedValidatorText ($source.Replace('throw new InvalidOperationException(', 'throw new ArgumentException(')) $config }
    Reject-AAH 'relaxed root validation' { Assert-AAHProtectedValidatorText ($source.Replace('Root element must be ENVELOPE.', 'Root element may be anything.')) $config }
    Reject-AAH 'modified voucher allowlist' { Assert-AAHProtectedValidatorText ($source.Replace('"ALLLEDGERENTRIES.LIST",', '"ALLLEDGERENTRIES.LIST", "UNSAFE",')) $config }
    Reject-AAH 'nullable diagnostic suppression' { Assert-AAHProtectedValidatorText $source ($config.Replace('severity = error', 'severity = none')) }
    Reject-AAH 'weakened nullable diagnostic' { Assert-AAHProtectedValidatorText $source ($config.Replace('severity = error', 'severity = warning')) }
    Reject-AAH 'added warning pragma' { Assert-AAHProtectedValidatorText ($source + [char]10 + '#pragma warning disable CS8602') $config }
    Write-Host 'Fix137 R5 protected-source mutation regressions passed: 8/8.' -ForegroundColor Green
    exit 0
} catch {
    Write-Host ('Fix137 R5 protected-source mutation regressions FAILED: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
