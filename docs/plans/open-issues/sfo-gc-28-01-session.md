# SFO GC 28/01 session — five bug fixes

## Context

The user ran the S1-SFO-2 Ground Control 28/01 training session and reported five problems. The bug bundle is `C:\Users\lefto\Downloads\S1-SFO-2 _ Ground Control 28_01.yaat-bug-report-bundle.zip` (1,696 s long, with client and server logs). Each report has been traced to a root cause (evidence below). The user settled four design questions: CLANDF always lands and stops; the T exit limit is per-airport data; M1/M2 applies to auto-routes and warns on explicit clearances; the push fix is only to hold the timed TAXI.

Shared setup: install the bundle as a fixture with `python tools/bug_bundle.py install "<bundle>" --desc sfo-gc-28-01`. It lands as `tests/Yaat.Sim.Tests/TestData/sfo-gc-28-01-recording.yaat-bug-report-bundle.zip`. Trim it per test where that makes replay faster. Every fix is TDD: write a failing test, confirm it fails, fix, confirm it passes. Each fix below is its own commit on `main`.

## 1. CLANDF lands and stops, even from past the threshold (SKW5416 → TAI562 go-around)

Root cause:
- SKW5416's first go-around was by design. SWA2644 was crossing 28L while SKW5416 was about 20 s out (client log: `go-around: SWA2644 on 28L (crossing…)`).
- CLANDF came in at t=1160, with the aircraft 0.81 nm past the threshold.
- `PatternCommandHandler.TryForceLanding` (`src/Yaat.Sim/Commands/PatternCommandHandler.cs:4298-4354`) never checks the aircraft's position.
- `FinalApproachPhase.ApplyForcedLandingGuidance` (`src/Yaat.Sim/Phases/Tower/FinalApproachPhase.cs:1690-1717`) uses an unsigned `GeoMath.DistanceNm` (`:678`). It aimed at a point behind the aircraft and dove it at 6,083 fpm.
- Speed only came down at the type's normal rate (about 2 kt/s). Nothing sets `Targets.DesiredDecelRate`.
- The aircraft touched down with about 1,450 ft of runway left, stopped about 2,300 ft past the end, and sat in RunwayExit with no exit node. That blocked 28L, and TAI562 went around (client log: `TAI562 go-around: SKW5416 on 28L (landing rollout…)`).

Change:
- While `ForceLanding` is set, forced guidance writes a hard `DesiredDecelRate`. That includes the air, which `FlightPhysics.SpeedChangeRate` already honours (`FlightPhysics.cs:1281`). Pick the value with aviation-sim-expert: it is deliberately non-physical, per the user.
- When the aircraft is past the threshold, aim at a touchdown point ahead on the runway, not the threshold. Do this by signing the along-track distance in `ApplyForcedLandingGuidance`.
- LandingPhase keeps a forced rollout decel after touchdown. Today the flag is cleared at touchdown (`LandingPhase.cs:785`); keep that until the aircraft has stopped or exited. The rollout decel is sized from the runway left (`RolloutDecelRate`, `LandingPhase.cs:519/1187`), so the aircraft stops on the pavement and takes the last exit.
- Guard: a RunwayExit that finds no exit node must not sit on the runway forever. Pick the last exit reachable behind the aircraft, or its end-of-runway turnoff.

Tests:
- Replay the fixture to just before the t=1160 CLANDF. Assert SKW5416 touches down on 28L, stops before the far end, and leaves the runway.
- Assert TAI562 does not go around because of SKW5416. It can still go around for another reason, so assert on the blocking occupant.
- A unit-level test: CLANDF from 0.8 nm past the threshold produces no descent aimed behind the aircraft.

## 2. The 28R exit on T holds two aircraft only when both are CWT G or smaller (per-airport data)

Root cause:
- `RunwayExitPhase.TryStartParallelCrossing` (`src/Yaat.Sim/Phases/Tower/RunwayExitPhase.cs:1077-1152`) moves the aircraft into Taxiing and then HoldingShort at the **28L** bar. It returns before `MarkHoldShortNodeOccupied` (`:1044`).
- From then on nothing claims the 28R exit hold-short node on T.
- `SimulationEngine.BuildOccupiedHoldShortNodes` (`src/Yaat.Sim/Simulation/SimulationEngine.Tick.cs:1278-1313`) therefore reports T as free.
- The bundle has overlaps at t≈925 (SKW5899+SWA2644), t≈1085 (SWA2644+JBU115) and t≈1435 (JBU115+SKW4672). T is only 356 ft between the two bars.

Change:
- New `sfo.json` field `exitCapacity` in `src/Yaat.Sim/Data/ARTCCs/ZOA/Airports/sfo.json`: `[{ "runway": "28R", "taxiway": "T", "maxAircraft": 2, "maxAircraftAboveCwt": 1, "cwtThreshold": "G" }]`, with a DTO beside `OneWayConstraintDefinition`. Read it the way `oneWayEdges` is read.
- Count occupancy of the segment from the exit bar to the next bar. That covers aircraft in RunwayExit, HoldingAfterExit, a pull-up Taxiing route whose first node is the exit bar, and HoldingShort at the parallel bar reached from it.
- Expose it as a query, "can this aircraft (with its CWT) take this exit". Consult it at the existing occupancy checks: `RunwayExitPhase.cs:196, 424, 524` and `LandingPhase.cs:1313, 1431`.
- CWT comes from `WakeTurbulenceData.GetCwt` (`src/Yaat.Sim/WakeTurbulenceData.cs:18`); G, H and I count as "G or smaller".
- An explicit `EXIT T` still bypasses the check, as documented in `landing-and-runway-exit.md:133`.

Tests (real SFO layout):
- A B737 holding short of 28L on T blocks an A321 landing 28R from exiting at T.
- Two E75Ls may share T.
- Replay the fixture near t=1085 and assert JBU115 does not exit on T while SWA2644 is on it.

Docs: `docs/landing-and-runway-exit.md`, and the airport-sidecar schema doc wherever `oneWayEdges` is documented.

## 3. ASDE-X history dots collapse when a target stops (yaat-server)

Root cause:
- CRC replaces the dot list with `HistoryLocations` from each target DTO (`crc-decompiled …Asdex.Targets/AsdexTarget.cs:40`).
- yaat-server only resends when `AsdexTargetFingerprint` changes (`X:\dev\yaat-server\src\Yaat.Server\Simulation\AircraftChangeTracker.cs:234-241`, captured `:1276-1285`, gate `CrcBroadcastService.cs:2150`).
- The fingerprint is position, heading, track, speed and terminated. None of these change once the aircraft stops, so the last trail sent while it was rolling stays on CRC's screen.
- `SaidTargetFingerprint` (`:1315`) has the same gap.

Change: add a history token to both fingerprints. The newest `PositionHistory` sample, or its count, is enough: the sim appends one every 5 s even when stationary (`SimulationEngine.Tick.cs:1162-1177`).

Test: in the yaat-server tests, a stopped aircraft emits an ASDE-X change on the next history sample. Its `HistoryLocations` converge to the stopped position.

## 4. T1 south ramp: M1 in, M2 out; supers use M1 both ways

Root cause:
- The pathfinder picks by cost alone.
- M1 is movement area (it carries the 01L hold-short), and the gate extension `SegmentExpander.ExtendToDestination` (`src/Yaat.Sim/Data/Airport/Pathfinding/SegmentExpander.cs:3711-3748`) hard-excludes an uncleared movement-area taxiway.
- So arrivals go in on M2. WJA1508 cleared `TAXI T A @B2` resolved as `T A M2 M5 @B2`.
- `sfo.json` has no `oneWayEdges`.

Change:
1. Before writing the data, confirm the M1/M2 node sequences and direction with the `layout-inspect` skill. M1 runs from the A/B junctions (#90/#158) to #401, and M2 from #92/#159 to #401. Only the ramp portion is one-way, not M1's run across A to 01L.
2. Add an optional `exemptWakeClasses: ["Super"]` to `OneWayConstraintDefinition` (`src/Yaat.Sim/Data/OneWayConstraintDefinition.cs:28-38`).
3. Pass the aircraft's wake class into `SearchContext.Compile` (`SearchContext.cs:289`) next to `AircraftCategory`. Make `OneWayResolver` / `ResolveOneWayMoves` (`SearchContext.cs:371`) skip exempt constraints; the resolver cache is keyed per layout, so filter at resolve time.
4. Make `ExtendToDestination` inherit hard one-way exclusion for its own auto-extension. It also has to let a one-way-required movement-area lane (M1 inbound) be taken implicitly when it is the only legal way into the ramp; otherwise the uncleared-movement-area exclusion (`:3741`, `:3906`) blocks it. Check this against 7110.65 with aviation-sim-expert: implying M1 without the controller naming it.
5. An explicit clearance against the rule is still flown, with the existing warning (`RouteMaterialiser.cs:749/809`).
6. Add the `sfo.json` entries: M1 inbound, and M2 outbound, both exempting Super. Then a super cannot take M2 inbound and must use M1 both ways, so M1 needs `block: reverse` with the Super exemption, and M2 `block: reverse` with none.

Tests (real SFO layout, LayoutInspector cross-check):
- `TAXI T A @B2` for a B738 goes in on M1.
- The departure route from B2 to A goes out on M2.
- An A388 or B748 (Super) goes in and out on M1.
- An explicit `TAXI A M2 @B2` inbound follows M2 and warns.

## 5. Scenario timed TAXI waits for the tug (SKW5564)

Root cause:
- The preset chain is `PUSH T7A`, `WAIT 30 SN`, `WAIT 30 TAXI T7A $7A`. The TAXI fired at t≈530, about 175 ft into a ~300 ft push.
- `PushbackPhase.cs:598` clears the push on TAXI.
- The deferred dispatcher (`src/Yaat.Sim/Simulation/SimulationEngine.DeferredCommands.cs:60-64, 143`) ignores an in-progress tug move.
- Starting across the lane, `RampLaneReposition.TryPlanSpotLineUp` then drove a ~560 ft loop.

Change: a scenario-scripted deferred command (`IsScenarioScripted`, `DeferredCommands.cs:140`) that would end an active `PushbackPhase` waits until the push completes, then fires. Instructor commands still cut the push short.

Test: replay the fixture (or spawn SKW5564 at F8 with the same presets). Assert the push completes before Taxiing starts, and no free-space leg heads away from 7A.

## Aviation rulings (aviation-sim-expert, before implementation)

1. **CLANDF** (judgement calls; no FAA source covers an instructor override):
   - Airborne `DesiredDecelRate` is 5.0 kt/s while ForceLanding is set, with a target of Vref, never below it.
   - Aim point with a signed along-track distance. Use the furthest of three: the current position + AGL/tan 6°, the threshold + 1,000 ft, or the current position + 500 ft. If what is left beyond it is shorter than the 6 kt/s stop distance + 500 ft, pull the aim point back toward the aircraft.
   - Rollout decel is v²/(2·(distance to the last exit ahead − 300 ft)), down to that exit's turn-off speed. Floor 3.0, cap 6.0 kt/s. Up to 10 kt/s is allowed only to stop 300 ft before the runway end. Keep ForceLanding until the aircraft stops or crosses the exit hold bar.
   - Descent is capped at 3,000 fpm above 100 ft AGL and 1,000 fpm below. The cap is exceeded only when the capped path cannot touch down with the rollout distance still available.
2. **Exit capacity.** CWT G, H and I count as "G or smaller". An aircraft in RunwayExit on T counts the same as one holding short of 28L on T. If either the occupant or the arrival is above G, capacity is 1. When T is full, take the next exit by the existing logic (AIM 4-3-21.a). An explicit `EXIT T` bypasses the check.
3. **Implied M1.** M1 may be implied only as the connector from the last cleared taxiway (A or B) into the ramp, never across the 01L holding position (AIM 4-3-18.a.5). The resolved route shows in the readback, and the instructor gets an advisory, "M1 not in clearance". The same applies outbound for supers (user decision, reviewed afterwards): M1 is their only legal way out, and the implied lane stops at A, short of the 01L bar. The departure start leg may use nonmovement pavement freely (7110.65 §3-7-2 NOTE 2), up to 4,000 ft, and never reaches a runway holding position.
4. **Scripted TAXI waits for the push.** It fires once the pushback phase is no longer active for any reason (completed, aborted or cancelled), tick-based for determinism. Later scripted commands in the chain queue behind it. Commands that don't end the push (SN, squawk) still fire on time. Instructor-typed commands still interrupt; that is documented as an instructor override.

## Review follow-ups (in progress)

- [ ] ASDE: the fingerprint token must cover exactly the newest 5 dots CRC is sent, not the 10-sample buffer (C# review).
- [ ] Push: later scripted commands the pushback would reject queue behind the held one; the instructor terminal shows the hold (aviation + C# review).
- [ ] Ramp: a TAXI to a spot or gate with no taxiways named is confined, or refused when it can't be (user: confine-or-refuse, per 7110.65 §3-7-2). From inside the ramp it may cut across taxilanes to the spot while staying inside the ramp, like push-across-the-ramp already does (user steer). Also from the C# review: implied lanes as a `TaxiRoute` property instead of warning-text matching; a supers-only closure must not reach aircraft-less searches (`GetForbiddenMovesIgnoringExemptions`); the spot-35 test must assert "no runway hold crossed" rather than "no warning"; a `null` `exemptWakeClasses` must not crash the loader; hoist the repeated categorisations.

## Out of scope

Three related findings (the spot line-up loop, the "Across" `PUSH T7A` from F8, I28L's 65 ft MAP altitude) are backlogged in `docs/plans/MAIN.md` Wave 1.

## Gates and verification

- The `aviation-sim-expert` review for fixes 1, 2, 4 and 5 (CLANDF decel value, exit-capacity rule, implied M1, deferred-until-push). Include the local-references preamble.
- `csharp-reviewer` on the diff.
- Targeted runs: `pwsh tools/gate.ps1 -Log .tmp/test.log -TimeoutSeconds 30 -Slot heavy -- dotnet test -- --filter-class "*.<Class>"`. Then `pwsh tools/test-all.ps1` (fix 3 is in yaat-server; fix 4 changes a public `Yaat.Sim` signature).
- `dotnet build -p:TreatWarningsAsErrors=true` clean.
- End to end: replay the fixture in the client via `yaat-client-driver`, or the replay test, and check SKW5416 and TAI562, T occupancy and SKW5564's push. Check the ASDE-X dots in CRC against a stopped aircraft.
- Docs: `docs/architecture.md`, `docs/landing-and-runway-exit.md`, `docs/ground/pathfinder.md` (wake-class exemption), `docs/ground/pushback.md` (deferred hold), CHANGELOG bullets via `yaat-changelog-and-commit`. Add glossary entries for "exit capacity" and "CWT" if missing.
