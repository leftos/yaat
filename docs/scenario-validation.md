# Scenario Validation

YAAT validates every vNAS training scenario offline so ARTCC training staff can catch typos, unsupported commands, stale procedure versions and inconsistent aircraft data before students hit them. There is no in-app validation surface: the client's batch window was removed once the CLI covered it, and a scenario *load* only reports the data problems `ScenarioLoader` finds while building the aircraft (missing parking, a SID/STAR resolved to a newer version) on `LoadScenarioResult.Warnings` — a separate channel from the validator below.

Three surfaces, all driven by `ScenarioValidator.Validate()` (`src/Yaat.Sim/Scenarios/ScenarioValidator.cs`):

1. **yaat-server CLI** — `dotnet run --project tools/Yaat.ScenarioValidator -- --all --json` (or one ARTCC / one file) fetches the scenarios from the vNAS data API and prints a text report or the raw `ScenarioValidationResult` JSON.
2. **Discord CI** — `.github/workflows/discord-scenario-validation.yml` in yaat-server runs the CLI weekly (and on the `/validate` button) and posts one report per ARTCC to that ARTCC's "Scenario Validation" channel. The post is built by hand from the JSON in the workflow's embedded Python (`format_message`), so **a new check only reaches Discord once that function extracts its list** — see "Adding a check".
3. **Local corpus sweep** — `tests/Yaat.Sim.Tests/Scenarios/VnasScenarioParseTests.cs` runs the validator over the cached scenarios under `tests/Yaat.Sim.Tests/TestData/Scenarios/{ARTCC}/` (refreshed by yaat-server's `tools/validate-all-scenarios.py`); it asserts only that the JSON deserialises and logs the findings.

## What it checks

`ScenarioValidationResult` carries one list per check. Only the first is a hard failure; the rest are advisories that the report lists but never fails on.

| List | Check | Typical cause |
|------|-------|---------------|
| `Failures` (`PresetParseFailure`) | Every `presetCommands[].command` parses with `CommandParser.ParseCompound` | A typo in ATCTrainer (`WAI T6`, `CFIXX`, `WAIT10`) or a command YAAT does not implement |
| `ProcedureIssues` (`ProcedureIssue`) | Each SID/STAR named in a navigation path resolves; `VersionChanged` when the navdata has a newer revision (`BDEGA3` → `BDEGA4`), `NotFound` otherwise | Scenario authored against an older AIRAC |
| `TransitionFixSubstitutions` | After a version upgrade, the scenario's transition fix still exists on the new procedure; suggests the closest valid one | A transition renamed or dropped between revisions |
| `AircraftTypeMismatches` (`AircraftTypeMismatch`) | The scenario's physical `aircraftType` and its `flightplan.aircraftType` name the same base ICAO type (wake prefix and equipment suffix stripped via `AircraftState.StripTypePrefix`; a blank filed type is not a mismatch) | An editor changed the aircraft (an A388 arriving as a filed B744 — #438). YAAT shows the physical type on the ground view and Tower Cab and the filed type on the radar, strips and flight plan, so the mismatch is visible to students |

Failures that are known scenario defects rather than parser bugs are catalogued in [scenario-validation-known-failures.md](scenario-validation-known-failures.md); check it before chasing one.

## Adding a check

1. Add the record and a list on `ScenarioValidationResult`, populated by a private `Validate…` method shaped like `ValidateProcedures`, with a unit test beside `ProcedureVersionResolutionTests` (hand-built `Scenario`, assert the list).
2. yaat-server `tools/Yaat.ScenarioValidator/Program.cs`: the JSON mode serialises the record automatically; add the counter to the console summary and a section to `PrintTextReport`.
3. yaat-server `.github/workflows/discord-scenario-validation.yml` `format_message`: extract the new list (both PascalCase and camelCase keys), add it to `summary_parts`, and emit its per-scenario lines. Without this step the Discord post silently omits it.
4. yaat-server `tools/validate-all-scenarios.py` `build_report`: the same section for the local report.
5. Update the table above.

## Cross-ARTCC batch validation (Discord CI)

The ARTCC set is duplicated in **four hardcoded lists** that must stay in sync — miss one and the pipeline half-works:

1. `yaat-server/tools/Yaat.ScenarioValidator/Program.cs` — `AllArtccs` (the weekly `--all` CI run)
2. `yaat-server/tools/validate-all-scenarios.py` — `ALL_ARTCCS` (local dev refresh/report tool)
3. `tests/Yaat.Sim.Tests/Scenarios/VnasScenarioParseTests.cs` — `[InlineData]` theory (local-only; skips when `TestData/Scenarios/{ID}` is absent)
4. `tools/discord-bot/validation-channels.json` — ARTCC → Discord channel snowflake (the routing key the weekly cron, `ensure-validation-buttons.js`, and `/validate` all iterate)

Current set (23 of vNAS's 24 ARTCCs): all 20 CONUS ARTCCs plus ZAN (Anchorage), ZHN (Honolulu — vNAS id is `ZHN`, not `HCF`), and
ZSU (San Juan). **ZUA (Guam) is deliberately excluded — 0 training scenarios**, so don't add it for "completeness." Adding an ARTCC
needs a real Discord channel in the "Scenario Validation" category wired into list 4 before the report can post. See [discord-integration.md](discord-integration.md) for the bot side.
