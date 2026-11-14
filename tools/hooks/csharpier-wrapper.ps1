#Requires -Version 7.0
$ErrorActionPreference = 'Stop'

# Runs `dotnet csharpier format` on the files passed by prek, then re-stages
# any modifications so prek doesn't fail the commit on "files were modified by
# this hook". The pre-push dotnet-build hook gates the push on the
# formatted result still compiling.
#
# Usage: csharpier-wrapper.ps1 file1.cs file2.cs ...

if ($args.Count -eq 0) {
    exit 0
}

dotnet csharpier format @args
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

git add -- @args
exit $LASTEXITCODE
