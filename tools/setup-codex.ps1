[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $CodexSkillsRoot = "C:\Users\Leftos\.agents\skills",
    [switch] $Force,
    [switch] $SkipMcp,
    [switch] $SkipSkillLinks,
    [switch] $CopyOnLinkFailure
)

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$ProjectSkillRoot = Join-Path $RepoRoot ".claude\skills"
$PowershellExe = (Get-Command pwsh -ErrorAction SilentlyContinue)?.Source
if (-not $PowershellExe) {
    $PowershellExe = (Get-Command powershell -ErrorAction Stop).Source
}

$CompatibleSkillNames = @(
    "bug-bundle",
    "layout-inspect",
    "test-fix",
    "consolidate-recordings"
)

$AdaptedSkillNames = @(
    "aviation-realism-review",
    "csharp-review",
    "architecture-doc-check",
    "prepare-release"
)

$ExpectedDescriptions = @{
    "test-fix" = "YAAT TDD bug-fix workflow: write failing test, confirm failure, apply fix, confirm pass"
}

function Get-SkillFrontmatter {
    param([Parameter(Mandatory = $true)][string] $SkillPath)

    $skillFile = Join-Path $SkillPath "SKILL.md"
    if (-not (Test-Path -LiteralPath $skillFile)) {
        Write-Warning "Missing SKILL.md: $SkillPath"
        return $null
    }

    $content = Get-Content -LiteralPath $skillFile -Raw
    $match = [regex]::Match($content, "(?s)^---\r?\n(.*?)\r?\n---")
    if (-not $match.Success) {
        Write-Warning "Invalid or missing YAML frontmatter: $skillFile"
        return $null
    }

    $data = [ordered]@{}
    foreach ($line in ($match.Groups[1].Value -split "\r?\n")) {
        if ($line -match "^\s*([A-Za-z0-9_-]+):\s*(.*)\s*$") {
            $key = $Matches[1]
            $value = $Matches[2].Trim()
            if (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'"))) {
                $value = $value.Substring(1, $value.Length - 2)
            }
            $data[$key] = $value
        }
    }

    return $data
}

function Test-Skill {
    param([Parameter(Mandatory = $true)][string] $Name)

    $skillPath = Join-Path $ProjectSkillRoot $Name
    $frontmatter = Get-SkillFrontmatter -SkillPath $skillPath
    if (-not $frontmatter) {
        return
    }

    $allowedKeys = @("name", "description", "license", "allowed-tools", "metadata")
    foreach ($key in $frontmatter.Keys) {
        if ($allowedKeys -notcontains $key) {
            Write-Warning "$Name has unexpected SKILL.md frontmatter key '$key'."
        }
    }

    if ($frontmatter["name"] -ne $Name) {
        Write-Warning "$Name frontmatter name is '$($frontmatter["name"])'."
    }

    if ([string]::IsNullOrWhiteSpace($frontmatter["description"])) {
        Write-Warning "$Name is missing a description."
    }

    if ($ExpectedDescriptions.ContainsKey($Name) -and ($frontmatter["description"] -ne $ExpectedDescriptions[$Name])) {
        Write-Warning "$Name description drifted. Expected: $($ExpectedDescriptions[$Name])"
    }

    $openAiYaml = Join-Path $skillPath "agents\openai.yaml"
    if (-not (Test-Path -LiteralPath $openAiYaml)) {
        Write-Warning "$Name is missing agents/openai.yaml metadata."
        return
    }

    $metadata = Get-Content -LiteralPath $openAiYaml -Raw
    if ($metadata -notmatch "display_name:\s*`"[^`"]+`"") {
        Write-Warning "$Name agents/openai.yaml is missing interface.display_name."
    }

    if ($metadata -notmatch "short_description:\s*`"[^`"]{25,64}`"") {
        Write-Warning "$Name agents/openai.yaml should have a 25-64 character short_description."
    }

    if ($metadata -match "default_prompt:" -and $metadata -notmatch [regex]::Escape("`$$Name")) {
        Write-Warning "$Name default_prompt should mention `$$Name."
    }
}

function New-SkillLink {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Source
    )

    $destination = Join-Path $CodexSkillsRoot $Name
    if (-not (Test-Path -LiteralPath $Source)) {
        Write-Warning "Cannot link missing skill source: $Source"
        return
    }

    if (Test-Path -LiteralPath $destination) {
        $item = Get-Item -LiteralPath $destination -Force
        $existingTarget = $item.Target
        if (($item.LinkType -eq "Junction" -or $item.LinkType -eq "SymbolicLink") -and ($existingTarget -eq $Source)) {
            Write-Host "Skill already linked: $Name -> $Source"
            return
        }

        if (-not $Force) {
            Write-Warning "Skipping existing skill path $destination. Re-run with -Force to replace it."
            return
        }

        if ($PSCmdlet.ShouldProcess($destination, "Remove existing Codex skill path")) {
            Remove-Item -LiteralPath $destination -Recurse -Force
        }
    }

    if ($PSCmdlet.ShouldProcess($destination, "Create junction to $Source")) {
        New-Item -ItemType Directory -Path $CodexSkillsRoot -Force | Out-Null
        try {
            New-Item -ItemType Junction -Path $destination -Target $Source | Out-Null
            Write-Host "Linked skill: $Name -> $Source"
        }
        catch {
            if (-not $CopyOnLinkFailure) {
                throw
            }

            Write-Warning "Junction failed for $Name; creating a local copy instead. Re-run setup to refresh it if the project skill changes."
            Copy-Item -LiteralPath $Source -Destination $destination -Recurse -Force
        }
    }
}

function Invoke-CodexMcpAdd {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string[]] $Arguments
    )

    if ((-not $WhatIfPreference) -and (-not $Force) -and (Test-McpRegistered -Name $Name)) {
        Write-Host "MCP server already registered: $Name"
        return
    }

    if ($PSCmdlet.ShouldProcess($Name, "Register Codex MCP server")) {
        & codex mcp add $Name @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "codex mcp add $Name failed with exit code $LASTEXITCODE."
        }
    }
}

function Test-McpRegistered {
    param([Parameter(Mandatory = $true)][string] $Name)

    if ($null -eq $script:ExistingMcpNames) {
        try {
            $raw = & codex mcp list --json
            if ($LASTEXITCODE -ne 0) {
                throw "codex mcp list --json failed with exit code $LASTEXITCODE."
            }

            $servers = $raw | ConvertFrom-Json
            $script:ExistingMcpNames = @($servers | ForEach-Object { $_.name })
        }
        catch {
            Write-Warning "Could not read existing Codex MCP registrations; setup will attempt to add MCP servers. $($_.Exception.Message)"
            $script:ExistingMcpNames = @()
        }
    }

    return $script:ExistingMcpNames -contains $Name
}

function Register-McpServers {
    Write-Host "Skipping standalone GitHub and Hugging Face MCP registration; use the official Codex plugins for those providers."

    if (-not [string]::IsNullOrWhiteSpace($env:CONTEXT7_API_KEY)) {
        Invoke-CodexMcpAdd -Name "context7" -Arguments @("--", $PowershellExe, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $RepoRoot "tools\mcp\context7-stdio.ps1"))
    }
    else {
        Write-Warning "CONTEXT7_API_KEY is not set; registering unauthenticated Context7 remote MCP."
        Invoke-CodexMcpAdd -Name "context7" -Arguments @("--url", "https://mcp.context7.com/mcp")
    }

    if (-not [string]::IsNullOrWhiteSpace($env:EXA_API_KEY)) {
        Invoke-CodexMcpAdd -Name "exa" -Arguments @("--", $PowershellExe, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $RepoRoot "tools\mcp\exa-stdio.ps1"))
    }
    else {
        Write-Warning "EXA_API_KEY is not set; registering Exa hosted MCP without an API key."
        Invoke-CodexMcpAdd -Name "exa" -Arguments @("--url", "https://mcp.exa.ai/mcp")
    }
}

$allSkillNames = $CompatibleSkillNames + $AdaptedSkillNames
foreach ($name in $allSkillNames) {
    Test-Skill -Name $name
}

if (-not $SkipSkillLinks) {
    foreach ($name in $allSkillNames) {
        New-SkillLink -Name $name -Source (Join-Path $ProjectSkillRoot $name)
    }
}

if (-not $SkipMcp) {
    Register-McpServers
}
