param(
    [ValidateSet('Start','Sample','Snapshot','Exit')][string]$Action,
    [string]$Profile,
    [int]$AppProcessId,
    [string]$Label = 'session',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\BuildProjectLegion\Phase3')
)
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if ($Action -eq 'Start') {
    if (-not [IO.Path]::IsPathFullyQualified($Profile)) { throw 'Use an absolute isolated profile path.' }
    $exe = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\BuildProjectLegion\Production\Release\Project Legion.exe'))
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $exe -ArgumentList @('--read-only','--trace', ('--qa-profile="' + $Profile + '"')) -WorkingDirectory (Split-Path $exe) -PassThru
    $handleMs = $null
    while ($timer.Elapsed.TotalSeconds -lt 30) {
        $process.Refresh()
        if ($process.HasExited) { throw "App exited during startup: $($process.ExitCode)" }
        if ($process.MainWindowHandle -ne 0) { $handleMs = $timer.ElapsedMilliseconds; break }
        Start-Sleep -Milliseconds 50
    }
    $record = [pscustomobject]@{ Label=$Label; ProcessId=$process.Id; StartedUtc=$process.StartTime.ToUniversalTime().ToString('o'); MainWindowHandleMs=$handleMs; Executable=$exe; SHA256=(Get-FileHash -LiteralPath $exe).Hash; Profile=$Profile }
    $record | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory "$Label-start.json")
    $record
    exit
}
if ($Action -eq 'Exit') {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $process = Get-Process -Id $AppProcessId -ErrorAction SilentlyContinue
    if ($process) { $exited = $process.WaitForExit(15000) } else { $exited = $true }
    $remaining = @(Get-Process -Name 'Project Legion' -ErrorAction SilentlyContinue | Select-Object Id, Path)
    $record = [pscustomobject]@{ Label=$Label; ObservedUtc=[DateTime]::UtcNow.ToString('o'); Exited=$exited; ObservationWaitMs=$timer.ElapsedMilliseconds; Remaining=$remaining }
    $record | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory "$Label-exit.json")
    $record
    exit
}
$results = @()
$count = if ($Action -eq 'Sample') { 3 } else { 1 }
for ($index=0; $index -lt $count; $index++) {
    $process = Get-Process -Id $AppProcessId
    $previousCpu = $process.TotalProcessorTime.TotalSeconds
    $timer = [Diagnostics.Stopwatch]::StartNew()
    if ($Action -eq 'Sample') { Start-Sleep -Seconds 5 }
    $process.Refresh()
    $cpuOneCore = if ($Action -eq 'Sample') { 100 * ($process.TotalProcessorTime.TotalSeconds - $previousCpu) / $timer.Elapsed.TotalSeconds } else { $null }
    $results += [pscustomobject]@{ Label=$Label; Index=$index; Utc=[DateTime]::UtcNow.ToString('o'); CPUPercentOneCore=$cpuOneCore; CPUPercentMachine=if ($null -ne $cpuOneCore) {$cpuOneCore / [Environment]::ProcessorCount} else {$null}; LogicalProcessors=[Environment]::ProcessorCount; WorkingSetMiB=$process.WorkingSet64 / 1MB; PrivateMiB=$process.PrivateMemorySize64 / 1MB; Handles=$process.HandleCount; Threads=$process.Threads.Count; Responding=$process.Responding }
}
$results | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory "$Label-$Action.json")
$results | Format-Table -AutoSize
