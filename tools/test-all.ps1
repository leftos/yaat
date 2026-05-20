#!/usr/bin/env pwsh
# Builds and runs the full test suites for both yaat and yaat-server repos.
# Usage:
#   pwsh tools/test-all.ps1                                        # Release (default — ~30% faster on Sim)
#   pwsh tools/test-all.ps1 -Config Debug                          # Debug (better stack traces for failures)
#   pwsh tools/test-all.ps1 -ServerDir X:\dev\yaat-server          # Override yaat-server checkout (worktree-friendly)
#   $env:YAAT_SERVER_DIR='X:\dev\yaat-server'; pwsh tools/test-all.ps1   # Same via env var
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
    [string]$ServerDir
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
        Invoke-Expression $Command
        if ($LASTEXITCODE -ne 0) {
            Write-Host "FAILED: $Label" -ForegroundColor Red
            $script:failed = $true
        } else {
            Write-Host "OK: $Label" -ForegroundColor Green
        }
    } finally {
        Pop-Location
    }
}

Write-Host "Configuration: $Config" -ForegroundColor Yellow

# Point dotnet at the .slnx explicitly. .NET 10 SDK 10.0.300 will otherwise
# drop a transient .sln in the repo root, which then conflicts with the .slnx
# on the next invocation ("more than one project or solution file") — see
# /yaat.sln and /yaat-server.sln in .gitignore.
Run-Step 'Build yaat' $yaatDir "dotnet build yaat.slnx -c $Config -p:TreatWarningsAsErrors=true"
Run-Step 'Build yaat-server' $serverDir "dotnet build yaat-server.slnx -c $Config -p:TreatWarningsAsErrors=true"
Run-Step 'Test yaat' $yaatDir "dotnet test yaat.slnx -c $Config --no-build"
Run-Step 'Test yaat-server' $serverDir "dotnet test yaat-server.slnx -c $Config --no-build"

Write-Host ''
if ($failed) {
    Write-Host 'One or more steps failed.' -ForegroundColor Red
    exit 1
} else {
    Write-Host 'All builds and tests passed.' -ForegroundColor Green
    exit 0
}
