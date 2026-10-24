# One replacement method for direct and elevated updates: stage, rename, verify.
# Existing executable/DLL bytes are never overwritten or truncated.
function Get-AAHChildPath([string]$Root, [string]$Relative) {
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd([char]92,[char]47) + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid application transaction path.' }
    $check = $path
    while ($check.Length -ge $prefix.Length) {
        if (Test-Path -LiteralPath $check) {
            if (((Get-Item -LiteralPath $check -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('Application transaction refuses a reparse point: ' + $check) }
        }
        $check = Split-Path -Parent $check
    }
    return $path
}
function Get-AAHFileHash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash }
function Undo-AAHPublish([string]$InstallDir, [string]$Transaction, $Journal) {
    # Reverse order. A journal is persisted before the first rename, so this also
    # recovers an interrupted updater on its next invocation.
    $entries = @($Journal.Entries)
    for ($i = $entries.Count - 1; $i -ge 0; $i--) {
        $entry = $entries[$i]
        $target = Get-AAHChildPath $InstallDir $entry.Relative
        $old = Get-AAHChildPath (Join-Path $Transaction 'old') $entry.Relative
        $staged = Get-AAHChildPath (Join-Path $Transaction 'new') $entry.Relative
        if (Test-Path -LiteralPath $old -PathType Leaf) {
            if ((Get-AAHFileHash $old) -ne $entry.OldHash) { throw ('Rollback original hash mismatch: ' + $entry.Relative) }
            if (Test-Path -LiteralPath $target) {
                if ((Get-AAHFileHash $target) -ne $entry.NewHash) { throw ('Rollback refuses an externally changed file: ' + $entry.Relative) }
                [IO.File]::Delete($target)
            }
            [IO.File]::Move($old, $target)
        } elseif (-not $entry.Existed -and -not (Test-Path -LiteralPath $staged) -and (Test-Path -LiteralPath $target)) {
            if ((Get-AAHFileHash $target) -ne $entry.NewHash) { throw ('Rollback refuses an externally changed new file: ' + $entry.Relative) }
            [IO.File]::Delete($target)
        }
    }
    [IO.File]::WriteAllText((Join-Path $Transaction 'rolled-back'), 'Complete')
}
function Install-AAHPublish([string]$PublishDir, [string]$InstallDir) {
    $InstallDir = [IO.Path]::GetFullPath($InstallDir).TrimEnd([char]92,[char]47)
    $PublishDir = [IO.Path]::GetFullPath($PublishDir).TrimEnd([char]92,[char]47)
    if (-not (Test-Path -LiteralPath (Join-Path $PublishDir 'AccountingAutomationHub.exe') -PathType Leaf)) { throw 'Validated publish executable is missing.' }
    foreach ($previous in @(Get-ChildItem -LiteralPath $InstallDir -Directory -Force -Filter '.aah-swap-*')) {
        if (($previous.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Invalid transaction directory.' }
        $journalPath = Join-Path $previous.FullName 'journal.json'
        if (-not (Test-Path -LiteralPath $journalPath)) { continue }
        if ((Test-Path -LiteralPath (Join-Path $previous.FullName 'committed')) -or (Test-Path -LiteralPath (Join-Path $previous.FullName 'rolled-back')))
        {
            # Completed transaction folders are never retained. FIXED37 keeps only a
            # temporary old-file copy while replacement is in flight; verified commit
            # removes it and interrupted transactions remain only for crash recovery.
            try { Remove-Item -LiteralPath $previous.FullName -Recurse -Force -ErrorAction Stop } catch { }
            continue
        }
        $journal = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
        if ($journal.Schema -ne 1 -or $journal.InstallDir -ne $InstallDir) { throw 'Invalid pending application transaction.' }
        Undo-AAHPublish $InstallDir $previous.FullName $journal
        # A recovered interrupted transaction is no longer an application backup.
        # Once the original installed bytes have been restored, remove the recovery
        # journal/staging immediately so a later successful update leaves no old app copy.
        try { Remove-Item -LiteralPath $previous.FullName -Recurse -Force -ErrorAction Stop } catch { }
    }
    $transaction = Join-Path $InstallDir ('.aah-swap-' + [Guid]::NewGuid().ToString('N'))
    New-Item -Path $transaction -ItemType Directory -ErrorAction Stop | Out-Null
    $entries = New-Object 'System.Collections.Generic.List[object]'
    # Fully stage and hash-check the publish before modifying an installed file.
    foreach ($file in @(Get-ChildItem -LiteralPath $PublishDir -Recurse -File -Force | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($PublishDir.Length).TrimStart([char]92,[char]47)
        if ($file.Extension -in @('.py','.pyc','.pyd','.db','.sqlite','.sqlite3')) { throw ('Non-application publish payload refused: ' + $relative) }
        $source = Get-AAHChildPath $PublishDir $relative
        $target = Get-AAHChildPath $InstallDir $relative
        $staged = Get-AAHChildPath (Join-Path $transaction 'new') $relative
        New-Item -Path (Split-Path -Parent $staged) -ItemType Directory -Force | Out-Null
        [IO.File]::Copy($source, $staged, $false)
        $newHash = Get-AAHFileHash $source
        if ((Get-AAHFileHash $staged) -ne $newHash) { throw ('Publish staging hash mismatch: ' + $relative) }
        $existed = Test-Path -LiteralPath $target -PathType Leaf
        $oldHash = ''; if ($existed) { $oldHash = Get-AAHFileHash $target }
        $entries.Add([pscustomobject]@{ Relative=$relative; Existed=$existed; OldHash=$oldHash; NewHash=$newHash })
    }
    $journal = [pscustomobject]@{ Schema=1; InstallDir=$InstallDir; Entries=$entries.ToArray() }
    $journal | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $transaction 'journal.json') -Encoding UTF8
    try {
        foreach ($entry in $entries) {
            $target = Get-AAHChildPath $InstallDir $entry.Relative
            $staged = Get-AAHChildPath (Join-Path $transaction 'new') $entry.Relative
            $old = Get-AAHChildPath (Join-Path $transaction 'old') $entry.Relative
            New-Item -Path (Split-Path -Parent $target) -ItemType Directory -Force | Out-Null
            if ($entry.Existed) {
                if ((Get-AAHFileHash $target) -ne $entry.OldHash) { throw ('Installed file changed during staging: ' + $entry.Relative) }
                New-Item -Path (Split-Path -Parent $old) -ItemType Directory -Force | Out-Null
                [IO.File]::Move($target, $old)
            }
            [IO.File]::Move($staged, $target)
        }
        foreach ($entry in $entries) {
            if ((Get-AAHFileHash (Get-AAHChildPath $InstallDir $entry.Relative)) -ne $entry.NewHash) { throw ('Installed hash mismatch: ' + $entry.Relative) }
        }
        [IO.File]::WriteAllText((Join-Path $transaction 'committed'), 'Verified')
        # Rollback is needed only while replacement is in flight. Once every installed
        # hash verifies, FIXED37 retains no old application version: delete the transaction
        # together with its temporary old-file copy immediately.
        try { Remove-Item -LiteralPath $transaction -Recurse -Force -ErrorAction Stop } catch { }
        Write-Host 'Application replacement verified. Transaction staging released.'
    } catch {
        $failure = $_.Exception.Message
        try {
            Undo-AAHPublish $InstallDir $transaction $journal
            # Rollback completed successfully; do not retain the failed release or old app copy.
            try { Remove-Item -LiteralPath $transaction -Recurse -Force -ErrorAction Stop } catch { }
        }
        catch { throw ('Application replacement failed: ' + $failure + '. Rollback requires recovery: ' + $_.Exception.Message + '. Journal: ' + $transaction) }
        throw ('Application replacement was rolled back: ' + $failure)
    }
}
