[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($env:CONTEXT7_API_KEY)) {
    throw "CONTEXT7_API_KEY is required for the Context7 stdio adapter."
}

& npx -y "@upstash/context7-mcp" "--api-key" $env:CONTEXT7_API_KEY
exit $LASTEXITCODE
