#!/usr/bin/env pwsh
# Builds and runs the full test suites for both yaat and yaat-server repos.
# The two builds run in sequence; the two test suites then run in parallel, and each
# suite's output is printed whole, in a fixed order, once both have finished. While they
# run, each assembly's verdict and each failed test print as they happen, with a heartbeat
# every minute naming the assemblies still running.
# Usage:
#   pwsh tools/test-all.ps1                                        # Release (default — ~30% faster on Sim)
#   pwsh tools/test-all.ps1 -Full                                  # Also run the heavy Nightly + PathfinderGrid sweeps
#   pwsh tools/test-all.ps1 -Config Debug                          # Debug (better stack traces for failures)
#   pwsh tools/test-all.ps1 -BuildOnly                             # Build both repos, run no tests
#   pwsh tools/test-all.ps1 -NoBuild                               # Test the binaries the last build left (after -BuildOnly)
#   pwsh tools/test-all.ps1 -ServerDir X:\dev\yaat-server          # Override yaat-server checkout (worktree-friendly)
#   $env:YAAT_SERVER_DIR='X:\dev\yaat-server'; pwsh tools/test-all.ps1   # Same via env var
#
# By default the heavy, gated-by-intent test categories are EXCLUDED so the
# local run stays fast (matching per-PR CI): `Nightly` (per-spot taxi-coverage
# grid sweeps) and `PathfinderGrid` (the state-aware-pruning necessity oracle
# sweep — a single ~55 s proof). Pass -Full to run them too (CI/nightly do).
#
# Worktrees: this script defaults `-ServerDir` to a sibling `yaat-server` directory
# (the standard layout). When yaat is checked out in a worktree like
# `X:\dev\yaat.wt\bug-xxx\`, the sibling default doesn't exist. Pass the real
# yaat-server path with `-ServerDir`, or set `YAAT_SERVER_DIR` once for the shell.
# `-YaatDir` has the same shape if you ever need to point at a non-default yaat
# checkout (rarely needed since the script lives inside one).
#
# The yaat-server build and test steps are given this yaat checkout's Yaat.Sim
# (`-p:YaatSimProject=...`). Without it, yaat-server's Directory.Build.props picks the
# `..\yaat` beside the server checkout, so a worktree's run would test the server against
# another tree's Yaat.Sim.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '',
    Justification = 'This script reports progress on the console; Write-Host is the only stream that prints by default.')]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Config = 'Release',

    [string]$YaatDir,
    [string]$ServerDir,

    [switch]$Full,

    # Build both repos and stop: lets several commands that need the Release binaries share one build
    # instead of each building the same obj/ trees at once.
    [switch]$BuildOnly,

    # Skip the builds and test what the last build left. Only for a tree just built with -BuildOnly:
    # on any other tree the tests run against stale binaries.
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($BuildOnly -and $NoBuild) {
    Write-Host '-BuildOnly and -NoBuild cannot be combined: one builds without testing, the other tests without building.' -ForegroundColor Red
    exit 2
}

if (-not $YaatDir) {
    $YaatDir = $env:YAAT_DIR
}
if (-not $YaatDir) {
    $YaatDir = Join-Path $PSScriptRoot '..'
}

if (-not $ServerDir) {
    $ServerDir = $env:YAAT_SERVER_DIR
}
if (-not $ServerDir) {
    $ServerDir = Join-Path $PSScriptRoot '..\..\yaat-server'
}

if (-not (Test-Path $YaatDir)) {
    Write-Host "Yaat directory not found: $YaatDir" -ForegroundColor Red
    Write-Host "Pass -YaatDir <path> or set `$env:YAAT_DIR." -ForegroundColor Yellow
    exit 1
}
if (-not (Test-Path $ServerDir)) {
    Write-Host "yaat-server directory not found: $ServerDir" -ForegroundColor Red
    Write-Host "Pass -ServerDir <path> or set `$env:YAAT_SERVER_DIR (e.g. X:\dev\yaat-server)." -ForegroundColor Yellow
    exit 1
}

$yaatDir = (Resolve-Path $YaatDir).Path
$serverDir = (Resolve-Path $ServerDir).Path
$yaatSimProject = Join-Path $yaatDir 'src\Yaat.Sim\Yaat.Sim.csproj'
$serverSimArg = "`"-p:YaatSimProject=$yaatSimProject`""

$failed = $false

# The stall watchdog (tools/TestAllWatchdog.ps1). A test host that keeps its suite alive while it
# stops making progress is hung, not slow. The heartbeat keeps printing every 60 s, so the gate's own
# stall kill may never fire: without the watchdog the run goes on to its ceiling with nothing naming
# the host. 90 s of no CPU is under the gate's 120 s stall kill, so the watchdog fires first; the dump
# budget covers an attach that hangs before it is killed as well. The tree walk examines only the
# process names that can sit between this process and a host, because reading .Parent for every
# process on the machine costs about a second. `dotnet-dump` comes from the local tool manifest,
# restored before the suites start.
$watchdogRootPid = $PID
$watchdogHostNamePattern = '\.Tests$|^testhost'
$watchdogProcessNamePattern = '^pwsh$|^dotnet$|^testhost|\.Tests$'
$watchdogIdleSeconds = 90
$watchdogCpuToleranceSeconds = 0.5
$watchdogNoHostSeconds = 30
$watchdogPollSeconds = 5
$watchdogCollectTimeoutSeconds = 60
$watchdogAnalyzeTimeoutSeconds = 120
. (Join-Path $PSScriptRoot 'TestAllWatchdog.ps1')

function Run-Step {
    param([string]$Label, [string]$WorkDir, [string]$Command)
    Write-Host "`n=== $Label ===" -ForegroundColor Cyan
    Push-Location $WorkDir
    try {
        # Out-Host keeps the command's output on the console instead of in the function's
        # return stream, so callers get a clean $true/$false.
        Invoke-Expression $Command | Out-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Host "FAILED: $Label" -ForegroundColor Red
            $script:failed = $true
            return $false
        }
        Write-Host "OK: $Label" -ForegroundColor Green
        return $true
    } finally {
        Pop-Location
    }
}

# A failed build must end the run here. The test steps use --no-build, so continuing
# would run whatever binaries the last successful build left behind and print
# "Passed!" lines that say nothing about the tree under test.
function Stop-OnBuildFailure {
    param([string]$Label)
    Write-Host "`n$Label failed — tests skipped (they would run against stale binaries)." -ForegroundColor Red
    exit 1
}

# The two test suites run concurrently; the builds above do not, and must not. `yaat.slnx`
# already contains the server project, which is why building yaat-server afterwards costs a
# few seconds rather than a full compile — two MSBuild processes over the same obj/ trees
# would race for the same intermediates.
#
# Output is captured per job and printed in a fixed order once both finish, because interleaved
# output from two suites is unreadable exactly when it matters. Each line also goes to a log file
# under .tmp/ as it arrives: that log is what a killed run leaves behind — the last lines of each
# suite, and which assembly never printed its `passed` line.
#
# Both suites share %LOCALAPPDATA%/yaat, deliberately. They only write to it when the navdata
# or CIFP cache is stale — once per AIRAC cycle — and the loser of that race fails to open the
# file, catches, and falls back to the bundled TestData copies. Giving each job its own
# YAAT_APPDATA_DIR would trade that rare, self-healing window for a cold cache on every run.
function Start-TestJob {
    param([string]$WorkDir, [string]$Command, [string]$LogPath)
    # Empty the log before the job starts, so the progress reader never picks up a previous
    # run's lines in the moment before the job opens the file.
    [System.IO.File]::WriteAllBytes($LogPath, [byte[]]@())
    Start-Job -ScriptBlock {
        param($workDir, $command, $outputLogPath)
        Set-Location $workDir
        # UTF-8 without a BOM and LF endings, flushed per line: the log is the only evidence a
        # host killed mid-run leaves behind, so it cannot wait for a buffer to fill.
        $writer = [System.IO.StreamWriter]::new($outputLogPath, $false, [System.Text.UTF8Encoding]::new($false))
        $writer.NewLine = "`n"
        $writer.AutoFlush = $true
        try {
            $output = Invoke-Expression $command 2>&1 | Out-String -Stream | ForEach-Object {
                $writer.WriteLine($_)
                $_
            }
            [pscustomobject]@{
                Output   = $output
                ExitCode = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }
            }
        } finally {
            $writer.Dispose()
        }
    } -ArgumentList $WorkDir, $Command, $LogPath
}

# Returns the whole lines a suite's job has written to its log since the last call. A line the
# job is still writing stays for the next call.
function Read-NewLogLines {
    param([hashtable]$Suite)
    if (-not (Test-Path $Suite.LogPath)) {
        return @()
    }
    $stream = [System.IO.File]::Open($Suite.LogPath, 'Open', 'Read', 'ReadWrite')
    try {
        $count = [int]($stream.Length - $Suite.Offset)
        if ($count -le 0) {
            return @()
        }
        $bytes = [byte[]]::new($count)
        $null = $stream.Seek($Suite.Offset, 'Begin')
        $stream.ReadExactly($bytes, 0, $count)
    } finally {
        $stream.Dispose()
    }
    $end = [Array]::LastIndexOf($bytes, [byte]10)
    if ($end -lt 0) {
        return @()
    }
    $Suite.Offset += $end + 1
    return [System.Text.Encoding]::UTF8.GetString($bytes, 0, $end).Split("`n")
}

function Format-Elapsed {
    param([TimeSpan]$Elapsed)
    return '[{0:00}:{1:00}]' -f [int][Math]::Floor($Elapsed.TotalMinutes), $Elapsed.Seconds
}

# Prints the progress a log line carries: an assembly's verdict, or a failed test. An assembly's
# `Running tests from` line only marks it running, for the heartbeat.
function Write-ProgressLine {
    param([hashtable]$Suite, [string]$Line, [TimeSpan]$Elapsed)
    $stamp = Format-Elapsed $Elapsed
    if ($Line -match '^Running tests from (?<dll>.+?\.dll) \(') {
        $null = $Suite.Running.Add([System.IO.Path]::GetFileNameWithoutExtension($Matches.dll))
    } elseif ($Line -match '^(?<dll>\S.*?\.dll) \([^)]*\) (?<verdict>.+?) \((?<time>[^()]+)\)$') {
        $verdict = $Matches.verdict
        $time = $Matches.time
        $assembly = [System.IO.Path]::GetFileNameWithoutExtension($Matches.dll)
        $null = $Suite.Running.Remove($assembly)
        $color = if ($verdict -eq 'passed') { 'Green' } else { 'Red' }
        Write-Host "$stamp [$($Suite.Name)] $assembly $verdict ($time)" -ForegroundColor $color
    } elseif ($Line -match '^failed (?<test>.+) \((?<time>[^()]+)\)$') {
        Write-Host "$stamp [$($Suite.Name)] failed $($Matches.test) ($($Matches.time))" -ForegroundColor Red
    }
}

function Write-Heartbeat {
    param([hashtable[]]$Suites, [TimeSpan]$Elapsed)
    $parts = @(foreach ($suite in $Suites) {
            if ($suite.Running.Count -gt 0) {
                "$($suite.Name): $(@($suite.Running | Sort-Object) -join ', ')"
            }
        })
    $state = if ($parts.Count -gt 0) { $parts -join '; ' } else { 'no assembly has started yet' }
    Write-Host "$(Format-Elapsed $Elapsed) still running - $state" -ForegroundColor DarkGray
}

# Waits for every suite's job, following each log as it grows, and watches the test hosts for a stall.
# Returns Outcome 'finished' when every job has ended, or 'stalled' when the watchdog fired: it has
# already printed the evidence and killed the hosts, and carries its response for the caller's final
# message. The caller still prints what each job left behind before failing the run.
function Wait-TestJobs {
    param([hashtable[]]$Suites)
    $jobs = @($Suites | ForEach-Object { $_.Job })
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $nextHeartbeat = 60
    $nextIdleCheck = $watchdogPollSeconds
    $idleState = @{}
    $noHostsSince = $null
    $noHostsReported = $false
    $watchdogDisabled = $false
    while ($true) {
        $null = Wait-Job -Job $jobs -Timeout 2
        # The state is read before the logs: a job closes its log before it ends, so the pass
        # that sees every job finished also reads every line they wrote.
        $finished = @($jobs | Where-Object { $_.State -in 'Completed', 'Failed', 'Stopped' }).Count -eq $jobs.Count
        foreach ($suite in $Suites) {
            foreach ($line in (Read-NewLogLines $suite)) {
                Write-ProgressLine $suite $line $clock.Elapsed
            }
        }
        if ($finished) {
            return [pscustomobject]@{ Outcome = 'finished'; Stall = $null }
        }
        if ($clock.Elapsed.TotalSeconds -ge $nextHeartbeat) {
            Write-Heartbeat $Suites $clock.Elapsed
            $nextHeartbeat += 60
        }
        if ($watchdogDisabled -or $clock.Elapsed.TotalSeconds -lt $nextIdleCheck) {
            continue
        }
        $nextIdleCheck += $watchdogPollSeconds
        $hosts = @()
        try {
            $hosts = @(Find-TestHost -RootPid $watchdogRootPid -HostNamePattern $watchdogHostNamePattern -ProcessNamePattern $watchdogProcessNamePattern)
        } catch {
            # A walk that cannot find its root is an error, not an empty tree: say so once instead of
            # letting the run look watched.
            Write-Host "test-all: watchdog could not walk the process tree under pid $watchdogRootPid ($($_.Exception.Message)); stall detection is off" -ForegroundColor Red
            $watchdogDisabled = $true
            continue
        }
        if ($hosts.Count -eq 0) {
            if ($null -eq $noHostsSince) {
                $noHostsSince = $clock.Elapsed
            }
            $anyAssemblyRunning = @($Suites | Where-Object { $_.Running.Count -gt 0 }).Count -gt 0
            if (-not $noHostsReported -and $anyAssemblyRunning -and (($clock.Elapsed - $noHostsSince).TotalSeconds -ge $watchdogNoHostSeconds)) {
                Write-Host "test-all: watchdog sees no test host under pid $watchdogRootPid; stall detection is off" -ForegroundColor Red
                $noHostsReported = $true
            }
        } else {
            $noHostsSince = $null
        }
        $stalled = Test-StalledHost -State $idleState -Hosts $hosts -IdleSeconds $watchdogIdleSeconds -CpuToleranceSeconds $watchdogCpuToleranceSeconds
        if ($null -ne $stalled) {
            $response = @{
                StalledHost           = $stalled
                RootPid               = $watchdogRootPid
                HostNamePattern       = $watchdogHostNamePattern
                ProcessNamePattern    = $watchdogProcessNamePattern
                Suites                = $Suites
                CheckoutRoot          = $yaatDir
                IdleSeconds           = $watchdogIdleSeconds
                CollectTimeoutSeconds = $watchdogCollectTimeoutSeconds
                AnalyzeTimeoutSeconds = $watchdogAnalyzeTimeoutSeconds
            }
            return [pscustomobject]@{ Outcome = 'stalled'; Stall = (Invoke-StallResponse @response) }
        }
    }
}

function New-Suite {
    param([string]$Name, $Job, [string]$LogPath)
    return @{
        Name    = $Name
        Job     = $Job
        LogPath = $LogPath
        Offset  = 0L
        Running = [System.Collections.Generic.HashSet[string]]::new()
    }
}

# Prints one finished job's captured output and folds its result into $script:failed.
function Complete-TestJob {
    param([string]$Label, $Job)
    Write-Host "`n=== $Label ===" -ForegroundColor Cyan

    $result = $null
    try {
        $result = Receive-Job -Job $Job -ErrorAction Stop
    } catch {
        Write-Host $_ -ForegroundColor Red
    } finally {
        Remove-Job -Job $Job -Force
    }

    # A job that died without returning its result object is a failure however it reads:
    # treat a missing verdict as one rather than letting the run go green on no evidence.
    if ($null -eq $result) {
        Write-Host "FAILED: $Label (the test job produced no result)" -ForegroundColor Red
        $script:failed = $true
        return
    }

    $result.Output | Out-Host
    if ($result.ExitCode -ne 0) {
        Write-Host "FAILED: $Label" -ForegroundColor Red
        $script:failed = $true
        return
    }

    Write-Host "OK: $Label" -ForegroundColor Green
}

# A crashing test host leaves its Mini dump and its JSON crash report in the folder the macOS
# block below names. Printing them here carries the report into the routed run's log, and copying
# both into the checkout's .tmp/dumps lets the gate's failureOutputs glob bring them back to the PC.
function Write-CrashEvidence {
    param(
        [Parameter(Mandatory)][string]$Folder,
        [Parameter(Mandatory)][datetime]$Since,
        [Parameter(Mandatory)][string]$CopyTo
    )
    if (-not (Test-Path $Folder)) {
        return
    }
    $evidence = @(Get-ChildItem -Path $Folder -File | Where-Object { $_.LastWriteTime -ge $Since })
    if ($evidence.Count -gt 0) {
        $null = New-Item -ItemType Directory -Path $CopyTo -Force
    }
    foreach ($dump in @($evidence | Where-Object { $_.Name -like '*.dmp' })) {
        Write-Host "Crash dump: $($dump.FullName)" -ForegroundColor Red
    }
    foreach ($report in @($evidence | Where-Object { $_.Name -like '*.crashreport.json' })) {
        Write-Host "Crash report: $($report.FullName)" -ForegroundColor Red
        Get-Content -Raw $report.FullName | Out-Host
    }
    foreach ($file in $evidence) {
        $copy = Join-Path $CopyTo $file.Name
        Copy-Item -LiteralPath $file.FullName -Destination $copy -Force
        Write-Host "Copied for the gate: $copy" -ForegroundColor Red
    }
}

# Exclude the heavy gated-by-intent categories unless -Full. `--filter-not-trait`
# only drops tests explicitly tagged Nightly, PathfinderGrid or Desktop; untagged
# tests still run. Desktop is the interactive-desktop suite, which starts real
# clients on the machine it runs on. Test options follow the `--` separator
# (Microsoft.Testing.Platform runner, selected by global.json); `dotnet test`
# runs the test assemblies concurrently under it.
$testFilter = if ($Full) {
    ''
} else {
    '-- --filter-not-trait "Category=Nightly" --filter-not-trait "Category=PathfinderGrid" ' +
    '--filter-not-trait "Category=Desktop"'
}

Write-Host "Configuration: $Config" -ForegroundColor Yellow
if ($Full) {
    Write-Host 'Scope: FULL (incl. Nightly + PathfinderGrid + Desktop)' -ForegroundColor Yellow
} else {
    Write-Host 'Scope: default (Nightly + PathfinderGrid + Desktop excluded; pass -Full to include)' -ForegroundColor Yellow
}

# Point dotnet at the .slnx explicitly. .NET 10 SDK 10.0.300 will otherwise
# drop a transient .sln in the repo root, which then conflicts with the .slnx
# on the next invocation ("more than one project or solution file") — see
# /yaat.sln and /yaat-server.sln in .gitignore.
if ($NoBuild) {
    Write-Host "`nSkipping the builds (-NoBuild): testing the binaries the last $Config build left." -ForegroundColor Yellow
} else {
    if (-not (Run-Step 'Build yaat' $yaatDir "dotnet build yaat.slnx -c $Config -p:TreatWarningsAsErrors=true")) {
        Stop-OnBuildFailure 'Build yaat'
    }
    if (-not (Run-Step 'Build yaat-server' $serverDir "dotnet build yaat-server.slnx -c $Config -p:TreatWarningsAsErrors=true $serverSimArg")) {
        Stop-OnBuildFailure 'Build yaat-server'
    }
}
if ($BuildOnly) {
    Write-Host "`nBoth builds passed; no tests run (-BuildOnly)." -ForegroundColor Green
    exit 0
}

# A rare test-host crash on the Mac builder (signal 11, once in about 590 routed runs) otherwise
# leaves no evidence: a routed run returns only its log, never a dump. These runtime variables
# make a crashing test host write a Mini dump and a JSON crash report to a folder outside the work
# tree the builder prunes; Write-CrashEvidence prints both paths and the report into this log and
# copies both into .tmp/dumps, which a failed routed run returns to the PC.
$crashDumpDir = $null
$crashScanStart = $null
if ($IsMacOS) {
    $crashDumpDir = Join-Path $HOME 'yaat-crashdumps'
    $null = New-Item -ItemType Directory -Path $crashDumpDir -Force
    $env:DOTNET_DbgEnableMiniDump = '1'
    $env:DOTNET_DbgMiniDumpType = '1'
    $env:DOTNET_DbgMiniDumpName = (Join-Path $crashDumpDir '%e-%p-%t.dmp')
    $env:DOTNET_EnableCrashReport = '1'
    $crashScanStart = Get-Date
    Write-Host "Crash dumps (macOS): $crashDumpDir" -ForegroundColor Yellow
}

# The watchdog dumps a stalled host with `dotnet-dump` from the local tool manifest, so the manifest
# must be restored before the suites start. A restore that fails (offline with a cold cache) is not a
# reason to skip the tests: the watchdog still reports and kills, only without a dump.
Write-Host "`nRestoring the local dotnet tools (the watchdog dumps a stalled host with dotnet-dump)..." -ForegroundColor Cyan
Push-Location $yaatDir
try {
    dotnet tool restore | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'dotnet tool restore failed - a stalled host is reported and killed without a dump.' -ForegroundColor Yellow
    }
} finally {
    Pop-Location
}

Write-Host "`nRunning both test suites in parallel..." -ForegroundColor Cyan
$yaatLogPath = Join-Path $yaatDir '.tmp\test-all-yaat.log'
$serverLogPath = Join-Path $yaatDir '.tmp\test-all-server.log'
$null = New-Item -ItemType Directory -Path (Join-Path $yaatDir '.tmp') -Force
$yaatTests = Start-TestJob $yaatDir "dotnet test yaat.slnx -c $Config --no-build $testFilter" -LogPath $yaatLogPath
$serverTests = Start-TestJob $serverDir "dotnet test yaat-server.slnx -c $Config --no-build $serverSimArg $testFilter" -LogPath $serverLogPath

$testOutcome = Wait-TestJobs @((New-Suite 'yaat' $yaatTests $yaatLogPath), (New-Suite 'server' $serverTests $serverLogPath))

# A stalled run still prints what each killed job left behind, then fails.
Complete-TestJob 'Test yaat' $yaatTests
Complete-TestJob 'Test yaat-server' $serverTests

if ($IsMacOS) {
    Write-CrashEvidence -Folder $crashDumpDir -Since $crashScanStart -CopyTo (Join-Path $yaatDir '.tmp/dumps')
}

Write-Host ''
if ($testOutcome.Outcome -eq 'stalled') {
    if (@($testOutcome.Stall.SuitesFinishedOnTheirOwn).Count -gt 0) {
        Write-Host "The $($testOutcome.Stall.SuitesFinishedOnTheirOwn -join ' and ') suite job finished on its own before the kill." -ForegroundColor Red
    }
    Write-Host 'A test host stalled: the evidence above names the hang. The run failed.' -ForegroundColor Red
    exit 1
} elseif ($failed) {
    Write-Host 'One or more steps failed.' -ForegroundColor Red
    exit 1
} else {
    $summary = if ($NoBuild) { 'All tests passed (builds skipped: -NoBuild).' } else { 'All builds and tests passed.' }
    Write-Host $summary -ForegroundColor Green
    exit 0
}
