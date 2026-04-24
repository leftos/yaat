#!/usr/bin/env bash
set -euo pipefail

# Generates a grouped changelog from git log between two tags.
# Usage: generate-changelog.sh [PREVIOUS_TAG] [CURRENT_REF]
# If PREVIOUS_TAG is empty or "--", uses the full log up to CURRENT_REF.

PREVIOUS_TAG="${1:-}"
CURRENT_REF="${2:-HEAD}"

if [[ "$PREVIOUS_TAG" == "--" || -z "$PREVIOUS_TAG" ]]; then
    RANGE="$CURRENT_REF"
else
    RANGE="${PREVIOUS_TAG}..${CURRENT_REF}"
fi

# Collect commits grouped by prefix. Note: the array name "GROUPS" collides with
# a readonly bash variable (current user's supplementary group IDs) — use BY_TYPE.
declare -A BY_TYPE
BY_TYPE=(
    [features]=""
    [fixes]=""
    [refactoring]=""
    [docs]=""
    [tests]=""
    [other]=""
)

while IFS= read -r line; do
    [[ -z "$line" ]] && continue

    subject="${line}"

    case "$subject" in
        feat:*|feat\(*|add:*|add\(*)
            BY_TYPE[features]+="- ${subject}"$'\n'
            ;;
        fix:*|fix\(*)
            BY_TYPE[fixes]+="- ${subject}"$'\n'
            ;;
        ref:*|ref\(*)
            BY_TYPE[refactoring]+="- ${subject}"$'\n'
            ;;
        docs:*|docs\(*)
            BY_TYPE[docs]+="- ${subject}"$'\n'
            ;;
        test:*|test\(*)
            BY_TYPE[tests]+="- ${subject}"$'\n'
            ;;
        *)
            BY_TYPE[other]+="- ${subject}"$'\n'
            ;;
    esac
done < <(git log --format="%s (%h)" "$RANGE" --)

output=""

if [[ -n "${BY_TYPE[features]}" ]]; then
    output+="### Features"$'\n'"${BY_TYPE[features]}"$'\n'
fi
if [[ -n "${BY_TYPE[fixes]}" ]]; then
    output+="### Bug Fixes"$'\n'"${BY_TYPE[fixes]}"$'\n'
fi
if [[ -n "${BY_TYPE[refactoring]}" ]]; then
    output+="### Refactoring"$'\n'"${BY_TYPE[refactoring]}"$'\n'
fi
if [[ -n "${BY_TYPE[docs]}" ]]; then
    output+="### Documentation"$'\n'"${BY_TYPE[docs]}"$'\n'
fi
if [[ -n "${BY_TYPE[tests]}" ]]; then
    output+="### Tests"$'\n'"${BY_TYPE[tests]}"$'\n'
fi
if [[ -n "${BY_TYPE[other]}" ]]; then
    output+="### Other"$'\n'"${BY_TYPE[other]}"$'\n'
fi

if [[ -z "$output" ]]; then
    echo "Initial release."
else
    printf '%s' "$output"
fi
