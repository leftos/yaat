#!/usr/bin/env bash
# PreToolUse(Read) guard for Claude Code. Blocks reading secrets and credential
# files into context — exposing a secret value in the transcript is a leak, so
# this is a hard denial rather than a prompt.
#
# Parsing such a file for key *names* only (e.g. `rg '^[A-Z_]+='`) stays allowed;
# that goes through Bash, not Read.
set -uo pipefail

FILE=$(jq -r '.tool_input.file_path // empty')
[ -n "$FILE" ] || exit 0

# Deliberately narrow: match secret-bearing extensions and whole basenames, not
# any path merely containing "secrets" — a source file like `Secrets.cs` or a doc
# about credentials is fine to read.
SECRET_RE='\.(env|key|pem|pfx|p12|jks|keystore)$'
SECRET_RE="$SECRET_RE"'|(^|[/\\])\.env([.-][A-Za-z0-9_.-]+)?$'
SECRET_RE="$SECRET_RE"'|(^|[/\\])(credentials|secrets|\.netrc|id_rsa|id_ed25519)(\.(json|ya?ml|txt|ini|conf))?$'
SECRET_RE="$SECRET_RE"'|appsettings\.Local\.json$'

if printf '%s' "$FILE" | grep -qiE "$SECRET_RE"; then
    jq -cn --arg reason "Blocked: reading secrets/credential files into context is not allowed. To learn which variables a file defines, parse it for key names only (e.g. rg '^[A-Z_]+=' <file>) — never the values." \
        '{hookSpecificOutput: {hookEventName: "PreToolUse", permissionDecision: "deny", permissionDecisionReason: $reason}}'
fi

exit 0
