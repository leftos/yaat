#requires -Version 7
<#
.SYNOPSIS
Runs tools/gate.ps1 through the cases its watchdog, job, slots, priority and usage have to get right, and exits 1 when
any case fails.

.DESCRIPTION
The canonical copy is ~/.claude/tools/gate/gate.selftest.ps1: change it there and run sync-gate.ps1, which copies it
and gate.ps1 into every repo that carries them.

Each case starts the gate as a caller would, `pwsh -NoProfile -File tools/gate.ps1 ...`, with a log under
.tmp/gate-selftest, and prints `ok <case>` or `FAIL <case>: <why>`. The gate samples every second here
(GATE_SAMPLE_SECONDS=1) so the stall and ceiling cases end in seconds. Every case but the two slot cases runs with
GATE_SLOT_HELD=1, so the self-test never waits on, or holds up, the gates other sessions are running. The cases that
leave a process behind on purpose write its pid to a file, check it by pid and stop it by pid afterwards. Each gate
started as its own process gets 90 s of wall time, after which the case fails and the gate is killed with its tree. A
case whose verdict rests on timing and fails while its log says the machine was under 30% free prints
`skip <case>: machine busy (free <p>%)` instead of failing.

The backstop is reached only through GATE_TEST_SAMPLER_FAIL, the gate's one test hook: tripping it with a working
sampler takes a machine busy enough that five times the ceiling passes in wall time before the ceiling does on the
load-adjusted clock, which this script cannot arrange. The race between a command's own exit and a kill is not covered:
no case can make the command exit inside the few milliseconds between the sample and the kill every time.

A plain script rather than Pester: the Pester on this machine is the in-box 3.4.
#>
[CmdletBinding()]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '',
    Justification = 'Interactive dev script; one ok or FAIL line a case on the console is the UX.')]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root
$dir = '.tmp/gate-selftest'
New-Item -ItemType Directory -Force $dir | Out-Null
$gate = Join-Path $PSScriptRoot 'gate.ps1'
$script:failed = 0

function Write-Result {
    param([string]$Case, [string]$Why)
    if ($Why) {
        Write-Host "FAIL ${Case}: $Why" -ForegroundColor Red
        $script:failed++
        return
    }
    Write-Host "ok $Case" -ForegroundColor Green
}

# Kills the process that ran a gate for a case, with everything under it, by the pid it wrote.
function Stop-GateTree {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '',
        Justification = 'Stops only the job process this self-test started, by the pid it wrote.')]
    param([string]$PidFile)
    $id = Read-Pid $PidFile
    if (Test-Alive $id) { (Get-Process -Id $id).Kill($true) }
}

# Runs one gate to its end, or for 90 s of wall time at most, in a job whose process writes its pid so that a gate
# that never returns is killed with its tree; returns its status, how long it took, what it wrote to standard error and
# whether it ran out of time.
function Invoke-Gate {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseUsingScopeModifierInNewRunspaces', '',
        Justification = 'The job reads its values from its own param block and -ArgumentList, which $using: cannot be combined with.')]
    param([string]$Case, [string[]]$Arguments)
    $errFile = Join-Path $dir "$Case.stderr"
    $hostFile = Join-Path $dir "$Case.host.pid"
    Remove-Item $errFile, $hostFile -ErrorAction SilentlyContinue
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $job = Start-Job -WorkingDirectory $root -ScriptBlock {
        param($gate, $arguments, $errFile, $hostFile)
        Set-Content -Path $hostFile -Value $PID
        & pwsh -NoProfile -File $gate @arguments 2> $errFile | Out-Null
        $LASTEXITCODE
    } -ArgumentList $gate, $Arguments, $errFile, $hostFile
    $finished = [bool](Wait-Job -Job $job -Timeout 90)
    $status = if ($finished) { [int](Receive-Job -Job $job | Select-Object -Last 1) } else { -1 }
    if (-not $finished) { Stop-GateTree $hostFile }
    Remove-Job -Job $job -Force
    $clock.Stop()
    return [pscustomobject]@{
        Status   = $status
        Seconds  = $clock.Elapsed.TotalSeconds
        Err      = if (Test-Path $errFile) { [string](Get-Content -Path $errFile -Raw) } else { '' }
        TimedOut = -not $finished
    }
}

function Get-LogText {
    param([string]$Case)
    $log = Join-Path $dir "$Case.log"
    if (-not (Test-Path $log)) { return '' }
    return [string](Get-Content -Path $log -Raw)
}

# Why a finished run is wrong: its status is not the one expected, or its log lacks a line it must hold; $null when
# neither.
function Get-RunProblem {
    param([string]$Case, $Run, [int]$Expected, [string]$Holds)
    if ($Run.TimedOut) { return 'the gate did not return in 90 s' }
    if ($Run.Status -ne $Expected) { return "exit $($Run.Status), expected $Expected; see $dir/$Case.log" }
    if ($Holds -and (Get-LogText $Case) -notmatch $Holds) { return "the log has no '$Holds'; see $dir/$Case.log" }
    return $null
}

# A run with -Log, -TimeoutSeconds and -StallSeconds set and the command after --, checked for its status and, when
# given, a line its log must hold.
function Test-Run {
    [CmdletBinding(PositionalBinding = $false)]
    param([string]$Case, [int]$Timeout, [int]$Stall, [string]$Script, [int]$Expected, [string]$Holds)
    $arguments = @('-Log', "$dir/$Case.log", '-TimeoutSeconds', "$Timeout", '-StallSeconds', "$Stall", '--',
        'pwsh', '-NoProfile', '-c', $Script)
    $run = Invoke-Gate -Case $Case -Arguments $arguments
    $why = Get-RunProblem -Case $Case -Run $run -Expected $Expected -Holds $Holds
    return [pscustomobject]@{ Why = $why; Seconds = $run.Seconds }
}

# The pid a case's process wrote to its file, or 0 when it never did.
function Read-Pid {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return 0 }
    $text = (Get-Content -Path $Path -Raw)
    if (-not $text) { return 0 }
    return [int]$text.Trim()
}

function Test-Alive {
    param([int]$Id)
    return $Id -gt 0 -and [bool](Get-Process -Id $Id -ErrorAction SilentlyContinue)
}

# The share of the machine free that a case's log gives last, or -1 when it gives none.
function Get-LogFreeShare {
    param([string]$Case)
    $found = [regex]::Matches((Get-LogText $Case), 'machine free (\d+)% on average')
    if ($found.Count -eq 0) { return -1 }
    return [int]$found[$found.Count - 1].Groups[1].Value
}

# A case whose verdict rests on timing: one that failed while its log says the machine was under 30% free is reported
# as skipped, since other sessions' load rather than the gate made it late.
function Write-TimedResult {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '',
        Justification = 'Interactive dev script; one ok, skip or FAIL line a case on the console is the UX.')]
    param([string]$Case, [string]$LogCase, [string]$Why)
    $free = Get-LogFreeShare $LogCase
    if ($Why -and $free -ge 0 -and $free -lt 30) {
        Write-Host "skip ${Case}: machine busy (free $free%)" -ForegroundColor Yellow
        return
    }
    Write-Result $Case $Why
}

# Stops a process a case left running on purpose, by the pid it wrote.
function Stop-Leftover {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '',
        Justification = 'Stops only the process this self-test started, by the pid it wrote.')]
    param([int]$Id)
    if (Test-Alive $Id) { Stop-Process -Id $Id -Force -ErrorAction SilentlyContinue }
}

function Test-Stall {
    $run = Test-Run -Case 'stall' -Timeout 60 -Stall 3 -Script 'Start-Sleep 30' -Expected 124 -Holds 'gate: STALLED'
    $why = $run.Why
    if (-not $why -and $run.Seconds -ge 12) { $why = "took $([math]::Round($run.Seconds)) s, expected under 12 s" }
    if (-not $why -and (Get-LogText 'stall') -notmatch 'gate: STALLED: .*\(machine free \d+% on average\)') {
        $why = "the STALLED line gives no share of the machine free; see $dir/stall.log"
    }
    Write-TimedResult -Case 'silent sleep is killed as stalled' -LogCase 'stall' -Why $why
}

# A build server left running by an earlier build keeps gaining CPU; it must not keep a hung command alive. The stand-in
# is cmd.exe copied under the server's name and spinning through a loop that ends on its own after about 30 s, started
# before the gate.
function Test-OldServer {
    $server = Join-Path $dir 'VBCSCompiler.exe'
    Copy-Item -Path (Join-Path $env:SystemRoot 'System32\cmd.exe') -Destination $server -Force
    $spin = Start-Process -FilePath $server -ArgumentList '/d', '/c', '"for /l %i in (1,1,60000000) do @rem"' -PassThru -WindowStyle Hidden
    try {
        Start-Sleep -Seconds 1
        $run = Test-Run -Case 'old-server' -Timeout 60 -Stall 3 -Script 'Start-Sleep 30' -Expected 124 -Holds 'gate: STALLED'
    }
    finally {
        if (-not $spin.HasExited) { $spin.Kill() }
    }
    $why = $run.Why
    if (-not $why -and $run.Seconds -ge 12) { $why = "took $([math]::Round($run.Seconds)) s, expected under 12 s" }
    Write-TimedResult -Case 'a build server started before the gate does not count as progress' -LogCase 'old-server' -Why $why
}

function Test-OutputProgress {
    $script = '1..8 | ForEach-Object { [Console]::Out.WriteLine($_); Start-Sleep 1 }'
    $run = Test-Run -Case 'output' -Timeout 60 -Stall 3 -Script $script -Expected 0
    Write-Result 'output counts as progress' $run.Why
}

function Test-CpuProgress {
    $script = '$end = (Get-Date).AddSeconds(8); while ((Get-Date) -lt $end) { }'
    $run = Test-Run -Case 'cpu' -Timeout 60 -Stall 3 -Script $script -Expected 0
    Write-TimedResult -Case 'CPU time counts as progress' -LogCase 'cpu' -Why $run.Why
}

# The command starts a child that starts a silent, CPU-busy grandchild and exits, and the command then sleeps without a
# word: the grandchild, whose parent is gone, is still in the command's job. The grandchild writes its pid and ends on
# its own after 30 s, so a leak does not outlive the self-test by much.
function Invoke-OrphanRun {
    param([string]$Case, [int]$Timeout, [int]$Stall, [int]$Sleep)
    $grandchild = "$dir/orphan-grandchild.ps1"
    $child = "$dir/orphan-child.ps1"
    Set-Content -Path $grandchild -Value @(
        'param($PidFile)', 'Set-Content -Path $PidFile -Value $PID',
        '$end = (Get-Date).AddSeconds(30); while ((Get-Date) -lt $end) { }')
    Set-Content -Path $child -Value @(
        'param($PidFile)',
        "Start-Process -FilePath pwsh -ArgumentList '-NoProfile', '-File', '$grandchild', `$PidFile -WindowStyle Hidden")
    $pidFile = "$dir/$Case.pid"
    Remove-Item $pidFile -ErrorAction SilentlyContinue
    $script = "pwsh -NoProfile -File '$child' '$pidFile'; Start-Sleep $Sleep"
    $arguments = @('-Log', "$dir/$Case.log", '-TimeoutSeconds', "$Timeout", '-StallSeconds', "$Stall", '--',
        'pwsh', '-NoProfile', '-c', $script)
    $run = Invoke-Gate -Case $Case -Arguments $arguments
    return [pscustomobject]@{ Run = $run; Grandchild = (Read-Pid $pidFile) }
}

function Test-Orphan {
    $result = Invoke-OrphanRun -Case 'orphan-progress' -Timeout 60 -Stall 3 -Sleep 8
    Stop-Leftover $result.Grandchild
    $why = Get-RunProblem -Case 'orphan-progress' -Run $result.Run -Expected 0
    if (-not $why -and $result.Grandchild -eq 0) { $why = 'the grandchild never wrote its pid' }
    Write-Result 'a grandchild whose parent exited still counts as progress' $why

    $result = Invoke-OrphanRun -Case 'orphan-kill' -Timeout 3 -Stall 30 -Sleep 30
    $alive = Test-Alive $result.Grandchild
    Stop-Leftover $result.Grandchild
    $why = Get-RunProblem -Case 'orphan-kill' -Run $result.Run -Expected 124 -Holds "gate: terminated the job's \d+ processes"
    if (-not $why -and $result.Grandchild -eq 0) { $why = 'the grandchild never wrote its pid before the kill' }
    elseif (-not $why -and $alive) { $why = "the grandchild $($result.Grandchild) outlived the kill" }
    Write-Result 'a kill reaches a grandchild whose parent exited' $why
}

# On a machine other sessions keep busy, the load-adjusted clock runs slow enough that the wall-time backstop can come
# first; that is the gate working as meant, so it is reported as skipped rather than failed.
function Test-Ceiling {
    $case = 'a busy loop reaches the load-adjusted ceiling'
    $script = '$end = (Get-Date).AddSeconds(60); while ((Get-Date) -lt $end) { }'
    $run = Test-Run -Case 'ceiling' -Timeout 3 -Stall 30 -Script $script -Expected 124 -Holds 'gate: TIMED OUT'
    Write-TimedResult -Case $case -LogCase 'ceiling' -Why $run.Why
}

# With every sample failing, the gate still lets a command finish, and still kills a hung one at the backstop.
function Test-SamplerFailure {
    $env:GATE_TEST_SAMPLER_FAIL = '1'
    try {
        $finished = Test-Run -Case 'sampler-finish' -Timeout 30 -Stall 1 -Script 'Start-Sleep 3' -Expected 0 -Holds 'gate: sampler failed: '
        $killed = Test-Run -Case 'sampler-kill' -Timeout 2 -Stall 1 -Script 'Start-Sleep 30' -Expected 124 `
            -Holds 'gate: BACKSTOP.*machine load unknown'
    }
    finally {
        $env:GATE_TEST_SAMPLER_FAIL = $null
    }
    Write-Result 'a failing sampler leaves a command to finish' $finished.Why
    $why = $killed.Why
    if (-not $why -and $killed.Seconds -ge 20) { $why = "took $([math]::Round($killed.Seconds)) s, expected under 20 s" }
    Write-TimedResult -Case 'a failing sampler still kills a hung command at the backstop' -LogCase 'sampler-kill' -Why $why
}

function Test-Status {
    $run = Test-Run -Case 'status' -Timeout 30 -Stall 30 -Script 'exit 3' -Expected 3
    Write-Result 'the command''s own status is kept' $run.Why
    $run = Test-Run -Case 'marker' -Timeout 30 -Stall 30 -Script '[Console]::Out.WriteLine(''Build FAILED.'')' -Expected 1
    Write-Result 'a green exit with a failure marker is 1' $run.Why
    $arguments = @('-Log', "$dir/no-markers.log", '-TimeoutSeconds', '30', '-NoMarkers', '--',
        'pwsh', '-NoProfile', '-c', '[Console]::Out.WriteLine(''Build FAILED.'')')
    $run = Invoke-Gate -Case 'no-markers' -Arguments $arguments
    Write-Result 'with -NoMarkers a green exit is 0 whatever the log says' (Get-RunProblem -Case 'no-markers' -Run $run -Expected 0)
}

# Arguments reach the command as they were given: one holding quotes and a space, one ending in a backslash, and one
# with a space that ends in a backslash.
function Test-Argument {
    $echo = "$dir/echo-args.ps1"
    Set-Content -Path $echo -Value '$args | ForEach-Object { [Console]::Out.WriteLine("[$_]") }'
    $given = @('say "hi" twice', 'C:\dir\', 'C:\a b\')
    $arguments = @('-Log', "$dir/arguments.log", '-TimeoutSeconds', '30', '--', 'pwsh', '-NoProfile', '-File', $echo) + $given
    $run = Invoke-Gate -Case 'arguments' -Arguments $arguments
    $why = Get-RunProblem -Case 'arguments' -Run $run -Expected 0
    $lines = @((Get-LogText 'arguments') -split "`r?`n" | Where-Object { $_ -match '^\[' })
    $expected = @($given | ForEach-Object { "[$_]" })
    if (-not $why -and ($lines -join '|') -ne ($expected -join '|')) {
        $why = "the command got $($lines -join ' '), expected $($expected -join ' ')"
    }
    Write-Result 'quotes and trailing backslashes reach the command intact' $why
}

# Run from a normal-priority parent, so the child reads below-normal only when the gate lowered it.
function Test-Priority {
    $self = Get-Process -Id $PID
    $was = $self.PriorityClass
    $self.PriorityClass = 'Normal'
    try {
        $run = Test-Run -Case 'priority' -Timeout 30 -Stall 30 -Script '(Get-Process -Id $PID).PriorityClass' -Expected 0 `
            -Holds 'BelowNormal'
    }
    finally {
        $self.PriorityClass = $was
    }
    Write-Result 'the command runs below normal priority' $run.Why
}

# Run in this session with &, as a repo's entry-point script runs it, so the caller's own environment and priority can be checked after
# the gate.
function Test-BuildEnvironment {
    $env:MSBUILDDISABLENODEREUSE = 'caller'
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = $null
    $priority = (Get-Process -Id $PID).PriorityClass
    $script = '$env:MSBUILDDISABLENODEREUSE; $env:DOTNET_CLI_USE_MSBUILD_SERVER'
    & $gate -Log "$dir/build-env.log" -TimeoutSeconds 30 -- pwsh -NoProfile -c $script *> $null
    $status = $LASTEXITCODE
    $text = Get-LogText 'build-env'
    $after = (Get-Process -Id $PID).PriorityClass
    $why = $null
    if ($status -ne 0) { $why = "exit $status, expected 0; see $dir/build-env.log" }
    elseif ($text -notmatch '(?m)^1\r?$' -or $text -notmatch '(?m)^0\r?$') { $why = "the command did not see 1 and 0; see $dir/build-env.log" }
    elseif ($env:MSBUILDDISABLENODEREUSE -ne 'caller' -or $null -ne $env:DOTNET_CLI_USE_MSBUILD_SERVER) {
        $why = "the caller's environment was left changed: '$env:MSBUILDDISABLENODEREUSE', '$env:DOTNET_CLI_USE_MSBUILD_SERVER'"
    }
    elseif ($after -ne $priority) { $why = "the caller's priority was left at $after, was $priority" }
    Write-Result 'MSBuild starts its own nodes, and the caller''s environment and priority are restored' $why
}

# A caller in its own session that moved with Set-Location and gives a relative -Log: the gate must watch the file the
# command writes, so a command that prints slowly and uses little CPU is not killed as stalled.
function Test-RelativeLog {
    $sub = Join-Path $dir 'relative'
    New-Item -ItemType Directory -Force $sub | Out-Null
    Remove-Item (Join-Path $sub 'relative.log') -ErrorAction SilentlyContinue
    $script = '1..6 | ForEach-Object { [Console]::Out.WriteLine($_); Start-Sleep 1 }'
    Push-Location $sub
    try {
        & $gate -Log 'relative.log' -TimeoutSeconds 60 -StallSeconds 3 -- pwsh -NoProfile -c $script *> $null
        $status = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    $why = $null
    if ($status -ne 0) { $why = "exit $status, expected 0; see $sub/relative.log" }
    elseif (-not (Test-Path (Join-Path $sub 'relative.log'))) { $why = "no log at $sub/relative.log" }
    Write-Result 'a relative -Log after Set-Location is watched where the command writes it' $why
}

function Test-Usage {
    $cases = @(
        @{ Case = 'no-log'; Arguments = @('-TimeoutSeconds', '5', '--', 'pwsh', '-c', 'exit 0'); Names = 'missing -Log' },
        @{ Case = 'no-timeout'; Arguments = @('-Log', "$dir/no-timeout.log", '--', 'pwsh', '-c', 'exit 0')
            Names = 'missing -TimeoutSeconds' },
        @{ Case = 'bad-timeout'; Arguments = @('-Log', "$dir/bad-timeout.log", '-TimeoutSeconds', 'abc', '--', 'pwsh', '-c', 'exit 0')
            Names = "-TimeoutSeconds must be a whole number above 0, got 'abc'" },
        @{ Case = 'no-command'; Arguments = @('-Log', "$dir/no-command.log", '-TimeoutSeconds', '5', '--'); Names = 'missing the command' },
        @{ Case = 'no-value'; Arguments = @('-Log', "$dir/no-value.log", '-TimeoutSeconds'); Names = 'gate: -TimeoutSeconds needs a value' }
    )
    foreach ($case in $cases) {
        $run = Invoke-Gate -Case $case.Case -Arguments $case.Arguments
        $why = $null
        if ($run.TimedOut) { $why = 'the gate did not return in 90 s' }
        elseif ($run.Status -ne 2) { $why = "exit $($run.Status), expected 2" }
        elseif (-not $run.Err.Contains($case.Names)) { $why = "standard error does not say '$($case.Names)': $($run.Err)" }
        Write-Result "usage: $($case.Case)" $why
    }
}

# Starts one gate as a job, so two can run at once; the job returns the gate's status and standard error.
function Start-GateJob {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '',
        Justification = 'Starts a background job of this run''s own; there is no system state for a caller to confirm.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseUsingScopeModifierInNewRunspaces', '',
        Justification = 'The job reads its values from its own param block and -ArgumentList, which $using: cannot be combined with.')]
    param([string[]]$Arguments)
    return Start-Job -WorkingDirectory $root -ScriptBlock {
        param($gate, $arguments)
        $text = & pwsh -NoProfile -File $gate @arguments 2>&1 | Out-String
        [pscustomobject]@{ Status = $LASTEXITCODE; Text = $text }
    } -ArgumentList $gate, $Arguments
}

function Get-Stamp {
    param([string]$Case)
    return @((Get-LogText $Case) -split "`r?`n" | Where-Object { $_ -match '^\d{10,}$' } | ForEach-Object { [long]$_ })
}

function Test-SlotOverlap {
    param($First, $Second)
    $a = Get-Stamp 'slot-a'
    $b = Get-Stamp 'slot-b'
    if ($a.Count -eq 0 -or $b.Count -eq 0) { return 'a run printed no timestamps' }
    $apart = ($a[-1] -lt $b[0]) -or ($b[-1] -lt $a[0])
    if (-not $apart) { return 'the two runs overlapped' }
    if (-not ("$($First.Text)$($Second.Text)$(Get-LogText 'slot-a')$(Get-LogText 'slot-b')" -match 'waiting for a slot')) {
        return 'neither gate said it was waiting for a slot'
    }
    return $null
}

function Test-Slot {
    $script = '1..4 | ForEach-Object { [Console]::Out.WriteLine([DateTime]::UtcNow.Ticks); Start-Sleep 1 }'
    $jobs = foreach ($case in 'slot-a', 'slot-b') {
        Start-GateJob -Arguments @('-Log', "$dir/$case.log", '-TimeoutSeconds', '60', '--', 'pwsh', '-NoProfile', '-c', $script)
    }
    $null = Wait-Job -Job $jobs -Timeout 60
    $results = @($jobs | ForEach-Object { if ($_.State -eq 'Completed') { Receive-Job $_ } })
    $jobs | Remove-Job -Force
    $contention = 'likely because other sessions'' gates held slot 0 (GATE_SLOTS=1 makes it the only one) meanwhile'
    $why = if ($results.Count -ne 2) { "a gate did not finish within 60 s, $contention" }
    elseif ($results[0].Status -ne 0 -or $results[1].Status -ne 0) {
        "exits $($results[0].Status) and $($results[1].Status), expected 0 and 0"
    }
    else { Test-SlotOverlap $results[0] $results[1] }
    Write-Result 'one slot runs two gates one after the other' $why
}

function Test-Nesting {
    $inner = @('pwsh', '-NoProfile', '-File', $gate, '-Log', "$dir/nest-inner.log", '-TimeoutSeconds', '20', '--',
        'pwsh', '-NoProfile', '-c', 'exit 0')
    $run = Invoke-Gate -Case 'nest' -Arguments (@('-Log', "$dir/nest.log", '-TimeoutSeconds', '20', '--') + $inner)
    $why = Get-RunProblem -Case 'nest' -Run $run -Expected 0
    if (-not $why -and $run.Seconds -ge 20) { $why = "took $([math]::Round($run.Seconds)) s, expected under 20 s" }
    Write-Result 'a gate inside a gate does not wait on its parent''s slot' $why
}

# An outer gate whose command is an inner gate whose command sleeps without a word: the outer's kill must reach the
# sleeper through the inner gate's job, nested in its own.
function Test-NestedKill {
    $sleeper = "$dir/nest-sleeper.ps1"
    $pidFile = "$dir/nest-kill.pid"
    Set-Content -Path $sleeper -Value @('param($PidFile)', 'Set-Content -Path $PidFile -Value $PID', 'Start-Sleep 30')
    Remove-Item $pidFile -ErrorAction SilentlyContinue
    $inner = @('pwsh', '-NoProfile', '-File', $gate, '-Log', "$dir/nest-kill-inner.log", '-TimeoutSeconds', '60',
        '-StallSeconds', '60', '--', 'pwsh', '-NoProfile', '-File', $sleeper, $pidFile)
    $outer = @('-Log', "$dir/nest-kill.log", '-TimeoutSeconds', '8', '-StallSeconds', '3', '--') + $inner
    $run = Invoke-Gate -Case 'nest-kill' -Arguments $outer
    $sleeperId = Read-Pid $pidFile
    $alive = Test-Alive $sleeperId
    Stop-Leftover $sleeperId
    $why = Get-RunProblem -Case 'nest-kill' -Run $run -Expected 124 -Holds 'gate: (STALLED|TIMED OUT|BACKSTOP)'
    if (-not $why -and $sleeperId -eq 0) { $why = 'the sleeper never wrote its pid' }
    elseif (-not $why -and $alive) { $why = "the sleeper $sleeperId outlived the outer gate's kill" }
    Write-Result 'an outer gate''s kill reaches the command of a gate inside it' $why
}

# A command that exits at once can be gone before the gate puts it in its job; its own status must stand, ten runs
# over, with no failure of the gate's.
function Test-QuickExit {
    $failures = @()
    foreach ($index in 1..10) {
        $case = "quick-exit-$index"
        $run = Invoke-Gate -Case $case -Arguments @('-Log', "$dir/$case.log", '-TimeoutSeconds', '30', '--', 'cmd', '/c', 'exit 0')
        $why = Get-RunProblem -Case $case -Run $run -Expected 0
        if (-not $why -and (Get-LogText $case) -match 'gate: the gate failed') { $why = "the gate failed; see $dir/$case.log" }
        if ($why) { $failures += "run ${index}: $why" }
    }
    Write-Result 'a command that exits at once keeps its own status' ($failures -join '; ')
}

# A gate run in a job's own session and stopped with Stop-Job terminates its command's job on the way out.
function Test-StopJob {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseUsingScopeModifierInNewRunspaces', '',
        Justification = 'The job reads its values from its own param block and -ArgumentList, which $using: cannot be combined with.')]
    param()
    $sleeper = "$dir/stop-sleeper.ps1"
    $pidFile = "$dir/stop-job.pid"
    Set-Content -Path $sleeper -Value @('param($PidFile)', 'Set-Content -Path $PidFile -Value $PID', 'Start-Sleep 30')
    Remove-Item $pidFile -ErrorAction SilentlyContinue
    $job = Start-Job -WorkingDirectory $root -ScriptBlock {
        param($gate, $log, $sleeper, $pidFile)
        & $gate -Log $log -TimeoutSeconds 60 -StallSeconds 60 -- pwsh -NoProfile -File $sleeper $pidFile
    } -ArgumentList $gate, "$dir/stop-job.log", $sleeper, $pidFile
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    while ((Read-Pid $pidFile) -eq 0 -and $clock.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 200 }
    $sleeperId = Read-Pid $pidFile
    Stop-Job -Job $job
    Remove-Job -Job $job -Force
    $clock.Restart()
    while ((Test-Alive $sleeperId) -and $clock.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 200 }
    $alive = Test-Alive $sleeperId
    Stop-Leftover $sleeperId
    $why = if ($sleeperId -eq 0) { 'the sleeper never wrote its pid' }
    elseif ($alive) { "the sleeper $sleeperId outlived the Stop-Job by 10 s" }
    Write-Result 'a gate stopped with Stop-Job takes its command with it' $why
}

# Run in this session after a class named GateNative, as an older version of the gate defined it, is loaded here.
function Test-OldNativeType {
    if (-not ('GateNative' -as [type])) {
        Add-Type -TypeDefinition 'public static class GateNative { public static int Old() { return 1; } }'
    }
    & $gate -Log "$dir/old-native.log" -TimeoutSeconds 30 -- pwsh -NoProfile -c 'exit 0' *> $null
    $status = $LASTEXITCODE
    $why = if ($status -ne 0) { "exit $status, expected 0; see $dir/old-native.log" }
    Write-Result 'a session that loaded an older GateNative still runs the gate' $why
}

$saved = @{}
$names = 'GATE_SAMPLE_SECONDS', 'GATE_SLOT_HELD', 'GATE_SLOTS', 'GATE_TEST_SAMPLER_FAIL', 'MSBUILDDISABLENODEREUSE',
'DOTNET_CLI_USE_MSBUILD_SERVER'
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $env:GATE_SAMPLE_SECONDS = '1'
    $env:GATE_SLOT_HELD = '1'
    $env:GATE_TEST_SAMPLER_FAIL = $null
    Test-Stall
    Test-OldServer
    Test-OutputProgress
    Test-CpuProgress
    Test-Orphan
    Test-Ceiling
    Test-SamplerFailure
    Test-Status
    Test-Argument
    Test-Priority
    Test-BuildEnvironment
    Test-RelativeLog
    Test-Usage
    Test-NestedKill
    Test-QuickExit
    Test-StopJob
    Test-OldNativeType
    $env:GATE_SLOT_HELD = $null
    $env:GATE_SLOTS = '1'
    Test-Slot
    Test-Nesting
}
finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, ($saved[$name] ?? [NullString]::Value)) }
}

if ($script:failed -gt 0) {
    Write-Host "$($script:failed) case(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'All cases passed.' -ForegroundColor Green
exit 0
