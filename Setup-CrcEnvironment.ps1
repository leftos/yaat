#Requires -Version 5.1
<#
.SYNOPSIS
    Adds YAAT environments to CRC's DevEnvironments.json.
.DESCRIPTION
    Reads the CRC installation path from the registry, then creates or
    updates DevEnvironments.json with YAAT server entries.

    By default, adds both:
      - YAAT1       → https://yaat1.leftos.dev
      - YAAT Local  → http://localhost:5000

    Use -Servers to override the default list.
.PARAMETER Servers
    Array of hashtables with Name and Url keys. Overrides the default list.
.EXAMPLE
    .\Setup-CrcEnvironment.ps1
    # Adds YAAT1 and YAAT Local with default URLs.
.EXAMPLE
    .\Setup-CrcEnvironment.ps1 -Servers @(@{Name="Custom";Url="http://192.168.1.50:5000"})
    # Adds a single custom entry.
#>
param(
    [hashtable[]]$Servers = @(
        @{ Name = "YAAT1"; Url = "https://yaat1.leftos.dev" },
        @{ Name = "YAAT Local"; Url = "http://localhost:5000" }
    )
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$regPath = "HKCU:\Software\CRC"
$regValue = "Install_Dir"

if (-not (Test-Path $regPath)) {
    Write-Error "CRC registry key not found at $regPath. Is CRC installed?"
    exit 1
}

$installDir = (Get-ItemProperty -Path $regPath).$regValue
if (-not $installDir -or -not (Test-Path $installDir)) {
    Write-Error "CRC install directory '$installDir' does not exist."
    exit 1
}

$jsonPath = Join-Path $installDir "DevEnvironments.json"

if (Test-Path $jsonPath) {
    $environments = Get-Content $jsonPath -Raw | ConvertFrom-Json

    if ($environments -isnot [System.Array]) {
        $environments = @($environments)
    }
}
else {
    $environments = @()
}

foreach ($server in $Servers) {
    $baseUrl = $server.Url.TrimEnd('/')
    $name = $server.Name

    $entry = [ordered]@{
        name         = $name
        clientHubUrl = "$baseUrl/hubs/client"
        apiBaseUrl   = "$baseUrl"
        isDisabled   = $false
        isSweatbox   = $false
    }

    $existing = $environments | Where-Object { $_.name -eq $name }
    if ($existing) {
        Write-Host "Environment '$name' already exists in $jsonPath - updating."
        $existing.clientHubUrl = $entry.clientHubUrl
        $existing.apiBaseUrl = $entry.apiBaseUrl
        $existing.isDisabled = $entry.isDisabled
        $existing.isSweatbox = $entry.isSweatbox
    }
    else {
        Write-Host "Adding '$name' to $jsonPath."
        $environments += [PSCustomObject]$entry
    }
}

if ($environments.Count -gt 0) {
    $environments | ConvertTo-Json -Depth 10 | Set-Content $jsonPath -Encoding UTF8
}

Write-Host "Done. Restart CRC to pick up changes."
