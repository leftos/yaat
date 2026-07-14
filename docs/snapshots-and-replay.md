# Snapshots, Recording, and Replay

> Read this before touching anything in `src/Yaat.Sim/Simulation/Snapshots/` or `src/Yaat.Sim/Simulation/Replay*` and before debugging a bug bundle. The bug-bundle skill (`tools/bug_bundle.py`) rides on this machinery.

## What gets serialized — `Simulation/Snapshots/`

`StateSnapshotDto` is the top of the tree:

```
StateSnapshotDto
├─ SchemaVersion (int)
├─ ElapsedSeconds, Rng (RngState), WeatherJson, Server?
└─ Aircraft[] : AircraftSnapshotDto
    ├─ identity + kinematics (callsign, lat/lon, true heading, alt, IAS, …)
    ├─ FlightPlan       — AircraftFlightPlanDto
    ├─ Transponder      — AircraftTransponderDto
    ├─ Ground           — AircraftGroundOpsDto    (Layout is JsonIgnore!)
    ├─ Track            — AircraftTrackDto
    ├─ Stars            — AircraftStarsStateDto
    ├─ Eram             — AircraftEramStateDto
    ├─ Approach         — AircraftApproachStateDto
    ├─ Procedure        — AircraftProcedureDto
    ├─ Pattern          — AircraftPatternDto
    ├─ Clearance        — AircraftClearanceDto
    ├─ HoldAnnotation   — AircraftHoldAnnotationDto
    ├─ Ghost            — AircraftGhostTrackDto
    ├─ Voice            — AircraftVoiceDto
    ├─ Targets          — ControlTargetsDto
    ├─ Queue            — CommandQueueDto
    └─ Phases (Phase chain) — polymorphic PhaseDto[]
```

`AircraftState.ToSnapshot()` and `FromSnapshot()` are hand-written constructors. Each sub-object owns its own `ToSnapshot/FromSnapshot` pair so changes localize.

## Schema versions — `SnapshotSchemaMigrator.cs`

| Version | Change |
|---|---|
| 1 → 2 | Added `ServerSnapshotDto` (null-safe, no transform) |
| 2 → 3 | Added `AircraftFlightPlanDto.CreatedByOwner` (null-safe) |
| 3 → 4 | Split actual vs filed aircraft type — seed `FlightPlan.AircraftType` from top-level `AircraftType` |

**Actual vs filed aircraft type (v4).** `AircraftState.AircraftType` is the physical type (fixed at spawn; drives Tower Cab, physics, and the operator Aircraft List). `AircraftFlightPlan.AircraftType` is the filed type (mutable via amendment; drives STARS, ASDE-X, the Flight Plan Editor, strips, and ERAM). Top-level wins for actual; the FP field is opt-in (no cross-fill). The migrator seeds the filed field from the top-level type for legacy v3 bundles.

The table lists only the steps with a data *transform*; the authoritative full chain (currently through v15) is the `Migrate()` source. `Migrate` must tolerate leniently-deserialized nulls on the fields it touches — e.g. a legacy aircraft snapshot can carry a null `FlightPlan`, so the v3→v4 seed guards with `ac.FlightPlan is { AircraftType: null or "" }` rather than dereferencing.

**Rule for adding a field**: if it defaults cleanly (`null` / `false` / `0`) and old data is correct under that default, **no migration step needed**. Just add it to the DTO. If old data needs transformation (rename, split, reinterpret), bump `SchemaVersion` and add a `Migrate()` step.

### Upgrading recordings on disk — surgical, never re-simulated

`SnapshotSchemaMigrator.Migrate` runs at replay (`SimulationEngine.RestoreFromSnapshot`) so old recordings load without any disk change. To rewrite committed fixtures to the current schema, `RecordingSchemaUpgrader.Upgrade(bytes)` (Yaat.Sim) transforms each snapshot's JSON **in place** via the same migrator — decompress → `StateSnapshotDto` → `Migrate` → re-serialize — across all three containers (non-zip `.br` `SessionRecording`, v4 archive zip, and bug-report bundle wrapping a v4 archive), copying every other entry verbatim. It **never re-simulates**; re-simulation with current code would rewrite a frozen pre-fix snapshot into the fixed state and silently invalidate hybrid-replay tests (see [e2e-tdd-issue-debugging.md §5b](e2e-tdd-issue-debugging.md)). The `Yaat.RecordingUpgrader` CLI (yaat-server) drives it and reserves re-simulation for the one v1→v2 bootstrap (v1 carries no snapshots to preserve). Snapshot entries are Brotli — decompress them with an explicit `BrotliStream`, not the autodetecting `RecordingCompression.Decompress`, whose plain-JSON heuristic can misfire on a Brotli stream that starts with `{`/`[`.

## What is NOT serialized

Some state is intentionally runtime-only:

- **`AircraftState.DeclinationCachePosition`** — `null` means "not cached"; warms up on the first tick after a round-trip.
- **`Ground.Layout`** is `[JsonIgnore]`. Only `Ground.LayoutAirportId` round-trips. On restore, `SimulationEngine` re-resolves the layout from the airport ID against the loaded ground graphs. This avoids embedding an entire taxiway graph per aircraft.
- **`PendingObservations`** (pilot "watch for condition" state) — ephemeral, never restored.

If you see `[JsonIgnore]` on a field, also check that there's a separate carrier (like `LayoutAirportId`) that lets restore reattach.

## Phase polymorphism

`PhaseDto` is the abstract base; every concrete phase DTO has a `[JsonDerivedType(typeof(XyzPhaseDto), "Xyz")]` registration. See [phases.md](phases.md) for the four steps required to add a new phase to the snapshot system.

**Never remove a phase's `[JsonDerivedType]` if a committed recording may have captured that phase in a snapshot.** Polymorphic deserialization throws `JsonException: Read unrecognized type discriminator id 'Xyz'` **before** `SnapshotSchemaMigrator` runs, so a version bump / migrator can't rescue it. The failure misleads — affected tests pass in isolation and fail only once a test that replays the offending recording runs (it looks like a static-singleton race but is the discriminator). When a phase is superseded, **retain the old class + its DTO + `JsonDerivedType` for restore-only**: mark it clearly and stop creating it from the command path (e.g. `PushbackToSpotPhase`, kept only for restore after the #233 pushback rewrite). "Unreleased software: delete freely" does **not** extend to types serialized into committed recording fixtures. Verify with `pwsh tools/test-all.ps1` (full suite), not just targeted tests.

## Recording — `RecordingArchive.cs`

A recording is a ZIP with this layout:

```
manifest.json                # Version, RngSeed, ActionCount, HasWeather,
                             # HasArtccConfig, HasTerminalLog, ArtccId, ScenarioId/Name,
                             # ClientVersion, ClientBuildKind, ServerVersion,
                             # Snapshots[], LayoutAirportIds[], AirportGeoJsonIds[]
scenario.json.br             # Brotli-compressed scenario JSON
actions.json.br              # Brotli-compressed RecordedAction[]
terminal-log.json.br         # Brotli-compressed RecordedTerminalEntry[] (optional; HasTerminalLog)
snapshots/NNN.json.br        # one per snapshot index
layouts/{AirportId}.json.br  # deduplicated ground layouts (optional)
airport-geojson/{AirportId}.geojson.br
                             # original airport GeoJSON sources (optional)
weather.json                 # plain JSON (optional; gated by HasWeather)
artcc-config.json.br         # ARTCC config JSON (optional; HasArtccConfig)
bookmarks.json               # plain JSON (optional; user-authored timeline bookmarks)
```

**`terminal-log.json.br`** is the room's broadcast terminal stream — the command echoes, responses, SAY lines, warnings, and chat the user saw — each captured with a wall-clock `Timestamp` and the scenario-elapsed `ElapsedSeconds` it occurred at (`RecordedTerminalEntry`). The server appends to `SimScenarioState.TerminalLog` inside `TrainingBroadcastService.BroadcastTerminalEntry` only while live (never during playback/reconstruction), and preserves it across rewind reloads alongside `ActionLog`. On load, the client repopulates its terminal from it (via the `GetTerminalLog` hub method) so every terminal line is a replay-scrub target — right-click a line → `RewindToSeconds(entry.ElapsedSeconds)`. Absent in recordings written before this feature (`HasTerminalLog` false → the reader returns an empty log). For those legacy recordings, `RecordingManager.ApplyPlaybackActions` still echoes each replayed command/chat into the otherwise-empty terminal during forward playback; when a terminal log is present that echo is suppressed (guarded on `TerminalLog.Count == 0`) so it does not duplicate the repopulated lines.

**Version fields** (`ClientVersion`, `ClientBuildKind`, `ServerVersion`) are stamped at export time for bug-report triage. `ClientVersion`/`ClientBuildKind` are sent by the exporting client (`BuildInfo.Version` / `BuildInfo.BuildKind`) and describe the user's build; `ServerVersion` is `SimBuildInfo.Version` — the Yaat.Sim assembly that actually ran the session on the server (Yaat.Server carries no independent version). Since the hosted server and a user's client can be on different builds, the two answer different questions: was the *user's* client behind a fix, vs. was the *sim code that ran* behind a fix. All three are null for recordings exported before this was added or migrated from legacy formats.

**`bookmarks.json`** persists the room's shared timeline bookmarks (GitHub issue #288). Bookmarks are server-authoritative per-room state (`SimScenarioState.Bookmarks`), synced to every RPO via the `BookmarksChanged` broadcast and the `RoomStateDto.Bookmarks` join seed. The client still injects `bookmarks.json` at save time via `RecordingArchive.WriteBookmarks(bytes, …)` (a copy-into-fresh-`Create` rebuild, not Update mode) from `SaveRecording`/`SaveBugReportBundle` — its `Bookmarks` mirror equals the server's list. On **load**, the *server* reads the entry via `RecordingArchive.ReadBookmarks()` in `RoomEngine.LoadRecordingArchiveAsync`, seeds `SimScenarioState.Bookmarks` (re-minting ids), and broadcasts — so every RPO in the room sees the loaded recording's bookmarks, not just whoever loaded it. The entry is not tracked in the manifest, so it is absent in older recordings — `ReadBookmarks()` returns `[]` then. Bookmarks are deliberately excluded from `ScenarioSnapshotDto` (they are timeline-global metadata, not per-tick state); `RewindAsync` and snapshot-replay carry them across the scenario reload explicitly, so scrubbing never drops a bookmark.

**`RecordedAction`** is a discriminated union via `[JsonDerivedType]`. The common members are `(ElapsedSeconds, $type)`; concrete types add their fields:

- `RecordedCommand(Callsign, Command, Initials, ConnectionId)` — every user command (including ones rejected at validation; replay is faithful to history).
- `RecordedChat(Initials, Message)` — a controller/RPO chat message. Has no simulation-state effect, so replay/reconstruction ignores it; recorded so bug-bundle tooling carries the chat log and forward tape-playback can re-surface it in the terminal.
- `RecordedSettingChange` — sim-control toggles (e.g. `SetValidateDctFixes`). Replay handlers in both repos apply these. **Pattern: any new sim-control toggle should produce one of these so replays stay faithful.**
- `RecordedAircraftSpawn` — full `AircraftSnapshotDto` for aircraft created by runtime generators. Replay injects this aircraft directly and skips the RNG-driven generator path when spawn actions are present, so generator implementation changes do not rename or re-type historical arrivals.
- Spawn, preset, and other event-shaped actions.

**Snapshot cadence**: snapshots are written on demand by the recording manager (rewind checkpoints, periodic captures). Live replay does not need a snapshot per tick — it ticks forward from the most recent prior snapshot, applying actions at their `ElapsedSeconds`.

## Replay — `Simulation/Replay/`

The replay surface on `SimulationEngine`:

| Method | Purpose |
|---|---|
| `ReplayFromStartTo(target, actions)` | Reset to t=0 and replay forward to `target`. **From-scratch every call** — only use for one-shot rewinds, never in a loop. |
| `FastForwardTo(target, actions)` | Advance from the current `ElapsedSeconds` to `target`, applying actions in between. Throws if `target ≤ current` (use `ReplayFromStartTo` or restore from a snapshot to rewind). Updates the replay cursor so subsequent `ReplayOneSecond` calls continue from `target`. |
| `ReplayRange(start, target, actions)` | Replay between two timestamps. Engine must already be at `start` (e.g. via snapshot restore); does not reset. |
| `ReplayRangeWithVerification(start, target, actions, archive)` | Same as `ReplayRange` + per-snapshot drift report (`SnapshotDriftReport`). Use this to find the first divergence point. |
| `ReplayOneSecond()` | Advance exactly one sim-second (4 sub-ticks) from current state, then advance the action cursor. **This is the right tool for stepping through a recording.** |
| `ReplayOneSubTick()` | 0.25s granularity — physics tests. |

`ReplayTrackApplier` handles track / coordination / `AS`-prefix commands during replay. It's wired into `SimulationEngine.ReplayCommand` *before* the aircraft-exists guard, so position-claiming commands (`AS X TRACK …`) work even when the aircraft has just been spawned.

Runtime aircraft spawns are action-driven during replay. `RecordedAircraftSpawn` actions apply before the tick's generator phase, and old archives that predate those actions synthesize them from snapshot deltas for aircraft that were not declared in the scenario JSON.

### `SnapshotDiff` — drift detection

`SnapshotDiff.Compare(actual, expected)` returns a `SnapshotDriftReport` with per-aircraft `FieldDrift` records. Default tolerances are loose (designed to absorb float rounding, not real divergence): position ±0.5nm, heading ±5°, altitude ±100ft, IAS ±10kt. Tighten if you're hunting determinism bugs.

Covered fields: position, heading, altitude, IAS, NavigationRoute, phase, track owner.

## Bug bundles — `tools/bug_bundle.py`

A `*.yaat-bug-report-bundle.zip` (v4) wraps a `RecordingArchive` plus client/server logs. Subcommands:

```
info        snapshot   track       actions     history
phases      commands   scenario    weather     layouts
artcc-config logs       install     validate
```

For single-aircraft triage, `history --callsign X` is one chronological view that replaces 5+ targeted `snapshot --at` calls. See `.claude/skills/bug-bundle/SKILL.md` for the full reference and CLAUDE.md for examples.

Bundles embed a room-scoped, anonymized `yaat-server.log`, including for **remote** servers (not just a local disk read).
Server-side: `RoomLogStore` (Simulation) is a per-room bounded ring buffer (50k-line cap, marks earlier lines dropped) that
`FileLogger` mirrors lines into only while inside `BeginRoomScope` (tick loop, `SendCommand`, CRC dispatch) — unscoped lines are
file-only and never exported. `SessionLogAnonymizer` (pure/static) replaces each participant's CID + real name + initials with a
stable pseudonym (`A0`..`B9`, whole-word matched so CIDs embedded in beacon codes/callsigns survive) before
`TrainingHub.GetSessionServerLog()` returns the text. Client: `ServerConnection.GetSessionServerLogAsync()` →
`MainViewModel.Timeline` always embeds the text into the archive. Tests: `RoomLogStoreTests`, `FileLoggerRoomScopeTests`,
`SessionLogAnonymizerTests`.

## Pitfalls

- **`ReplayFromStartTo` is not a step function.** It resets to t=0 and replays forward every call — looping it is `O(N²)` and trips assertions like `MagneticDeclination` cache mismatches. To step, use `ReplayOneSecond()`. To advance from the current time to a later one, use `FastForwardTo(target, actions)` (it throws on rewind, which is the whole point — silent rewinds were the original footgun).
- **Bundles need ARTCC config.** v4 bundles include ARTCC config (`HasArtccConfig` flag); replay reads it into `Scenario.ArtccConfig`. Older test fixtures without it must call `TestArtccConfig.LoadZoa()`.
- **Rewind and snapshot generation reconstruct through the *server-side* tick, not the Sim-only replay.** Server-side track state (auto-track ownership, delayed handoffs, auto-accept) lives in `TickProcessor` (`ProcessDelayedHandoffs`/`ProcessAutoAccept`/`ProcessDeferredAutoTrack`), which the Sim-only `ReplayRangeCore` never runs. So `RecordingManager.RewindAsync` and snapshot generation (`RoomEngine.CreateTempReplayEngine` → `RecordingManager.GenerateSnapshotsViaServerTick`) drive reconstruction via `RecordingManager.ReconstructViaServerTick` — a per-second loop of `RoomEngine.AdvanceOneSecond()` + post-tick recorded-action replay, the same machinery the live forward-playback loop uses. The temp room reloads via `ScenarioLifecycleService.ReloadForRewind` (the sync sibling of `ReloadForRewindAsync`) so `StudentPosition`/`AtcPositions`/auto-track conditions resolve. Using the old bare Sim-only replay here was issue #188: rewind reverted ownership to the start-of-file auto-track owner and re-queued every aircraft's delayed handoff to the student, and generated snapshots captured `Track.Owner = null`. Reconstruction runs with `IsBroadcastSuppressed = true`; the strip/TDLS broadcasters honor that flag so reconstruction doesn't spam phantom strips/PDCs.
- **Replaying a command must mirror live's post-dispatch state, not just the dispatch.** Both replay paths — `SimulationEngine.ReplayCommand` (Sim/client) and `RecordingManager.ReplayCommand` (server reconstruction) — call `CommandDispatcher.DispatchCompound`, but the live `SendCommand`/`SendCommandAsync` paths do more after a successful dispatch. In particular they call `PilotInitialContactEligibility.RegisterControllerContact`, which establishes the two-way comms that clears a solo Class B/C boundary hold. Both replay paths must call it too (on the immediate *and* reaction-delay/deferred branches), or a reconstructed/replayed vector leaves the gate unsatisfied and the aircraft spuriously orbits — diverging from the live session.
- **Sim-side replay restores the student position from snapshot 0.** The scenario JSON does not carry the resolved runtime student position (the server sets it at load via `InitializeTrackPositions`; the server reconstruction path re-derives it through `ReloadForRewind`). The Sim-only `ReplayWithScenarioOverride` loads only the scenario JSON, so it restores `StudentPosition`/`StudentTcp`/`StudentPositionType`/`IsStudentTowerPosition` from `SessionRecording.StudentPositionState` (populated by `RecordingArchive` from snapshot 0). Without it, `CanInitiateWithStudent` and the proactive check-in misbehave and many solo behaviors desync on client playback. Legacy recordings without snapshots carry no student position (null) and replay as before.
- **`Ground.Layout` doesn't round-trip.** Only the `LayoutAirportId` does. If a restore is missing a layout, that airport's GeoJSON wasn't loaded — fix the loader, don't add the layout to the DTO.
- **Don't add `[JsonIgnore]` and call it done.** If state matters across a session, it should serialize. CRC display state in particular must be wired through `ToSnapshot`/`FromSnapshot` — don't defer with "runtime-only" (see [crc-display-state.md](crc-display-state.md) and the `feedback_serialize_display_state` memory).
- **Build a diagnostic, don't grep.** When investigating "X diverges from Y over time," `ReplayRangeWithVerification` will find the first divergent snapshot in one pass. Five targeted `snapshot --at` calls is a sign you should be writing a diff iterator instead.
- **Schema bumps can be free.** A new optional field with a clean default doesn't need a migration step. Only bump `SchemaVersion` when old data needs transformation.
