<#
.SYNOPSIS
  Builds a client release without GitHub Actions and creates its draft: release.yml and
  release-macos.yml, run from this PC through the gate.

.DESCRIPTION
  Checks the tag out into a detached worktree (X:\dev\yaat.wt\release-local-<tag>), copies this
  folder's scripts into it when the tag predates them, and runs the two legs in parallel through the
  gate: pack-win-linux.ps1 on the Windows builder, pack-macos.ps1 pinned to the Mac builder. It then
  gathers the assets under the names release.yml and release-macos.yml give them into
  .tmp/release-local/<tag>/release, builds the body with release-notes.sh, and creates the draft.
  Publishing stays with the deploy or prepare-release, as for a hosted release.

  Secrets are read from G:\My Drive\Private Config\yaat\secrets\macos-signing\ and LMKIT_LICENSE_KEY
  from this checkout's .env into this process alone, and reach the legs as bare --env names. Nothing
  prints them.

.PARAMETER Tag
  The release tag, v<version>, pushed to origin.
.PARAMETER BuildOnly
  Build and gather the assets and the body; create no release.
.PARAMETER ReleaseName
  Create the draft under this name instead of the tag (a scratch test). A draft makes no git tag.
#>
param(
  [Parameter(Mandatory)][string]$Tag,
  [switch]$BuildOnly,
  [string]$ReleaseName
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$gate = Join-Path $repo 'tools\gate.ps1'
$signing = 'G:\My Drive\Private Config\yaat\secrets\macos-signing'
$gitHubRepo = 'leftos/yaat'
$macBuilder = 'builder@mac-mini'
if (-not $ReleaseName) { $ReleaseName = $Tag }

. (Join-Path $PSScriptRoot 'release-local-common.ps1')

function Assert-NoRelease([string]$Name) {
  gh release view $Name --repo $gitHubRepo --json tagName *> $null
  if ($LASTEXITCODE -eq 0) { throw "a GitHub release named $Name already exists (CI's draft, or an earlier run): delete it or use -BuildOnly" }
}

# Signing material and the LM-Kit key, into this process's environment under release-macos.yml's names.
function Import-ReleaseSecret {
  $env:MACOS_DEVID_APP_CERT_P12_BASE64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $signing 'devid-application.p12')))
  $env:MACOS_DEVID_INSTALLER_CERT_P12_BASE64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $signing 'devid-installer.p12')))
  $env:MACOS_DEVID_CERT_PASSWORD = (Get-Content (Join-Path $signing 'p12-password.txt') -Raw).Trim()
  $p8 = Get-ChildItem $signing -Filter 'AuthKey_*.p8' | Select-Object -First 1
  if (-not $p8) { throw "no AuthKey_*.p8 in $signing" }
  $env:MACOS_NOTARY_API_KEY_P8_BASE64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($p8.FullName))
  foreach ($line in Get-Content (Join-Path $signing 'ids.env')) {
    if ($line -match '^(NOTARY_API_KEY_ID|NOTARY_API_ISSUER_ID|SIGN_APP_IDENTITY|SIGN_INSTALL_IDENTITY)=(.+)$') {
      [Environment]::SetEnvironmentVariable("MACOS_$($Matches[1])", $Matches[2].Trim())
    }
  }
  $lmkit = Get-Content (Join-Path $repo '.env') | Where-Object { $_ -match '^LMKIT_LICENSE_KEY=' } | Select-Object -First 1
  if (-not $lmkit) { throw "no LMKIT_LICENSE_KEY in $repo\.env; the build would ship LM-Kit Community Edition" }
  $env:LMKIT_LICENSE_KEY = ($lmkit -replace '^LMKIT_LICENSE_KEY=', '').Trim()
}

function Invoke-Leg([string]$Name, [string[]]$GateArguments, [string]$Script, [string]$Version, [string]$ShortSha, [string]$Worktree) {
  # The gate keeps a run's log in the worktree it runs in; the logs are copied out before the worktree goes.
  $log = Join-Path $Worktree ".tmp\release-local-$Name.log"
  New-Item -ItemType Directory -Force -Path (Split-Path $log) | Out-Null
  $arguments = @('-NoProfile', '-File', $gate, 'run', '--log', $log, '--timeout', '5400', '--slot', 'heavy', '--cd', $Worktree) + $GateArguments +
    @('--', 'pwsh', '-NoProfile', '-File', "tools/release-local/$Script", '-Version', $Version, '-ShortSha', $ShortSha)
  Write-Host "  $Name leg: log $log"
  return Start-Process pwsh -ArgumentList $arguments -NoNewWindow -PassThru
}

function Copy-ReleaseAsset([string]$OutRoot, [string]$Version, [string]$ReleaseDir) {
  New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null
  # The auto-updater fetches RELEASES*, *.json and *-full.nupkg by exact name: copied verbatim.
  Get-ChildItem $OutRoot -Recurse -File | Where-Object { ($_.Name -like 'RELEASES*') -or ($_.Name -like '*.json') -or ($_.Name -like '*-full.nupkg') } |
    Copy-Item -Destination $ReleaseDir -Force
  # The legacy single osx channel, superseded by the arch split, points at the arm64 index.
  Copy-Item (Join-Path $ReleaseDir 'releases.osx-arm64.json') (Join-Path $ReleaseDir 'releases.osx.json') -Force
  Copy-Item (Join-Path $ReleaseDir 'assets.osx-arm64.json') (Join-Path $ReleaseDir 'assets.osx.json') -Force
  Copy-Item (Join-Path $ReleaseDir 'RELEASES-osx-arm64') (Join-Path $ReleaseDir 'RELEASES-osx') -Force
  $renames = [ordered]@{
    'win/YaatClient-win-Setup.exe' = "YaatClient-$Version-win-Setup.exe"
    'win/YaatClient-win-Portable.zip' = "YaatClient-$Version-win-Portable.zip"
    'linux/YaatClient.AppImage' = "YaatClient-$Version-linux.AppImage"
    'macos/YaatClient-osx-arm64-Setup.pkg' = "YaatClient-$Version-osx-arm64-Setup.pkg"
    'macos/YaatClient-osx-arm64-Portable.zip' = "YaatClient-$Version-osx-arm64-Portable.zip"
    'macos/YaatClient-osx-x64-Setup.pkg' = "YaatClient-$Version-osx-x64-Setup.pkg"
    'macos/YaatClient-osx-x64-Portable.zip' = "YaatClient-$Version-osx-x64-Portable.zip"
  }
  foreach ($source in $renames.Keys) { Copy-Item (Join-Path $OutRoot $source) (Join-Path $ReleaseDir $renames[$source]) -Force }
}

if (-not $BuildOnly) { Assert-NoRelease $ReleaseName }
git -C $repo fetch --quiet --tags origin
Assert-LastExit 'git fetch --tags'
$sha = (git -C $repo rev-parse --verify "$Tag^{commit}").Trim()
$worktree = "X:\dev\yaat.wt\release-local-$Tag"
$stage = Join-Path $repo ".tmp\release-local\$Tag"
if (Test-Path $worktree) { throw "$worktree exists: an earlier run's worktree; remove it with git worktree remove --force" }
git -C $repo worktree add --detach $worktree $sha
Assert-LastExit "git worktree add $worktree"
try {
  $scripts = Join-Path $worktree 'tools\release-local'
  New-Item -ItemType Directory -Force -Path $scripts | Out-Null
  foreach ($file in Get-ChildItem $PSScriptRoot -File) {
    if (-not (Test-Path (Join-Path $scripts $file.Name))) { Copy-Item $file.FullName $scripts }
  }
  $version = ([xml](Get-Content (Join-Path $worktree 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
  $shortSha = $sha.Substring(0, 7)
  Write-Host "Release $Tag ($sha): version $version, InformationalVersion $version+$shortSha"

  Import-ReleaseSecret
  $legs = @(
    Invoke-Leg 'win-linux' @('--env', 'LMKIT_LICENSE_KEY') 'pack-win-linux.ps1' $version $shortSha $worktree
    Invoke-Leg 'macos' (@('--builder', $macBuilder, '--env', 'LMKIT_LICENSE_KEY') + @('MACOS_DEVID_APP_CERT_P12_BASE64', 'MACOS_DEVID_INSTALLER_CERT_P12_BASE64',
      'MACOS_DEVID_CERT_PASSWORD', 'MACOS_NOTARY_API_KEY_P8_BASE64', 'MACOS_NOTARY_API_KEY_ID', 'MACOS_NOTARY_API_ISSUER_ID',
      'MACOS_SIGN_APP_IDENTITY', 'MACOS_SIGN_INSTALL_IDENTITY' | ForEach-Object { '--env', $_ })) 'pack-macos.ps1' $version $shortSha $worktree
  )
  $legs | Wait-Process
  $failed = @($legs | Where-Object { $_.ExitCode -ne 0 })
  if ($failed.Count -gt 0) { throw "$($failed.Count) leg(s) failed: read .tmp\release-local-win-linux.log and .tmp\release-local-macos.log" }

  if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
  $releaseDir = Join-Path $stage 'release'
  Copy-ReleaseAsset (Join-Path $worktree '.tmp\release-local\out') $version $releaseDir
  # Git for Windows' bash, never WSL's: the notes are built from the worktree's CHANGELOG.md, and
  # the body is written by bash itself so its bytes never pass through a console encoding.
  $gitRoot = Split-Path (Get-Command git).Source
  while ($gitRoot -and -not (Test-Path (Join-Path $gitRoot 'bin\bash.exe'))) { $gitRoot = Split-Path $gitRoot }
  if (-not $gitRoot) { throw "no Git for Windows bash.exe above $((Get-Command git).Source)" }
  $bash = Join-Path $gitRoot 'bin\bash.exe'
  $notesScript = (Join-Path $PSScriptRoot 'release-notes.sh') -replace '\\', '/'
  $notesDir = (Join-Path $stage 'notes') -replace '\\', '/'
  Push-Location $worktree
  try {
    & $bash $notesScript notes $Tag $gitHubRepo $notesDir
    Assert-LastExit 'release-notes.sh notes'
    & $bash -c '"$0" body "$1" "$2" > "$3"' $notesScript $version $notesDir ((Join-Path $stage 'release-body.md') -replace '\\', '/')
    Assert-LastExit 'release-notes.sh body'
  }
  finally { Pop-Location }
  Get-ChildItem $releaseDir -File | Sort-Object Name | ForEach-Object { '{0,-50} {1,14:N0} bytes' -f $_.Name, $_.Length }

  if ($BuildOnly) {
    Write-Host "Built $Tag into $stage; no release created (-BuildOnly)."
  }
  else {
    Assert-NoRelease $ReleaseName
    $assets = (Get-ChildItem $releaseDir -File).FullName
    gh release create $ReleaseName --repo $gitHubRepo --draft --title $ReleaseName --target $sha `
      --notes-file (Join-Path $stage 'release-body.md') @assets
    Assert-LastExit "gh release create $ReleaseName"
    Write-Host "Created the draft release $ReleaseName. Publish it as for a hosted release (prepare-release 7.3 or the deploy)."
  }
}
finally {
  Get-ChildItem (Join-Path $worktree '.tmp') -Filter 'release-local-*.log' -ErrorAction SilentlyContinue |
    Copy-Item -Destination (Join-Path $repo '.tmp') -Force
  git -C $repo worktree remove --force $worktree
  if ($LASTEXITCODE -ne 0) { Write-Warning "could not remove the worktree $worktree; remove it with git worktree remove --force" }
}
