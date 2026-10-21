# Fix137 R8 - read-only current Stock chooser/inherited gate regression.
param([string]$PackageRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference = 'Stop'
$Utf8 = New-Object -TypeName System.Text.UTF8Encoding -ArgumentList $false,$true
function Assert-R8([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Read-R8([string]$Relative) {
    return [System.IO.File]::ReadAllText((Join-Path $PackageRoot $Relative), $Utf8)
}
function Test-R8Choice([string]$Xaml,[string]$Code) {
    $markers = @(
        'ItemsSource="{Binding DataContext.FinalChoices, RelativeSource={RelativeSource AncestorType=Window}}"',
        'TextSearch.TextPath="Name"',
        'Header="Master Source"',
        'Text="{Binding Authority}"',
        'Text="{Binding Detail}"',
        'Text="{Binding FinalValue, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"',
        'IsEditable="True"',
        'MaxDropDownHeight="420"'
    )
    foreach ($marker in $markers) { if (-not $Xaml.Contains($marker)) { return $false } }
    return ($Code.Contains('public IReadOnlyList<StockReviewChoice> FinalChoices') -and
            $Code.Contains('new StockReviewChoice(item.Name.Trim(), ResolveAuthority(item)') -and
            $Code.Contains('public sealed record StockReviewChoice(string Name, string Authority, string Detail);'))
}
try {
    . (Join-Path $PSScriptRoot 'StockReviewCommitContract.ps1')
    $xaml = Read-R8 'src\AccountingAutomationHub.Desktop\Views\SalesStockBulkReviewWindow.xaml'
    $code = Read-R8 'src\AccountingAutomationHub.Desktop\Views\SalesStockBulkReviewWindow.xaml.cs'
    $updater = Read-R8 'Installer\Update-App.ps1'
    $start = $updater.IndexOf('    # Fix121: unique Item rename + visible Unit selection')
    $end = $updater.IndexOf('    # Fix122: Sales KPI', $start)
    Assert-R8 ($start -ge 0 -and $end -gt $start) 'Fix121 inherited gate boundary is missing.'
    $gate = $updater.Substring($start, $end - $start)
    Assert-R8 (Test-R8Choice $xaml $code) 'Current authority-aware chooser contract failed.'
    Assert-R8 ($gate.Contains('Fix121/Fix137 R8 authority-aware Stock Item chooser compatibility gate failed.')) 'Current compatibility branch is missing.'
    Assert-R8 ($gate.Contains('if (-not ($fix121ReviewXaml.Contains(')) 'Current chooser must fail closed.'
    $legacyMarker = 'ItemsSource="{Binding DataContext.FinalValues, RelativeSource={RelativeSource AncestorType=Window}}"'
    Assert-R8 (-not $gate.Contains(([char]39 + $legacyMarker + [char]39 + ','))) 'Obsolete string-only marker remains in the required marker list.'
    foreach ($marker in @('One row is shown for each UNIQUE imported Item Name', 'ChangeStockItemInventoryUnit', 'Manual Sales Unit Mapping', 'Manual Purchase Unit Mapping', 'Manual Sales Quantity Conversion', 'Manual Purchase Quantity Conversion', 'Tally-backed Stock Item', 'RepairLegacyManualSalesGstinFromSource', 'UpdateCustomerGstinReferences')) {
        Assert-R8 ($gate.Contains($marker)) ('Inherited Stock/GST contract was removed: ' + $marker)
    }
    foreach ($marker in @('ItemsSource="{Binding DataContext.FinalChoices, RelativeSource={RelativeSource AncestorType=Window}}"', 'TextSearch.TextPath="Name"', 'Header="Master Source"', 'Text="{Binding Authority}"', 'Text="{Binding Detail}"', 'Text="{Binding FinalValue, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"', 'IsEditable="True"', 'MaxDropDownHeight="420"')) {
        Assert-R8 (-not (Test-R8Choice ($xaml.Replace($marker,'')) $code)) ('Removal mutation accepted: ' + $marker)
    }
    Assert-R8 (-not (Test-R8Choice ($xaml.Replace('FinalChoices, RelativeSource={RelativeSource AncestorType=Window}', 'FinalValues, RelativeSource={RelativeSource AncestorType=Window}')) $code)) 'Old string-only chooser rollback was accepted.'
    foreach ($marker in @('public IReadOnlyList<StockReviewChoice> FinalChoices', 'new StockReviewChoice(item.Name.Trim(), ResolveAuthority(item)', 'public sealed record StockReviewChoice(string Name, string Authority, string Detail);')) {
        Assert-R8 (-not (Test-R8Choice $xaml ($code.Replace($marker,'')))) ('Source mutation accepted: ' + $marker)
    }
    Assert-R8 (Test-AAHStockReviewCommit $code) 'Current guarded Apply All commit/persistence contract failed.'
    Assert-R8 ($xaml.Contains('VirtualizingPanel.VirtualizationMode="Recycling"')) 'Review virtualization was removed.'
    Write-Host 'Fix137 R8 current chooser, historical gate and 12 removal/rollback mutations passed.' -ForegroundColor Green
    exit 0
} catch {
    Write-Host ('Fix137 R8 source regression FAILED: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
