# Systemic review: optional parameters

## Context

Optional parameters (`= null`, `= false`, etc.) caused a silent bug in `DepartureClearanceHandler`: `InsertTowerPhasesAfterCurrent` didn't pass `IProcedureLookup` to `ResolveDepartureRoute` because the parameter had a `= null` default. The compiler was happy, CIFP SID resolution silently failed for CTO-from-hold-short, and aircraft skipped runway transition waypoints.

## Goal

Audit all optional parameters across Yaat.Sim, Yaat.Client, and yaat-server. For each, decide:
1. **Make required** — if callers should always pass the value explicitly
2. **Keep optional** — only if the default is genuinely correct in all cases (e.g., cancellation tokens)
3. **Remove** — if the parameter is unused or redundant

## Checklist

- [x] Scan `src/Yaat.Sim/` for `= null` and `= false` and `= true` and `= 0` in method signatures
- [x] Scan `src/Yaat.Client/` for the same
- [x] Scan `../yaat-server/src/Yaat.Server/` for the same
- [x] For each, check all call sites — are any relying on the default when they shouldn't be?
- [x] Make parameters required where the default hides missing wiring (Category A — 16 methods)
- [x] Fix all call sites (~26 test files, server callers, ProbeClient)
- [x] Category C (renderer options): assessed, no changes needed (display params, not wiring risk)
- [x] Category D (case-by-case): all 19+ items assessed, all genuinely optional — no changes needed
- [x] Build + test (0W 0E, 1764 tests passing)

## Changes Made (Category A)

| File | Method | Params made required |
|------|--------|---------------------|
| `CommandDispatcher.cs` | `DispatchCompound`, `Dispatch`, `ApplyCommand`, `DispatchWithPhase`, `TryApplyTowerCommand`, `BuildApplyAction` | `IApproachLookup?`, `IProcedureLookup?`, `bool validateDctFixes` |
| `DepartureClearanceHandler.cs` | `TryClearedForTakeoff` | `IProcedureLookup?`, `IRunwayLookup?` |
| `SimulationEngine.cs` | constructor | `IApproachLookup?`, `IProcedureLookup?` |
| `FlightPhysics.cs` | `UpdateCommandQueue` | `Func<string, AircraftState?>? aircraftLookup` |
| `GeoJsonParser.cs` | `Parse`, `ParseMultiple`, `BuildLayout` | `IRunwayLookup?`, `string? runwayAirportCode` |
| `AirportGroundDataService.cs` | constructor | `IRunwayLookup` (made non-nullable) |
| `DtoConverter.cs` | `ToStarsTrack`, `ToAsdexTrack` | `StarsCoastPhase`, `HashSet<uint>?`, `AsdexConfig?` |

## Bug Fixed During Audit

`CommandDispatcher.Dispatch()` was calling `DispatchCompound()` with named arg `autoCrossRunway:`, which caused `validateDctFixes` to silently use its `true` default instead of the caller's value. Fixed to pass all args positionally.

## Priority

Medium — the critical instance (IProcedureLookup) is already fixed. The rest are latent risks.
