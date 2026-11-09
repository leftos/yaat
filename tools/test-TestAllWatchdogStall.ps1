#!/usr/bin/env pwsh
# Case table for tools/TestAllWatchdog.ps1's stall verdict: a host is called stalled only when it is
# blocked in its own wait. Case A waits on a child that keeps working, with a process named outside
# the walk's pattern between the two; case A2 waits on a child that burns CPU without adding a
# process; case B has its threads held by the machine; case C, the control, is blocked in its own
# wait with no child and must still read STALLED; case D sleeps in Thread.Sleep, which is the host's
# own timer, not the machine holding it, and must read STALLED too; case E drives the idle clock by
# hand to show a poll with held threads pauses it rather than resetting it; cases F and G wait on a
# parent and on a child the machine holds, and must not read STALLED; cases H, I and J drive the
# clock by hand for a host whose ancestor has a held thread at every poll, which pauses the clock only
# while that ancestor's CPU stays under the progress bar, and for one idle window at most. Every wait
# is on a named event, a process handle, or a poll until the host has no held thread, each with a hang
# guard, and no case reads the wall clock. Test-StalledHost is called with IdleSeconds 0 except in
# cases E, H, I and J, so each of the other verdicts rests only on the progress it saw between two walks.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '',
    Justification = 'The case results are the script''s console output, as in test-all.ps1.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '',
    Justification = 'The case helpers start and stop only this case''s own child processes, which the script cleans up.')]
param([string]$WatchdogScript = (Join-Path $PSScriptRoot 'TestAllWatchdog.ps1'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. $WatchdogScript

Add-Type -Namespace WatchdogCase1767 -Name Nt -MemberDefinition @'
[DllImport("ntdll.dll")] public static extern int NtSuspendProcess(System.IntPtr process);
[DllImport("ntdll.dll")] public static extern int NtResumeProcess(System.IntPtr process);
'@

$hangGuardMs = 600000
$hostPattern = '\.Tests$|^testhost'
$walkPattern = '^pwsh$|^dotnet$|^testhost|\.Tests$'
$tag = [guid]::NewGuid().ToString('N')
$folder = Join-Path ([IO.Path]::GetTempPath()) "watchdog-case-$tag"
New-Item -ItemType Directory -Path $folder | Out-Null
$fakeHost = Join-Path $folder 'Fake.Tests.exe'
Copy-Item (Join-Path $env:SystemRoot 'System32\cmd.exe') $fakeHost
$pwshExe = (Get-Process -Id $PID).Path

function New-CaseEvent([string]$suffix) {
    $name = "Local\watchdog-case-$tag-$suffix"
    return [pscustomobject]@{ Name = $name; Handle = [System.Threading.EventWaitHandle]::new($false, 'ManualReset', $name) }
}
function Wait-CaseEvent([object]$evt, [string]$what) {
    if (-not $evt.Handle.WaitOne($hangGuardMs)) { throw "hung: $what" }
}
function Start-FakeHost([string]$arguments) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($fakeHost, $arguments)
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    return [System.Diagnostics.Process]::Start($psi)
}
function Start-CommandHost([string]$command) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($pwshExe)
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.ArgumentList.Add('-NoProfile')
    $psi.ArgumentList.Add('-Command')
    $psi.ArgumentList.Add($command)
    return [System.Diagnostics.Process]::Start($psi)
}
function Get-Verdict([hashtable]$state, [System.Diagnostics.Process]$fake, [string]$hostNamePattern = $hostPattern) {
    $hosts = @(Find-TestHost -RootPid $PID -HostNamePattern $hostNamePattern -ProcessNamePattern $walkPattern | Where-Object Id -EQ $fake.Id)
    if ($hosts.Count -ne 1) { throw "the walk found $($hosts.Count) hosts with pid $($fake.Id)" }
    # The ancestor pause cap is lifted, so a verdict here never rests on how long a walk took (the
    # pauses granted between two polls are the time between them); cases I and J pin the cap.
    return Test-StalledHost -State $state -Hosts $hosts -IdleSeconds 0 -CpuToleranceSeconds 0.5 -AncestorPauseCapSeconds ([int]::MaxValue)
}
# Waits until the walk's record of $p shows no held thread, the figure the verdict reads, so a verdict
# cannot rest on a thread the machine happens to be holding at the poll (load would make it vacuous).
function Wait-HostSettled([System.Diagnostics.Process]$p, [string]$what, [string]$hostNamePattern = $hostPattern) {
    $guard = [System.Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $hosts = @(Find-TestHost -RootPid $PID -HostNamePattern $hostNamePattern -ProcessNamePattern $walkPattern | Where-Object Id -EQ $p.Id)
        if ($hosts.Count -ne 1) { throw "$($what): the walk found $($hosts.Count) hosts with pid $($p.Id)" }
        if ($hosts[0].HeldThreads -eq 0) { return }
        if ($guard.ElapsedMilliseconds -gt $hangGuardMs) { throw "hung: $what" }
        Start-Sleep -Milliseconds 100
    }
}
function Suspend-CaseProcess([System.Diagnostics.Process]$p, [string]$case) {
    $status = [WatchdogCase1767.Nt]::NtSuspendProcess($p.Handle)
    if ($status -ne 0) { throw ('{0}: NtSuspendProcess failed 0x{1:X8}' -f $case, $status) }
}
# Drives Test-StalledHost on an injected clock, one poll every 10 s, for a synthetic host whose one
# ancestor has a held thread at every poll and whose CPU rises by $cpuStep between polls. Returns the
# second of the first poll that called the host stalled, or $null when none of $polls polls did.
function Get-AncestorCaseStallSecond([double]$cpuStep, [int]$polls) {
    $clockState = [pscustomobject]@{ Now = [datetime]::new(2026, 1, 1, 0, 0, 0, [DateTimeKind]::Utc) }
    $clock = { $clockState.Now }.GetNewClosure()
    $ancestor = [pscustomobject]@{ Key = '4343|1'; CpuSeconds = 5.0; HeldThreads = 1 }
    $synthetic = [pscustomobject]@{
        Name           = 'Synthetic.Tests'
        Id             = 4244
        StartTime      = $clockState.Now.AddMinutes(-5)
        CpuSeconds     = 1.0
        Threads        = 4
        HeldThreads    = 1
        TreeCpuSeconds = 10.0
        TreeKeys       = ''
        Ancestors      = @($ancestor)
    }
    $state = @{}
    for ($poll = 0; $poll -lt $polls; $poll++) {
        if ($null -ne (Test-StalledHost -State $state -Hosts @($synthetic) -IdleSeconds 90 -CpuToleranceSeconds 0.5 -Clock $clock)) {
            return $poll * 10
        }
        $clockState.Now = $clockState.Now.AddSeconds(10)
        $ancestor.CpuSeconds += $cpuStep
    }
    return $null
}
function Get-ThreadStateText([System.Diagnostics.Process]$p) {
    $p.Refresh()
    return ($p.Threads | ForEach-Object { if ($_.ThreadState -eq 'Wait') { "Wait:$($_.WaitReason)" } else { "$($_.ThreadState)" } }) -join ','
}

$failures = @()
$started = @()
$events = @()
try {
    # Case A (run 1): the host waits on a child that keeps working, here starting a new process between
    # the walks, and a `cmd` whose name is outside the walk's pattern sits between host and child.
    $ready = New-CaseEvent 'ready'; $go = New-CaseEvent 'go'; $spawned = New-CaseEvent 'spawned'; $release = New-CaseEvent 'release'
    $events += $ready, $go, $spawned, $release
    $hold = "[System.Threading.EventWaitHandle]::OpenExisting('$($release.Name)').WaitOne() | Out-Null"
    $child = @"
`$ready = [System.Threading.EventWaitHandle]::OpenExisting('$($ready.Name)')
`$go = [System.Threading.EventWaitHandle]::OpenExisting('$($go.Name)')
`$spawned = [System.Threading.EventWaitHandle]::OpenExisting('$($spawned.Name)')
`$null = `$ready.Set()
`$null = `$go.WaitOne()
`$grand = Start-Process -FilePath '$pwshExe' -ArgumentList '-NoProfile','-Command',"$hold" -NoNewWindow -PassThru
`$null = `$spawned.Set()
$hold
"@
    Set-Content -Path (Join-Path $folder 'child.ps1') -Value $child
    $hostA = Start-FakeHost "/c cmd /c `"`"$pwshExe`" -NoProfile -File `"$folder\child.ps1`"`""
    $started += $hostA
    Wait-CaseEvent $ready 'case A: the child never came up'
    $stateA = @{}
    $first = Get-Verdict $stateA $hostA
    $null = $go.Handle.Set()
    Wait-CaseEvent $spawned 'case A: the child never started its grandchild'
    $treeA = @(Get-TestAllProcessTree -RootPid $hostA.Id -ProcessNamePattern $walkPattern)
    $verdictA = Get-Verdict $stateA $hostA
    $treeIdsA = @($treeA | ForEach-Object Id) -join ','
    $verdictTextA = if ($verdictA) { 'STALLED' } else { 'not stalled' }
    Write-Host "case A: host pid $($hostA.Id), tree pids $treeIdsA, first $([bool]$first), verdict $verdictTextA"
    if ($null -ne $verdictA) {
        $failures += "case A: a host whose child started a new process between the walks, through a name outside the walk's pattern, was called STALLED"
    }

    # Case A2 (run 2): the descendant set stays the same and the child burns CPU instead, so only the
    # tree's CPU figure can tell the work apart from a hang.
    $ready2 = New-CaseEvent 'ready2'; $go2 = New-CaseEvent 'go2'; $spun = New-CaseEvent 'spun2'; $release2 = New-CaseEvent 'release2'
    $events += $ready2, $go2, $spun, $release2
    $hold2 = "[System.Threading.EventWaitHandle]::OpenExisting('$($release2.Name)').WaitOne() | Out-Null"
    $spinner = @"
`$ready = [System.Threading.EventWaitHandle]::OpenExisting('$($ready2.Name)')
`$go = [System.Threading.EventWaitHandle]::OpenExisting('$($go2.Name)')
`$spun = [System.Threading.EventWaitHandle]::OpenExisting('$($spun.Name)')
`$proc = [System.Diagnostics.Process]::GetCurrentProcess()
`$null = `$ready.Set()
`$null = `$go.WaitOne()
`$proc.Refresh()
`$baseline = `$proc.TotalProcessorTime.TotalSeconds
while ((`$proc.TotalProcessorTime.TotalSeconds - `$baseline) -lt 1) { `$proc.Refresh() }
`$null = `$spun.Set()
$hold2
"@
    Set-Content -Path (Join-Path $folder 'spinner.ps1') -Value $spinner
    $hostA2 = Start-FakeHost "/c `"`"$pwshExe`" -NoProfile -File `"$folder\spinner.ps1`"`""
    $started += $hostA2
    Wait-CaseEvent $ready2 'case A2: the spinner never came up'
    $stateA2 = @{}
    $firstA2 = Get-Verdict $stateA2 $hostA2
    $null = $go2.Handle.Set()
    Wait-CaseEvent $spun 'case A2: the spinner never used its CPU'
    $treeA2 = @(Get-TestAllProcessTree -RootPid $hostA2.Id -ProcessNamePattern $walkPattern)
    $verdictA2 = Get-Verdict $stateA2 $hostA2
    $treeIdsA2 = @($treeA2 | ForEach-Object Id) -join ','
    $verdictTextA2 = if ($verdictA2) { 'STALLED' } else { 'not stalled' }
    Write-Host "case A2: host pid $($hostA2.Id), tree pids $treeIdsA2, first $([bool]$firstA2), verdict $verdictTextA2"
    if ($null -ne $verdictA2) {
        $failures += "case A2: a host whose child burned CPU without adding a process was called STALLED (the tree's CPU is the only signal)"
    }

    # Case B (run 2): the host's threads are held by the machine (NtSuspendProcess, as suspend-rig models starvation).
    $hostB = Start-FakeHost '/c pause'
    $started += $hostB
    $stateB = @{}
    Suspend-CaseProcess $hostB 'case B'
    $null = Get-Verdict $stateB $hostB
    $verdictB = Get-Verdict $stateB $hostB
    Write-Host "case B: host pid $($hostB.Id) threads [$(Get-ThreadStateText $hostB)], verdict $(if ($verdictB) { 'STALLED' } else { 'not stalled' })"
    if ($null -ne $verdictB) {
        $failures += "case B: a host whose threads are all held Suspended by the machine was called STALLED"
    }
    $null = [WatchdogCase1767.Nt]::NtResumeProcess($hostB.Handle)

    # Case C (control): a host blocked in its own wait, with no child and no thread held by the machine, is stalled.
    $hostC = Start-FakeHost '/c pause'
    $started += $hostC
    $stateC = @{}
    $null = Get-Verdict $stateC $hostC
    Wait-HostSettled $hostC 'case C: the host never settled in its wait'
    $verdictC = Get-Verdict $stateC $hostC
    Write-Host "case C: host pid $($hostC.Id) threads [$(Get-ThreadStateText $hostC)], verdict $(if ($verdictC) { 'STALLED' } else { 'not stalled' })"
    if ($null -eq $verdictC) {
        $failures += "case C: a host blocked in its own wait with no child was not called STALLED"
    }

    # Case D (run 3): the host sleeps in Thread.Sleep, which reads Wait:ExecutionDelay. That is the
    # host waiting on its own timer, not the machine holding it, so it is stalled.
    $slept = New-CaseEvent 'slept'
    $events += $slept
    $hostD = Start-CommandHost "[System.Threading.EventWaitHandle]::OpenExisting('$($slept.Name)').Set() | Out-Null; [System.Threading.Thread]::Sleep([int]::MaxValue)"
    $started += $hostD
    Wait-CaseEvent $slept 'case D: the sleeping host never set its event'
    $stateD = @{}
    $null = Get-Verdict -state $stateD -fake $hostD -hostNamePattern '^pwsh$'
    Wait-HostSettled $hostD 'case D: the host never settled in its sleep' '^pwsh$'
    $verdictD = Get-Verdict -state $stateD -fake $hostD -hostNamePattern '^pwsh$'
    Write-Host "case D: host pid $($hostD.Id) threads [$(Get-ThreadStateText $hostD)], verdict $(if ($verdictD) { 'STALLED' } else { 'not stalled' })"
    if ($null -eq $verdictD) {
        $failures += "case D: a host sleeping in its own timer was not called STALLED (Thread.Sleep is not the machine holding it)"
    }

    # Case E (clock): a poll with held threads pauses the idle clock instead of resetting it. Two held
    # polls with an idle poll between them must leave the host's Since exactly one held interval
    # further on, not at now, and must leave the CPU baseline and the tree keys alone.
    try {
        $clockState = [pscustomobject]@{ Now = [datetime]::new(2026, 1, 1, 0, 0, 0, [DateTimeKind]::Utc) }
        $clock = { $clockState.Now }
        $synthetic = [pscustomobject]@{
            Name           = 'Synthetic.Tests'
            Id             = 4242
            StartTime      = $clockState.Now.AddMinutes(-5)
            CpuSeconds     = 1.0
            Threads        = 4
            HeldThreads    = 0
            TreeCpuSeconds = 10.0
            TreeKeys       = '4242|1'
        }
        $stateE = @{}
        $null = Test-StalledHost -State $stateE -Hosts @($synthetic) -IdleSeconds 60 -CpuToleranceSeconds 0.5 -Clock $clock
        $keyE = Get-TestHostKey -HostProcess $synthetic
        $clockState.Now = $clockState.Now.AddSeconds(10)
        $synthetic.HeldThreads = 2
        $null = Test-StalledHost -State $stateE -Hosts @($synthetic) -IdleSeconds 60 -CpuToleranceSeconds 0.5 -Clock $clock
        $afterHeld = $stateE[$keyE].Since
        $clockState.Now = $clockState.Now.AddSeconds(10)
        $synthetic.HeldThreads = 0
        $null = Test-StalledHost -State $stateE -Hosts @($synthetic) -IdleSeconds 60 -CpuToleranceSeconds 0.5 -Clock $clock
        $beforeSecondHold = $stateE[$keyE].Since
        $clockState.Now = $clockState.Now.AddSeconds(10)
        $synthetic.HeldThreads = 2
        $null = Test-StalledHost -State $stateE -Hosts @($synthetic) -IdleSeconds 60 -CpuToleranceSeconds 0.5 -Clock $clock
        $moved = [int]($stateE[$keyE].Since - $beforeSecondHold).TotalSeconds
        Write-Host "case E: a held poll moved the idle clock $([int]($afterHeld - $beforeSecondHold).TotalSeconds)s, the second $($moved)s"
        if ($moved -ne 10) {
            $failures += "case E: a held poll moved the idle clock by $($moved) s, not the held interval of 10 s"
        }
        if (($stateE[$keyE].BaselineTreeCpuSeconds -ne 10.0) -or ($stateE[$keyE].TreeKeys -ne '4242|1')) {
            $failures += "case E: a held poll reset the CPU baseline or the tree keys"
        }
        $clockState.Now = $clockState.Now.AddSeconds(50)
        $synthetic.HeldThreads = 0
        $verdictE = Test-StalledHost -State $stateE -Hosts @($synthetic) -IdleSeconds 60 -CpuToleranceSeconds 0.5 -Clock $clock
        Write-Host "case E: a poll 50 s later with no progress is $(if ($verdictE) { 'STALLED' } else { 'not stalled' })"
        if ($null -eq $verdictE) {
            $failures += "case E: 60 s of its own idle time since the clock last moved was not called STALLED"
        }
    } catch {
        $failures += "case E: the idle clock could not be driven: $($_.Exception.Message)"
    }

    # Case F (YAAT-1964): the host waits on its parent, as an MTP test host waits on the `dotnet test`
    # driver's reply to each result it sends, and the machine holds the parent. The host itself is
    # idle in its own wait; the process it waits on is starved, so the host is not stalled.
    $readyF = New-CaseEvent 'readyF'
    $events += $readyF
    $parentScript = @"
`$psi = [System.Diagnostics.ProcessStartInfo]::new('$fakeHost', '/c pause')
`$psi.UseShellExecute = `$false
`$psi.RedirectStandardInput = `$true
`$h = [System.Diagnostics.Process]::Start(`$psi)
`$null = [System.Threading.EventWaitHandle]::OpenExisting('$($readyF.Name)').Set()
`$h.WaitForExit()
"@
    Set-Content -Path (Join-Path $folder 'parentF.ps1') -Value $parentScript
    $parentF = Start-CommandHost "& '$folder\parentF.ps1'"
    $started += $parentF
    Wait-CaseEvent $readyF 'case F: the parent never started its host'
    $hostF = @(Get-TestAllProcessTree -RootPid $parentF.Id -ProcessNamePattern $walkPattern | Where-Object Name -EQ 'Fake.Tests')
    if ($hostF.Count -ne 1) { throw "case F: the walk found $($hostF.Count) hosts under the parent" }
    $hostFProcess = Get-Process -Id $hostF[0].Id
    $started += $hostFProcess
    Wait-HostSettled $hostFProcess 'case F: the host never settled in its wait'
    Suspend-CaseProcess $parentF 'case F'
    try {
        $stateF = @{}
        $null = Get-Verdict $stateF $hostFProcess
        $verdictF = Get-Verdict $stateF $hostFProcess
        Write-Host "case F: host pid $($hostFProcess.Id) threads [$(Get-ThreadStateText $hostFProcess)], parent pid $($parentF.Id) threads [$(Get-ThreadStateText $parentF)], verdict $(if ($verdictF) { 'STALLED' } else { 'not stalled' })"
        if ($null -ne $verdictF) {
            $failures += "case F: a host waiting on a parent whose threads the machine holds was called STALLED"
        }
    } finally {
        $null = [WatchdogCase1767.Nt]::NtResumeProcess($parentF.Handle)
    }

    # Case G (YAAT-1964): the host waits on a child the machine holds, with no CPU and no new process
    # between the walks, as a test host waits on a starved launch script. Not stalled.
    $readyG = New-CaseEvent 'readyG'; $releaseG = New-CaseEvent 'releaseG'
    $events += $readyG, $releaseG
    $childG = @"
`$null = [System.Threading.EventWaitHandle]::OpenExisting('$($readyG.Name)').Set()
[System.Threading.EventWaitHandle]::OpenExisting('$($releaseG.Name)').WaitOne() | Out-Null
"@
    Set-Content -Path (Join-Path $folder 'childG.ps1') -Value $childG
    $hostG = Start-FakeHost "/c `"`"$pwshExe`" -NoProfile -File `"$folder\childG.ps1`"`""
    $started += $hostG
    Wait-CaseEvent $readyG 'case G: the child never came up'
    $childGNode = @(Get-TestAllProcessTree -RootPid $hostG.Id -ProcessNamePattern '^pwsh$')
    if ($childGNode.Count -ne 1) { throw "case G: the walk found $($childGNode.Count) pwsh children under the host" }
    $childGProcess = Get-Process -Id $childGNode[0].Id
    $started += $childGProcess
    Wait-HostSettled $hostG 'case G: the host never settled in its wait'
    Suspend-CaseProcess $childGProcess 'case G'
    try {
        $stateG = @{}
        $null = Get-Verdict $stateG $hostG
        $verdictG = Get-Verdict $stateG $hostG
        Write-Host "case G: host pid $($hostG.Id) threads [$(Get-ThreadStateText $hostG)], child pid $($childGProcess.Id) threads [$(Get-ThreadStateText $childGProcess)], verdict $(if ($verdictG) { 'STALLED' } else { 'not stalled' })"
        if ($null -ne $verdictG) {
            $failures += "case G: a host waiting on a child whose threads the machine holds was called STALLED"
        }
    } finally {
        $null = [WatchdogCase1767.Nt]::NtResumeProcess($childGProcess.Handle)
    }

    # Case H (clock): the host's ancestor has a held thread at every poll but its CPU rises past the
    # tolerance each time, as a `dotnet test` driver serving a sibling host does. It is not starved, so
    # it does not pause the clock: a host idle in its own wait is still called stalled at 90 s.
    $stallSecondH = Get-AncestorCaseStallSecond -cpuStep 1.0 -polls 10
    Write-Host "case H: a host under a held ancestor whose CPU rises is $(if ($null -ne $stallSecondH) { "STALLED at $($stallSecondH) s" } else { 'not stalled by 90 s' })"
    if (($null -eq $stallSecondH) -or ($stallSecondH -gt 90)) {
        $failures += "case H: a host whose held ancestor kept making CPU progress was not called STALLED within 90 s"
    }

    # Case I (clock): the same ancestor with its CPU standing still, as a starved driver's does, pauses
    # the clock until its pauses add up to one idle window: the host is not stalled at 175 s, and is
    # stalled by 185 s, one window of pause plus one window of its own idle time.
    $stallSecondI = Get-AncestorCaseStallSecond -cpuStep 0.0 -polls 20
    Write-Host "case I: a host under a held ancestor whose CPU stands still is $(if ($null -ne $stallSecondI) { "STALLED at $($stallSecondI) s" } else { 'not stalled by 190 s' })"
    if (($null -ne $stallSecondI) -and ($stallSecondI -le 175)) {
        $failures += "case I: a host whose held ancestor made no CPU progress was called STALLED at $($stallSecondI) s, before the pause cap was used"
    }
    if (($null -eq $stallSecondI) -or ($stallSecondI -gt 185)) {
        $failures += "case I: a host whose held ancestor made no CPU progress was not called STALLED by 185 s (the ancestor pause cap is one window)"
    }

    # Case J (clock): the ancestor's CPU rises 0.1 s per poll, under the 0.5 s progress bar, as a
    # driver relaying a sibling's results on a sliver of a core may. Its pauses are capped at one
    # window, so the host is cut by 180 s, and not before its own 90 s.
    $stallSecondJ = Get-AncestorCaseStallSecond -cpuStep 0.1 -polls 20
    Write-Host "case J: a host under a held ancestor whose CPU creeps is $(if ($null -ne $stallSecondJ) { "STALLED at $($stallSecondJ) s" } else { 'not stalled by 190 s' })"
    if (($null -eq $stallSecondJ) -or ($stallSecondJ -lt 90) -or ($stallSecondJ -gt 180)) {
        $failures += "case J: a host whose held ancestor's CPU crept under the progress bar was not called STALLED between 90 s and 180 s (got $(if ($null -ne $stallSecondJ) { "$($stallSecondJ) s" } else { 'none' }))"
    }
} finally {
    foreach ($e in $events) { if ($e.Name -like '*release*') { $null = $e.Handle.Set() } }
    foreach ($p in $started) {
        try { $p.Kill($true) } catch { Write-Host "note: pid $($p.Id): $($_.Exception.Message)" }
        if (-not $p.WaitForExit($hangGuardMs)) { Write-Host "hung: pid $($p.Id) did not exit after its kill" }
        $p.Dispose()
    }
    foreach ($e in $events) { $e.Handle.Dispose() }
    Remove-Item -Recurse -Force $folder
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    exit 1
}
Write-Host 'PASS: the watchdog calls stalled only a host blocked in its own wait'
exit 0
