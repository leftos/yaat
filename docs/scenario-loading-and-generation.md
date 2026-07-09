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

It also surfaces `InitialStripBayByCallsign` — the scenario's top-level `flightStripConfigurations`
(entries of `{ facilityId, bayId, rack, aircraftIds }`) resolved to a callsign-keyed
`Dictionary<string, ScenarioStripBayAssignment>`. The config references aircraft by **ULID**
(`ScenarioAircraft.Id`), but the runtime `AircraftState` only carries the callsign back to a config
entry, so `ResolveStripBayAssignments` joins ULID → callsign at load. Both load paths copy it onto
`SimScenarioState.InitialStripBayByCallsign`, where the server's spawn hook
(`TickProcessor.AfterAircraftSpawned`) reads it to drop configured departures straight into their
bay instead of the printer queue (see [`flight-strips.md`](flight-strips.md)).

The server's `LoadScenarioAsync` (`ScenarioLifecycleService.cs:180`) iterates the three buckets: immediate aircraft are added
to the world and `DispatchPresetCommands` runs synchronously; delayed aircraft are queued; deferred aircraft only have their
`ScenarioId` stamped and are reported in the manifest. `SimulationEngine.LoadScenario`
(`SimulationEngine.cs:378`) is the standalone (test/replay) equivalent.

## The five spawn-condition types and phase-list seeding

`LoadAircraft` (`ScenarioLoader.cs:199`) switches on `StartingConditions.Type`:

| Type | Position source | Phase seeding | Notes |
|---|---|---|---|
| `Coordinates` | explicit lat/lon | none until ground check (below) | airborne unless at/near field elevation with no positive authored speed |
| `FixOrFrd` | `FrdResolver.Resolve(fix)` | none until ground check | accepts a fix name or an FRD string |
| `OnRunway` | `AircraftInitializer.InitializeOnRunway` | `LinedUpAndWaiting → Takeoff → InitialClimb` | helicopter swaps in `HelicopterTakeoffPhase` |
| `OnFinal` | `AircraftInitializer.InitializeOnFinal` | `FinalApproach(SkipInterceptCheck) → Landing` | helicopter swaps in `HelicopterLandingPhase` |
| `Parking` | `layout.FindParkingByName ?? FindSpotByName` | `AtParkingPhase` | sets `Ground.AutoDeleteExempt` and `IsScriptedDeparture` |

`Coordinates` and `FixOrFrd` share a code path. The ground check runs **before** the cruise-speed default: if the authored speed is
non-positive (`0` when both altitude and speed are omitted, `-1` when altitude is authored but speed omitted) and altitude is within
200 ft of field elevation, the aircraft is a ground spawn — speed is forced to `0`, it is marked `IsOnGround`, given an
`AtParkingPhase`, assigned a ground layout, and (mirroring the `Parking` path) flagged `Ground.AutoDeleteExempt` +
`IsScriptedDeparture` so a `TAXI` preset fires and it isn't culled under `autoDeleteMode: Parked`. Only an airborne spawn falls
through to `AircraftPerformance.DefaultSpeed`. Order matters: resolving the default first would turn the `-1` sentinel into a
positive cruise speed and spawn a field-elevation departure airborne.

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
- **Beacon.** Assigned by `ScenarioLoader.AssignSpawnBeacons(beaconPool, result.AllAircraftStates)`, a **post-load pass** rather
  than an inline step: the pool's banks come from the ARTCC config, which can only be resolved once the load has parsed the
  scenario's ARTCC and student position. Callers must invoke it after configuring banks — `SimulationEngine.LoadScenario` does so
  immediately after `ScenarioLoader.Load` (no banks reach Yaat.Sim, so it falls back to sequential codes), and the server calls it
  right after `ConfigureBeaconCodePool` in `ScenarioLifecycleService`. An aircraft with a filed FP draws a discrete code from
  `BeaconCodePool.AssignNextCode(isVfr)` — the same allocator `AmendFlightPlan` uses, so codes come from the facility's IFR/VFR
  banks and never duplicate a live aircraft's. A cold-call aircraft gets no `AssignedCode` and squawks 1200 (VFR conspicuity).
  `InferFlightRules` (`:161`) returns the explicit `rules` if set, else `VFR` when there's no FP or cruise altitude ≤ 0, else
  `IFR`.

`DispatchPresetCommands` also backfills a missing destination for *filed* plans to the primary airport
(`SimulationEngine.cs:2105`) so arrivals show in STARS lists; cold-call aircraft are left destination-less until a controller
files via `DA`/`VP`.

## Navigation route building and the RouteExpander mismatch footgun

`ArrivalRouteResolver.PopulateNavigationRoute` (`src/Yaat.Sim/Scenarios/ArrivalRouteResolver.cs`) turns
`StartingConditions.NavigationPath` into the aircraft's `Targets.NavigationRoute`. It expands the path with
`RouteExpander.Expand(navigationPath, navDb, includeAllTransitionsOnMismatch: false)`, resolves each fix to a position (dropping
unresolvable fixes with a warning, collapsing adjacent duplicates), appends CIFP STAR runway-transition fixes
(`AppendStarRunwayTransition`), then chains the remaining filed route via `RouteChainer.AppendRouteRemainder`.

> **`ArrivalRouteResolver` is shared.** `PopulateNavigationRoute` and `ApplyAltitudeProfile` (below) were extracted from
> `ScenarioLoader` into this helper so the `ADD … {wpt}.{star}[.{rwy}]` on-STAR spawn variant (`AircraftGenerator.GenerateOnStar`)
> builds its arrival route and descend-via overlay through the exact same code as a scenario-defined `onAltitudeProfile` arrival.

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

When `ScenarioAircraft.OnAltitudeProfile` is true, `ArrivalRouteResolver.ApplyAltitudeProfile(state, navigationPath, warnings)`
acts as an auto-DVIA at spawn. It finds the STAR token in the path, resolves the CIFP STAR
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
- **weight** — `S` (small — CWT I), `S+` (smallplus — CWT H upper-small bizjets/commuters), `L` (large — mainline narrow-body + regional jets), `H` (heavy).
- **engine** — `P` (piston), `T` (turboprop), `J` (jet), `H` (helicopter). `ValidateCombo` (`:338`) rejects the four impossible
  fixed-wing combos (Heavy+Piston, Heavy+Turboprop, Small+Jet, SmallPlus+Piston); `H` is valid with any weight (the weight token
  is cosmetic for rotorcraft — see the helicopter bucket below).
- **position** — index 3 onward, one of five variants (see table below).

The trailing overrides (an explicit type like `B738`, an airline like `*UAL`) are parsed **right-to-left from the end, stopping
at index 4** (`:41`). The stop-at-4 is the slot-skip that protects the first position token (index 3): a runway like `28R` looks
exactly like an ICAO type (3–4 chars, letter+digit, per `IsLikelyAircraftType`), but at index 3 it is the position, not
a type override. (This is the same left-to-right-vs-right-to-left slot-skip discipline used elsewhere for optional trailing args.)
`IsLikelyAircraftType` accepts a letter+digit designator syntactically (works before the specs DB loads), but an **all-letter**
code (`PUMA`, `GAZL`, `LYNX`) only when `AircraftCategorization.IsKnownType` confirms it's a real ICAO type — so an all-letter
airport ICAO or fix name on a STAR arrival (`KOAK`, `TBARR`) is never misread as an aircraft type.

Position variants, dispatched from the position tokens at `SpawnParser.cs:66`:

| Leading token | Variant | `SpawnPositionType` | Required tail |
|---|---|---|---|
| `-{bearing}` | bearing | `Bearing` | `{distance} {altitude}` (inbound to primary airport) |
| `@{fix}` + numeric | at-fix | `AtFix` | `{altitude}` (fix or FRD) |
| `@{name}` (no numeric) | parking | `Parking` | parking/helipad name |
| `{runway}` alone | lined up | `Runway` | — |
| `{runway} {n}` (numeric) | on final | `OnFinal` | `{distance_nm}` |
| `{runway} {route}` (non-numeric) | departure lined up | `Runway` | dot-joined route (e.g. `NIMI6.OAK.SAU` → space-joined) |
| `{wpt}.{star}[.{rwy}]` (dotted) | arrival on STAR | `OnStar` | `[altitude] [SP{kts}] [LVL] [airport]` — IFR only; dispatched *before* the trailing scan |

The `OnStar` dispatch is the one exception to the index-4 slot-skip: because its position token (index 3) contains a `.` and is
unambiguous, `Parse` routes it to `ParseOnStarVariant` *before* the generic right-to-left type/`*airline` scan, so that variant
owns its full order-independent trailing parse (`SP###` speed, bare-number altitude, `LVL`, airport, type, `*airline`).
`GenerateOnStar` builds the route + descend-via overlay via `ArrivalRouteResolver`, and computes a default establishment altitude
from the STAR's published crossings (3:1 gradient, AIM 5-4-2.b) when none is given.

The `@`-prefix disambiguation (`:71`) switches on token **count**, not on whether the second token parses as a number: `@GATE3`
alone is a parking spawn, `@FIX 5000` is an at-fix airborne spawn, and anything longer is an error. Counting rather than sniffing
for a number is what keeps an AGL altitude (`@SUNOL KOAK+010`) on the at-fix path instead of falling through to parking.

Altitude arguments on the bearing and at-fix variants resolve through `AltitudeResolver.Resolve()`, so `035` = 3500 ft, `005` =
500 ft, `3500` = 3500 ft, and `KOAK+010` = 1000 ft AGL. Zero, negative, and unparseable values are rejected, as are extra position
tokens. The `SpawnRequest` shape and field-per-variant usage is in `src/Yaat.Sim/Scenarios/SpawnRequest.cs`.

## `AircraftGenerator`: buckets, airline-fleet coupling, fallback chain, callsigns

`AircraftGenerator.Generate(request, primaryAirportId, existingAircraft, groundLayout, rng, beaconPool)`
(`src/Yaat.Sim/Scenarios/AircraftGenerator.cs:124`) is the synthesizer, used by both the `ADD` command and the arrival generator.
`GenerateCore` (`:164`) does, in order:

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

The `TypeTable` buckets span four weight tiers that track RECAT CWT category: `Small` (CWT I — GA/light and light
bizjets), `SmallPlus` (CWT H — upper-small business jets `C560`/`C56X`/`C680`/`LJ60`/`LJ45` plus the commuter turboprops
`AT72`/`DH8C`/`SF34`/`B190`/`B350`), `Large` (mainline narrow-body `B737`/`A320` family **plus** regional jets
`CRJ7`/`E170`/`E75L`/`E145`/`E135`, CWT F/G), and `Heavy`. SmallPlus has no piston bucket (it falls back to `Small`). The
SmallPlus turboprop pool deliberately keeps the CWT G commuter turboprops — their far lower landing energy and superior
low-speed deceleration keep them short-field appropriate (e.g. OAK 28R, 5,448 ft) even where approach speed alone (SF34)
matches the regional jets — which is why no `Large+Turboprop` bucket exists. The weight↔CWT split matters for wake spacing: a SmallPlus follower still spans weightCode LARGE (those CWT G
turboprops) and SMALL (CWT H), so it maps to the coarse `Large` wake class (`SimulationEngine.WakeClassForWeight`), while each
spawned type's precise CWT still drives ATPA.

There is one **helicopter** bucket, `(Small, Helicopter)` = `R22`/`R44`/`B06` — the light-civil pool auto-selected by
`ADD {rules} {weight} H @spot` when no explicit type is given. It is keyed on `Small` only; any other weight + `H` resolves to it
via the fallback chain (engine-wins-over-size), which is why the weight token is cosmetic for helicopters. These three are the
only helicopters with a per-type `AircraftProfiles.json` profile, so they are the only heli codes that satisfy
`AssertEveryTypeResolves`; heavier heli types (`H60`, `S76`, `EC35`, …) are reachable only as explicit type overrides, where the
category resolves via `AircraftCategorization` and performance falls back to the `CategoryPerformance.Helicopter` baseline. `H`
never appears in the scenario arrival generator (`SimulationEngine.ResolveEngine` maps only Piston/Turboprop/Jet), so random
arrival streams don't spawn helicopters — they enter only through `ADD`.

The bucket↔fleet coupling is validated at startup by `AssertEveryTypeResolves` (`:42`): every `TypeTable` type must resolve
through both `AircraftProfileDatabase` and `AircraftCategorization` (with the expected category), every **jet** type must resolve
to a CWT category (so a non-resolvable designator like `E175`-instead-of-`E75L` can't silently go wake-unknown), and every
curated airline in `Airlines` must exist in `AirlineFleets`. It throws loudly rather than degrading to category-default
performance or pairing `SWA` with an `A320`. This table is tuned often — keep it in sync with the data DBs or the startup
assertion fails.

`BuildAddFlightPlan` (`:101`) encodes the cold-call vs filed split: a VFR `ADD` builds a blank `AircraftFlightPlan`
(`HasFlightPlan=false`) — the controller files later; an IFR `ADD` builds a filed plan with the generated type so STARS/strips
have a non-blank readout. IFR runway/final spawns canonicalize the departure/destination airport via
`NavigationDatabase.TryResolveAirport` so a filed SID route resolves (`:416`, `:475`).

## Runtime firing of the four queues

All four drain in `TickPrePhysics` (`SimulationEngine.cs:465`) once per sim-second:

- **`ProcessDelayedSpawns`** (`:1669`) — iterates `DelayedQueue` backward; when `ElapsedSeconds >= SpawnAtSeconds`, removes the
  entry, stamps `SpawnedAtSeconds`, adds to world, dispatches presets, emits a `[Spawn] Delayed` terminal line and any
  auto-track messages.
- **`ProcessGenerators`** — skipped entirely during replay/playback when recorded aircraft spawns exist (those are replayed
  verbatim, not regenerated). Each non-exhausted generator past its `StartTimeOffset` (and not past `MaxTime`) feeds arrivals
  onto the runway's final via `SpawnGeneratedArrival` (builds an `OnFinal` arrival, adds it to the world). The model is
  **time-first** (`TrySpawnArrival`). **Config nullability mirrors the vNAS model:** `MaxTime` is `int?` — **null means no
  time-based exhaustion** (the stream runs for the whole session), which is what most published scenarios want since they omit
  the field; only a non-null `MaxTime` exhausts the generator. `IntervalDistance` defaults to `0` when omitted (no author
  distance floor), so spacing falls back to the radar/wake minimum rather than a fabricated 5 nm. (`InitialDistance` is always
  present in the live corpus, so YAAT keeps its sane 10 nm fallback rather than vNAS's literal `0`, which would spawn on the
  threshold.) The model is:
  - **Cadence**: `IntervalTime` (pacing-scaled, optional ±25% jitter via `EffectiveSpawnIntervalSeconds`) drives *when* the next
    arrival is due — the only spawn trigger is `ElapsedSeconds >= NextSpawnSeconds`. At load the **first** generator gets
    `NextSpawnSeconds = StartTimeOffset` (fires as soon as it activates). Each **subsequent** generator with `RandomizeInterval`
    on gets `NextSpawnSeconds = StartTimeOffset + rng*IntervalTime` — a random initial phase within its first interval, so
    multiple generators that share a `StartTimeOffset` don't all spawn on the same first tick. Non-randomized generators keep the
    authored `StartTimeOffset` (deterministic). The "first" slot is the first generator successfully added, so one skipped for a
    missing runway doesn't claim it.
  - **Placement** (`#2`, back of the stream, bounded): when due, the arrival is placed at
    `D = max(InitialDistance, rearmost + gap)`, where `gap = SpacingGapNm` is the larger (binding) of the configured
    `IntervalDistance` and the 7110.65 Table 5-5-2 wake-turbulence minimum for the leader/follower pair (the two constraints
    *bind*, they do not add). `rearmost` is the rearmost aircraft inbound to the runway (`RearmostInbound`, a final-approach
    corridor query). `D` is capped at `MaxDistance`: if no room exists within the cap the spawn **waits** (defers via
    `SpawnRetryBackoffSeconds`) rather than exceeding it. An empty corridor has no `rearmost`, so the arrival spawns exactly at
    `InitialDistance` — the cold start needs no special case.
  Consequences: a long `IntervalTime` drains the corridor between spawns, so arrivals keep entering near `InitialDistance`,
  time-spaced; a short `IntervalTime` packs the stream back toward `MaxDistance` at `gap` spacing, then throttles on "no room".
  Each spawn appends a `GeneratorSpawnRecord` to `GeneratorSpawnLog` (diagnostic). The solo-training arrival-rate percent (via
  `ScenarioPacing`) widens the interval and can clamp the generator off entirely.
  - **AutoTrack threading (server-side application).** Generator spawns leave the sim in
    `TickPrePhysicsResult.GeneratorSpawns` — each a `GeneratorSpawn(State, AutoTrackConditions?)` carrying the generator's
    `AutoTrackConfiguration`. The server's `TickProcessor.ProcessPrePhysics` runs each autotrack-bearing spawn through the same
    `ApplyAutoTrackConditions` scenario aircraft use (owner + scratchpad + a delayed handoff to the student) **before**
    broadcasting it, so the owner is in the first datablock. Replay-safety: a spawn **with** autotrack is recorded
    (`RecordGeneratedSpawn`) *after* the server applies the owner, so the recorded snapshot replays with owner/scratchpad
    intact; a spawn **without** autotrack is recorded eagerly in `SpawnGeneratedArrival` (no owner to wait for). The eager-in-sim
    record would otherwise capture an untracked state and replay would lose the autotrack.
  - **AutoTrack altitudes (`interimAltitude` / `clearedAltitude`).** `AutoTrackConditions` carries both (vNAS-faithful;
    `interimAltitude` was previously dropped on deserialize). They are inherited **datablock-display** state and never touch the
    aircraft's flight target. `ApplyAutoTrackConditions` (server) resolves each STARS hundreds-of-feet string (stripping a leading
    qualifier like `"P040"`) and wires them to the **ERAM datablock only**: `interim → Eram.InterimAltitude`,
    `cleared → Eram.ControllerEnteredAltitude`. The STARS datablock is deliberately **not** populated from these fields (the old
    `cleared → Stars.TemporaryAltitude` mapping was removed) pending confirmation of whether/how non-ERAM (STARS) scenarios use
    them.
- **`ApplyArrivalSpacing`** (runs each tick immediately after `ProcessGenerators`) — in-trail speed management for the generator
  stream, the simulated approach controller (TRACON) that feeds correctly-spaced traffic to the tower (LC) student. Placement
  (above) only sets spacing at the *instant* of spawn; without this, a follower spawned farther out flies the faster
  distance-based `OnFinal` speed (`≤5 NM → Vref, ≤10 → 1.4·Vref, else 1.6·Vref`) and overruns the closer-in, decelerating leader
  (the QXE831/SWA8154 compression: 5 NM → 1.3 NM, busting the 3 NM floor). For each generator runway it sorts the same-runway
  final corridor (`CorridorAircraft`, closest-first) and, for each **generator-arrival follower in `FinalApproachPhase`**, stamps
  a `ControlTargets.SpeedCeiling`: `clamp(leaderIAS + clamp((gap − target)·25, ±20), followerVref, followerScheduledSpeed)`, where
  `target = SpacingGapNm` (the same binding `max(IntervalDistance, 3 NM, wake)` used at spawn). So the follower equalizes to the
  leader's speed at the target gap, slows (down to its own Vref) when closer, and may re-accelerate (never above its normal
  profile) when farther. The pure math lives in `ArrivalSpacingManager`; the ceiling is enforced continuously and downstream of
  the phase by `FlightPhysics.UpdateSpeed` (`goal = min(TargetSpeed, SpeedCeiling)`), so it only ever *lowers* the phase target,
  collapses to exactly Vref inside 5 NM (never blocking the landing decel), and needs no `FinalApproachPhase` change. **Override:**
  a one-way latch (`AircraftApproachState.AutoSpacingReleased`) hands speed authority back for good once a manual speed command is
  issued, speed restrictions are deleted, or the student owns the track (`ShouldReleaseAutoSpacing`). Uses no RNG, so replay/rewind
  stay deterministic; it runs during replay too — old recordings have `IsGeneratorArrival` false (the marker is set at
  `SpawnGeneratedArrival`, snapshot-serialized) and are therefore unaffected. Aviation-reviewed against 7110.65 §5-5-4 (radar
  floor), §5-7-1.c.3.1 ("reduce the trailing aircraft first"), and §5-9-5.a (approach control owns final separation until handoff).
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
route and any restriction already on another fix. If the named fix is not yet on the route, it is *appended* (not used to wipe the
route), so a chain of `CFIX` builds a crossing profile in issue order even on a routeless aircraft. So multiple `CFIX` presets are
dispatched independently and all their crossing restrictions land at spawn simultaneously. Each immediate `CFIX` leaves an
already-applied block in the `CommandQueue`; the next `CFIX` supersedes it without emitting a spurious "queue cleared … (lost:
CFIX …)" warning, because `ClearConflictingBlocks` only reports *not-yet-applied* blocks as lost.

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
- `Generators` → `GeneratorStateDto` with the config JSON-serialized plus the runtime cursor (`NextSpawnSeconds` — the time-first
  cadence cursor — and `IsExhausted`) and the runway snapshot.

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
- **The `Coordinates`/`FixOrFrd` speed default is mode-dependent, and the ground check runs first.** Both altitude and speed
  omitted → speed 0 (ground spawn). Altitude authored at/near field elevation with speed omitted → also a ground spawn (speed
  forced to 0), *not* a cruise default — the ground gate is evaluated against the `-1` sentinel before `DefaultSpeed` resolves it.
  Only a genuinely airborne spawn (above field elevation, or an explicit positive speed) resolves to a category cruise default.
  Don't reorder the default resolution ahead of the ground check, or field-elevation departures spawn airborne and fly off.
- **Generator `MaxTime` is `int?`; null means run forever, not "stop at 3600".** Most published scenarios omit `maxTime`, so the
  generator must keep feeding traffic for the whole session — only a non-null value exhausts it. Likewise an omitted
  `intervalDistance` resolves to `0` (radar/wake floor only), not a 5 nm author floor. Don't reintroduce non-null numeric
  defaults on these fields: that silently truncates real scenarios' arrival streams or widens their spacing.
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
