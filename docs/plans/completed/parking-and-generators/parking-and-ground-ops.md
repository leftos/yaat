# Phase 2: Parking & Ground Ops

Makes ground-level aircraft spawn correctly and respond to taxi commands. Covers three overlapping issues: Parking position type, Coordinates/FixOrFrd ground detection, and ground layout wiring through tick + command dispatch.

## ScenarioLoader.cs — Parking Position Type

**File:** `X:\dev\yaat-server\src\Yaat.Server\Scenarios\ScenarioLoader.cs`

Replace `case "Parking":` deferral (lines 168-170) with `LoadAtParking()` method.

`LoadAtParking` follows the `LoadOnRunway` pattern (line 202):
1. Resolve airport ID (aircraft `airportId` or scenario `primaryAirportId`)
2. Get layout via `groundData.GetLayout(airportId)` — if null, warn + `BuildDeferredAircraft`
3. Find parking node via `layout.FindParkingByName(cond.Parking)` — if null, warn + defer
4. Get elevation via `fixes.GetAirportElevation(airportId)`
5. Call `AircraftInitializer.InitializeAtParking(node, elevation)` → `PhaseInitResult`
6. Populate state from result (same as other position types)

Thread `IAirportGroundData? groundData` parameter through `Load()` → `LoadAircraft()` → `LoadAtParking()`.

Remove the parking deferral warning message.

## ScenarioLoader.cs — Coordinates/FixOrFrd Ground Detection

Same file. After building state for Coordinates or FixOrFrd, detect ground-level aircraft:

```
if speed <= 0 && altitude < 200:
    state.IsOnGround = true
    create AtParkingPhase (aircraft is stationary on ground)
```

This handles scenarios where aircraft are placed at ground level via coordinates (e.g., N436MS at OAK with alt=9, speed=0).

## ScenarioLoader.cs — Heading Resolution

Same file. In `ResolveHeading` (or equivalent logic):

1. Check `cond.Heading` first — if set, use it directly
2. Fall back to existing `NavigationPath` first-fix bearing logic
3. Fall back to 0

This fixes scenarios where `heading: 133` is specified but silently ignored.

## SimulationHostedService.cs — Runtime Wiring

**File:** `X:\dev\yaat-server\src\Yaat.Server\Simulation\SimulationHostedService.cs`

### ResolveGroundLayout helper

New private method: given an `AircraftState`, returns `AirportGroundLayout?` if `IsOnGround`, using runway airport or departure field for lookup.

### SendCommandAsync (line 358)

Resolve ground layout for the target aircraft; pass to `DispatchCompound` call.

### DispatchPresetCommands (line 640)

Resolve ground layout for the aircraft; pass to `Dispatch` call.

### PreTick (line 771)

When building `PhaseContext` for each aircraft: if `IsOnGround`, look up ground layout and set it on the context. Without this, `TaxiingPhase` can't resolve node coordinates after the first segment.

### ScenarioLoader.Load call

Pass `_groundData` (the injected `IAirportGroundData`) to `ScenarioLoader.Load()`.

## CommandDispatcher.cs — Ground Command Fix

**File:** `X:\dev\yaat\src\Yaat.Sim\Commands\CommandDispatcher.cs`

### Dispatch method (line 114)

Add `AirportGroundLayout? groundLayout` parameter. If the command is a ground command (`IsGroundCommand`), route through `DispatchCompound` instead of the simple `ApplyCommand` path.

### DispatchCompound no-phase path

After the tower command rejection (line 39), add a ground command rejection with a clear error message. Defense-in-depth: all ground aircraft should have phases, but this prevents silent failure if they don't.

## Test Scenario

Scenario `01HG3N8Q5PPR7QXZK33ZPC4D5M.json`:
- N436MS should spawn at heading 133 (not 0), with phase "At Parking"
- `TAXI B 28R` → aircraft starts moving along taxiway B
- Parking-type aircraft (e.g., N172SP at GA7) should spawn correctly and accept taxi commands
