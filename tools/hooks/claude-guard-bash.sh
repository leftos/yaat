#!/usr/bin/env bash
# PreToolUse(Bash) guard for Claude Code. Denies shell commands that violate a
# hard rule in CLAUDE.md and states the fix in the denial message.
#
# Registered from .claude/settings.json (tracked, so contributors and the CI
# Claude workflows get the same guards). Reads the hook payload on stdin and
# writes at most one permission decision to stdout.
#
# Some checks here may also exist in a maintainer's user-level hooks; that
# duplication is intentional — this file has to stand on its own for anyone who
# clones the repo, and a doubled denial is harmless.
# Denial messages quote commands in markdown backticks and must not expand, so
# they are single-quoted on purpose.
# shellcheck disable=SC2016
set -uo pipefail

CMD=$(jq -r '.tool_input.command // empty')
[ -n "$CMD" ] || exit 0

deny() {
    jq -cn --arg reason "$1" \
        '{hookSpecificOutput: {hookEventName: "PreToolUse", permissionDecision: "deny", permissionDecisionReason: $reason}}'
    exit 0
}

# Drop heredoc bodies. A body line like `dotnet format` is file content being
# authored, not a command being run.
strip_heredocs() {
    awk '
        inbody {
            sub(/[[:space:]]+$/, "")
            if ($0 == term) { inbody = 0 }
            next
        }
        {
            if (match($0, /<<-?[[:space:]]*[\047"]?[A-Za-z_][A-Za-z0-9_]*[\047"]?/)) {
                t = substr($0, RSTART, RLENGTH)
                gsub(/^<<-?[[:space:]]*/, "", t)
                gsub(/[\047"]/, "", t)
                term = t
                inbody = 1
            }
            print
        }
    '
}

# Split into pipeline/list segments and strip legitimate wrappers, so a command
# name is only matched in command position. Quoted spans are blanked out first:
# without that, `rg -n 'a|dotnet build' file` splits inside the search pattern
# and the tail looks like a `dotnet build` invocation. Grepping a doc that
# mentions a guarded command must not trip the guard.
segments() {
    printf '%s\n' "$CMD" |
        strip_heredocs |
        sed -E "s/'[^']*'//g; s/\"[^\"]*\"//g" |
        sed -E 's/&&|\|\||;|\|/\n/g' |
        sed -E 's/^[[:space:]]*//' |
        sed -E 's/^(timeout[[:space:]]+[0-9]+[smhd]?|nice([[:space:]]+-n[[:space:]]*-?[0-9]+)?|command|exec)[[:space:]]+//' |
        sed -E 's/^([A-Za-z_][A-Za-z0-9_]*=[^[:space:]]*[[:space:]]+)+//'
}

# True when some segment *starts* with a match for the given extended regex.
starts_with() { segments | grep -qE "^$1([[:space:]]|$)"; }

# True when the whole command line matches (for flags, redirects, pipes).
line_has() { printf '%s' "$CMD" | grep -qE "$1"; }

if starts_with 'prek[[:space:]]+run[[:space:]]+.*(--all-files|-a)'; then
    deny 'Never `prek run --all-files`: it runs over the whole tree instead of the staged set, and its stash/reapply can drop unstaged work. Use bare `prek run`, or let the hook fire on `git commit`.'
fi

if starts_with 'dotnet[[:space:]]+format' && ! line_has 'dotnet[[:space:]]+format[[:space:]]+(style|analyzers|whitespace)([[:space:]]|$)'; then
    deny 'Do NOT run bare `dotnet format` — its whitespace rules fight with CSharpier. Run `dotnet format style` or `dotnet format analyzers` separately, or just let prek run them.'
fi

if starts_with 'dotnet' && line_has '[[:space:]](-q|--nologo|-v[[:space:]]*q)([[:space:]]|$)'; then
    deny 'Never pass -q / -v q / --nologo to a dotnet command — it suppresses output and causes spurious errors. Drop the flag.'
fi

if starts_with 'dotnet[[:space:]]+(test|build|run)' && ! line_has '\|[[:space:]]*tee[[:space:]]'; then
    deny 'dotnet test/build/run must tee output to .tmp/ — add: 2>&1 | tee .tmp/<name>.log. Use a generic name (e.g. .tmp/build.log) unless you will need to compare multiple runs later, then use a unique one.'
fi

if starts_with 'dotnet[[:space:]]+test' && ! line_has '(^|[[:space:]])timeout[[:space:]]+[0-9]'; then
    deny 'dotnet test must be wrapped in a timeout to catch soft hangs — `timeout 30` for a filtered run (--filter ...), `timeout 120` for the full suite.'
fi

exit 0
