#!/usr/bin/env bash
# Run a verification gate from Bash: a shim that checks its three required
# arguments and hands the run to tools/gate.ps1 beside it.
#
# gate.ps1 does the work: the whole output to the log and only the tail on
# screen, the command's own exit status (or 1 when a green run's log holds a
# failure marker), and a watchdog that kills a stalled or runaway command and
# exits 124. Read its help block for the details. gate.ps1 is a copy of the
# canonical ~/.claude/tools/gate/gate.ps1: change it there and run sync-gate.ps1.
#
# The log, the ceiling and the command are all required and have no defaults,
# so a gate never runs without a ceiling. A missing or invalid argument is named
# on stderr with the usage, and the shim exits 2 before anything runs.
#
# Usage:
#   tools/gate.sh <log> <ceiling-seconds> <command> [args...]
#   GATE_TAIL=<n>    lines printed on screen (default 20), gate.ps1 -Tail
#   GATE_STALL=<n>   seconds without progress before a stall kill, gate.ps1 -StallSeconds
#
# Examples:
#   tools/gate.sh .tmp/build.log 300 dotnet build -p:TreatWarningsAsErrors=true
#   tools/gate.sh .tmp/test.log 30 dotnet test tests/Yaat.Sim.Tests -- --filter-class "*Pathfinding*"
#   tools/gate.sh .tmp/test-all.log 900 pwsh tools/test-all.ps1
# From yaat-server: ../yaat/tools/gate.sh .tmp/build.log 300 dotnet build

set -euo pipefail

usage='usage: tools/gate.sh <log> <ceiling-seconds> <command> [args...]'
errors=()

if [ "$#" -lt 1 ] || [ -z "$1" ]; then
    errors+=("gate: missing <log>: every gate writes its whole output to a log, e.g. tools/gate.sh .tmp/test.log 30 dotnet test")
fi

if [ "$#" -lt 2 ] || [ -z "$2" ]; then
    errors+=("gate: missing <ceiling-seconds>: every gate needs a ceiling in seconds (a whole number above 0); there is no default")
elif [ "$2" = timeout ]; then
    errors+=("gate: the ceiling is now the second argument: tools/gate.sh <log> <seconds> <command...>, not 'timeout <n>' inside the command")
elif ! [[ $2 =~ ^0*[1-9][0-9]*$ ]]; then
    errors+=("gate: <ceiling-seconds> must be a whole number above 0, got '$2'")
fi

if [ "$#" -lt 3 ]; then
    errors+=("gate: missing the command after the ceiling")
fi

if [ "${#errors[@]}" -gt 0 ]; then
    printf '%s\n' "${errors[@]}" "$usage" >&2
    exit 2
fi

log="$1"
ceiling="$2"
shift 2

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

options=(-Log "$log" -TimeoutSeconds "$ceiling" -Tail "${GATE_TAIL:-20}")
if [ -n "${GATE_STALL:-}" ]; then
    options+=(-StallSeconds "$GATE_STALL")
fi

exec pwsh -NoProfile -File "$here/gate.ps1" "${options[@]}" -- "$@"
