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

The table lists only the steps with a data *transform*; the authoritative full chain (currently through v19) is the `Migrate()` source. `Migrate` must tolerate leniently-deserialized nulls on the fields it touches — e.g. a legacy aircraft snapshot can carry a null `FlightPlan`, so the v3→v4 seed guards with `ac.FlightPlan is { AircraftType: null or "" }` rather than dereferencing.

**Rule for adding a field**: if it defaults cleanly (`null` / `false` / `0`) and old data is correct under that default, **no migration step needed**. Just add it to the DTO. If old data needs transformation (rename, split, reinterpret), bump `SchemaVersion` and add a `Migrate()` step.

**The run profile is not a field.** `SimulationEngine.RunProfile` (live / replay / test / soak — [tick-loop.md](tick-loop.md) § the engine's partial files) is host state: the host that drives the engine sets it, and it is never captured into or restored from a snapshot. Restoring a live snapshot into a replaying room must not make the room live.

**An "on by default" scenario setting still defaults to `false` on the Sim side.** `SimScenarioState.AutoCrossRunway` and `AutoPullUpToParallel` are bare `bool`s (false), the `PhaseContext` fallback is `Scenario?.X ?? false`, and the `ScenarioSnapshotDto` field is the same. The on-by-default lives only in the client's `UserPreferences` (`AutoPullUpToParallel` defaults true there), which pushes the value to the server at scenario bootstrap (`MainViewModel.SendAutoCrossRunway` / `SendAutoPullUpToParallel`). The reason is replay fidelity: `engine.Replay(recording, t)` builds a fresh `SimScenarioState`, so a Sim-side default of `true` would switch the feature on for every recording made before it existed and diverge those replays (aircraft auto-pulling up before the recorded `TAXI`/`RES`). A new opt-out toggle mirrors this pair end to end and flips only the client default.

### Upgrading recordings on disk — surgical, never re-simulated

`SnapshotSchemaMigrator.Migrate` runs at replay (`SimulationEngine.RestoreFromSnapshot`) so old recordings load without any disk change. To rewrite committed fixtures to the current schema, `RecordingSchemaUpgrader.Upgrade(bytes)` (Yaat.Sim) transforms each snapshot's JSON **in place** via the same migrator — decompress → `StateSnapshotDto` → `Migrate` → re-serialize — across all three containers (non-zip `.br` `SessionRecording`, v4 archive zip, and bug-report bundle wrapping a v4 archive), copying every other entry verbatim. The same pass rewrites recorded canonicals that the current grammar no longer accepts — the retired `HSE` half-strip verb becomes the `HSA` id form (`HalfStripEditCanonicalRewriter`) and bay tokens gain their `FACILITY/` qualifier (`StripBayCanonicalQualifier`); both are idempotent so they need no schema gate. It **never re-simulates**; re-simulation with current code would rewrite a frozen pre-fix snapshot into the fixed state and silently invalidate hybrid-replay tests (see [e2e-tdd-issue-debugging.md §5b](e2e-tdd-issue-debugging.md)). The `Yaat.RecordingUpgrader` CLI (yaat-server) drives it and reserves re-simulation for the one v1→v2 bootstrap (v1 carries no snapshots to preserve). Snapshot entries are Brotli. `RecordingCompression.Decompress` autodetects in a fixed order: gzip by its magic number, then Brotli, then plain UTF-8 JSON only when Brotli genuinely cannot read the bytes. It never sniffs the first byte for `{`/`[`, because a Brotli stream can legitimately start with those bytes and a first-byte heuristic misfires on it.

## What is NOT serialized

Some state is intentionally runtime-only:

- **`AircraftState.DeclinationCachePosition`** — `null` means "not cached"; warms up on the first tick after a round-trip.
- **`Ground.Layout`** is `[JsonIgnore]`. Only `Ground.LayoutAirportId` round-trips. On restore, `SimulationEngine` re-resolves the layout from the airport ID against the loaded ground graphs. This avoids embedding an entire taxiway graph per aircraft.
- **`PendingObservations`** (pilot "watch for condition" state) — ephemeral, never restored.
- **`CommandBlock.ApplyAction` / `CommandBlock.ParsedCommands`** — the queued-block closure and its parsed
  commands are runtime-only; `SourceCommandText` is the durable carrier. `SimulationEngine.RehydrateRestoredQueueBlocks`
  (top of `TickPhysics`, shared by both hosts) rebuilds them by re-parsing that text before the queue can fire, so a
  queued instruction survives rewind/replay/restore instead of firing as a silent no-op. An unrecoverable block is
  dropped with an RPO warning. See [command-handlers.md](command-handlers.md).

If you see `[JsonIgnore]` on a field, also check that there's a separate carrier (like `LayoutAirportId`) that lets restore reattach.

**A rebuilt route needs its cursor, not just its nodes.** Navigator-owning ground phases don't serialize their `TaxiRoute` — it is rebuilt from stored node ids against the live layout on the first tick after restore — so the rebuild has to be told *where along it* the aircraft was. `RunwayExitPhase` is the sharp case: its segment 0 is a virtual approach leg [aircraft position → branch node] down the runway centerline, so rebuilding from segment 0 for an aircraft that has already turned off hands `GroundNavigator` a leg pointing *backward*, and the ~180° entry-alignment slow-turn taxis the reconstruction back onto the runway it just vacated (issue #309). `ExitWaypointIndex` round-trips for exactly this reason, `ResumeSegmentIndexAfterRestore` floors it at the first real segment whenever the aircraft is already past the branch (a snapshot can land on the tick before the navigator advances), and past the branch segment 0 is re-anchored on the centerline *behind* it so its arrival bearing is still the runway heading. See [landing-and-runway-exit.md](landing-and-runway-exit.md).

**A general-purpose route may start with a virtual leg.** `AircraftGroundOps.AssignedTaxiRoute` *is* serialized (`TaxiRouteDto`), and `TaxiRoute.FromSnapshot` resolves every segment endpoint by node id against the layout. A ramp-lane reposition (`RampLaneReposition`, issue #396) puts a `VirtualNode` leg — aircraft position → lane node — at segment 0, and a virtual id is never in `layout.Nodes`, which would have nulled the whole route on rewind/replay. `TaxiSegmentDto` therefore carries optional `From/ToLatitude`/`Longitude`, filled only for virtual (negative-id) endpoints; restore rebuilds the virtual node from them and the synthetic edge via `VirtualNode.CreateSegment`. Old snapshots leave the fields null and resolve by id exactly as before — no schema bump, the same pattern as `HoldShortPointDto.Latitude/Longitude`. A destination-end cut (issue #400) is the other shape: a free-space leg between two *layout* nodes with no edge between them. `ToSnapshot` flags it (`TaxiSegmentDto.IsFreeSpace`, from `VirtualNode.IsVirtualEdge`) and `FromSnapshot` rebuilds it from the nodes' own positions; an *unflagged* pair with no edge still voids the route — a bundle whose node ids predate the current parser must drop the route, not sprout stray legs (`VfrFollowSequencesToFinalTests` caught exactly that). `TaxiRouteDto` also carries `DestinationParking` / `DestinationSpot` (nullable, additive) so a restored taxi-to-parking still installs `AtParkingPhase` at the end of the route.

`GroundNavigator` itself is deliberately non-round-tripping below that: it persists no Bézier/arc progress, so a restore mid-fillet replays that arc from its start and the reconstruction runs a second or two behind on the same path. Bounded and self-limiting — but it means a snapshot comparison should check that drift is not *growing*, not that it is zero.

## Phase polymorphism

`PhaseDto` is the abstract base; every concrete phase DTO has a `[JsonDerivedType(typeof(XyzPhaseDto), "Xyz")]` registration. See [phases.md](phases.md) for the four steps required to add a new phase to the snapshot system.

**Never remove a phase's `[JsonDerivedType]` if a committed recording may have captured that phase in a snapshot.** Polymorphic deserialization throws `JsonException: Read unrecognized type discriminator id 'Xyz'` **before** `SnapshotSchemaMigrator` runs, so a version bump / migrator can't rescue it. The failure misleads — affected tests pass in isolation and fail only once a test that replays the offending recording runs (it looks like a static-singleton race but is the discriminator). When a phase is superseded, **retain the old class + its DTO + `JsonDerivedType` for restore-only**: mark it clearly and stop creating it from the command path (e.g. `PushbackToSpotPhase`, kept only for restore after the #233 pushback rewrite). "Unreleased software: delete freely" does **not** extend to types serialized into committed recording fixtures. Verify with `pwsh tools/test-all.ps1` (full suite), not just targeted tests.

## Recording — `RecordingArchive.cs`

A recording is a ZIP with this layout:

```
manifest.json                # Version, RngSeed, MagneticModelDateUtc, ActionCount, HasWeather,
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
- `RecordedLiveTrafficSample(Callsign, Sample, SpawnState?)` — one live-traffic sample for a shadow aircraft; `SpawnState` (the shadow's snapshot) rides only on the sample that created it. **Pre-tick**, like `RecordedAircraftSpawn` (`SimulationEngine.IsPreTickAction`). `RecordedLiveTrafficRemoval(Callsign, Reason)` applies after the second. See [live-traffic.md](live-traffic.md).
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

Runtime aircraft spawns are action-driven during replay. `RecordedAircraftSpawn` actions apply before the tick's generator phase, and old archives that predate those actions synthesize them from snapshot deltas for aircraft that were not declared in the scenario JSON. `RecordedLiveTrafficSample` shares that pre-tick slot (`SimulationEngine.IsPreTickAction`), which is the spine's `OpenSecond` step — `host.ApplyPreTickRecordedActions` — after the clock increment and before pre-physics on every run kind: the `ReplayHost` (`Replay`, `ReplayOneSecond`, `ReplayOneSubTick`), the `ReconstructionHost` (`RecordingManager.ReconstructViaServerTick`) and the `LiveRoomHost` in tape playback (`ApplyPreTickPlaybackActions`). `RecordedSpawnReplayServerTests` guards the spawn half, see [live-traffic.md](live-traffic.md) for the sample half.

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
- **Rewind and snapshot generation reconstruct through the *server-side* tick, not the Sim-only replay.** Server-side track state (auto-track ownership, delayed handoffs, auto-accept) lives in `TickProcessor` (`ProcessDelayedHandoffs`/`ProcessAutoAccept`/`ProcessDeferredAutoTrack`), which the Sim-only `ReplayRangeCore` never runs. So `RecordingManager.RewindAsync` and snapshot generation (`RoomEngine.CreateTempReplayEngine` → `RecordingManager.GenerateSnapshotsViaServerTick`) drive reconstruction via `RecordingManager.ReconstructViaServerTick` — the spine under a `ReconstructionHost`, which fills every room-owned step the live host fills and re-applies the recorded log around each second. The temp room reloads via `ScenarioLifecycleService.ReloadForRewind` (the sync sibling of `ReloadForRewindAsync`) so `StudentPosition`/`AtcPositions`/auto-track conditions resolve. Using the old bare Sim-only replay here was issue #188: rewind reverted ownership to the start-of-file auto-track owner and re-queued every aircraft's delayed handoff to the student, and generated snapshots captured `Track.Owner = null`. Reconstruction runs with `IsBroadcastSuppressed = true`; the strip/TDLS broadcasters honor that flag so reconstruction doesn't spam phantom strips/PDCs.
- **Replaying a command must mirror live's post-dispatch state, not just the dispatch.** Both replay paths — `SimulationEngine.ReplayCommand` (Sim/client) and `RecordingManager.ReplayCommand` (server reconstruction) — call `CommandDispatcher.DispatchCompound`, but the live `SendCommand`/`SendCommandAsync` paths do more after a successful dispatch. In particular they call `PilotInitialContactEligibility.RegisterControllerContact`, which establishes the two-way comms that clears a solo Class B/C boundary hold. Both replay paths must call it too (on the immediate *and* reaction-delay/deferred branches), or a reconstructed/replayed vector leaves the gate unsatisfied and the aircraft spuriously orbits — diverging from the live session.
- **Sim-side replay restores the student position from snapshot 0.** The scenario JSON does not carry the resolved runtime student position (the server sets it at load via `InitializeTrackPositions`; the server reconstruction path re-derives it through `ReloadForRewind`). The Sim-only `ReplayWithScenarioOverride` loads only the scenario JSON, so it restores `StudentPosition`/`StudentTcp`/`StudentPositionType`/`IsStudentTowerPosition` from `SessionRecording.StudentPositionState` (populated by `RecordingArchive` from snapshot 0). Without it, `CanInitiateWithStudent` and the proactive check-in misbehave and many solo behaviors desync on client playback. Legacy recordings without snapshots carry no student position (null) and replay as before.
- **The magnetic model is evaluated at the session's recorded day, not "now".** `SimScenarioState.MagneticModelDateUtc` is set once at load (today for a live session, `RecordingManifest.ResolveMagneticModelDateUtc()` for a loaded archive — the recorded value, else the `RecordedAtUtc` day for pre-feature archives), carried in every `ScenarioSnapshotDto`, the manifest, `SessionRecording`, and the room checkpoint, and fed to every sim-state use of `MagneticDeclination` (aircraft declination via `PhysicsTickOptions`, scenario/spawn magnetic→true headings). Without it a replay a year later drifts by the WMM's secular variation (~0.1°/yr) and byte-identical snapshot comparisons fail. Display-only callers (client, ASDE-X variation, navdata approach courses) still use the process day — they are not replayed state. The one sim-side residual is FRD resolution (`FrdResolver`), which runs inside the command parser with no scenario in scope.
- **The live host and the reconstruction host differ only at the end of the second.** Both run the same spine and the same `TickProcessor` bodies; `LiveRoomHost` samples position history, advances weather behind `HasMeaningfulChange` and issues METARs, while `ReconstructionHost` re-applies recorded weather ungated and does neither of the others. The tick oracle records the position-history difference as an accepted divergence until tick step 5 retires it.
- **`Ground.Layout` doesn't round-trip.** Only the `LayoutAirportId` does. If a restore is missing a layout, that airport's GeoJSON wasn't loaded — fix the loader, don't add the layout to the DTO.
- **Don't add `[JsonIgnore]` and call it done.** If state matters across a session, it should serialize. CRC display state in particular must be wired through `ToSnapshot`/`FromSnapshot` — don't defer with "runtime-only" (see [crc-display-state.md](crc-display-state.md) and the `feedback_serialize_display_state` memory).
- **Build a diagnostic, don't grep.** When investigating "X diverges from Y over time," `ReplayRangeWithVerification` will find the first divergent snapshot in one pass. Five targeted `snapshot --at` calls is a sign you should be writing a diff iterator instead.
- **Schema bumps can be free.** A new optional field with a clean default doesn't need a migration step. Only bump `SchemaVersion` when old data needs transformation.
- **Fillet-minted node ids are geometry-coupled.** Tangent-cut nodes are numbered by the fillet generator at parse time, and snapshot DTOs persist them (`RunwayExitPhaseDto.ExitNodeId`, `LandingPhaseDto.CandidateExitPathNodeIds`, taxi-route segment endpoints). Any fillet-geometry change renumbers or moves those nodes, so a restored phase can be handed a stale path (`[Exit] <callsign>: no edge between nodes A and B`); `RunwayExitPhase` degrades by re-searching from the aircraft's actual position, but every replay-derived expectation (node whitelists, taxiway at time T, event by time T) is coupled to the geometry version. Triage recipes for replay tests broken by a geometry change:
  - *Correct but late* (an exit or arc got longer, a speed profile changed, the event slid past the recorded window): keep the invariant and add a bounded physics-only tail — `ReplayOneSecond` to the window end, then `TickOneSecond` for up to ~60 s, breaking on the awaited state.
  - *Premise change* (an aircraft now legitimately picks a different exit): if the test's subject needs the recorded premise, pin it by injecting a command mid-replay — `Replay(rec, T_before_choice)` → `SendCommand("ER G")` → `ReplayOneSecond` loop. Find T with `python tools/bug_bundle.py history --callsign X`.
  - *Node-id whitelists*: hold-short ids come from the raw graph and are stable; tangent-cut ids are not. Never assert tangent-cut ids — regenerate whitelists from `LayoutInspector --exits`.
