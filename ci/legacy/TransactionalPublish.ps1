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
        $parent = [IO.Path]::GetDirectoryName($check)
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $check) { throw 'Invalid application transaction path ancestry.' }
        $check = $parent
    }
    return $path
}

# Hash with the .NET streaming API rather than invoking the PowerShell
# Get-FileHash cmdlet for every one of hundreds of release files. A stalled
# hash must fail BEFORE a journal or installed-file mutation.
function Get-AAHFileHash([string]$Path) {
    $stream = New-Object System.IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read, 1048576)
    $hasher = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    $buffer = New-Object byte[] 1048576
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        while (($count = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $hasher.AppendData($buffer, 0, $count)
            if ($clock.Elapsed -gt [TimeSpan]::FromMinutes(2)) {
                throw ('Application SHA256 exceeded the per-file safety budget: ' + [IO.Path]::GetFileName($Path))
            }
        }
        return [BitConverter]::ToString($hasher.GetHashAndReset()).Replace('-','')
    } finally {
        $hasher.Dispose()
        $stream.Dispose()
    }
}
function Write-AAHTransactionProgress([string]$Message) {
    # Separate append-only progress log, visible while UpdateHost waits for
    # the original PowerShell installer to finish. Not an alternate updater.
    try {
        $dir = Join-Path $env:LOCALAPPDATA 'AccountingAutomationHub\AutoUpdateLogs'
        [IO.Directory]::CreateDirectory($dir) | Out-Null
        [IO.File]::AppendAllText(
            (Join-Path $dir 'github-update-staging.log'),
            ([DateTimeOffset]::Now.ToString('o') + ' ' + $Message + [Environment]::NewLine))
    } catch { }
}

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
        if (-not (Test-Path -LiteralPath $journalPath)) {
            # No journal means no installed file could have been moved. The
            # exclusive UpdateHost mutex prevents a concurrent transaction.
            try { Remove-Item -LiteralPath $previous.FullName -Recurse -Force -ErrorAction Stop } catch { }
            continue
        }
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
    $stagingClock = [Diagnostics.Stopwatch]::StartNew()
    $files = @(Get-ChildItem -LiteralPath $PublishDir -Recurse -File -Force | Sort-Object FullName)
    $scanned = 0
    $unchanged = 0
    Write-AAHTransactionProgress ("STAGING START: {0} verified publish files" -f $files.Count)
    # All source files are SHA256 checked. An installed file with the exact
    # same SHA256 is deliberately left in place (and is absent from the journal).
    # This preserves the original journal/rollback/install flow but avoids
    # re-copying, re-hashing and renaming hundreds of identical files per update.
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($PublishDir.Length).TrimStart([char]92,[char]47)
        if ($file.Extension -in @('.py','.pyc','.pyd','.db','.sqlite','.sqlite3')) { throw ('Non-application publish payload refused: ' + $relative) }
        $source = Get-AAHChildPath $PublishDir $relative
        $target = Get-AAHChildPath $InstallDir $relative
        $newHash = Get-AAHFileHash $source
        $existed = Test-Path -LiteralPath $target -PathType Leaf
        $oldHash = ''; if ($existed) { $oldHash = Get-AAHFileHash $target }
        if ($existed -and $oldHash -eq $newHash) {
            $unchanged++
        } else {
            $staged = Get-AAHChildPath (Join-Path $transaction 'new') $relative
            New-Item -Path (Split-Path -Parent $staged) -ItemType Directory -Force | Out-Null
            [IO.File]::Copy($source, $staged, $false)
            if ((Get-AAHFileHash $staged) -ne $newHash) { throw ('Publish staging hash mismatch: ' + $relative) }
            $entries.Add([pscustomobject]@{ Relative=$relative; Existed=$existed; OldHash=$oldHash; NewHash=$newHash })
        }
        $scanned++
        if (($scanned % 25) -eq 0 -or $scanned -eq $files.Count) {
            Write-AAHTransactionProgress ("STAGING {0}/{1}: changed={2}; identical={3}; elapsed={4}s; last={5}" -f
                $scanned, $files.Count, $entries.Count, $unchanged,
                [math]::Round($stagingClock.Elapsed.TotalSeconds,1), $relative)
        }
        if ($stagingClock.Elapsed -gt [TimeSpan]::FromMinutes(5)) {
            throw ('Verified application staging exceeded its safety budget before any installed file replacement. Processed ' + $scanned + '/' + $files.Count)
        }
    }
    Write-AAHTransactionProgress ("STAGING COMPLETE: changed={0}; unchanged={1}; elapsed={2}s" -f
        $entries.Count, $unchanged, [math]::Round($stagingClock.Elapsed.TotalSeconds,1))
    if ($entries.Count -eq 0) {
        # Every SHA256 matches. No installed file or rollback journal required.
        Remove-Item -LiteralPath $transaction -Recurse -Force -ErrorAction Stop
        Write-Host 'Application replacement verified. All installed file hashes already match.'
        return
    }
    # The journal is persisted BEFORE the first installed-file rename.
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
        Write-AAHTransactionProgress ("COMMITTED: verified {0} changed application files; data untouched" -f $entries.Count)
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
