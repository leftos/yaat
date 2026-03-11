# Issue #48: CTO Command Fails with "Cannot resolve runway"

## Root Cause

`CommandDispatcher.ResolveRunway` resolves the airport as:
```csharp
var airportId = aircraft.Departure ?? aircraft.Destination;
if (airportId is null) return null;
```

`AircraftState.Departure` and `Destination` are initialised to `""` (empty string), not `null`.
VFR local traffic aircraft spawned from parking with no flight plan have `Departure = ""` and
`Destination = ""`. The null guard passes, then `runways.GetRunway("", "28L")` is called.
No airport is indexed under the empty key → returns `null` → error.

Runway 15/33 is present in vNAS navdata (FixDatabase has no closed-runway filter); it fails for
the same empty airport ID reason, not because of a navdata gap.

## Fix

**File:** `src/Yaat.Sim/Commands/CommandDispatcher.cs`
**Method:** `ResolveRunway`

Treat empty strings same as null; fall back to `GroundLayout.AirportId`:

```csharp
var airportId = !string.IsNullOrEmpty(aircraft.Departure) ? aircraft.Departure
    : !string.IsNullOrEmpty(aircraft.Destination) ? aircraft.Destination
    : aircraft.GroundLayout?.AirportId;
```

## Tests

Add to `tests/Yaat.Sim.Tests/DepartureClearanceHandlerTests.cs`:

- **`ResolveRunway_EmptyDeparture_UsesGroundLayoutAirportId`** — aircraft with `Departure=""`,
  `Destination=""`, `GroundLayout.AirportId="OAK"`; lookup has 28L for OAK → returns non-null
- **`CTO_VfrLocalTrafficAtHoldShort_Succeeds`** — aircraft at hold short "28L" with empty
  Departure/Destination → `TryDepartureClearance` returns Success

## Files to Edit

| File | Change |
|------|--------|
| `src/Yaat.Sim/Commands/CommandDispatcher.cs` | Fix `ResolveRunway` empty-string handling |
| `tests/Yaat.Sim.Tests/DepartureClearanceHandlerTests.cs` | 2 regression tests |

No changes to `HoldShortAnnotator`, `TaxiPathfinder`, `RunwayIdentifier`, or `DepartureClearanceHandler`.
