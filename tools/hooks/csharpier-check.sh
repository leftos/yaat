#!/usr/bin/env bash
set -euo pipefail

# Snapshot which files are staged before csharpier runs.
staged=$(git diff --cached --name-only)

# Run csharpier (exit code 0 = no changes, 1 = files formatted).
dotnet csharpier format . || true

# Check what csharpier modified in the working tree.
modified=$(git diff --name-only)

if [ -z "$modified" ]; then
    exit 0
fi

# Every file csharpier touched must already be in the staged set.
# If csharpier reformatted an unrelated file, reject the commit.
fail=0
for f in $modified; do
    if ! echo "$staged" | grep -qxF "$f"; then
        echo "  csharpier reformatted unrelated file: $f" >&2
        fail=1
    fi
done

if [ "$fail" -eq 1 ]; then
    echo "csharpier modified files outside the staged set. Format them in a separate commit." >&2
    git checkout -- . 2>/dev/null || true
    exit 1
fi

# All modified files were already staged — re-stage the formatted versions.
echo "$modified" | xargs git add
