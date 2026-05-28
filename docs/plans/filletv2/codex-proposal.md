# Clean Room Ground Fillet Builder

## Summary

`AirportGroundLayout` is the shared mutable ground graph: nodes, straight edges, Bezier arc edges, runway metadata, adjacency rebuilding, and query helpers for taxi routing, runway exits, hold-short search, nearest-node selection, and parking/spot lookup.

`FilletArcGenerator` is the current in-place topology transformer: it converts eligible intersections into tangent nodes plus `GroundArc`s, then repairs topology with several cleanup passes.

The clean room implementation should live beside the current generator at first, produce a comparable `AirportGroundLayout`, and keep production behavior on the current generator until comparison data is good.

## Generator Interface

- [ ] Add an `IFilletArcGenerator` interface in the airport data namespace so implementations can be selected, run, and compared through one contract.
- [ ] Keep the interface graph-oriented: it accepts an `AirportGroundLayout`, mutates that layout in place, and returns a result object with statistics and diagnostics.
- [ ] Wrap the existing static `FilletArcGenerator.Apply` behind a `CurrentFilletArcGenerator` adapter that implements the interface.
- [ ] Implement the new clean room generator as a separate `CleanRoomFilletArcGenerator` that implements the same interface.
- [ ] Add a small registry or factory that maps `FilletGenerationMode` to `IFilletArcGenerator` without reflection or string lookups.

```csharp
internal interface IFilletArcGenerator
{
    string Name { get; }

    FilletGenerationResult Apply(AirportGroundLayout layout);
}

internal sealed record FilletGenerationResult(
    string GeneratorName,
    FilletStatistics Statistics,
    IReadOnlyList<FilletDiagnostic> Diagnostics
);

internal sealed record FilletDiagnostic(
    FilletDiagnosticSeverity Severity,
    string Code,
    string Message
);

internal enum FilletDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}
```

## Implementation Checklist

- [ ] Add a second fillet mode under the airport parsing path: `None`, `Current`, and `CleanRoom`.
- [ ] Preserve the existing `GeoJsonParser.Parse(..., bool applyFillets)` overload by mapping `false` to `None` and `true` to `Current`.
- [ ] Route `GeoJsonParser` fillet application through the `IFilletArcGenerator` factory.
- [ ] Implement the clean room generator as an analysis stage followed by a deterministic graph rewrite stage.
- [ ] Analyze the unfilleted base graph into immutable junction plans.
- [ ] Classify shape points, runway-protected nodes, hold-short and parking exclusions, collinear pairs, and eligible turn pairs.
- [ ] Apply all planned graph operations in one rewrite pass.
- [ ] Split source segments at tangent stations.
- [ ] Add Bezier arcs with accurate tangent bearings, distance, and minimum radius of curvature.
- [ ] Preserve runway threshold and collinear nodes with stubs when those nodes must remain reachable.
- [ ] Carry through untouched source edges without broad nearest-neighbor rescue logic.
- [ ] Normalize duplicate edges, duplicate arcs, degenerate arcs, self-loops, distances, and adjacency after the rewrite.
- [ ] Treat orphan rescue, parallel bypass removal, and direct shortens as validation failures or explicit planned operations, not generic cleanup passes.

## Comparison Harness

- [ ] Build both current and clean room layouts from the same GeoJSON input.
- [ ] Apply generators through `IFilletArcGenerator` instances so the harness can compare any two implementations.
- [ ] Start each comparison from equivalent unfilleted layouts, either by cloning the base layout or rebuilding it from the same classified GeoJSON features.
- [ ] Compare node counts by type and rounded position rather than node ID.
- [ ] Compare straight edge signatures by rounded endpoint positions and taxiway name.
- [ ] Compare arc signatures by rounded endpoints, taxiway set, tangent bearings, minimum radius, and distance.
- [ ] Compare behavior probes for runway exits, hold-short reachability, parking-to-runway taxi routes, and known OAK/SFO/FLL regressions.
- [ ] Add a `Yaat.LayoutInspector` option to dump both variants side by side as structured JSON.
- [ ] Add an optional HTML overlay that renders both variants and highlights mismatched nodes, edges, and arcs.

## Requirements To Preserve

- [ ] Shape-point/manual curve chains remain authored geometry and are not filleted as real intersections.
- [ ] Runway centerline edges remain straight centerline segments.
- [ ] Runway-to-taxiway arcs are transitions and never count as runway centerline segments.
- [ ] `GroundArc.MinRadiusOfCurvatureFt` remains accurate for pathfinding and ground-speed limits.
- [ ] Arc tangent bearings remain accurate in both traversal directions.
- [ ] Output remains the existing `AirportGroundLayout` contract.
- [ ] Adjacency is rebuilt after generation.
- [ ] No edge or arc references a missing node.
- [ ] Temporary side-by-side support is removed after acceptance; replace the old generator rather than keeping both as permanent production choices.

## Test Plan

- [ ] Add focused topology tests using real OAK, SFO, and FLL layouts.
- [ ] Assert no missing-node references, self-loops, duplicate same-taxiway edges, degenerate arcs, arc-only tangent nodes, or runway-centerline displacement.
- [ ] Add comparator tests that run current versus clean room layouts and snapshot mismatch reports for known airports.
- [ ] Run existing ground-path and runway-exit regression tests that exercise this surface: `AirportE2ETests`, `RunwayAwareExitTests`, OAK/SFO all-exit tests, FLL taxi-backtrack, SFO 28R exit, OAK taxi spin, and pushback/taxi start-node tests.
- [ ] Classify each mismatch as accepted improvement, clean room bug, or current-generator dependency before changing production mode.
- [ ] Run targeted tests with `timeout 30 dotnet test ... 2>&1 | tee .tmp/<name>.log`.
- [ ] Run `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log` before implementation commits.

## Assumptions

- [ ] The first implementation is comparison-only; production parsing continues to use the current generator by default.
- [ ] Clean room means algorithmically independent, not a new graph data model.
- [ ] Node IDs are not stable comparison keys; geometry and behavior are the acceptance surface.
- [ ] Aviation realism review is required before switching production behavior because this affects ground ops and runway exit behavior.
