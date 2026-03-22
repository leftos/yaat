param(
    [Parameter(Mandatory)]
    [string]$File,

    [string]$Bundle
)

$content = Get-Content $File -Raw
$prompts = @()
$currentBlock = @()

foreach ($line in ($content -split "`n")) {
    $line = $line.TrimEnd("`r")

    if ($line -match '^\s*-\s+(.+)') {
        # Flush any accumulated block
        if ($currentBlock.Count -gt 0) {
            $prompts += ($currentBlock -join "`n").Trim()
            $currentBlock = @()
        }
        # Single-line prompt
        $prompts += $Matches[1].Trim()
    }
    elseif ($line.Trim() -eq '') {
        # Blank line: flush block
        if ($currentBlock.Count -gt 0) {
            $prompts += ($currentBlock -join "`n").Trim()
            $currentBlock = @()
        }
    }
    else {
        # Continuation of a multi-line block
        $currentBlock += $line
    }
}

# Flush final block
if ($currentBlock.Count -gt 0) {
    $prompts += ($currentBlock -join "`n").Trim()
}

# Filter empty
$prompts = $prompts | Where-Object { $_.Trim() -ne '' }

Write-Host "Found $($prompts.Count) prompts:`n"
for ($i = 0; $i -lt $prompts.Count; $i++) {
    $preview = $prompts[$i] -split "`n" | Select-Object -First 1
    if ($preview.Length -gt 80) { $preview = $preview.Substring(0, 77) + "..." }
    Write-Host "  [$($i + 1)] $preview"
}

Write-Host ""
$confirm = Read-Host "Launch all $($prompts.Count) in separate tabs? (y/n)"
if ($confirm -ne 'y') {
    Write-Host "Aborted."
    exit
}

foreach ($prompt in $prompts) {
    $escaped = $prompt -replace "'", "''"
    $bundleArg = if ($Bundle) { " -Bundle '$Bundle'" } else { "" }
    $claudeCmd = "claude$bundleArg"

    # Write prompt to a temp file to avoid shell escaping issues
    $tmpFile = [System.IO.Path]::GetTempFileName()
    Set-Content -Path $tmpFile -Value $prompt -NoNewline

    $bashCmd = "cat '$($tmpFile -replace '\\', '/')' | claude$bundleArg; exec bash"
    Start-Process wt -ArgumentList "new-tab", "bash", "-c", $bashCmd
}

Write-Host "Launched $($prompts.Count) tabs."
