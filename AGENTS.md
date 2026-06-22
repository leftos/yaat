# YAAT Codex Instructions

`CLAUDE.md` is the canonical project instruction file. Read it first and treat it as the source of truth for YAAT architecture, build/test commands, review gates, and workflow rules. This file only maps those rules into Codex-native behavior.

## Canonical Sources

- Project rules: `CLAUDE.md`
- Project skills: `.claude/skills/`
- Claude agent source prompts: `.claude/agents/`
- Claude command source prompts: `.claude/commands/`
- Local reference material: `.claude/reference/`

Do not move or fork those sources casually. Codex wrappers should point back to the canonical Claude files when a Claude-only feature needs a Codex-native entrypoint.

## YAAT Rules To Preserve

- Run from the YAAT repo root; the sibling server repo is `..\yaat-server`.
- Do not inspect, print, summarize, copy, or expose secrets or credential files such as `.env`, `.key`, `.pem`, `.pfx`, `.p12`, `credentials`, or `secrets`.
- Tooling or scripts may load environment variables from those files into the process environment when needed for local commands, as long as secret values are not displayed in tool output, included in prompts, written to logs, committed, or otherwise surfaced to the agent/user conversation.
- Prefer loading only the specific variables needed for the task. Verify secret presence with boolean checks only; never echo or print secret values.
- Do not perform unsafe deletes or resets. Never use `git reset --hard` or broad checkout/revert commands unless explicitly requested.
- Tee all `dotnet build`, `dotnet test`, and `dotnet run` output into `.tmp/`.
- Wrap `dotnet test` with a timeout to catch hangs.
- Do not run bare `dotnet format`; use the configured hooks or project-approved formatting commands from `CLAUDE.md`.
- Never pass `-q`, `-v q`, `--nologo`, or extra quieting flags to `dotnet format`.
- Build after edits with warnings as errors: `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log`.
- For broad verification, prefer `pwsh tools/test-all.ps1` because it covers both YAAT and `yaat-server`.
- Keep `docs/architecture.md` current before commits.

## Claude Concepts In Codex

- Claude skills that are format-compatible are linked into Codex by `tools/setup-codex.ps1`.
- Claude agents do not map to user-defined Codex subagents in this environment. Use the corresponding Codex skill wrappers instead:
  - `.claude/agents/aviation-sim-expert.md` -> `aviation-realism-review`
  - `.claude/agents/csharp-reviewer.md` -> `csharp-review`
  - `.claude/agents/architecture-updater.md` -> `architecture-doc-check`
- Claude slash commands do not map directly. Use the corresponding Codex skill wrapper:
  - `.claude/commands/prepare-release.md` -> `prepare-release`
- Claude hooks are not linked into Codex. Their behavior is restated here and checked by `tools/setup-codex.ps1`.

## Mandatory Aviation Review

Any change touching aviation logic needs the aviation realism review flow from `CLAUDE.md`. Use the `aviation-realism-review` skill and the local FAA references under `.claude/reference/faa/`. Do not web-search for FAA 7110.65 or AIM content that is already available locally.

## Local Setup

Run `tools/setup-codex.ps1 -WhatIf` to preview local Codex setup, then `tools/setup-codex.ps1` to create local skill junctions and register MCP servers. The setup script writes only user-local Codex state and never commits tokens.

Use `tools/codex-yaat.ps1` to launch Codex from the YAAT repo while adding `..\yaat-server` as an extra readable/writable directory. The wrapper intentionally does not set model or reasoning flags so `~/.codex/config.toml` remains authoritative.

## Cursor Cloud specific instructions

### Repository layout

Both repos live under `/agent/repos/` as siblings:
- `/agent/repos/yaat` — client + Yaat.Sim shared library
- `/agent/repos/yaat-server` — ASP.NET Core server (references `../yaat/src/Yaat.Sim/`)

### Building

Both repos use `.slnx` solution format and target `net10.0`. The `wasm-tools` workload is required for the `Yaat.VStrips.Web` project — run `dotnet workload restore` from the yaat repo root if the workload is missing.

Standard build/test commands from `CLAUDE.md` apply. Key commands:
- `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log`
- `timeout 120 dotnet test 2>&1 | tee .tmp/test.log`
- `pwsh tools/test-all.ps1` for cross-repo verification

### Running the server

```bash
cd /agent/repos/yaat-server
dotnet run --project src/Yaat.Server 2>&1 | tee .tmp/server-run.log
```

The dev profile (`launchSettings.json`) binds to **port 5130** (`http://localhost:5130`), not port 5000 (which is the production/Docker default). The client's auto-connect default is `http://localhost:5000`, so when connecting the client, use `http://localhost:5130`.

### Running the client

```bash
cd /agent/repos/yaat
DISPLAY=:1 dotnet run --project src/Yaat.Client
```

The client is an Avalonia desktop GUI app. It requires a display server (`DISPLAY=:1` is available on the Cloud VM). When connecting, set the server URL to `http://localhost:5130`, provide a VATSIM CID (e.g. `1234567`), initials, and ARTCC (e.g. `ZOA`).

### Lint checks

- `dotnet format style --verify-no-changes` — style check (per-repo)
- `dotnet format analyzers --verify-no-changes` — analyzer check (per-repo)
- `csharpier check <repo-path>` — CSharpier formatting check (global tool)

Do NOT run bare `dotnet format`; always use `dotnet format style` or `dotnet format analyzers` separately.
