# SKW3078 spin at SFO @268 — session handoff

Investigation in progress on a ground taxi bug captured by
`tests/Yaat.Sim.Tests/Simulation/Skw3078FixComparisonCapture.cs`. SKW3078
spins in place (heading rotates 18–22°/s while position barely moves) from
~t=825 to t=963 while taxiing `TAXI E A @B10` near node #268, the SFO E/F
4-way crossing south of 28L.

This document is a handoff for a fresh session.

## What's done in this branch

Commits, oldest first:

- `0586d3d` **fix: ground conflict stop distance accounts for trailer length**
  — A350 trailing E175 used to nose into the leader's tail because
  `GetSeparation` ignored the trailer's length.
- `0e12ed9` changelog entry for the above.
- `21f2cd2`, `f2d7734`, `8f547c8`, `2d24259` — Yaat.LayoutInspector tooling
  improvements: comma-separated lists, `--node-depth`, `--walk-trace`,
  pathfinder forensics (foreign-twy / tight-arc detection), interactive HTML
  with search + toggle highlights + per-aircraft table, CSS/JS extracted to
  companion files in the Editorial Forensic style.
- `9cc7226` **fix: drop duplicate corner arcs at intersections post-fillet**
  — `RemoveDuplicateCornerArcs` cleanup pass collapses arcs that round the
  same physical corner to the largest-radius survivor. SFO @268 went from 6
  arcs to 4. Investigation writeup in
  `docs/plans/open-issues/fillet-parallel-edges-at-268.md`.

## What's pending

### 1. Changelog entry for the fillet cleanup fix

`9cc7226` is user-visible (fewer redundant arcs at airport intersections;
cleaner taxi paths). Add a `### Fixed` bullet under `## Unreleased` in
`CHANGELOG.md`. Suggested phrasing:

> Aircraft taxiing through 4-way taxiway intersections no longer encounter
> redundant fillet arcs at the same corner. Where upstream intersections
> previously injected parallel bypass edges along the same centerline (e.g.
> SFO E/F at @268), the pair iterator emitted multiple arcs per corner with
> different radii; a post-fillet cleanup now keeps only the largest-radius
> arc per (intersection, corner) and discards the duplicates.

The LI tooling commits don't need changelog entries (dev-only).

### 2. Uncommitted test file

`tests/Yaat.Sim.Tests/Simulation/Skw3078TaxiEAtoB10RouteTests.cs` is
uncommitted. It contains two tests that document the route at @268 — one
synthetic (spawn at node 852, issue `TAXI E A @B10`, inspect the resolved
route) and one bundle-replay (load the recording, replay to t=820, inspect
the live route). Both currently pass — they assert "no immediate
reversals", which is satisfied. They're useful as regression baselines for
Issue 2 work. Decide whether to commit as-is or rework once Issue 2 is fixed.

### 3. Issue 2: SKW3078 spin

Still open. The fillet cleanup eliminated the worst arc-count anomaly but
**did not fix the spin** — the underlying navigator/path-walking issue
remains. Details below.

## What we know about Issue 2

### Symptom

In `Skw3078FixComparisonCapture` (run any of `Capture_Before`,
`Capture_After`, `Capture_AfterNoStationaryRestriction` to produce
`.tmp/skw3078-*.json`), the tick recording for SKW3078 between t=825 and
t=963 shows:

- Position barely moves (gs oscillates 0–30 kt with frequent dips to 0).
- Heading rotates continuously at ~18–22°/s — far above the normal taxi
  turn rate. At t=831→832 the heading flips by 158° (285° → 126°) in a
  single second, then continues rotating.
- The aircraft eventually pins at hdg=307°, gs=0 from t=935 onward.

Visually (in LI HTML), the aircraft appears to do a slow pirouette near
node #268 while neighboring aircraft (THY9WC nearby) stop too late and
nose into a stationary E175 (that part was Issue 1, now fixed).

### What the pathfinder builds (this is NOT the bug)

Both the synthetic-spawn route from node 852 (an E hold-short of 28L,
heading SW along E) and the bundle-replay's live route at t=820 produce
the **same** 137-segment route to parking B10. The early segments are:

```
[ 0]  852 → 1484  (E)
[ 1] 1484 → 1480  (E)
[ 2] 1480 →  141  (E)
[ 3]  141 → 1748  (E)
[ 4] 1748 → 1754  (E)
[ 5] 1754 → 1753  (E)
[ 6] 1753 → 1752  (F)   ← unauthorized hop onto taxiway F
[ 7] 1752 → 1755  (F)   ← unauthorized hop on F
[ 8] 1755 → 1750  (E)
[ 9] 1750 →   57  (E)
[10]   57 → 1238  (A)
…
```

Segments 6–8 are the F-hops the controller never authorized
(`TAXI E A @B10`, no F in the sequence). This is what triggers the spin —
the navigator faithfully drives toward each segment endpoint and the tight
geometry (9 ft / 13 ft radius arcs at this cluster — see below) demands
<2 kt, but the aircraft is moving 5+ kt and overshoots, producing the
heading-rotation pattern.

### Where the F-hops come from

`TaxiPathfinder.WalkTaxiway` walks E from node 141 and **dead-ends at
node 1753** because 1753 has no outgoing E edges (its only non-arc edges
are labeled F). The pathfinder then bridges to A using cached A* from 1753
to a node on A; the cheapest bridge happens to use the F edges
`1753↔1752↔1755` to reach 1750 (which is on E going SW — i.e. now A in the
walk's terms).

The cluster topology around @268 (lat 37.6189, lon −122.3802):

```
1748 (E) ─ E ─ 1754 (E) ──── F-E arc ──── 1753 (F-edges only)
                                              │ F  (7 ft)
                                              ↓
                                            1752 (F-edges only)
                                              │ F  (42 ft)
                                              ↓
                                            1755 (F-edges only)
                                              │ F-E arc 9 ft radius, maxSafe=1.9 kt
                                              ↓
                                            1750 (E going SW) ── E ─ 57
```

So even though 1753, 1752, 1755 are physically stacked within ~70 ft of
the @268 intersection (they're all `Fillet:tangent-node@268`), they're
labeled F and the only way E can resume is via the F-E arc at 1755↔1750
which has radius=9 ft, maxSafe=1.9 kt — un-taxiable above slow taxi.

### What the @268 cleanup pass did and didn't fix

The cleanup pass (commit `9cc7226`) removed the *redundant* arcs — the
ones that rounded the same corner with smaller radii because parallel
bypass edges existed. After cleanup @268 has 4 arcs:

- `1747↔1748`  r=74.1 ft  turn 101.5°  (corner SE+NE, F-going-NW × E-going-SW)
- `1749↔1750`  r=13.2 ft  turn  79.3°  (corner SE+SW, the tight one) ← ONLY arc at this corner
- `1753↔1754`  r=74.6 ft  turn  78.4°  (corner NW+NE, F-going-SE × E-going-SW)
- `1755↔1750`  r= 9.0 ft  turn 100.8°  (corner NW+SW, the *very* tight one) ← ONLY arc at this corner

The 9 ft and 13 ft arcs are the only arcs at their corners — the cleanup
keeps the largest radius, but if there's only one, it stays. Those tight
arcs come from constrained tangent placements: the E(57↔268) edge is only
22 ft long, so any tangent on it sits within 11 ft of @268, and the
resulting arc radius is mechanically tiny.

The route is still bad because:

1. The pathfinder still runs `WalkTaxiway` on E and still dead-ends at
   1753 (the cleanup didn't remove the tangent nodes, just some arcs).
2. The bridge code from 1753 still picks the F detour through
   `1752→1755→1750` because those F edges still exist.
3. The 9 ft / 13 ft arcs the navigator encounters along that detour are
   still there, still dictating <2 kt limits, and the aircraft still
   overshoots them at 5 kt.

## Investigation entry points

### Files to read

- `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` — the per-tick navigator
  that decides target heading and speed limits when an aircraft is
  taxiing. Spinning is most likely a navigator-level reaction to the
  bad route + tight arcs, not a pathfinder bug per se.
- `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs` — `WalkTaxiway` (~line
  1093) and `FindRoute` (the cached bridge). The F-hop comes from here.
- `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs` — `RemoveDuplicateCornerArcs`
  is at the bottom; the parallel-edge issue is described in
  `docs/plans/open-issues/fillet-parallel-edges-at-268.md`.

### Tools to use

- LI HTML with `--ticks .tmp/skw3078-after.json` — open the rendered HTML,
  search "1753" or "268" in the highlight panel, scrub through the time
  slider 825–965 to see the spin physically.
- LI's `--walk-trace 852 E` — shows exactly where `WalkTaxiway` dead-ends
  and why (it's already documented; just re-run after any change).
- LI's `--pathfinder 852 E A --pf-dest-parking B10` — shows the full
  resolved route plus FOREIGN-TWY and TIGHT-ARC diagnostics. The current
  output flags F at indices 6–7 and the 9 ft / 13 ft tight arcs.
- `python tools/bug_bundle.py track <bundle> --pair SKW3078 SKW5707
  --start 820 --end 970` — sparse 5 s snapshots from the bundle.

### Reproducing the spin in a test

```bash
timeout 60 dotnet test tests/Yaat.Sim.Tests \
  --filter "FullyQualifiedName~Skw3078FixComparisonCapture" \
  --logger "console;verbosity=normal" 2>&1 | tee .tmp/skw3078-rerun.log
```

The capture writes `.tmp/skw3078-after.json` etc. Render with:

```bash
dotnet run --project tools/Yaat.LayoutInspector -- \
  tests/Yaat.Sim.Tests/TestData/sfo.geojson \
  --ticks .tmp/skw3078-after.json --html .tmp/skw3078-after.html
```

## Suggested next steps for the new session

Pick one of these angles. They're listed in increasing scope.

### A. Make `WalkTaxiway` not dead-end at 1753

Smallest scope. The walk currently treats 1753 as a dead-end because its
only outgoing edges are labeled F. But geometrically 1753 is on the E
centerline (origin tag `Fillet:tangent-node@268 on-F(→1235)` is misleading;
the node sits *between* E-NE tangents and the F-NW tangents and is part of
the E pass-through topology). If `WalkTaxiway` can be taught to step from
1753 onto the F-E arc that *exits* on E side (1754↔1753 arc going back NE,
or via 1752→1755→1750 arc chain landing on E at 1750), the route stops
needing the F bridge.

Risk: low if scoped to the case "no E continuation but there's an F-E arc
with an E exit". Verify against the OAK pathfinder regression set listed in
`fillet-parallel-edges-at-268.md`.

### B. Remove redundant tangent nodes during MergeCoincidentNodes

Larger scope. Nodes 1745, 1746, 1752 (and others) at @268 only exist
because the parallel bypass edges from upstream caused the pair iterator
to create extra tangent placements. After the arc cleanup, those tangent
nodes have no associated phase-c-arc and are vestigial. A separate cleanup
pass after `RemoveDuplicateCornerArcs` could prune tangent nodes whose
only remaining edges are short tangent-link / passthrough stubs that
duplicate a more direct route.

Risk: medium. Affects graph topology, not just arcs. Same regression set.

### C. Replace the tight-radius arcs at @268's E-SW corner

Different angle. The 9 ft / 13 ft arcs come from the constrained 22 ft
E(57↔268) edge. One option: when the available edge length forces a
sub-5 kt arc, suppress the arc entirely and let the aircraft pivot at the
intersection node directly (sharp corner, slower turn but no overshoot).
This requires a "minimum arc viability" threshold in the fillet generator.

Risk: medium-high. Changes how taxi turns work geometrically, may affect
many intersections beyond @268.

### D. Make the navigator handle un-taxiable arcs gracefully

Independent of the fillet bugs. If the navigator detects an arc with
`maxSafe < currentSpeed` and the aircraft is *committed* to traversing it,
it should slow harder *before* entering the arc rather than overshooting.
The current behavior is to clamp speed mid-arc, which produces the
overshoot/correct/overshoot heading-rotation pattern we see.

Risk: contained to `GroundNavigator` / `FlightPhysics` ground-taxi code.
Unlikely to affect the OAK pathfinder regression set. **This is probably
the highest-leverage fix** — it would prevent spin behaviors generally,
not just at @268, even if other graph fixes also land later.

## Don't waste time on

- Fighting the fillet build logic to eliminate parallel edges at build
  time. Four attempts each broke OAK G/D and SFO M2/A1 pathfinder tests.
  Provenance signals (origin tags) aren't reliable enough to discriminate
  bypass artifacts from legitimate post-fillet edges. The cleanup pass
  approach worked precisely because it doesn't touch build semantics.
- The "subagent fresh perspective" — I tried that. Their recommended
  consume-as-walked variant produced the same OAK regressions plus didn't
  even fix @268 (because @141's `phase-d-shorten` add takes a different
  code path that doesn't go through `InterpolateAlongWalk`).

## Memory worth seeding

In `C:\Users\Leftos\.claude\projects\X--dev-yaat\memory\`:

- A `feedback_*.md` capturing "post-build cleanup beats build-time
  surgery" for graph artifacts whose build-time discrimination signal is
  unreliable. The session ate ~90 min trying build-time fixes before the
  post-fillet cleanup pass landed cleanly.
- A `project_*.md` capturing the @268 cluster topology if Issue 2 work
  continues to live in that area.
