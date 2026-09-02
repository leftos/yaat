#!/usr/bin/env bash
# Run a verification gate, tee its output, and exit with the GATE's status.
#
# Why this exists: CLAUDE.md requires every dotnet/pwsh invocation to be teed to
# .tmp/. That makes each gate a pipeline, and a shell reports the LAST command's
# status — so `dotnet build 2>&1 | tee log | tail` exits 0 even when the build
# failed. A gate that trusts `$?` there reads a failed build as green.
#
# This wrapper tees to the log and exits with the first command's status, then
# additionally fails when the log contains a known failure marker (a runner can
# print "Passed!" for stale binaries after "Build FAILED" and still exit 0).
#
# Usage:
#   tools/gate.sh <logfile> <command> [args...]
#
# Examples:
#   tools/gate.sh .tmp/build.log dotnet build -p:TreatWarningsAsErrors=true
#   tools/gate.sh .tmp/test.log dotnet test tests/Yaat.Sim.Tests -- --filter-class "*Pathfinding*"
#   tools/gate.sh .tmp/test-all.log pwsh tools/test-all.ps1
#
# Never append `| tail` or `| grep` to an invocation of this script: that
# recreates the exact masking it exists to prevent. Read the log file instead.

set -o pipefail

if [ "$#" -lt 2 ]; then
    echo "usage: tools/gate.sh <logfile> <command> [args...]" >&2
    exit 2
fi

log="$1"
shift

mkdir -p "$(dirname "$log")"

"$@" 2>&1 | tee "$log"
status=${PIPESTATUS[0]}

# A non-zero exit is conclusive. A zero exit is not: check the log for markers
# that a runner can print while still exiting 0.
if [ "$status" -eq 0 ]; then
    if grep -qE '^Build FAILED\.|error CS[0-9]+|: error |Test run summary: Failed!|^\s*failed: [1-9]' "$log"; then
        echo "gate: command exited 0 but its log reports a failure -> $log" >&2
        status=1
    fi
fi

if [ "$status" -ne 0 ]; then
    echo "gate: FAILED (status $status). Full output: $log" >&2
else
    echo "gate: passed. Full output: $log"
fi

exit "$status"
