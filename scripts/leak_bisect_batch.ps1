#requires -Version 7.0
<#
.SYNOPSIS
Runs repeatable, memory-bounded SharpEmu profiling trials on Windows.

.DESCRIPTION
Samples the working set and private bytes of SharpEmu and its child processes
at a fixed interval. By default, it runs five 45-second trials and writes the
time series to a uniquely named CSV in the system temporary directory.

Each invocation profiles one scenario. Run the same invocation again with a
different -Scenario and -EnvironmentOverrides to compare configurations. A
mandatory per-process-tree memory limit stops the current run and the remainder
of the batch when reached.

The script does not include, copy, or modify game data.

.EXAMPLE
./scripts/leak_bisect_batch.ps1 `
  -EmulatorPath 'C:/SharpEmu/SharpEmu.exe' `
  -EbootPath '<path-to-eboot.bin>' `
  -MemoryLimitMB 8192
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $EmulatorPath,

    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $EbootPath,

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 1048576)]
    [int] $MemoryLimitMB,

    [ValidateRange(5, 10)]
    [int] $TrialCount = 5,

    [ValidateRange(1, 3600)]
    [int] $DurationSeconds = 45,

    [ValidateRange(1, 60)]
    [int] $SampleIntervalSeconds = 1,

    [ValidateNotNullOrEmpty()]
    [string] $Scenario = 'baseline',

    [hashtable] $EnvironmentOverrides = @{}
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ProcessTreeIds {
    param(
        [Parameter(Mandatory = $true)]
        [int] $RootProcessId,

        [Parameter(Mandatory = $true)]
        [datetime] $RootStartTime
    )

    $processes = @(Get-CimInstance -ClassName Win32_Process -Property ProcessId, ParentProcessId, CreationDate)
    $processById = @{}
    foreach ($processInfo in $processes) {
        $processId = [int] $processInfo.ProcessId
        $created = [datetime] $processInfo.CreationDate
        if ($created -ge $RootStartTime.AddSeconds(-2)) {
            $processById[$processId] = $processInfo
        }
    }

    $treeIds = [System.Collections.Generic.HashSet[int]]::new()
    [void] $treeIds.Add($RootProcessId)
    do {
        $addedProcess = $false
        foreach ($processInfo in $processById.Values) {
            $processId = [int] $processInfo.ProcessId
            $parentProcessId = [int] $processInfo.ParentProcessId
            if (-not $treeIds.Contains($processId) -and $treeIds.Contains($parentProcessId)) {
                [void] $treeIds.Add($processId)
                $addedProcess = $true
            }
        }
    } while ($addedProcess)

    return @($treeIds)
}

function Get-ProcessTreeMemory {
    param(
        [Parameter(Mandatory = $true)]
        [int[]] $ProcessIds
    )

    $workingSetBytes = [int64] 0
    $privateBytes = [int64] 0
    $observedProcessCount = 0
    foreach ($processId in $ProcessIds) {
        try {
            $process = Get-Process -Id $processId -ErrorAction Stop
            $workingSetBytes += [int64] $process.WorkingSet64
            $privateBytes += [int64] $process.PrivateMemorySize64
            $observedProcessCount++
        }
        catch [System.ArgumentException] {
            # The process may have exited between enumeration and sampling.
        }
        catch [Microsoft.PowerShell.Commands.ProcessCommandException] {
            # Access may be lost if a child exits while this sample is collected.
        }
    }

    [pscustomobject] @{
        ProcessCount = $observedProcessCount
        WorkingSetBytes = $workingSetBytes
        PrivateBytes = $privateBytes
    }
}

function Stop-StartedProcessTree {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process] $RootProcess,

        [Parameter(Mandatory = $true)]
        [datetime] $RootStartTime
    )

    $rootProcessId = $RootProcess.Id
    try {
        # If the root exits naturally but leaves a child alive, it is still
        # discoverable through its parent PID. Verify each process start time
        # before killing it to avoid acting on a PID that has been reused.
        $descendantIds = @(Get-ProcessTreeIds -RootProcessId $rootProcessId -RootStartTime $RootStartTime | Where-Object { $_ -ne $rootProcessId })
        foreach ($processId in $descendantIds) {
            try {
                $descendant = Get-Process -Id $processId -ErrorAction Stop
                if ($descendant.StartTime -ge $RootStartTime.AddSeconds(-2)) {
                    $descendant.Kill($true)
                }
            }
            catch [System.ArgumentException] {
                # The child exited between enumeration and cleanup.
            }
            catch [Microsoft.PowerShell.Commands.ProcessCommandException] {
                # The child exited between enumeration and cleanup.
            }
        }
    }
    catch {
        Write-Warning "Could not enumerate all SharpEmu child processes during cleanup: $($_.Exception.Message)"
    }

    try {
        $RootProcess.Refresh()
        if (-not $RootProcess.HasExited) {
            $RootProcess.Kill($true)
            if (-not $RootProcess.WaitForExit(10000)) {
                throw "SharpEmu process tree did not exit within 10 seconds (root PID $rootProcessId)."
            }
        }
    }
    catch [System.InvalidOperationException] {
        # The root exited between Refresh and Kill; its descendants are already
        # covered by Process.Kill(entireProcessTree) when the root was alive.
    }
}

$resolvedEmulatorPath = (Resolve-Path -LiteralPath $EmulatorPath).Path
$resolvedEbootPath = (Resolve-Path -LiteralPath $EbootPath).Path
foreach ($entry in $EnvironmentOverrides.GetEnumerator()) {
    if ([string]::IsNullOrWhiteSpace([string] $entry.Key)) {
        throw 'Environment override names cannot be empty.'
    }
    if ($null -eq $entry.Value) {
        throw "Environment override '$($entry.Key)' cannot have a null value. Use an empty string to clear it."
    }
}

$timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$csvPath = Join-Path ([System.IO.Path]::GetTempPath()) "sharpemu-memory-profile-$timestamp-$([guid]::NewGuid().ToString('N').Substring(0, 8)).csv"
$samples = [System.Collections.Generic.List[object]]::new()
$batchStoppedEarly = $false
$memoryLimitBytes = [int64] $MemoryLimitMB * 1MB

Write-Host "Profiling scenario '$Scenario': $TrialCount trial(s), ${DurationSeconds}s each, process-tree limit ${MemoryLimitMB} MB."
Write-Host "Results will be written outside the repository to: $csvPath"

for ($trial = 1; $trial -le $TrialCount; $trial++) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $resolvedEmulatorPath
    $startInfo.WorkingDirectory = Split-Path -Parent $resolvedEmulatorPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    [void] $startInfo.ArgumentList.Add($resolvedEbootPath)
    foreach ($entry in $EnvironmentOverrides.GetEnumerator()) {
        $startInfo.Environment[[string] $entry.Key] = [string] $entry.Value
    }

    $rootProcess = [System.Diagnostics.Process]::new()
    $rootProcess.StartInfo = $startInfo
    $trialWatch = [System.Diagnostics.Stopwatch]::new()
    $peakWorkingSetBytes = [int64] 0
    $peakPrivateBytes = [int64] 0
    $stopReason = $null
    $exitCode = $null
    $lastProcessIds = @()
    $rootStartTime = $null
    $processStarted = $false
    $trialError = $null

    try {
        if (-not $rootProcess.Start()) {
            throw "Failed to start emulator executable: $resolvedEmulatorPath"
        }
        $processStarted = $true
        $rootProcess.Refresh()
        $rootStartTime = $rootProcess.StartTime
        $trialWatch.Start()
        Write-Host "Trial $trial/$TrialCount started (PID $($rootProcess.Id))."

        while ($true) {
            $rootProcess.Refresh()
            if ($rootProcess.HasExited) {
                $stopReason = 'process-exited-before-duration'
                $exitCode = $rootProcess.ExitCode
                break
            }

            $elapsedSeconds = [Math]::Round($trialWatch.Elapsed.TotalSeconds, 2)
            if ($elapsedSeconds -ge $DurationSeconds) {
                $stopReason = 'duration-reached'
                break
            }

            $lastProcessIds = @(Get-ProcessTreeIds -RootProcessId $rootProcess.Id -RootStartTime $rootStartTime)
            $memory = Get-ProcessTreeMemory -ProcessIds $lastProcessIds
            $peakWorkingSetBytes = [Math]::Max($peakWorkingSetBytes, $memory.WorkingSetBytes)
            $peakPrivateBytes = [Math]::Max($peakPrivateBytes, $memory.PrivateBytes)
            $sampleTime = [DateTime]::UtcNow
            $workingSetMB = [Math]::Round($memory.WorkingSetBytes / 1MB, 2)
            $privateMB = [Math]::Round($memory.PrivateBytes / 1MB, 2)
            $reachedMemoryLimit = $memory.WorkingSetBytes -ge $memoryLimitBytes

            $samples.Add([pscustomobject] @{
                Scenario = $Scenario
                Trial = $trial
                RootProcessId = $rootProcess.Id
                TimestampUtc = $sampleTime.ToString('o')
                ElapsedSeconds = $elapsedSeconds
                ProcessCount = $memory.ProcessCount
                WorkingSetMB = $workingSetMB
                PrivateBytesMB = $privateMB
                PeakWorkingSetMB = [Math]::Round($peakWorkingSetBytes / 1MB, 2)
                PeakPrivateBytesMB = [Math]::Round($peakPrivateBytes / 1MB, 2)
                StopReason = if ($reachedMemoryLimit) { 'memory-limit-reached' } else { '' }
                ExitCode = ''
            })

            if ($reachedMemoryLimit) {
                $stopReason = 'memory-limit-reached'
                $batchStoppedEarly = $true
                break
            }

            Start-Sleep -Seconds ([Math]::Min($SampleIntervalSeconds, [Math]::Max(1, $DurationSeconds - [int] $trialWatch.Elapsed.TotalSeconds)))
        }
    }
    catch {
        $trialError = $_
        $stopReason = 'profiler-error'
        $batchStoppedEarly = $true
    }
    finally {
        $trialWatch.Stop()
        if ($processStarted) {
            try {
                Stop-StartedProcessTree -RootProcess $rootProcess -RootStartTime $rootStartTime
            }
            catch {
                if ($null -eq $trialError) {
                    $trialError = $_
                    $stopReason = 'cleanup-error'
                    $batchStoppedEarly = $true
                }
            }
        }
        if ($processStarted) {
            try {
                $rootProcess.Refresh()
                if ($rootProcess.HasExited -and $stopReason -eq 'process-exited-before-duration') {
                    $exitCode = $rootProcess.ExitCode
                }
            }
            catch [System.InvalidOperationException] {
                # The process exited before its exit code could be read.
            }
        }
        $rootProcess.Dispose()
    }

    if ($samples.Count -eq 0 -or $samples[$samples.Count - 1].Trial -ne $trial) {
        $samples.Add([pscustomobject] @{
            Scenario = $Scenario
            Trial = $trial
            RootProcessId = if ($processStarted) { $rootProcess.Id } else { '' }
            TimestampUtc = [DateTime]::UtcNow.ToString('o')
            ElapsedSeconds = [Math]::Round($trialWatch.Elapsed.TotalSeconds, 2)
            ProcessCount = 0
            WorkingSetMB = 0
            PrivateBytesMB = 0
            PeakWorkingSetMB = [Math]::Round($peakWorkingSetBytes / 1MB, 2)
            PeakPrivateBytesMB = [Math]::Round($peakPrivateBytes / 1MB, 2)
            StopReason = $stopReason
            ExitCode = $exitCode
        })
    }
    else {
        $samples[$samples.Count - 1].StopReason = $stopReason
        $samples[$samples.Count - 1].ExitCode = $exitCode
    }

    Write-Host "Trial $trial ended: $stopReason; peak working set $([Math]::Round($peakWorkingSetBytes / 1MB, 2)) MB."
    if ($batchStoppedEarly) {
        Write-Warning 'The process-tree memory limit was reached. Remaining trials were skipped.'
        break
    }
    if ($stopReason -eq 'process-exited-before-duration') {
        Write-Warning 'The emulator exited before the measurement window. Remaining trials were skipped.'
        break
    }
    if ($null -ne $trialError) {
        Write-Warning "Profiling stopped after an error: $($trialError.Exception.Message)"
        break
    }
}

$samples | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding utf8
Write-Host "Completed $(@($samples | Select-Object -ExpandProperty Trial -Unique).Count) trial(s). CSV: $csvPath"
if ($null -ne $trialError) {
    throw $trialError
}
