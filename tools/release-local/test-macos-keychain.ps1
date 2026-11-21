<#
.SYNOPSIS
  A probe of macos-keychain.ps1 on the Mac builder, run inside gate's mac-signing lock.

.DESCRIPTION
  Creates two scratch keychains, A (this repo's, under a folder whose name has a space) and B
  (another repo's), and checks, in pack-macos.ps1's order, that `security create-keychain` then
  Add-UserKeychain leaves A once and first with a missing entry pruned; that another run adding B on
  top is kept; that Remove-UserKeychain removes A alone; that removing A from a list holding only A
  falls back to the login keychain; and that removing a keychain not in the list changes nothing.
  The starting list is set back and both keychains deleted whatever happens, and the restored list is
  checked. Prints "keychain probe: PASS" when every check holds. The branch where the login keychain
  is missing is not covered: it would need the builder's login keychain deleted.
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-local-common.ps1')
. (Join-Path $PSScriptRoot 'macos-keychain.ps1')

function Assert-KeychainList([string]$What, [string[]]$Expected) {
  $actual = Get-UserKeychainList
  if (($actual -join "`n") -cne ($Expected -join "`n")) {
    throw "${What}: expected [$($Expected -join ', ')], got [$($actual -join ', ')]"
  }
  Write-Host "${What}: ok"
}

function Get-ExistingKeychain([string[]]$List) {
  return , @($List | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
}

function New-ScratchKeychain([string]$Path) {
  security create-keychain -p ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))) $Path
  Assert-LastExit "security create-keychain $Path"
}

# Under the run folder rather than $TMPDIR: security reports /var paths as /private/var, which would
# not compare equal to the paths given.
$dirs = (Join-Path (Get-Location) '.tmp/kc probe'), (Join-Path (Get-Location) '.tmp/kc-probe')
New-Item -ItemType Directory -Force -Path $dirs | Out-Null
$a = Join-Path $dirs[0] 'probe a.keychain-db'
$b = Join-Path $dirs[1] 'probe-b.keychain-db'
$ghost = Join-Path $dirs[1] 'missing.keychain-db'
$login = Join-Path $HOME 'Library/Keychains/login.keychain-db'
$start = Get-UserKeychainList
$existing = Get-ExistingKeychain $start
Write-Host "starting list: [$($start -join ', ')]"
$restoreError = $null
try {
  $withGhost = @($start) + @($ghost)
  security list-keychains -d user -s @withGhost
  Assert-LastExit 'security list-keychains -s (start plus a missing keychain)'

  # pack-macos.ps1's order: create-keychain (which may add A itself), then Add-UserKeychain.
  New-ScratchKeychain $a
  Add-UserKeychain $a
  Assert-KeychainList 'after create-keychain and Add-UserKeychain A' (@($a) + $existing)

  # The other repo's run adds B on top of the list as it finds it.
  New-ScratchKeychain $b
  $current = Get-UserKeychainList
  $current = @($current | Where-Object { $_ -ne $b })
  security list-keychains -d user -s $b @current
  Assert-LastExit 'security list-keychains -s (the other repo adds B)'
  Assert-KeychainList 'after the other repo adds B' (@($b, $a) + $existing)

  Remove-UserKeychain $a
  Assert-KeychainList 'after Remove-UserKeychain A' (@($b) + $existing)

  $onlyA = @($a)
  security list-keychains -d user -s @onlyA
  Assert-LastExit 'security list-keychains -s (only A)'
  Remove-UserKeychain $a
  Assert-KeychainList 'after Remove-UserKeychain A from a list of only A' @($login)

  Remove-UserKeychain $a
  Assert-KeychainList 'after Remove-UserKeychain A when A is not listed' @($login)
}
finally {
  security list-keychains -d user -s @start
  if ($LASTEXITCODE -ne 0) { $restoreError = "restoring the starting list failed (exit $LASTEXITCODE)" }
  foreach ($keychain in $a, $b) {
    if (Test-Path -LiteralPath $keychain) { security delete-keychain $keychain }
  }
  Remove-Item -Recurse -Force $dirs -ErrorAction SilentlyContinue
  if (-not $restoreError) {
    $restored = Get-ExistingKeychain (Get-UserKeychainList)
    Write-Host "list set back to: [$($restored -join ', ')]"
    if (($restored -join "`n") -cne ($existing -join "`n")) {
      $restoreError = "the list set back is [$($restored -join ', ')], not the starting [$($existing -join ', ')]"
    }
  }
  if ($restoreError) { Write-Warning $restoreError }
}
if ($restoreError) { throw $restoreError }
Write-Host 'keychain probe: PASS'
