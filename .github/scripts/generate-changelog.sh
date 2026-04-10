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

# Collect commits grouped by prefix
declare -A GROUPS
GROUPS=(
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
            GROUPS[features]+="- ${subject}"$'\n'
            ;;
        fix:*|fix\(*)
            GROUPS[fixes]+="- ${subject}"$'\n'
            ;;
        ref:*|ref\(*)
            GROUPS[refactoring]+="- ${subject}"$'\n'
            ;;
        docs:*|docs\(*)
            GROUPS[docs]+="- ${subject}"$'\n'
            ;;
        test:*|test\(*)
            GROUPS[tests]+="- ${subject}"$'\n'
            ;;
        *)
            GROUPS[other]+="- ${subject}"$'\n'
            ;;
    esac
done < <(git log --format="%s (%h)" "$RANGE" --)

output=""

if [[ -n "${GROUPS[features]}" ]]; then
    output+="### Features"$'\n'"${GROUPS[features]}"$'\n'
fi
if [[ -n "${GROUPS[fixes]}" ]]; then
    output+="### Bug Fixes"$'\n'"${GROUPS[fixes]}"$'\n'
fi
if [[ -n "${GROUPS[refactoring]}" ]]; then
    output+="### Refactoring"$'\n'"${GROUPS[refactoring]}"$'\n'
fi
if [[ -n "${GROUPS[docs]}" ]]; then
    output+="### Documentation"$'\n'"${GROUPS[docs]}"$'\n'
fi
if [[ -n "${GROUPS[tests]}" ]]; then
    output+="### Tests"$'\n'"${GROUPS[tests]}"$'\n'
fi
if [[ -n "${GROUPS[other]}" ]]; then
    output+="### Other"$'\n'"${GROUPS[other]}"$'\n'
fi

if [[ -z "$output" ]]; then
    echo "Initial release."
else
    printf '%s' "$output"
fi
