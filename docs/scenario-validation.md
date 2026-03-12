# Scenario Preset Command Validation

YAAT can validate that all preset commands in an ARTCC's training scenarios parse correctly. This helps ARTCC training staff identify typos or unsupported commands before students encounter them.

## Quick Start

```bash
# 1. Download scenarios for your ARTCC
python tools/refresh-scenarios.py ZOA

# 2. Run the parse tests
dotnet test tests/Yaat.Sim.Tests/ --filter "FullyQualifiedName~VnasScenarioParseTests" -v n
```

The test output shows each scenario by name and lists any preset commands that failed to parse, including the scenario name, aircraft callsign, and the raw command text.

## How It Works

1. **`tools/refresh-scenarios.py`** downloads scenario JSON files from the vNAS data API (`data-api.vnas.vatsim.net`) into `tests/Yaat.Sim.Tests/TestData/Scenarios/<ARTCC>/`.
2. **`VnasScenarioParseTests`** loads all `.json` files from the ARTCC subdirectory and runs `CommandParser.ParseCompound()` on every preset command in every aircraft.
3. Failures are reported with the scenario's user-friendly name, the aircraft callsign, and the raw command string.

## Adding Your ARTCC

To test a different ARTCC's scenarios:

1. Download them: `python tools/refresh-scenarios.py ZLA`
2. Add an `[InlineData("ZLA")]` line to the `AllScenarios_PresetCommandsParse` test in `VnasScenarioParseTests.cs`
3. Run the test

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

The test output includes the scenario name so ARTCC staff can locate and fix the affected scenarios in ATCTrainer.
