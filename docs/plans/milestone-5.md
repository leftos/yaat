# Milestone 5: Approach Control — Implementation Plan

## Context

M5 adds IFR approach control: approach clearances, STAR joining, radial interception, holding patterns, procedure navigation, and descent management. Aircraft will fly full procedure paths (transition → IAF → IF → FAF → threshold) from CIFP data, not just vector-to-final. This is the largest M5-era feature, touching CIFP parsing, phase system, command pipeline, and client display.

User requirements:
- All 11 commands from main-plan.md, plus force variants (CAPPF/JAPPF), plus APPS query
- Full CIFP approach procedure data (reference: `C:\Users\Leftos\source\repos\zoa-reference-cli\`)
- Single-verb approach clearance combos: `CAPP AT FIX ILS30`, `CAPP DCT FIX CFIX A034 ILS30`, `PTAC 070 025 ILS30`
- Shortened forms: `AT FIX ILS30`, `DCT FIX A034 ILS30`
- CFIX altitude prefixes: `A000` (at-or-above), `B000` (at-or-below), `000` (at)
- Context-based `HOLD` disambiguation (args → holding pattern; no args → ground hold)
- Hold-in-lieu of procedure turn for JAPP (skipped by CAPPSI/JAPPSI)
- Distance-based intercept angle validation per 7110.65 §5-9-2 TBL 5-9-1 (CAPPF/JAPPF override)

---

## Chunk 1: Full CIFP Procedure Data Model and Parser

**Goal:** Expand CifpParser to extract complete approach procedures from ARINC 424 data.

### New files (Yaat.Sim)
- [x] `src/Yaat.Sim/Data/Vnas/CifpModels.cs` — Data model types:
  - `CifpAltitudeRestrictionType` enum: `At`, `AtOrAbove`, `AtOrBelow`, `Between`, `GlideSlopeIntercept`
  - `CifpAltitudeRestriction` record: `Type`, `Altitude1Ft`, `Altitude2Ft?`
  - `CifpSpeedRestriction` record: `SpeedKts`, `IsMaximum`
  - `CifpFixRole` enum: `None`, `IAF`, `IF`, `FAF`, `MAHP`
  - `CifpPathTerminator` enum: `IF`, `TF`, `CF`, `DF`, `RF`, `AF`, `HA`, `HF`, `HM`, `PI`, `CA`, `FA`, `VA`, `VM`, `VI`, `CI`, `Other`
  - `CifpLeg` record: `FixIdentifier`, `PathTerminator`, `TurnDirection?`, `Altitude?`, `Speed?`, `FixRole`, `Sequence`, `OutboundCourse?`, `LegDistanceNm?`, `VerticalAngle?`
  - `CifpTransition` record: `Name`, `Legs`
  - `CifpApproachProcedure` record: `Airport`, `ApproachId`, `TypeCode` (char), `ApproachTypeName` (string), `Runway?`, `CommonLegs`, `Transitions` (dict), `MissedApproachLegs`, `HasHoldInLieu`, `HoldInLieuLeg?`

### Modified files
- [x] `src/Yaat.Sim/Data/Vnas/CifpParser.cs` — Add `ParseApproaches(cifpFilePath, airportIcao)` method:
  - Single-pass ARINC 424 parse for subsection F records matching the airport
  - Column positions from zoa-reference-cli: pos 12=subsection, 13-18=approach ID, 19=route type (A=transition), 20-24=transition name, 26-28=sequence, 29-33=fix ID, 39-42=waypoint description, 43=turn dir, 47-48=path terminator, 82=alt description, 83-87=alt1, 88-92=alt2, 99-101=speed
  - Fix role from waypoint description char at position 42: A/I=IAF, B=IF, D/F=FAF, M=MAHP
  - Detect hold-in-lieu: HA/HF/HM path terminators (can be at IAF, IF, or FAF per AIM 5-4-9.1.5)
  - Separate missed approach legs (after MAHP marker)
  - Route type 'A' at position 19 = transition leg; otherwise = common leg
  - Approach type name mapping: I=ILS, L=LOC, H=RNAV(GPS), R=RNAV, P=GPS, etc.

### Tests
- [x] `tests/Yaat.Sim.Tests/CifpParserTests.cs` (extended):
  - Parse ILS, RNAV approach records
  - Correct IAF/IF/FAF/MAHP extraction
  - Transition parsing and common route separation
  - Hold-in-lieu detection
  - Altitude/speed restriction parsing
  - Existing `Parse()` method unaffected (regression test)

### Reference
- `C:\Users\Leftos\source\repos\zoa-reference-cli\zoa_ref\cifp.py` — Python CIFP parser with field positions
- Existing parser: `src/Yaat.Sim/Data/Vnas/CifpParser.cs:25-96`

---

## Chunk 2: Approach Procedure Database (Runtime Lookup Service)

**Goal:** Create a lazy-loading service that indexes CIFP approach procedures per airport and provides lookup + route-building methods.

### New files (Yaat.Sim)
- [ ] `src/Yaat.Sim/Data/IApproachLookup.cs` — Interface:
  - `CifpApproachProcedure? GetApproach(string airportCode, string approachId)`
  - `IReadOnlyList<CifpApproachProcedure> GetApproaches(string airportCode)`
  - `string? ResolveApproachId(string airportCode, string shorthand)` — maps "ILS30"/"I28R"/"28R" → full CIFP approach ID
  - `IReadOnlyList<NavigationTarget> BuildApproachRoute(CifpApproachProcedure approach, string? transitionName, IFixLookup fixes)` — resolve CIFP legs to nav targets

- [ ] `src/Yaat.Sim/Data/ApproachDatabase.cs` — Implementation:
  - Constructor takes `CifpDataService` (for the CIFP file path) and `IFixLookup` (for waypoint resolution)
  - Lazy per-airport: first `GetApproach(airport, ...)` triggers `CifpParser.ParseApproaches()` + cache
  - `ResolveApproachId()`: parses shorthand — extract type code + runway from user input (e.g., "ILS28R" → type 'I', runway "28R"), search cached approaches
  - `BuildApproachRoute()`: iterate transition legs (if any) + common legs, resolve each fix via `IFixLookup.GetFixPosition()`, skip legs without fixes (CA/VA/VM types), return ordered `NavigationTarget` list

### Modified files
- [ ] `src/Yaat.Sim/Phases/PhaseContext.cs` — Add `IApproachLookup? ApproachLookup` property

### Server wiring (yaat-server)
- [ ] Wire `ApproachDatabase` in DI (Program.cs or RoomEngineFactory)
- [ ] Pass through `RoomEngine` to command dispatch and phase context

### Tests
- [ ] Approach ID resolution: "ILS28R" → correct, "28R" → any approach for runway, "RNAV17LZ" → specific variant
- [ ] Route building: correct fix sequence from CIFP legs
- [ ] Transition + common route concatenation
- [ ] Missing fix graceful handling (skip with warning)

---

## Chunk 3: New Command Types and Parsed Command Records

**Goal:** Add all M5 CanonicalCommandType values, ParsedCommand records, CommandScheme aliases, and CommandMetadata. No simulation logic — just vocabulary.

### Modified files (Yaat.Sim)
- [x] `src/Yaat.Sim/Commands/CanonicalCommandType.cs` — Add:
  ```
  // Approach commands
  ClearedApproach, JoinApproach,
  ClearedApproachStraightIn, JoinApproachStraightIn,
  ClearedApproachForce, JoinApproachForce,
  JoinFinalApproachCourse, JoinStar,
  JoinRadialOutbound, JoinRadialInbound,
  HoldingPattern, PositionTurnAltitudeClearance,
  DescendVia, CrossFix, DepartFix,
  ListApproaches
  ```

- [x] `src/Yaat.Sim/Commands/ParsedCommand.cs` — Add records:
  - `CrossFixAltitudeType` enum: `At`, `AtOrAbove`, `AtOrBelow`
  - `HoldingEntry` enum: `Direct`, `Teardrop`, `Parallel`
  - `ClearedApproachCommand(string ApproachId, string? AirportCode, bool Force, string? AtFix, double? AtFixLat, double? AtFixLon, string? DctFix, double? DctFixLat, double? DctFixLon, int? CrossFixAltitude, CrossFixAltitudeType? CrossFixAltType)` — rich single-verb form
  - `JoinApproachCommand(string ApproachId, string? AirportCode, bool Force)` — JAPP
  - `ClearedApproachStraightInCommand(string ApproachId, string? AirportCode)` — CAPPSI
  - `JoinApproachStraightInCommand(string ApproachId, string? AirportCode)` — JAPPSI
  - `JoinFinalApproachCourseCommand(string ApproachId)` — JFAC
  - `JoinStarCommand(string StarId, string? Transition)` — JARR
  - `JoinRadialOutboundCommand(string FixName, double FixLat, double FixLon, int Radial)` — JRADO
  - `JoinRadialInboundCommand(string FixName, double FixLat, double FixLon, int Radial)` — JRADI
  - `HoldingPatternCommand(string FixName, double FixLat, double FixLon, int InboundCourse, double LegLength, bool IsMinuteBased, TurnDirection Direction, HoldingEntry? Entry)` — HOLD
  - `PositionTurnAltitudeClearanceCommand(int Heading, int Altitude, string ApproachId)` — PTAC
  - `DescendViaCommand(int? Altitude)` — DVIA
  - `CrossFixCommand(string FixName, double FixLat, double FixLon, int Altitude, CrossFixAltitudeType AltType, int? Speed)` — CFIX
  - `DepartFixCommand(string FixName, double FixLat, double FixLon, int Heading)` — DEPART
  - `ListApproachesCommand(string? AirportCode)` — APPS

- [x] `src/Yaat.Sim/Commands/CommandDescriber.cs` — Add all new types to `ToCanonicalType()`, `DescribeCommand()`, `DescribeNatural()`, `ClassifyCommand()`

### Modified files (Yaat.Client)
- [x] `src/Yaat.Client/Services/CommandScheme.cs` — Add `Default()` entries:
  - `ClearedApproach` → `["CAPP"]`, `JoinApproach` → `["JAPP"]`
  - `ClearedApproachStraightIn` → `["CAPPSI"]`, `JoinApproachStraightIn` → `["JAPPSI"]`
  - `ClearedApproachForce` → `["CAPPF"]`, `JoinApproachForce` → `["JAPPF"]`
  - `JoinFinalApproachCourse` → `["JFAC", "JLOC", "JF"]`
  - `JoinStar` → `["JARR", "ARR", "STAR", "JSTAR"]`
  - `JoinRadialOutbound` → `["JRADO", "JRAD"]`
  - `JoinRadialInbound` → `["JRADI", "JICRS"]`
  - `HoldingPattern` → `["HOLDP"]` (distinct from ground HOLD; see Chunk 4 for disambiguation)
  - `PositionTurnAltitudeClearance` → `["PTAC"]`
  - `DescendVia` → `["DVIA"]`
  - `CrossFix` → `["CFIX"]`
  - `DepartFix` → `["DEPART", "DEP"]`
  - `ListApproaches` → `["APPS"]`

- [x] `src/Yaat.Client/Services/CommandMetadata.cs` — Add 16 entries

### Tests
- [x] `CommandSchemeCompletenessTests` must pass (all enum values in scheme + metadata)

---

## Chunk 4: Server-Side Command Parsing

**Goal:** Parse all M5 verb forms from canonical strings into ParsedCommand records. No dispatch yet.

### New files (yaat-server)
- [ ] `src/Yaat.Server/Commands/ApproachCommandParser.cs` — Static parsing methods:
  - `ParseCapp(arg, fixes, force)` — Handles rich single-verb forms:
    - `CAPP ILS30` → basic approach clearance
    - `CAPP AT FIX ILS30` or `AT FIX ILS30` → at-fix approach clearance
    - `CAPP DCT FIX ILS30` or `DCT FIX ILS30` → DCT + approach clearance
    - `CAPP DCT FIX CFIX A034 ILS30` or `DCT FIX A034 ILS30` → DCT + cross-fix + approach clearance
    - Token parsing: scan for `AT`/`DCT`/`CFIX` keywords, resolve fixes, parse altitude with A/B prefix
  - `ParseCappSi(arg, fixes)` — Same structure as CAPP, returns straight-in variant
  - `ParseJapp(arg, fixes, force)` — `JAPP ILS30 [airport]`
  - `ParseJfac(arg)` — `JFAC ILS30`
  - `ParseJarr(arg)` — `JARR SUNOL1 [KENNO]`
  - `ParseJrado(arg, fixes)` — `JRADO OAK090` (fix name + 3-digit radial)
  - `ParseJradi(arg, fixes)` — `JRADI OAK090`
  - `ParseHold(arg, fixes)` — `HOLD SUNOL 090 3 R [D|T|P]` or `HOLD SUNOL 090 1M L`
  - `ParsePtac(arg, fixes)` — `PTAC 280 025 ILS30` (heading, altitude, approach)
  - `ParseDvia(arg, fixes)` — `DVIA [alt]`
  - `ParseCfix(arg, fixes)` — `CFIX SUNOL A040 [250]` — altitude prefix: strip A/B, resolve via `AltitudeResolver.Resolve()`, map prefix to `CrossFixAltitudeType`
  - `ParseDepart(arg, fixes)` — `DEPART SUNOL 270`
  - `ParseApps(arg)` — `APPS [airport]` (airport code optional; if omitted, resolved from aircraft destination)

### Modified files (yaat-server)
- [ ] `src/Yaat.Server/Commands/CommandParser.cs` — Add verb entries in `Parse()`:
  - `"CAPP"` / `"CAPPF"` / `"CAPPSI"` / `"JAPP"` / `"JAPPF"` / `"JAPPSI"` → ApproachCommandParser
  - `"JFAC"` / `"JLOC"` / `"JF"` → ApproachCommandParser.ParseJfac
  - `"JARR"` / `"ARR"` / `"STAR"` / `"JSTAR"` → ApproachCommandParser.ParseJarr
  - `"JRADO"` / `"JRAD"` → ApproachCommandParser.ParseJrado
  - `"JRADI"` / `"JICRS"` → ApproachCommandParser.ParseJradi
  - `"HOLD"` with args → ApproachCommandParser.ParseHold (no args → existing HoldPositionCommand)
  - `"PTAC"` → ApproachCommandParser.ParsePtac
  - `"DVIA"` → ApproachCommandParser.ParseDvia
  - `"CFIX"` → ApproachCommandParser.ParseCfix
  - `"DEPART"` / `"DEP"` → ApproachCommandParser.ParseDepart
  - `"APPS"` → ApproachCommandParser.ParseApps
  - Shortened forms without verb: `"AT"` and `"DCT"` when followed by fix + approach ID → route to CAPP parser

### Tests
- [ ] `tests/Yaat.Server.Tests/ApproachCommandParserTests.cs`:
  - Each parser with valid/invalid inputs
  - CFIX altitude prefix: A040, B060, 040, 3400
  - HOLD nm-based and minute-based legs, explicit/omitted entry
  - JRADO/JRADI fix+radial parsing
  - PTAC heading+altitude+approach
  - Rich CAPP forms: `CAPP AT OAK ILS28R`, `CAPP DCT SUNOL CFIX A034 ILS28R`
  - Shortened forms: `AT SUNOL ILS28R`, `DCT SUNOL A034 ILS28R`

---

## Chunk 5: Simple Navigation Commands (JRADO, JRADI, DEPART, CFIX, DVIA, APPS)

**Goal:** Dispatch and physics for five navigation commands that work through `CommandQueue` without the phase system, plus one query command (APPS).

### Modified files (Yaat.Sim)
- [ ] `src/Yaat.Sim/Commands/CommandDispatcher.cs` — Add cases in `ApplyCommand()`:
  - **JRADO**: Fly present heading. Build 2-block queue: block 1 = fly present heading (immediate), block 2 = fly radial heading (trigger: `InterceptRadial` on fix+radial). On intercept, heading = radial (outbound from fix).
  - **JRADI**: Same intercept trigger. On intercept, heading = reciprocal of radial + add fix as nav target (inbound to fix).
  - **DEPART**: Build 2-block queue: block 1 = DCT fix (immediate), block 2 = FH heading (trigger: `ReachFix`).
  - **CFIX**: Add fix to nav route. Set altitude target based on `CrossFixAltitudeType` (At → DM/CM toward alt; AtOrAbove → CM if below; AtOrBelow → DM if above). Optional speed. **Crossing restriction expires after fix passage** — use 2-block queue: block 1 = set alt/speed target + nav to fix (immediate), block 2 = revert altitude target to previously assigned altitude (trigger: `ReachFix`).
  - **DVIA**: If altitude provided, set as `TargetAltitude` (basic DM). If no altitude, per AIM 5-4-1.a.2.a, authorize descent per published STAR restrictions — set target to lowest mandatory crossing altitude on active STAR/route (if known from nav route). If no STAR context available, warn and no-op.
  - **APPS**: Query command — no aircraft state change. Resolve airport: explicit airport arg always wins (even if an aircraft is selected), else fall back to selected aircraft's destination airport from flight plan. If neither, error. Query `ApproachDatabase.GetApproaches(airport)` and format as terminal broadcast. Each approach listed as its user-typable ID (e.g., `ILS28R`, `RNAV17L`, `LOC30`). Group by runway. Example output: `OAK approaches: RWY 28R: ILS28R, RNAV28R | RWY 30: ILS30, LOC30, RNAV30`.

### Existing infrastructure reused
- `BlockTrigger.InterceptRadial` (already exists in `CommandQueue.cs`)
- `BlockTrigger.ReachFix` (already exists)
- `AltitudeResolver.Resolve()` for altitude parsing

### Tests
- [ ] JRADO: aircraft intercepts radial, flies outbound heading
- [ ] JRADI: aircraft intercepts radial, flies inbound toward fix
- [ ] DEPART: navigates to fix, then flies heading
- [ ] CFIX: navigates to fix with correct altitude target; A/B prefix variants; altitude reverts after fix passage
- [ ] DVIA without altitude: sets target from STAR restrictions (or warns if no context)
- [ ] Compound usage: `DCT SUNOL; CFIX A040; DM 020`
- [ ] APPS with explicit airport: lists all approaches grouped by runway
- [ ] APPS without airport: resolves from aircraft destination
- [ ] APPS with unknown airport or no destination: error message

---

## Chunk 6: JARR (Join STAR)

**Goal:** STAR joining with transition resolution and navigation route building.

### Modified files (Yaat.Sim)
- [ ] `src/Yaat.Sim/Data/FixDatabase.cs` — Expose STAR data:
  - `GetStarBody(starId)` → `IReadOnlyList<string>?` (from existing `_starBodies`)
  - `GetStarTransitionFixes(starId)` → `IReadOnlyList<(string TransitionName, IReadOnlyList<string> Fixes)>?` (need to store transition data during `BuildProcedureIndex`)
  - Store STAR transitions during index build (currently only bodies are stored)

- [ ] `src/Yaat.Sim/Commands/CommandDispatcher.cs` — Add JARR dispatch:
  - Resolve STAR from FixDatabase
  - If transition specified: build route from transition fixes + body fixes
  - If no transition: find nearest STAR body fix **ahead of aircraft** (within ±90° of heading), navigate from there. Ignore fixes behind the aircraft to prevent U-turns.
  - Resolve all fix names to lat/lon via FixDatabase, populate `NavigationRoute`
  - Single-block immediate command (no phase system)

### Proto changes
- [ ] Extend `BuildProcedureIndex()` in `FixDatabase.cs` to store STAR transitions (not just bodies)

### Tests
- [ ] JARR with transition: correct route from transition entry through body
- [ ] JARR without transition: nearest fix selection (only ahead of aircraft)
- [ ] Invalid STAR name: error message
- [ ] Navigation: aircraft follows STAR fix sequence

---

## Chunk 7: Holding Pattern Phase

**Goal:** Real holding pattern with inbound course, turn direction, leg length, and entry determination.

### New files (Yaat.Sim)
- [ ] `src/Yaat.Sim/Phases/HoldingEntryCalculator.cs` — Static method:
  - `HoldingEntry ComputeEntry(double aircraftHeading, double inboundCourse, TurnDirection holdDirection)` — 70° sector boundaries per AIM 5-3-8
  - Compute theta = normalize(aircraftHeading - inboundCourse) to [0, 360)
  - **Right turns**: Direct = [0, 110), Teardrop = [110, 250), Parallel = [250, 360)
  - **Left turns**: Parallel = [0, 110), Teardrop = [110, 250), Direct = [250, 360)

- [ ] `src/Yaat.Sim/Phases/Approach/HoldingPatternPhase.cs`:
  - Props: `FixName`, `FixLat`, `FixLon`, `InboundCourse`, `LegLengthNm`/`LegLengthMinutes`, `Direction`, `Entry` (auto-computed if null)
  - State machine: `Navigating` → `(Entry maneuver)` → `Inbound` → `TurnToOutbound` → `Outbound` → `TurnToInbound` → `Inbound` → repeat
  - Entry maneuvers: Direct = overfly fix, turn outbound. Teardrop = overfly fix, 30° offset outbound, turn inbound. Parallel = overfly fix, fly reciprocal of inbound, turn back.
  - Decelerate to max holding speed on arrival (reuse `CategoryPerformance.MaxHoldingSpeed()`)
  - Never self-completes — any new command exits via `ClearsPhase`
  - Name: "HoldingPattern" (distinct from existing "HoldingAtFix")

### Modified files
- [ ] `src/Yaat.Sim/Commands/CommandDispatcher.cs` — Add `HoldingPatternCommand` dispatch:
  - Create `PhaseList` with single `HoldingPatternPhase`
  - Set on aircraft, start phase system

### Aviation review
- [ ] **aviation-sim-expert must review** entry determination logic and holding pattern geometry

### Tests
- [ ] Entry computation: all 3 sectors for both left/right turns (6 test cases minimum)
- [ ] Holding geometry: fix overfly, outbound leg timing, inbound intercept
- [ ] Time-based vs distance-based legs
- [ ] Phase never self-completes
- [ ] Any command exits hold

---

## Chunk 8: Approach Clearance Infrastructure + JFAC

**Goal:** Add `ApproachClearance` as a PhaseList-level property. Implement `InterceptCoursePhase` and JFAC as the simplest approach-joining command.

### New files (Yaat.Sim)
- [ ] `src/Yaat.Sim/Phases/ApproachClearance.cs` — Record:
  - `string ApproachId`, `string AirportCode`, `string RunwayId`
  - `double FinalApproachCourse` (heading)
  - `bool StraightIn`, `bool Force`
  - `CifpApproachProcedure? Procedure` (resolved CIFP data, if available)

- [ ] `src/Yaat.Sim/Phases/Approach/InterceptCoursePhase.cs`:
  - Flies aircraft on current heading until intercepting final approach course
  - Cross-track distance check + heading alignment (similar to `FinalApproachPhase.CheckInterceptDistance` pattern)
  - On intercept: turns onto course heading, completes phase
  - Successor: `FinalApproachPhase`
  - `CanAcceptCommand()`: approach commands = Allowed, others = ClearsPhase

### Modified files
- [ ] `src/Yaat.Sim/Phases/PhaseList.cs` — Add:
  - `ApproachClearance? ActiveApproach { get; set; }`

- [ ] `src/Yaat.Sim/Commands/CommandDispatcher.cs` — Add JFAC dispatch:
  - Resolve approach from `ApproachDatabase`
  - Get final approach course from runway heading
  - Set `PhaseList.ActiveApproach`
  - Assign runway to PhaseList
  - Build phase sequence: `InterceptCoursePhase` → `FinalApproachPhase` → `LandingPhase`

### Tests
- [ ] JFAC: aircraft vectored, intercepts FAC, transitions to final approach
- [ ] InterceptCoursePhase: correct intercept detection + course alignment
- [ ] ApproachClearance stored on PhaseList and accessible by FinalApproachPhase

---

## Chunk 9: CAPP/JAPP/PTAC — Full Approach Clearance Commands

**Goal:** Full procedure navigation, approach clearance combos, intercept validation.

### New files (Yaat.Sim)
- [ ] `src/Yaat.Sim/Commands/ApproachCommandHandler.cs` — Static methods:
  - `TryClearedApproach(aircraft, command, approachDb, fixes, runways, logger)`:
    - Resolve approach from ApproachDatabase
    - Handle rich CAPP forms: AT fix → queue DCT to fix first; DCT fix → queue DCT; CFIX → queue altitude constraint
    - Validate intercept angle per §5-9-2 TBL 5-9-1 (unless Force) — distance-based: 20° at <2nm, 30° at ≥2nm from approach gate
    - **Cancel existing speed restrictions** per 7110.65 §5-7-4 (approach clearance cancels prior speed assignments)
    - Build phase sequence: optional DCT phase → `ApproachNavigationPhase` → `FinalApproachPhase` → `LandingPhase`
    - Set `PhaseList.ActiveApproach`
  - `TryJoinApproach(aircraft, command, ...)`:
    - Like CAPP but routes through nearest IAF/IF on the procedure
    - If procedure has hold-in-lieu (at CIFP-identified fix — can be IAF, IF, or FAF per AIM 5-4-9.1.5) and NOT straight-in: insert hold-in-lieu
  - `TryStraightIn(aircraft, command, ...)`:
    - Like JAPP but skips hold-in-lieu
  - `TryPtac(aircraft, command, ...)`:
    - Set heading immediately (TargetHeading)
    - Set altitude (maintained until established on approach)
    - Set approach clearance
    - Build phase sequence: `InterceptCoursePhase` → `FinalApproachPhase` → `LandingPhase`

- [ ] `src/Yaat.Sim/Phases/Approach/ApproachNavigationPhase.cs`:
  - Navigates aircraft through approach fix sequence (from BuildApproachRoute)
  - Respects altitude/speed restrictions from CIFP legs (at-or-above → ensure aircraft climbs/descends to constraint before fix)
  - Hold-in-lieu: if `HasHoldInLieu` and NOT `StraightIn`, insert one circuit of holding at the CIFP-identified hold fix before continuing
  - Completes when reaching the FAF or last fix before final approach
  - Name: "Approach" (or "ApproachNav")

### Modified files
- [ ] `src/Yaat.Sim/Commands/CommandDispatcher.cs` — Route CAPP/JAPP/CAPPSI/JAPPSI/CAPPF/JAPPF/PTAC to `ApproachCommandHandler` via `TryApplyTowerCommand()`
- [ ] `src/Yaat.Sim/Phases/Tower/FinalApproachPhase.cs` — When `PhaseList.ActiveApproach` is set, use its runway info to initialize glideslope (currently gets runway from PhaseList.AssignedRunway, which we set in the approach handler)

### Intercept Angle Validation (7110.65 §5-9-2 TBL 5-9-1)
- Compute intercept angle: `abs(NormalizeAngle(aircraftHeading - finalApproachCourse))`
- Compute distance to approach gate using `ApproachGateDatabase` (existing code)
- **Distance-based limits**: if < 2nm from approach gate → max 20°; if ≥ 2nm → max 30°
- If angle exceeds limit and not Force: reject with warning message, return error
- CAPPF/JAPPF: skip check entirely

### Aviation review
- [ ] **aviation-sim-expert must review** approach navigation, hold-in-lieu execution, intercept validation

### Tests
- [ ] CAPP basic: approach navigation → final → landing
- [ ] CAPP AT FIX: navigates to fix first, then approach
- [ ] CAPP DCT FIX CFIX A034: DCT + altitude constraint + approach
- [ ] JAPP: joins at nearest IAF
- [ ] JAPP with hold-in-lieu: executes one hold circuit
- [ ] CAPPSI/JAPPSI: skips hold-in-lieu
- [ ] PTAC: heading → intercept → final approach → landing
- [ ] Intercept rejection: 30° at ≥2nm, 20° at <2nm from gate + force override
- [ ] CAPPF: bypasses intercept check
- [ ] Speed restriction canceled on approach clearance

---

## Chunk 10: Client UI, DTOs, Polish, Documentation

**Goal:** Client display of approach state, DTO updates, USER_GUIDE, and end-to-end testing.

### Modified files (Yaat.Sim)
- [ ] `src/Yaat.Sim/AircraftState.cs` — Add: `string? ActiveApproachId`, `string? ApproachStatus` (for DTO display)

### Modified files (yaat-server)
- [ ] `src/Yaat.Server/Dtos/TrainingDtos.cs` — Add approach fields to `AircraftDto`
- [ ] `src/Yaat.Server/Simulation/DtoConverter.cs` — Populate approach fields from AircraftState

### Modified files (Yaat.Client)
- [ ] `src/Yaat.Client/Models/AircraftModel.cs` — Add `ApproachId`, `ApproachStatus` properties + `UpdateFromDto`
- [ ] DataGrid or existing columns — Show approach state (can use existing Phase column or add dedicated column)

### Documentation
- [ ] `USER_GUIDE.md` — Document all M5 commands with examples:
  - Basic forms: `CAPP ILS28R`, `JAPP ILS28R`, `PTAC 280 025 ILS30`
  - Rich forms: `CAPP AT SUNOL ILS28R`, `CAPP DCT SUNOL CFIX A034 ILS28R`
  - Shortened forms: `AT SUNOL ILS28R`, `DCT SUNOL A034 ILS28R`
  - JARR, JFAC, JRADO, JRADI, HOLD, CFIX, DEPART, DVIA
  - Query: `APPS OAK` or `N456MS APPS` (list available approaches)
- [ ] `docs/command-aliases/reference.md` — Update approach section with implemented commands
- [ ] `docs/plans/main-plan.md` — Update M5 status

### Tests
- [ ] End-to-end: CAPP ILS28R → full navigation → landing
- [ ] Hold-in-lieu flow: JAPP through IAF with hold → one circuit → approach → landing
- [ ] PTAC flow: heading → intercept → glideslope → landing

---

## Dependency Graph

```
Chunk 1 (CIFP Parser)
  → Chunk 2 (ApproachDatabase)
     → Chunk 8 (ApproachClearance + JFAC)
        → Chunk 9 (CAPP/JAPP/PTAC)
           → Chunk 10 (Client UI + Polish)

Chunk 3 (Command Types)  ← independent of Chunks 1-2
  → Chunk 4 (Server Parsing)
     → Chunk 5 (JRADO/JRADI/DEPART/CFIX/DVIA)
     → Chunk 6 (JARR)
     → Chunk 7 (Holding Pattern)
     → Chunk 8 (merges both tracks)
```

Chunks 1-2 and 3-4 can proceed in parallel.
Chunks 5, 6, 7 can proceed in parallel after Chunk 4.
Chunks 8-10 are sequential, requiring both tracks complete.

---

## Key Existing Code to Reuse

| What | File | Why |
|------|------|-----|
| `AltitudeResolver.Resolve()` | `Yaat.Sim/Commands/AltitudeResolver.cs` | CFIX/PTAC altitude parsing |
| `FrdResolver.Resolve()` | `Yaat.Sim/Data/FrdResolver.cs` | JRADO/JRADI radial geometry |
| `RouteChainer.AppendRouteRemainder()` | `Yaat.Sim/Commands/RouteChainer.cs` | JARR route continuation |
| `FlightPhysics.SignedCrossTrackDistanceNm()` | `Yaat.Sim/FlightPhysics.cs` | Course intercept detection |
| `ApproachGateDatabase` | `Yaat.Sim/Data/ApproachGateDatabase.cs` | §5-9-1 intercept distance checks |
| `CategoryPerformance.MaxHoldingSpeed()` | `Yaat.Sim/AircraftCategory.cs` | Holding deceleration |
| `BlockTrigger.InterceptRadial` | `Yaat.Sim/CommandQueue.cs` | JRADO/JRADI intercept |
| `GlideSlopeGeometry` | `Yaat.Sim/Phases/GlideSlopeGeometry.cs` | Approach descent |
| Python CIFP parser | `C:\Users\Leftos\source\repos\zoa-reference-cli\zoa_ref\cifp.py` | ARINC 424 field positions reference |

---

## Verification

### Per-chunk
- Both repos build with 0 warnings after each chunk
- All existing tests pass (136 Sim + 24 Client + 49 Server)
- New tests pass for each chunk
- `CommandSchemeCompletenessTests` pass after Chunk 3

### End-to-end (after Chunk 10)
1. Start yaat-server, connect YAAT client
2. Load a scenario with IFR arrivals
3. Issue `CAPP ILS28R` — verify aircraft navigates full approach procedure and lands
4. Issue `PTAC 280 025 ILS30` — verify heading/altitude/intercept/glideslope/landing
5. Issue `JARR SUNOL1 KENNO` — verify STAR navigation
6. Issue `HOLD SUNOL 090 3 R` — verify holding pattern with correct entry
7. Issue `JRADO OAK090` — verify radial intercept and outbound tracking
8. Verify CRC displays aircraft correctly throughout approach phases
9. Verify terminal broadcasts for approach clearances, intercept warnings, and go-arounds
