#Requires -Version 5.1
<#
.SYNOPSIS
    Adds YAAT environments to CRC's DevEnvironments.json.
.DESCRIPTION
    Locates CRC's per-user config directory by probing platform-specific candidate
    paths for a marker file (GeneralSettings.json), then creates or updates
    DevEnvironments.json next to it.

    Candidate paths (first match wins):
      Windows: HKCU:\Software\CRC\Install_Dir, then $env:LOCALAPPDATA\CRC
      macOS:   ~/Library/Application Support/CRC
      Linux:   ~/.config/CRC

    Runs on Windows PowerShell 5.1 and PowerShell 7+ (cross-platform).

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

function Get-CrcConfigDir {
    $marker = "GeneralSettings.json"
    $candidates = @()

    # Windows PowerShell 5.1 doesn't define $IsWindows/$IsMacOS/$IsLinux — fall back to Platform.
    $platform = $PSVersionTable.Platform
    $isWin = ($null -eq $platform) -or ($platform -eq 'Win32NT')
    $isMac = ($platform -eq 'Unix') -and (Test-Path '/System/Library/CoreServices/SystemVersion.plist')
    $isLinux = ($platform -eq 'Unix') -and -not $isMac

    if ($isWin) {
        $regPath = "HKCU:\Software\CRC"
        $regValue = "Install_Dir"
        if (Test-Path $regPath) {
            $installDir = (Get-ItemProperty -Path $regPath -ErrorAction SilentlyContinue).$regValue
            if ($installDir) {
                $candidates += $installDir
            }
        }
        if ($env:LOCALAPPDATA) {
            $candidates += (Join-Path $env:LOCALAPPDATA "CRC")
        }
    }
    elseif ($isMac) {
        $candidates += (Join-Path $HOME "Library/Application Support/CRC")
    }
    elseif ($isLinux) {
        $candidates += (Join-Path $HOME ".config/CRC")
    }

    foreach ($candidate in $candidates) {
        if (Test-Path (Join-Path $candidate $marker)) {
            return $candidate
        }
    }

    return $null
}

$configDir = Get-CrcConfigDir
if (-not $configDir) {
    Write-Error "CRC config directory not found. Is CRC installed and has it been run at least once?"
    exit 1
}

$jsonPath = Join-Path $configDir "DevEnvironments.json"

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
