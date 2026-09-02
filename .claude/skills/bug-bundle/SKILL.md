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

**Time-series of aircraft state, with selectable columns (`--fields`):**
```bash
# default columns: phase / alt / vs / ias / target-speed / assigned-alt / following
python tools/bug_bundle.py track <bundle.zip> --callsigns BXR1960 --start 3050 --end 3130
# heading + navigation debugging preset (hdg, mhdg, trk, bank, target/assigned hdg, turn dir, next fix, off-nose)
python tools/bug_bundle.py track <bundle.zip> --callsigns BXR1960 --fields nav
# pick exact columns, or a preset: default, nav, vert, pos, proc, full
python tools/bug_bundle.py track <bundle.zip> --callsigns N42416 --fields phase,hdg,sid,nextfix,offnose
```
`--fields` shapes only the text table; `--json` always emits every field. Reach for the `nav` preset on "turned the wrong way / didn't follow the SID/STAR" bugs: `offnose` is the bearing to the next nav fix minus true heading (negative = fix is left of the nose), `turn` is the commanded turn direction (L/R), and an empty `nextfix` means no route waypoint is loaded. Field keys: `phase, alt, vs, ias, hdg, mhdg, trk, bank, thdg, ahdg, turn, tgt_spd, aspd, aalt, talt, following, lat, lon, nextfix, offnose, sid, star, deprwy` (`mhdg`/`ahdg` are magnetic; `hdg`/`trk`/`thdg` are true).

**Did any two aircraft collide / taxi through each other? (all-pairs minimum-separation scan):**
```bash
python tools/bug_bundle.py proximity <bundle.zip> 2>&1 | tee .tmp/bb-proximity.log
python tools/bug_bundle.py proximity <bundle.zip> --callsign DAL802 --start 850 --end 930
```
Ranks every on-ground pair by closest approach in feet (interpolated between snapshots, so a fast transient pass-through can't hide between the ~5 s samples), with each aircraft's phase and speed at that moment. A genuine pass-through shows a near-zero minimum at the top; ~100 ft is normal queueing proximity. Run this before assuming a "drove through each other" report is real, and before hand-picking pairs for `track --pair`. `--airborne` includes airborne aircraft (gaps stay lateral-only — a departure climbing over ground traffic shows a small "gap"); `--max-gap-ft` filters; `--top N` (default 20).

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

**Trim a bundle to a shorter time window (in place):**
```bash
python tools/bug_bundle.py trim <bundle.zip> --max-seconds 90
python tools/bug_bundle.py trim <bundle.zip> --max-snapshots 60 --out .tmp/trimmed.zip
```
Drops snapshots past `--max-seconds N` (keeps snapshots whose `ElapsedSeconds <= N`) or keeps only the first `--max-snapshots N` in index order. Actions, scenario, weather, ARTCC config, layouts, and logs are preserved unchanged; the manifest's `Snapshots` index is rewritten to match. With `--out` writes a new file; without `--out` overwrites the input bundle. Use it to:
- Shrink a TestData fixture to just the snapshots needed to reproduce a bug, so the test starts replaying from the relevant time window faster.
- Cut a large recording (50+ MB) into a focused fixture before committing it to `tests/Yaat.Sim.Tests/TestData/`.
- Isolate "pre-bug" state when the recording captures minutes of unrelated taxi/cruise time before the moment of interest. Pair with `history --callsign X` to pick a cutoff just past the symptom.
- Pre-trim before `install --issue N` to keep TestData lean. Always verify the trimmed bundle with `validate` afterwards.

**Before installing a bundle for an issue, check whether the fix already
exists.** The nightly-review bot files a fix PR alongside the issue it opens, so
starting from the issue text alone can duplicate work that is already on a
branch:

```bash
gh issue view <N> --repo leftos/yaat --json title,body,closedByPullRequestsReferences
gh pr list --repo leftos/yaat --search "<N>" --state all
```

If a PR is linked, land it via the `land-bot-pr` skill instead of reimplementing.

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
| `track` | Time-series per callsign across snapshots. Columns via `--fields` (keys or presets `default`/`nav`/`vert`/`pos`/`proc`/`full`; `--json` emits all). Also `--callsigns A B`, `--pair A B`, `--start/--end` |
| `proximity` | All-pairs minimum-separation scan, interpolated between snapshots — ranks pairs by closest approach in feet with phase/speed at that moment (`--callsign X`, `--start/--end`, `--top N`, `--max-gap-ft F`, `--airborne`, `--json`) |
| `actions` | Recorded user actions timeline (`--json`) |
| `history` | Per-callsign chronological events: commands + phase / route / target / approach / track / runway changes (`--callsign X`, `--start/--end`, `--include-global`, `--json`) |
| `live-status` | Live-traffic feed health over the session (`LiveTrafficStatus` actions: wall clock, connected, message age, in-scope count) and the real-world UTC window to slice the SWIM raw log by (`--callsign X` narrows it to that shadow's observations and names the feed facility; `--pad` minutes, `--all`, `--start/--end`). Prints the `swim-slice.ps1` line to run (yaat `docs/live-traffic.md`, *Reproducing a report*) |
| `phases` | Per-callsign phase-transition timeline only (`--callsign X`, `--start/--end`, `--json`) |
| `commands` | Actions filtered to one recipient callsign (`--callsign X`, `--start/--end`, `--json`) |
| `scenario` | Pretty-print `scenario.json.br`. Optional `--aircraft CS [CS ...]` filter and `--show {full,presets,spawns,summary}` (default `full`). |
| `weather` | Print `weather.json` if present |
| `layouts` | List airport IDs, `--airport X` to dump one, `--all --out-dir D` for all |
| `logs` | Extract `yaat-client.log`/`yaat-server.log` to `.tmp/` |
| `trim` | Shrink a bundle by dropping late snapshots (`--max-seconds N` or `--max-snapshots N`, optional `--out`); preserves actions/scenario/weather/logs and rewrites the manifest's snapshot index |
| `install` | Copy into TestData as `[issue{N}-]{desc}-recording[.yaat-bug-report-bundle].zip` (`--issue` optional for local installs), then run yaat-server's `Yaat.RecordingUpgrader` on it in place (current snapshot schema + retired-canonical rewrite such as `HSE` → `HSA`) |
| `validate` | Manifest + Brotli decompression integrity check |

### Tips

- `info` is the first thing to run; it tells you duration, aircraft involved, ARTCC, and whether logs are included.
- For single-aircraft triage, `history --callsign X` is the second thing to run. It collapses 5+ targeted `snapshot --at` calls into one chronological view.
- **Sim-elapsed time comes from `history` / `actions` / `snapshot --at`, never from log wall-clock timestamps.** The client and server logs carry human wall-clock times; replay and snapshots are indexed by sim-elapsed `t=`. A PAUSE/UNPAUSE or sim-rate change (common in instructor recordings) breaks any linear wall-clock→`t` mapping by the total paused time, which the log alone cannot show. For "replay to just before command C on aircraft X", read the `t=NNN CMD …` line from `history --callsign X` and replay to just under that.
- `snapshot --at T` uses the same nearest-at-or-before-T rule as the C# `RecordingArchive.ReadSnapshotAt` — so `--at 60` returns the snapshot whose `ElapsedSeconds` is the largest value ≤ 60.
- Live-traffic actions render with `LIVE` (sample: `via=<facility> utc=<observed>`), `LIVERM` (removal) and `LIVEST` (feed status) tags.
- `history` event tags: `CMD` (action), `PHASES` (chain installed/rebuilt), `PHASE+` (current phase advanced), `PHASE-` (chain cleared), `ROUTE` (NavigationRoute changed), `TGT` (assigned alt/spd/hdg changed), `APPR` (Approach state), `TRACK` (ownership), `RWY` (DestinationRunway), `SPAWN`/`DESPAWN`. Output is ASCII-only (no unicode arrows) so it survives Windows cp1252 stdout.
- `install` upgrades the archive in place via `../yaat-server/tools/Yaat.RecordingUpgrader` (needs the sibling checkout + dotnet; prints `upgraded: … MIGRATED/up-to-date`), then validates it; a post-install validation warning usually means the bundle is truncated. A missing-upgrader warning means the bundle keeps its recorded schema and any retired canonicals — run the upgrader by hand before relying on strip-command replay.
- Output goes to stdout by default (pipeable). Use `--out <path>` to write a file; `logs` always writes files and prints paths.
- `scenario`, `weather`, `artcc-config`, and `layouts` always pretty-print the JSON they emit (indent=2). Falls back to raw text if the payload isn't valid JSON.
