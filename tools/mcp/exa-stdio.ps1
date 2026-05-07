[CmdletBinding()]
param(
    [string] $Tools = "web_search_exa,get_code_context_exa"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($env:EXA_API_KEY)) {
    throw "EXA_API_KEY is required for the Exa stdio adapter."
}

& npx -y "exa-mcp-server" "tools=$Tools"
exit $LASTEXITCODE
