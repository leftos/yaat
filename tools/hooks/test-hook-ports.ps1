#Requires -Version 7.0
$ErrorActionPreference = 'Stop'

# Characterization test for the PowerShell ports of the prek hooks. It runs the
# original bash whitespace hook on one copy of every fixture and the PowerShell
# port on another, then compares the resulting bytes, the staged paths and the
# auto-stage report. Once the bash original is gone it checks the port against
# the expected bytes alone.
#
# Usage: pwsh -NoProfile -File tools/hooks/test-hook-ports.ps1

$hooksDir = $PSScriptRoot
$gitBash = 'C:\Program Files\Git\bin\bash.exe'
$oldScript = Join-Path $hooksDir 'whitespace-fix-autostage.sh'
$newScript = Join-Path $hooksDir 'whitespace-fix-autostage.ps1'
$failures = [System.Collections.Generic.List[string]]::new()

$fixtures = [ordered]@{
    'trailing.txt'     = [byte[]]@(0x61, 0x20, 0x20, 0x0A, 0x62, 0x09, 0x0A)
    'crlf.txt'         = [byte[]]@(0x61, 0x0D, 0x0A, 0x62, 0x0D, 0x0A)
    'noeol.txt'        = [byte[]]@(0x61, 0x62, 0x63)
    'manyeol.txt'      = [byte[]]@(0x61, 0x62, 0x63, 0x0A, 0x0A, 0x0A)
    'wsonly.txt'       = [byte[]]@(0x20, 0x20, 0x0A, 0x09, 0x0A)
    'nlonly.txt'       = [byte[]]@(0x0A, 0x0A)
    'empty.txt'        = [byte[]]@()
    'binary.bin'       = [byte[]]@(0x61, 0x00, 0x62, 0x20, 0x20, 0x0A)
    'clean.txt'        = [byte[]]@(0x6F, 0x6B, 0x0A)
    'bom.txt'          = [byte[]]@(0xEF, 0xBB, 0xBF, 0x78, 0x20, 0x0A)
    'space name.txt'   = [byte[]]@(0x79, 0x20, 0x0A)
    'ff.txt'           = [byte[]]@(0x7A, 0x0A, 0x0C, 0x0A)
    'spaces-noeol.txt' = [byte[]]@(0x20, 0x20, 0x20)
    'tab-noeol.txt'    = [byte[]]@(0x78, 0x09)
}

# The bytes the hook's rules demand. `crlf.txt` and `nlonly.txt` are unchanged
# and unstaged: Git's clean filter erases a CRLF-only difference before the hash
# comparison, and a file of only newlines never passes the `grep -Iq .` gate.
# `spaces-noeol.txt` ends empty and staged: sed writes no newline after an
# unterminated line, so the fixed result is 0 bytes and the `[ -s ]` gate skips
# the perl step. `tab-noeol.txt` is its non-empty neighbour, which keeps `x`.
$expectedBytes = [ordered]@{
    'crlf.txt'         = [byte[]]@(0x61, 0x0D, 0x0A, 0x62, 0x0D, 0x0A)
    'nlonly.txt'       = [byte[]]@(0x0A, 0x0A)
    'wsonly.txt'       = [byte[]]@(0x0A)
    'noeol.txt'        = [byte[]]@(0x61, 0x62, 0x63, 0x0A)
    'empty.txt'        = [byte[]]@()
    'binary.bin'       = [byte[]]@(0x61, 0x00, 0x62, 0x20, 0x20, 0x0A)
    'clean.txt'        = [byte[]]@(0x6F, 0x6B, 0x0A)
    'spaces-noeol.txt' = [byte[]]@()
    'tab-noeol.txt'    = [byte[]]@(0x78, 0x0A)
}

function Add-Failure {
    param([string]$Fixture, [string]$What)
    $script:failures.Add("FAIL ${Fixture}: $What")
}

function Test-BytesEqual {
    param([byte[]]$Left, [byte[]]$Right)
    if ($Left.Length -ne $Right.Length) {
        return $false
    }

    for ($i = 0; $i -lt $Left.Length; $i++) {
        if ($Left[$i] -ne $Right[$i]) {
            return $false
        }
    }

    return $true
}

function Test-SameSet {
    param([string[]]$Left, [string[]]$Right)
    $leftText = (@($Left) | Sort-Object) -join "`n"
    $rightText = (@($Right) | Sort-Object) -join "`n"
    return $leftText -eq $rightText
}

function Invoke-Git {
    param([string]$Repo, [string[]]$Arguments)
    $output = & git -C $Repo @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw ("git {0} failed in {1}: {2}" -f ($Arguments -join ' '), $Repo, ($output -join ' '))
    }
}

function New-TempRepo {
    $path = Join-Path ([IO.Path]::GetTempPath()) ("hookport-" + [guid]::NewGuid().ToString())
    New-Item -ItemType Directory -Path $path | Out-Null
    Invoke-Git $path @('init', '-q')
    Invoke-Git $path @('config', 'core.autocrlf', 'false')
    return $path
}

function Write-Fixtures {
    param([string]$Repo)
    foreach ($name in $script:fixtures.Keys) {
        foreach ($copy in @('a', 'b')) {
            $dir = Join-Path $Repo $copy
            if (-not (Test-Path -LiteralPath $dir)) {
                New-Item -ItemType Directory -Path $dir | Out-Null
            }

            [IO.File]::WriteAllBytes((Join-Path $dir $name), $script:fixtures[$name])
        }
    }

    Invoke-Git $Repo @('add', '-A')
    $identity = @('-c', 'user.name=HookPort', '-c', 'user.email=hookport@example.invalid')
    Invoke-Git $Repo ($identity + @('commit', '-q', '-m', 'fixtures'))
}

function Invoke-HookCommand {
    param([string[]]$Command, [string[]]$Paths, [string]$ErrorFile)
    $exe = $Command[0]
    $arguments = @($Command | Select-Object -Skip 1) + $Paths
    & $exe @arguments 2> $ErrorFile | Out-Null
    return $LASTEXITCODE
}

function Get-StagedNames {
    param([string]$Repo, [string]$Prefix)
    $raw = (& git -C $Repo diff --cached --name-only -z) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "git diff --cached failed in $Repo"
    }

    $names = @()
    foreach ($entry in ($raw -split "`0")) {
        if ($entry.StartsWith($Prefix)) {
            $names += $entry.Substring($Prefix.Length)
        }
    }

    return $names
}

function Get-AutoStagedNames {
    param([string]$ErrorFile, [string]$Prefix)
    $lines = @(Get-Content -LiteralPath $ErrorFile -ErrorAction SilentlyContinue)
    $names = @()
    foreach ($line in @($lines | Select-Object -Skip 1)) {
        $trimmed = $line.Trim()
        if ($trimmed.StartsWith($Prefix)) {
            $names += $trimmed.Substring($Prefix.Length)
        } elseif ($trimmed -ne '') {
            $names += $trimmed
        }
    }

    return $names
}

$repo = New-TempRepo
try {
    Write-Fixtures $repo
    $oldExists = Test-Path -LiteralPath $oldScript
    $aPaths = @($fixtures.Keys | ForEach-Object { "a/$_" })
    $bPaths = @($fixtures.Keys | ForEach-Object { "b/$_" })
    $newError = Join-Path $repo 'hook-new.err'
    $oldError = Join-Path $repo 'hook-old.err'
    Push-Location -LiteralPath $repo
    try {
        $newExit = Invoke-HookCommand @('pwsh', '-NoProfile', '-File', $newScript) $bPaths $newError
        $oldExit = $null
        if ($oldExists) {
            $oldExit = Invoke-HookCommand @($gitBash, ($oldScript -replace '\\', '/')) $aPaths $oldError
        }
    } finally {
        Pop-Location
    }

    if (-not $oldExists) {
        Write-Output 'old script absent: expected-bytes checks only'
    } elseif ($oldExit -ne 0) {
        Add-Failure 'old script' "exit $oldExit"
    }

    if ($newExit -ne 0) {
        Add-Failure 'new script' "exit $newExit"
    }

    $changed = @()
    foreach ($name in $fixtures.Keys) {
        $actual = [IO.File]::ReadAllBytes((Join-Path $repo "b/$name"))
        if (-not (Test-BytesEqual $actual $fixtures[$name])) {
            $changed += $name
        }

        if ($expectedBytes.Contains($name) -and (-not (Test-BytesEqual $actual $expectedBytes[$name]))) {
            $hex = [BitConverter]::ToString($actual)
            $want = [BitConverter]::ToString($expectedBytes[$name])
            Add-Failure $name "bytes $hex, expected $want"
        }
    }

    $stagedB = Get-StagedNames $repo 'b/'
    $newNames = Get-AutoStagedNames $newError 'b/'
    $newLines = @(Get-Content -LiteralPath $newError -ErrorAction SilentlyContinue)
    if ($changed.Count -gt 0) {
        $header = ''
        if ($newLines.Count -gt 0) {
            $header = $newLines[0]
        }

        if ($header -ne 'Auto-staged whitespace fixes in:') {
            Add-Failure '(report)' "header missing, first line: ${header}"
        }
    }

    if (-not (Test-SameSet $stagedB $changed)) {
        Add-Failure '(staging)' "b side staged ($($stagedB -join ', ')), changed ($($changed -join ', '))"
    }

    if (-not (Test-SameSet $newNames $changed)) {
        Add-Failure '(report)' "new script reported ($($newNames -join ', ')), changed ($($changed -join ', '))"
    }

    if ($oldExists) {
        foreach ($name in $fixtures.Keys) {
            $aBytes = [IO.File]::ReadAllBytes((Join-Path $repo "a/$name"))
            $bBytes = [IO.File]::ReadAllBytes((Join-Path $repo "b/$name"))
            if (-not (Test-BytesEqual $aBytes $bBytes)) {
                $aHex = [BitConverter]::ToString($aBytes)
                $bHex = [BitConverter]::ToString($bBytes)
                Add-Failure $name "b side $bHex, bash $aHex"
            }
        }

        $stagedA = Get-StagedNames $repo 'a/'
        if (-not (Test-SameSet $stagedA $stagedB)) {
            Add-Failure '(staging)' "bash staged ($($stagedA -join ', ')), port staged ($($stagedB -join ', '))"
        }

        $oldNames = Get-AutoStagedNames $oldError 'a/'
        if (-not (Test-SameSet $oldNames $newNames)) {
            Add-Failure '(report)' "bash reported ($($oldNames -join ', ')), port reported ($($newNames -join ', '))"
        }
    }
} finally {
    if (Test-Path -LiteralPath $repo) {
        Remove-Item -LiteralPath $repo -Recurse -Force
    }
}

if ($failures.Count -eq 0) {
    Write-Output 'PASS'
    exit 0
}

foreach ($failure in $failures) {
    Write-Output $failure
}

exit 1
