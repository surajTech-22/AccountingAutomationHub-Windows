# Exercise the production output classifier using a disposable package fixture.
param([string]$PackageRoot)
$ErrorActionPreference = 'Stop'
. (Join-Path $PackageRoot 'Installer\PackageBuildOutputs.ps1')
function Assert-Retry([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$fixture = Join-Path $env:TEMP ('AAH-package-retry-' + [Guid]::NewGuid().ToString('N'))
try {
    $manifest = [pscustomobject]@{ files = @(
        [pscustomobject]@{ path='src/App/App.csproj' },
        [pscustomobject]@{ path='src/App/Main.cs' },
        [pscustomobject]@{ path='Tests/Regression/Regression.csproj' }
    ) }
    foreach ($entry in $manifest.files) {
        $path = Join-Path $fixture $entry.path
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        [IO.File]::WriteAllText($path, 'source fixture')
    }
    $roots = @(Get-AAHBuildOutputRoots $manifest $fixture)
    Assert-Retry ($roots.Count -eq 4) 'Expected exactly bin/obj for two manifest-listed projects.'
    foreach ($retry in 1..2) {
        foreach ($relative in @('src/App/obj/project.assets.json','Tests/Regression/bin/Release/test.dll')) {
            $path = Join-Path $fixture $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
            [IO.File]::WriteAllText($path, 'generated')
        }
        $sourceFiles = @(Get-ChildItem -LiteralPath $fixture -Recurse -File | Where-Object { -not (Test-AAHBuildOutput $_.FullName $roots) })
        Assert-Retry ($sourceFiles.Count -eq 3) 'Retry includes generated files as source.'
        foreach ($directory in $roots) { if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force } }
        Assert-Retry (@(Get-ChildItem -LiteralPath $fixture -Recurse -File).Count -eq 3) 'Cleanup removed source or retained generated output.'
    }
    foreach ($relative in @('src/App/Main.cs','src/App/obj-extra/extra.cs','src/Unknown/bin/extra.dll','extra.txt')) {
        Assert-Retry (-not (Test-AAHBuildOutput (Join-Path $fixture $relative) $roots)) ('Unexpected exclusion: ' + $relative)
    }
    $manifest.files += [pscustomobject]@{ path='src/App/obj/owned.txt' }
    $ownedRoots = @(Get-AAHBuildOutputRoots $manifest $fixture)
    Assert-Retry (-not (Test-AAHBuildOutput (Join-Path $fixture 'src/App/obj/owned.txt') $ownedRoots)) 'Manifest-owned content became disposable.'
    # Load only the pure path predicate; never execute the shutdown helper in a test.
    $stopPath = Join-Path $PackageRoot 'Installer\Stop-InstalledAAH.ps1'
    $parseTokens = $null; $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($stopPath, [ref]$parseTokens, [ref]$parseErrors)
    Assert-Retry ($parseErrors.Count -eq 0) 'Shutdown helper parse failed.'
    $predicate = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-AAHExecutablePath' }, $true)
    Assert-Retry ($null -ne $predicate) 'Installed-path identity predicate missing.'
    . ([scriptblock]::Create($predicate.Extent.Text))
    $expected = Join-Path $fixture 'Installed\AccountingAutomationHub.exe'
    Assert-Retry (Test-AAHExecutablePath $expected.ToUpperInvariant() $expected) 'Case-insensitive Windows identity failed.'
    Assert-Retry (-not (Test-AAHExecutablePath (Join-Path $fixture 'Other\AccountingAutomationHub.exe') $expected)) 'Unrelated AAH copy matched.'
    Assert-Retry (-not (Test-AAHExecutablePath '' $expected)) 'Unknown executable identity matched.'
    Write-Host 'PASS: package retry, exact output roots, source preservation and owned-output exclusion.' -ForegroundColor Green
    exit 0
} catch {
    Write-Host ('Package retry regression FAILED: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
} finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
