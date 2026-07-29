#!/usr/bin/env bash
# Regression test for claude-guard-bash.sh.
#
# The guards must deny rule violations *and* stay out of the way otherwise: the
# first cut matched flagged command names anywhere in the string, so grepping a
# doc that mentions `dotnet build`, or writing a heredoc containing one, was
# denied. Cases live in claude-guard-cases.jsonl so no flagged command string
# ever has to appear in the shell command that runs this script.
#
# Usage: bash tools/hooks/test-claude-guards.sh
set -uo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
CASES="$HERE/claude-guard-cases.jsonl"
GUARD="$HERE/claude-guard-bash.sh"

fails=0
while IFS= read -r line; do
    [ -n "$line" ] || continue
    want=$(printf '%s' "$line" | jq -r '.expect')
    label=$(printf '%s' "$line" | jq -r '.cmd' | tr '\n' '~' | cut -c1-52)
    out=$(printf '%s' "$line" | jq -c '{tool_input: {command: .cmd}}' | bash "$GUARD")
    if [ -z "$out" ]; then
        got=allow
    else
        got=$(printf '%s' "$out" | jq -r '.hookSpecificOutput.permissionDecision')
    fi
    if [ "$want" = "$got" ]; then
        printf '  ok   %-6s %s\n' "$got" "$label"
    else
        printf '  FAIL want=%-5s got=%-5s %s\n' "$want" "$got" "$label"
        fails=$((fails + 1))
    fi
done < "$CASES"

printf '\n%s failure(s)\n' "$fails"
[ "$fails" -eq 0 ]
