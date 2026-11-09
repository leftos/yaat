#!/usr/bin/env pwsh
# Idle watchdog for tools/test-all.ps1: names a test host that has stopped doing work.
#
# Dot-sourced by test-all.ps1. A test host that keeps its suite job alive while it stops making
# progress is hung, not slow: without this the run goes on until the ceiling, with only a heartbeat to
# show it is still there and nothing naming the process or the stack it is stuck on (YAAT-177).
#
# The tree is walked with PowerShell 7's Get-Process (.Parent, .StartTime, .TotalProcessorTime)
# rather than WMI, so it also works on the macOS gate builder. Parent links are built from every live
# process that could be in the tree, whatever its name: a `cmd` between a test host and the process
# doing its work would otherwise hide that work. Reading `.Parent` for every process on the machine
# costs about a second per walk, so the walk reads the root's start time first and asks only for the
# processes that started at or after it, since nothing else can be its descendant. Only the names
# that can be hosts (pwsh, dotnet, testhost, *.Tests) are reported, and a child is kept only when it
# started at or after its apparent parent, so a reused parent PID cannot pull another session's
# processes in.
#
# Idle is judged against the host's whole tree, not against its own CPU: a hung host often still has
# a timer thread ticking (yaat-server's room tick runs every second), which moves the CPU figure but
# by far less than work, while a host waiting on child processes that do the work, or whose threads
# the machine is holding, looks idle without being hung. The clock is reset when the tree's CPU rises
# past the tolerance over its baseline, when the set of live processes under the host changes, or
# while the host, a process under it, or one above it below the root that made no CPU progress since
# the last poll has a thread the machine is holding; a host that shows none of those for the idle
# threshold is stalled. Held threads count across that span because an MTP host waits for the
# `dotnet test` driver's reply to each result it sends, so a starved driver or child leaves an idle
# host that only looks hung; an ancestor whose CPU rises is serving a sibling host, not starved.
# Ancestors' pauses extend one idle window by at most one more, so a hung host is cut within two.
#
# On a stall it prints the host, the tree and each suite log's tail, dumps the host with dotnet-dump
# (Heap, its own timeout, and the partial dump deleted when the attach is cut short), prints the
# dump's managed stacks next to the dump, then kills the watched test hosts and stops the suite jobs.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '',
    Justification = 'The stall report is the run''s console output, in test-all.ps1''s own style.')]
param()

# Every live process under $RootPid whose name matches $ProcessNamePattern, as records carrying what
# the watchdog reports: name, pid, parent pid, CPU seconds, start time and thread count. Each record
# also carries TreeCpuSeconds (its own CPU plus that of every live descendant, whatever its name) and
# TreeKeys (the descendants' pid|startTicks keys, sorted and joined), so a host whose children keep
# working is told apart from one that is hung. Throws when the root itself is missing, so a failed
# walk is never mistaken for a tree with no hosts in it.
function Get-TestAllProcessTree {
    param(
        [Parameter(Mandatory)][int]$RootPid,
        [Parameter(Mandatory)][string]$ProcessNamePattern
    )

    # The root's start time bounds the walk: nothing that started before the root can be its
    # descendant, so reading it first keeps the expensive `.Parent` query to the processes that could
    # be in the tree.
    try {
        $root = Get-Process -Id $RootPid -ErrorAction Stop
    } catch {
        throw "the process tree walk found no process $RootPid"
    }
    try {
        $rootStartTime = $root.StartTime
    } finally {
        $root.Dispose()
    }
    if ($null -eq $rootStartTime) {
        throw "the process tree walk could not read the start time of process $RootPid"
    }

    $all = @{}
    foreach ($process in @(Get-Process -ErrorAction SilentlyContinue)) {
        try {
            # A dead process another holder keeps open (a ClrMD snapshot clone the gate's job still holds)
            # reads as an idle child with no CPU: it is not part of the tree.
            if ($process.HasExited) {
                continue
            }
            $cpu = $process.TotalProcessorTime
            $start = $process.StartTime
            if (($null -eq $cpu) -or ($null -eq $start)) {
                # The process is unreadable (no access) or gone since the snapshot: it is not in the tree.
                continue
            }
            if ($start -lt $rootStartTime) {
                continue
            }
            # Read once: each read of .Parent costs a native query.
            $parent = $process.Parent
            $entry = [pscustomobject]@{
                Name           = $process.Name
                Id             = $process.Id
                ParentId       = if ($null -eq $parent) { 0 } else { $parent.Id }
                CpuSeconds     = $cpu.TotalSeconds
                StartTime      = $start
                Threads        = 0
                HeldThreads    = 0
                NameMatches    = ($process.Name -match $ProcessNamePattern)
                TreeCpuSeconds = 0
                TreeKeys       = ''
            }
            # Threads are read only for the hosts the walk reports, not for every process.
            if ($entry.NameMatches) {
                $entry.Threads = $process.Threads.Count
            }
        } catch {
            # The process exited or cannot be read: it is not in the tree.
            continue
        } finally {
            $process.Dispose()
        }
        $all[$entry.Id] = $entry
    }

    if (-not $all.ContainsKey($RootPid)) {
        throw "the process tree walk found no process $RootPid"
    }

    $byParent = @{}
    foreach ($entry in $all.Values) {
        if ($entry.ParentId -eq 0) {
            continue
        }
        if (-not $byParent.ContainsKey($entry.ParentId)) {
            $byParent[$entry.ParentId] = [System.Collections.Generic.List[object]]::new()
        }
        $byParent[$entry.ParentId].Add($entry)
    }

    $tree = [System.Collections.Generic.List[object]]::new()
    $seen = @{}
    $seen[$RootPid] = $true
    $queue = [System.Collections.Generic.Queue[object]]::new()
    $queue.Enqueue($all[$RootPid])
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
            # A name that cannot be a host stays in the walk, so its own children are still reached.
            if ($child.NameMatches) {
                $tree.Add($child)
            }
            $queue.Enqueue($child)
        }
    }

    foreach ($entry in $tree) {
        $treeCpuSeconds = $entry.CpuSeconds
        $keys = [System.Collections.Generic.List[string]]::new()
        foreach ($descendant in @(Get-TestAllSubtree -ByParent $byParent -Root $entry)) {
            $treeCpuSeconds += $descendant.CpuSeconds
            $keys.Add((Get-TestHostKey -HostProcess $descendant))
        }
        $keys.Sort([System.StringComparer]::Ordinal)
        $entry.TreeCpuSeconds = $treeCpuSeconds
        $entry.TreeKeys = $keys -join ','
    }
    return @($tree)
}

# Every live process under $Root, whatever its name, by the same parent-start guard the tree walk
# uses: a child counts only when it started at or after its parent.
function Get-TestAllSubtree {
    param(
        [Parameter(Mandatory)][hashtable]$ByParent,
        [Parameter(Mandatory)][object]$Root
    )

    $descendants = [System.Collections.Generic.List[object]]::new()
    $seen = @{}
    $seen[$Root.Id] = $true
    $queue = [System.Collections.Generic.Queue[object]]::new()
    $queue.Enqueue($Root)
    while ($queue.Count -gt 0) {
        $parent = $queue.Dequeue()
        if (-not $ByParent.ContainsKey($parent.Id)) {
            continue
        }
        foreach ($child in $ByParent[$parent.Id]) {
            if ($seen.ContainsKey($child.Id)) {
                continue
            }
            if ($child.StartTime -lt $parent.StartTime) {
                continue
            }
            $seen[$child.Id] = $true
            $descendants.Add($child)
            $queue.Enqueue($child)
        }
    }
    return @($descendants)
}

# How many of a process's threads the machine is holding rather than the process waiting on its own:
# ready or about to run with no processor free, in transition, or waiting on a page, a suspension or
# a scheduling delay. A host whose threads are held that way is starved, not hung.
function Get-TestAllHeldThreadCount {
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][long]$StartTimeTicks
    )

    $heldStates = @(
        [System.Diagnostics.ThreadState]::Ready
        [System.Diagnostics.ThreadState]::Standby
        [System.Diagnostics.ThreadState]::Transition
    )
    $heldWaitReasons = @(
        [System.Diagnostics.ThreadWaitReason]::Suspended
        [System.Diagnostics.ThreadWaitReason]::PageIn
        [System.Diagnostics.ThreadWaitReason]::PageOut
        [System.Diagnostics.ThreadWaitReason]::FreePage
        [System.Diagnostics.ThreadWaitReason]::VirtualMemory
    )

    try {
        $process = Get-Process -Id $ProcessId -ErrorAction Stop
    } catch {
        # The host ended since the walk: it has no held threads.
        return 0
    }
    $held = 0
    try {
        # The pid may have been reused since the walk: only the process the walk saw has its threads read.
        if ($process.StartTime.Ticks -ne $StartTimeTicks) {
            return 0
        }
        foreach ($thread in $process.Threads) {
            try {
                # WaitReason is only valid while the thread is in Wait.
                if ($thread.ThreadState -in $heldStates) {
                    $held++
                } elseif (($thread.ThreadState -eq [System.Diagnostics.ThreadState]::Wait) -and
                        ($thread.WaitReason -in $heldWaitReasons)) {
                    $held++
                }
            } catch {
                # The thread exited between the snapshot and the property read: it is not held.
                continue
            }
        }
    } catch {
        # The host ended or cannot be read: it has no held threads.
        return 0
    } finally {
        $process.Dispose()
    }
    return $held
}

# A host's identity across two walks: a pid alone can be reused by a different process.
function Get-TestHostKey {
    param([Parameter(Mandatory)][object]$HostProcess)
    return '{0}|{1}' -f $HostProcess.Id, $HostProcess.StartTime.Ticks
}

# The test hosts under the test-all process: the leaf processes that run the tests, selected by name.
# The `dotnet test` driver and the suite jobs idle on purpose, so they are not hosts. Each host also
# carries its tree figures and the count of threads the machine is holding in it and in the processes
# it may be waiting on, for Test-StalledHost.
function Find-TestHost {
    param(
        [Parameter(Mandatory)][int]$RootPid,
        [Parameter(Mandatory)][string]$HostNamePattern,
        [Parameter(Mandatory)][string]$ProcessNamePattern
    )
    # A process with no threads is a snapshot clone (ManagedStackCaptureTests), live or killed, never a host.
    $tree = @(Get-TestAllProcessTree -RootPid $RootPid -ProcessNamePattern $ProcessNamePattern)
    $byId = @{}
    foreach ($node in $tree) {
        $byId[$node.Id] = $node
    }
    $hosts = @($tree | Where-Object { ($_.Threads -gt 0) -and ($_.Name -match $HostNamePattern) })
    # One poll's held counts by pid|startTicks: hosts under one driver share their ancestors, and each
    # thread read takes a whole-system snapshot, so each process is read once per poll. The cache is
    # this call's own and is dropped with it, so no count crosses polls.
    $heldCache = @{}
    foreach ($hostProcess in $hosts) {
        $ancestors = @(Get-TestAllAncestor -HostProcess $hostProcess -ById $byId -HeldCache $heldCache)
        $hostProcess.HeldThreads = Get-TestAllWaitedOnHeldThreadCount -HostProcess $hostProcess -Ancestors $ancestors -HeldCache $heldCache
        $hostProcess | Add-Member -NotePropertyName Ancestors -NotePropertyValue $ancestors -Force
    }
    return $hosts
}

# Get-TestAllHeldThreadCount read at most once per pid|startTicks per $HeldCache.
function Get-TestAllCachedHeldThreadCount {
    param(
        [Parameter(Mandatory)][hashtable]$HeldCache,
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][long]$StartTimeTicks
    )

    $key = '{0}|{1}' -f $ProcessId, $StartTimeTicks
    if (-not $HeldCache.ContainsKey($key)) {
        $HeldCache[$key] = Get-TestAllHeldThreadCount -ProcessId $ProcessId -StartTimeTicks $StartTimeTicks
    }
    return $HeldCache[$key]
}

# How many threads the machine is holding in a host and in the processes it may be waiting on: its
# descendants (TreeKeys) and $Ancestors, the records Get-TestAllAncestor gave for it.
function Get-TestAllWaitedOnHeldThreadCount {
    param(
        [Parameter(Mandatory)][object]$HostProcess,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Ancestors,
        [Parameter(Mandatory)][hashtable]$HeldCache
    )

    $held = Get-TestAllCachedHeldThreadCount -HeldCache $HeldCache -ProcessId $HostProcess.Id -StartTimeTicks $HostProcess.StartTime.Ticks
    foreach ($key in @($HostProcess.TreeKeys -split ',' | Where-Object { $_.Length -gt 0 })) {
        $parts = $key -split '\|'
        $held += Get-TestAllCachedHeldThreadCount -HeldCache $HeldCache -ProcessId ([int]$parts[0]) -StartTimeTicks ([long]$parts[1])
    }
    foreach ($ancestor in $Ancestors) {
        $held += $ancestor.HeldThreads
    }
    return $held
}

# A host's ancestors below the root, each as its key, its own CPU seconds and the count of its threads
# the machine is holding. They are followed by ParentId through $ById, the walk's own records, which
# never hold the root, so the chain stops beneath it; a record that started after the process it would
# be the parent of is a reused pid and ends the chain too.
function Get-TestAllAncestor {
    param(
        [Parameter(Mandatory)][object]$HostProcess,
        [Parameter(Mandatory)][hashtable]$ById,
        [Parameter(Mandatory)][hashtable]$HeldCache
    )

    $ancestors = [System.Collections.Generic.List[object]]::new()
    $seen = @{}
    $seen[$HostProcess.Id] = $true
    $child = $HostProcess
    while ($ById.ContainsKey($child.ParentId) -and (-not $seen.ContainsKey($child.ParentId))) {
        $ancestor = $ById[$child.ParentId]
        if ($ancestor.StartTime -gt $child.StartTime) {
            break
        }
        $seen[$ancestor.Id] = $true
        $ancestors.Add([pscustomobject]@{
                Key         = Get-TestHostKey -HostProcess $ancestor
                CpuSeconds  = $ancestor.CpuSeconds
                HeldThreads = Get-TestAllCachedHeldThreadCount -HeldCache $HeldCache -ProcessId $ancestor.Id -StartTimeTicks $ancestor.StartTime.Ticks
            })
        $child = $ancestor
    }
    return @($ancestors)
}

# The held threads that pause a host's clock, split in two: Own, those in the host and under it, and
# Ancestor, those in each ancestor whose CPU did not rise past $CpuToleranceSeconds since the previous
# poll. An ancestor whose CPU rose is serving other work (the `dotnet test` driver answering a sibling
# host), not starved, so its held threads say nothing about this host. An ancestor first seen at this
# poll counts as starved. Records each ancestor's CPU in $Tracked for the next poll; a host record with
# no Ancestors (a synthetic one) has all its held threads as its own.
function Get-TestAllPausingHeldThreads {
    param(
        [Parameter(Mandatory)][object]$HostProcess,
        [Parameter(Mandatory)][hashtable]$Tracked,
        [Parameter(Mandatory)][double]$CpuToleranceSeconds
    )

    $ancestorsProperty = $HostProcess.PSObject.Properties['Ancestors']
    $ancestors = if ($null -eq $ancestorsProperty) { @() } else { @($ancestorsProperty.Value) }
    $own = $HostProcess.HeldThreads
    $starved = 0
    $cpuByKey = @{}
    foreach ($ancestor in $ancestors) {
        $cpuByKey[$ancestor.Key] = $ancestor.CpuSeconds
        $own -= $ancestor.HeldThreads
        $previous = $Tracked.AncestorCpu[$ancestor.Key]
        if (($null -eq $previous) -or (($ancestor.CpuSeconds - $previous) -le $CpuToleranceSeconds)) {
            $starved += $ancestor.HeldThreads
        }
    }
    $Tracked.AncestorCpu = $cpuByKey
    return [pscustomobject]@{ Own = $own; Ancestor = $starved }
}

# How far a held poll moves a host's idle clock on: the whole $Interval when the host or a process
# under it is held, with no limit; when only starved ancestors are held, the interval up to what is
# left of $CapSeconds of ancestor pauses ($Tracked.AncestorPause, cleared when the clock resets), so a
# host blocked in its own wait is cut within the idle window plus the cap however its ancestors
# behave. $null when nothing held pauses it.
function Get-TestAllIdlePause {
    param(
        [Parameter(Mandatory)][object]$Held,
        [Parameter(Mandatory)][hashtable]$Tracked,
        [Parameter(Mandatory)][timespan]$Interval,
        [Parameter(Mandatory)][int]$CapSeconds
    )

    if ($Held.Own -gt 0) {
        return $Interval
    }
    $left = [timespan]::FromSeconds($CapSeconds) - $Tracked.AncestorPause
    if (($Held.Ancestor -le 0) -or ($left -le [timespan]::Zero)) {
        return $null
    }
    $grant = if ($Interval -lt $left) { $Interval } else { $left }
    $Tracked.AncestorPause += $grant
    return $grant
}

# Moves one known host's idle clock ($Tracked, its entry in Test-StalledHost's state) on to $Now and
# returns whether the host has now shown no progress for $IdleSeconds. A negative
# $AncestorPauseCapSeconds caps ancestor pauses at one idle window.
function Update-TestAllHostClock {
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)][object]$HostProcess,
        [Parameter(Mandatory)][hashtable]$Tracked,
        [Parameter(Mandatory)][datetime]$Now,
        [Parameter(Mandatory)][int]$IdleSeconds,
        [Parameter(Mandatory)][double]$CpuToleranceSeconds,
        [Parameter(Mandatory)][int]$AncestorPauseCapSeconds
    )

    $held = Get-TestAllPausingHeldThreads -HostProcess $HostProcess -Tracked $Tracked -CpuToleranceSeconds $CpuToleranceSeconds
    $treeGrew = (($HostProcess.TreeCpuSeconds - $Tracked.BaselineTreeCpuSeconds) -gt $CpuToleranceSeconds)
    $treeChanged = ($HostProcess.TreeKeys -ne $Tracked.TreeKeys)
    if ($treeGrew -or $treeChanged) {
        $Tracked.BaselineTreeCpuSeconds = $HostProcess.TreeCpuSeconds
        $Tracked.TreeKeys = $HostProcess.TreeKeys
        $Tracked.Since = $Now
        $Tracked.LastPoll = $Now
        $Tracked.AncestorPause = [timespan]::Zero
        return $false
    }
    $ancestorCap = if ($AncestorPauseCapSeconds -lt 0) { $IdleSeconds } else { $AncestorPauseCapSeconds }
    $interval = $Now - $Tracked.LastPoll
    $pause = Get-TestAllIdlePause -Held $held -Tracked $Tracked -Interval $interval -CapSeconds $ancestorCap
    $Tracked.LastPoll = $Now
    if ($null -ne $pause) {
        # The machine held this host for the pause: the idle clock moves on by it, and the baseline
        # and keys stay as they were. A pause short of the interval (the ancestor cap ran out) still
        # leaves the rest of the interval as idle time to judge.
        $Tracked.Since = $Tracked.Since.Add($pause)
        if ($pause -eq $interval) {
            return $false
        }
    }
    return (($Now - $Tracked.Since).TotalSeconds -ge $IdleSeconds)
}

# Tracks each host's progress in $State (keyed by pid and start time, one clock per host) and returns
# the first host that has shown no progress for $IdleSeconds. Progress is the tree's CPU rising past
# $CpuToleranceSeconds over its baseline, or a set of live processes under the host that changed since
# the last walk; either resets the clock. A poll that finds a thread the machine is holding (in the
# host, under it, or in an ancestor that made no CPU progress since the previous poll) pauses the
# clock for the interval since the last poll instead of resetting it, since the host has yet to show
# what it does once the machine lets it run; pauses from ancestors alone add up to at most
# $AncestorPauseCapSeconds between resets, one idle window when it is not given (test-all never
# gives it). A host that shows no progress and no held thread for the idle threshold is stalled; one
# that exits is dropped from the state. The clock is injectable for the case table, and test-all.ps1
# never passes it.
function Test-StalledHost {
    param(
        [Parameter(Mandatory)][hashtable]$State,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Hosts,
        [Parameter(Mandatory)][int]$IdleSeconds,
        [Parameter(Mandatory)][double]$CpuToleranceSeconds,
        [scriptblock]$Clock = { [datetime]::UtcNow },
        [int]$AncestorPauseCapSeconds = -1
    )

    $now = & $Clock
    $present = @{}
    foreach ($hostProcess in $Hosts) {
        $key = Get-TestHostKey -HostProcess $hostProcess
        $present[$key] = $true
        if (-not $State.ContainsKey($key)) {
            $State[$key] = @{
                BaselineTreeCpuSeconds = $hostProcess.TreeCpuSeconds
                TreeKeys               = $hostProcess.TreeKeys
                Since                  = $now
                LastPoll               = $now
                AncestorCpu            = @{}
                AncestorPause          = [timespan]::Zero
            }
            $null = Get-TestAllPausingHeldThreads -HostProcess $hostProcess -Tracked $State[$key] -CpuToleranceSeconds $CpuToleranceSeconds
            continue
        }
        $clockArgs = @{
            HostProcess             = $hostProcess
            Tracked                 = $State[$key]
            Now                     = $now
            IdleSeconds             = $IdleSeconds
            CpuToleranceSeconds     = $CpuToleranceSeconds
            AncestorPauseCapSeconds = $AncestorPauseCapSeconds
        }
        if (Update-TestAllHostClock @clockArgs) {
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
