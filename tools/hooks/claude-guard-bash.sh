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
    local reason=$1
    [ -n "${2:-}" ] && reason="$reason $2"
    jq -cn --arg reason "$reason" \
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

# Legitimate wrappers and prefixes stripped from the front of a segment.
PREFIX_RE='^(timeout[[:space:]]+[0-9]+[smhd]?|nice([[:space:]]+-n[[:space:]]*-?[0-9]+)?|command|exec)[[:space:]]+'
TIMEOUT_RE='^timeout[[:space:]]+[0-9]+[smhd]?[[:space:]]+'
ENV_ASSIGN_RE='^([A-Za-z_][A-Za-z0-9_]*=[^[:space:]]*[[:space:]]+)+'
GATE_PS1_RE='^(pwsh([[:space:]]+-[^[:space:]]+)*[[:space:]]+)?'
GATE_PS1_RE+='[^[:space:]]*gate\.ps1[[:space:]]+'
GATE_PS1_RE+='(([^-[:space:]][^[:space:]]*|-[^-[:space:]][^[:space:]]*)[[:space:]]+)*--[[:space:]]+'
GATE_RUN_RE='^(pwsh([[:space:]]+-[^[:space:]]+)*[[:space:]]+)?'
GATE_RUN_RE+='gate[[:space:]]+run[[:space:]]+'
GATE_RUN_RE+='((--?[A-Za-z][-A-Za-z0-9]*(=[^[:space:]]+)?|[^-[:space:]][^[:space:]]*)[[:space:]]+)*--[[:space:]]+'
TAB=$'\t'

# Emits one "wrapped<TAB>timed<TAB>segment" line for one raw pipeline segment.
# Both flags belong to that segment alone: `wrapped` is 1 when its own wrapper
# captures the output (a `gate.ps1 … --` head, or a `gate run … --log … --`
# head), `timed` is 1 when its own wrapper sets a ceiling (a `timeout N` prefix,
# the gate's -TimeoutSeconds or gate run's --timeout).
emit_segment() {
    local s=$1 head wrapped=0 timed=0
    s=${s#"${s%%[![:space:]]*}"}
    if [[ $s =~ $PREFIX_RE ]]; then
        head=${BASH_REMATCH[0]}
        s=${s:${#head}}
        [[ $head == timeout* ]] && timed=1
    fi
    if [[ $s =~ $GATE_PS1_RE ]]; then
        head=${BASH_REMATCH[0]}
        s=${s:${#head}}
        wrapped=1
        [[ $head =~ -TimeoutSeconds[[:space:]]+[0-9] ]] && timed=1
    fi
    if [[ $s =~ $GATE_RUN_RE ]]; then
        head=${BASH_REMATCH[0]}
        s=${s:${#head}}
        [[ $head =~ (^|[[:space:]])--log([[:space:]]|=) ]] && wrapped=1
        [[ $head =~ (^|[[:space:]])--timeout(=|[[:space:]]+)[0-9] ]] && timed=1
    fi
    if [[ $s =~ $TIMEOUT_RE ]]; then
        s=${s:${#BASH_REMATCH[0]}}
        timed=1
    fi
    if [[ $s =~ $ENV_ASSIGN_RE ]]; then
        s=${s:${#BASH_REMATCH[0]}}
    fi
    printf '%s\t%s\t%s\n' "$wrapped" "$timed" "$s"
}

# Split into pipeline/list segments and strip each one's own legitimate
# wrappers, so a command name is only matched in command position. Quoted spans
# are blanked out first: without that, `rg -n 'a|dotnet build' file` splits
# inside the search pattern and the tail looks like a `dotnet build`
# invocation. Grepping a doc that mentions a guarded command must not trip the
# guard. A span that carries no separator keeps its text with the quotes
# dropped, so a redirect to a quoted `.tmp/x.log` stays visible to the capture
# rule; a span that does carry one is blanked whole. Newlines are folded to \001
# while blanking, so a quoted span that runs over several lines (a commit
# message) is blanked whole instead of leaking its inner lines as segments.
# A `gate run … -- <command>` wrapper is stripped the same way as `gate.ps1`, so
# the gate CLI's inner command is checked in command position too.
segments() {
    printf '%s\n' "$CMD" |
        strip_heredocs |
        tr '\n' '\001' |
        sed -E \
            -e "s/'([^'|;&[:cntrl:]]*)'/\1/g" \
            -e "s/\"([^\"|;&[:cntrl:]]*)\"/\1/g" \
            -e "s/'[^']*'//g" \
            -e "s/\"[^\"]*\"//g" |
        tr '\001' '\n' |
        sed -E 's/&&|\|\||;|\|/\n/g' |
        while IFS= read -r segment; do emit_segment "$segment"; done
}

# Computed once, and every rule below matches with bash's own `[[ =~ ]]`: this
# runs before every Bash tool call, and each process spawn under Git Bash costs
# from tens to hundreds of milliseconds (measured 2026-09-24: the version that
# re-ran the pipeline per rule took 9-28 s a call on a loaded machine).
mapfile -t ROWS < <(segments)
SEGS=()
WRAPPED=()
TIMED=()
for row in "${ROWS[@]}"; do
    WRAPPED+=("${row%%"$TAB"*}")
    row=${row#*"$TAB"}
    TIMED+=("${row%%"$TAB"*}")
    SEGS+=("${row#*"$TAB"}")
done

# True when some segment *starts* with a match for the given extended regex.
starts_with() {
    local re="^$1([[:space:]]|$)" s
    for s in "${SEGS[@]}"; do
        [[ $s =~ $re ]] && return 0
    done
    return 1
}

# True when one segment both starts with $1 and matches $2. A rule that joins a
# command name to a flag must test both over the same segment: over the whole
# line, `git commit -q` beside any dotnet call reads as `dotnet -q`.
segment_has() {
    local head="^$1([[:space:]]|$)" s
    for s in "${SEGS[@]}"; do
        [[ $s =~ $head && $s =~ $2 ]] && return 0
    done
    return 1
}

# True when segment $1 captured its own output: it ran under its own capturing
# wrapper, or its own text redirects to a `.tmp/*.log` file. `| tee` prints the
# whole log and reports tee's status, so it never counts.
captured_at() {
    local redirect='>>?[[:space:]]*["'"'"']?[^[:space:]"'"'"']*\.tmp/[^[:space:]"'"'"']*\.log'
    [ "${WRAPPED[$1]}" = 1 ] || [[ ${SEGS[$1]} =~ $redirect ]]
}

if starts_with 'prek[[:space:]]+run[[:space:]]+.*(--all-files|-a)'; then
    deny 'Never `prek run --all-files`: it runs over the whole tree instead of the staged set, and its stash/reapply can drop unstaged work. Use bare `prek run`, or let the hook fire on `git commit`.'
fi

bare_dotnet_format() {
    local any='^dotnet[[:space:]]+format([[:space:]]|$)' sub='^dotnet[[:space:]]+format[[:space:]]+(style|analyzers|whitespace)([[:space:]]|$)' s
    for s in "${SEGS[@]}"; do
        [[ $s =~ $any && ! $s =~ $sub ]] && return 0
    done
    return 1
}
if bare_dotnet_format; then
    deny 'Do NOT run bare `dotnet format` — its whitespace rules fight with CSharpier. Run `dotnet format style` or `dotnet format analyzers` separately, or just let prek run them.'
fi

if segment_has 'dotnet' '[[:space:]](-q|--nologo|-v[[:space:]]*q)([[:space:]]|$)'; then
    deny 'Never pass -q / -v q / --nologo to a dotnet command — it suppresses output and causes spurious errors. Drop the flag.'
fi

# Every line a command prints lands in the agent's context and is re-read on
# every later turn, so a build or test run writes its full output to a .tmp log
# and prints only the tail; the log is what to read for more, instead of
# re-running. Each segment is judged by its own wrapper: a bare `dotnet test`
# after a captured sibling borrows nothing from it.
first_uncaptured() {
    local i
    for i in "${!SEGS[@]}"; do
        [[ ${SEGS[$i]} =~ ^dotnet[[:space:]]+(test|build|run)([[:space:]]|$) ]] || continue
        captured_at "$i" || { BAD=${SEGS[$i]}; return 0; }
    done
    return 1
}
CAPTURE_MSG='dotnet test/build/run must write its full output to a .tmp/<name>.log and print only the tail: '
CAPTURE_MSG+='`pwsh tools/gate.ps1 -Log .tmp/<name>.log -TimeoutSeconds <seconds> -Slot heavy -- <command...>` '
CAPTURE_MSG+='(from yaat-server: `pwsh ../yaat/tools/gate.ps1`), or `<command> > .tmp/<name>.log 2>&1; '
CAPTURE_MSG+='rc=$?; tail -n 20 .tmp/<name>.log; (exit $rc)`. `| tee` prints the whole log into context '
CAPTURE_MSG+='and hides the exit status. For more of the output, read the log file — never re-run the command.'
if first_uncaptured; then
    deny "$CAPTURE_MSG" "Uncaptured command: \`$BAD\`."
fi

# A ceiling is gate.ps1's -TimeoutSeconds, gate run's --timeout, or a coreutils
# `timeout <n>` on the same segment as the test it bounds.
first_untimed() {
    local i
    for i in "${!SEGS[@]}"; do
        [[ ${SEGS[$i]} =~ ^dotnet[[:space:]]+test([[:space:]]|$) ]] || continue
        [ "${TIMED[$i]}" = 1 ] || { BAD=${SEGS[$i]}; return 0; }
    done
    return 1
}
TEST_MSG='dotnet test must run under a ceiling to catch soft hangs: '
TEST_MSG+='`pwsh tools/gate.ps1 -Log .tmp/test.log -TimeoutSeconds 30 -Slot heavy -- dotnet test ...` '
TEST_MSG+='for a filtered run (--filter-class ...), `pwsh tools/gate.ps1 -Log .tmp/test.log -TimeoutSeconds 120 '
TEST_MSG+='-Slot heavy -- dotnet test` for the full suite (from yaat-server: `pwsh ../yaat/tools/gate.ps1`). '
TEST_MSG+='The gate kills a hung run and exits 124.'
if first_untimed; then
    deny "$TEST_MSG" "Untimed command: \`$BAD\`."
fi

exit 0
