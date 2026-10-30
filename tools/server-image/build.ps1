<#
.SYNOPSIS
  Builds the yaat-server container image with the .NET SDK (no Docker engine) and pushes it to
  ghcr.io, or writes it to an archive for inspection.

.DESCRIPTION
  Checks out both repos at the commits named into fresh detached worktrees under .tmp, side by
  side so yaat-server's Directory.Build.props finds that yaat as its sibling. Publishes the vStrips
  and vTDLS browser apps into that yaat-server's wwwroot, then publishes Yaat.Server for linux-x64
  with -t:PublishContainer, using container.targets beside this script for the image's base, user,
  port and environment (the runtime stage of yaat-server's Dockerfile).

  Never builds the working checkouts: their git-ignored files (appsettings.Local.json, a developer's
  override loaded after the environment) would be published into the image. Run it through the
  gate; a push needs SDK_CONTAINER_REGISTRY_UNAME and SDK_CONTAINER_REGISTRY_PWORD (a token with
  write:packages) forwarded with a bare --env NAME each.

.PARAMETER ServerSha
  The full yaat-server commit to build.
.PARAMETER ClientSha
  The full yaat commit to build.
.PARAMETER Tags
  The image tags to push, such as latest and <server short>-<client short>.
.PARAMETER ArchivePath
  Write the image to this .tar.gz instead of pushing, and print its configuration.
#>
[CmdletBinding(DefaultParameterSetName = 'Push')]
param(
  [Parameter(Mandatory)][string]$ServerSha,
  [Parameter(Mandatory)][string]$ClientSha,
  [Parameter(Mandatory, ParameterSetName = 'Push')][string[]]$Tags,
  [Parameter(Mandatory, ParameterSetName = 'Archive')][string]$ArchivePath
)

$ErrorActionPreference = 'Stop'
$yaatRepo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$serverRepo = (Resolve-Path (Join-Path $yaatRepo '..\yaat-server')).Path
$buildRoot = Join-Path $yaatRepo '.tmp\server-image-build'
$yaat = Join-Path $buildRoot 'yaat'
$server = Join-Path $buildRoot 'yaat-server'

function Clear-BuildWorktree([string]$Repo, [string]$Path) {
  if (Test-Path $Path) {
    git -C $Repo worktree remove --force $Path
    if ($LASTEXITCODE -ne 0) { throw "could not remove the build worktree $Path ($LASTEXITCODE)" }
  }
  git -C $Repo worktree prune
}

function Add-BuildWorktree([string]$Repo, [string]$Path, [string]$Sha) {
  Clear-BuildWorktree $Repo $Path
  git -C $Repo worktree add --detach $Path $Sha
  if ($LASTEXITCODE -ne 0) { throw "could not check out $Sha of $Repo into $Path ($LASTEXITCODE)" }
}

# A file a developer keeps beside the server and git ignores must never ride into the image.
function Assert-NoLocalOverride([string]$PublishDir) {
  $found = @(Get-ChildItem $PublishDir -Filter 'appsettings.Local.json' -Recurse -ErrorAction SilentlyContinue)
  if ($found.Count -gt 0) { throw "the publish holds a local override: $($found.FullName -join ', ')" }
}

# The SDK writes a plain tar whatever the archive's extension, and layers may be either.
function Open-TarStream([string]$File) {
  $stream = [IO.File]::OpenRead($File)
  $magic = [byte[]]::new(2)
  $null = $stream.Read($magic, 0, 2)
  $stream.Position = 0
  if (($magic[0] -eq 0x1f) -and ($magic[1] -eq 0x8b)) {
    return [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionMode]::Decompress)
  }
  return $stream
}

function Get-TarEntryName([string]$File) {
  $stream = Open-TarStream $File
  try {
    $reader = [System.Formats.Tar.TarReader]::new($stream)
    $names = [Collections.Generic.List[string]]::new()
    while ($null -ne ($entry = $reader.GetNextEntry())) { $names.Add($entry.Name.TrimStart('.', '/')) }
    return , $names
  }
  finally { $stream.Dispose() }
}

function Show-ImageArchive([string]$Path) {
  $dir = Join-Path ([IO.Path]::GetDirectoryName($Path)) 'archive-contents'
  if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
  New-Item -ItemType Directory -Path $dir | Out-Null
  $stream = Open-TarStream $Path
  try { [System.Formats.Tar.TarFile]::ExtractToDirectory($stream, $dir, $false) }
  finally { $stream.Dispose() }
  $manifest = Get-Content (Join-Path $dir 'manifest.json') -Raw | ConvertFrom-Json
  $config = (Get-Content (Join-Path $dir $manifest[0].Config) -Raw | ConvertFrom-Json).config
  Write-Host "Image config:"
  Write-Host "  User: $($config.User)"
  Write-Host "  WorkingDir: $($config.WorkingDir)"
  Write-Host "  Entrypoint: $($config.Entrypoint -join ' ')"
  Write-Host "  ExposedPorts: $(($config.ExposedPorts.PSObject.Properties.Name) -join ', ')"
  $config.Env | ForEach-Object { Write-Host "  Env: $_" }
  foreach ($layer in $manifest[0].Layers) {
    $file = Join-Path $dir $layer
    $entries = Get-TarEntryName $file
    $marks = @('app/Yaat.Server.dll', 'app/wwwroot/vstrips/index.html', 'app/wwwroot/vtdls/index.html') |
      Where-Object { $entries.Contains($_) }
    Write-Host ("layer {0}: {1:N1} MB, {2} entries; {3}" -f $layer, ((Get-Item $file).Length / 1MB), $entries.Count, ($marks -join ', '))
  }
}

Add-BuildWorktree $yaatRepo $yaat $ClientSha
Add-BuildWorktree $serverRepo $server $ServerSha
try {

dotnet msbuild (Join-Path $PSScriptRoot 'web-apps.proj') -restore -m -t:Publish "-p:YaatRoot=$yaat" "-p:YaatServer=$server"
if ($LASTEXITCODE -ne 0) { throw "web app publish failed ($LASTEXITCODE)" }

$publishArgs = @(
  (Join-Path $server 'src\Yaat.Server\Yaat.Server.csproj'),
  '-c', 'Release', '-r', 'linux-x64', '--self-contained', 'false',
  '-t:PublishContainer',
  "-p:CustomAfterMicrosoftCommonTargets=$(Join-Path $PSScriptRoot 'container.targets')",
  "-p:YaatServerCommit=$ServerSha",
  "-p:YaatClientCommit=$ClientSha"
)
if ($PSCmdlet.ParameterSetName -eq 'Archive') {
  $ArchivePath = [IO.Path]::GetFullPath($ArchivePath, (Get-Location).Path)
  New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($ArchivePath)) | Out-Null
  $publishArgs +='-p:ContainerImageTags=archive', "-p:ContainerArchiveOutputPath=$ArchivePath"
}
else {
  foreach ($name in 'SDK_CONTAINER_REGISTRY_UNAME', 'SDK_CONTAINER_REGISTRY_PWORD') {
    if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is not set; forward it with a bare --env $name." }
  }
  # pwsh -File passes "-Tags a,b" as one string, so split on commas here.
  $Tags = @($Tags | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
  $publishArgs += "-p:ContainerImageTags=`"$($Tags -join ';')`""
}

# Publish the files first and check them, so nothing is pushed until the check passes.
dotnet publish ($publishArgs | Where-Object { $_ -ne '-t:PublishContainer' })
if ($LASTEXITCODE -ne 0) { throw "Yaat.Server publish failed ($LASTEXITCODE)" }
Assert-NoLocalOverride (Join-Path $server 'src\Yaat.Server\bin\Release\net10.0\linux-x64\publish')

dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Yaat.Server container publish failed ($LASTEXITCODE)" }

if ($PSCmdlet.ParameterSetName -eq 'Archive') { Show-ImageArchive $ArchivePath }
else { Write-Host "Pushed ghcr.io/leftos/yaat-server:$($Tags -join ', :')" }

}
finally {
  Clear-BuildWorktree $serverRepo $server
  Clear-BuildWorktree $yaatRepo $yaat
}
