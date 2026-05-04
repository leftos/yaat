#!/usr/bin/env bash
set -euo pipefail

# Runs `dotnet csharpier format` on the files passed by prek, then re-stages
# any modifications so prek doesn't fail the commit on "files were modified by
# this hook". The dotnet-build hook runs last and gates the commit on the
# formatted result still compiling.
#
# Usage: csharpier-wrapper.sh file1.cs file2.cs ...

if [ "$#" -eq 0 ]; then
    exit 0
fi

dotnet csharpier format "$@"
git add -- "$@"
