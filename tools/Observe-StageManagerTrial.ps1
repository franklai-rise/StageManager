param(
    [Parameter(Mandatory)][int]$TargetProcessId,
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [double]$Hours = 24,
    [int]$SampleSeconds = 60
)
# Attach to an independently launched app; never start/stop it or change its settings.
$ErrorActionPreference = 'Stop'
if ($Hours -le 0 -or $SampleSeconds -lt 1) { throw 'Positive duration and interval required.' }
$targetExe = (Resolve-Path -LiteralPath $ExecutablePath).Path
$trialProcess = Get-Process -Id $TargetProcessId
if ($trialProcess.MainModule.FileName -ne $targetExe) { throw 'Process path does not match the trial executable.' }
$processStarted = $trialProcess.StartTime.ToUniversalTime()
$trialStarted = [DateTime]::UtcNow
$deadline = $trialStarted.AddHours($Hours)
$trialRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $trialRoot -Force | Out-Null
$stateFile = Join-Path $trialRoot 'observation.json'
$sampleFile = Join-Path $trialRoot 'samples.csv'
if (Test-Path -LiteralPath $stateFile) { throw 'Refusing to replace an existing observation.' }
$state = [ordered]@{
    Status = 'Running'; ProcessId = $TargetProcessId
    ProcessStartedUtc = $processStarted.ToString('O')
    StartedUtc = $trialStarted.ToString('O'); DeadlineUtc = $deadline.ToString('O')
    Sha256 = (Get-FileHash -LiteralPath $targetExe -Algorithm SHA256).Hash.ToLowerInvariant()
    Samples = 0; ManualInteractionChecks = 'Pending'; ReleasePromotion = 'Not authorized by this observer'
}
function Save-State {
    $state | ConvertTo-Json | Set-Content -LiteralPath ($stateFile + '.tmp') -Encoding utf8
    Move-Item -LiteralPath ($stateFile + '.tmp') -Destination $stateFile -Force
}
Save-State
$previousCpu = $trialProcess.TotalProcessorTime
$previousUtc = [DateTime]::UtcNow
try {
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Seconds $SampleSeconds
        $trialProcess.Refresh()
        if ($trialProcess.HasExited -or $trialProcess.StartTime.ToUniversalTime() -ne $processStarted) {
            throw 'Observed application exited; a new process cannot continue the same trial.'
        }
        $nowUtc = [DateTime]::UtcNow
        $cpu = $trialProcess.TotalProcessorTime
        [pscustomobject]@{
            TimestampUtc = $nowUtc.ToString('O')
            PrivateMB = [Math]::Round($trialProcess.PrivateMemorySize64 / 1MB, 2)
            WorkingSetMB = [Math]::Round($trialProcess.WorkingSet64 / 1MB, 2)
            Handles = $trialProcess.HandleCount
            Threads = $trialProcess.Threads.Count
            CpuPercent = [Math]::Round(100 * ($cpu - $previousCpu).TotalSeconds /
                ([Math]::Max(.001, ($nowUtc - $previousUtc).TotalSeconds) * [Environment]::ProcessorCount), 4)
        } | Export-Csv -LiteralPath $sampleFile -NoTypeInformation -Append -Encoding utf8
        $previousCpu = $cpu; $previousUtc = $nowUtc
        $state.Samples++
        $state.LastSampleUtc = $nowUtc.ToString('O')
        Save-State
    }
    $state.Status = 'ObservationComplete'
    $state.CompletedUtc = [DateTime]::UtcNow.ToString('O')
}
catch {
    $state.Status = 'Interrupted'
    $state.Error = $_.Exception.Message
}
finally {
    Save-State
    $trialProcess.Dispose()
}
# ObservationComplete is not a full pass: manual app/Focus/DPI checks are still required.
