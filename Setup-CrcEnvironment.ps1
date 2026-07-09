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

    By default, adds:
      - YAAT1  → https://yaat1.leftos.dev

    When run from the YAAT repo, the default list is loaded from
    docs/crc-environments.json (the same canonical file used by the standalone
    yaat-crc-config tool and the C# CrcConfigService). When run as a downloaded
    .ps1 with no repo nearby, the hardcoded defaults are used.

    Use -Servers to override — for example to point CRC at a server you host yourself.
.PARAMETER Servers
    Array of hashtables with Name and Url keys. Overrides the default list.
.EXAMPLE
    .\Setup-CrcEnvironment.ps1
    # Adds YAAT1 with its default URL.
.EXAMPLE
    .\Setup-CrcEnvironment.ps1 -Servers @(@{Name="YAAT Local";Url="http://localhost:5000"})
    # Adds an entry for a server running on this machine.
#>
param(
    [hashtable[]]$Servers
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-DefaultServers {
    $hardcoded = @(
        @{ Name = "YAAT1"; Url = "https://yaat1.leftos.dev" }
    )

    $scriptDir = $PSScriptRoot
    if (-not $scriptDir) {
        return $hardcoded
    }

    $candidate = Join-Path $scriptDir "docs/crc-environments.json"
    if (-not (Test-Path $candidate)) {
        return $hardcoded
    }

    try {
        $entries = Get-Content $candidate -Raw | ConvertFrom-Json
        if ($entries -isnot [System.Array]) { $entries = @($entries) }
        return @($entries | ForEach-Object { @{ Name = $_.name; Url = $_.apiBaseUrl } })
    }
    catch {
        Write-Warning "Failed to read $candidate ($_); falling back to hardcoded defaults."
        return $hardcoded
    }
}

if (-not $Servers) {
    $Servers = Resolve-DefaultServers
}

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
