#Requires -Version 7.0
$ErrorActionPreference = 'Stop'

# Strips per-line trailing whitespace and normalizes EOF to exactly one
# newline on the files passed by prek, then re-stages any modifications so
# prek doesn't fail the commit on "files were modified by this hook".
#
# Replaces the builtin `trailing-whitespace` and `end-of-file-fixer` hooks,
# which would otherwise apply the same fix but exit non-zero and force a
# re-stage + re-commit cycle.
#
# A file is left alone when it is empty, contains a NUL byte, or holds only
# newlines — the `grep -Iq .` gate the bash original used. A change counts only
# when it survives Git's clean filter, because `git hash-object` applies it: a
# CRLF-only difference is therefore no change at all.
#
# Usage: whitespace-fix-autostage.ps1 file1 file2 ...

function Test-IsLineTailByte {
    # Bytes `sed -e 's/[ \t\r]*$//'` removes before a line's newline.
    param([byte]$Value)
    if ($Value -eq 0x20) { return $true }
    if ($Value -eq 0x09) { return $true }
    return $Value -eq 0x0D
}

function Test-IsWhitespaceByte {
    # Perl's `\s` in byte mode: [ \t\n\x0B\f\r].
    param([byte]$Value)
    if (($Value -eq 0x20) -or ($Value -eq 0x09) -or ($Value -eq 0x0A)) { return $true }
    return ($Value -eq 0x0B) -or ($Value -eq 0x0C) -or ($Value -eq 0x0D)
}

function Test-TextCandidate {
    # The `grep -Iq .` gate: a file with a NUL byte is binary, and a file whose
    # only bytes are newlines has no "." to match, so both are left alone.
    param([byte[]]$Bytes)
    if ($Bytes.Length -eq 0) { return $false }
    if ([Array]::IndexOf($Bytes, [byte]0x00) -ge 0) { return $false }

    foreach ($value in $Bytes) {
        if ($value -ne 0x0A) { return $true }
    }

    return $false
}

function ConvertTo-TrimmedLines {
    # Every line (split on \n, the last one included whether or not it ends with
    # one) loses its trailing spaces, tabs and CRs, and keeps the newline it had:
    # GNU sed writes none after a last line that had none, so a final line of
    # only whitespace leaves an empty result.
    param([byte[]]$Bytes)
    $stream = [System.IO.MemoryStream]::new($Bytes.Length + 1)
    try {
        $lineStart = 0
        while ($lineStart -lt $Bytes.Length) {
            $newline = [Array]::IndexOf($Bytes, [byte]0x0A, $lineStart)
            $lineEnd = $Bytes.Length - 1
            if ($newline -ge 0) {
                $lineEnd = $newline - 1
            }

            while (($lineEnd -ge $lineStart) -and (Test-IsLineTailByte $Bytes[$lineEnd])) {
                $lineEnd--
            }

            $stream.Write($Bytes, $lineStart, $lineEnd - $lineStart + 1)
            if ($newline -lt 0) {
                break
            }

            $stream.WriteByte(0x0A)
            $lineStart = $newline + 1
        }

        return ,$stream.ToArray()
    } finally {
        $stream.Dispose()
    }
}

function ConvertTo-SingleFinalNewline {
    # `perl -0777 -pe 's/\s*\z/\n/'`: the run of trailing whitespace bytes at the
    # end of the file becomes one newline; a whitespace-only file becomes one.
    param([byte[]]$Bytes)
    $end = $Bytes.Length
    while (($end -gt 0) -and (Test-IsWhitespaceByte $Bytes[$end - 1])) {
        $end--
    }

    $result = [byte[]]::new($end + 1)
    [Array]::Copy($Bytes, $result, $end)
    $result[$end] = 0x0A
    return ,$result
}

function Get-GitHash {
    # The bash original decided "changed" with `git hash-object`, whose clean
    # filter makes an eol-only difference invisible. Comparing the same way
    # keeps the port byte-for-byte faithful.
    param([string]$Path)
    $hash = & git hash-object -- $Path
    if ($LASTEXITCODE -ne 0) {
        throw "git hash-object failed for $Path"
    }

    return ([string](@($hash)[0])).Trim()
}

if ($args.Count -eq 0) {
    exit 0
}

$changed = [System.Collections.Generic.List[string]]::new()
foreach ($path in $args) {
    if (-not [System.IO.File]::Exists($path)) { continue }
    $bytes = [System.IO.File]::ReadAllBytes($path)
    if (-not (Test-TextCandidate $bytes)) { continue }

    $fixed = ConvertTo-TrimmedLines $bytes
    if ($fixed.Length -gt 0) {
        $fixed = ConvertTo-SingleFinalNewline $fixed
    }

    $scratch = "$path.whitespace-fix.tmp"
    $originalHash = Get-GitHash $path
    [System.IO.File]::WriteAllBytes($scratch, $fixed)
    $fixedHash = Get-GitHash $scratch
    if ($originalHash -eq $fixedHash) {
        [System.IO.File]::Delete($scratch)
        continue
    }

    [System.IO.File]::Move($scratch, $path, $true)
    $changed.Add($path)
}

if ($changed.Count -gt 0) {
    git add -- @changed
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    [Console]::Error.WriteLine('Auto-staged whitespace fixes in:')
    foreach ($path in $changed) {
        [Console]::Error.WriteLine("  $path")
    }
}

exit 0
