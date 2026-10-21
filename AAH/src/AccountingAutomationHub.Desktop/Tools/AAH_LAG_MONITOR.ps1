param([int]$Seconds = 300)
$ErrorActionPreference = "SilentlyContinue"
$root = Join-Path $env:LOCALAPPDATA "Accounting Automation Hub"
$log = Join-Path $root "Logs"
New-Item -ItemType Directory -Force -Path $log | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $log ("lag-monitor-" + $stamp + ".csv")
$summary = Join-Path $log ("lag-summary-" + $stamp + ".txt")
$trace = Join-Path $log "performance-trace.csv"
$started = Get-Date
"Timestamp,Responding,CPUSeconds,WorkingSetMB,PrivateMB,Threads,Handles" | Set-Content -Encoding ASCII $out
$until = $started.AddSeconds([Math]::Max(10,$Seconds))
Write-Host "Accounting Automation Hub Lag Monitor"
Write-Host "Sampling once per second. Keep using the Hub until the slow/hold action occurs."
Write-Host "Process samples: $out"
Write-Host "Press Ctrl+C to stop early."
while ((Get-Date) -lt $until) {
  $p = Get-Process AccountingAutomationHub -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($null -eq $p) {
    ((Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff") + ",NOT-RUNNING,0,0,0,0,0") | Add-Content -Encoding ASCII $out
  } else {
    $responding = $p.Responding
    $ws = [Math]::Round($p.WorkingSet64 / 1MB, 1)
    $priv = [Math]::Round($p.PrivateMemorySize64 / 1MB, 1)
    $line = (Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff") + "," + $responding + "," + [Math]::Round($p.CPU,3) + "," + $ws + "," + $priv + "," + $p.Threads.Count + "," + $p.HandleCount
    $line | Add-Content -Encoding ASCII $out
    if (-not $responding) { Write-Host ((Get-Date -Format "HH:mm:ss") + " UI NOT RESPONDING") -ForegroundColor Yellow }
  }
  Start-Sleep -Seconds 1
}
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("Accounting Automation Hub Lag Diagnostic")
$lines.Add("Started: " + $started.ToString("yyyy-MM-dd HH:mm:ss"))
$lines.Add("Ended:   " + (Get-Date).ToString("yyyy-MM-dd HH:mm:ss"))
$lines.Add("Process samples: " + $out)
$sampleRows = @(Import-Csv $out)
$notResponding = @($sampleRows | Where-Object { $_.Responding -eq "False" }).Count
$maxWorking = ($sampleRows | ForEach-Object { [double]$_.WorkingSetMB } | Measure-Object -Maximum).Maximum
$maxPrivate = ($sampleRows | ForEach-Object { [double]$_.PrivateMB } | Measure-Object -Maximum).Maximum
$maxThreads = ($sampleRows | ForEach-Object { [double]$_.Threads } | Measure-Object -Maximum).Maximum
$lines.Add("Not-responding samples: " + $notResponding)
$lines.Add("Peak working set MB: " + [Math]::Round([double]$maxWorking,1))
$lines.Add("Peak private MB: " + [Math]::Round([double]$maxPrivate,1))
$lines.Add("Peak threads: " + [int]$maxThreads)
if (Test-Path $trace) {
  $stageRows = @(Import-Csv $trace | Where-Object {
    [DateTime]$t = [DateTime]::MinValue
    [DateTime]::TryParse($_.Timestamp, [ref]$t) -and $t -ge $started.AddSeconds(-2)
  })
  $lines.Add("")
  $lines.Add("Internal stage trace: " + $trace)
  $lines.Add("Stage events during monitor: " + $stageRows.Count)
  if ($stageRows.Count -gt 0) {
    $lines.Add("")
    $lines.Add("Slowest stage events:")
    foreach ($row in @($stageRows | Sort-Object { [double]$_.DurationMs } -Descending | Select-Object -First 20)) {
      $lines.Add(("{0,9:N1} ms  {1}  {2}" -f [double]$row.DurationMs, $row.Operation, $row.Detail))
    }
    $lines.Add("")
    $lines.Add("Stage summary:")
    foreach ($group in @($stageRows | Group-Object Operation | Sort-Object Name)) {
      $values = @($group.Group | ForEach-Object { [double]$_.DurationMs })
      $avg = ($values | Measure-Object -Average).Average
      $max = ($values | Measure-Object -Maximum).Maximum
      $lines.Add(("{0} : count={1}; avg={2:N1} ms; max={3:N1} ms" -f $group.Name, $values.Count, [double]$avg, [double]$max))
    }
  }
} else {
  $lines.Add("")
  $lines.Add("Internal performance trace was not found. Run the latest Hub build while monitoring.")
}
$lines | Set-Content -Encoding UTF8 $summary
Write-Host "Done."
Write-Host "Summary: $summary" -ForegroundColor Green
Write-Host "Process samples: $out"
Write-Host "Internal stage trace: $trace"
