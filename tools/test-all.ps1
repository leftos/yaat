#!/usr/bin/env pwsh
# Builds and runs the full test suites for both yaat and yaat-server repos.
# The two builds run in sequence; the two test suites then run in parallel, and each
# suite's output is printed whole, in a fixed order, once both have finished.
# Usage:
#   pwsh tools/test-all.ps1                                        # Release (default — ~30% faster on Sim)
#   pwsh tools/test-all.ps1 -Full                                  # Also run the heavy Nightly + PathfinderGrid sweeps
#   pwsh tools/test-all.ps1 -Config Debug                          # Debug (better stack traces for failures)
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

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Config = 'Release',

    [string]$YaatDir,
    [string]$ServerDir,

    [switch]$Full
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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

$failed = $false

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
# Output is captured per job and printed in a fixed order once both finish, rather than
# streamed. Interleaved output from two suites is unreadable exactly when it matters, and the
# whole run is short enough that live progress is worth less than a legible failure.
#
# Both suites share %LOCALAPPDATA%/yaat, deliberately. They only write to it when the navdata
# or CIFP cache is stale — once per AIRAC cycle — and the loser of that race fails to open the
# file, catches, and falls back to the bundled TestData copies. Giving each job its own
# YAAT_APPDATA_DIR would trade that rare, self-healing window for a cold cache on every run.
function Start-TestJob {
    param([string]$WorkDir, [string]$Command)
    Start-Job -ScriptBlock {
        param($workDir, $command)
        Set-Location $workDir
        $output = Invoke-Expression $command 2>&1 | Out-String -Stream
        [pscustomobject]@{
            Output   = $output
            ExitCode = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }
        }
    } -ArgumentList $WorkDir, $Command
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

# Exclude the heavy gated-by-intent categories unless -Full. `--filter-not-trait`
# only drops tests explicitly tagged Nightly or PathfinderGrid; untagged tests
# still run. Test options follow the `--` separator (Microsoft.Testing.Platform
# runner, selected by global.json); `dotnet test` runs the test assemblies
# concurrently under it.
$testFilter = if ($Full) { '' } else { '-- --filter-not-trait "Category=Nightly" --filter-not-trait "Category=PathfinderGrid"' }

Write-Host "Configuration: $Config" -ForegroundColor Yellow
if ($Full) {
    Write-Host 'Scope: FULL (incl. Nightly + PathfinderGrid sweeps)' -ForegroundColor Yellow
} else {
    Write-Host 'Scope: default (Nightly + PathfinderGrid excluded; pass -Full to include)' -ForegroundColor Yellow
}

# Point dotnet at the .slnx explicitly. .NET 10 SDK 10.0.300 will otherwise
# drop a transient .sln in the repo root, which then conflicts with the .slnx
# on the next invocation ("more than one project or solution file") — see
# /yaat.sln and /yaat-server.sln in .gitignore.
if (-not (Run-Step 'Build yaat' $yaatDir "dotnet build yaat.slnx -c $Config -p:TreatWarningsAsErrors=true")) {
    Stop-OnBuildFailure 'Build yaat'
}
if (-not (Run-Step 'Build yaat-server' $serverDir "dotnet build yaat-server.slnx -c $Config -p:TreatWarningsAsErrors=true")) {
    Stop-OnBuildFailure 'Build yaat-server'
}
Write-Host "`nRunning both test suites in parallel..." -ForegroundColor Cyan
$yaatTests = Start-TestJob $yaatDir "dotnet test yaat.slnx -c $Config --no-build $testFilter"
$serverTests = Start-TestJob $serverDir "dotnet test yaat-server.slnx -c $Config --no-build $testFilter"

$null = Wait-Job -Job $yaatTests, $serverTests

Complete-TestJob 'Test yaat' $yaatTests
Complete-TestJob 'Test yaat-server' $serverTests

Write-Host ''
if ($failed) {
    Write-Host 'One or more steps failed.' -ForegroundColor Red
    exit 1
} else {
    Write-Host 'All builds and tests passed.' -ForegroundColor Green
    exit 0
}
