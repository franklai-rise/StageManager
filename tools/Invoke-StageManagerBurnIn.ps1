param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath,
    [double]$Hours = 24,
    [int]$SampleSeconds = 10,
    [string]$RuntimeProbePath,
    [int]$WindowBurst = 500,
    [double]$CpuLimitPercent = 0.2,
    [double]$PrivateMemoryLimitMB = 50,
    [switch]$StopWhenFinished
)

$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
if (-not [System.IO.Path]::IsPathRooted($resolvedExecutable)) {
    throw 'ExecutablePath must resolve to an absolute path.'
}
if ($Hours -le 0 -or $SampleSeconds -lt 1 -or $CpuLimitPercent -le 0 -or $PrivateMemoryLimitMB -le 0) {
    throw 'Hours and resource limits must be positive, and SampleSeconds must be at least one.'
}

if (-not ('StageManagerBurnIn.NativeMethods' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace StageManagerBurnIn {
    public static class NativeMethods {
        [DllImport("user32.dll")]
        public static extern uint GetGuiResources(IntPtr process, uint flags);
    }
}
'@
}

$resultsRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\burn-in'
New-Item -ItemType Directory -Path $resultsRoot -Force | Out-Null
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$csvPath = Join-Path $resultsRoot "burn-in-$runId.csv"
$summaryPath = Join-Path $resultsRoot "burn-in-$runId.json"
$process = Start-Process -FilePath $resolvedExecutable -WorkingDirectory (Split-Path -Parent $resolvedExecutable) -WindowStyle Hidden -PassThru
$startedUtc = [DateTime]::UtcNow
$deadlineUtc = $startedUtc.AddHours($Hours)
$samples = [System.Collections.Generic.List[object]]::new()

try {
    Start-Sleep -Seconds 5
    $process.Refresh()
    if ($process.HasExited) {
        throw "Stage Manager exited during startup with code $($process.ExitCode). Another instance may already be running."
    }

    if ($WindowBurst -gt 0 -and -not [string]::IsNullOrWhiteSpace($RuntimeProbePath)) {
        $resolvedProbe = (Resolve-Path -LiteralPath $RuntimeProbePath).Path
        $probe = Start-Process -FilePath $resolvedProbe -ArgumentList @('--stress', $WindowBurst) -WorkingDirectory (Split-Path -Parent $resolvedProbe) -WindowStyle Hidden -PassThru
        if (-not $probe.WaitForExit(120000)) {
            $probe.Kill($true)
            throw 'The 500-window RuntimeProbe burst exceeded two minutes.'
        }
        if ($probe.ExitCode -ne 0) {
            throw "RuntimeProbe failed with exit code $($probe.ExitCode)."
        }
        $probe.Dispose()
    }

    $priorCpu = $process.TotalProcessorTime
    $priorSampleUtc = [DateTime]::UtcNow
    while ([DateTime]::UtcNow -lt $deadlineUtc) {
        Start-Sleep -Seconds $SampleSeconds
        $process.Refresh()
        if ($process.HasExited) {
            throw "Stage Manager exited during burn-in with code $($process.ExitCode)."
        }

        $nowUtc = [DateTime]::UtcNow
        $cpuDelta = ($process.TotalProcessorTime - $priorCpu).TotalSeconds
        $wallDelta = [Math]::Max(0.001, ($nowUtc - $priorSampleUtc).TotalSeconds)
        $cpuPercent = 100 * $cpuDelta / ($wallDelta * [Environment]::ProcessorCount)
        $samples.Add([pscustomobject]@{
            TimestampUtc = $nowUtc.ToString('O')
            PrivateMemoryMB = [Math]::Round($process.PrivateMemorySize64 / 1MB, 2)
            WorkingSetMB = [Math]::Round($process.WorkingSet64 / 1MB, 2)
            CpuPercent = [Math]::Round($cpuPercent, 4)
            Handles = $process.HandleCount
            GdiObjects = [StageManagerBurnIn.NativeMethods]::GetGuiResources($process.Handle, 0)
            UserObjects = [StageManagerBurnIn.NativeMethods]::GetGuiResources($process.Handle, 1)
        })
        $priorCpu = $process.TotalProcessorTime
        $priorSampleUtc = $nowUtc
    }

    $samples | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding utf8
    $steady = @($samples | Select-Object -Skip ([Math]::Min(6, $samples.Count)))
    if ($steady.Count -eq 0) { $steady = @($samples) }
    $first = $steady | Select-Object -First 1
    $last = $steady | Select-Object -Last 1
    $summary = [ordered]@{
        Executable = $resolvedExecutable
        Version = $process.MainModule.FileVersionInfo.FileVersion
        StartedUtc = $startedUtc.ToString('O')
        DurationHours = [Math]::Round(([DateTime]::UtcNow - $startedUtc).TotalHours, 3)
        Samples = $samples.Count
        AverageCpuPercent = [Math]::Round(($steady | Measure-Object CpuPercent -Average).Average, 4)
        MaximumPrivateMemoryMB = [Math]::Round(($steady | Measure-Object PrivateMemoryMB -Maximum).Maximum, 2)
        PrivateMemoryGrowthMB = [Math]::Round($last.PrivateMemoryMB - $first.PrivateMemoryMB, 2)
        HandleGrowth = $last.Handles - $first.Handles
        GdiObjectGrowth = $last.GdiObjects - $first.GdiObjects
        UserObjectGrowth = $last.UserObjects - $first.UserObjects
        CpuLimitPercent = $CpuLimitPercent
        PrivateMemoryLimitMB = $PrivateMemoryLimitMB
        CsvPath = $csvPath
    }
    $summary.Passed =
        $summary.AverageCpuPercent -le $CpuLimitPercent -and
        $summary.MaximumPrivateMemoryMB -le $PrivateMemoryLimitMB -and
        $summary.PrivateMemoryGrowthMB -le 5 -and
        $summary.HandleGrowth -le 10 -and
        $summary.GdiObjectGrowth -le 2 -and
        $summary.UserObjectGrowth -le 2
    $summary | ConvertTo-Json | Set-Content -LiteralPath $summaryPath -Encoding utf8
    $summary | ConvertTo-Json
}
finally {
    if ($StopWhenFinished -and -not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit(10000)
    }
    $process.Dispose()
}
