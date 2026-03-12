# Scenario Preset Command Validation

YAAT can validate that all preset commands in an ARTCC's training scenarios parse correctly. This helps ARTCC training staff identify typos or unsupported commands before students encounter them.

Three ways to validate:

1. **CLI tool** — standalone, for offline/CI use
2. **Auto-validate on load** — warnings in the terminal panel when a scenario loads
3. **Batch validation window** — Scenario > Validate Scenarios menu item

## CLI Tool

```bash
# Validate all scenarios for an ARTCC (downloads from vNAS API)
dotnet run --project tools/Yaat.ScenarioValidator -- --artcc ZOA

# Validate a single local scenario file
dotnet run --project tools/Yaat.ScenarioValidator -- --file scenario.json

# Validate all local files in a directory
dotnet run --project tools/Yaat.ScenarioValidator -- --dir path/to/scenarios/

# JSON output for scripting
dotnet run --project tools/Yaat.ScenarioValidator -- --artcc ZOA --json
```

Exit code: 0 if no new failures (known typos don't count), 1 if new failures found.

## Auto-Validate on Load

When a scenario loads in the client, preset commands are validated automatically. Parse failures appear as `[WARN]` entries in the terminal panel. Known typos are reported as `[INFO]` with a count.

## Batch Validation Window

**Scenario > Validate Scenarios** fetches all scenarios for the configured ARTCC from the vNAS data API, validates each one, and displays a report window with:

- Summary: ARTCC name, total scenarios/presets/failure counts
- DataGrid: scenario name, aircraft, command, status (Parse Failed / Known Typo)
- Copy Report button: copies a text report to clipboard for sharing with ARTCC staff

## Test Integration

```bash
# 1. Download scenarios for your ARTCC
python tools/refresh-scenarios.py ZOA

# 2. Run the parse tests
dotnet test tests/Yaat.Sim.Tests/ --filter "FullyQualifiedName~VnasScenarioParseTests" -v n
```

Both the test and CLI tool use the shared `ScenarioValidator` class in `Yaat.Sim.Scenarios`.

## How It Works

1. `ScenarioValidator.Validate()` deserializes the scenario JSON and runs `CommandParser.ParseCompound()` on every preset command.
2. Failures are checked against `ScenarioValidator.KnownTypos` — a set of known scenario data typos that ARTCC staff need to fix upstream.
3. Results are returned as `ScenarioValidationResult` with per-command `PresetParseFailure` records.

## Adding Your ARTCC

To test a different ARTCC's scenarios:

1. Download them: `python tools/refresh-scenarios.py ZLA`
2. Add an `[InlineData("ZLA")]` line to the `AllScenarios_PresetCommandsParse` test in `VnasScenarioParseTests.cs`
3. Run the test

Or use the CLI: `dotnet run --project tools/Yaat.ScenarioValidator -- --artcc ZLA`

## File Layout

```
tests/Yaat.Sim.Tests/TestData/Scenarios/
  ZOA/
    _summaries.json          # Scenario index (names + IDs)
    01GWNG9BZB4VH5ZDCXNGPYH5FY.json  # Individual scenario files
    ...
```

Scenario files are **gitignored** — they must be downloaded locally. They are not committed to the public repo because they contain ARTCC training data from the vNAS data API.

## Refreshing Scenarios

Re-run the download script to pick up new or updated scenarios. Existing files are skipped (cached) — delete the ARTCC directory to force a full re-download:

```bash
# Refresh (skip cached)
python tools/refresh-scenarios.py ZOA

# Force full re-download
rm -rf tests/Yaat.Sim.Tests/TestData/Scenarios/ZOA
python tools/refresh-scenarios.py ZOA

# Download all known ARTCCs
python tools/refresh-scenarios.py --all
```

## Interpreting Failures

Parse failures fall into two categories:

1. **Typos in scenario data** — e.g., `WAI T6 DVIA` (space in WAIT), `CFIXX` (extra X), `WAIT10` (missing space). These should be reported to the ARTCC's training staff for correction.
2. **Unsupported commands** — commands that YAAT doesn't implement yet. These may need parser additions.

The output includes the scenario name so ARTCC staff can locate and fix the affected scenarios in ATCTrainer.
