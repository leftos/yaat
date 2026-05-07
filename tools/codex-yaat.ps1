[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $CodexArgs
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$serverRoot = (Resolve-Path (Join-Path $repoRoot "..\yaat-server") -ErrorAction SilentlyContinue)

if (-not $serverRoot) {
    Write-Warning "Sibling repo ..\yaat-server was not found. Launching with YAAT only."
}

$arguments = @("-C", $repoRoot)
if ($serverRoot) {
    $arguments += @("--add-dir", $serverRoot.Path)
}

if ($CodexArgs) {
    $arguments += $CodexArgs
}

& codex @arguments
exit $LASTEXITCODE
