# The user keychain search list on the Mac builder, shared with other repos' signing runs. Dot-sourced
# after release-local-common.ps1 (Assert-LastExit); defines functions only. Callers hold gate's
# mac-signing lock, so no other signing run edits the list between a read and the write after it.

# Always an array, even of one keychain: a lone string splatted into `security list-keychains -s`
# would pass each character as a keychain path and wreck the user's search list.
function Get-UserKeychainList {
  $list = @(security list-keychains -d user | ForEach-Object { $_.Trim().Trim('"') } | Where-Object { $_ })
  Assert-LastExit 'security list-keychains'
  return , $list
}

# Puts $Keychain first in the current list, after pruning entries whose keychain file is gone.
function Add-UserKeychain([string]$Keychain) {
  # Assigned first: the function's comma-wrapped return would reach foreach or a pipe as one item.
  $list = Get-UserKeychainList
  $rest = foreach ($entry in $list) {
    if ($entry -eq $Keychain) { continue }
    if (-not (Test-Path -LiteralPath $entry -PathType Leaf)) {
      Write-Warning "pruned missing keychain from the user search list: $entry"
      continue
    }
    $entry
  }
  $rest = @($rest)
  security list-keychains -d user -s $Keychain @rest
  Assert-LastExit 'security list-keychains -s'
}

# Removes only $Keychain from whatever the list holds now; every other entry stays.
function Remove-UserKeychain([string]$Keychain) {
  $list = Get-UserKeychainList
  $rest = @($list | Where-Object { $_ -ne $Keychain })
  if ($rest.Count -eq $list.Count) {
    Write-Verbose "$Keychain is not in the user keychain search list: nothing to remove"
    return
  }
  if ($rest.Count -eq 0) {
    $login = Join-Path $HOME 'Library/Keychains/login.keychain-db'
    if (-not (Test-Path -LiteralPath $login -PathType Leaf)) {
      Write-Warning "the user keychain search list holds only $Keychain and $login is missing: list left untouched"
      return
    }
    Write-Warning "the user keychain search list held only ${Keychain}: set to $login"
    $rest = @($login)
  }
  security list-keychains -d user -s @rest
  Assert-LastExit 'security list-keychains -s'
}
