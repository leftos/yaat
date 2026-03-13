param(
    [Parameter(Mandatory=$true, Position=0)]
    [int]$IssueNumber
)

# Read .env from repo root
$envFile = Join-Path $PSScriptRoot "..\..\.env"
if (-not (Test-Path $envFile)) {
    Write-Error "No .env file found at $envFile"
    exit 1
}

$env = @{}
Get-Content $envFile | ForEach-Object {
    if ($_ -match '^([A-Z_]+)=(.*)$') {
        $env[$Matches[1]] = $Matches[2]
    }
}

$workerUrl = $env['WORKER_URL']
$secret = $env['GITHUB_WEBHOOK_SECRET']

if (-not $workerUrl) { Write-Error "WORKER_URL not set in .env"; exit 1 }
if (-not $secret) { Write-Error "GITHUB_WEBHOOK_SECRET not set in .env"; exit 1 }

$url = "$workerUrl/sync/$IssueNumber"
Write-Host "Syncing issue #$IssueNumber..."

$response = Invoke-RestMethod -Uri $url -Method Post -Headers @{
    "Authorization" = "Bearer $secret"
} -ErrorAction Stop

Write-Host "Synced $($response.synced) message(s) for issue #$($response.issue)"
