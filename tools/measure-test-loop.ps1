#!/usr/bin/env pwsh
# Measures the developer test loop so a framework or harness change can be judged on numbers
# instead of impressions. Produces the same metrics on xunit.v3 and on TUnit, so a migration
# branch can be diffed against main.
#
# Usage:
#   pwsh tools/measure-test-loop.ps1 -Label main-xunit
#   pwsh tools/measure-test-loop.ps1 -Label wt-tunit -Runs 3
#   pwsh tools/measure-test-loop.ps1 -Label quick -Only discovery,one-class
#   pwsh tools/measure-test-loop.ps1 -Label full -Skip cold-build
#
# Metrics (all Release by default; -Runs N repetitions, median reported):
#   full-gate         tools/test-all.ps1 wall time (both repos)
#   sim-run           Yaat.Sim.Tests executable, whole project: wall + process CPU + CPU/wall ratio
#   one-class         a single mid-size test class, filtered
#   discovery         --list-tests only (no execution)
#   incr-build-test   touch one TEST file, rebuild the test project  <-- the source-gen metric
#   incr-build-sim    touch one src/Yaat.Sim file, rebuild the test project
#   cold-build        delete the test project's obj/bin, rebuild
#
# incr-build-test is the metric that matters most when judging a source-generated test framework:
# it is the cost paid on every edit-run cycle, and it is where generator work lands. Read it
# before reading run times.

param(
    [Parameter(Mandatory = $true)]
    [string]$Label,

    [ValidateSet('Debug', 'Release')]
    [string]$Config = 'Release',

    [string]$YaatDir,
    [string]$ServerDir,

    [int]$Runs = 3,

    # Metric selection. -Only wins over -Skip when both are given.
    [string[]]$Only = @(),
    [string[]]$Skip = @(),

    # The class exercised by the one-class metric. Mid-size (30 tests), real sim, no Avalonia,
    # no heavy replay - representative of a "run the subsystem I am working on" iteration.
    [string]$SampleClass = 'CommandQueueTests',

    [string]$OutFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- paths

if (-not $YaatDir) { $YaatDir = $env:YAAT_DIR }
if (-not $YaatDir) { $YaatDir = Join-Path $PSScriptRoot '..' }
if (-not $ServerDir) { $ServerDir = $env:YAAT_SERVER_DIR }
if (-not $ServerDir) { $ServerDir = Join-Path $PSScriptRoot '..\..\yaat-server' }

if (-not (Test-Path $YaatDir)) {
    Write-Host "Yaat directory not found: $YaatDir" -ForegroundColor Red
    exit 1
}
$yaatDir = (Resolve-Path $YaatDir).Path
$serverDir = if (Test-Path $ServerDir) { (Resolve-Path $ServerDir).Path } else { $null }

$simTestProj = Join-Path $yaatDir 'tests\Yaat.Sim.Tests\Yaat.Sim.Tests.csproj'
$simTestDir = Split-Path $simTestProj -Parent
$simTestExe = Join-Path $simTestDir "bin\$Config\net10.0\Yaat.Sim.Tests.exe"

if (-not (Test-Path $simTestProj)) {
    Write-Host "Sim test project not found: $simTestProj" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------- framework detection
#
# The filter CLI is framework-specific: xunit.v3's MTP extension exposes --filter-class, TUnit
# exposes --treenode-filter. Detect from the csproj so one script produces comparable numbers on
# both branches. A wrong guess here silently measures "zero tests ran", so the run is verified
# against an expected non-zero count later.

$projText = Get-Content $simTestProj -Raw
if ($projText -match 'Include="TUnit"') {
    $framework = 'tunit'
    $classFilterArgs = @('--treenode-filter', "/*/*/$SampleClass/*")
    # Verified against a known-cardinality probe: alternation only binds inside ONE path segment,
    # and untagged tests DO survive '!=' (matching xunit's --filter-not-trait). A top-level '|'
    # between whole paths silently selects the wrong set - see docs/plans/tunit-migration.md.
    $devFilterArgs = @('--treenode-filter', '/*/*/*/*[(Category!=Nightly)&(Category!=PathfinderGrid)]')
} elseif ($projText -match 'Include="xunit\.v3"') {
    $framework = 'xunit.v3'
    $classFilterArgs = @('--filter-class', "*.$SampleClass")
    $devFilterArgs = @('--filter-not-trait', 'Category=Nightly', '--filter-not-trait', 'Category=PathfinderGrid')
} else {
    Write-Host "Could not detect the test framework from $simTestProj" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------- measurement helpers

function Measure-Wall {
    <#  Runs a scriptblock, returns elapsed seconds. Throws nothing on command failure -
        the caller inspects $script:lastOk.  #>
    param([scriptblock]$Action)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & $Action
    $sw.Stop()
    return $sw.Elapsed.TotalSeconds
}

function Invoke-Measured {
    <#  Starts a process directly (not via `dotnet test`) so TotalProcessorTime covers the work
        we care about rather than an MSBuild parent. Returns wall seconds, CPU seconds and the
        captured stdout path.  #>
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$WorkingDirectory
    )

    $outFile = [System.IO.Path]::GetTempFileName()
    $errFile = [System.IO.Path]::GetTempFileName()

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    foreach ($a in $Arguments) { $null = $psi.ArgumentList.Add($a) }
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($psi)
    # Drain both pipes before waiting, or a full pipe buffer deadlocks the child.
    $stdoutTask = $p.StandardOutput.ReadToEndAsync()
    $stderrTask = $p.StandardError.ReadToEndAsync()
    $p.WaitForExit()
    $sw.Stop()

    [System.IO.File]::WriteAllText($outFile, $stdoutTask.Result)
    [System.IO.File]::WriteAllText($errFile, $stderrTask.Result)

    $cpu = $null
    try { $cpu = $p.TotalProcessorTime.TotalSeconds } catch { $cpu = $null }

    return [pscustomobject]@{
        Wall     = $sw.Elapsed.TotalSeconds
        Cpu      = $cpu
        ExitCode = $p.ExitCode
        OutFile  = $outFile
        ErrFile  = $errFile
    }
}

function Get-Median {
    param([double[]]$Values)
    # @() around the pipeline is load-bearing: Sort-Object emits a scalar for a single-element
    # input, and under Set-StrictMode -Version Latest indexing/.Count on that scalar throws.
    # -Runs 1 is the common smoke-test case, so this is the path most likely to be exercised.
    $vals = @($Values)
    if ($vals.Count -eq 0) { return $null }
    $sorted = @($vals | Sort-Object)
    return [double]$sorted[[int][math]::Floor($sorted.Count / 2)]
}

$results = [System.Collections.Generic.List[object]]::new()

function Add-Result {
    param(
        [string]$Metric,
        [double[]]$Walls,
        [double[]]$Cpus = @(),
        [string]$Note = ''
    )
    $results.Add([pscustomobject]@{
            Metric     = $Metric
            MedianWall = Get-Median $Walls
            AllWalls   = ($Walls | ForEach-Object { '{0:N1}' -f $_ }) -join ' / '
            MedianCpu  = if (@($Cpus).Count) { Get-Median $Cpus } else { $null }
            Note       = $Note
        })
    $m = Get-Median $Walls
    Write-Host ("  -> median {0:N1}s   [{1}]  {2}" -f $m, (($Walls | ForEach-Object { '{0:N1}' -f $_ }) -join ' / '), $Note) -ForegroundColor Green
}

$KnownMetrics = @('discovery', 'one-class', 'sim-run', 'sim-run-dev', 'incr-build-test', 'incr-build-sim', 'cold-build', 'full-gate')

# Called from bash/pwsh alike, `-Only a,b,c` arrives as ONE string when the caller is not
# PowerShell (the shell passes a single argv entry). Split defensively so both forms work;
# otherwise the filter silently matches nothing and the run reports success with an empty table.
function Expand-MetricList {
    param([string[]]$Raw)
    return @($Raw | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}
# @(...) on assignment is load-bearing: a function returning an empty pipeline yields $null,
# not @(), and @($null + $null) is a ONE-element array holding null - which then reads as a
# phantom metric named ''.
$Only = @(Expand-MetricList $Only)
$Skip = @(Expand-MetricList $Skip)

foreach ($m in (@($Only) + @($Skip) | Where-Object { $_ })) {
    if ($KnownMetrics -notcontains $m) {
        Write-Host "Unknown metric '$m'. Known: $($KnownMetrics -join ', ')" -ForegroundColor Red
        exit 1
    }
}

function Should-Run {
    param([string]$Metric)
    if (@($Only).Count) { return $Only -contains $Metric }
    return -not ($Skip -contains $Metric)
}

# ---------------------------------------------------------------- preamble

Write-Host ''
Write-Host "Label:      $Label" -ForegroundColor Yellow
Write-Host "Repo:       $yaatDir" -ForegroundColor Yellow
Write-Host "Framework:  $framework (detected)" -ForegroundColor Yellow
Write-Host "Config:     $Config   Runs: $Runs   SampleClass: $SampleClass" -ForegroundColor Yellow
Write-Host ''
Write-Host 'Timings are wall-clock on a shared machine. Close other builds and test runs first;' -ForegroundColor DarkYellow
Write-Host 'a concurrent Claude session or IDE build makes every number below meaningless.' -ForegroundColor DarkYellow
Write-Host ''

# A build must exist before any run metric. Do it once, untimed, so the run metrics are not
# measuring a build.
#
# Build the TEST PROJECT, not yaat.slnx. Every metric except full-gate concerns Yaat.Sim.Tests,
# which does not reference yaat-server; the solution does (tools/Yaat.GuideCapture -> ServerApp),
# so a solution build couples these measurements to the state of a sibling checkout that has
# nothing to do with them. In a worktree whose yaat-server is on a different branch that coupling
# is a hard build failure and no metric can be collected at all.
Write-Host '=== priming build (untimed) ===' -ForegroundColor Cyan
Push-Location $yaatDir
try {
    & dotnet build $simTestProj -c $Config -p:TreatWarningsAsErrors=true | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Priming build failed - cannot measure.' -ForegroundColor Red
        exit 1
    }
} finally { Pop-Location }

if (-not (Test-Path $simTestExe)) {
    Write-Host "Test executable not found after build: $simTestExe" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------- metrics

if (Should-Run 'discovery') {
    Write-Host "`n=== discovery (--list-tests) ===" -ForegroundColor Cyan
    $walls = @(); $count = 0
    for ($i = 0; $i -lt $Runs; $i++) {
        $r = Invoke-Measured -FilePath $simTestExe -Arguments @('--list-tests') -WorkingDirectory $simTestDir
        $walls += $r.Wall
        $count = (Get-Content $r.OutFile | Measure-Object -Line).Lines
        Remove-Item $r.OutFile, $r.ErrFile -ErrorAction SilentlyContinue
    }
    Add-Result -Metric 'discovery' -Walls $walls -Note "$count output lines"
}

if (Should-Run 'one-class') {
    Write-Host "`n=== one-class ($SampleClass) ===" -ForegroundColor Cyan
    $walls = @(); $cpus = @(); $note = ''
    for ($i = 0; $i -lt $Runs; $i++) {
        $r = Invoke-Measured -FilePath $simTestExe -Arguments $classFilterArgs -WorkingDirectory $simTestDir
        $walls += $r.Wall
        if ($null -ne $r.Cpu) { $cpus += $r.Cpu }
        $tail = (Get-Content $r.OutFile -Tail 6) -join ' '
        # A filter that matches nothing exits 0 on some runners. Surface the summary line so a
        # silently-empty run cannot be read as a fast run.
        $note = if ($tail -match '(?i)zero tests|no tests') { 'WARNING: filter matched no tests' } else { 'ok' }
        Remove-Item $r.OutFile, $r.ErrFile -ErrorAction SilentlyContinue
    }
    Add-Result -Metric 'one-class' -Walls $walls -Cpus $cpus -Note $note
}

function Measure-SimRun {
    <#  One whole-project run. $Args is the filter (empty = everything).
        Reports CPU/wall, which is the core-utilisation figure: on 16 cores, 16x is saturated.  #>
    param([string]$MetricName, [string[]]$FilterArgs)

    $walls = @(); $cpus = @(); $note = ''; $total = ''
    for ($i = 0; $i -lt $Runs; $i++) {
        $r = Invoke-Measured -FilePath $simTestExe -Arguments $FilterArgs -WorkingDirectory $simTestDir
        $walls += $r.Wall
        if ($null -ne $r.Cpu) { $cpus += $r.Cpu }
        # Record the test count: a filter that silently selects the wrong set is the failure mode
        # this whole comparison is most exposed to, and a smaller set just looks like a speed-up.
        $summary = @(Get-Content $r.OutFile | Where-Object { $_ -match '^\s*(total|failed):' })
        $total = ($summary -join ' ').Trim()
        $note = "exit $($r.ExitCode)"
        Remove-Item $r.OutFile, $r.ErrFile -ErrorAction SilentlyContinue
    }
    $mw = Get-Median $walls
    $mc = if (@($cpus).Count) { Get-Median $cpus } else { $null }
    if ($null -ne $mc -and $mw -gt 0) {
        $note += (', CPU/wall {0:N1}x of {1}' -f ($mc / $mw), [Environment]::ProcessorCount)
    }
    if ($total) { $note += ", $total" }
    Add-Result -Metric $MetricName -Walls $walls -Cpus $cpus -Note $note
}

if (Should-Run 'sim-run') {
    Write-Host "`n=== sim-run (whole Yaat.Sim.Tests, UNFILTERED) ===" -ForegroundColor Cyan
    Measure-SimRun -MetricName 'sim-run' -FilterArgs @()
}

# The unfiltered run includes the Nightly + PathfinderGrid sweeps: only 4 test methods, but each
# drives a huge internal sweep (PathfinderGrid is documented as a single ~55 s proof). One long
# serial test holds one core while the other 15 drain and idle, which drags CPU/wall down and makes
# the suite look like it has parallelism headroom it does not have. Neither developers nor per-PR
# CI run those, so this filtered variant - not sim-run - is the number that describes the real loop.
if (Should-Run 'sim-run-dev') {
    Write-Host "`n=== sim-run-dev (Nightly + PathfinderGrid excluded, as devs and PR-CI run it) ===" -ForegroundColor Cyan
    Measure-SimRun -MetricName 'sim-run-dev' -FilterArgs $devFilterArgs
}

# --- build metrics -------------------------------------------------
#
# Touching a TEST file re-runs the test assembly's source generator without rebuilding Yaat.Sim.
# That isolates generator cost, which is the thing a source-generated framework adds.

function Measure-IncrementalBuild {
    param([string]$TouchFile, [string]$MetricName)

    if (-not (Test-Path $TouchFile)) {
        Write-Host "  skip: touch target missing ($TouchFile)" -ForegroundColor DarkYellow
        return
    }
    $walls = @()
    for ($i = 0; $i -lt $Runs; $i++) {
        # Touch mtime only - never edit content, so the tree stays clean and the measurement
        # is repeatable.
        (Get-Item $TouchFile).LastWriteTime = Get-Date
        $w = Measure-Wall {
            Push-Location $yaatDir
            try { & dotnet build $simTestProj -c $Config | Out-Null } finally { Pop-Location }
        }
        $walls += $w
    }
    Add-Result -Metric $MetricName -Walls $walls -Note (Split-Path $TouchFile -Leaf)
}

if (Should-Run 'incr-build-test') {
    Write-Host "`n=== incr-build-test (touch a test file, rebuild) ===" -ForegroundColor Cyan
    Measure-IncrementalBuild -TouchFile (Join-Path $simTestDir "$SampleClass.cs") -MetricName 'incr-build-test'
}

if (Should-Run 'incr-build-sim') {
    Write-Host "`n=== incr-build-sim (touch a Yaat.Sim file, rebuild) ===" -ForegroundColor Cyan
    Measure-IncrementalBuild -TouchFile (Join-Path $yaatDir 'src\Yaat.Sim\SimulationWorld.cs') -MetricName 'incr-build-sim'
}

if (Should-Run 'cold-build') {
    Write-Host "`n=== cold-build (clear obj/bin for the test project) ===" -ForegroundColor Cyan
    $walls = @()
    for ($i = 0; $i -lt $Runs; $i++) {
        foreach ($d in @('obj', 'bin')) {
            $target = Join-Path $simTestDir $d
            if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        }
        $walls += Measure-Wall {
            Push-Location $yaatDir
            try { & dotnet build $simTestProj -c $Config | Out-Null } finally { Pop-Location }
        }
    }
    Add-Result -Metric 'cold-build' -Walls $walls -Note 'obj+bin removed each run'
}

if (Should-Run 'full-gate') {
    Write-Host "`n=== full-gate (tools/test-all.ps1) ===" -ForegroundColor Cyan
    if (-not $serverDir) {
        Write-Host '  skip: yaat-server not found; pass -ServerDir' -ForegroundColor DarkYellow
    } else {
        $walls = @()
        for ($i = 0; $i -lt $Runs; $i++) {
            $walls += Measure-Wall {
                & pwsh (Join-Path $PSScriptRoot 'test-all.ps1') -Config $Config -YaatDir $yaatDir -ServerDir $serverDir | Out-Null
            }
        }
        Add-Result -Metric 'full-gate' -Walls $walls -Note 'both repos, default filter'
    }
}

# ---------------------------------------------------------------- report

# An empty result set means every metric was filtered out or every one failed. Reporting an empty
# table as a successful run is how a measurement harness lies: the caller sees exit 0 and a
# well-formed (but contentless) table. Fail loudly instead.
if ($results.Count -eq 0) {
    Write-Host ''
    Write-Host 'No metrics ran - nothing was measured.' -ForegroundColor Red
    Write-Host ("  -Only = [{0}]  -Skip = [{1}]" -f ($Only -join ', '), ($Skip -join ', ')) -ForegroundColor Yellow
    exit 1
}

Write-Host "`n"
Write-Host "=== $Label ($framework, $Config, median of $Runs) ===" -ForegroundColor Cyan

$lines = @()
$lines += ''
$lines += "### $Label - $framework, $Config, median of $Runs runs"
$lines += ''
$lines += ('Machine: {0} logical processors. Recorded {1}.' -f [Environment]::ProcessorCount, (Get-Date -Format 'yyyy-MM-dd HH:mm'))
$lines += ''
$lines += '| Metric | Median wall (s) | Median CPU (s) | All runs | Note |'
$lines += '|---|---:|---:|---|---|'
foreach ($r in $results) {
    $cpuCell = if ($null -ne $r.MedianCpu) { '{0:N1}' -f $r.MedianCpu } else { '-' }
    $lines += ('| {0} | {1:N1} | {2} | {3} | {4} |' -f $r.Metric, $r.MedianWall, $cpuCell, $r.AllWalls, $r.Note)
}
$lines += ''

$lines | ForEach-Object { Write-Host $_ }

if ($OutFile) {
    $lines | Add-Content -Path $OutFile -Encoding utf8
    Write-Host "`nAppended to $OutFile" -ForegroundColor Green
}
