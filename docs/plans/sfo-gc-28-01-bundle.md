# SFO GC 28/01 bundle — triaged reports

One RPO session: `S1-SFO-2 _ Ground Control 28_01 (High Intensity Optional).yaat-bug-report-bundle.zip`, client 0.13.4-beta, SFO, 2152 s. It was triaged into 15 issues on 2026-09-22, one issue per root cause. Each issue body carries the evidence, the code references and a hypothesis. The hypotheses are unverified, so every fix starts with a failing test at HEAD (`test-fix`). Install the bundle once as a TestData fixture (`bug_bundle.py install … --desc sfo-gc-28-01`) and reuse it across the waves.

Take the waves top to bottom. Wave 1 is small and removes RPO workarounds. Wave 2 shares the pathfinder code, so fix it together. Wave 3 measures before it designs. Wave 4 is the two features.

## Wave 1: small fixes

- [ ] **Radar View lists every METAR for a tower or ground student position** (user steer 2026-09-22, with #449): the radar filters by the student position's STARS area (`ArtccConfigService.GetPositionDisplayConfig`), which returns nothing for a facility with no STARS configuration, so SFO GC shows KAUN and the rest. Wanted, as CRC STARS does: the position's subordinate airports; OAK_TWR shows only OAK, OAK_APP and SFO_DEP their own areas' airports
- [ ] **#450 Terminal search box too narrow, no clear button**: `TerminalPanelView.axaml`, following the ✕ pattern in `FindBarView.axaml`
- [ ] **#451 An aircraft stopped on an edgeless spot marker can't be taxied**: the start-node fallback accepts a node with zero edges. Fix site: `GroundCommandHandler` start-node lookup, `AirportGroundLayout.FindNearestNode`
- [ ] **#452 `PUSH Y TAIL S` faces the reciprocal**: the facing is taken from `AirportGroundLayout.GetEdgeBearingForTaxiway`, which returns the only edge at the exit node regardless of the hint
- [ ] **#453 `GIVEWAY` never releases on its own**: decided 2026-09-22 that it resumes once the target has passed or no longer conflicts, and `RES` stays as the manual override. Fix site: `GroundCommandHandler.TryGiveWay`. Gate: `aviation-sim-expert`

## Wave 2: taxi routing (`SegmentExpander`, `RouteCostFunction`; `docs/ground/pathfinder.md`)

- [ ] **#454 A named numbered taxilane is replaced by its parallel sibling** (T5A → T5B, T8 → B5 T9, T9 via T8/B5). Numbered lanes are all free, and touching the named lane at a junction counts as honouring it
- [ ] **#455 `TAXI A` joins A across the airport instead of via the connector the aircraft already holds short of** (K): two parts, a wrong-side junction pick and a cleared taxiway not counted as authorized
- [ ] **#456 `TAXI Tn $n` overshoots the spot or reaches it from the far side of the lane**: reproduce at HEAD first, since 93ae8e91 touched this. The Wave 4 Backlog item on `TryStopNoseAtSpot`'s straight-line distance may be the same stop
- [ ] **#457 `TAXI A F 28L` crosses 01L/19R twice to avoid one unauthorized connector**: choose between a curated SFO connector and a runway-crossing cost in `RouteCostFunction`. Gate: `aviation-sim-expert`

## Wave 3: ground motion and push geometry

- [ ] **#458 A B744 holding short of T on B blocks traffic crossing on T**: measure before designing (stop point against the bar, nose to T's centreline, the crosser's path, the detector's clearance). `HoldShortAnnotator.ComputeHoldShortPositions`, `GroundConflictDetector`
- [ ] **#459 Turn from T onto B overshoots past 90° and corrects back**: the `GroundNavigator` entry-alignment family (`Ual58Spot9ReversalTests` is the precedent)
- [ ] **#460 Push-route drawing: no first-leg arrow, and a refusal when facing a taxiway (`PUSH A F1` too)**: `GroundViewModel.RefreshPushRoutePreview`, `TugPathCheck.MovementAreaRefusal`. Related: the push-route preview plans with no parked neighbours (Bug reports list)

## Wave 4: features

- [ ] **#461 Best-effort `TAXI` toward a runway or gate the named route doesn't reach**: taxi the given route, use the destination as the direction hint on the last taxiway, and hold short before leaving it. Needs a design interview and an aviation review first
- [ ] **#462 Choose push or pull per segment of a drawn push route**: `GroundViewModel.AddPushWaypoint`, `TugMovePlanner`
