# Milestone 9: Helicopter Operations

**Goal:** Full rotary-wing tower control — hover, air taxi, CTOPP, LAND at designated spots.

**Prerequisites:** M2 (tower), M3 (ground), M5 (approach). Skipping M6-M8 for now.

---

## Aviation References

All helicopter ATC procedures are grounded in:
- **FAA 7110.65 §3-11** — Helicopter Operations (§3-11-1 through §3-11-6)
- **AIM §4-3-3** — Traffic Patterns (helicopter pattern at 500ft AGL)
- **AIM §4-3-17** — VFR Helicopter Operations at Controlled Airports
- **AIM Chapter 10** — Helicopter IFR Operations
- **7110.65 §5-7-3.e.5** — Minimum helicopter radar speed: 60 KIAS
- **7110.65 §5-9-2** — Helicopter intercept angle: 45° (vs 30° fixed-wing)

### Key Regulatory Facts

1. **Three ground movement modes** (§3-11-1):
   - **Ground taxi** — wheeled helicopters only, same as fixed-wing
   - **Hover taxi** — below 20 KIAS, in ground effect
   - **Air taxi** — preferred method; below 100ft AGL, above 20 KIAS; pilot controls speed/altitude
2. **Simultaneous ops** (§3-11-5): 200ft minimum between landing/takeoff points, non-conflicting courses
3. **Separation** (§3-11-3/4): Preceding aircraft must have departed/taxied off before next departs/lands
4. **Takeoff from non-runway** (§3-11-2): Cleared from any movement area — helipad, taxiway, parking
5. **Landing at non-runway** (§3-11-6): Land at helipad, taxiway, Maltese cross, etc.
6. **Pattern** (AIM §4-3-3): 500ft AGL, closer to runway, may be opposite side from fixed-wing
7. **Helicopters CAN use runways** — fully integrated with fixed-wing when desired
8. **CTOPP = Cleared for Takeoff Present Position** — ATCTrainer convention for helicopter vertical takeoff from non-runway positions (ramp, helipad, parking). Unlike CTO which requires a runway assignment, CTOPP clears the helicopter for vertical liftoff from wherever it is.
9. **IFR approaches** (AIM §10-1-2): Helicopters fly standard IAPs with reduced Cat A visibility minima; max 90 KIAS at MAP
10. **Speed assignments** (§5-7-3.e.5): Minimum 60 KIAS for helicopters

---

## Current Infrastructure

### Already exists
- `HoldPresentPositionPhase` with `OrbitDirection == null` → helicopter hover (HPP command)
- `HoldAtFixPhase` with `OrbitDirection == null` → helicopter hover at fix (HFIX command)
- `CanonicalCommandType.HoldPresentPositionHover` / `HoldAtFixHover`
- `ParsedCommand.HoldPresentPositionHoverCommand` / `HoldAtFixHoverCommand`
- Ground phase infrastructure (AtParking, Pushback, Taxi, HoldShort, etc.)
- Pattern phase infrastructure (Upwind, Crosswind, Downwind, Base)
- Tower phase infrastructure (Takeoff, Landing, GoAround, etc.)
- `AirportGroundLayout` with Parking nodes

### Gaps
- No `Helicopter` aircraft category — only Jet/Turboprop/Piston
- No helicopter-specific performance constants
- No `GroundNodeType.Helipad` — only Parking/Spot/TaxiwayIntersection/RunwayHoldShort
- No air taxi phase (airborne low-altitude movement)
- No LAND command (direct landing at a named spot)
- No helicopter-specific takeoff (vertical liftoff vs ground roll)
- Pattern geometry is fixed-wing only (no 500ft AGL / tighter pattern)
- No helicopter detection logic in `AircraftCategorization`

---

## Chunk Plan

### Chunk 1: Helicopter Category & Detection

Add `Helicopter` to `AircraftCategory` and populate performance constants.

**Files:** `Yaat.Sim/AircraftCategory.cs`, `yaat-server/Data/VnasDataService.cs` (categorization init)

- [x] Add `Helicopter` variant to `AircraftCategory` enum
- [x] Update `AircraftCategorization.Initialize()` — uses `AircraftDescription == "Helicopter"` from VNAS data (more reliable than EngineType); also fixed pre-existing bug where "Turboprop/Turboshaft" EngineType fell through to Jet
- [x] Add `Helicopter` branch to every `CategoryPerformance` method (see constants below)
- [x] Update `AircraftCategorization.Categorize()` fallback — unknown types still default to Jet
- [x] Update `DefaultSpeed(Helicopter, alt)` — 100 KIAS below 10k, 120 above
- [x] Verify: `dotnet build`, all existing tests pass (455 tests: 320 Sim + 71 Client + 64 Server)

**Helicopter Performance Constants** (validated by aviation-sim-expert):

| Method | Helicopter Value | Rationale |
|--------|-----------------|-----------|
| `TurnRate` | 5.0 deg/sec | Much tighter than fixed-wing; limited at cruise by bank angle |
| `ClimbRate(<10k)` | 1200 fpm | Light/medium helicopter normal climb |
| `ClimbRate(≥10k)` | 800 fpm | |
| `DescentRate` | 800 fpm | |
| `AccelRate` | 2.0 kts/sec | Moderate acceleration |
| `DecelRate` | 3.0 kts/sec | Can decelerate to hover quickly |
| `GroundAccelRate` | 2.0 kts/sec | For wheeled ground taxi |
| `RotationSpeed` | 0 (N/A) | No rotation — vertical liftoff |
| `InitialClimbSpeed` | 60 kts | Climb at ~60 KIAS |
| `InitialClimbRate` | 1200 fpm | |
| `ApproachSpeed` | 70 kts | |
| `FlareAltitude` | 50 ft | Higher flare — begins deceleration earlier for hover landing |
| `FlareDescentRate` | 150 fpm | Slow descent to hover/touchdown |
| `TouchdownSpeed` | 0 kts | Hover to touchdown |
| `RolloutDecelRate` | 0 (N/A) | No rollout — lands vertically |
| `PatternAltitudeAgl` | 500 ft | AIM §4-3-3 |
| `PatternSizeNm` | 0.5 nm | Closer to runway per AIM |
| `CrosswindExtensionNm` | 0.2 nm | Tight pattern |
| `BaseExtensionNm` | 0.3 nm | |
| `DownwindSpeed` | 70 kts | |
| `BaseSpeed` | 60 kts | |
| `PatternTurnRate` | 6.0 deg/sec | Tight turns at low speed |
| `PatternDescentRate` | 500 fpm | |
| `TouchAndGoRolloutSeconds` | 3.0 sec | Quick transition |
| `StopAndGoPauseSeconds` | 3.0 sec | Hover pause |
| `LowApproachAltitudeAgl` | 30 ft | |
| `RejectedLandingMinSpeed` | 0 kts | Can always go around (hover) |
| `MaxHoldingSpeed` | N/A (uses altitude-based) | Same as fixed-wing |
| `TaxiSpeed` | 15 kts | Wheeled ground taxi |
| `PushbackSpeed` | 5 kts | Same |
| `GroundTurnRate` | 30 deg/sec | Pedal turn capability |
| `TaxiAccelRate` | 2 kts/sec | |
| `TaxiDecelRate` | 3 kts/sec | |
| `RunwayExitSpeed` | 15 kts | |
| `RunwayCrossingSpeed` | 10 kts | |
| `DefaultSpeed` | 100/120 | |

New methods needed:
| Method | Value | Purpose |
|--------|-------|---------|
| `AirTaxiSpeed` | 40 kts | §3-11-1.c: 20-80 KIAS typical |
| `AirTaxiAltitudeAgl` | 50 ft | Below 100ft AGL per §3-11-1.c |
| `CanGroundTaxi` | false (default) | Skid-equipped can't ground taxi |

---

### Chunk 2: Helipad Ground Data

Add helipad support to the airport ground data model and GeoJSON parser.

**Files:** `Yaat.Sim/Data/Airport/AirportGroundLayout.cs`, `Yaat.Sim/Data/Airport/GeoJsonParser.cs`, `yaat-server/Dtos/TrainingDtos.cs`, `yaat-server/Data/AirportGroundDataService.cs`

- [x] Add `Helipad` to `GroundNodeType` enum
- [x] Extend `GeoJsonParser.BuildLayout()` to handle `"helipad"` feature type (Point geometry with `name`, `heading` properties — same shape as parking)
- [x] Add `FindHelipadByName(string name)` to `AirportGroundLayout` (case-insensitive, searches Helipad nodes)
- [x] Add `FindSpotByName(string name)` utility to `AirportGroundLayout` for LAND to named spots
- [x] Update `GroundNodeDto` serialization — `"Helipad"` type string (automatic via ToString())
- [x] Connect helipads to nearest taxiway node (same logic as parking, but with larger connect radius — 0.3nm — since helipads may be further from taxiways)
- [x] Create sample helipad GeoJSON entries for OAK/SFO in vzoa repo (at least 1-2 per airport for testing)
- [x] Server: AirportGroundDataService now loads from subdirectory with multiple .geojson files
- [x] Verify: build, existing ground tests pass (455 tests)

**GeoJSON helipad format** (mirrors parking):
```json
{
  "type": "Feature",
  "geometry": { "type": "Point", "coordinates": [-122.22, 37.72] },
  "properties": { "type": "helipad", "name": "H1", "heading": 280 }
}
```

---

### Chunk 3: Helicopter Takeoff & Landing Phases

Helicopter takeoff is vertical liftoff (no ground roll). Landing is a decelerating descent to hover, then touchdown. These replace the fixed-wing TakeoffPhase/LandingPhase when the aircraft is a helicopter.

**Files:** `Yaat.Sim/Phases/Tower/` (new + modified), `Yaat.Sim/Phases/PhaseRunner.cs`

- [x] Create `HelicopterTakeoffPhase` — vertical climb from ground to 400ft AGL at initial climb rate; no acceleration phase; sets InitialClimbSpeed as target once airborne
- [x] Create `HelicopterLandingPhase` — decelerate + descend to landing spot; once below 50ft AGL, 150 fpm descent; touchdown at speed=0, no rollout
- [x] All callsites updated: PhaseRunner, PatternBuilder, AircraftInitializer, CommandDispatcher (CTL, approach, ReplaceApproachEnding), ApproachCommandHandler (3 instances), DepartureClearanceHandler (3 places), TaxiingPhase
- [x] CTO/CTL/LUAW commands work with helicopter phases (same clearances per §3-11-2/6)
- [x] Touch-and-go works via Vr=0 (immediate airborne after brief touch); stop-and-go similarly
- [x] Go-around: RejectedLandingMinSpeed=0, helicopter can always go around from any speed
- [x] Aviation-sim-expert reviewed: all values accurate, caught missed HelicopterLandingPhase in ReplaceApproachEnding (fixed)
- [x] Verify: build 0W 0E, all 455 tests pass (320 Sim + 71 Client + 64 Server)

**HelicopterTakeoffPhase behavior:**
1. Start: on ground at position (helipad, parking, runway threshold)
2. Lift off vertically: climb at `InitialClimbRate`, accelerate to `InitialClimbSpeed`
3. Complete when: altitude ≥ fieldElevation + 400ft AGL
4. Transitions to: `InitialClimbPhase` (same as fixed-wing)

**HelicopterLandingPhase behavior:**
1. Start: airborne, approaching landing spot
2. Decelerate toward 0 KIAS while descending
3. Below 50ft AGL: slow descent (150 fpm) to touchdown
4. Touchdown: speed = 0, altitude = field elevation
5. Transitions to: `AtParkingPhase` (if at helipad/parking) or `HoldingAfterExitPhase` (if at runway)

---

### Chunk 4: Air Taxi Phase & Command

The primary helicopter movement mode per §3-11-1.c. Below 100ft AGL, 20-80 KIAS, point-to-point.

**Files:** `Yaat.Sim/Phases/Ground/AirTaxiPhase.cs` (new), `Yaat.Sim/Commands/CanonicalCommandType.cs`, `Yaat.Sim/Commands/ParsedCommand.cs`, `Yaat.Sim/Commands/CommandDispatcher.cs`, `Yaat.Client/Services/CommandScheme.cs`, `Yaat.Client/Services/CommandMetadata.cs`

- [x] Add `AirTaxi` to `CanonicalCommandType`
- [x] Add `AirTaxiCommand(string? Destination)` to `ParsedCommand.cs`
- [x] Add `ATXI {destination}` to `CommandScheme.Default()` — destination is a parking/helipad/spot name, or omitted for "proceed as directed"
- [x] Add to `CommandMetadata.AllCommands`
- [x] Create `AirTaxiPhase`:
  - Aircraft lifts to `AirTaxiAltitudeAgl` (50ft AGL) if on ground
  - Navigates to destination via direct bearing (no taxiway graph — airborne)
  - Maintains speed at `AirTaxiSpeed` (~40 KIAS)
  - On arrival (within 0.05nm of destination): decelerates to hover
  - Self-completes when hovering over destination
- [x] Wire into `CommandDispatcher` via `GroundCommandHandler.TryAirTaxi`
- [x] Reject `AirTaxi` for non-helicopter aircraft with appropriate message
- ~~`HoverTaxi`/`HTAXI` removed — hover taxi follows taxiways (same as TAXI), so a separate command was redundant~~
- [x] Verify: CommandSchemeCompletenessTests pass, build succeeds

**Resolution:** Destination resolves via:
1. `AirportGroundLayout.FindHelipadByName(name)` → helipad position
2. `AirportGroundLayout.FindParkingByName(name)` → parking position
3. `FixDatabase.GetFixPosition(name)` → named fix (for off-airport air taxi)

---

### Chunk 5: LAND Command

Direct landing at a named spot. Bypasses pattern/approach — helicopter descends directly to the spot.

**Files:** `Yaat.Sim/Commands/CanonicalCommandType.cs`, `Yaat.Sim/Commands/ParsedCommand.cs`, `Yaat.Sim/Commands/CommandDispatcher.cs`, `Yaat.Client/Services/CommandScheme.cs`, `Yaat.Client/Services/CommandMetadata.cs`

- [x] Add `Land` to `CanonicalCommandType`
- [x] Add `LandCommand(string SpotName, bool NoDelete = false)` to `ParsedCommand.cs`
- [x] Add `LAND {spot}` to `CommandScheme.Default()` — spot is helipad/parking name
- [x] Add to `CommandMetadata.AllCommands`
- [x] Dispatch behavior:
  - Resolve spot name → position (helipad > parking > spot node)
  - If airborne: navigate to spot, then descend via `HelicopterLandingPhase`
  - If on ground (hover/air taxi): navigate to spot airborne, then land
  - If air taxiing: redirect to new destination, then land
- [x] Support `NODEL` suffix — exempts aircraft from auto-delete on landing
- [x] Reject for non-helicopter aircraft (fixed-wing must use CTL + runway)
- [x] Verify: build, completeness tests pass

---

### Chunk 6: Helicopter Pattern Modifications

Modify pattern geometry and phases to support helicopter-specific patterns (500ft AGL, tighter, possibly opposite side).

**Files:** `Yaat.Sim/Phases/PatternGeometry.cs`, `Yaat.Sim/Phases/PatternBuilder.cs`, pattern phase files

- [x] `PatternGeometry.Compute()` already uses `CategoryPerformance` for altitude/size/extensions — adding Helicopter constants in Chunk 1 automatically produces tighter pattern geometry
- [x] Verify pattern waypoints are reasonable for helicopter: 500ft AGL, 0.5nm offset, 0.2nm crosswind extension, 0.3nm base extension
- [x] Ensure pattern phases use correct speeds from `CategoryPerformance` (DownwindSpeed/BaseSpeed already category-aware)
- [x] Support opposite-side pattern: existing `PatternDirection.Left/Right` already works; AIM §4-3-3 allows helicopter on opposite side by default
- [x] Steeper descent: helicopter pattern descent rate (500 fpm vs 700+ for fixed-wing) — already handled by `PatternDescentRate(Helicopter)`
- [x] Final approach for helicopter in pattern: steeper glideslope (6°) via category-based `GlideSlopeGeometry.AngleForCategory()` — fixed hardcoded 3° in AircraftInitializer, DownwindPhase, AircraftGenerator
- [x] Verify: existing pattern tests still pass with Jet/Turboprop/Piston; all 455 tests pass

---

### Chunk 7: Helicopter-Specific Command Variants

Additional helicopter commands from the main plan and ATCTrainer reference.

**Files:** `Yaat.Sim/Commands/CanonicalCommandType.cs`, `Yaat.Sim/Commands/ParsedCommand.cs`, `Yaat.Sim/Commands/CommandDispatcher.cs`, `Yaat.Client/Services/CommandScheme.cs`, `Yaat.Client/Services/CommandMetadata.cs`

- [x] Add `ClearedTakeoffPresent` to `CanonicalCommandType` — CTOPP = Cleared for Takeoff Present Position
- [x] CTOPP behavior: triggers `HelicopterTakeoffPhase` + `InitialClimbPhase` from current ground position (parking, helipad, ramp) — vertical liftoff without runway assignment
- [x] Ensure existing HPP/HPPL/HPPR/HFIX/HFIXL/HFIXR commands work correctly for helicopters (OrbitDirection=null → hover; speed=0 verified)
- [x] Verify hover physics: `FlightPhysics.UpdatePosition()` with speed=0 → zero displacement (no drift)
- [x] Add helicopter-aware checks to existing commands:
  - `SPD` — minimum 60 KIAS warning per §5-7-3.e.5 (warns but allows)
  - Intercept angle: 45° for helicopters vs distance-based for fixed-wing (§5-9-2)
- [x] Update `CommandSchemeCompletenessTests` — all new types registered
- [x] Verify: build 0W 0E, all 455 tests pass

---

### Chunk 8: Scenario Support & Spawn Integration

Support spawning helicopters from scenarios and ADD command.

**Files:** `Yaat.Sim/Scenarios/AircraftInitializer.cs`, `Yaat.Sim/Scenarios/SpawnRequest.cs`, `yaat-server/Scenarios/ScenarioLoader.cs`, `yaat-server/Spawn/SpawnParser.cs`

- [x] `AircraftCategorization.Categorize()` correctly identifies helicopter types from VNAS AircraftDescription
- [x] `InitializeAtParking()` — works for helicopter at parking/helipad (enters `AtParkingPhase`)
- [x] Parking name resolves helipad via `FindSpotByName` fallback in both `ScenarioLoader` and `AircraftGenerator`
- [x] ADD command: `ADD V S P %H1` — `%` prefix for parking/helipad spot spawn
- [x] `SpawnParser.ParseParkingVariant` → `SpawnPositionType.Parking` → `AircraftGenerator.GenerateAtParking`
- [x] Category-aware glideslope in `AircraftGenerator.GenerateOnFinal` (was hardcoded 300 ft/nm)
- [x] ScenarioLoader passes category to `InitializeOnRunway` for helicopter takeoff phases
- [x] Verify: build 0W 0E, all 455 tests pass

---

### Chunk 9: Client UI & Polish

Client-side display updates for helicopter operations.

**Files:** `Yaat.Client/Models/AircraftModel.cs`, `Yaat.Client/Views/DataGridView.axaml`, `Yaat.Client/ViewModels/MainViewModel.cs`, `Yaat.Client/Services/CommandInputController.cs`

- [x] AircraftModel: helicopter-specific phase names propagate via DTOs (AirTaxi, Landing-H, Takeoff-H)
- [x] DataGrid: no new columns needed — Phase column already shows phase name
- [x] Command autocomplete: ATXI, LAND, CTOPP in CommandMetadata with "Helicopter" category
- [x] Ground view: helipad nodes rendered with purple color, "H" marker, larger radius (5f vs 4f parking), name labels
- [x] Radar view: no changes needed — helicopter targets render same as fixed-wing
- [x] Terminal: CommandDescriber provides helicopter-specific messages ("Air taxi to H1", "Land at H1", "Cleared for takeoff, present position")
- [x] Verify: build 0W 0E, all 455 tests pass

---

## Definition of Done

- [x] Helicopters detected from VNAS AircraftDescription; `Helicopter` category with validated performance constants
- [x] Helipads in GeoJSON ground data with parser support
- [x] Helicopter takeoff: vertical liftoff (no ground roll) — `HelicopterTakeoffPhase`
- [x] Helicopter landing: decelerate to hover, then touchdown (no rollout) — `HelicopterLandingPhase`
- [x] Air taxi: below 100ft AGL, 20-80 KIAS, point-to-point — `AirTaxiPhase`
- [x] LAND command: direct landing at named helipad/parking
- [x] CTOPP: cleared for takeoff present position (vertical liftoff from ramp/helipad)
- [x] Helicopter traffic pattern: 500ft AGL, tighter geometry (category-aware via `CategoryPerformance`)
- [x] Existing commands (CTO, CTL, SPD, FH, etc.) work for helicopters
- [x] Scenarios can spawn helicopter aircraft (parking + helipad + runway + on-final)
- [x] CommandSchemeCompletenessTests pass
- [x] All existing tests pass (455: 320 Sim + 71 Client + 64 Server)

---

## Commands Summary

### New commands

| Command | Canonical | Description | Reference |
|---------|-----------|-------------|-----------|
| `ATXI {spot}` | `AirTaxi` | Air taxi to spot (below 100ft AGL) | §3-11-1.c |
| `LAND {spot}` | `Land` | Direct landing at named spot | §3-11-6 |
| `CTOPP` | `ClearedTakeoffPresent` | Cleared for takeoff, present position (vertical liftoff from ramp/helipad) | ATCTrainer convention |

### Existing commands with helicopter-aware behavior

| Command | Helicopter Behavior |
|---------|-------------------|
| `CTO` | Vertical liftoff (HelicopterTakeoffPhase) |
| `CTL` | Decelerate to hover + touchdown (HelicopterLandingPhase) |
| `LUAW` | Hover at threshold |
| `TG/SG` | Hover briefly, then lift off |
| `GA` | Immediate climb from any speed |
| `HPP` | Already handles helicopter hover (speed=0) |
| `HFIX` | Already handles helicopter hover at fix |
| `SPD` | Min 60 KIAS warning per §5-7-3.e.5 |
| `CAPP/JAPP` | Auto-slow to 90 KIAS at MAP |
| Pattern entry | 500ft AGL, tighter geometry |
| `TAXI` | Wheeled helicopters only; others get warning |
| `PUSH` | Works for wheeled helicopters at parking |

---

## Not In Scope (deferred)

- **Autorotation emergency** — not essential for ATC training
- **Point-in-Space approaches** (AIM §10-1-3) — complex; defer to later milestone
- **Copter-specific IAPs** — would require CIFP parsing for copter procedures
- **SVFR helicopter rules** (reduced visibility/separation) — defer to weather milestone
- **Wake turbulence modeling** (rotor outwash) — nice-to-have, not blocking
- **Simultaneous helicopter operations** (200ft rule) — would need conflict detection enhancement
- **Gulf of Mexico Grid System** — very niche
