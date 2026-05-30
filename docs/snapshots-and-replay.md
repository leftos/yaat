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

**Rule for adding a field**: if it defaults cleanly (`null` / `false` / `0`) and old data is correct under that default, **no migration step needed**. Just add it to the DTO. If old data needs transformation (rename, split, reinterpret), bump `SchemaVersion` and add a `Migrate()` step.

## What is NOT serialized

Some state is intentionally runtime-only:

- **`AircraftState.DeclinationCachePosition`** — `null` means "not cached"; warms up on the first tick after a round-trip.
- **`Ground.Layout`** is `[JsonIgnore]`. Only `Ground.LayoutAirportId` round-trips. On restore, `SimulationEngine` re-resolves the layout from the airport ID against the loaded ground graphs. This avoids embedding an entire taxiway graph per aircraft.
- **`PendingObservations`** (pilot "watch for condition" state) — ephemeral, never restored.
- **`EramPointoutState`** — runtime-only, consistent with other ERAM per-track state.

If you see `[JsonIgnore]` on a field, also check that there's a separate carrier (like `LayoutAirportId`) that lets restore reattach.

## Phase polymorphism

`PhaseDto` is the abstract base; every concrete phase DTO has a `[JsonDerivedType(typeof(XyzPhaseDto), "Xyz")]` registration. See [phases.md](phases.md) for the four steps required to add a new phase to the snapshot system.

## Recording — `RecordingArchive.cs`

A recording is a ZIP with this layout:

```
manifest.json                # Version, RngSeed, ActionCount, HasWeather,
                             # HasArtccConfig, ArtccId, ScenarioId/Name,
                             # Snapshots[], LayoutAirportIds[]
scenario.json.br             # Brotli-compressed scenario JSON
actions.json.br              # Brotli-compressed RecordedAction[]
snapshots/NNN.json.br        # one per snapshot index
layouts/{AirportId}.json.br  # deduplicated ground layouts (optional)
weather.json                 # plain JSON (optional; gated by HasWeather)
artcc-config.json.br         # ARTCC config JSON (optional; HasArtccConfig)
```

**`RecordedAction`** is a discriminated union via `[JsonDerivedType]`. The common members are `(ElapsedSeconds, $type)`; concrete types add their fields:

- `RecordedCommand(Callsign, Command, Initials, ConnectionId)` — every user command (including ones rejected at validation; replay is faithful to history).
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

## Pitfalls

- **`ReplayFromStartTo` is not a step function.** It resets to t=0 and replays forward every call — looping it is `O(N²)` and trips assertions like `MagneticDeclination` cache mismatches. To step, use `ReplayOneSecond()`. To advance from the current time to a later one, use `FastForwardTo(target, actions)` (it throws on rewind, which is the whole point — silent rewinds were the original footgun).
- **Bundles need ARTCC config.** v4 bundles include ARTCC config (`HasArtccConfig` flag); replay reads it into `Scenario.ArtccConfig`. Older test fixtures without it must call `TestArtccConfig.LoadZoa()`.
- **`Ground.Layout` doesn't round-trip.** Only the `LayoutAirportId` does. If a restore is missing a layout, that airport's GeoJSON wasn't loaded — fix the loader, don't add the layout to the DTO.
- **Don't add `[JsonIgnore]` and call it done.** If state matters across a session, it should serialize. CRC display state in particular must be wired through `ToSnapshot`/`FromSnapshot` — don't defer with "runtime-only" (see [crc-display-state.md](crc-display-state.md) and the `feedback_serialize_display_state` memory).
- **Build a diagnostic, don't grep.** When investigating "X diverges from Y over time," `ReplayRangeWithVerification` will find the first divergent snapshot in one pass. Five targeted `snapshot --at` calls is a sign you should be writing a diff iterator instead.
- **Schema bumps can be free.** A new optional field with a clean default doesn't need a migration step. Only bump `SchemaVersion` when old data needs transformation.
