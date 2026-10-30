<#
.SYNOPSIS
  The Windows and Linux legs of a local client release: release.yml's build, package-win and
  package-linux jobs, run from the root of a release worktree.

.DESCRIPTION
  Publishes Yaat.Client for win-x64 and linux-x64 the way release.yml's build job does, then packs
  each with vpk (Linux cross-packed with `vpk [linux] pack`) into .tmp/release-local/out/{win,linux}.
  release.ps1 routes it to the Windows builder through the gate; LMKIT_LICENSE_KEY arrives there as
  a forwarded variable (empty builds LM-Kit's Community Edition, as in CI).

.PARAMETER Version
  The <Version> of the worktree's Directory.Build.props.
.PARAMETER ShortSha
  The release commit's 7-character short hash, for InformationalVersion.
#>
param(
  [Parameter(Mandatory)][string]$Version,
  [Parameter(Mandatory)][string]$ShortSha
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-local-common.ps1')

$work = Join-Path (Get-Location) '.tmp/release-local'
$out = Join-Path $work 'out'
foreach ($dir in (Join-Path $out 'win'), (Join-Path $out 'linux'), (Join-Path $work 'publish')) {
  if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}
$vpk = Install-Vpk (Join-Path $work 'vpk')

foreach ($rid in 'win-x64', 'linux-x64') {
  Publish-YaatClient -Rid $rid -Version $Version -ShortSha $ShortSha -OutDir (Join-Path $work "publish/$rid")
}

& $vpk pack --packId YaatClient --packVersion $Version `
  --packDir (Join-Path $work 'publish/win-x64') --mainExe Yaat.Client.exe --packTitle YAAT `
  --icon src/Yaat.Client/Resources/icon.ico --outputDir (Join-Path $out 'win') -x
Assert-LastExit 'vpk pack (win-x64)'
& $vpk '[linux]' pack --packId YaatClient --packVersion $Version `
  --packDir (Join-Path $work 'publish/linux-x64') --mainExe Yaat.Client --packTitle YAAT `
  --icon src/Yaat.Client/Resources/icon.png --outputDir (Join-Path $out 'linux') -x
Assert-LastExit 'vpk [linux] pack (linux-x64)'

Get-ChildItem $out -Recurse -File | ForEach-Object { '{0,-60} {1,14:N0} bytes' -f $_.FullName.Substring($out.Length + 1), $_.Length }
