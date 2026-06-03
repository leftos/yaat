# Scenario Loading and Aircraft Generation

> Read this before touching `ScenarioLoader`, `AircraftInitializer`, `AircraftGenerator`, `SpawnParser`, `GroundSpawnSnap`,
> `SimulationEngine`'s spawn/queue processors, or `ScenarioLifecycleService` (yaat-server) — or before diagnosing any "aircraft
> spawned in the wrong state / wrong route / wrong type / wrong beacon" bug. Two disjoint pipelines build an aircraft's spawn
> state, and four runtime queues fire spawns and presets after load. This doc maps both so you don't cold-read 3000 LOC to
> answer "how did this aircraft reach this state at t=0".

> **Related:** [`hold-for-release.md`](hold-for-release.md) covers how hold-for-release gates the spawn/queue path — a held
> runway/airborne departure is skipped in `ProcessDelayedSpawns`, and a held ground departure is marked at spawn.

A spawn-state bug almost always traces back to *how* the aircraft was constructed at spawn, and that answer is split across two
pipelines that share almost no code:

1. **Scenario load** — the ATCTrainer-format scenario JSON is deserialized, and each `ScenarioAircraft` becomes an
   `AircraftState` via `ScenarioLoader`. This is the bulk of every scenario.
2. **On-the-fly generation** — the `ADD` command and arrival generators synthesize an aircraft from a terse `SpawnRequest`
   via `AircraftGenerator`, picking a type, airline, and callsign procedurally.

Both pipelines feed the same `SimulationWorld`, but they build state differently: the loader copies an authored flight plan and
route; the generator invents one. After load, four runtime queues on `SimScenarioState` fire delayed spawns, generated arrivals,
timed presets, and global triggers, one check per sim-second in PrePhysics ([tick-loop.md](tick-loop.md)).

## The two pipelines and the four runtime queues

| | Scenario load (`ScenarioLoader`) | Generation (`AircraftGenerator`) |
|---|---|---|
| Trigger | `LoadScenario` / `LoadScenarioAsync` at session start | `ADD` command, or an arrival `ScenarioGeneratorConfig` firing each interval |
| Input | `ScenarioAircraft` (authored JSON) | `SpawnRequest` (parsed from `ADD` args, or built by a generator) |
| Type / callsign | copied from JSON | invented from weight+engine bucket, airline fleet, N-number/airline rules |
| Flight plan | authored departure/destination/route | blank for VFR cold call; synthetic for IFR |
| Phase list | seeded per spawn-condition type | seeded per `SpawnPositionType` (same `AircraftInitializer`) |

Both pipelines share `AircraftInitializer` (runway/parking/final pose + phase list) and `GlideSlopeGeometry` (on-final altitude).

The four runtime queues live on `SimScenarioState` (`src/Yaat.Sim/Simulation/SimScenarioState.cs:19`) and the entry classes are
in `src/Yaat.Sim/Simulation/ScenarioQueues.cs`:

| Queue | Element | Fired by | What it does |
|---|---|---|---|
| `DelayedQueue` | `DelayedSpawn` | `ProcessDelayedSpawns` | spawn a loaded aircraft when `ElapsedSeconds >= SpawnAtSeconds` |
| `Generators` | `GeneratorState` | `ProcessGenerators` | synthesize and spawn an arrival each interval until exhausted |
| `PresetQueue` | `ScheduledPreset` | `ProcessTimedPresets` | dispatch a per-aircraft preset command at its fire time |
| `TriggerQueue` | `ScheduledTrigger` | `ProcessTriggers` | run a global scenario command (SQALL/SNALL/SSALL) at its fire time |

(`DelayedHandoffQueue` also lives there but belongs to track/handoff scheduling, not spawn — out of scope here.) All four are
drained in `SimulationEngine.TickPrePhysics` (`src/Yaat.Sim/Simulation/SimulationEngine.cs:465`) in the fixed order
delayed-spawns → generators → triggers → timed-presets, before physics runs.

## Scenario JSON model (ATCTrainer format)

`src/Yaat.Sim/Scenarios/ScenarioModels.cs` mirrors the ATCTrainer scenario JSON consumed via the vNAS data-api. The top-level
`Scenario` holds:

- `Aircraft` — `List<ScenarioAircraft>`, the authored traffic.
- `InitializationTriggers` — timed global commands.
- `AircraftGenerators` — `List<ScenarioGeneratorConfig>` arrival generators.
- `Atc` — ATC positions to resolve (student + auto-track owners).
- `PrimaryAirportId`, `PrimaryApproach`, `StudentPositionId`, `AutoDeleteMode`, `MinimumRating`, `FlightStripConfigurations`.

Each `ScenarioAircraft` carries `AircraftId` (the callsign), `AircraftType` (the *actual* physical type), `TransponderMode`,
`StartingConditions`, `OnAltitudeProfile`, an optional `FlightPlan`, `PresetCommands`, `SpawnDelay`, `AirportId`,
`AutoTrackConditions`, and `ExpectedApproach`. `StartingConditions.Type` is the discriminator — one of `Coordinates`, `FixOrFrd`,
`OnRunway`, `OnFinal`, `Parking` — and the other `StartingConditions` fields (`Fix`, `Coordinates`, `Runway`, `Altitude`,
`Speed`, `NavigationPath`, `Heading`, `Parking`, `DistanceFromRunway`) are read selectively per type.

> `ScenarioGeneratorConfig` is named to avoid colliding with the runtime `AircraftGenerator` static class in the same namespace
> (note in `ScenarioModels.cs:197`). It's the JSON config; `AircraftGenerator` is the synthesizer; `GeneratorState` is the
> per-generator runtime cursor.

Real example JSONs live in `docs/atctrainer-scenario-examples/`. Parse coverage is asserted by `VnasScenarioParseTests`
(see [scenario-validation.md](scenario-validation.md)).

## Load pipeline and the immediate / delayed / deferred split

`ScenarioLoader.Load(json, groundData, rng)` (`src/Yaat.Sim/Scenarios/ScenarioLoader.cs:49`) deserializes the JSON, then for
each `ScenarioAircraft` calls `LoadAircraft`, which returns a `LoadedAircraft` (or `null` when the spawn is malformed and
unrecoverable, e.g. `Coordinates` with no coordinates). Each `LoadedAircraft` routes into one of three buckets by the
load-time triage at `ScenarioLoader.cs:71`:

- **Deferred** (`DeferralReason is not null`) — the spawn *could not be positioned* (missing/unknown runway, missing ground data,
  parking spot not found). The aircraft still gets a `CreateBaseState` (so it shows in lists) but no position/phase; it is built
  by `BuildDeferredAircraft` (`:550`). The server never auto-spawns these — they sit in `ScenarioLoadResult.DeferredAircraft`
  as broadcast-only entries (`ScenarioLifecycleService.cs:240`).
- **Delayed** (`SpawnDelaySeconds > 0`) — positioned correctly but held in `DelayedQueue` until its delay elapses.
- **Immediate** (everything else) — added to the world and its presets dispatched at load.

`ScenarioLoadResult` also surfaces `HasParkingSpawns` (any `Parking` spawn without a TAXI preset — drives the solo-training
parking call-up source gate) and `HasArrivalGenerators`.

The server's `LoadScenarioAsync` (`ScenarioLifecycleService.cs:180`) iterates the three buckets: immediate aircraft are added
to the world and `DispatchPresetCommands` runs synchronously; delayed aircraft are queued; deferred aircraft only have their
`ScenarioId` stamped and are reported in the manifest. `SimulationEngine.LoadScenario`
(`SimulationEngine.cs:378`) is the standalone (test/replay) equivalent.

## The five spawn-condition types and phase-list seeding

`LoadAircraft` (`ScenarioLoader.cs:199`) switches on `StartingConditions.Type`:

| Type | Position source | Phase seeding | Notes |
|---|---|---|---|
| `Coordinates` | explicit lat/lon | none until ground check (below) | airborne unless near field elevation at 0 kt |
| `FixOrFrd` | `FrdResolver.Resolve(fix)` | none until ground check | accepts a fix name or an FRD string |
| `OnRunway` | `AircraftInitializer.InitializeOnRunway` | `LinedUpAndWaiting → Takeoff → InitialClimb` | helicopter swaps in `HelicopterTakeoffPhase` |
| `OnFinal` | `AircraftInitializer.InitializeOnFinal` | `FinalApproach(SkipInterceptCheck) → Landing` | helicopter swaps in `HelicopterLandingPhase` |
| `Parking` | `layout.FindParkingByName ?? FindSpotByName` | `AtParkingPhase` | sets `Ground.AutoDeleteExempt` and `IsScriptedDeparture` |

`Coordinates` and `FixOrFrd` share a code path: after resolving position/altitude/speed, if `speed <= 0` and altitude is within
200 ft of field elevation, the aircraft is marked `IsOnGround`, given an `AtParkingPhase`, and assigned a ground layout
(`ScenarioLoader.cs:277`). Speed defaults are subtle: when **both** altitude and speed are omitted, speed is `0` (a ground
spawn); otherwise an omitted speed is `-1`, later resolved to `AircraftPerformance.DefaultSpeed` for the type/category/altitude.

`AircraftInitializer` (`src/Yaat.Sim/Scenarios/AircraftInitializer.cs`) is the shared phase/pose builder:

- `InitializeOnRunway` (`:28`) places the aircraft at the runway threshold, heading down the runway, `IsOnGround=true`, speed 0.
- `InitializeAtParking` (`:51`) places it at the parking node, using the node's `TrueHeading` (or 0).
- `InitializeOnFinal` (`:71`) computes distance from `DistanceFromRunway`, else from requested altitude via the glideslope, else
  defaults to 5 nm; positions the aircraft on the extended centerline; sets approach speed scaled by distance
  (`<=5 nm → final approach speed`, `<=10 nm → ×1.4`, beyond → ×1.6). The glideslope angle comes from
  `GlideSlopeGeometry.AngleForCategory` (3° standard, 6° helicopter; `src/Yaat.Sim/Phases/GlideSlopeGeometry.cs:17`).

See [phases.md](phases.md) for what each seeded phase does once the aircraft starts ticking, and
[aircraft-data-model.md](aircraft-data-model.md) for the `AircraftState` / `PhaseList` shapes.

## `CreateBaseState`, approach inheritance, beacon assignment

`CreateBaseState(ac, primaryAirportId, primaryApproach)` (`ScenarioLoader.cs:108`) builds the shared `AircraftState` skeleton —
callsign, type, airport id, transponder, flight plan, approach — that all five spawn types then fill in with position/phase.
Three decisions matter:

- **Actual vs filed type.** `AircraftType` (top-level) is the *physical* type and always wins for performance. The filed FP type
  (`FlightPlan.aircraftType`) is opt-in: `FlightPlan.AircraftType` is only populated when the JSON explicitly sets it.
  `EquipmentSuffix` derives from the filed string when present, else from the actual type, via `ExtractSuffix` (`:1233` — splits
  on `/`, else `"A"`). A cold-call aircraft (no `flightplan` block at all) gets a blank suffix.
- **Approach inheritance.** `PrimaryApproach` is only inherited when the aircraft has no `ExpectedApproach` of its own **and** its
  destination matches the primary airport (or has no destination, or there's no primary airport). The destination match
  normalizes the K-prefix via the local `NormalizeAirportCode` (`:194`). An aircraft destined elsewhere never inherits the
  primary's approach, even if the same approach id exists at its destination.
- **Beacon.** `AssignSpawnBeacon` (`:180`): an aircraft with a filed FP gets a discrete code from
  `SimulationWorld.GenerateBeaconCode(rng)`; a cold-call aircraft gets no `AssignedCode` and squawks 1200 (VFR conspicuity).
  `InferFlightRules` (`:161`) returns the explicit `rules` if set, else `VFR` when there's no FP or cruise altitude ≤ 0, else
  `IFR`.

`DispatchPresetCommands` also backfills a missing destination for *filed* plans to the primary airport
(`SimulationEngine.cs:2105`) so arrivals show in STARS lists; cold-call aircraft are left destination-less until a controller
files via `DA`/`VP`.

## Navigation route building and the RouteExpander mismatch footgun

`PopulateNavigationRoute` (`ScenarioLoader.cs:560`) turns `StartingConditions.NavigationPath` into the aircraft's
`Targets.NavigationRoute`. It expands the path with `RouteExpander.Expand(navigationPath, navDb, includeAllTransitionsOnMismatch:
false)` (`:570`), resolves each fix to a position (dropping unresolvable fixes with a warning, collapsing adjacent duplicates),
appends CIFP STAR runway-transition fixes (`AppendStarRunwayTransition`), then chains the remaining filed route via
`RouteChainer.AppendRouteRemainder`.

> **The `includeAllTransitionsOnMismatch: false` argument is load-bearing.** Passing `true` for a flying route makes a
> radar-vectors SID (NIMI5/OAK6) fabricate a turn-back through every synthetic `[OAK, …]` transition fix. The scenario loader is
> in the "navigation route" caller class and must pass `false`. The full explanation of this footgun — the data shape, the
> emit-all-vs-emit-nothing decision, and the complete caller table — lives in
> [navigation-database.md](navigation-database.md#the-rv-sid-footgun). Do not restate it; if you add a new route-building call
> site here, follow that doc's rule.

Heading derivation runs after route population (`ScenarioLoader.cs:295`): a scenario-assigned `Heading` (magnetic) is converted
to true via `MagneticDeclination.MagneticToTrue`; else the heading points at the first nav-route fix; else 0.

## SID / STAR version-upgrade substitution

Before route population, `ResolveVersionChanges(navigationPath, state, warnings)` (`ScenarioLoader.cs:607`) walks the nav-path
tokens and upgrades stale procedure versions in place. For each non-numeric token it calls `navDb.ResolveSidId` / `ResolveStarId`
(which strip the version digit and match the current cycle — see [navigation-database.md](navigation-database.md)); when the
resolved id differs from the raw one, it:

1. Replaces the procedure token in both the nav path and `FlightPlan.Route`.
2. Checks the adjacent transition fix (the **next** token for a SID exit, the **preceding** token for a STAR entry). If that fix
   is no longer on the upgraded procedure (`IsFixOnSid` / `IsFixOnStar`, which check NavData body + enroute transitions + CIFP
   runway-transition legs), it substitutes the geographically **closest** valid transition fix
   (`FindClosestTransitionFix` / `…ToPosition`). When the old fix isn't even in the navdb, it falls back to the fix beyond it as
   the geographic reference.

The modified path is returned and `state.FlightPlan.Route` is updated in place. This is why a scenario filed against an older
AIRAC still routes correctly without re-authoring.

## The descend-via altitude-profile overlay

When `ScenarioAircraft.OnAltitudeProfile` is true, `ApplyAltitudeProfile(state, navigationPath, warnings)`
(`ScenarioLoader.cs:1072`) acts as an auto-DVIA at spawn. It finds the STAR token in the path, resolves the CIFP STAR
(`navDb.GetStar`), builds an ordered leg list (common legs + the runway transition selected by `FindRunwayTransition` — explicit
designator → `DestinationRunway` → first available), resolves those legs to `NavigationTarget`s via
`DepartureClearanceHandler.ResolveLegsToTargets`, then **overlays** the altitude/speed restrictions onto the matching fixes
already in `Targets.NavigationRoute` (matching by name). It sets `Procedure.ActiveStarId` and `Procedure.StarViaMode = true`, and
immediately applies the first constrained fix's restrictions via `FlightPhysics.ApplyFixConstraints` so the aircraft starts
descending toward the first constraint at spawn rather than after coasting through unconstrained fixes. If the path names a
procedure-shaped token that NavData doesn't know, it warns and applies nothing.

## `GroundSpawnSnap` for coordinate ground spawns

`Coordinates` / `FixOrFrd` ground spawns express "ready to taxi from this point" — but the authored coordinates are usually a
few feet off the nearest taxiway edge, which would force any subsequent `TAXI` to cut diagonally across terrain. After heading
derivation (`ScenarioLoader.cs:331`), `GroundSpawnSnap.Apply(state, layout)`
(`src/Yaat.Sim/Data/Airport/GroundSpawnSnap.cs:48`) snaps an on-ground aircraft onto the nearest taxi edge and rotates its
heading to that edge's bearing — choosing the edge direction closer to the aircraft's original heading as a tiebreaker. It runs
at load time (before the first tick) so a paused-at-load scenario shows the snapped pose with no visible teleport. It is bounded
by `MaxSnapDistanceFt = 200.0` (`:39`); beyond that, or when no edge is found, it logs a warning and leaves the pose unchanged.
It does **not** apply to `Parking` (already on a graph node), `OnRunway`/`OnFinal`, or airborne aircraft.

## The `ADD` command grammar and trailing-override slot-skip

`SpawnParser.Parse(args)` (`src/Yaat.Sim/Scenarios/SpawnParser.cs:5`) parses the `ADD` argument string into a `SpawnRequest`. The
grammar is `{rules} {weight} {engine} {position…} [type] [*airline]`:

- **rules** — `I`/`IFR` or `V`/`VFR`.
- **weight** — `S` (small), `L` (large), `H` (heavy).
- **engine** — `P` (piston), `T` (turboprop), `J` (jet). `ValidateCombo` (`:338`) rejects the three impossible combos
  (Heavy+Piston, Heavy+Turboprop, Small+Jet).
- **position** — index 3 onward, one of five variants (see table below).

The trailing overrides (an explicit type like `B738`, an airline like `*UAL`) are parsed **right-to-left from the end, stopping
at index 4** (`:41`). The stop-at-4 is the slot-skip that protects the first position token (index 3): a runway like `28R` looks
exactly like an ICAO type (3–4 chars, letter+digit, per `IsLikelyAircraftType` at `:355`), but at index 3 it is the position, not
a type override. (This is the same left-to-right-vs-right-to-left slot-skip discipline used elsewhere for optional trailing args.)

Position variants, dispatched from the position tokens at `SpawnParser.cs:66`:

| Leading token | Variant | `SpawnPositionType` | Required tail |
|---|---|---|---|
| `-{bearing}` | bearing | `Bearing` | `{distance} {altitude}` (inbound to primary airport) |
| `@{fix}` + numeric | at-fix | `AtFix` | `{altitude}` (fix or FRD) |
| `@{name}` (no numeric) | parking | `Parking` | parking/helipad name |
| `{runway}` alone | lined up | `Runway` | — |
| `{runway} {n}` (numeric) | on final | `OnFinal` | `{distance_nm}` |
| `{runway} {route}` (non-numeric) | departure lined up | `Runway` | dot-joined route (e.g. `NIMI6.OAK.SAU` → space-joined) |

The `@`-prefix disambiguation (`:71`) is the trickiest: `@FIX 5000` (a numeric second token) is an at-fix airborne spawn;
`@GATE3` or `@GATE3 something-non-numeric` is a parking spawn. The `SpawnRequest` shape and field-per-variant usage is in
`src/Yaat.Sim/Scenarios/SpawnRequest.cs`.

## `AircraftGenerator`: buckets, airline-fleet coupling, fallback chain, callsigns

`AircraftGenerator.Generate(request, primaryAirportId, existingAircraft, groundLayout, rng)`
(`src/Yaat.Sim/Scenarios/AircraftGenerator.cs:124`) is the synthesizer. `GenerateCore` (`:164`) does, in order:

1. **Airline first (IFR only).** For IFR, pick the airline *before* the type so the type can be constrained to that airline's
   real fleet (`:178`). Resolution order: explicit `*airline` override → `PickCompatibleAirportAirline` (weighted by the
   airport's `AirportAirlines` arrival counts, `√(arrivals)`-weighted, `:715`) → `PickCompatibleAirline` (any curated airline
   with fleet overlap, `:703`). VFR uses N-numbers and has no airline.
2. **Type.** `ResolveType` (`:566`): an explicit type wins outright. Otherwise it picks from the `TypeTable` bucket for
   `(weight, engine)` (`:17`). When the exact bucket exists and the airline has fleet data, it filters to types the airline
   actually operates (per `AirlineFleets`); if there's no overlap it logs a warning and uses the full bucket pool — it does
   **not** silently swap engine type. When the exact bucket is *empty* (e.g. `Heavy+Piston`), it walks
   `EnumerateBucketFallbackChain` (`:643`): exact → same engine, nearest weights → same weight, other engines → any remaining.
   **Engine wins over size** so a piston request resolves to a piston type whenever any piston bucket has entries. The airline
   filter is dropped on the fallback chain.
3. **Callsign.** `GenerateCallsign` (`:685`): VFR or airline-less → `GenerateNNumber` (FAA N-number, leading digit 1–9, up to 2
   trailing letters, `I`/`O` excluded); else `GenerateAirlineCallsign` (`{airline}{3-or-4 digits}`). Both retry up to 100 times
   against the existing-callsign set, then fall back to a wide random.
4. **Beacon / transponder.** Parking spawns sit on `Standby`; everything else squawks Mode C. VFR gets no assigned code and
   squawks 1200; IFR gets a discrete `GenerateBeaconCode`.
5. **Position.** Switches on `SpawnPositionType` to the matching `Generate*` method, each of which uses the same
   `AircraftInitializer` / `GlideSlopeGeometry` as the loader.

The bucket↔fleet coupling is validated at startup by `AssertEveryTypeResolves` (`:42`): every `TypeTable` type must resolve
through both `AircraftProfileDatabase` and `AircraftCategorization` (with the expected category), and every curated airline in
`Airlines` must exist in `AirlineFleets`. It throws loudly rather than degrading to category-default performance or pairing
`SWA` with an `A320`. This table is tuned often — keep it in sync with the data DBs or the startup assertion fails.

`BuildAddFlightPlan` (`:101`) encodes the cold-call vs filed split: a VFR `ADD` builds a blank `AircraftFlightPlan`
(`HasFlightPlan=false`) — the controller files later; an IFR `ADD` builds a filed plan with the generated type so STARS/strips
have a non-blank readout. IFR runway/final spawns canonicalize the departure/destination airport via
`NavigationDatabase.TryResolveAirport` so a filed SID route resolves (`:416`, `:475`).

## Runtime firing of the four queues

All four drain in `TickPrePhysics` (`SimulationEngine.cs:465`) once per sim-second:

- **`ProcessDelayedSpawns`** (`:1669`) — iterates `DelayedQueue` backward; when `ElapsedSeconds >= SpawnAtSeconds`, removes the
  entry, stamps `SpawnedAtSeconds`, adds to world, dispatches presets, emits a `[Spawn] Delayed` terminal line and any
  auto-track messages.
- **`ProcessGenerators`** (`:1698`) — skipped entirely during replay/playback when recorded aircraft spawns exist (those are
  replayed verbatim, not regenerated). For each non-exhausted generator past its `NextSpawnSeconds`: if past `MaxTime` it
  exhausts; otherwise it builds an `OnFinal` `SpawnRequest` at the generator's current `NextSpawnDistance`, calls
  `AircraftGenerator.Generate`, adds the aircraft, records the spawn for replay (`RecordGeneratedAircraftSpawn`), and advances
  the generator (`AdvanceGenerator`, `:1787` — bumps `NextSpawnSeconds` by the pacing-scaled interval with optional ±25% jitter,
  and steps `NextSpawnDistance`, wrapping at `MaxDistance` back to `InitialDistance`). The solo-training arrival-rate percent
  (via `ScenarioPacing`) can clamp the generator off entirely.
- **`ProcessTriggers`** (`:1994`) — fires `ScheduledTrigger`s whose `FireAtSeconds` elapsed via `ExecuteGlobalCommand`, which
  only handles the global squawk commands (`SQALL`/`SNALL`/`SSALL`).
- **`ProcessTimedPresets`** (`:1931`) — fires `ScheduledPreset`s whose `FireAtSeconds` elapsed: parses the command with
  `CommandParser.ParseCompound`, then dispatches through the **live** `CommandDispatcher.DispatchCompound` against a world
  snapshot (see next section).

The server's `HandleSpawnNow` / `HandleSpawnDelay` (`ScenarioLifecycleService.cs:486` / `:525`) let an instructor pull a delayed
aircraft into the world early or re-time it; `HandleSpawnAircraftAsync` (`:547`) is the `ADD`-command entry point that parses,
generates, applies scratchpad rules, and broadcasts the spawn.

## Preset pre-execution through the LIVE dispatcher

Presets are not a separate execution path — they run through the same `CommandDispatcher.DispatchCompound` that controller
commands use ([command-pipeline.md](command-pipeline.md)). `DispatchPresetCommands(loaded)` (`SimulationEngine.cs:2095`) runs at
spawn:

1. Invokes the optional `PresetOverride` hook (tests use it to rewrite presets).
2. Backfills a missing destination for filed plans (above).
3. Splits presets by `TimeOffset`: `> 0` presets go to `PresetQueue` (fired later by `ProcessTimedPresets`); `0` presets run
   immediately.
4. Dispatches each immediate preset via `DispatchSinglePreset` (`:2063`), building a full `DispatchContext` — including the live
   `TerminalEmitter` (`_terminalEntries.Add`), RNG, weather, aircraft lookup, and the scenario's `ValidateDctFixes` /
   `AutoCrossRunway` / `SoloTrainingMode` / `RpoShowPilotSpeech` flags.

### The CFIX composition case

`CFIX` is **additive** — `DispatchCrossFix` stamps the restriction on the named route fix in place, preserving the rest of the
route and any restriction already on another fix. So multiple `CFIX` presets are dispatched independently and all their crossing
restrictions land at spawn simultaneously.

Composition is still needed for the **mixed** case: when a `CFIX` is followed by a non-`CFIX` command (e.g. `CFIX ...; CAPP`).
Dispatched separately, the later block (`CAPP`) would rebuild the route and lose the `CFIX` restrictions, so the presets are
**composed into one compound command** joined with `; ` and dispatched as a single `DispatchSinglePreset` call — the later block
then waits in the `CommandQueue` until the crossing fix is reached. The composition path therefore triggers only when the presets
are *not* all `CFIX` and the first one is a `CFIX`. This is the one place preset dispatch deviates from "one command at a time."

Because presets go through the live dispatcher, they obey the same dry-run-validate / dimension-clearing / phase-gate rules as
typed commands — a malformed preset is rejected and logged (`[Preset] Unparseable`), not silently dropped.

## Server orchestration and the rewind-reload twin path

There are two server entry points that build a scenario, and they are deliberately near-identical:

- **`LoadScenarioAsync`** (`ScenarioLifecycleService.cs:107`) — the live load. Picks a fresh `rngSeed`, runs `ScenarioLoader.Load`,
  ensures the ARTCC config is loaded, builds `SimScenarioState`, sets the ground layout, resolves track positions / coordination
  channels / strip bays / TDLS configs, then spawns immediate aircraft (+ presets + auto-track), queues delayed aircraft, stamps
  deferred aircraft, queues triggers, and initializes generators. The ARTCC-tab path does **not** call this directly — instead
  the client first calls the `GetScenarioJsonById` query, which routes to `ResolveGatedJsonAsync` (`:51`): that pulls the
  **canonical** JSON from the server catalog (not the client payload), applies the rating gate against the canonical
  MinimumRating, and returns the JSON. The client then runs the normal difficulty/pacing setup and loads it back via
  `LoadScenario` — so catalog loads get the same difficulty prompt as local-file loads, and client JSON tampering still can't
  obtain gated content (the gate is enforced at fetch).
- **`ReloadForRewindAsync`** (`:638`) — the rewind twin. Takes the **provided** `rngSeed` (not a fresh one) and the saved
  scenario JSON, runs the *same* `ScenarioLoader.Load` + spawn/queue setup, but skips broadcasting (caller sets
  `IsBroadcastSuppressed`) and the pacing-override recording. After reload, the caller in `RecordingManager`
  (`yaat-server: …/RecordingManager.cs:148`) either restores the nearest snapshot and replays the remaining seconds, or replays
  from scratch.

The shared seed is what makes rewind deterministic: re-running `ScenarioLoader.Load` with the same seed reconstructs the exact
immediate-aircraft set, beacon codes, and generated-type choices, so the replayed action log lands on the same aircraft. See
[server-rooms-and-hub.md](server-rooms-and-hub.md) for the room lifecycle around these calls and
[snapshots-and-replay.md](snapshots-and-replay.md) for the replay machinery that runs on top of the reload.

`ExecuteUnloadScenario` (`:345`) is the teardown: it disposes any held recording archive, clears the world and **all four
queues** plus the handoff queue, broadcasts deletes, and resets the engine's evaluators.

## Snapshot / replay survival of the queues

The four queues are part of the scenario snapshot so a rewind checkpoint can restore mid-load-out state.
`SimScenarioState.ToSnapshot()` (`SimScenarioState.cs:120`) serializes:

- `DelayedQueue` → `DelayedSpawnDto` with the `LoadedAircraft` JSON-serialized whole (and `SpawnAtSeconds`).
- `TriggerQueue`, `PresetQueue` → command + fire-time DTOs.
- `Generators` → `GeneratorStateDto` with the config JSON-serialized plus the runtime cursor (`NextSpawnSeconds`,
  `NextSpawnDistance`, `IsExhausted`) and the runway snapshot.

`SimulationEngine.RestoreFromSnapshot` (`SimulationEngine.cs:142`) clears and rebuilds all four queues from the DTOs. The one
runtime-only field that does not survive JSON is `AircraftState.Ground.Layout` (it's `[JsonIgnore]`d in `AircraftGroundOps.cs:20`)
— on restore, the delayed aircraft's layout is **reattached by airport id** from the persisted `LayoutAirportId`
(`SimulationEngine.cs:207`). If you add a runtime-only reference to a queued type, you must reattach it the same way or it comes
back null after a rewind. See [snapshots-and-replay.md](snapshots-and-replay.md) for the full DTO tree and the `[JsonIgnore]`
reattachment pattern.

## Footguns and gotchas

- **`AircraftType` is the physical type; `FlightPlan.AircraftType` is opt-in filed paperwork.** Performance always reads the
  top-level type. Treating the filed FP type as the physical type breaks performance for any scenario that files a different
  type (or none).
- **The `Coordinates`/`FixOrFrd` speed default is mode-dependent.** Both altitude and speed omitted → speed 0 (ground spawn).
  Only one omitted → `-1` → resolved to a category default. Don't assume omitted speed always means a default cruise speed.
- **Approach inheritance is destination-gated.** `PrimaryApproach` only flows to aircraft destined for (or matching) the primary
  airport. An arrival to a different field will never pick it up; don't "fix" a missing approach by widening the inheritance.
- **The route-build call must pass `includeAllTransitionsOnMismatch: false`.** A new flying-route call site that takes the `true`
  default fabricates RV-SID turn-backs. (See [navigation-database.md](navigation-database.md#the-rv-sid-footgun).)
- **`ADD` trailing-override parse stops at index 4, not 3.** A runway token (`28R`) at the position slot is shaped like an ICAO
  type; the right-to-left scan must not consume it. If you change the override parsing, preserve the stop-at-4 slot-skip.
- **Engine wins over size in the generator fallback chain, and the airline filter is dropped there.** An empty exact bucket
  resolves to the nearest same-engine bucket first; do not reorder the chain to prefer size, and don't re-apply the airline
  filter on the fallback (it could pair the wrong airline with an off-bucket type).
- **`AssertEveryTypeResolves` throws at startup.** Adding a `TypeTable` type without an `AircraftProfileDatabase` entry / correct
  category, or an `Airlines` entry without `AirlineFleets` data, crashes the server on boot. Run the data DBs' refresh tools and
  keep the table in sync.
- **Presets run through the LIVE dispatcher** — they obey dry-run validation, dimension-aware clearing, and the phase gate. `CFIX`
  is additive (stamps the named route fix in place), so multiple `CFIX` presets dispatch independently; only a `CFIX` followed by a
  non-`CFIX` command (e.g. `CFIX ...; CAPP`) is composed into one compound so the later block waits for the crossing fix instead of
  rebuilding the route and clearing earlier restrictions.
- **`ProcessGenerators` is suppressed during replay** when recorded spawns exist — generated arrivals are replayed verbatim from
  the action log, not regenerated. Don't add unconditional generator work in the tick path; gate it the same way.
- **Rewind reuses the original `rngSeed`.** Determinism of the rewound session depends on it. Picking a fresh seed in
  `ReloadForRewindAsync` would desync the action log from the reconstructed aircraft.
- **`GroundSpawnSnap` silently no-ops beyond 200 ft.** A coord ground spawn placed far from any taxiway keeps its authored pose
  (with a warning) — the aircraft will not be on a taxi edge, and a subsequent `TAXI` plans from off-graph.

## Checklist: adding a spawn-condition type or `ADD` variant

1. **New `StartingConditions.Type`:** add a `case` in `LoadAircraft` (`ScenarioLoader.cs:218`); build the pose + phase list via a
   new `AircraftInitializer` method (or reuse one); decide ground vs airborne and whether `GroundSpawnSnap` applies; on any
   un-resolvable position, return `BuildDeferredAircraft` rather than `null` so the aircraft still shows in lists.
2. **New `ADD` position variant:** add a `SpawnPositionType` enum value (`SpawnRequest.cs:23`) and the per-variant fields; add a
   `Parse*Variant` branch in `SpawnParser` (mind the `@`/`-`/runway disambiguation and the index-4 slot-skip); add the matching
   `Generate*` method in `AircraftGenerator` and a `case` in `GenerateCore`.
3. **Phase seeding:** wire the seeded phases per [phases.md](phases.md); confirm helicopter variants if the type can be a heli.
4. **Snapshot survival:** if the new spawn type adds a runtime-only reference to a queued aircraft, reattach it by id in
   `RestoreFromSnapshot` exactly like `Ground.Layout`.
5. **Tests:** add a real-data E2E (no synthetic stubs — `TestVnasData.EnsureInitialized`) that loads a scenario exercising the
   new type/variant and asserts the resulting `AircraftState`; for parse changes, add a `SpawnParser` unit test covering the
   ambiguous tokens. See [test-harness.md](test-harness.md).
6. **Docs:** update `COMMANDS.md` for any `ADD` grammar change and this doc for the new type/variant.
