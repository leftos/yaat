# #475a — a tug move is held forever beside a staggered neighbour stand

Index line: [MAIN.md](../MAIN.md), **Misc ground issues at OAK (#475)**, part (a). Bundle: the `S1-OAK-2_3_4` bug report bundle attached to the issue.

## Root cause (measured 2026-10-01)

- The report's "SWA1960" is SWA1905 (B738, OAK gate 27): `PUSH TE` at t=1076 stopped 22.5 ft in, `AutoYieldTarget = SWA5456` (B738, gate 29), held until the RPO's `BREAK` at t=1202.
- Gate 29 is parallel to 27 (both 113° true), 133.8 ft right of 27's axis and 42.3 ft further back along the push line. The pair starts 45.4 ft apart outline to outline; a straight push passes the wings abeam at the row's own wingtip gap, 16.4 ft, and never closer.
- `GroundOutlineSweep.Sweep` (`src/Yaat.Sim/GroundOutlineSweep.cs` ~:100-101) sets `floorFt = FloorFt(startFt)`, `FloorFt` = `max(0.5, min(25, start) − 0.5)` (~:71-72) = 24.5 ft here, so the detector (`GroundConflictDetector.TugMoveFoulsParkedAt` ~:1474-1486 → `TugMoveLimit` ~:1354-1374) brakes the push to 0 kt and never releases it. The planner's two `Sweep` callers (`TugMovePlanner.cs` ~:1880, ~:4075) refuse `PUSH $E` off gate 27 for the same reason. With gate 29 empty the push completes in 51 s.
- A scan of parallel B738 stand pairs found the shape at 30 OAK pairs (row gaps 6.2–18.4 ft) and 22 SFO pairs (14.2–22.8 ft).

## Ruling (aviation-sim-expert, 2026-10-01)

- `floor = max(0.5, min(25, startFt, rowFt) − 0.5)` for **every** tug move, turning ones included (a swing closer than the row gap still falls through `rowFt − 0.5`).
- `rowFt`: the least outline clearance when the mover's **parked** outline slides along its own nose axis in the **first move's direction only** (aft for a push, forward for a pull), from 0 to `moverReach + obstacleReach`. Measured once, from the pose where the tow began, and carried unchanged through every later move of the same tow (continuations, mid-push re-plans); it must survive a snapshot round trip (determinism). The planner and the detector read the one `FloorFt`.
- Counted only when the neighbour's nose is within ±10° of the mover's parked nose, same direction (not modulo 180) [J], and `rowFt ≥ 10.0 ft` (`RowAnchorMinFt`, about ICAO Annex 14's smallest stand clearance) [J]; otherwise today's floor (OAK 26→27 at 6.2 ft still holds). A neighbour on the push line overlaps in the slide and keeps today's floor.
- After the change, check that `TugMovePlanner.Choose` still prefers the candidate with more clearance on a tie.
- Docs: the Floor bullet in `docs/ground/pushback.md` (~:209) and the `FloorFt` doc comment get the row anchor, the 10 ft bound and the same-direction 10° test, marked [J]; make the taxilane formula agree between `docs/ground/pushback.md` (~:210, "0.05 × span + 10 ft") and `GroundOutlineSweep.cs` (~:61-67, 0.1 W + 10 / + 20), both from memory (the AC is not on disk).

## Failing test (written and run red against main by the investigating agent)

`tests/Yaat.Sim.Tests/Simulation/Issue475StaggeredStandPushTests.cs`, real OAK layout and navdata, B738s on stands 27 and 29:

- `PushTe_FromGate27_PastB738OnStaggeredGate29_CompletesWithoutBeingHeld` — fails today: "SWA1905 never completed PUSH TE within 120s: held by SWA5456 at 25.7 ft". Asserts completion within 120 s, no zero-limit run over 5 s, and a closest approach ≥ the measured row gap − 1.5 ft.
- `PushTe_FromGate27_IntoParallelAircraftOnThePushLine_StopsShortOfIt` — passes today and must still pass: a B738 parked parallel on the push line 230 ft back stops the push, `AutoYieldTarget` names it, closest ≥ 10 ft.

Shape: `TestVnasData.EnsureInitialized()`, `TestAirportGroundData().GetLayout("OAK")`, a `SimulationEngine` with a `SimScenarioState` (`PrimaryAirportId = "OAK"`), aircraft spawned `AtParkingPhase` at `layout.FindParkingByName("27")`/`("29")` with the stand's `TrueHeading`, `engine.SendCommand("SWA1905", "PUSH TE")`, ticked with `engine.TickOneSecond()` until `HoldingAfterPushbackPhase`, clearance from `GroundOutline.ClearanceBetween(pusher, aTowedNoseFirst: false, parked)`, the row gap from the stands' lateral offset minus `GroundOutlineSize.Of("B738", towedNoseFirst: false).WingspanFt`, with a setup assertion that the layout still staggers 29 behind 27 (row gap between 10 ft and `GroundOutlineSweep.WingtipBufferFt`, start clearance above it). Add the snapshot round-trip test for the carried row anchor, and, since turning moves are covered, a planner test that `PUSH $E` off gate 27 is no longer refused.

Delete this file when the fix lands.
