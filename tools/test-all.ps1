#!/usr/bin/env pwsh
# Builds and runs the full test suites for both yaat and yaat-server repos.
# Usage:
#   pwsh tools/test-all.ps1                 # Release (default — ~30% faster on Sim)
#   pwsh tools/test-all.ps1 -Config Debug   # Debug (better stack traces for failures)

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Config = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$yaatRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path (Join-Path $PSScriptRoot '..'))) {
    $yaatRoot = Split-Path -Parent $PSScriptRoot
}

$yaatDir = Join-Path $PSScriptRoot '..'
$serverDir = Join-Path $PSScriptRoot '..\..\yaat-server'

$yaatDir = (Resolve-Path $yaatDir).Path
$serverDir = (Resolve-Path $serverDir).Path

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

Run-Step 'Build yaat' $yaatDir "dotnet build -c $Config -p:TreatWarningsAsErrors=true"
Run-Step 'Build yaat-server' $serverDir "dotnet build -c $Config -p:TreatWarningsAsErrors=true"
Run-Step 'Test yaat' $yaatDir "dotnet test -c $Config --no-build"
Run-Step 'Test yaat-server' $serverDir "dotnet test -c $Config --no-build"

Write-Host ''
if ($failed) {
    Write-Host 'One or more steps failed.' -ForegroundColor Red
    exit 1
} else {
    Write-Host 'All builds and tests passed.' -ForegroundColor Green
    exit 0
}
