param([Parameter(Mandatory=$true)][string]$PackageRoot)
$ErrorActionPreference = 'Stop'
function Assert-R7([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
$Utf8 = New-Object -TypeName System.Text.UTF8Encoding -ArgumentList $false,$true
# R24: Verify exact current R24 MainWindow source, then present its strictly
# approved R23 baseline to the inherited Repair 12 hash/Stock Review checks.
# No production source is changed and arbitrary edits cannot bypass the pin.
$Root = $PackageRoot
. (Join-Path $PackageRoot 'Installer\Compat-R24ExactProof.ps1')
function Read-Strict([string]$Relative) { return [System.IO.File]::ReadAllText((Join-Path $PackageRoot $Relative), $Utf8) }
try {
    . (Join-Path $PSScriptRoot 'StockReviewCommitContract.ps1')
    $xaml = Read-Strict 'src\AccountingAutomationHub.Desktop\Views\SalesStockBulkReviewWindow.xaml'
    $code = Read-Strict 'src\AccountingAutomationHub.Desktop\Views\SalesStockBulkReviewWindow.xaml.cs'
    $main = Read-Strict 'src\AccountingAutomationHub.Desktop\ViewModels\MainWindowViewModel.cs'
    $main = Undo-R24ApprovedSource 'src/AccountingAutomationHub.Desktop/ViewModels/MainWindowViewModel.cs' $main
    Assert-R7 ($xaml.Contains('x:Name="ReviewGrid"')) 'Review grid commit target missing.'
    Assert-R7 ($xaml.Contains('Header="Master Source"')) 'Stock review Master Source column missing.'
    Assert-R7 ($xaml.Contains('TextSearch.TextPath="Name"')) 'Authority-aware Stock chooser text path missing.'
    Assert-R7 ($xaml.Contains('VirtualizingPanel.VirtualizationMode="Recycling"')) 'Stock review virtualization missing.'
    Assert-R7 (Test-AAHStockReviewCommit $code) 'Current guarded Apply All commit/persistence contract failed.'
    Assert-R7 ($code.Contains('ResolveAuthority(currentMaster)')) 'Current Stock Master authority projection missing.'
    Assert-R7 ($code.Contains('new StockReviewChoice(item.Name.Trim(), ResolveAuthority(item)')) 'Authority-aware Final Stock Item choices missing.'
    # R7 originally required application-idle hidden presentation warming. R43 deliberately keeps
    # hidden WPF pages unbound so background cache completion cannot rebuild large ObservableCollections
    # while the user is scrolling. Accept the historical warm-up contract, the R43
    # visible-page-only hydration contract, or the exact Repair 12 bounded idle
    # presentation-prewarm supersession; never weaken Stock review authority/commit checks.
    if ($main.Contains('R8 Repair 12: a module-ready event invalidates only presentations that')) {
        $fastProofPath = Join-Path $PackageRoot 'Tests\AAH.TallyInventory.Tests\FIXED41R8R12FastPresentationProof.json'
        Assert-R7 (Test-Path -LiteralPath $fastProofPath -PathType Leaf) 'Repair 12 fast-presentation proof missing.'
        $fastProof = (Read-Strict 'Tests\AAH.TallyInventory.Tests\FIXED41R8R12FastPresentationProof.json' | ConvertFrom-Json)
        $mainProof = @($fastProof.files | Where-Object { $_.path -eq 'src/AccountingAutomationHub.Desktop/ViewModels/MainWindowViewModel.cs' })
        Assert-R7 ($mainProof.Count -eq 1) 'Repair 12 MainWindow lineage proof is ambiguous.'
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $actualTextHash = ([System.BitConverter]::ToString($sha.ComputeHash($Utf8.GetBytes($main)))).Replace('-','').ToLowerInvariant()
        } finally { $sha.Dispose() }
        Assert-R7 ($actualTextHash -eq [string]$mainProof[0].current_text_sha256) 'Repair 12 MainWindow fast-presentation hash mismatch.'
        Assert-R7 ($main.Contains('QueueHiddenModulePresentationWarm(update.Snapshot, update);')) 'Repair 12 module-ready hidden-presentation handoff missing.'
        Assert-R7 ($main.Contains('QueueSafePresentationWarmSlice(clientId, financialYear, generation, 0);')) 'Repair 12 bounded idle warm scheduler missing.'
        Assert-R7 ($main.Contains('TryGetReadyPresentation(')) 'Repair 12 ready-stream presentation barrier missing.'
        Assert-R7 ($main.Contains('one complete page presentation per idle slice')) 'Repair 12 page-level idle slicing marker missing.'
        Assert-R7 ($main.Contains('Only the persisted')) 'Repair 12 Banking/Cash hidden-repair isolation marker missing.'
        Assert-R7 ($main.Contains('QueueAtomicVisiblePageRefresh(page, client);')) 'Repair 12 retained atomic visible-page handoff missing.'
    } elseif ($main.Contains('QueueHiddenModulePresentationWarm(latest, update);')) {
        Assert-R7 ($main.Contains('DispatcherPriority.ApplicationIdle')) 'Presentation warm must run at application idle.'
        Assert-R7 (-not $main.Contains('case ClientWorkspaceModule.Banking:
                    case ClientWorkspaceModule.LedgerMapping:
                    case ClientWorkspaceModule.Cash:')) 'Hidden presentation warm must not start Banking/Cash repair paths.'
    } else {
        Assert-R7 ($main.Contains('Keep hidden pages unbound.')) 'R43 visible-only presentation contract missing.'
        Assert-R7 ($main.Contains('VisibleModulesForPage(page).Contains(update.Module)')) 'R43 visible-module completion filter missing.'

        if ($main.Contains('WarmVisibleWorkspaceModule(update.Snapshot, update.Module);')) {
            # Historical R43 contract: one completed visible module hydrates directly.
            Assert-R7 ($main.Contains('QueueWorkspaceNavigationRefresh();')) 'R43 navigation refresh handoff missing.'
        } else {
            # FIXED41 R8 Repair 8 supersedes the direct per-stream paint with one atomic
            # visible-page refresh. Accept it only when the exact approved Repair 8 text
            # hash from the retained lineage proof matches; marker checks alone are not enough.
            $atomicProofPath = Join-Path $PackageRoot 'Tests\AAH.TallyInventory.Tests\FIXED41R8R8AtomicTabProof.json'
            Assert-R7 (Test-Path -LiteralPath $atomicProofPath -PathType Leaf) 'Repair 8 atomic presentation proof missing.'
            $atomicProof = (Read-Strict 'Tests\AAH.TallyInventory.Tests\FIXED41R8R8AtomicTabProof.json' | ConvertFrom-Json)
            $mainProof = @($atomicProof.files | Where-Object { $_.path -eq 'src/AccountingAutomationHub.Desktop/ViewModels/MainWindowViewModel.cs' })
            Assert-R7 ($mainProof.Count -eq 1) 'Repair 8 MainWindow lineage proof is ambiguous.'
            $sha = [System.Security.Cryptography.SHA256]::Create()
            try {
                $actualTextHash = ([System.BitConverter]::ToString($sha.ComputeHash($Utf8.GetBytes($main)))).Replace('-','').ToLowerInvariant()
            } finally { $sha.Dispose() }
            Assert-R7 ($actualTextHash -eq [string]$mainProof[0].current_text_sha256) 'Repair 8 MainWindow atomic presentation hash mismatch.'
            Assert-R7 ($main.Contains('QueueAtomicVisiblePageRefresh(page, client);')) 'Repair 8 atomic visible-page handoff missing.'
            Assert-R7 ($main.Contains('private void QueueAtomicVisiblePageRefresh(PageViewModel page, ClientProfile client)')) 'Repair 8 atomic visible-page scheduler missing.'
            Assert-R7 ($main.Contains('WarmVisiblePageFromSnapshot(page, latest);')) 'Repair 8 atomic snapshot publication missing.'
            Assert-R7 ($main.Contains('modules.Any(module => !_workspaceStateCache.IsModuleReady(client.Id, client.FinancialYear, module))')) 'Repair 8 complete-stream readiness barrier missing.'
            Assert-R7 ($main.Contains('QueueWorkspaceNavigationRefresh();')) 'Repair 8 navigation refresh handoff missing.'
        }
        Assert-R7 (-not $main.Contains('QueueHiddenModulePresentationWarm(latest, update);')) 'R43 hidden presentation warming is called again.'
        Assert-R7 (-not $main.Contains('QueueAllSafeModulePresentations(openingSnapshot);')) 'R43 all-hidden presentation warming is called again.'
    }
    Write-Host 'Fix137 R7 latest-behavior Stock review/source contracts passed.' -ForegroundColor Green
    exit 0
} catch {
    Write-Host ('Fix137 R7 Stock review/source regression FAILED: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
