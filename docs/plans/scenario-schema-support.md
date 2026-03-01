# ATCTrainer Scenario Schema Support

Status of YAAT's support for fields found in the ATCTrainer scenario JSON format.
Reference examples: `X:\dev\lc-trainer\docs\atctrainer-scenario-examples\`

## Legend

- [x] **Implemented** — parsed, loaded, and actively used in simulation
- [~] **Parsed only** — deserialized into model but not used by loader/simulation
- [ ] **Not supported** — silently dropped during deserialization or stubbed out

---

## Top-Level Scenario Fields

- [x] `id` — used as scenario session ID
- [x] `name` — displayed in UI and logs
- [x] `artccId` — parsed (used for VNAS data context)
- [x] `aircraft[]` — loaded into simulation (see per-aircraft fields below)
- [x] `initializationTriggers[]` — queued and fired at time offsets (SQALL supported)
- [~] `aircraftGenerators[]` — deserialized as stub (`Id` only); emits warning "deferred to M4"
- [~] `atc[]` — deserialized (artccId, facilityId, positionId, autoConnect, autoTrackAirportIds) but not used
- [x] `primaryAirportId` — stored on session, sent to client
- [~] `primaryApproach` — parsed but not used
- [~] `studentPositionId` — parsed but not used
- [~] `autoDeleteMode` — parsed but not used (values: None, Parked, OnLanding)
- [~] `minimumRating` — parsed but not used
- [~] `flightStripConfigurations[]` — deserialized as stub (`Id` only); not used

---

## Per-Aircraft Fields (`aircraft[]`)

- [x] `id` — parsed (not used as key; callsign is the key)
- [x] `aircraftId` — callsign, used as primary identifier
- [x] `aircraftType` — used for categorization and equipment suffix
- [x] `transponderMode` — set on AircraftState (C, Standby, etc.)
- [x] `startingConditions` — see breakdown below
- [~] `onAltitudeProfile` — parsed but never read (VNAV descent profiling not implemented)
- [x] `flightplan` — see breakdown below
- [x] `presetCommands[]` — dispatched at spawn (timeOffset=0 only; timeOffset>0 skipped)
- [x] `spawnDelay` — aircraft with delay>0 go into delayed spawn queue
- [x] `airportId` — used to resolve runway for OnRunway/OnFinal
- [~] `difficulty` — parsed but not used
- [~] `autoTrackConditions` — parsed (positionId, handoffDelay, scratchPad, clearedAltitude) but not used
- [~] `expectedApproach` — parsed but not used

---

## Starting Conditions (`startingConditions`)

### Position Types

- [x] `Coordinates` — lat/lon + optional altitude/speed/heading; heading resolved from navigationPath if not specified
- [x] `FixOrFrd` — fix name or FRD string resolved to lat/lon; altitude/speed optional
- [x] `OnRunway` — positioned at runway threshold via AircraftInitializer; requires runway lookup
- [x] `OnFinal` — positioned on final approach via AircraftInitializer; uses distanceFromRunway for glideslope
- [ ] `Parking` — deferred (emits warning "deferred to M2"); aircraft created with placeholder state

### Starting Condition Fields

- [x] `type` — discriminator for position type
- [x] `fix` — used by FixOrFrd type
- [x] `coordinates` (`lat`, `lon`) — used by Coordinates type
- [x] `runway` — used by OnRunway and OnFinal types
- [x] `altitude` — used by Coordinates and FixOrFrd (default 5000 if missing)
- [x] `speed` — used by Coordinates and FixOrFrd (defaults to category speed if missing)
- [ ] `heading` — present in some Coordinates examples but NOT in the model; silently dropped
- [x] `navigationPath` — used to resolve initial heading toward first waypoint; NOT set as navigation route
- [ ] `parking` — present in Parking type but NOT in the model; silently dropped (Parking is deferred anyway)
- [ ] `distanceFromRunway` — present in OnFinal examples but NOT in the model; OnFinal uses a fixed default distance

---

## Flight Plan Fields (`flightplan`)

- [x] `rules` — mapped to AircraftState.FlightRules
- [x] `departure` — mapped to AircraftState.Departure
- [x] `destination` — mapped to AircraftState.Destination
- [x] `cruiseAltitude` — mapped to AircraftState.CruiseAltitude
- [x] `cruiseSpeed` — mapped to AircraftState.CruiseSpeed
- [x] `route` — mapped to AircraftState.Route
- [~] `remarks` — parsed into model but never mapped to AircraftState (no Remarks field exists)
- [x] `aircraftType` — used as equipment type (overrides top-level aircraftType); suffix extracted

---

## Preset Commands (`presetCommands[]`)

- [x] `id` — parsed (not used functionally)
- [x] `command` — dispatched via CommandParser → CommandDispatcher at spawn
- [ ] `timeOffset` — parsed; commands with timeOffset=0 are dispatched, timeOffset>0 are **skipped** (timed preset commands not implemented)

---

## Initialization Triggers (`initializationTriggers[]`)

- [x] `id` — parsed (not used functionally)
- [x] `command` — executed at scheduled time (currently only SQALL is recognized)
- [x] `timeOffset` — triggers fire when scenario elapsed time reaches this value

---

## Aircraft Generators (`aircraftGenerators[]`)

Entirely stubbed. The model only has `Id`. All other fields from ATCTrainer are silently dropped:

- [ ] `runway` — which runway to generate arrivals for
- [ ] `engineType` — Jet, Piston, etc.
- [ ] `weightCategory` — Large, Small, etc.
- [ ] `initialDistance` — starting distance for first generated aircraft
- [ ] `maxDistance` — maximum spawn distance
- [ ] `intervalDistance` — spacing between generated aircraft
- [ ] `startTimeOffset` — when to start generating
- [ ] `maxTime` — when to stop generating
- [ ] `intervalTime` — time between spawns
- [ ] `randomizeInterval` — add randomness to timing
- [ ] `randomizeWeightCategory` — vary weight classes
- [ ] `autoTrackConfiguration` — auto-track settings for generated aircraft

---

## ATC Positions (`atc[]`)

Deserialized but not used by the simulation:

- [~] `id`
- [~] `artccId`
- [~] `facilityId`
- [~] `positionId`
- [~] `autoConnect`
- [~] `autoTrackAirportIds[]`

---

## Flight Strip Configurations (`flightStripConfigurations[]`)

Deserialized as stub (only `Id`). Full schema fields silently dropped:

- [ ] `facilityId`
- [ ] `bayId`
- [ ] `rack`
- [ ] `aircraftIds[]`

---

## Summary

| Category | Implemented | Parsed only | Not supported |
|----------|:-----------:|:-----------:|:-------------:|
| Top-level scenario | 5 | 7 | 0 |
| Per-aircraft | 7 | 4 | 0 |
| Starting conditions | 9 | 0 | 4 |
| Flight plan | 7 | 1 | 0 |
| Preset commands | 2 | 0 | 1 |
| Init triggers | 3 | 0 | 0 |
| Aircraft generators | 0 | 0 | 12 |
| ATC positions | 0 | 6 | 0 |
| Flight strip configs | 0 | 0 | 4 |
| **Total** | **33** | **18** | **21** |

### Key gaps for future milestones

1. **Parking position type** (M2) — ground operations prerequisite
2. **Navigation path as route** — aircraft should fly the path, not just point toward first fix
3. **`onAltitudeProfile`** — VNAV descent along STAR altitude constraints
4. **Timed preset commands** (`timeOffset > 0`) — schedule commands after spawn
5. **Aircraft generators** (M4) — procedural traffic generation
6. **`heading` field** — explicit heading for Coordinates type (override navigationPath heading)
7. **`distanceFromRunway`** — OnFinal spawn distance from threshold
8. **`remarks`** — flight plan remarks (CALLSIGN, equipment, etc.)
9. **ATC positions / auto-track** — simulated controller handoffs and tracking
10. **Auto-delete mode** — auto-remove aircraft on landing or parking
11. **Flight strip configurations** — pre-arranged flight strip display
