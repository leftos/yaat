<#
.SYNOPSIS
  The macOS leg of a local client release: release-macos.yml's build, smoke-intel (its static half)
  and package-macos jobs, run on the Mac builder from the root of a release worktree.

.DESCRIPTION
  Publishes Yaat.Client for osx-arm64 and osx-x64, checks that every shipped osx-x64 native carries
  an x86_64 slice, and packs, signs and notarizes both with vpk into .tmp/release-local/out/macos.
  The LM-Kit transcribe of smoke-intel needs an Intel Mac and is skipped.

  The signing material arrives as forwarded variables named as release-macos.yml's secrets. They are
  decoded into a temporary keychain, which is added to the user's keychain search list beside the
  existing ones; the list is put back, the keychain deleted and the decoded files removed whatever
  happens.

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

$secretNames = 'MACOS_DEVID_APP_CERT_P12_BASE64', 'MACOS_DEVID_INSTALLER_CERT_P12_BASE64', 'MACOS_DEVID_CERT_PASSWORD',
  'MACOS_NOTARY_API_KEY_P8_BASE64', 'MACOS_NOTARY_API_KEY_ID', 'MACOS_NOTARY_API_ISSUER_ID',
  'MACOS_SIGN_APP_IDENTITY', 'MACOS_SIGN_INSTALL_IDENTITY'
$missing = @($secretNames | Where-Object { -not [Environment]::GetEnvironmentVariable($_) })
if ($missing.Count -gt 0) { throw "missing forwarded signing variables: $($missing -join ', ')" }

$work = Join-Path (Get-Location) '.tmp/release-local'
$out = Join-Path $work 'out/macos'
$secrets = Join-Path $work 'signing'
foreach ($dir in $out, (Join-Path $work 'publish'), $secrets) {
  if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}
New-Item -ItemType Directory -Force -Path $out, $secrets | Out-Null
$vpk = Install-Vpk (Join-Path $work 'vpk')

# release-macos.yml's smoke-intel, static half: the x64 build must not ship an arm64-only native.
function Assert-X64Slice([string]$PublishDir) {
  $exempt = 'LM-Kit.ggml.backend.metal.dylib', 'LM-Kit.onnxruntime.dylib'
  if ((file (Join-Path $PublishDir 'Yaat.Client')) -notmatch 'executable x86_64') { throw 'Yaat.Client (osx-x64) is not an x86_64 executable' }
  $bad = foreach ($lib in Get-ChildItem $PublishDir -Filter '*.dylib') {
    if ($exempt -contains $lib.Name) { continue }
    $archs = lipo -archs $lib.FullName
    if ($archs -notmatch 'x86_64') { "$($lib.Name) ($archs)" }
  }
  if ($bad) { throw "osx-x64 natives without an x86_64 slice: $($bad -join ', ')" }
  Write-Host 'osx-x64: every shipped native carries an x86_64 slice (the LM-Kit Intel transcribe is skipped: no Intel Mac)'
}

function Build-AppIcon([string]$Dir) {
  $iconset = Join-Path $Dir 'icon.iconset'
  New-Item -ItemType Directory -Force -Path $iconset | Out-Null
  foreach ($size in 16, 32, 64, 128, 256, 512) {
    $double = $size * 2
    sips -z $size $size src/Yaat.Client/Resources/icon.png --out (Join-Path $iconset "icon_${size}x${size}.png") | Out-Null
    Assert-LastExit "sips $size"
    sips -z $double $double src/Yaat.Client/Resources/icon.png --out (Join-Path $iconset "icon_${size}x${size}@2x.png") | Out-Null
    Assert-LastExit "sips $double"
  }
  iconutil -c icns $iconset -o (Join-Path $Dir 'icon.icns')
  Assert-LastExit 'iconutil'
  return (Join-Path $Dir 'icon.icns')
}

function Build-InfoPlist([string]$Dir) {
  $short = ($Version -split '-')[0]
  $plist = (Get-Content build/macos/Info.plist.template -Raw) -replace '__SHORT_VERSION__', $short -replace '__BUNDLE_VERSION__', $Version
  $path = Join-Path $Dir 'Info.plist'
  [IO.File]::WriteAllText($path, $plist)
  return $path
}

# Always an array, even of one keychain: a lone string splatted into `security list-keychains -s`
# would pass each character as a keychain path and wreck the user's search list.
function Get-UserKeychainList {
  $list = @(security list-keychains -d user | ForEach-Object { $_.Trim().Trim('"') } | Where-Object { $_ })
  $missing = @($list | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
  if (($list.Count -eq 0) -or ($missing.Count -gt 0)) { throw "unexpected user keychain search list ($($list -join ', ')); fix it before signing" }
  return , $list
}

# release-macos.yml's signing import, into a keychain added beside the user's (the workflow replaces
# the search list, which is harmless only on a throwaway runner).
function Import-SigningIdentity([string]$Keychain, [string[]]$KeychainList) {
  $keychainPassword = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
  $appCert = Join-Path $secrets 'devid_app.p12'
  $installerCert = Join-Path $secrets 'devid_installer.p12'
  $notaryKey = Join-Path $secrets 'notary_key.p8'
  [IO.File]::WriteAllBytes($appCert, [Convert]::FromBase64String($env:MACOS_DEVID_APP_CERT_P12_BASE64))
  [IO.File]::WriteAllBytes($installerCert, [Convert]::FromBase64String($env:MACOS_DEVID_INSTALLER_CERT_P12_BASE64))
  [IO.File]::WriteAllBytes($notaryKey, [Convert]::FromBase64String($env:MACOS_NOTARY_API_KEY_P8_BASE64))

  security create-keychain -p $keychainPassword $Keychain
  Assert-LastExit 'security create-keychain'
  security set-keychain-settings -lut 21600 $Keychain
  Assert-LastExit 'security set-keychain-settings'
  security unlock-keychain -p $keychainPassword $Keychain
  Assert-LastExit 'security unlock-keychain'
  foreach ($cert in $appCert, $installerCert) {
    security import $cert -P $env:MACOS_DEVID_CERT_PASSWORD -A -t cert -f pkcs12 -k $Keychain
    Assert-LastExit "security import $(Split-Path -Leaf $cert)"
  }
  security set-key-partition-list -S 'apple-tool:,apple:' -k $keychainPassword $Keychain | Out-Null
  Assert-LastExit 'security set-key-partition-list'
  security list-keychains -d user -s $Keychain @KeychainList
  Assert-LastExit 'security list-keychains'
  xcrun notarytool store-credentials velopack-profile --key $notaryKey `
    --key-id $env:MACOS_NOTARY_API_KEY_ID --issuer $env:MACOS_NOTARY_API_ISSUER_ID --keychain $Keychain
  Assert-LastExit 'xcrun notarytool store-credentials'
  Remove-Item $appCert, $installerCert, $notaryKey -Force
}

foreach ($rid in 'osx-arm64', 'osx-x64') {
  Publish-YaatClient -Rid $rid -Version $Version -ShortSha $ShortSha -OutDir (Join-Path $work "publish/$rid")
}
Assert-X64Slice (Join-Path $work 'publish/osx-x64')
$icns = Build-AppIcon $secrets
$plist = Build-InfoPlist $secrets
$entitlements = (Resolve-Path build/macos/Yaat.Client.entitlements).Path

$keychain = Join-Path $secrets 'app-signing.keychain-db'
$originalList = Get-UserKeychainList
try {
  Import-SigningIdentity $keychain $originalList
  foreach ($rid in 'osx-arm64', 'osx-x64') {
    # `stapler staple` can fail just after an accepted notarization, before Apple's ticket has
    # propagated; vpk does not retry, so the whole pack is repeated.
    foreach ($attempt in 1..3) {
      & $vpk pack --packId YaatClient --packVersion $Version --packDir (Join-Path $work "publish/$rid") `
        --mainExe Yaat.Client --packTitle YAAT --icon $icns --channel $rid --runtime $rid --plist $plist --outputDir $out `
        --signAppIdentity $env:MACOS_SIGN_APP_IDENTITY --signInstallIdentity $env:MACOS_SIGN_INSTALL_IDENTITY `
        --signEntitlements $entitlements --notaryProfile velopack-profile --keychain $keychain -x
      if (($LASTEXITCODE -eq 0) -or ($attempt -eq 3)) { break }
      Write-Warning "vpk pack ($rid) failed (exit $LASTEXITCODE), attempt $attempt of 3; retrying in 60 s"
      Start-Sleep -Seconds 60
    }
    Assert-LastExit "vpk pack ($rid)"
  }
}
finally {
  security list-keychains -d user -s @originalList
  if (Test-Path $keychain) { security delete-keychain $keychain }
  Remove-Item -Recurse -Force $secrets -ErrorAction SilentlyContinue
  $restoredList = Get-UserKeychainList
  if (Compare-Object $originalList $restoredList) { Write-Warning "the user keychain search list differs from before the run: $($restoredList -join ', ')" }
}

Get-ChildItem $out -File | ForEach-Object { '{0,-60} {1,14:N0} bytes' -f $_.Name, $_.Length }
