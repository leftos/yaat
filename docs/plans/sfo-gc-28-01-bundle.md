# SFO GC 28/01 bundle — triaged reports

One RPO session: `S1-SFO-2 _ Ground Control 28_01 (High Intensity Optional).yaat-bug-report-bundle.zip`, client 0.13.4-beta, SFO, 2152 s. It was triaged into 15 issues on 2026-09-22, one issue per root cause. Wave 1 (#448–#453) was fixed 2026-09-22 on the `wt/zen-raven` session branch; it lands on main when that session ships. Each remaining issue body carries the evidence, code references and a hypothesis. The hypotheses are unverified, and two of them (#453, #454) turned out wrong once reproduced. So every fix starts with a failing test at HEAD (`test-fix`).

**The recording.** The full 22 MB bundle is not in the repo; ask the user for it. A 400 s trim is committed as `tests/Yaat.Sim.Tests/TestData/issue453-asa826-giveway-recording.yaat-bug-report-bundle.zip`. It covers the first 400 s only, and every remaining item happens later. For a replay test, trim the full bundle around the item's time (`bug_bundle.py trim`), install it (`bug_bundle.py install --issue N`), then regenerate the routing census for the new fixture (`YAAT_ROUTING_CENSUS_REGENERATE=1` on `RecordingCorpusRoutingCensusTests`; the diff must be a pure addition). A dispatch-level test on the real SFO layout, built from a pose read out of the bundle (`bug_bundle.py snapshot --at T --callsign X`), is cheaper and was enough for #451, #452 and the #454 repro.

Take the waves top to bottom. Wave 2 shares the pathfinder code, so fix it together. Wave 3 measures before it designs. Wave 4 is the two features.

## Wave 2: taxi routing (`SegmentExpander`, `RouteCostFunction`; `docs/ground/pathfinder.md`)

- [ ] **#454 A named numbered taxilane is replaced by its parallel sibling** (T5A → T5B, T8 → B5 T9, T9 via T8/B5). **Reproduced 2026-09-22:** SKW3398 holding short of K on B, E75L at (37.621827131692896, -122.38554745714856) nosed 297.9° true, gets `TAXI B K A T5A @D1` → "Taxi via B K A T5B @D1". The route never touches T5A, so the issue's "touching counts as honouring" hypothesis is wrong: T5A is dropped outright. D1's only edge leads to T5B's side of the alley, and `RampLaneReposition` logs "no node on the route to D1 is worth cutting to the stand from". Wanted: drive T5A, then cut across to D1, or refuse by name, but never swap lanes silently. A draft RED test (dispatch on the real SFO layout, asserting T5A is driven and T5B is not) is in the session's `.tmp/NamedTaxilaneHonouredTests.cs.wip`, if that worktree still exists. Enabling the `SegmentExpander` SimLog category printed nothing for this route, so the parking-destination path that drops T5A is still unidentified: find it first (`SegmentExpander.SelectBestParkingStop` / `ExtendToDestination` are the suspects). UAL2627 `TAXI B4 T8 @F1` and UAL2183 `TAXI T9 $9` are the other cases
- [ ] **#455 `TAXI A` joins A across the airport instead of via the connector the aircraft already holds short of** (K): two parts, a wrong-side junction pick and a cleared taxiway not counted as authorized. Same SKW3398 pose as #454 (bundle t=585–589)
- [ ] **#456 `TAXI Tn $n` overshoots the spot or reaches it from the far side of the lane**: reproduce at HEAD first, since 93ae8e91 touched this. The Wave 4 Backlog item on `TryStopNoseAtSpot`'s straight-line distance may be the same stop. SKW5590 is at bundle t=0–65
- [ ] **#457 `TAXI A F 28L` crosses 01L/19R twice to avoid one unauthorized connector**: choose between a curated SFO connector and a runway-crossing cost in `RouteCostFunction`. Gate: `aviation-sim-expert`

## Wave 3: ground motion and push geometry

- [ ] **#458 A B744 holding short of T on B blocks traffic crossing on T**: measure before designing (stop point against the bar, nose to T's centreline, the crosser's path, the detector's clearance). The user's point, 2026-09-22: the holder's nose blocks the crosser, not its wingtips. `HoldShortAnnotator.ComputeHoldShortPositions` (setback = length + 30 ft to the aircraft's centre), `GroundConflictDetector`
- [ ] **#459 Turn from T onto B overshoots past 90° and corrects back**: the `GroundNavigator` entry-alignment family (`Ual58Spot9ReversalTests` is the precedent). UAL2164 at bundle t≈1905–1950
- [ ] **#460 Push-route drawing: no first-leg arrow, and a refusal when facing a taxiway (`PUSH A F1` too)**: `GroundViewModel.RefreshPushRoutePreview`, `TugPathCheck.MovementAreaRefusal`. Related: the push-route preview plans with no parked neighbours (Bug reports list)

## Wave 4: features

- [ ] **#461 Best-effort `TAXI` toward a runway or gate the named route doesn't reach**: taxi the given route, use the destination as the direction hint on the last taxiway, and hold short before leaving it. Needs a design interview and an aviation review first
- [ ] **#462 Choose push or pull per segment of a drawn push route**: `GroundViewModel.AddPushWaypoint`, `TugMovePlanner`

## Found on the way (not yet an issue)

- [ ] When the airport a view depicts has no METAR in the loaded weather, the Ground View shows the first station in the list, and the Radar View shows every station (`MainViewModel.Weather.PickGroundWeather`, `FilterWeatherForPosition`). Ask the user whether it should show nothing instead
