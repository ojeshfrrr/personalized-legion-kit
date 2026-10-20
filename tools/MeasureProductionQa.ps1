param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\BuildProjectLegion\Production\Release\Project Legion.exe'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\BuildProjectLegion\Phase4\performance'),
    [int]$RunsPerMode = 3,
    [bool[]]$TraceModes = @($false,$true)
)
$ErrorActionPreference = 'Stop'
$Executable = [IO.Path]::GetFullPath($Executable)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if (Get-Process -Name 'ProjectLegion.ReadOnlyUiChecks','ProjectLegion.ReadOnlyProfiler' -ErrorAction SilentlyContinue) {
    throw 'Close the integration harness and profiler before collecting matched runs.'
}
$summary = @()
for ($run=1; $run -le $RunsPerMode; $run++) {
    foreach ($trace in $TraceModes) {
        $name = 'run-{0}-trace-{1}' -f $run,$trace.ToString().ToLowerInvariant()
        $folder = Join-Path $OutputDirectory $name
        $profile = Join-Path $folder 'profile'
        if (Test-Path $folder) { throw "Refusing to overwrite prior measurements: $folder" }
        New-Item -ItemType Directory -Path $profile -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $profile '.project-legion-qa-profile') -Value 'Isolated read-only QA profile; no upstream settings migration.'
        @{ EnableHardwareSensors=$true; UseNewSensorDashboard=$true; MinimizeToTray=$false; MinimizeOnClose=$false;
           EnableHardwareAcceleration=$false; RememberWindowPosition=$false; WindowSize=@{Width=1250;Height=780} } |
            ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $profile 'settings.json')
        $launchArgs = @('--read-only','--minimized','--qa-exit-after-seconds=45', ('--qa-profile="' + $profile + '"'))
        if ($trace) { $launchArgs += '--trace' }
        $launch = [Diagnostics.Stopwatch]::StartNew()
        $process = Start-Process -FilePath $Executable -ArgumentList $launchArgs -WorkingDirectory (Split-Path $Executable) -PassThru
        $pidValue = $process.Id
        $startedUtc = $process.StartTime.ToUniversalTime()
        $metadata = Join-Path $profile 'qa-session.json'
        $session = $null
        while ($launch.Elapsed.TotalSeconds -lt 30) {
            if (!(Get-Process -Id $pidValue -ErrorAction SilentlyContinue)) { throw "App exited before initialization: $name" }
            try { $session = Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json } catch { }
            if ($null -ne $session.Stages.StartupCompleted) { break }
            Start-Sleep -Milliseconds 100
        }
        if ($null -eq $session.Stages.StartupCompleted) { throw "Initialization timed out: $name" }
        Start-Sleep -Seconds 10
        $samples = @()
        for ($index=0; $index -lt 3; $index++) {
            $observed = Get-Process -Id $pidValue
            $cpuBefore = $observed.TotalProcessorTime.TotalSeconds
            $interval = [Diagnostics.Stopwatch]::StartNew()
            Start-Sleep -Seconds 5
            $observed.Refresh()
            $cpu = 100 * ($observed.TotalProcessorTime.TotalSeconds - $cpuBefore) / $interval.Elapsed.TotalSeconds
            $samples += [pscustomobject]@{Index=$index;Utc=[DateTime]::UtcNow.ToString('o');CPUOneCore=$cpu;
                CPUMachine=$cpu/[Environment]::ProcessorCount;WorkingSetMiB=$observed.WorkingSet64/1MB;
                PrivateMiB=$observed.PrivateMemorySize64/1MB;Handles=$observed.HandleCount;Threads=$observed.Threads.Count;
                Responding=$observed.Responding;GameRunning=[bool](Get-Process -Name 'VALORANT-Win64-Shipping' -ErrorAction SilentlyContinue)}
        }
        $samples | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'samples.json')
        while ($launch.Elapsed.TotalSeconds -lt 65 -and (Get-Process -Id $pidValue -ErrorAction SilentlyContinue)) {
            Start-Sleep -Milliseconds 100
        }
        $exited = !(Get-Process -Id $pidValue -ErrorAction SilentlyContinue)
        $session = Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json
        $record = [pscustomobject]@{Name=$name;ProcessId=$pidValue;Trace=$trace;ProcessStartedUtc=$startedUtc.ToString('o');
            Executable=$Executable;SHA256=(Get-FileHash -LiteralPath $Executable).Hash;Exited=$exited;
            ObservedLifetimeMs=$launch.ElapsedMilliseconds;Stages=$session.Stages;Samples=$samples;
            ForegroundVisibilityVerified=$false;Mode='Minimized, read-only, isolated; software rendering; default telemetry cadence'}
        $record | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $folder 'result.json')
        $summary += $record
        $summary | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.json')
        Write-Output ("{0}: startup={1:N0}ms, exited={2}" -f $name,$session.Stages.StartupCompleted,$exited)
        if (!$exited) { throw "Timed session remains alive: PID $pidValue. Do not start another run." }
    }
}
