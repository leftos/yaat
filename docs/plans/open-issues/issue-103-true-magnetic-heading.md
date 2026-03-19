# Issue #103: True vs Magnetic Heading — Type-Safe Wrapper Types

## Status: Implementation Complete — Ready for Commit

## Summary

Introduced `TrueHeading` and `MagneticHeading` readonly record structs to prevent cross-frame mixing. User commands H150 (magnetic) must be converted to true before physics; CRC receives true; display shows magnetic.

## Type Design

- `TrueHeading` / `MagneticHeading` — `readonly record struct`, auto-normalize [0,360)
- Helpers: `.ToReciprocal()`, `.ToRadians()`, `.ToDisplayInt()`, `.SignedAngleTo()`, `.AbsAngleTo()`, `.IsCloseTo()`, `.ToMagnetic(decl)` / `.ToTrue(decl)`
- Operators: `± double`, `TrueHeading - TrueHeading → double`
- `GeoMath` public overloads: `ProjectPoint(TrueHeading)`, `SignedCrossTrackDistanceNm(TrueHeading)`, `AlongTrackDistanceNm(TrueHeading)`, `TurnHeadingToward(TrueHeading)`
- Raw-double GeoMath variants renamed: `*Raw` suffix, `internal` access

## Completed

- [x] Create `TrueHeading.cs` and `MagneticHeading.cs` with all helpers
- [x] Add `MagneticToTrue` overload to `MagneticDeclination`
- [x] Update `AircraftState`: `TrueHeading`, `TrueTrack`, `PushbackTrueHeading`, `Declination`, `MagneticHeading`, `MagneticTrack`
- [x] Update `ControlTargets`: `TargetTrueHeading` (TrueHeading?), `AssignedMagneticHeading` (MagneticHeading?)
- [x] Update all `ParsedCommand` records (FlyHeadingCommand, TurnLeftCommand, etc. → MagneticHeading)
- [x] Update `RunwayInfo`: `TrueHeading1/2` (TrueHeading), derived `TrueHeading` property
- [x] Update `GroundNode.TrueHeading` (TrueHeading?), `RunwayRectangle.TrueHeading`, `PhaseInitResult.TrueHeading`
- [x] Update `RecordedWarp.TrueHeading`, `ApproachClearance.FinalApproachCourse` (TrueHeading), `InterceptCoursePhase.FinalApproachCourse` (TrueHeading)
- [x] Make `FlightPhysics.NormalizeAngle` private — all external callers use `.SignedAngleTo()` / `.AbsAngleTo()` / `GeoMath.SignedBearingDifference()`
- [x] Make `FlightPhysics.NormalizeHeading` private (renamed `NormalizeBearing`) — callers construct `TrueHeading` directly
- [x] Remove `FlightPhysics.NormalizeHeadingInt` — callers use `.ToDisplayInt()` or `FlightPhysics.BearingToDisplayInt()`
- [x] Delete `PatternGeometry.NormalizeHeading` and `AirportGroundLayout.NormalizeAngle` private copies
- [x] Fix `FlightPhysics.Update()` — cache `aircraft.Declination` each tick
- [x] Fix `FlightCommandHandler` — magnetic→true conversion at command input boundary
- [x] Fix `NavigationCommandHandler` — JRADO/JRADI/JAWY use typed present heading, radial conversion
- [x] Fix `CommandParser`, `CommandDescriber`, `GroundCommandParser`, `DepartureCommandParser`, `ApproachCommandParser`
- [x] Fix all phase files — private `_runwayHeading` fields → `TrueHeading`, all GeoMath calls pass TrueHeading directly
- [x] Fix `PatternGeometry` heading properties → `TrueHeading`, `PatternCommandHandler` uses typed values
- [x] Fix `ApproachCommandHandler` — `finalCourse` → `TrueHeading`, `reciprocal` → `.ToReciprocal()`
- [x] Fix `ScenarioLoader` — magnetic→true conversion at spawn
- [x] Fix `AircraftGenerator`, `SimulationEngine`, `RunwayCrossingDetector`, `NavigationDatabase`, `GeoJsonParser`
- [x] Fix `VisualDetection` — params → `TrueHeading`, typed angle methods
- [x] Fix `ConflictAlertDetector`, `GroundConflictDetector`, `AtpaVolumeGeometry`
- [x] Fix `AirportGroundLayout` — method params `double runwayHeading` → `TrueHeading runwayHeading`
- [x] Fix `FlightCommandHandler.PickBestEdgeHeading` → returns `TrueHeading`
- [x] Fix `PushbackPhase.ComputeAlignmentHeading` → returns `TrueHeading?`
- [x] Fix `AircraftInitializer` — `reciprocalHeading` → `.ToReciprocal()`
- [x] Yaat.Sim builds 0W 0E

## Remaining — Yaat.Sim cleanup

- [x] `MakeTurnPhase` — `ComputeExitHeading()` return type and `targetHdg` local → `TrueHeading`
- [x] `VfrHoldPhase` — `targetHdg` local → `TrueHeading`
- [x] `AirportGroundLayout.GetEdgeHeadingForTaxiway` → renamed to `GetEdgeBearingForTaxiway`
- [x] `GroundCommandHandler` — `edgeHeading` → `edgeBearing`
- [x] `RunwayExitPhase` — `taxiwayHdg` → `taxiwayBearing`
- [x] `STurnPhase` — removed dead `targetHdg` variable
- [x] `FlightPhysics` — renamed `moveHeading` → `moveDir`
- [x] Final audit: only JSON DTOs and private internal bearing math remain as `double` — all acceptable

## Completed — Tests

- [x] Fix all Yaat.Sim.Tests (1663 pass)
- [x] Fix Yaat.Client.Tests (270 pass)
- [x] Fix Yaat.Server.Tests (243 pass)
- [x] Fixed `SignedAngleTo` argument-order bug in `LineUpPhase.NavigateToTarget`
- [x] Fixed `SignedAngleTo` argument-order bug in `ApproachCommandHandler.ValidateInterceptAngle`
- [x] Fixed `:D3` format on `double` (was `int`) in `NavigationCommandHandler.DispatchDepartFix`
- [x] Fixed 360/0 normalization assertions (use `.IsCloseTo()` for heading=360° tests)

## Completed — yaat-server

- [x] `AircraftChangeTracker.cs`, `DtoConverter.cs`, `CrcClientState.Stars.cs`, `RoomEngine.cs`
- [x] Server tests fixed (harness, CtoParser, rewind, E2E)
- [x] Server builds 0W 0E, 243 tests pass

## Completed — Yaat.Client

- [x] `TargetRenderer.cs` — ProjectPoint call wraps Heading in TrueHeading
- [x] `GroundViewModel.cs` — GroundNode.TrueHeading from DTO
- [x] Client builds 0W 0E, 270 tests pass

## Completed — Final

- [x] `pwsh tools/test-all.ps1` — all tests pass both repos (2176 total)
- [x] `dotnet build -p:TreatWarningsAsErrors=true` — both repos 0W 0E
- [x] Audited all `double.*[Hh]eading` — only JSON DTOs and private bearing math remain
- [ ] Update USER_GUIDE.md if needed
- [ ] Update docs/architecture.md
