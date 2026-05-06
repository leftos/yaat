---
name: bug-bundle
description: "Inspect, extract, install, and validate YAAT v4 bug bundles. Use when a *.yaat-bug-report-bundle.zip or *-recording.zip path appears in conversation, when triaging a YAAT controller bug report or recording-driven complaint, when correlating in-game behavior with recorded snapshots / actions / logs, or when fetching GitHub issue attachments into tests/Yaat.Sim.Tests/TestData/. Start with `info` for an overview, then `history --callsign X` for per-aircraft chronology."
---

# Bug Bundle Tool

Python CLI that makes v4 bug bundles (`*.yaat-bug-report-bundle.zip`,
`*-recording.zip`) easy to triage, install into TestData, and validate.

Requires `brotli` (`pip install brotli`).

## Usage

When the user attaches a bug bundle, asks about the contents of a recording,
or wants an issue's recording placed into `tests/Yaat.Sim.Tests/TestData/`,
reach for this tool instead of writing throwaway C# or manual unzip scripts.

### Common Queries

**Triage summary (duration, ARTCC, aircraft at t=0):**
```bash
python tools/bug_bundle.py info <bundle.zip> 2>&1 | tee .tmp/bb-info.log
```

**Dump snapshot nearest to a bug time:**
```bash
python tools/bug_bundle.py snapshot <bundle.zip> --at 182 --out .tmp/bb-snap-182.json
```

**Filter snapshot to one aircraft:**
```bash
python tools/bug_bundle.py snapshot <bundle.zip> --at 182 --callsign UAL238 --out .tmp/bb-ual238-182.json
```

**Timeline of recorded user actions:**
```bash
python tools/bug_bundle.py actions <bundle.zip> 2>&1 | tee .tmp/bb-actions.log
```

**Per-callsign chronological story (commands + phase / route / target / approach changes):**
```bash
python tools/bug_bundle.py history <bundle.zip> --callsign N42416 --out .tmp/bb-hist-N42416.log
```
This is usually the first thing to run when triaging a single-aircraft complaint — it shows everything that was issued to and happened to one aircraft in one sweep, so you don't have to walk multiple `snapshot --at` calls.

**Just the phase-transition timeline:**
```bash
python tools/bug_bundle.py phases <bundle.zip> --callsign N9225L --out .tmp/bb-phases-N9225L.log
```

**Just the commands issued to one aircraft:**
```bash
python tools/bug_bundle.py commands <bundle.zip> --callsign N42416 --out .tmp/bb-cmds-N42416.log
```

**One-line summary of every aircraft in the scenario (callsign / type / dep-dest / start / presets):**
```bash
python tools/bug_bundle.py scenario <bundle.zip> --show summary 2>&1 | tee .tmp/bb-scen-summary.log
```

**Preset commands for one or more aircraft:**
```bash
python tools/bug_bundle.py scenario <bundle.zip> --aircraft N346G --show presets
python tools/bug_bundle.py scenario <bundle.zip> --aircraft N346G N172SP --show presets
```

**Starting conditions (parking spot / fix / coordinates) for one or more aircraft:**
```bash
python tools/bug_bundle.py scenario <bundle.zip> --aircraft N346G --show spawns
```

**Full scenario block for one aircraft (everything: type, FP, presets, autotrack, etc.):**
```bash
python tools/bug_bundle.py scenario <bundle.zip> --aircraft N346G
```

**Extract logs to `.tmp/`:**
```bash
python tools/bug_bundle.py logs <bundle.zip>
```

**Install into TestData (local path):**
```bash
python tools/bug_bundle.py install <local.zip> --issue 134 --desc oak-runway-exit
```

**Install with a custom (non-issue-numbered) name:**
```bash
python tools/bug_bundle.py install <local.zip> --desc sa-armed-for-downwind
```
Omitting `--issue` produces `{desc}-recording[.yaat-bug-report-bundle].zip` —
useful when the bundle isn't yet tied to a GitHub issue.

**Install from a GitHub issue (uses `gh`):**
```bash
python tools/bug_bundle.py install --issue 134 --desc oak-runway-exit
```
The GitHub-fetch path still requires `--issue`.

**Format integrity check:**
```bash
python tools/bug_bundle.py validate <bundle.zip>
```

### Subcommands Reference

| Command | Purpose |
|---------|---------|
| `info` | Manifest summary + aircraft callsigns at t=0 (`--json`) |
| `snapshot` | Snapshot nearest to `--at <seconds>`, optional `--callsign X` |
| `track` | Time-series per callsign across snapshots (`--callsigns A B`, `--pair A B`, `--start/--end`, `--json`) |
| `actions` | Recorded user actions timeline (`--json`) |
| `history` | Per-callsign chronological events: commands + phase / route / target / approach / track / runway changes (`--callsign X`, `--start/--end`, `--include-global`, `--json`) |
| `phases` | Per-callsign phase-transition timeline only (`--callsign X`, `--start/--end`, `--json`) |
| `commands` | Actions filtered to one recipient callsign (`--callsign X`, `--start/--end`, `--json`) |
| `scenario` | Pretty-print `scenario.json.br`. Optional `--aircraft CS [CS ...]` filter and `--show {full,presets,spawns,summary}` (default `full`). |
| `weather` | Print `weather.json` if present |
| `layouts` | List airport IDs, `--airport X` to dump one, `--all --out-dir D` for all |
| `logs` | Extract `yaat-client.log`/`yaat-server.log` to `.tmp/` |
| `install` | Copy into TestData as `[issue{N}-]{desc}-recording[.yaat-bug-report-bundle].zip` (`--issue` optional for local installs) |
| `validate` | Manifest + Brotli decompression integrity check |

### Tips

- `info` is the first thing to run; it tells you duration, aircraft involved, ARTCC, and whether logs are included.
- For single-aircraft triage, `history --callsign X` is the second thing to run. It collapses 5+ targeted `snapshot --at` calls into one chronological view.
- `snapshot --at T` uses the same nearest-at-or-before-T rule as the C# `RecordingArchive.ReadSnapshotAt` — so `--at 60` returns the snapshot whose `ElapsedSeconds` is the largest value ≤ 60.
- `history` event tags: `CMD` (action), `PHASES` (chain installed/rebuilt), `PHASE+` (current phase advanced), `PHASE-` (chain cleared), `ROUTE` (NavigationRoute changed), `TGT` (assigned alt/spd/hdg changed), `APPR` (Approach state), `TRACK` (ownership), `RWY` (DestinationRunway), `SPAWN`/`DESPAWN`. Output is ASCII-only (no unicode arrows) so it survives Windows cp1252 stdout.
- `install` validates the archive post-copy; a post-install warning usually means the bundle is truncated.
- Output goes to stdout by default (pipeable). Use `--out <path>` to write a file; `logs` always writes files and prints paths.
- `scenario`, `weather`, `artcc-config`, and `layouts` always pretty-print the JSON they emit (indent=2). Falls back to raw text if the payload isn't valid JSON.
