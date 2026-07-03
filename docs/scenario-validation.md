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

## Cross-ARTCC batch validation (Discord CI)

A separate weekly pipeline (`discord-scenario-validation.yml`) runs `ScenarioValidator.Validate()` against **every** ARTCC and
posts per-ARTCC reports to Discord (see [discord-integration.md](discord-integration.md)). The ARTCC set is duplicated in **four
hardcoded lists** that must stay in sync — miss one and the pipeline half-works:

1. `yaat-server/tools/Yaat.ScenarioValidator/Program.cs` — `AllArtccs` (the weekly `--all` CI run)
2. `yaat-server/tools/validate-all-scenarios.py` — `ALL_ARTCCS` (local dev refresh/report tool)
3. `tests/Yaat.Sim.Tests/Scenarios/VnasScenarioParseTests.cs` — `[InlineData]` theory (local-only; skips when `TestData/Scenarios/{ID}` is absent)
4. `tools/discord-bot/validation-channels.json` — ARTCC → Discord channel snowflake (the routing key the weekly cron, `ensure-validation-buttons.js`, and `/validate` all iterate)

Current set (23 of vNAS's 24 ARTCCs): all 20 CONUS ARTCCs plus ZAN (Anchorage), ZHN (Honolulu — vNAS id is `ZHN`, not `HCF`), and
ZSU (San Juan). **ZUA (Guam) is deliberately excluded — 0 training scenarios**, so don't add it for "completeness." Adding an ARTCC
needs a real Discord channel in the "Scenario Validation" category wired into list 4 before the report can post.
