---
name: bug-bundle
description: "Inspect, extract, install, and validate YAAT v4 bug bundles via tools/bug_bundle.py"
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

**Extract logs to `.tmp/`:**
```bash
python tools/bug_bundle.py logs <bundle.zip>
```

**Install into TestData (local path):**
```bash
python tools/bug_bundle.py install <local.zip> --issue 134 --desc oak-runway-exit
```

**Install from a GitHub issue (uses `gh`):**
```bash
python tools/bug_bundle.py install --issue 134 --desc oak-runway-exit
```

**Format integrity check:**
```bash
python tools/bug_bundle.py validate <bundle.zip>
```

### Subcommands Reference

| Command | Purpose |
|---------|---------|
| `info` | Manifest summary + aircraft callsigns at t=0 (`--json`) |
| `snapshot` | Snapshot nearest to `--at <seconds>`, optional `--callsign X` |
| `actions` | Recorded user actions timeline (`--json`) |
| `scenario` | Decompress `scenario.json.br` |
| `weather` | Print `weather.json` if present |
| `layouts` | List airport IDs, `--airport X` to dump one, `--all --out-dir D` for all |
| `logs` | Extract `yaat-client.log`/`yaat-server.log` to `.tmp/` |
| `install` | Copy into TestData as `issue{N}-{desc}-recording[.yaat-bug-report-bundle].zip` |
| `validate` | Manifest + Brotli decompression integrity check |

### Tips

- `info` is the first thing to run; it tells you duration, aircraft involved, ARTCC, and whether logs are included.
- `snapshot --at T` uses the same nearest-at-or-before-T rule as the C# `RecordingArchive.ReadSnapshotAt` — so `--at 60` returns the snapshot whose `ElapsedSeconds` is the largest value ≤ 60.
- `install` validates the archive post-copy; a post-install warning usually means the bundle is truncated.
- Output goes to stdout by default (pipeable). Use `--out <path>` to write a file; `logs` always writes files and prints paths.
