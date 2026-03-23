#Requires -Version 7
<#
.SYNOPSIS
  Creates paired yaat + yaat-server worktrees and optionally launches claude.

.DESCRIPTION
  Layout:
    <yaat-repo>/.claude/worktrees/<name>/yaat/          <- yaat worktree
    <yaat-repo>/.claude/worktrees/<name>/yaat-server/   <- yaat-server worktree

  The sibling layout preserves yaat-server's ../yaat project reference.

.PARAMETER Name
  Worktree name (default: random hex).

.PARAMETER Launch
  Start claude --dangerously-skip-permissions in the yaat worktree.

.EXAMPLE
  .\claude-worktree.ps1
  .\claude-worktree.ps1 -Launch
  .\claude-worktree.ps1 -Name my-feature -Launch
#>
param(
    [string]$Name,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$YaatRoot = Resolve-Path (Join-Path $ScriptDir '..')
$ServerPath = Join-Path (Split-Path -Parent $YaatRoot) 'yaat-server'

if (-not (Test-Path (Join-Path $ServerPath '.git'))) {
    Write-Error "yaat-server repo not found at $ServerPath"
}
$ServerRoot = Resolve-Path $ServerPath

if (-not $Name) {
    $hex = -join ((1..8) | ForEach-Object { '{0:x}' -f (Get-Random -Maximum 16) })
    $Name = "wt-$hex"
}

$Branch = "worktree-$Name"
$Base = Join-Path $YaatRoot '.claude' 'worktrees' $Name
$YaatWt = Join-Path $Base 'yaat'
$ServerWt = Join-Path $Base 'yaat-server'

Write-Host "Creating paired worktrees: $Name"
Write-Host "  yaat:        $YaatWt"
Write-Host "  yaat-server: $ServerWt"

git -C $YaatRoot worktree add $YaatWt -b $Branch
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to create yaat worktree" }

git -C $ServerRoot worktree add $ServerWt -b $Branch
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to create yaat-server worktree" }

Write-Host ""
Write-Host "Worktrees created. To use:"
Write-Host "  cd `"$YaatWt`""
Write-Host ""
Write-Host "To clean up:"
Write-Host "  git -C `"$YaatRoot`" worktree remove `"$YaatWt`""
Write-Host "  git -C `"$ServerRoot`" worktree remove `"$ServerWt`""
Write-Host "  git -C `"$YaatRoot`" branch -D `"$Branch`""
Write-Host "  git -C `"$ServerRoot`" branch -D `"$Branch`""

if ($Launch) {
    Write-Host ""
    Write-Host "Launching claude in $YaatWt..."
    Set-Location $YaatWt
    claude --dangerously-skip-permissions
}
