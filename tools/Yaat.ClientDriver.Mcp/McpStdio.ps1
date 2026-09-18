<#
.SYNOPSIS
JSON-RPC-over-stdio plumbing shared by smoke.ps1 and live-check.ps1.

.DESCRIPTION
Dot-source this file. Start-McpServer launches the server with stdin/stdout redirected and stderr
inherited — the server logs to stderr, so leaving it attached keeps the log visible and avoids the
deadlock a full, undrained pipe would cause. MCP stdio framing is newline-delimited JSON: one
request object per line, one response object per line, no Content-Length headers.
#>

function Start-McpServer {
    param(
        [Parameter(Mandatory)][string]$Command,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)][string]$WorkingDirectory
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Command
    foreach ($argument in $Arguments) { $psi.ArgumentList.Add($argument) }
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.StandardInputEncoding = [System.Text.UTF8Encoding]::new($false)
    $psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)

    $process = [System.Diagnostics.Process]::Start($psi)
    [pscustomobject]@{ Process = $process; NextId = 1 }
}

function Stop-McpServer {
    param([Parameter(Mandatory)]$Session)

    if ($null -eq $Session -or $null -eq $Session.Process) { return }
    try {
        if (-not $Session.Process.HasExited) { $Session.Process.Kill($true) }
    }
    catch {
        Write-Warning "Could not stop the server process: $($_.Exception.Message)"
    }
}

function Send-McpLine {
    param([Parameter(Mandatory)]$Session, [Parameter(Mandatory)][string]$Line)

    $Session.Process.StandardInput.WriteLine($Line)
    $Session.Process.StandardInput.Flush()
}

function Read-McpLine {
    param([Parameter(Mandatory)]$Session, [int]$TimeoutSeconds = 20)

    $task = $Session.Process.StandardOutput.ReadLineAsync()
    if (-not $task.Wait([TimeSpan]::FromSeconds($TimeoutSeconds))) {
        throw "Timed out after $TimeoutSeconds s waiting for a line on the server's stdout."
    }
    $line = $task.Result
    if ($null -eq $line) {
        $exitCode = if ($Session.Process.HasExited) { $Session.Process.ExitCode } else { '(still running)' }
        throw "The server closed stdout without answering; exit code $exitCode."
    }
    $line
}

function Send-McpNotification {
    param([Parameter(Mandatory)]$Session, [Parameter(Mandatory)][string]$Method, $Params)

    $payload = @{ jsonrpc = '2.0'; method = $Method }
    if ($null -ne $Params) { $payload['params'] = $Params }
    Send-McpLine -Session $Session -Line ($payload | ConvertTo-Json -Depth 10 -Compress)
}

function Invoke-McpRequest {
    param([Parameter(Mandatory)]$Session, [Parameter(Mandatory)][string]$Method, $Params, [int]$TimeoutSeconds = 20)

    $id = $Session.NextId
    $Session.NextId = $id + 1
    $payload = @{ jsonrpc = '2.0'; id = $id; method = $Method }
    if ($null -ne $Params) { $payload['params'] = $Params }
    Send-McpLine -Session $Session -Line ($payload | ConvertTo-Json -Depth 10 -Compress)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        if ([DateTime]::UtcNow -gt $deadline) { throw "No reply to '$Method' within $TimeoutSeconds s." }
        $remaining = [int][Math]::Max(1, ($deadline - [DateTime]::UtcNow).TotalSeconds)
        $line = Read-McpLine -Session $Session -TimeoutSeconds $remaining
        try {
            $parsed = $line | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            throw "The server wrote a non-JSON line to stdout before its reply to '$Method' — stdout must carry nothing but JSON-RPC frames. Line: $line"
        }
        if ($null -ne $parsed.id -and [int]$parsed.id -eq $id) { return $parsed }
    }
}

function Initialize-McpSession {
    param([Parameter(Mandatory)]$Session, [string]$ClientName = 'yaat-client-driver-check', [int]$TimeoutSeconds = 20)

    $params = @{
        protocolVersion = '2025-06-18'
        capabilities    = @{}
        clientInfo      = @{ name = $ClientName; version = '1.0.0' }
    }
    $response = Invoke-McpRequest -Session $Session -Method 'initialize' -Params $params -TimeoutSeconds $TimeoutSeconds
    if ($null -ne $response.error) { throw "initialize failed: $($response.error | ConvertTo-Json -Depth 5 -Compress)" }
    Send-McpNotification -Session $Session -Method 'notifications/initialized'
    $response
}

function Invoke-McpTool {
    param([Parameter(Mandatory)]$Session, [Parameter(Mandatory)][string]$Name, [hashtable]$ToolArguments = @{}, [int]$TimeoutSeconds = 60)

    $response = Invoke-McpRequest -Session $Session -Method 'tools/call' -Params @{ name = $Name; arguments = $ToolArguments } -TimeoutSeconds $TimeoutSeconds
    if ($null -ne $response.error) { throw "$Name failed at the protocol level: $($response.error | ConvertTo-Json -Depth 5 -Compress)" }
    $response.result
}

function Get-McpResultText {
    param([Parameter(Mandatory)]$Result)

    ($Result.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join "`n"
}
