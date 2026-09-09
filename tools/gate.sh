#!/usr/bin/env bash
# Run a verification gate: full output to a log, only the tail on screen, exit
# with the GATE's status.
#
# Why this exists:
#  - Every line a command prints lands in the agent's context and is re-read on
#    every later turn, so a build or test run prints its last lines here and the
#    rest stays in the log. For more of the output, read or `rg` the log; never
#    re-run the command to see it again.
#  - A shell reports the LAST command's status, so `cmd 2>&1 | tee log | tail`
#    exits 0 even when the build failed. This wrapper exits with the command's
#    own status, and additionally fails when the log holds a known failure
#    marker (a runner can print "Passed!" for stale binaries after
#    "Build FAILED" and still exit 0).
#
# Usage:
#   tools/gate.sh <logfile> <command> [args...]
#   GATE_TAIL=<n>   lines printed on screen (default 20)
#
# Examples:
#   tools/gate.sh .tmp/build.log dotnet build -p:TreatWarningsAsErrors=true
#   tools/gate.sh .tmp/test.log timeout 30 dotnet test tests/Yaat.Sim.Tests -- --filter-class "*Pathfinding*"
#   tools/gate.sh .tmp/test-all.log pwsh tools/test-all.ps1

set -uo pipefail

if [ "$#" -lt 2 ]; then
    echo "usage: tools/gate.sh <logfile> <command> [args...]" >&2
    exit 2
fi

log="$1"
shift

mkdir -p "$(dirname "$log")"

"$@" > "$log" 2>&1
status=$?

markers='^Build FAILED\.|error CS[0-9]+|: error |Test run summary: Failed!|^\s*failed: [1-9]'

# A non-zero exit is conclusive. A zero exit is not: check the log for markers
# that a runner can print while still exiting 0.
if [ "$status" -eq 0 ] && grep -qE "$markers" "$log"; then
    echo "gate: command exited 0 but its log reports a failure -> $log" >&2
    status=1
fi

if [ "$status" -ne 0 ]; then
    grep -nE "$markers" "$log" | head -n 40
fi
tail -n "${GATE_TAIL:-20}" "$log"

if [ "$status" -ne 0 ]; then
    echo "gate: FAILED (status $status). Full output: $log" >&2
else
    echo "gate: passed. Full output: $log"
fi

exit "$status"
