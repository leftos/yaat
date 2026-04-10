# Replay Tape Completeness — CRC Mutations via Canonical Commands

## Context

YAAT's replay tape records instructor-originated mutations (commands, flight plan amendments, weather, settings) but misses mutations from CRC clients. When a scenario is rewound after CRC activity, all track ownership, handoffs, pointouts, scratchpads, and temporary altitudes revert — the rewind is not faithful.

The root cause: `CrcClientState.Stars.cs` handlers mutate `AircraftState` fields directly and never feed `RoomEngine.Record(...)`. Two mutation pipelines exist for the same aircraft state (instructor commands → `TrackCommandHandler` → recorded; CRC slews → direct field writes → not recorded).

**Design principle (per user direction):** every CRC operation should also be invokable as a canonical YAAT command from the RPO terminal. We unify the two pipelines: CRC handlers become thin translation layers that validate STARS-specific concerns and then dispatch through the existing canonical command pipeline. The replay tape captures every mutation as a `RecordedCommand` (no new `RecordedAction` subtypes needed for this work), and RPOs gain command-terminal parity with CRC scope operations.

Outcome: rewind reproduces identical aircraft state regardless of mutation origin, and any operation a CRC student can perform via slew, an RPO can perform via the command terminal.

## Existing infrastructure (to reuse)

- **`Yaat.Sim/Commands/CommandRegistry.cs:670–763`** — already has TRACK, DROP, HO, ACCEPT, CANCEL, PO, OK, SP1, SP2, TA, CRUISE, ANNOTATE
- **`Yaat.Sim/Commands/CanonicalCommandType.cs`** — enum where new types are added; tests enforce parity with `CommandRegistry.All`
- **`yaat-server/src/Yaat.Server/Simulation/TrackCommandHandler.cs:138`** — `HandleTrackCommand(cmd, room, callsign, identity)` is the central dispatcher; already routes Scratchpad1/2, TemporaryAltitude, Cruise, PointOut, Acknowledge to `TrackEngine`
- **`Yaat.Sim/Commands/TrackEngine.cs`** — pure mutation logic in Yaat.Sim (HandleTrack, HandleScratchpad1, HandlePointOut, HandlePointOutNoArgs, etc.)
- **`yaat-server/src/Yaat.Server/Simulation/RoomEngine.cs:50`** — `Record(RecordedAction)` already exists; appends to `scenario.ActionLog` and auto-takes-control out of playback mode
- **`RoomEngine.cs:967` `SendCommandAsync(connId, callsign, command, initials)`** — full pipeline; we will NOT call this from CRC handlers, but the parsing/dispatch logic inside it shows the pattern to mirror in the new helper
- **`RoomEngine.cs:664` `ReplayCommand(cmd)`** — already replays `RecordedCommand` → parses → routes track commands through `TrackCommandHandler.HandleTrackCommand`. No replay-side changes needed for any mutation that becomes a canonical command.
- **`yaat-server/tests/Yaat.Server.Tests/RewindTests.cs`** + **`tests/Yaat.Sim.Tests/Simulation/RecordingArchiveTests.cs`** — testing pattern (`RoomEngineTestHarness`, set `ElapsedSeconds`, mutate, assert `ActionLog`)

## Gap inventory: CRC mutations vs canonical commands

| CRC operation | File:line | Existing canonical? |
|---|---|---|
| `CrcInitiateControl` (slew unowned, callsign+slew) | `CrcClientState.Stars.cs:104` | `TRACK` |
| `CrcTerminateControl` | `:123` | `DROP` |
| `CrcHandoff` (TCP shorthand, ERAM `CXX`) | `:182` | `HO` |
| Bare slew → accept inbound handoff | `:520` (CrcImpliedBareSlew) | `ACCEPT` |
| Owner slew → cancel outbound handoff | `:538` | `CANCEL` |
| `CrcImpliedPointout` (`text*`) | `:675` | `PO` |
| `CrcImpliedRejectPointout` (`UN`) | `:715` | **MISSING — add `PORJ`** |
| Owner slew → retract own outbound pointout | `:563` | **MISSING — add `PORT`** |
| Bare slew → accept inbound pointout | `:579` | `OK` (Acknowledge) |
| Bare slew → ack conflict alert | `:589` | **MISSING — add `CAACK`** |
| `CrcImpliedTemporaryAltitude` (`+NNN`) | `:663` | `TA` |
| `CrcImpliedAmendFiledAltitude` (`++NNN`) | `:669` | `CRUISE` |
| Scratchpad1 set/clear/toggle | `:402,416,509` | `SP1` |
| Scratchpad2 set/clear (`+`, `+text`) | `:403,410` | `SP2` |
| Pilot reported altitude (`NNN`) | `:502` | **MISSING — add `PRA`** |
| `CrcConflictAlert` (CA inhibit toggle) | `:274` | **MISSING — add `CAINH`** |
| Leader direction (`1`–`9`) | `:345` | **MISSING — add `LDR`** |
| J-Ring (`*J…`) | `:351` | **MISSING — add `JRING`** |
| Cone (`*P…`) | `:358` | **MISSING — add `CONE`** |
| `CrcCreateGhostTrack` | `:325` | **MISSING — add `GHOST`** (creates aircraft; biggest item) |
| `CrcCoordination` | `:242` | already routes through `RoomEngine.HandleCrcCoordination` (existing coordination pipeline — verify this records, fix if not) |

## Design

### Step 1 — Internal `RoomEngine.RecordAndDispatch` helper

Add a sync method on `RoomEngine`:

```csharp
internal CommandResultDto RecordAndDispatch(string callsign, string canonicalCommand, TrackOwner identity, string initials)
{
    var scenario = Room.ActiveScenario;
    if (scenario is null) return new CommandResultDto(false, "No active scenario");

    var (effective, asOverride) = TrackCommandHandler.ExtractAsPrefix(canonicalCommand);
    var parseResult = CommandParser.Parse(effective);
    if (!parseResult.IsSuccess || parseResult.Value is null)
        return new CommandResultDto(false, parseResult.ErrorMessage ?? "Parse error");

    var cmd = parseResult.Value;
    CommandResultDto result;
    if (TrackCommandHandler.IsTrackCommand(cmd))
        result = _trackHandler.HandleTrackCommand(cmd, Room, callsign, identity);
    else if (CoordinationCommandHandler.IsCoordinationCommand(cmd))
        result = _coordinationHandler.HandleCommand(cmd, Room, callsign, identity);
    else
        return new CommandResultDto(false, "RecordAndDispatch only supports track/coordination commands");

    if (result.Success)
        Record(new RecordedCommand(scenario.ElapsedSeconds, callsign, canonicalCommand, initials, ConnectionId: ""));
    return result;
}
```

Notes:
- Uses `_trackHandler`/`_coordinationHandler` already injected into `RoomEngine` (see ctor at `RoomEngine.cs:18`).
- Mirrors the relevant slice of `SendCommandAsync` (parse → dispatch → record), but synchronous and without assignment-override / broadcast logic that doesn't apply to internal dispatch.
- `Record()` is private today (`RoomEngine.cs:50`); no visibility change required since `RecordAndDispatch` lives on `RoomEngine`.
- Replay path (`RoomEngine.ApplyRecordedAction → ReplayCommand`) already handles `RecordedCommand` for every track/coordination command — no replay-side changes for anything that becomes a canonical command.

### Step 2 — Add missing canonical commands

For each new command type: add the enum value in `CanonicalCommandType.cs`, add an entry in `CommandRegistry.All`, add a `*Command` parsed-command record, add parsing in `CommandParser`, add a handler branch in `TrackCommandHandler.HandleTrackCommand` switch (`TrackCommandHandler.cs:153`), and add the actual mutation in `TrackEngine` (Yaat.Sim side).

| New `CanonicalCommandType` | Aliases | Args | Mutation |
|---|---|---|---|
| `RejectPointout` | `PORJ`, `UN` | none | `ac.Pointout!.Status = Rejected` (recipient identity must match `Pointout.Recipient`) |
| `RetractPointout` | `PORT` | none | `ac.Pointout = null` (owner identity must match `Pointout.Sender` and `ac.Owner`) |
| `AcknowledgeConflictAlert` | `CAACK` | none | mark all `ConflictAlerts.Conflicts` involving callsign as `IsAcknowledged = true` |
| `PilotReportedAltitude` | `PRA`, `PILOT` | `altHundreds` (0 = clear) | `ac.PilotReportedAltitude = altHundreds == 0 ? null : altHundreds` |
| `InhibitConflictAlert` | `CAINH`, `CAI` | none (toggle) | `ac.IsCaInhibited = !ac.IsCaInhibited`; if becoming inhibited, also drop relevant conflicts (mirror logic in `CrcConflictAlert`) |
| `LeaderDirection` | `LDR` | digit `1`–`9` (`5` = clear) | `ac.GlobalLeaderDirection = digit == 5 ? null : digit` |
| `JRing` | `JRING` | `radius` int (none = clear) | `ac.TpaType = radius is null ? null : (int)StarsTpaType.JRing` (preserve radius if model carries it) |
| `Cone` | `CONE` | `radius` int (none = clear) | `ac.TpaType = radius is null ? null : (int)StarsTpaType.Cone` |
| `GhostTrack` | `GHOST` | `[airport] runway` (airport defaults to scenario primary) | Creates ghost track staggered off runway end (0.1nm increments). Overlay if callsign matches existing aircraft, pure-ghost otherwise. Ghost position tracked via `GhostAirportId`/`GhostRunwayId` on `AircraftState`. |

Per CLAUDE.md command rules, names should match ATCTrainer/VICE where applicable; verify against `docs/command-aliases/reference.md` before finalizing aliases.

**Tests for Commit 1:** parser round-trips for each new command (`CommandParserTests`); registry/canonical parity (the existing test that enforces every `CanonicalCommandType` exists in `CommandScheme.Default()` and `CommandRegistry.All`); mutation tests in `TrackCommandHandlerTests` invoking each new command and asserting field changes.

### Step 3 — Refactor `CrcClientState.Stars.cs` handlers

Each `Crc*` helper currently mutates `ac.Owner`, `ac.HandoffPeer`, etc. directly. Replace each direct mutation with a `RecordAndDispatch` call that:

1. Performs CRC-specific validation (`TRACK NOT FOUND`, `ALREADY TRACKED`, `RequireOwnership`, TCP resolution, etc.) and short-circuits with the existing error strings.
2. Synthesizes the canonical command text (e.g. `"TRACK"`, `"HO 2B"`, `"PO 30"`, `"SP1 ABC"`, `"+040"` → `"TA 040"`, `"++110"` → `"CRUISE 110"`).
3. Calls `_roomEngine.RecordAndDispatch(callsign, canonicalText, identity, initials: "CRC")` and returns the error message from the result (or `null` on success).
4. **Removes** the direct field mutation — the canonical handler in `TrackEngine` is now the only writer.

Special cases:

- **`CrcImpliedBareSlew`** branches based on aircraft state (handoff accept vs cancel vs pointout retract vs pointout accept vs CA ack vs implied IC). Each branch dispatches a different canonical command. The "ack conflict alerts + …" combinations may dispatch multiple canonical commands in sequence, each producing its own `RecordedCommand` entry. Order matches the current sequential mutation logic.
- **Identity translation**: CRC `TrackOwner` already matches the type expected by `TrackCommandHandler.HandleTrackCommand`. The `AS` prefix is only needed if the canonical text is going to a path that resolves identity from `connectionId`; since `RecordAndDispatch` takes `identity` directly, `AS` can be omitted in the synthesized text. The recorded command string still embeds enough context (`AS <subset><sector>`) for correct replay through `ReplayCommand` → `_trackHandler.ResolveEffectiveIdentity` → `HandleTrackCommand`. Verify replay round-trips identity correctly during testing.
- **`CrcCreateGhostTrack`**: dispatches `GHOST [airport] <runway>`. The handler resolves the runway, counts existing ghosts on that runway (via `GhostAirportId`/`GhostRunwayId` fields), and projects the ghost position off the runway end at stagger offsets (0.1nm increments). For CRC-sourced ghost tracks (which arrive as lat/lon from scope click), the CRC handler must infer the nearest runway from the lat/lon — use the controller's position identity (e.g., OAK_TWR → KOAK) to determine airport, then find the nearest runway end to the clicked position.
- **`CrcImpliedAmendFiledAltitude` (`++NNN`)**: dispatches `CRUISE <hundreds>`. CRUISE already mutates `ac.CruiseAltitude` via `TrackEngine.HandleCruise` — no new code needed beyond the dispatch swap.
- **`CrcCoordination`**: already routes through `_roomEngine.HandleCrcCoordination`. Verify (1) it currently records anything, (2) if not, change `HandleCrcCoordination` to use `Record()` or route through `RecordAndDispatch` with the appropriate coordination command.

**Tests for Commit 2:** `RewindTests`-style tests for each refactored CRC handler — call the handler, assert `ActionLog` contains the expected `RecordedCommand` text, then run the rewind/replay path and verify aircraft state matches.

## Files to modify

**Yaat.Sim (yaat repo):**
- `src/Yaat.Sim/Commands/CanonicalCommandType.cs` — new enum values
- `src/Yaat.Sim/Commands/CommandRegistry.cs` — registry entries (keep grouped under "Track Operations" / "Data Operations")
- `src/Yaat.Sim/Commands/CommandParser.cs` — parsing for new tokens
- `src/Yaat.Sim/Commands/ParsedCommand.cs` — new `*Command` records
- `src/Yaat.Sim/Commands/CommandSchemeParser.cs` — scheme entries (parity test enforces this)
- `src/Yaat.Sim/Commands/CommandDescriber.cs` — describer entries
- `src/Yaat.Sim/Commands/TrackEngine.cs` — new mutation methods (`HandleRejectPointout`, `HandleRetractPointout`, `HandleAckConflict`, `HandlePilotReportedAltitude`, `HandleInhibitCa`, `HandleLeaderDirection`, `HandleJRing`, `HandleCone`, `HandleCreateGhost`)
- `tests/Yaat.Sim.Tests/Commands/*` — parser + handler tests

**yaat-server:**
- `src/Yaat.Server/Simulation/TrackCommandHandler.cs:153` — switch arms for new `*Command` types
- `src/Yaat.Server/Simulation/RoomEngine.cs` — new `RecordAndDispatch` method
- `src/Yaat.Server/Hubs/CrcClientState.Stars.cs` — refactor every `Crc*` helper to dispatch via `RecordAndDispatch` and remove direct `AircraftState` mutations
- `tests/Yaat.Server.Tests/RewindTests.cs` (or new sibling) — record-and-replay tests for each CRC mutation type

## Out of scope

- `RecordedAmendFlightPlan` and the `TrainingHub.AmendFlightPlan` DTO path — already records correctly. The CRC `++NNN` filed-altitude amendment uses the new `CRUISE` dispatch instead, leaving multi-field DTO amendments as their own pipeline.
- Display-only state (StripAnnotation flags, per-controller leader direction overrides if any exist) — only `AircraftState`-level mutations are addressed.
- New `RecordedAction` subtypes — the unified canonical-command approach makes them unnecessary. Existing subtypes are untouched.

## Commit shape

**Commit 1 — `feat: canonical commands for CRC-only operations`**
Adds `CanonicalCommandType` entries, parser/registry/scheme/describer wiring, `TrackEngine` mutation methods, `TrackCommandHandler` switch arms, and unit tests. No CRC handler changes — RPOs immediately gain command-terminal parity, replay tape behaviour unchanged.

**Commit 2 — `feat: route CRC mutations through canonical command pipeline`**
Adds `RoomEngine.RecordAndDispatch`, refactors every `Crc*` helper in `CrcClientState.Stars.cs` to validate-then-dispatch, deletes direct `AircraftState` writes from CRC handlers, adds replay/rewind tests covering CRC-originated mutations. This is the commit that fixes the issue.

## Verification

1. **Build clean both repos**: `pwsh tools/test-all.ps1` from yaat repo (builds + tests both projects).
2. **Sim tests**: `dotnet test tests/Yaat.Sim.Tests` — registry parity, parser round-trips, new `TrackEngine` mutations.
3. **Server tests**: `dotnet test ../yaat-server/tests/Yaat.Server.Tests` — `RewindTests` for each refactored CRC handler. Each test pattern:
   - Load minimal scenario via `RoomEngineTestHarness`
   - Set `Room.ActiveScenario.ElapsedSeconds`
   - Invoke the `Crc*` helper directly
   - Assert `ActionLog` length and the recorded `canonicalText`
   - Call `RoomEngine.RewindAsync(0)` then verify `ApplyRecordedAction` reproduces the original state
4. **Manual smoke**: connect a CRC client to a YAAT room, perform IC/handoff/pointout/scratchpad/CA-inhibit/leader-direction operations; rewind the scenario via the timeline scrubber; verify all CRC effects re-apply.
5. **Manual RPO smoke**: open YAAT command terminal, invoke each new canonical command (`PORJ`, `PORT`, `CAACK`, `PRA 250`, `CAINH`, `LDR 3`, `JRING 5`, `CONE 5`, `GHOST AAL100 37.7 -122.2`); confirm same effects as the CRC slews.
6. **Update docs**: `COMMANDS.md`, `docs/command-aliases/reference.md`, `docs/yaat-vs-atctrainer.md`, `USER_GUIDE.md` for the new command set; `docs/architecture.md` if the dispatch helper warrants a note. Mark `docs/plans/open-issues/replay-tape-completeness.md` for deletion (or update to point at completed work).

## Open risks

- **`GhostTrack` complexity**: ghost creation creates new aircraft with manual position and overlay-vs-pure-ghost branches. The canonical command needs argument syntax for `lat`/`lon` (or a `here` UI modifier the radar context menu can fill in). Worth scoping a dedicated mini-design as part of Commit 1; if too large, defer ghost-track recording to a follow-up and explicitly note the gap.
- **Identity round-trip on replay**: confirm `_trackHandler.ResolveEffectiveIdentity(connectionId, room, asOverrideTcp)` correctly resolves identity for synthesized commands where `connectionId` is empty. May need to embed `AS <tcp>` in the canonical text and rely on `ExtractAsPrefix` during replay.
- **Bare-slew multi-effect ordering**: rewind is order-sensitive. Each branch in `CrcImpliedBareSlew` must dispatch its sequence of canonical commands in the same order it currently mutates state. Add ordered-tape assertions in tests.
- **Coordination commands** (`CrcCoordination` → `HandleCrcCoordination`): unverified whether the existing coordination pipeline records. If it doesn't, fold a fix into Commit 2 (or a separate commit) so coordination state also rewinds correctly.
