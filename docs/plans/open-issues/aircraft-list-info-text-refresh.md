# Aircraft list info text refresh — add specific context per phase

## Bug (user's report)

> Info text in the aircraft list datagrid could use some further design. "pattern entry" as a generic term isn't helpful when we know exactly what pattern entry it's trying to do. "hdg PTN-ENTRY" is also not helpful when it's a direct. "Exiting runway" without the runway? "VFR Follow" without which callsign you're following? We should review all of our various info text and refresh it to be more detailed and offer context.

## Scope

Every string produced by `ComputePhaseStatus()` in `src/Yaat.Client/Models/AircraftModel.cs`, plus any related heading-suffix appends. This is a **full audit** of info text, not just the four cases the user called out.

## Suspected code

- `src/Yaat.Client/Models/AircraftModel.cs:711` — `ComputePhaseStatus()`; main switch that builds the text.
  - Line 731: `"Pattern Entry" => $"{PatternDirection} pattern entry"` — generic.
  - Line 735: `"Runway Exit" => "Exiting runway"` — missing runway.
  - Lines 798-800: `FormatFallbackPhase()` handles `"Following "` prefix — does not surface the target callsign.
  - Lines 844-850: `AppendHeadingIfAssigned()` — tacks on heading even when redundant.
- `src/Yaat.Sim/Phases/Pattern/PatternEntryPhase.cs` — source for pattern-entry direction, runway, and entry type (direct / 45 / midfield). Make sure those fields are exposed via the aircraft DTO reaching the client.
- `src/Yaat.Sim/Phases/Pattern/VfrFollowPhase.cs:47` — target callsign; must be reachable by the client (check existing DTO).
- `src/Yaat.Sim/Phases/Ground/RunwayExitPhase.cs` — runway id; confirm it reaches the client side.
- Sim DTOs: `src/Yaat.Sim/Dto/AircraftStateDto.cs` (or equivalent) — may need new fields if the phase detail isn't currently sent over SignalR.

## Prescriptive proposed text

| Current | Proposed |
|---|---|
| `"{direction} pattern entry"` | Direct: `"direct {direction} downwind {runway}"`. 45: `"45 to {direction} downwind {runway}"`. Midfield: `"midfield crossing to {direction} downwind {runway}"`. |
| `"hdg PTN-ENTRY"` suffix on pattern entry | **Remove** — the phase text already says "direct/45/midfield to downwind". Heading is redundant. |
| `"Exiting runway"` | `"exiting runway {runway} via {taxiwayName}"` (fall back to `"exiting runway {runway}"` if taxiway can't be resolved). |
| `"VFR Follow"` / `"Following "` | `"following {targetCallsign}"`. If the follower's own pattern leg is known: `"following {targetCallsign} on {leg}"`. |
| Generic trailing `hdg XXX` | Suppress when the phase implies a heading (pattern entry, base, final, taxi). Keep for vectors-only phases (heading selects, etc.). |

Implementer must audit **every** case in `ComputePhaseStatus()` — not just the four above — and propose text improvements where the current output is generic. Flag any case where the needed detail is on the sim side but not reaching the client DTO.

## Acceptance criteria

- For each phase listed in `ComputePhaseStatus()`, the resulting string includes relevant context (runway, target callsign, taxiway, leg, etc.) where applicable.
- Heading suffix is suppressed for phases where it's redundant.
- A unit test on `ComputePhaseStatus` (with fabricated phase states) asserts the exact expected string for each case.
- Visual verification: run client, connect to local server, load a scenario, exercise pattern entry + follow + runway exit + vectors and confirm text reads naturally.
- No DTO fields are added without a client that actually consumes them (per CLAUDE.md "No repurposing DTO fields. Add new fields with clear names.").

## TDD note

- Create `tests/Yaat.Client.Tests/AircraftModelPhaseStatusTests.cs` (check existing client test layout first).
- Fabricate `AircraftModel` states rather than driving through full simulation.
- No recording replay needed. Visual verification is required in addition to unit tests (per CLAUDE.md: "For UI or frontend changes, start the dev server and use the feature in a browser before reporting the task as complete" — for Avalonia, use `dotnet run --project src/Yaat.Client` with a local server).
