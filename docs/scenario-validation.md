# Scenario Preset Command Validation

YAAT validates the preset commands in every loaded training scenario so ARTCC training staff can catch typos and unsupported commands before students hit them.

Two surfaces:

1. **Auto-validate on load** — preset commands are parsed automatically when a scenario loads. Parse failures appear as `[WARN]` entries in the terminal panel; recognized known-typo patterns are summarized as `[INFO]` with a count.
2. **Batch validation window** — *Scenario → Validate Scenarios* fetches every scenario for the configured ARTCC from the vNAS data API, validates each one, and shows a report window:
   - Summary: ARTCC name, total scenarios / presets / failure counts.
   - DataGrid: scenario name, aircraft, command.
   - **Copy Report** button: copies a structured text report (grouped by scenario → aircraft) to the clipboard for sharing with ARTCC staff.

## How it works

`ScenarioValidator.Validate()` (in `Yaat.Sim.Scenarios`) deserializes the scenario JSON and runs `CommandParser.ParseCompound()` on every preset command. Results come back as a `ScenarioValidationResult` with per-command `PresetParseFailure` records.

All failures are reported — no silent suppression. Reports are grouped by scenario and aircraft for readability.

## Interpreting failures

Parse failures fall into two categories:

1. **Typos in scenario data** — e.g., `WAI T6 DVIA` (space in `WAIT`), `CFIXX` (extra `X`), `WAIT10` (missing space). Report these to the ARTCC's training staff for correction in ATCTrainer.
2. **Unsupported commands** — commands YAAT doesn't implement yet. These may need parser additions.

The output includes the scenario name so ARTCC staff can locate and fix the affected scenarios.
