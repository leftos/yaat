# ATCTrainer Command Parity Plan

Commands that exist in ATCTrainer but not yet in YAAT, grouped by codebase area so multiple commands can be implemented in a single pass.

## Pass 1 — Speed & Altitude Rate (FlightPhysics + ControlTargets)

All touch `FlightPhysics.UpdateSpeed`/`UpdateAltitude` and `ControlTargets`. One exploration of the speed/altitude pipeline covers all four.

- [x] **EXP / NORM** — Expedite climb/descent + resume normal rate *(high)*
  - `EXP` multiplies climb/descent rate (~1.5x category rate)
  - `EXP {alt}` expedites then resumes normal rate at altitude
  - `NORM` restores normal climb/descent rate
  - Add `IsExpediting` flag to `AircraftState`; multiply rate in `FlightPhysics.UpdateAltitude`

- [x] **MACH** — Maintain mach number *(high)*
  - `MACH {mach}` / `M {mach}` — e.g., `MACH .82`
  - Needed for enroute high-altitude operations
  - Store mach target on `ControlTargets`; convert to IAS based on altitude in `FlightPhysics.UpdateSpeed`
  - Display mach in aircraft grid when above transition altitude

- [x] **RFAS / FAS** — Reduce to final approach speed *(high)*
  - Sets speed to the aircraft's category final approach speed
  - Simple: look up `CategoryPerformance.FinalApproachSpeed` and set as speed target

- [x] **DVIA SPD** — Via-mode with speed restriction *(medium)*
  - `DVIA SPD {speed} {fix}` — descend via with speed restriction at fix
  - ATCTrainer combines this in one command; YAAT could support it too
  - Parse as combined DVIA + CFIX-speed variant

## Pass 2 — Pattern Geometry & Phases (PatternBuilder + Pattern Phases)

All touch `PatternBuilder`, `PatternGeometry`, or pattern phase classes. One pass through the pattern system covers all five.

- [ ] **ELC / ERC** — Enter left/right crosswind *(high)*
  - Completes the set of pattern entries (ELD/ERD/ELB/ERB/EF already exist)
  - Build crosswind leg geometry in `PatternBuilder`; new `CrosswindPhase` entry point

- [ ] **PS / PATTSIZE** — Pattern size *(medium)*
  - `PS {size}` — adjust pattern dimensions (0.5–20 NM)
  - Store on `AircraftState`; pass to `PatternGeometry` calculations
  - Useful for non-standard patterns (military, heavy aircraft)

- [ ] **MNA** — Make normal approach *(medium)*
  - Restores standard pattern geometry after `MSA` (short approach)
  - Toggle flag that `MSA` sets

- [ ] **NO270** — Cancel 270 instruction *(medium)*
  - Cancels an in-progress 270° turn in the pattern
  - Check if aircraft is in a 270 phase and exit it

- [ ] **MLS / MRS** — S-turns on final *(medium)*
  - `MLS [count]` / `MRS [count]` — initial left/right S-turns
  - Adds lateral weaving on final approach for spacing
  - New pattern phase or modifier on `FinalApproachPhase`

## Pass 3 — Command Queue (CommandQueue + CommandDispatcher)

Both touch `CommandQueue` internals and command display. Tiny pass.

- [ ] **DELAT** — Remove queued commands *(medium)*
  - Clears pending command blocks from the `CommandQueue`
  - Currently no way to cancel pending blocks after `;` chains
  - Simple: `aircraft.CommandQueue.Clear()` or clear specific blocks

- [ ] **SHOWAT** — Display pending AT/LV/WAIT commands *(medium)*
  - Shows queued conditional triggers in the terminal
  - Read from `CommandQueue` and format for display
  - Could also show in the aircraft detail panel

## Pass 4 — Ground Operations (Ground Phases + GroundConflictDetector)

All touch ground phase logic, `AircraftState` ground flags, or `GroundConflictDetector`.

- [ ] **TAXIALL** — Taxi all parked aircraft *(medium)*
  - `TAXIALL {path} [hs-list]` — applies taxi to all aircraft at parking
  - Convenience for mass departure scenarios
  - Loop through all aircraft in `AtParkingPhase` and dispatch taxi command

- [ ] **BREAK** — Break ground conflict *(medium)*
  - Forces aircraft to ignore ground conflicts for 15 seconds
  - Add `ConflictIgnoreUntil` timestamp to `AircraftState`; check in `GroundConflictDetector`

- [ ] **GO** — Start takeoff roll *(medium)*
  - Begins the takeoff roll during a stop-and-go
  - Currently stop-and-go auto-continues; `GO` would add manual control
  - Useful for realistic sequencing (hold on runway after full stop, then GO)

- [ ] **CLRD** — Aircraft has clearance *(medium)*
  - Sets a "clearance delivered" state on the aircraft
  - Mostly cosmetic/status tracking for ground operations

## Pass 5 — Aircraft State / Flight Plan (AircraftState + Destination)

Touch `AircraftState` identity/destination fields and potentially approach/procedure database reloads.

- [ ] **APT / DEST** — Change primary airport / destination *(high)*
  - Changes the aircraft's destination airport
  - Useful for diversions, VFR transitions, practice approaches at non-destination airports
  - Updates `AircraftState.DestinationAirport`; may need to reload approach/procedure databases

- [ ] **FP / VP** — Create flight plan via command *(low)*
  - YAAT has the Flight Plan Editor UI instead
  - Could add as CLI shortcut: `FP I 350 SUNOL OAK` creates IFR plan
  - Low value since FPE is more capable

- [ ] **REMARKS** — Alter flight plan remarks via command *(low)*
  - YAAT has FPE for this
  - Could add as convenience: `REMARKS /V/ TCAS EQUIPPED`

## Pass 6 — Landing / Approach (LandingPhase + RunwayInfo)

Standalone — requires runway intersection geometry not shared with other groups.

- [ ] **LAHSO** — Land and hold short of operations *(high)*
  - `LAHSO {rwy}` — cleared to land, hold short of intersecting runway
  - Requires runway intersection geometry (available from ground layout data)
  - Adds a hold-short point during landing rollout

## Pass 7 — Navigation / Route (FixDatabase + ControlTargets.NavigationRoute)

Requires airway database loading — standalone exploration of NavData route structures.

- [ ] **JAWY** — Join airway *(medium)*
  - Airways are named fix sequences; `JAWY {airway}` joins aircraft to airway route
  - Requires airway database (not currently loaded — needs NavData protobuf extension or separate source)
  - More relevant for enroute; useful once YAAT supports center-level scenarios

## Won't Implement / Not Applicable

YAAT handles these differently or they aren't needed.

- **SAYF** — YAAT's `SAY` already broadcasts on the terminal; only relevant with multi-frequency support
- **OPENCHAT** — N/A; YAAT is not on the VATSIM network (has room-level chat)
- **OPS / STATS** — Better as a UI panel, not a command
- **STRIP** — CRC handles strip management
- **SHOWPATH / HIDEPATH** — Implement as a radar view feature, not a command
- **T {deg} {dir}** — Already fully covered by `LT`/`RT` and `T30L`/`T30R`
