#!/usr/bin/env pwsh
# Idle watchdog for tools/test-all.ps1: names a test host that has stopped doing work.
#
# Dot-sourced by test-all.ps1. A test host that keeps its suite job alive while it stops making
# progress is hung, not slow: without this the run goes on until the ceiling, with only a heartbeat to
# show it is still there and nothing naming the process or the stack it is stuck on (YAAT-177).
#
# The tree is walked with PowerShell 7's Get-Process (.Parent, .StartTime, .TotalProcessorTime)
# rather than WMI, so it also works on the macOS gate builder. Only processes that can sit between the
# test-all process and a test host are examined (pwsh, dotnet, testhost, *.Tests): reading `.Parent`
# for every process on the machine costs about a second per walk. A child is kept only when it started
# at or after its apparent parent, so a reused parent PID cannot pull another session's processes in.
#
# Idle is judged against a CPU baseline, not against the last reading: a hung host often still has a
# timer thread ticking (yaat-server's room tick runs every second), which moves the CPU figure but by
# far less than work. The clock is reset only when a host has used more than the tolerance since the
# baseline; a host under that tolerance for the idle threshold is stalled.
#
# On a stall it prints the host, the tree and each suite log's tail, dumps the host with dotnet-dump
# (Heap, its own timeout, and the partial dump deleted when the attach is cut short), prints the
# dump's managed stacks next to the dump, then kills the watched test hosts and stops the suite jobs.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '',
    Justification = 'The stall report is the run''s console output, in test-all.ps1''s own style.')]
param()

# Every process under $RootPid that started after its parent and whose name matches
# $ProcessNamePattern, as records carrying what the watchdog reports: name, pid, parent pid, CPU
# seconds and start time. Throws when the root itself is missing, so a failed walk is never mistaken
# for a tree with no hosts in it.
function Get-TestAllProcessTree {
    param(
        [Parameter(Mandatory)][int]$RootPid,
        [Parameter(Mandatory)][string]$ProcessNamePattern
    )

    $known = @{}
    $byParent = @{}
    foreach ($process in @(Get-Process -ErrorAction SilentlyContinue)) {
        try {
            if ($process.Name -notmatch $ProcessNamePattern) {
                continue
            }
            # Read once: each read of .Parent costs a native query.
            $parent = $process.Parent
            $entry = [pscustomobject]@{
                Name       = $process.Name
                Id         = $process.Id
                ParentId   = if ($null -eq $parent) { 0 } else { $parent.Id }
                CpuSeconds = $process.TotalProcessorTime.TotalSeconds
                StartTime  = $process.StartTime
            }
        } catch {
            # The process exited between the snapshot and the property read: it is not a host.
            continue
        } finally {
            $process.Dispose()
        }
        $known[$entry.Id] = $entry
        if ($entry.ParentId -ne 0) {
            if (-not $byParent.ContainsKey($entry.ParentId)) {
                $byParent[$entry.ParentId] = [System.Collections.Generic.List[object]]::new()
            }
            $byParent[$entry.ParentId].Add($entry)
        }
    }

    if (-not $known.ContainsKey($RootPid)) {
        throw "the process tree walk found no process $RootPid"
    }

    $tree = [System.Collections.Generic.List[object]]::new()
    $seen = @{}
    $seen[$RootPid] = $true
    $queue = [System.Collections.Generic.Queue[object]]::new()
    $queue.Enqueue($known[$RootPid])
    while ($queue.Count -gt 0) {
        $parent = $queue.Dequeue()
        if (-not $byParent.ContainsKey($parent.Id)) {
            continue
        }
        foreach ($child in $byParent[$parent.Id]) {
            if ($seen.ContainsKey($child.Id)) {
                continue
            }
            if ($child.StartTime -lt $parent.StartTime) {
                continue
            }
            $seen[$child.Id] = $true
            $tree.Add($child)
            $queue.Enqueue($child)
        }
    }
    return @($tree)
}

# A host's identity across two walks: a pid alone can be reused by a different process.
function Get-TestHostKey {
    param([Parameter(Mandatory)][object]$HostProcess)
    return '{0}|{1}' -f $HostProcess.Id, $HostProcess.StartTime.Ticks
}

# The test hosts under the test-all process: the leaf processes that run the tests, selected by name.
# The `dotnet test` driver and the suite jobs idle on purpose, so they are not hosts.
function Find-TestHost {
    param(
        [Parameter(Mandatory)][int]$RootPid,
        [Parameter(Mandatory)][string]$HostNamePattern,
        [Parameter(Mandatory)][string]$ProcessNamePattern
    )
    return @(Get-TestAllProcessTree -RootPid $RootPid -ProcessNamePattern $ProcessNamePattern |
            Where-Object { $_.Name -match $HostNamePattern })
}

# Tracks each host's CPU baseline in $State (keyed by pid and start time, one clock per host) and
# returns the first host that has stayed within $CpuToleranceSeconds of its baseline for
# $IdleSeconds. A host that exits is dropped from the state.
function Test-StalledHost {
    param(
        [Parameter(Mandatory)][hashtable]$State,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Hosts,
        [Parameter(Mandatory)][int]$IdleSeconds,
        [Parameter(Mandatory)][double]$CpuToleranceSeconds
    )

    $now = [datetime]::UtcNow
    $present = @{}
    foreach ($hostProcess in $Hosts) {
        $key = Get-TestHostKey -HostProcess $hostProcess
        $present[$key] = $true
        if (-not $State.ContainsKey($key)) {
            $State[$key] = @{ BaselineCpuSeconds = $hostProcess.CpuSeconds; Since = $now }
            continue
        }
        $tracked = $State[$key]
        if (($hostProcess.CpuSeconds - $tracked.BaselineCpuSeconds) -gt $CpuToleranceSeconds) {
            $tracked.BaselineCpuSeconds = $hostProcess.CpuSeconds
            $tracked.Since = $now
            continue
        }
        if (($now - $tracked.Since).TotalSeconds -ge $IdleSeconds) {
            return $hostProcess
        }
    }
    foreach ($key in @($State.Keys)) {
        if (-not $present.ContainsKey($key)) {
            $State.Remove($key)
        }
    }
    return $null
}

# Waits a bounded time for a redirected stream's text; a stream still open after that is reported and
# dropped, so a wedged child cannot block the watchdog.
function Read-TestAllStream {
    param(
        [Parameter(Mandatory)][System.Threading.Tasks.Task[string]]$Stream,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][int]$ProcessId
    )
    try {
        if (-not $Stream.Wait(5000)) {
            Write-Host "test-all: dropped the dump process's $Name (pid $ProcessId, still open 5 s after it exited)" -ForegroundColor Red
            return ''
        }
        return $Stream.Result
    } catch {
        Write-Host "test-all: could not read the dump process's $Name (pid $ProcessId): $($_.Exception.Message)" -ForegroundColor Red
        return ''
    }
}

# Runs dotnet as a child process under its own ceiling, so an attach that hangs cannot outlast the
# watchdog. Both streams are read asynchronously: a child that fills a pipe would never exit.
function Invoke-TestAllChildProcess {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][int]$TimeoutSeconds
    )

    $dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($dotnet)
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    $processId = $process.Id
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $exited = $process.WaitForExit($TimeoutSeconds * 1000)
    if (-not $exited) {
        try {
            $process.Kill($true)
        } catch {
            Write-Host "test-all: could not kill the dump process pid ${processId}: $($_.Exception.Message)" -ForegroundColor Red
        }
        $null = $process.WaitForExit(5000)
    }
    $output = Read-TestAllStream -Stream $stdout -Name 'stdout' -ProcessId $processId
    $errorOutput = Read-TestAllStream -Stream $stderr -Name 'stderr' -ProcessId $processId
    $exitCode = if ($exited) { $process.ExitCode } else { $null }
    $process.Dispose()

    return [pscustomobject]@{
        TimedOut = -not $exited
        ExitCode = $exitCode
        Output   = $output
        Error    = $errorOutput
    }
}

# The last $Count whole lines of a suite log, readable while the job still writes to it.
function Get-TestAllLogTail {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][int]$Count
    )
    if (-not (Test-Path -LiteralPath $Path)) {
        return @()
    }
    try {
        return @(Get-Content -LiteralPath $Path -Tail $Count -ErrorAction Stop)
    } catch {
        return @("(could not read ${Path}: $($_.Exception.Message))")
    }
}

# Reports a stall and acts on it: the host and the tree, each suite log's tail, a heap dump of the
# host with its managed stacks beside it, then the kill of every watched host and of the suite jobs.
# The report and the dump are one attempt; the kills run in `finally` so a failure in the report
# cannot leave the stalled hosts running. Returns what it produced, for the caller's message and for
# a probe to assert on.
function Invoke-StallResponse {
    param(
        [Parameter(Mandatory)][object]$StalledHost,
        [Parameter(Mandatory)][int]$RootPid,
        [Parameter(Mandatory)][string]$HostNamePattern,
        [Parameter(Mandatory)][string]$ProcessNamePattern,
        [Parameter(Mandatory)][AllowEmptyCollection()][hashtable[]]$Suites,
        [Parameter(Mandatory)][string]$CheckoutRoot,
        [Parameter(Mandatory)][int]$IdleSeconds,
        [Parameter(Mandatory)][int]$CollectTimeoutSeconds,
        [Parameter(Mandatory)][int]$AnalyzeTimeoutSeconds
    )

    $result = @{
        DumpPath                 = $null
        DumpTimedOut             = $false
        AnalyzeOutput            = ''
        KilledPids               = @()
        SuitesFinishedOnTheirOwn = @()
    }
    $initialHosts = @()
    try {
        $initialHosts = @(Find-TestHost -RootPid $RootPid -HostNamePattern $HostNamePattern -ProcessNamePattern $ProcessNamePattern)
    } catch {
        Write-Host "test-all: could not walk the process tree before the kill: $($_.Exception.Message)" -ForegroundColor Red
    }

    try {
        Write-Host ''
        Write-Host "test-all: STALLED $($StalledHost.Name) pid $($StalledHost.Id): no CPU for $IdleSeconds s" -ForegroundColor Red
        Write-Host "test-all: process tree under pid $RootPid" -ForegroundColor Red
        foreach ($node in (Get-TestAllProcessTree -RootPid $RootPid -ProcessNamePattern $ProcessNamePattern)) {
            Write-Host ('  {0} pid {1} parent {2} cpu {3:N1}s started {4:yyyy-MM-dd HH:mm:ss}' -f `
                    $node.Name, $node.Id, $node.ParentId, $node.CpuSeconds, $node.StartTime)
        }

        foreach ($suite in $Suites) {
            Write-Host "test-all: last 40 lines of the $($suite.Name) suite log ($($suite.LogPath))" -ForegroundColor Red
            foreach ($line in (Get-TestAllLogTail -Path $suite.LogPath -Count 40)) {
                Write-Host "  $line"
            }
        }

        $dumpsDirectory = Join-Path (Join-Path $CheckoutRoot '.tmp') 'dumps'
        $null = New-Item -ItemType Directory -Path $dumpsDirectory -Force
        $dumpName = '{0}-{1}-{2}.dmp' -f $StalledHost.Name, $StalledHost.Id, (Get-Date).ToString('yyyyMMdd-HHmmss')
        $dumpPath = Join-Path $dumpsDirectory $dumpName

        Write-Host "test-all: dumping pid $($StalledHost.Id) to $dumpPath" -ForegroundColor Yellow
        $collect = Invoke-TestAllChildProcess -Arguments @(
            'tool', 'run', 'dotnet-dump', 'collect', '-p', "$($StalledHost.Id)", '--type', 'Heap', '-o', $dumpPath
        ) -WorkingDirectory $CheckoutRoot -TimeoutSeconds $CollectTimeoutSeconds
        $result.DumpTimedOut = $collect.TimedOut

        $dumped = $false
        if ($collect.TimedOut) {
            Write-Host "test-all: dump of $($StalledHost.Id) timed out; killing without one" -ForegroundColor Red
            if (Test-Path -LiteralPath $dumpPath) {
                Remove-Item -LiteralPath $dumpPath -Force
                Write-Host "test-all: deleted the partial dump $dumpPath" -ForegroundColor Yellow
            }
        } elseif ($collect.ExitCode -ne 0) {
            Write-Host "test-all: dump of $($StalledHost.Id) failed (exit $($collect.ExitCode)); killing without one" -ForegroundColor Red
            if ($collect.Output.Trim().Length -gt 0) { Write-Host $collect.Output.TrimEnd() }
            if ($collect.Error.Trim().Length -gt 0) { Write-Host $collect.Error.TrimEnd() }
        } elseif (-not (Test-Path -LiteralPath $dumpPath)) {
            Write-Host "test-all: dump of $($StalledHost.Id) wrote no file at $dumpPath; killing without one" -ForegroundColor Red
        } else {
            $dumped = $true
            $result.DumpPath = $dumpPath
            Write-Host "test-all: dump written to $dumpPath" -ForegroundColor Yellow
        }

        if ($dumped) {
            Write-Host "test-all: analyzing dump $dumpPath..." -ForegroundColor Yellow
            $analyze = Invoke-TestAllChildProcess -Arguments @(
                'tool', 'run', 'dotnet-dump', 'analyze', $dumpPath, '-c', 'clrstack -all', '-c', 'exit'
            ) -WorkingDirectory $CheckoutRoot -TimeoutSeconds $AnalyzeTimeoutSeconds
            if ($analyze.TimedOut) {
                Write-Host "test-all: stack analysis of $dumpPath timed out after $AnalyzeTimeoutSeconds s" -ForegroundColor Red
            } else {
                $result.AnalyzeOutput = $analyze.Output
                if ($analyze.Output.Trim().Length -gt 0) {
                    $stackPath = "$dumpPath.clrstack.txt"
                    [System.IO.File]::WriteAllText($stackPath, $analyze.Output)
                    Write-Host "test-all: stack text written to $stackPath" -ForegroundColor Yellow
                    Write-Host $analyze.Output.TrimEnd()
                }
                if ($analyze.Error.Trim().Length -gt 0) { Write-Host $analyze.Error.TrimEnd() }
            }
        }
    } catch {
        Write-Host "test-all: the stall report failed: $($_.Exception.Message)" -ForegroundColor Red
    } finally {
        # Kill from a fresh walk: the hosts identified at the start may have exited, and a pid may have
        # been reused, so only processes that are watched hosts in the tree right now are killed.
        $currentHosts = @()
        try {
            $currentHosts = @(Find-TestHost -RootPid $RootPid -HostNamePattern $HostNamePattern -ProcessNamePattern $ProcessNamePattern)
        } catch {
            Write-Host "test-all: could not re-walk the process tree for the kill: $($_.Exception.Message)" -ForegroundColor Red
            $currentHosts = $initialHosts
        }
        $currentKeys = @{}
        foreach ($hostProcess in $currentHosts) {
            $currentKeys[(Get-TestHostKey -HostProcess $hostProcess)] = $true
        }
        foreach ($initial in $initialHosts) {
            if (-not $currentKeys.ContainsKey((Get-TestHostKey -HostProcess $initial))) {
                Write-Host "test-all: $($initial.Name) pid $($initial.Id) ended on its own before the kill" -ForegroundColor Yellow
            }
        }
        foreach ($hostProcess in $currentHosts) {
            try {
                Stop-Process -Id $hostProcess.Id -Force -ErrorAction Stop
                Write-Host "test-all: killed $($hostProcess.Name) pid $($hostProcess.Id)" -ForegroundColor Red
            } catch {
                # A host that ended on its own between the walk and the kill is not a failure.
                if ($_.CategoryInfo.Category -ne [System.Management.Automation.ErrorCategory]::ObjectNotFound) {
                    Write-Host "test-all: could not kill $($hostProcess.Name) pid $($hostProcess.Id): $($_.Exception.Message)" -ForegroundColor Red
                }
            }
        }
        $result.KilledPids = @($currentHosts | ForEach-Object { $_.Id })

        foreach ($suite in $Suites) {
            if ($suite.Job.State -in 'Completed', 'Failed', 'Stopped') {
                $result.SuitesFinishedOnTheirOwn = @($result.SuitesFinishedOnTheirOwn) + $suite.Name
                continue
            }
            try {
                Stop-Job -Job $suite.Job -ErrorAction Stop
            } catch {
                if ($_.CategoryInfo.Category -ne [System.Management.Automation.ErrorCategory]::ObjectNotFound) {
                    Write-Host "test-all: could not stop the $($suite.Name) suite job: $($_.Exception.Message)" -ForegroundColor Red
                }
            }
        }
    }

    return [pscustomobject]$result
}
