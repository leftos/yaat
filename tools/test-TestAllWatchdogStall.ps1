#!/usr/bin/env pwsh
# Case table for tools/TestAllWatchdog.ps1's stall verdict: a host is called stalled only when it is
# blocked in its own wait. Case A waits on a child that keeps working, with a process named outside
# the walk's pattern between the two; case A2 waits on a child that burns CPU without adding a
# process; case B has its threads held by the machine; case C, the control, is blocked in its own
# wait with no child and must still read STALLED; case D sleeps in Thread.Sleep, which is the host's
# own timer, not the machine holding it, and must read STALLED too; case E drives the idle clock by
# hand to show a poll with held threads pauses it rather than resetting it. Every wait is on a named
# event or a process handle with a hang guard, and no case reads the wall clock. Test-StalledHost is
# called with IdleSeconds 0 except in case E, so each verdict rests only on the progress it saw
# between two walks.
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
    return Test-StalledHost -State $state -Hosts $hosts -IdleSeconds 0 -CpuToleranceSeconds 0.5
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
while (`$proc.TotalProcessorTime.TotalSeconds -lt 1) { `$proc.Refresh() }
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
    $null = [WatchdogCase1767.Nt]::NtSuspendProcess($hostB.Handle)
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
