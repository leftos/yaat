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

- Recording files go in `tests/Yaat.Sim.Tests/TestData/` with a descriptive name (e.g., `sfo-crossing-runways-recording.json`)
- Silently skip (early return) if `NavData.dat`, the GeoJSON, or the recording file is absent — keeps CI green on machines without navdata
- You can replay to an intermediate time (not just `TotalElapsedSeconds`) to capture state mid-scenario
- Preset commands in the scenario JSON fire at their `timeOffset` during replay
- `engine.FindAircraft(callsign)` returns the live `AircraftState` after replay; inspect `.Phases.AssignedRunway`, `.AssignedTaxiRoute.Segments`, `.Heading`, `.Latitude`, etc.

### Available Recordings

| File | Scenario | Airport | Good for testing |
|------|----------|---------|-----------------|
| `oak-taxi-recording.json` | OAK taxi session | OAK | Taxi routing, variant inference |
| `sfo-crossing-runways-recording.json` | S2-SFO-1 | SFO | CTO runway resolution (#51), AAL2839 taxi detour (#53) |
| `sfo-issue53-yhbm1-recording.json` | S2-SFO-1 | SFO | SWA7348 TAXI Y H B M1 HS 01L wrong direction (#53) |
| `sfo-issue53-n346g-recording.json` | S1-SFO-2 | SFO | N346G TAXI T41W C E HS 10L, AMX669 TAXI M2 B M1 HS 01L (#53) |

### How to Add a New Recording

1. Reproduce the issue in YAAT client
2. Save a recording via the session recording feature
3. Copy the `.yaat-recording.json` to `tests/Yaat.Sim.Tests/TestData/` with a short, descriptive name
4. Write a replay test that fails with the bug and passes after the fix

### Key OAK Reference Points

| Name | Type | Location | Notes |
|------|------|----------|-------|
| NEW7 | Parking | North of D | Good for RAMP→D transition tests |
| OLD8 | Parking | North of D | Alternative north-of-D parking |
| Taxiway D | Taxiway | lat 37.728–37.740 | East-west, north side of airport |
| Taxiway W | Taxiway | — | Has variants W1-W7 connecting to runway 30 |
| Runway 30 | Runway | — | Primary destination for south-flow departures |
| Runway 15/33 | Runway | — | Crossed by taxiway D-F route |
