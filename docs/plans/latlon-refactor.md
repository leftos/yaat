# `LatLon` refactor

## Status

In progress — five commits on yaat, one sibling on yaat-server. Spin off from
the perf-investigation session that landed `adcfc74 perf: cache magnetic
declination per aircraft`, where it became obvious we had no typed coord in
Yaat.Sim and the WMM cache ended up carrying two raw `double` fields instead.

- [x] Commit 1 (yaat): add `LatLon` type
- [x] Commit 2a (yaat): foundation — new API surface on Yaat.Sim types, no caller migrations
- [x] Commit 2b (yaat): migrate ~70 Yaat.Sim internal files to the new API
- [ ] Commit 3 (yaat): migrate Yaat.Client, tools, tests
- [ ] Commit 4 (yaat): remove old API, break wire format
- [ ] Commit 5 (yaat-server, sibling to commit 4): migrate 35 sites

## Decisions (locked)

- **Type name**: `LatLon` (6 chars, unambiguous, reads naturally in signatures).
- **Shape on `AircraftState`**: replace `Latitude`/`Longitude` entirely — no
  back-compat shim. Per CLAUDE.md "no backwards-compat shims, migration paths"
  for unreleased software.
- **Rollout**: four commits on yaat, one sibling commit on yaat-server. Each
  intermediate commit keeps the tree buildable via a temporary dual API
  (`Position` added alongside `Latitude`/`Longitude`). The old API is removed
  only in the final yaat commit, which breaks the wire format and forces the
  yaat-server sibling. See §Commit structure.
- **Definition**: `public readonly record struct LatLon(double Lat, double Lon)` in
  `src/Yaat.Sim/LatLon.cs`. Value type, no heap alloc, record struct gives
  free equality + deconstruction. Field names `Lat`/`Lon` match CRC protocol
  `Point` and existing tuple style; avoids "Latitude/Longitude" verbosity at call
  sites (`ac.Position.Lat` not `ac.Position.Latitude`).

## Why

1. **Type safety** — the perf fix on `FlightPhysics.Update` introduced
   `DeclinationCacheLat` / `DeclinationCacheLon` as two raw `double`s with a
   NaN sentinel. A typed `LatLon?` would express "either we have a cache key
   or we don't" without the NaN dance.
2. **Readability** — `GeoMath.DistanceNm(a.Position, b.Position)` beats
   `GeoMath.DistanceNm(a.Latitude, a.Longitude, b.Latitude, b.Longitude)`.
3. **Parameter mistakes** — 40+ function signatures take `(double lat, double lon)`;
   argument-swap bugs (`lon, lat`) are not caught by the compiler today.
4. **Allocation-free** — `readonly record struct` stays on the stack; no
   performance penalty versus two raw fields.

## External constraints

- **VATSIM CRC protocol** (`../yaat-server/extern/vatsim-vnas/.../Point { Lat, Lon }`
  — MessagePack DTO): **cannot be changed**. Any conversion to/from CRC stays
  at the DTO boundary: `new Point { Lat = p.Lat, Lon = p.Lon }` and
  `new LatLon(point.Lat, point.Lon)`.
- **vNAS data-api scenario JSON**: `Coordinates: { lat, lon }` is external input
  format — parsed via System.Text.Json, so property names are controlled by
  `[JsonPropertyName]`, not the C# identifier. We can freely use `LatLon` in the
  parsed model as long as the JSON keys survive.
- **Recording archive format**: `SessionRecording` / snapshots serialize
  `AircraftState` and `ControlTargets` with `{"Latitude": ..., "Longitude": ...}`
  today. Changing to `{"Position": {"Lat": ..., "Lon": ...}}` breaks any
  recordings already captured. YAAT is unreleased; bundles in `TestData/` are
  regeneratable from the bug-bundle tool. We commit to the new shape and rebake
  any test recordings that fail.

## Scope inventory

### Type-level declarations to migrate (11 sites)

- [ ] `src/Yaat.Sim/AircraftState.cs:33-34` — `Latitude`/`Longitude` → `Position`
- [ ] `src/Yaat.Sim/AircraftState.cs:47-50` (new in `adcfc74`) —
  `DeclinationCacheLat`/`DeclinationCacheLon` → `DeclinationCachePosition : LatLon?`
  (null means "not cached yet", replaces the NaN sentinel)
- [ ] `src/Yaat.Sim/ControlTargets.cs:148-149` — `Latitude`/`Longitude` → `Position`
- [ ] `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs:19-20` (`GroundNode`) —
  `Latitude`/`Longitude` → `Position`
- [ ] `src/Yaat.Sim/Scenarios/AircraftInitializer.cs:15-16` — internal
  `InitResult.Latitude`/`Longitude` → `InitResult.Position`
- [ ] `src/Yaat.Sim/Simulation/Snapshots/AircraftSnapshotDto.cs:17-18` —
  DTO `Latitude`/`Longitude` → `Position`. Wire format changes.
- [ ] `src/Yaat.Sim/Simulation/Snapshots/ControlTargetsDto.cs:26-27` — same.
  Wire format changes.
- [ ] `src/Yaat.Client.Core/Services/ServerConnection.cs:477` (aircraft DTO
  positional record param) → `LatLon Position`
- [ ] `src/Yaat.Client.Core/Services/ServerConnection.cs:648`
  (`GroundNodeDto`) → `LatLon Position`. Wire format changes.
- [ ] `tools/Yaat.LayoutInspector/QueryResults.cs:18` — record ctor →
  `LatLon Position`

### Record ctors & one-shot records (12 sites)

- [ ] `src/Yaat.Sim/Commands/ParsedCommand.cs:69` — `WarpCommand`
- [ ] `src/Yaat.Sim/Commands/ParsedCommand.cs:108` — `ResolvedFix(string Name, LatLon Position)`
- [ ] `src/Yaat.Sim/Commands/ParsedCommand.cs:184` — `DirectFixDeparture`
- [ ] `src/Yaat.Sim/Commands/ParsedCommand.cs:292` — `HoldAtFixOrbitCommand`
- [ ] `src/Yaat.Sim/Commands/ParsedCommand.cs:295` — `HoldAtFixHoverCommand`
- [ ] `src/Yaat.Sim/Commands/ParsedCommand.cs:375` — `AtFixCondition`
- [ ] `src/Yaat.Sim/Data/FrdResolver.cs:3` — `ResolvedPosition(double, double)`:
  this record is essentially a `LatLon` with a different name. **Delete the record,
  return `LatLon?` directly.**
- [ ] `src/Yaat.Sim/Data/Airport/GeoJsonParser.cs:489` — `ParkingFeature`
- [ ] `src/Yaat.Sim/Data/Airport/GeoJsonParser.cs:491` — `SpotFeature`
- [ ] `src/Yaat.Sim/Data/Airport/GeoJsonParser.cs:493` — `TaxiwayFeature` (list of coords)
- [ ] `src/Yaat.Sim/Data/Airport/GeoJsonParser.cs:495` — `RunwayFeature` (list of coords)
- [ ] `src/Yaat.Client/ViewModels/RadarViewModel.cs:1611` — `DrawnWaypoint`

### Function signatures taking `(double lat, double lon)` (40 sites)

Single-pair params — convert to `LatLon`:

- [ ] `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs`:
  - `FindNearestNode(LatLon)`
  - `FindNearestTaxiEdge(LatLon)`
  - `FindNearestCenterlineNode(LatLon, TrueHeading, string?)`
  - `FindNearestExit(LatLon, TrueHeading, string?, double)`
  - `FindExitByTaxiway(LatLon, string, double)`
- [ ] `src/Yaat.Sim/Data/Airport/CoordinateIndex.cs` — `Add`, `FindNearest`,
  `BucketKey` → all take `LatLon`
- [ ] `src/Yaat.Sim/Data/Airport/CubicBezier.cs:168` — `ClosestT(LatLon, int)`
- [ ] `src/Yaat.Sim/Data/Airport/RunwayCrossingDetector.cs:147` —
  `IsOnRunway(LatLon, in RunwayRectangle)`
- [ ] `src/Yaat.Sim/Data/FrdResolver.cs:77` — `ToFrd(LatLon, IReadOnlyList<Fix>, double)`
  (and change the fix-list tuple to a typed record — see below)
- [ ] `src/Yaat.Sim/MagneticDeclination.cs:42,51,61` — `GetDeclination(LatLon)`,
  `TrueToMagnetic(double, LatLon)`, `MagneticToTrue(double, LatLon)`

Paired-pair params — both pairs become `LatLon`:

- [ ] `src/Yaat.Sim/GeoMath.cs`:
  - `DistanceNm(LatLon, LatLon)`
  - `BearingTo(LatLon, LatLon)`
  - `ProjectPoint(LatLon, TrueHeading, double) → LatLon`
  - `ProjectPointRaw(LatLon, double, double) → LatLon`
  - `SignedCrossTrackDistanceNm(LatLon p, LatLon lineStart, TrueHeading heading)` —
    already has a 5-arg shape; regularize
- [ ] `src/Yaat.Sim/Scenarios/AircraftGenerator.cs:385` —
  `ComputeBearing(LatLon, LatLon)`

Test helpers:

- [ ] `tests/Yaat.Sim.Tests/` — 11 `MakeAircraft(..., double lat, double lon, ...)`
  / `MakeNode(..., double lat, double lon)` helpers, all take `LatLon`
- [ ] `tools/Yaat.LayoutInspector/Tick/HoldShortResolver.cs:55` —
  `NearestDistances(LatLon, ExitRef, RunwayReference)`
- [ ] `tools/Yaat.LayoutInspector/Tick/RunwayReference.cs:49` —
  `CrossTrackFt(LatLon)`

Client view / VM entry points:

- [ ] `src/Yaat.Client/ViewModels/GroundViewModel.cs:564` — `FindNearestNodeId(LatLon)`
- [ ] `src/Yaat.Client/ViewModels/RadarViewModel.cs:358,630,1334` —
  `SetPrimaryAirportPosition(LatLon)`, `PlaceRangeRing(LatLon)`,
  `PlaceRouteWaypoint(LatLon)`
- [ ] `src/Yaat.Client/Views/Map/MapViewport.cs:28,49` —
  `LatLonToScreen(LatLon) → (float, float)`,
  `ScreenToLatLon(float, float) → LatLon`
- [ ] `src/Yaat.Client/Views/Radar/RadarView.axaml.cs:476,486` —
  `OnRangeRingPlaced(LatLon)`, `OnRoutePointPlaced(LatLon)`
- [ ] `src/Yaat.Client/Views/Radar/RadarView.ContextMenus.cs:644` —
  `OnMapRightClicked(LatLon, Point)`

### Tuple usages `(double Lat, double Lon)` (30 sites)

Mostly in Yaat.Client — all become `LatLon`:

- [ ] `src/Yaat.Client/Services/LiveWeatherService.cs:253` —
  `GetArtccCenter(string) : LatLon`
- [ ] `src/Yaat.Client/Services/TowerCabMapParser.cs:18,24,163` —
  `Points : List<LatLon>`, `ParseCoordinateArray(JsonElement) : List<LatLon>`
- [ ] `src/Yaat.Client/ViewModels/GroundViewModel.cs:1402` —
  `intermediates : List<LatLon>`
- [ ] `src/Yaat.Client/ViewModels/RadarViewModel.cs:137,312,577` —
  `_fixes : IReadOnlyList<(string Name, LatLon Pos)>` → `IReadOnlyList<Fix>`
  (new record `Fix(string Name, LatLon Position)`)
- [ ] `src/Yaat.Client/Views/Ground/GroundRenderer.cs:766` —
  `nodeLatLon : Dictionary<int, LatLon>`
- [ ] `src/Yaat.Client/Views/Radar/RadarCanvas.cs:46,48,187,525,536,537,557,559,614` —
  `FixesProperty` + `DrawRouteOrigin`/`RubberBandTarget` carry `LatLon`
- [ ] `src/Yaat.Client/Views/Radar/RadarRenderer.cs:236,247,248,423,460` —
  same `LatLon` threading

### FrdResolver.Resolve return shape

- [ ] `src/Yaat.Sim/Data/FrdResolver.cs` — `Resolve(...)` currently returns
  `ResolvedPosition?`. That record is a `(double Lat, double Lon)`. Delete
  `ResolvedPosition`, return `LatLon?` directly. One call site in
  `ScenarioLoader.LoadAircraft` and handful in server — trivially updated.

### yaat-server cross-repo (35 sites)

yaat-server references `Yaat.Sim` as a project, so `LatLon` flows through. The
server touch sites are straightforward `ac.Position` reads:

- [ ] `src/Yaat.Server/Simulation/AircraftChangeTracker.cs` — 11 sites
  (`AsdexTargetFingerprint`, `TowerCabFingerprint` DTO conversions)
- [ ] `src/Yaat.Server/Simulation/CrcVisibilityTracker.cs` — 4 sites
  (`GeoMath.DistanceNm` calls against airport pos)
- [ ] `src/Yaat.Server/Simulation/DtoConverter.cs` — 9 sites (CRC `Point` construction
  — **stays raw `{Lat=, Lon=}`** since `Point` is VATSIM protocol)
- [ ] `src/Yaat.Server/Simulation/SayCommandHandler.cs` — 3 sites (position text
  formatting)
- [ ] `src/Yaat.Server/Simulation/SimulationHostedService.cs:140` —
  `PositionHistory.Add` tuple → `LatLon`
- [ ] `src/Yaat.Server/Simulation/TickProcessor.cs` — 2 sites
  (`GeoMath.DistanceNm` calls)
- [ ] `src/Yaat.Server/Simulation/TrackCommandHandler.cs:390-394` — `ghost.Latitude`
  / `ghost.Longitude` reads. Check whether `ghost` is a `Yaat.Sim` type or
  server-local — if server-local, keep the original shape.

### Cross-repo sequencing

See §Commit structure for the full 5-commit breakdown. Only commit 4 breaks the
yaat-server build, and the yaat-server sibling (commit 5) lands immediately
after.

### Tests

- [ ] 11 test-helper `MakeAircraft`/`MakeNode` functions take `LatLon` instead of
  `(double lat, double lon)`. Callers inside each test file trivially convert.
- [ ] Rebake any test recording/bundle that pin-tests JSON shape of
  `AircraftState` or `ControlTargets` (grep for hard-coded `"Latitude":` in
  `tests/Yaat.Sim.Tests/TestData/`). Those are zip/brotli bundles — decompress
  via `tools/bug_bundle.py`, update, rewrap.
- [ ] `RecordingArchiveTests.AircraftState_GroundLayoutAirportId_RoundTrips` —
  already relies on JSON serialization of `AircraftState`. Update the assertion
  if it names specific fields.

## Design: `LatLon` itself

```csharp
// src/Yaat.Sim/LatLon.cs
namespace Yaat.Sim;

/// <summary>
/// Geographic coordinate (latitude, longitude) in degrees. Value type —
/// no heap allocation. Equality, hashing, and deconstruction come from
/// record struct. Component names match the CRC Point DTO and the existing
/// tuple convention used across the codebase.
/// </summary>
/// <param name="Lat">Latitude in degrees. Positive north, negative south.</param>
/// <param name="Lon">Longitude in degrees. Positive east, negative west.</param>
public readonly record struct LatLon(double Lat, double Lon)
{
    /// <summary>Origin (0, 0). Not useful for real navigation — mostly a cache sentinel.</summary>
    public static readonly LatLon Zero = new(0.0, 0.0);

    public override string ToString() => $"({Lat:F6},{Lon:F6})";
}
```

- No helper methods on the struct itself. All math stays in `GeoMath`.
- `ToString` chosen to match the existing `$"({ac.Latitude:F6},{ac.Longitude:F6})"`
  format used in diagnostic output.
- **No implicit conversion from tuple** — we want the compiler to force explicit
  `new LatLon(lat, lon)` at every remaining boundary (e.g., parsing external JSON).
  Implicit tuple conversion lets argument-swap bugs survive.

## Commit structure

Five commits total: four on yaat, one sibling on yaat-server. Each commit is
small enough to review on its own and leaves the tree buildable — except the
brief window between commit 4 (yaat) and commit 5 (yaat-server), where
yaat-server will not build. Solo committer, unreleased software — acceptable.

### Commit 1 (yaat): add `LatLon` type

- New file `src/Yaat.Sim/LatLon.cs`. Zero call-site changes.
- Build green on both repos, all tests pass, wire format unchanged.

### Commit 2a (yaat): foundation — new API surface

- Add `Position` property alongside `Latitude`/`Longitude` on `AircraftState`,
  `ControlTargets`, and `GroundNode`. `Position` is a read-through
  (`new LatLon(Latitude, Longitude)`); writes flow back through the legacy
  scalar fields. Marked `[JsonIgnore]` so the wire format is unchanged.
- Add `DeclinationCachePosition : LatLon?` on `AircraftState`. The old
  `DeclinationCacheLat`/`Lon` NaN sentinels remain but go unused once the
  single writer/reader (`FlightPhysics`) migrates in 2b. Removed in commit 4.
- Add new `LatLon`-shaped overloads on `GeoMath` (`DistanceNm`, `BearingTo`,
  `ProjectPoint`, etc.) and `MagneticDeclination`. Old `(double, double, ...)`
  overloads stay side-by-side (NOT `[Obsolete]` — the zero-warnings policy
  would turn every un-migrated caller into an error while 2b is in flight).
- Refactor `FrdResolver`: delete `ResolvedPosition`, return `LatLon?` directly.
  Update its handful of callers in place (part of 2a because deleting the
  type forces these edits).
- Add overloads on `CoordinateIndex`, `CubicBezier.ClosestT`,
  `RunwayCrossingDetector.IsOnRunway`, `AirportGroundLayout` find methods.
- Zero changes to caller sites outside `FrdResolver` consumers.
- Build green, tests pass.

### Commit 2b (yaat): migrate Yaat.Sim internal callers

- Migrate ~70 files in `src/Yaat.Sim/` from `ac.Latitude`/`ac.Longitude` reads
  to `ac.Position`, and from 4-arg `GeoMath` calls to 2-arg `LatLon` shapes.
  Clusters: Commands, Phases, Data/Airport, Data/Vnas, Scenarios, Simulation,
  flight-physics/conflict detectors.
- `FlightPhysics` migrates `DeclinationCacheLat`/`Lon` read-and-write to
  `DeclinationCachePosition` (null now means "not cached" — no more NaN).
- Writes to `ac.Latitude`/`ac.Longitude` migrate to `ac.Position = new LatLon(...)`.
- DTOs and their callers stay untouched — wire format unchanged until commit 4.
- Build green on yaat. Yaat.Client and tests compile unchanged because old
  API still exists on the types. yaat-server still green.

### Commit 3 (yaat): migrate Yaat.Client, tools, tests

- Update `[ObservableProperty] private double _latitude` in
  `src/Yaat.Client/Models/AircraftModel.cs` → `private LatLon _position`.
- Migrate `RadarViewModel`, `GroundViewModel`, `MapViewport`, `RadarView`,
  `RadarCanvas`, `RadarRenderer`, `GroundRenderer` to `LatLon`.
- Convert `(double Lat, double Lon)` tuple usages in `LiveWeatherService`,
  `TowerCabMapParser`, etc. to `LatLon`.
- Convert tool helpers in `Yaat.LayoutInspector`.
- Convert test helpers (`MakeAircraft`, `MakeNode`) in `tests/Yaat.Sim.Tests/`.
- Build green on yaat. yaat-server still green.

### Commit 4 (yaat): remove old API, break wire format

- Remove `Latitude`/`Longitude` properties from `AircraftState`, `ControlTargets`,
  `GroundNode`, and DTOs.
- Remove `DeclinationCacheLat`/`Lon` from `AircraftState`.
- Remove the `(double, double, …)` overloads on `GeoMath`.
- DTO wire format changes here (`{"Position": {"Lat": …, "Lon": …}}` instead of
  `{"Latitude": …, "Longitude": …}`). Rebake any test recordings in
  `tests/*/TestData/` that assert on the old shape.
- Build green on yaat. **yaat-server build breaks here and stays broken until
  commit 5 lands.**

### Commit 5 (yaat-server, sibling to commit 4): migrate 35 sites

- Update all `ac.Latitude`/`ac.Longitude` reads to `ac.Position.Lat`/`.Lon`.
- Update DTO conversions in `DtoConverter` — CRC `Point` stays `{Lat=, Lon=}`
  (VATSIM protocol), only the source property changes.
- Update `PositionHistory` tuple → `LatLon`.
- Check `TrackCommandHandler.cs:390-394` — if `ghost` is a Yaat.Sim type,
  migrate; if server-local, leave alone.
- Full yaat-server test suite passes.
- Commit message references the yaat commit 4 URL.

## Validation

- [ ] `dotnet build -p:TreatWarningsAsErrors=true` clean on both yaat and yaat-server.
- [ ] `dotnet test tests/Yaat.Sim.Tests` — 3172 tests pass, <30 s runtime.
- [ ] `dotnet test tests/Yaat.Client.Tests` and `tests/Yaat.Client.UI.Tests` pass.
- [ ] yaat-server's test suite passes.
- [ ] **Recording round-trip**: run one recording through `engine.Replay()` before
  and after — aircraft trajectories should be byte-identical (LatLon has the same
  `double Lat, double Lon` representation as the old two-field shape).
- [ ] Client launch: open an airport, place a range ring, draw a route — all the
  `LatLon`-touched interaction surfaces work.
- [ ] `grep -rn "\.Latitude\b" src/ tests/ tools/` returns only yaat-server-boundary
  DTOs (CRC `Point` construction) and nothing else.

## Risks & gotchas

- **DTO wire format churn**: `StateSnapshotDto`, `AircraftSnapshotDto`,
  `ControlTargetsDto`, `GroundNodeDto` all change shape on the wire. Any in-flight
  scenario recording not regenerated will fail to deserialize. Acceptable cost
  per the "unreleased software" CLAUDE.md rule.
- **Scenario JSON parsing**: `Coordinates: { lat, lon }` comes from external
  vNAS data-api — parsed by a model type that currently reads
  `public double Lat` / `public double Lon`. If we swap that model field to
  `LatLon Position` with `[JsonPropertyName("lat"/"lon")]`, System.Text.Json
  can't deserialize a flat object into a nested struct automatically. Options:
  (a) keep the parse model as two `double` fields and construct `LatLon` after
  parse; (b) custom `JsonConverter<LatLon>` that flattens. **(a) is cleaner**
  — the model type is a one-shot parse result, the public surface above it uses
  `LatLon`.
- **CRC protocol `Point`**: the VATSIM struct is serialized by MessagePack with
  a fixed layout. Must not touch. Conversion to/from `LatLon` happens at the
  `DtoConverter` boundary in yaat-server.
- **XAML bindings**: one code-behind hit (`GroundView.axaml.cs:730`) — plain C#,
  not a binding expression. No `.axaml` file references `Latitude`/`Longitude`
  directly, so XAML bindings don't need updating.
- **Source-generated ObservableProperty**: `src/Yaat.Client/Models/AircraftModel.cs`
  has `[ObservableProperty] private double _latitude` (etc.). The generator
  emits `Latitude { get; set; }` — update the backing field to
  `private LatLon _position` and the generator will emit `Position`.

## Cleanup

- [ ] Delete this plan file after the commit lands.
- [ ] Save a memory at `C:\Users\Leftos\.claude\projects\X--dev-yaat\memory\`
  if there's a lasting rule worth remembering (e.g., "CRC `Point` stays
  serialized, `LatLon` is the internal type"). If nothing surprising is
  learned, no memory needed.
