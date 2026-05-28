#!/usr/bin/env bash
set -euo pipefail

# Strips per-line trailing whitespace and normalizes EOF to exactly one
# newline on the files passed by prek, then re-stages any modifications so
# prek doesn't fail the commit on "files were modified by this hook".
#
# Replaces the builtin `trailing-whitespace` and `end-of-file-fixer` hooks,
# which would otherwise apply the same fix but exit non-zero and force a
# re-stage + re-commit cycle.
#
# Usage: whitespace-fix-autostage.sh file1 file2 ...

if [ "$#" -eq 0 ]; then
    exit 0
fi

changed=()
for f in "$@"; do
    [ -f "$f" ] || continue

    # Defensive binary skip — prek's `types = ["text"]` should already filter.
    if ! grep -Iq . "$f" 2>/dev/null; then
        continue
    fi

    orig_hash=$(git hash-object "$f")
    tmp="$f.whitespace-fix.tmp"

    sed -e 's/[ \t\r]*$//' "$f" > "$tmp"
    if [ -s "$tmp" ]; then
        perl -i -0777 -pe 's/\s*\z/\n/' "$tmp"
    fi

    new_hash=$(git hash-object "$tmp")
    if [ "$orig_hash" != "$new_hash" ]; then
        mv "$tmp" "$f"
        changed+=("$f")
    else
        rm "$tmp"
    fi
done

if [ "${#changed[@]}" -gt 0 ]; then
    git add -- "${changed[@]}"
    printf 'Auto-staged whitespace fixes in:\n' >&2
    printf '  %s\n' "${changed[@]}" >&2
fi
