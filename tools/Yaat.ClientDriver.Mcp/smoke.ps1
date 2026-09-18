<#
.SYNOPSIS
Protocol smoke test for the yaat-client-driver MCP server: no windows are opened and no input is sent.

.DESCRIPTION
Starts the server the way .mcp.json does, runs initialize → tools/list → tools/call list_processes,
and fails when stdout carries anything that is not a JSON-RPC frame, when a tool the contract
promises is missing, or when the call comes back as an error. Prints "SMOKE OK <n> tools" on success.

.EXAMPLE
pwsh tools/Yaat.ClientDriver.Mcp/smoke.ps1
#>

[CmdletBinding()]
param(
    [string]$Command = 'dotnet',
    [string[]]$Arguments = @('run', '--project', 'tools/Yaat.ClientDriver.Mcp', '--no-build'),
    [string[]]$ExpectedTools = @(
        'launch_yaat',
        'list_processes',
        'stop_process',
        'tail_yaat_log',
        'list_windows',
        'dump_tree',
        'find_elements',
        'screenshot',
        'get_value',
        'invoke',
        'click',
        'click_point',
        'set_text',
        'send_keys',
        'focus'
    )
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'McpStdio.ps1')

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$session = $null
try {
    $session = Start-McpServer -Command $Command -Arguments $Arguments -WorkingDirectory $repoRoot
    $null = Initialize-McpSession -Session $session

    $toolsResponse = Invoke-McpRequest -Session $session -Method 'tools/list'
    if ($null -ne $toolsResponse.error) { throw "tools/list failed: $($toolsResponse.error | ConvertTo-Json -Depth 5 -Compress)" }
    $toolNames = @($toolsResponse.result.tools | ForEach-Object { $_.name })

    $missing = @($ExpectedTools | Where-Object { $toolNames -notcontains $_ })
    if ($missing.Count -gt 0) {
        throw "tools/list is missing $($missing.Count) tool(s): $($missing -join ', '). Present: $($toolNames -join ', ')"
    }

    $result = Invoke-McpTool -Session $session -Name 'list_processes' -ToolArguments @{ nameContains = 'explorer' }
    if ($result.isError) { throw "list_processes returned isError: $(Get-McpResultText -Result $result)" }

    Write-Host "SMOKE OK $($toolNames.Count) tools"
    exit 0
}
catch {
    [Console]::Error.WriteLine("SMOKE FAILED: $($_.Exception.Message)")
    exit 1
}
finally {
    Stop-McpServer -Session $session
}
