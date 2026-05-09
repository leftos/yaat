[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$envFile = Join-Path $repoRoot ".env"

function Import-AllowedEnvFromDotEnv {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string[]] $Names
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    foreach ($line in (Get-Content -LiteralPath $Path)) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith("#")) {
            continue
        }

        foreach ($name in $Names) {
            if ($trimmed -match ("^\s*" + [regex]::Escape($name) + "\s*=\s*(.*?)\s*$")) {
                $value = $Matches[1].Trim()
                if ((($value.StartsWith('"')) -and ($value.EndsWith('"'))) -or (($value.StartsWith("'")) -and ($value.EndsWith("'")))) {
                    $value = $value.Substring(1, $value.Length - 2)
                }

                [Environment]::SetEnvironmentVariable($name, $value, "Process")
            }
        }
    }
}

Import-AllowedEnvFromDotEnv -Path $envFile -Names @(
    "API_MARKET_KEY",
    "AERODATABOX_APIMARKET_KEY"
)

if ([string]::IsNullOrWhiteSpace($env:API_MARKET_KEY) -and (-not [string]::IsNullOrWhiteSpace($env:AERODATABOX_APIMARKET_KEY))) {
    $env:API_MARKET_KEY = $env:AERODATABOX_APIMARKET_KEY
}

if ([string]::IsNullOrWhiteSpace($env:API_MARKET_KEY)) {
    throw "API_MARKET_KEY or AERODATABOX_APIMARKET_KEY is required for the AeroDataBox MCP server."
}

if (-not (Get-Command npx -ErrorAction SilentlyContinue)) {
    throw "npx is required to launch mcp-remote for AeroDataBox MCP."
}

& npx -y mcp-remote "https://prod.api.market/api/mcp/aedbx/aerodatabox" "--header" 'x-api-market-key:${API_MARKET_KEY}'
exit $LASTEXITCODE
