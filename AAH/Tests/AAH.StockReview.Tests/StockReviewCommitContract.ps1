# Current Stock Review Apply All contract, shared by inherited R7/R8 and R11.
# Read-only source check: preserve the fail-closed R10 edit/persistence handoff.
function Test-AAHStockReviewCommit([string]$Code) {
    $start = $Code.IndexOf('private async void ApplyAll_Click(')
    if ($start -lt 0) { return $false }
    $end = $Code.IndexOf('private void Cancel_Click(', $start)
    if ($end -le $start) { return $false }
    $body = $Code.Substring($start, $end - $start)
    $required = @(
        'if (_isApplying) return;',
        'if (!ReviewGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||',
        '!ReviewGrid.CommitEdit(DataGridEditingUnit.Row, true))',
        'ValidationText = "Complete the current Stock Item edit before applying.";',
        'if (!ValidateRows()) return;',
        'Mode == SalesStockToolsWindow.ItemAliasMode && ApplyItemMappingsAsync is null',
        'ValidationText = "The Stock mapping save service is unavailable.',
        'var selections = GetItemMappingSelections();',
        'var save = ApplyItemMappingsAsync',
        '?? throw new InvalidOperationException("The Stock mapping save service is unavailable.");',
        'var result = await save(selections);',
        'ValidationText = exception.Message;',
        'if (result.Warning.Length > 0)',
        'DialogResult = true;',
        'Close();'
    )
    foreach ($marker in $required) { if (-not $body.Contains($marker)) { return $false } }
    $commit = $body.IndexOf('if (!ReviewGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||')
    $row = $body.IndexOf('!ReviewGrid.CommitEdit(DataGridEditingUnit.Row, true))', $commit)
    $failedCommitReturn = $body.IndexOf('return;', $row)
    $validate = $body.IndexOf('if (!ValidateRows()) return;', $failedCommitReturn)
    $missingService = $body.IndexOf('Mode == SalesStockToolsWindow.ItemAliasMode && ApplyItemMappingsAsync is null', $validate)
    $missingServiceReturn = $body.IndexOf('return;', $missingService)
    $modeBranch = $body.IndexOf('if (Mode != SalesStockToolsWindow.ItemAliasMode)', $missingServiceReturn)
    $unitClose = $body.IndexOf('DialogResult = true;', $modeBranch)
    $unitReturn = $body.IndexOf('return;', $unitClose)
    $saving = $body.IndexOf('_isApplying = true;', $unitReturn)
    $selection = $body.IndexOf('var selections = GetItemMappingSelections();', $saving)
    $capture = $body.IndexOf('var save = ApplyItemMappingsAsync', $selection)
    $nonNull = $body.IndexOf('?? throw new InvalidOperationException("The Stock mapping save service is unavailable.");', $capture)
    $await = $body.IndexOf('var result = await save(selections);', $nonNull)
    $successClose = $body.IndexOf('DialogResult = true;', $await)
    $catch = $body.IndexOf('catch (Exception exception)', $successClose)
    $error = $body.IndexOf('ValidationText = exception.Message;', $catch)
    if (-not (0 -le $commit -and $commit -lt $row -and $row -lt $failedCommitReturn -and
        $failedCommitReturn -lt $validate -and $validate -lt $missingService -and
        $missingService -lt $missingServiceReturn -and $missingServiceReturn -lt $modeBranch -and
        $modeBranch -lt $unitClose -and $unitClose -lt $unitReturn -and $unitReturn -lt $saving -and
        $saving -lt $selection -and $selection -lt $capture -and $capture -lt $nonNull -and $nonNull -lt $await -and $await -lt $successClose -and
        $successClose -lt $catch -and $catch -lt $error)) { return $false }
    # No success/close before the durable Item Mapping callback. Unit/Conversion
    # mode alone retains the old parent-owned workflow.
    if ($body.Substring($saving, $await - $saving).Contains('DialogResult = true;') -or
        $body.Substring($saving, $await - $saving).Contains('Close();')) { return $false }
    if (-not [regex]::IsMatch($body, '(?s)if \(!ReviewGrid\.CommitEdit\(DataGridEditingUnit\.Cell, true\) \|\|\s*!ReviewGrid\.CommitEdit\(DataGridEditingUnit\.Row, true\)\)\s*\{[^{}]*\breturn;')) { return $false }
    if (-not [regex]::IsMatch($body, '(?s)if \(Mode == SalesStockToolsWindow\.ItemAliasMode && ApplyItemMappingsAsync is null\)\s*\{[^{}]*\breturn;')) { return $false }
    if ([regex]::IsMatch($body, 'await\s+ApplyItemMappingsAsync\s*\(')) { return $false }
    if (-not [regex]::IsMatch($body, '(?s)var save = ApplyItemMappingsAsync\s*\?\?\s*throw new InvalidOperationException\(')) { return $false }
    if (-not $Code.Contains('private void Window_Closing(') -or
        -not $Code.Contains('if (_isApplying) e.Cancel = true;') -or
        -not $Code.Contains('if (_isApplying) return;')) { return $false }
    return $true
}
