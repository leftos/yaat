# ATCTrainer Command Parity Plan

Commands that exist in ATCTrainer but not yet in YAAT, prioritized by training value.

## High Priority

Useful for realistic ATC training scenarios. Should be implemented.

- [ ] **EXP / NORM** — Expedite climb/descent + resume normal rate
  - `EXP` multiplies climb/descent rate (~1.5x category rate)
  - `EXP {alt}` expedites then resumes normal rate at altitude
  - `NORM` restores normal climb/descent rate
  - Add `IsExpediting` flag to `AircraftState`; multiply rate in `FlightPhysics.UpdateAltitude`

- [ ] **MACH** — Maintain mach number
  - `MACH {mach}` / `M {mach}` — e.g., `MACH .82`
  - Needed for enroute high-altitude operations
  - Store mach target on `ControlTargets`; convert to IAS based on altitude in `FlightPhysics.UpdateSpeed`
  - Display mach in aircraft grid when above transition altitude

- [ ] **RFAS / FAS** — Reduce to final approach speed
  - Sets speed to the aircraft's category final approach speed
  - Simple: look up `CategoryPerformance.FinalApproachSpeed` and set as speed target

- [ ] **ELC / ERC** — Enter left/right crosswind
  - Completes the set of pattern entries (ELD/ERD/ELB/ERB/EF already exist)
  - Build crosswind leg geometry in `PatternBuilder`; new `CrosswindPhase` entry point

- [ ] **APT / DEST** — Change primary airport / destination
  - Changes the aircraft's destination airport
  - Useful for diversions, VFR transitions, practice approaches at non-destination airports
  - Updates `AircraftState.DestinationAirport`; may need to reload approach/procedure databases

- [ ] **LAHSO** — Land and hold short of operations
  - `LAHSO {rwy}` — cleared to land, hold short of intersecting runway
  - Requires runway intersection geometry (available from ground layout data)
  - Adds a hold-short point during landing rollout

## Medium Priority

Nice to have for completeness. Implement as time permits.

- [ ] **DELAT** — Remove queued commands
  - Clears pending command blocks from the `CommandQueue`
  - Currently no way to cancel pending blocks after `;` chains
  - Simple: `aircraft.CommandQueue.Clear()` or clear specific blocks

- [ ] **SHOWAT** — Display pending AT/LV/WAIT commands
  - Shows queued conditional triggers in the terminal
  - Read from `CommandQueue` and format for display
  - Could also show in the aircraft detail panel

- [ ] **TAXIALL** — Taxi all parked aircraft
  - `TAXIALL {path} [hs-list]` — applies taxi to all aircraft at parking
  - Convenience for mass departure scenarios
  - Loop through all aircraft in `AtParkingPhase` and dispatch taxi command

- [ ] **BREAK** — Break ground conflict
  - Forces aircraft to ignore ground conflicts for 15 seconds
  - Add `ConflictIgnoreUntil` timestamp to `AircraftState`; check in `GroundConflictDetector`

- [ ] **MLS / MRS** — S-turns on final
  - `MLS [count]` / `MRS [count]` — initial left/right S-turns
  - Adds lateral weaving on final approach for spacing
  - New pattern phase or modifier on `FinalApproachPhase`

- [ ] **PS / PATTSIZE** — Pattern size
  - `PS {size}` — adjust pattern dimensions (0.5–20 NM)
  - Store on `AircraftState`; pass to `PatternGeometry` calculations
  - Useful for non-standard patterns (military, heavy aircraft)

- [ ] **MNA** — Make normal approach
  - Restores standard pattern geometry after `MSA` (short approach)
  - Toggle flag that `MSA` sets

- [ ] **NO270** — Cancel 270 instruction
  - Cancels an in-progress 270° turn in the pattern
  - Check if aircraft is in a 270 phase and exit it

- [ ] **GO** — Start takeoff roll
  - Begins the takeoff roll during a stop-and-go
  - Currently stop-and-go auto-continues; `GO` would add manual control
  - Useful for realistic sequencing (hold on runway after full stop, then GO)

- [ ] **DVIA SPD** — Via-mode with speed restriction
  - `DVIA SPD {speed} {fix}` — descend via with speed restriction at fix
  - ATCTrainer combines this in one command; YAAT could support it too
  - Parse as combined DVIA + CFIX-speed variant

- [ ] **CLRD** — Aircraft has clearance
  - Sets a "clearance delivered" state on the aircraft
  - Mostly cosmetic/status tracking for ground operations

## Low Priority

YAAT handles these differently or they're not needed.

- [ ] **FP / VP** — Create flight plan via command
  - YAAT has the Flight Plan Editor UI instead
  - Could add as CLI shortcut: `FP I 350 SUNOL OAK` creates IFR plan
  - Low value since FPE is more capable

- [ ] **REMARKS** — Alter flight plan remarks via command
  - YAAT has FPE for this
  - Could add as convenience: `REMARKS /V/ TCAS EQUIPPED`

- [ ] **SAYF** — Send message over command frequency
  - YAAT's `SAY` already broadcasts on the terminal
  - Only relevant if YAAT adds multi-frequency support

- [ ] **OPENCHAT** — Open private message window
  - N/A — YAAT is not on the VATSIM network
  - YAAT has room-level chat instead (`'msg`, `/msg`, `>msg`)

- [ ] **OPS / STATS** — Operations statistics
  - Show arrival/departure counts, throughput metrics
  - Could be a useful reporting feature later
  - Not a command priority — better as a UI panel

- [ ] **STRIP** — Push flight strips to bay
  - CRC handles strip management
  - Only relevant if YAAT adds its own strip display

- [ ] **SHOWPATH / HIDEPATH** — Debug path visualization
  - ATCTrainer spawns waypoint aircraft to show the path
  - YAAT's radar view could draw the route line instead (better UX)
  - Implement as a radar view feature, not a command

- [ ] **JAWY** — Join airway
  - Airways are just named fix sequences
  - Rare in TRACON training; more relevant for enroute
  - Requires airway database (not currently loaded)

- [ ] **T {deg} {dir}** — Turn with direction argument
  - Already fully covered by `LT`/`RT` and `T30L`/`T30R`
  - No need to add a third syntax
