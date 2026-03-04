# E2E Testing with Real Airport Layouts

## Approach

Prefer real airport GeoJSON layouts over synthetic test layouts for ground operation E2E tests. Real layouts catch edge cases that synthetic graphs miss (unexpected node connectivity, ramp topology, taxiway naming).

## Data Sources

### Airport GeoJSON

Combined GeoJSON files live in yaat-server: `X:\dev\yaat-server\ArtccResources\ZOA\airports\`

These are the same files the server loads at runtime. Each is a single combined GeoJSON with all features (taxiways, parking, runways, spots, helipads).

The per-feature split files in `X:\dev\vzoa\training-files\atctrainer-airport-files\` are for editing in QGIS — don't use them in tests.

### Loading in Tests

```csharp
// In TaxiPathfinderTests.cs
private const string ArtccResourcesDir = @"X:\dev\yaat-server\ArtccResources\ZOA\airports";

private static AirportGroundLayout? LoadAirportLayout(string airportId, string subdir)
{
    string combined = Path.Combine(ArtccResourcesDir, $"{subdir}.geojson");
    if (File.Exists(combined))
        return GeoJsonParser.Parse(airportId, File.ReadAllText(combined));
    return null;
}
```

Tests return early if the file is missing, so they pass even without the yaat-server repo cloned.

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
| SFO | `sfo` | Complex hold-short patterns, multiple runways |

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

### Key OAK Reference Points

| Name | Type | Location | Notes |
|------|------|----------|-------|
| NEW7 | Parking | North of D | Good for RAMP→D transition tests |
| OLD8 | Parking | North of D | Alternative north-of-D parking |
| Taxiway D | Taxiway | lat 37.728–37.740 | East-west, north side of airport |
| Taxiway W | Taxiway | — | Has variants W1-W7 connecting to runway 30 |
| Runway 30 | Runway | — | Primary destination for south-flow departures |
| Runway 15/33 | Runway | — | Crossed by taxiway D-F route |
