# Helpers shared by the local release legs (pack-win-linux.ps1, pack-macos.ps1). Dot-sourced;
# defines functions only.

# The vpk release.yml and release-macos.yml pin (VPK_VERSION), installed under the run folder so a
# builder needs no global tool.
$script:VpkVersion = '1.2.0'

# Native commands are called directly and checked after: a wrapper function would rebind their
# switches (`-c` to a declared `-Command`) or drop words like `--version` from $args.
function Assert-LastExit([string]$What) {
  if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE)" }
}

function Install-Vpk([string]$ToolPath) {
  # A tool's launcher finds .NET by DOTNET_ROOT or the default install folder; a builder whose
  # dotnet lives elsewhere (the mac-mini's ~/gate-tools/dotnet) needs it pointed at the one in use.
  if (-not $env:DOTNET_ROOT) {
    $env:DOTNET_ROOT = Split-Path ((Get-Item (Get-Command dotnet).Source).ResolvedTarget ?? (Get-Command dotnet).Source)
  }
  $exe = Join-Path $ToolPath ($IsWindows ? 'vpk.exe' : 'vpk')
  if (-not (Test-Path $exe)) {
    # To the host: anything left on the pipeline would join the path this function returns.
    dotnet tool install vpk --version $script:VpkVersion --tool-path $ToolPath | Out-Host
    Assert-LastExit "dotnet tool install vpk $script:VpkVersion"
  }
  return $exe
}

# release.yml's build job, for one runtime identifier.
function Publish-YaatClient {
  param([string]$Rid, [string]$Version, [string]$ShortSha, [string]$OutDir)
  if (-not $env:LMKIT_LICENSE_KEY) { Write-Warning 'LMKIT_LICENSE_KEY is not set: this build runs LM-Kit as Community Edition.' }
  dotnet publish src/Yaat.Client -c Release -r $Rid --self-contained `
    -p:PublishSingleFile=true "-p:InformationalVersion=$Version+$ShortSha" "-p:LmKitLicenseKey=$env:LMKIT_LICENSE_KEY" -o $OutDir
  Assert-LastExit "dotnet publish -r $Rid"
}
