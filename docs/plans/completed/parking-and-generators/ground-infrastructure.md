# Phase 1: Ground Data Infrastructure

Shared foundation for parking, ground ops, and generators.

## ScenarioModels.cs — StartingConditions

**File:** `X:\dev\yaat-server\src\Yaat.Server\Scenarios\ScenarioModels.cs`

Add two properties to `StartingConditions`:
```csharp
[JsonPropertyName("heading")]
public double? Heading { get; set; }

[JsonPropertyName("parking")]
public string? Parking { get; set; }
```

Both are currently silently dropped by JSON deserialization. Real scenarios use `"heading": 133` and `"parking": "29"`.

## ScenarioModels.cs — AircraftGenerator

Same file. Replace the stub `AircraftGenerator` (lines 188-192, currently only has `Id`) with the full model:

| Field | Type | Default | Notes |
|-------|------|---------|-------|
| `id` | `string` | `""` | existing |
| `runway` | `string` | `""` | which runway to generate arrivals for |
| `engineType` | `string` | `"Jet"` | Jet, Piston, etc. |
| `weightCategory` | `string` | `"Large"` | Large, Small, Heavy, SmallPlus |
| `initialDistance` | `double` | `10` | NM from threshold for first spawn |
| `maxDistance` | `double` | `50` | max spawn distance before wrapping |
| `intervalDistance` | `double` | `5` | NM spacing between spawns |
| `startTimeOffset` | `int` | `0` | seconds after scenario start |
| `maxTime` | `int` | `3600` | stop generating after this many seconds |
| `intervalTime` | `int` | `300` | seconds between spawns |
| `randomizeInterval` | `bool` | `false` | +-25% jitter on timing |
| `randomizeWeightCategory` | `bool` | `false` | vary weight classes |
| `autoTrackConfiguration` | `AutoTrackConditions?` | `null` | reuses existing class (line 173) |

## YaatOptions.cs

**File:** `X:\dev\yaat-server\src\Yaat.Server\YaatOptions.cs`

```csharp
public string? AirportFilesPath { get; set; }
```

Optional config. No `ValidateOnStart` — missing path produces warnings at use sites and defers gracefully.

## AirportGroundDataService.cs (new)

**New file:** `X:\dev\yaat-server\src\Yaat.Server\Data\AirportGroundDataService.cs`

Implements `IAirportGroundData` from Yaat.Sim. Constructor takes `IOptions<YaatOptions>` + `ILogger`.

Behavior:
- Returns `null` from `GetLayout()` if `AirportFilesPath` not configured
- Airport ID normalization: strip `K` prefix for 4-char ICAO, lowercase → file name (`KOAK` → `oak`)
- Check for monolithic file first (`oak.geojson`) → `GeoJsonParser.Parse`
- Then check for split directory (`oak/`) → `GeoJsonParser.ParseMultiple`
- `ConcurrentDictionary<string, AirportGroundLayout?>` cache — null entries cached to avoid re-hitting filesystem
- Log info on first load of each airport; log warning on missing files

## Program.cs — DI

**File:** `X:\dev\yaat-server\src\Yaat.Server\Program.cs`

After `IRunwayLookup` registration:
```csharp
builder.Services.AddSingleton<IAirportGroundData, AirportGroundDataService>();
```

## SimulationHostedService.cs — Constructor

**File:** `X:\dev\yaat-server\src\Yaat.Server\Simulation\SimulationHostedService.cs`

Add `IAirportGroundData` to primary constructor parameters.
