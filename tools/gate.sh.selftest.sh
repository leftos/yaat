#!/usr/bin/env bash
# Self-test for tools/gate.sh, the Bash shim over tools/gate.ps1: argument
# checks, status pass-through, argument quoting and a run from another directory.
#
# Usage: bash tools/gate.sh.selftest.sh   (from the repository root)
set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"
mkdir -p .tmp
gate=tools/gate.sh
fails=0

ok() { printf 'ok   %s\n' "$1"; }
fail() {
    printf 'FAIL %s\n' "$1"
    fails=$((fails + 1))
}

# expect <name> <status> <stderr-substring or empty> -- <gate.sh args...>
expect() {
    local name="$1" want="$2" text="$3" got
    shift 4
    got=0
    "$gate" "$@" >.tmp/gs.out 2>.tmp/gs.err || got=$?
    if [ "$got" -ne "$want" ]; then
        fail "$name: exit $got, want $want"
    elif [ -n "$text" ] && ! grep -qF -- "$text" .tmp/gs.err; then
        fail "$name: stderr lacks '$text'"
    else
        ok "$name"
    fi
}

expect 'exit 0 passes' 0 '' -- .tmp/gs.log 5 pwsh -NoProfile -c 'exit 0'
if grep -qF 'ceiling 5 s' .tmp/gs.out; then ok 'the verdict shows the ceiling'; else fail 'the verdict lacks "ceiling 5 s"'; fi

expect 'exit 3 passes through' 3 '' -- .tmp/gs.log 5 pwsh -NoProfile -c 'exit 3'
expect 'old timeout form refused' 2 'the ceiling is now the second argument' -- .tmp/gs.log timeout 5 pwsh -c 'exit 0'
expect 'missing ceiling' 2 'gate: missing <ceiling-seconds>' -- .tmp/gs.log
expect 'non-numeric ceiling' 2 "must be a whole number above 0, got 'abc'" -- .tmp/gs.log abc pwsh -c 'exit 0'
expect 'zero ceiling' 2 "must be a whole number above 0, got '0'" -- .tmp/gs.log 0 pwsh -c 'exit 0'
expect 'missing command' 2 'gate: missing the command after the ceiling' -- .tmp/gs.log 5
expect 'missing log' 2 'gate: missing <log>' --

# The script's text is PowerShell and must reach the file unexpanded.
# shellcheck disable=SC2016
printf '%s\n' 'Write-Output "count=$($args.Count)"' '$args | ForEach-Object { Write-Output "[$_]" }' >.tmp/gs-args.ps1
expect 'argument with a space' 0 '' -- .tmp/gs.log 5 pwsh -NoProfile -File .tmp/gs-args.ps1 filter '*X Y*'
if grep -qF 'count=2' .tmp/gs.log && grep -qF '[*X Y*]' .tmp/gs.log; then
    ok 'the spaced argument arrives as one'
else
    fail 'the spaced argument was split or lost'
fi

if (cd .tmp && ../tools/gate.sh gs2.log 5 pwsh -NoProfile -c 'exit 0') >.tmp/gs.out 2>&1; then
    ok 'runs from another directory by relative path'
else
    fail 'run from .tmp by relative path failed'
fi

printf '\n%s failure(s)\n' "$fails"
[ "$fails" -eq 0 ]
