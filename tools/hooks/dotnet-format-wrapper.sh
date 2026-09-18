#!/usr/bin/env bash
set -euo pipefail

# Runs `dotnet format <subcommand>` scoped to the files passed by prek.
#
# `dotnet format` does not accept positional file arguments — its positional
# slot is a project or solution. To scope it to a file list, we pass them via
# `--include`, which takes a space-separated list of relative paths.
#
# Usage: dotnet-format-wrapper.sh <subcommand> <severity> file1.cs file2.cs ...
#
# Called from prek.toml as:
#   entry = "bash tools/hooks/dotnet-format-wrapper.sh style info"
# prek appends staged filenames, so the script receives them as $3...$N. `style` runs at info, the level the
# CI `--verify-no-changes` check uses; `analyzers` stays at warn.

if [ "$#" -lt 2 ]; then
    echo "usage: $0 <subcommand> <severity> [files...]" >&2
    exit 2
fi

subcommand="$1"
severity="$2"
shift 2

if [ "$#" -eq 0 ]; then
    # No files matched — nothing to format.
    exit 0
fi

# `--include` must come last so its variadic file list consumes to end-of-args.
dotnet format "$subcommand" --severity "$severity" --no-restore --include "$@"

# Re-stage formatter modifications so prek doesn't fail the commit on "files were
# modified by this hook". The dotnet-build hook runs last and gates the commit on
# the formatted result still compiling.
git add -- "$@"
