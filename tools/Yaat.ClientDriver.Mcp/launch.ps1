<#
.SYNOPSIS
Starts the yaat-client-driver MCP server from a copy of its build output outside the repo.

.DESCRIPTION
A server running from tools/Yaat.ClientDriver.Mcp/bin locks its own exe and dlls, and the solution
build (which prek runs on every commit) then fails to overwrite them. This script copies the build
output to %LOCALAPPDATA%/yaat/client-driver-mcp/run-<pid> and runs the server from there, so the
repo's bin stays free. Copies left by sessions whose launcher has exited are pruned on the way in.

stdout is the MCP protocol channel: nothing here may write to it. Errors go to stderr.

.EXAMPLE
pwsh -NoProfile -File tools/Yaat.ClientDriver.Mcp/launch.ps1
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot "bin/$Configuration/net10.0-windows"
$serverDll = 'Yaat.ClientDriver.Mcp.dll'
if (-not (Test-Path (Join-Path $source $serverDll))) {
    [Console]::Error.WriteLine("yaat-client-driver: no build output at $source. Run: dotnet build tools/Yaat.ClientDriver.Mcp")
    exit 1
}

$shadowRoot = Join-Path $env:LOCALAPPDATA 'yaat/client-driver-mcp'
$null = New-Item -ItemType Directory -Force $shadowRoot

foreach ($stale in Get-ChildItem $shadowRoot -Directory -Filter 'run-*') {
    $ownerPid = ($stale.Name -replace '^run-', '') -as [int]
    $owner = if ($null -ne $ownerPid) { Get-Process -Id $ownerPid -ErrorAction SilentlyContinue }
    if (($null -ne $owner) -and ($owner.ProcessName -eq 'pwsh')) { continue }
    try {
        Remove-Item $stale.FullName -Recurse -Force
    }
    catch {
        [Console]::Error.WriteLine("yaat-client-driver: could not prune $($stale.FullName): $($_.Exception.Message)")
    }
}

$shadow = Join-Path $shadowRoot "run-$PID"
$null = New-Item -ItemType Directory -Force $shadow
Copy-Item (Join-Path $source '*') $shadow -Recurse -Force

& dotnet (Join-Path $shadow $serverDll)
exit $LASTEXITCODE
