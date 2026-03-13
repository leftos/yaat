# E2E Testing with Real Airport Layouts

## Approach

Prefer real airport GeoJSON layouts over synthetic test layouts for ground operation E2E tests. Real layouts catch edge cases that synthetic graphs miss (unexpected node connectivity, ramp topology, taxiway naming).

## Data Sources

### Airport GeoJSON

Test GeoJSON files live in `tests/Yaat.Sim.Tests/TestData/` (e.g., `oak.geojson`).

To add a new airport, download from the vNAS training API:

```
https://data-api.vnas.vatsim.net/api/training/airports/{FAA_ID}/map
```

Example: `curl -o tests/Yaat.Sim.Tests/TestData/mia.geojson https://data-api.vnas.vatsim.net/api/training/airports/MIA/map`

### Loading in Tests

GeoJSON files are checked into `tests/Yaat.Sim.Tests/TestData/`:

```csharp
// In AirportE2ETests.cs / TaxiPathfinderTests.cs
private const string TestDataDir = "TestData";

private static AirportGroundLayout? LoadLayout(string airportId, string subdir)
{
    string path = Path.Combine(TestDataDir, $"{subdir}.geojson");
    if (File.Exists(path))
        return GeoJsonParser.Parse(airportId, File.ReadAllText(path));
    return null;
}
```

### Finding Nodes

```csharp
// Find parking by name
var parking = layout.Nodes.Values.FirstOrDefault(n =>
    n.Type == GroundNodeType.Parking && n.Name == "NEW7");

// Find nodes on a taxiway
var dEdges = layout.Edges.Where(e =>
    string.Equals(e.TaxiwayName, "D", StringComparison.OrdinalIgnoreCase));

// Find intersection of two taxiways
var junction = FindIntersectionNode(layout, "T", "D"); // helper in test class
```

## Available Airports

| Airport | Subdir | Good for |
|---------|--------|----------|
| OAK | `oak` | Taxiway variants (W/W1-W7), parking ramp transitions, runway crossings (15/33) |
| SFO | `sfo` | Complex hold-short patterns, multiple runways, dual parallel pairs (01/19, 28/10) |

## Test Patterns

### Route Resolution

Test through `TaxiPathfinder.ResolveExplicitPath` — same entry point as `GroundCommandHandler.TryTaxi`:

```csharp
var route = TaxiPathfinder.ResolveExplicitPath(
    layout,
    fromNodeId: parkingNode.Id,
    taxiwayNames: ["D"],
    out string? failReason,
    explicitHoldShorts: ["D"],
    destinationRunway: "30",   // optional
    runways: runwayLookup,     // optional, needed for variant inference
    airportId: "OAK"           // optional, needed for variant inference
);
```

### What to Assert

- Route is non-null and failReason is null
- Expected taxiway names appear in segments
- Hold-short points exist with correct reason and target name
- Hold-short position relative to taxiway transitions (e.g., before first D segment)
- Route summary (`route.ToSummary()`) matches expected format
- Route extends past hold-short (aircraft continues when cleared)

### Existing OAK Tests

In `TaxiPathfinderTests.cs`:
- `OAK_LayoutLoads_HasWVariants` — layout smoke test
- `OAK_WalkW3_Succeeds` — single taxiway walk
- `OAK_TaxiW3W_ToRunway30_InfersWVariant` — variant inference
- `OAK_TaxiBW_ToRunway30_E2E` — multi-taxiway route to runway
- `OAK_TaxiDF_CrossesRunway15_33` — runway crossing detection
- `OAK_TaxiDHoldShortD_FromParking_*` — taxiway hold-short from parking

## Recording-Based Replay Tests

In addition to direct pathfinder unit tests, you can replay saved scenario recordings through `SimulationEngine.Replay()` to reproduce exact user sessions and assert on resulting aircraft state.

### Recording Format

Recordings are `SessionRecording` JSON files containing `ScenarioJson`, `RngSeed`, `WeatherJson`, `Actions` (list of user commands and setting changes with elapsed-second timestamps), and `TotalElapsedSeconds`.

### Loading a Recording

```csharp
// In SimulationEngineReplayTests.cs or SfoReplayTests.cs
var json = File.ReadAllText("TestData/my-recording.json");
var recording = JsonSerializer.Deserialize<SessionRecording>(json,
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

var engine = new SimulationEngine(fixes, fixes, groundData);
engine.Replay(recording, targetSeconds); // replay up to targetSeconds
var aircraft = engine.FindAircraft("UAL859");
```

### Replay Tips

- Recording files go in `tests/Yaat.Sim.Tests/TestData/` with a descriptive name (e.g., `oak-taxi-recording.json`)
- Silently skip (early return) if `NavData.dat`, the GeoJSON, or the recording file is absent — keeps CI green on machines without navdata
- You can replay to an intermediate time (not just `TotalElapsedSeconds`) to capture state mid-scenario
- Preset commands in the scenario JSON fire at their `timeOffset` during replay
- `engine.FindAircraft(callsign)` returns the live `AircraftState` after replay; inspect `.Phases.AssignedRunway`, `.AssignedTaxiRoute.Segments`, `.Heading`, `.Latitude`, etc.
- **Initialize SimLog in tests** — wire `SimLog.Initialize(loggerFactory)` in your engine builder so Yaat.Sim logs route to xunit output. Use `MartinCostello.Logging.XUnit`.

### Pitfall: Recordings with WAIT Presets

Scenario aircraft often have preset commands like `WAIT 30 TAXI M2 $2`. These create `DeferredDispatch` entries that fire after the WAIT timer expires. If WAIT/deferred dispatch behavior changes, **all recordings made before the fix will have wrong expectations** — the aircraft state at any given time `t` depends on the exact dispatch pipeline.

When writing replay tests for aircraft with WAIT presets:
- Check the scenario's `presetCommands` for the aircraft under test (key: `aircraftId` in the `ScenarioJson`)
- If the aircraft has WAIT presets, the test is sensitive to WAIT dispatch timing
- Prefer testing aircraft without WAIT presets, or re-record after WAIT behavior stabilizes
- `DispatchCompound` clears both `Queue.Blocks` and `DeferredDispatches` — a later command fully replaces earlier state

### Available Recordings

| File | Scenario | Airport | Good for testing |
|------|----------|---------|-----------------|
| `oak-taxi-recording.json` | OAK taxi session | OAK | Taxi routing, variant inference |
| `issue58-jstar-intermediate-fix-recording.json` | S3-NCTC-3 Area C Complete | OAK | JSTAR intermediate fix joining (JARR EMZOH4 SKIZM) |
| `issue58-star-180-recording.json` | S3-NCTC-2 Area C Sequencing | OAK | NavigationPath STAR expansion (pre-assigned STARs) |

### How to Add a New Recording

1. Reproduce the issue in YAAT client
2. Save a recording via the session recording feature
3. Copy the `.yaat-recording.json` to `tests/Yaat.Sim.Tests/TestData/` with a short, descriptive name. If the recording is for a GitHub issue, include the issue number in the filename (e.g., `sfo-issue53-taxi-overshoot-recording.json`) so the issue thread is easy to find later.
4. Write a replay test that fails with the bug and passes after the fix

### Per-Tick Observation Pattern

For debugging navigation, physics, or phase bugs, replay to a point and then tick manually while logging aircraft state each second:

```csharp
engine.Replay(recording, targetTime);
var aircraft = engine.FindAircraft("CALLSIGN");

for (int t = 1; t <= 30; t++)
{
    engine.TickOneSecond();
    aircraft = engine.FindAircraft("CALLSIGN");
    var nextFix = aircraft.Targets.NavigationRoute.Count > 0
        ? aircraft.Targets.NavigationRoute[0].Name : "(none)";
    output.WriteLine($"t={t} hdg={aircraft.Heading:F1} "
        + $"lat={aircraft.Latitude:F4} lon={aircraft.Longitude:F4} "
        + $"alt={aircraft.Altitude:F0} next={nextFix}");
}
```

This pattern lets you "see" heading reversals, altitude oscillations, fix sequencing, and other bugs in the test output. See `Issue58JstarIntermediateFixTests` for a complete example.

### Key OAK Reference Points

| Name | Type | Location | Notes |
|------|------|----------|-------|
| NEW7 | Parking | North of D | Good for RAMP→D transition tests |
| OLD8 | Parking | North of D | Alternative north-of-D parking |
| Taxiway D | Taxiway | lat 37.728–37.740 | East-west, north side of airport |
| Taxiway W | Taxiway | — | Has variants W1-W7 connecting to runway 30 |
| Runway 30 | Runway | — | Primary destination for south-flow departures |
| Runway 15/33 | Runway | — | Crossed by taxiway D-F route |
