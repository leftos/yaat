# Pathfinder & Ground Navigator — Clean Room Review

Two independent reviews of TaxiPathfinder, GroundNavigator, FilletArcGenerator, and GeoJsonParser.

## HIGH — Bugs

- [ ] **Arc speed constraint distances wrong during arc traversal**
  `GroundNavigator.cs:364` — `_speedConstraints[].PathDistNm` are offsets from the segment
  *endpoint*, but `dist` during arc traversal is distance to the current *waypoint* (~15ft).
  `totalDist = dist + pathDist` underestimates real distance → premature braking through arcs
  near hold-shorts or tight turns.

- [ ] **Inverted guard in `PickBestStartEdge`**
  `TaxiPathfinder.cs:993` — `if (candidates.Count == 0) return candidates[0]` throws
  `IndexOutOfRangeException`. Currently shielded (caller only invokes when count >= 2), but
  latent crash if method is called from new code. Fix: `if (candidates.Count <= 1)`.

- [ ] **Dead code: `ComputeArcSteering` + `AdvanceByDistance`**
  `GroundNavigator.cs:479-571` — Public static method with zero callers. Replaced by polyline
  waypoint approach in `SubdivideArc`. Remove both methods.

## MEDIUM — Correctness Risks

- [ ] **Braking uses waypoint dist, not remaining arc dist**
  `GroundNavigator.cs:354` — For the immediate target's braking limit, `dist` is tiny (~15ft to
  next waypoint) while `_currentNodeRequiredSpeed` was computed for the segment endpoint. Safety
  backstop at line 371 partially compensates, but braking model is locally inconsistent during
  arc traversal.

- [ ] **Arc state lost on snapshot restore**
  `GroundNavigator.cs:619-641` — `FromSnapshot` doesn't restore `_currentArc` or `_arcWaypoints`.
  Aircraft mid-arc when snapshotted resume as straight-line, cutting corners.

- [ ] **Orphan rescue ignores taxiway context**
  `FilletArcGenerator.cs:1282-1298` — `RescueOrphanedTangentNodes` connects to globally nearest
  node by position, which could be a parking spot, helipad, or node on a different taxiway across
  a runway. Could create phantom cross-runway edges.

- [ ] **Yen's candidate sort biased toward shortest distance**
  `TaxiPathfinder.cs:495` — `candidates.Sort` always by `TotalDistanceNm` regardless of active
  strategy. For FewestTurns or Fastest, this can miss the optimal K-th path.

- [ ] **A\* lacks closed set**
  `TaxiPathfinder.cs:1180` — Stale priority queue entries get re-expanded. Not a correctness bug
  (non-negative costs), but wastes cycles on dense post-fillet graphs.

- [ ] **`JsonDocument` not disposed in `ParseMultiple`**
  `GeoJsonParser.cs:123` — `JsonDocument.Parse` result never disposed. `Clone()` copies data but
  the document holds unmanaged memory until GC. Wrap in `using`.

- [ ] **`int.Parse` for heading without `FormatException` catch**
  `GeoJsonParser.cs:420` — Malformed heading string (e.g. "N", "12.5") throws `FormatException`.
  The surrounding try/catch only catches `InvalidOperationException`.

- [ ] **Float equality in runway bearing HashSet**
  `FilletArcGenerator.cs:122` — `rwyEdgesBefore` is a `HashSet<(..., double Brg)>`. The
  `Contains` check uses `double` equality which fails on precision. The 1.0° tolerance fallback
  works but causes unnecessary scans for every surviving runway edge.

## MEDIUM — Maintainability

- [ ] **`FilletNode` is ~780 lines**
  `FilletArcGenerator.cs:255` — Four phases (A: plan, B+C: create tangents/arcs, D: rebuild
  edges) with complex shared state. Exceeds 100-line limit. Extract each phase.

- [ ] **`WalkTaxiway` has 11 parameters**
  `TaxiPathfinder.cs:655` — Violates 5-param limit. Group into a `WalkOptions` record.

- [ ] **`ResolveExplicitPath` has 9 parameters**
  `TaxiPathfinder.cs:72` — Same issue. Group optional params into an options record.

- [ ] **`WalkTaxiway` is ~310 lines**
  `TaxiPathfinder.cs:655-978` — Bridge-candidate selection and walk loop should be extracted.

- [ ] **Mutation-during-iteration in `FilletArcGenerator.Apply`**
  `FilletArcGenerator.cs:59-70` — Main loop mutates graph and calls `RebuildAdjacencyLists()` per
  intersection. Processing order affects results with no topological ordering.

## LOW — Performance

- [ ] **`MinDistToTaxiway` O(N) per call in walk loop**
  `TaxiPathfinder.cs:1126` — Called per candidate at each fork. Pre-index nodes by taxiway name.

- [ ] **`RebuildAdjacencyLists` O(N*E) total in fillet pass**
  `FilletArcGenerator.cs:70` — One rebuild per intersection. Incremental updates would help.

- [ ] **`ConnectToNearestTaxiway` scans all nodes**
  `GeoJsonParser.cs:375` — O(N) per parking/helipad. `coordIndex` is available but not used.

- [ ] **`BuildMergeMap` O(N^2) over intersection nodes**
  `FilletArcGenerator.cs` — Compares every pair. Spatial index would make this near-linear.

## LOW — Cleanup

- [ ] **Duplicated comment**
  `TaxiPathfinder.cs:677-678` — "Find all edges on this taxiway from the current node." twice.

- [ ] **Typo: `hasStrightEdge`**
  `FilletArcGenerator.cs:1268` — Should be `hasStraightEdge`.

- [ ] **`CornerSpeedKts` set but never read internally**
  `GroundNavigator.cs:38` — Serialized in snapshots but unused in speed calculations.

- [ ] **Max-based scoring unintuitive**
  `TaxiPathfinder.cs:379` — `finalScore = max(distScore, transScore, timeScore)` means any route
  dominating one metric gets 1.0 even if terrible on others.

- [ ] **`ParseMultiple` re-parses combined JSON**
  `GeoJsonParser.cs:133` — After building JSON string from `JsonElement`s, re-parses everything.
  Could work directly with parsed feature lists.

## Architecture Positives

- Navigator/Phase separation is clean — steering vs route management.
- Multi-strategy scoring in `FindRoutes` is solid for diverse taxi route options.
- Polyline waypoint approach for arcs is simpler and more predictable than carrot-on-stick.
- Bridge strategy scoring intelligently prefers staying on current taxiway.
