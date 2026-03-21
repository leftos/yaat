# Test-Driven Debugging with Recordings

## The Core Idea

Users report bugs by attaching a `.yaat-recording.json` to a GitHub issue. That recording is a complete snapshot: the scenario JSON, RNG seed, weather, and every action the user took — timestamped to the second. You can replay it deterministically in a test, freeze time at any point, and inspect every field on every aircraft.

The workflow is always TDD:

1. **Add the recording** to `tests/Yaat.Sim.Tests/TestData/`
2. **Write a failing test** that replays to the right moment and asserts the correct behavior
3. **Confirm the test fails** against the current code (proving it catches the bug)
4. **Fix the bug**
5. **Confirm the test passes**

This has been the workflow for every recording-based issue fix in the project (#58, #62, #67, #70, #72, #74, #75). The test is the proof that the bug existed and that the fix works.

## What's in a Recording

A `SessionRecording` contains everything needed to reproduce a session from scratch:

| Field | Purpose |
|-------|---------|
| `ScenarioJson` | Full scenario JSON (aircraft, presets, weather, positions) |
| `RngSeed` | Deterministic random seed — same seed = same spawns, same behavior |
| `WeatherJson` | Weather profile active at recording time (wind, altimeter) |
| `Actions` | Every user action with `ElapsedSeconds` timestamp |
| `TotalElapsedSeconds` | Session duration |

Actions include commands (`RecordedCommand`), spawns, deletes, warps, flight plan amendments, weather changes, and setting changes. Replay applies them in timestamp order, ticking physics 4x/second between them.

### v2 Recordings (with State Snapshots)

v2 recordings add complete simulation state snapshots captured every second. This allows:

1. **Exact state restore** — load the snapshot at time T and get the precise state the user saw, regardless of code changes since the recording
2. **Hybrid replay** — restore snapshot at time T, then replay commands from T onward with current (fixed) code to test whether a fix works
3. **Faster rewind** — instead of replaying from t=0, restore the nearest snapshot and replay only the remaining seconds

| Field | Purpose |
|-------|---------|
| `Version` | `1` = commands only (original format), `2` = commands + snapshots |
| `Snapshots` | `List<TimedSnapshot>` — one per second, each containing full `StateSnapshotDto` |

Each `StateSnapshotDto` captures: all aircraft (position, physics, control targets, command queue, phases, track ownership, scratchpads, procedures), scenario state (queues, generators, settings, coordination), and RNG state. Snapshots are versioned via `SchemaVersion` with a migration chain (`SnapshotSchemaMigrator`) — old snapshots are upgraded on load, and breaking changes throw `SnapshotSchemaException` (fallback to command replay).

**File format:** v2 recordings are Brotli-compressed JSON (`.yaat-recording.br`). `RecordingCompression.Decompress()` auto-detects the format (Brotli, gzip, or plain JSON) and decompresses transparently. v1 `.yaat-recording.json` files still load without issue. Snapshots are captured every 5 seconds.

**Generating snapshots:** Snapshots are generated at export time, not during runtime. The server replays the recording through a temporary isolated room with the full server pipeline (including track commands, coordination, etc.) and captures a snapshot every second. Zero runtime memory overhead.

**Migration:** The `MigrateRecording` SignalR endpoint accepts a v1 JSON string and returns v2 gzip bytes with snapshots generated via replay.

## Bug Report Bundles

A `.yaat-bug-report-bundle.zip` packages a recording with client and server logs into a single file. Created via **Scenario > Save Bug Report Bundle...** in the client.

**Contents:**
| Entry | Description |
|-------|-------------|
| `recording.yaat-recording.br` | The session recording (Brotli-compressed v2 format) |
| `yaat-client.log` | Client log at the time of the report |
| `yaat-server.log` | Server log (only included when connected to a local server) |

**Using bundles in tests:** Extract the recording from the zip and place it in TestData as a standalone file. `RecordingLoader.Load()` in `tests/Yaat.Sim.Tests/Helpers/RecordingLoader.cs` handles `.br`, `.json.gz`, `.json`, and `.zip` formats transparently.

## Step-by-Step: From Issue to Test

### 1. Get the recording into TestData

Download the recording from the issue. **If it's a v1 `.yaat-recording.json` file, upgrade it to v2 before investigating:**

```bash
cd yaat-server
dotnet run --project tools/Yaat.RecordingUpgrader -- ../yaat/tests/Yaat.Sim.Tests/TestData/issue123-some-bug-recording.json
```

This generates a `.br` file with state snapshots alongside the original. Place both in TestData:

```
tests/Yaat.Sim.Tests/TestData/issue77-alwys-descent-recording.br
```

If the attachment is a `.yaat-bug-report-bundle.zip`, extract the recording from it (it may be `.br`, `.json.gz`, or `.json`). Upgrade to v2 if needed, then rename to the convention below. The zip also contains logs which may help diagnose the issue but don't need to go into TestData.

Convention: `issue{N}-{short-description}-recording.br` (v2) or `.json` (v1). Including the issue number makes it easy to trace back to the GitHub thread.

### 2. Understand the bug from the issue

Read the user's description. Key things to extract:
- **Which aircraft** is misbehaving (callsign)
- **What it does wrong** (turns the wrong way, stays too high, ignores a command)
- **What it should do** (follow the STAR, descend to a crossing restriction, turn toward a fix)

### 3. Create the test class

Every issue gets its own test file in `tests/Yaat.Sim.Tests/Simulation/`:

```csharp
namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #77: Aircraft on ALWYS STAR remain too high,
/// not crossing BERKS at 5000.
///
/// Recording: S3-NCTB-6 (A) SFO19 — UAL238 on ALWYS3 STAR with
/// onAltitudeProfile=true, should descend to meet crossing restrictions.
/// </summary>
public class Issue77AlwysDescentTests(ITestOutputHelper output)
{
    private const string RecordingPath =
        "TestData/issue77-alwys-descent-recording.json";
```

The class doc comment should summarize: what issue, what recording, what aircraft, what's wrong. Someone reading the test 6 months from now should understand the bug without opening the GitHub issue.

### 4. Add the boilerplate

Every replay test needs two helpers — recording loader and engine builder:

```csharp
private static SessionRecording? LoadRecording()
{
    if (!File.Exists(RecordingPath))
    {
        return null;
    }

    var bytes = File.ReadAllBytes(RecordingPath);
    string json;

    // Detect gzip (magic bytes 0x1F 0x8B) for v2 .json.gz recordings
    if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
    {
        using var ms = new MemoryStream(bytes);
        using var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        json = reader.ReadToEnd();
    }
    else
    {
        json = System.Text.Encoding.UTF8.GetString(bytes);
    }

    return JsonSerializer.Deserialize<SessionRecording>(
        json,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
}

private SimulationEngine? BuildEngine()
{
    var navDb = TestVnasData.NavigationDb;
    if (navDb is null)
    {
        return null;
    }

    var groundData = new TestAirportGroundData();
    var loggerFactory = LoggerFactory.Create(builder =>
        builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
    SimLog.Initialize(loggerFactory);

    NavigationDatabase.SetInstance(navDb);
    return new SimulationEngine(groundData);
}
```

Both return `null` when data files are missing (NavData.dat, FAACIFP18.gz, GeoJSON). Tests that get `null` silently skip — CI stays green on machines without test data.

### 5. Find the right replay time

This is the investigative part. You need to figure out **when** in the recording the bug manifests. Use a diagnostic test that logs aircraft state over time:

```csharp
[Fact]
public void Diagnostic_LogDescentProfile()
{
    var recording = LoadRecording();
    var engine = BuildEngine();
    if (recording is null || engine is null) return;

    engine.Replay(recording, 200); // start after aircraft spawns

    var aircraft = engine.FindAircraft("UAL238");
    Assert.NotNull(aircraft);

    for (int t = 1; t <= 300; t++)
    {
        engine.TickOneSecond();
        aircraft = engine.FindAircraft("UAL238");
        if (aircraft is null) break;

        if (t % 10 == 0)
        {
            var nextFix = aircraft.Targets.NavigationRoute.Count > 0
                ? aircraft.Targets.NavigationRoute[0].Name : "(none)";
            output.WriteLine(
                $"t={t} alt={aircraft.Altitude:F0} "
                + $"tgtAlt={aircraft.Targets.TargetAltitude} "
                + $"vs={aircraft.VerticalSpeed:F0} "
                + $"next={nextFix}");
        }
    }
}
```

Run this, read the output. You'll see exactly where things go wrong — the heading reversal, the altitude plateau, the missing fix. This tells you what to assert and at what time.

**What to log depends on the bug type:**

| Bug type | Log these fields |
|----------|-----------------|
| Wrong heading / 180 reversal | `Heading`, `Targets.NavigationRoute[0].Name`, lat/lon |
| Stays too high / wrong descent | `Altitude`, `VerticalSpeed`, `Targets.TargetAltitude`, next fix |
| Doesn't follow route | `Targets.NavigationRoute` (all fixes), `Heading`, bearing to next fix |
| Wrong approach / transition | `Phases.ActiveApproach`, `Phases.Phases` list, nav targets |
| Ground taxi overshoot | `AssignedTaxiRoute.Segments`, lat/lon, current segment index |

### 6. Write the real assertion

Now write a test that replays to the right time and asserts the correct behavior. This test should **fail** against the current code:

```csharp
[Fact]
public void UAL238_CrossesBerksBelow5000()
{
    var recording = LoadRecording();
    var engine = BuildEngine();
    if (recording is null || engine is null) return;

    engine.Replay(recording, 182);

    var aircraft = engine.FindAircraft("UAL238");
    Assert.NotNull(aircraft);

    // Tick until aircraft passes BERKS or 600 seconds elapse
    for (int t = 1; t <= 600; t++)
    {
        engine.TickOneSecond();
        aircraft = engine.FindAircraft("UAL238");
        Assert.NotNull(aircraft);

        // Check if BERKS just got sequenced (was first, now isn't)
        var route = aircraft.Targets.NavigationRoute;
        bool pastBerks = route.All(f => f.Name != "BERKS")
            && aircraft.Altitude > 0; // still alive

        if (pastBerks && t > 60)
        {
            Assert.True(
                aircraft.Altitude <= 5500,
                $"Aircraft should cross BERKS at/below 5000 but was at "
                + $"{aircraft.Altitude:F0}");
            return;
        }
    }

    Assert.Fail("Aircraft never passed BERKS in 600 seconds");
}
```

### 7. Confirm failure, fix, confirm pass

Run the test. It should fail with a clear message like "Aircraft should cross BERKS at/below 5000 but was at 26000". Now fix the bug. Run the test again — it should pass.

## Techniques for Different Bug Types

### Navigation: wrong heading, 180 reversal, missed fix

Replay to just before the aircraft reaches the problematic fix. Tick forward manually and assert no large heading change:

```csharp
// From Issue58JstarIntermediateFixTests
double prevHeading = aircraft.Heading;
for (int t = 1; t <= 30; t++)
{
    engine.TickOneSecond();
    var ac = engine.FindAircraft(callsign);
    double delta = NormalizeAngleDiff(ac.Heading - prevHeading);
    prevHeading = ac.Heading;
}
double totalChange = NormalizeAngleDiff(aircraft.Heading - initialHeading);
Assert.True(totalChange < 120, "Likely a 180 reversal");
```

Also check that the first nav fix is ahead of the aircraft, not behind:

```csharp
double bearing = GeoMath.BearingTo(ac.Latitude, ac.Longitude,
    route[0].Latitude, route[0].Longitude);
double offNose = NormalizeAngleDiff(bearing - ac.Heading);
Assert.True(offNose < 90, $"First fix is {offNose:F0} off nose — behind aircraft");
```

**Examples:** Issue #58 (JSTAR intermediate fix, backwards STAR route), Issue #70 (fix-to-fix routing ignored)

### Approach: wrong transition, wrong clearance

Replay to when the aircraft is on the STAR and the approach hasn't been issued yet. Then call the approach resolution logic directly and assert the result:

```csharp
// From Issue74CappWrongTransitionTests
engine.Replay(recording, 400);
var aircraft = engine.FindAircraft("UAL238");

var resolved = ApproachCommandHandler.ResolveApproach(null, null, aircraft);
var procedure = resolved.Procedure!;
var selected = ApproachCommandHandler.SelectBestTransition(procedure, aircraft);

// BERKS is a common leg, not a transition — must not match CCR
Assert.Null(selected);
```

For full CAPP E2E, send the command through the engine and inspect the resulting phases:

```csharp
engine.Replay(recording, 688);
var result = engine.SendCommand("UAL238", "CAPP");
Assert.True(result.Success);
Assert.Equal("19L", aircraft.Phases.ActiveApproach.RunwayId);

// Verify no wrong-transition fixes in nav targets
var navTargets = aircraft.Targets.NavigationRoute.Select(n => n.Name);
Assert.DoesNotContain("CCR", navTargets);
```

**Examples:** Issue #74 (wrong transition via BERKS), Issue #75 (CAPP heading intercept)

### Descent: stays too high, misses constraints

Replay to spawn time, then tick forward and log the full descent profile. Assert the aircraft is below expected altitudes at key points:

```csharp
// From Issue72DescentProfileTests
engine.Replay(recording, 182);
var aircraft = engine.FindAircraft("UAL238");

for (int t = 1; t <= 300; t++)
{
    engine.TickOneSecond();
    aircraft = engine.FindAircraft("UAL238");
}

Assert.True(aircraft.Altitude < 20000,
    $"Should be below FL200 after 5min descent, was {aircraft.Altitude:F0}");
```

Also verify the route has altitude constraints loaded (to catch constraint-loading bugs separately from descent-planning bugs):

```csharp
int constraintCount = route.Count(t => t.AltitudeRestriction is not null);
Assert.True(constraintCount >= 3, "Route missing altitude constraints");
```

**Examples:** Issue #62 (DVIA not working), Issue #67 (procedure version), Issue #72 (descent profile)

### Ground: taxi overshoot, wrong path

For ground bugs, the approach is the same — replay and inspect the taxi route and aircraft position:

```csharp
engine.Replay(recording, recording.TotalElapsedSeconds);
var aircraft = engine.FindAircraft("AAL2839");
Assert.NotNull(aircraft.AssignedTaxiRoute);

// Verify aircraft didn't overshoot into the ramp
var segments = aircraft.AssignedTaxiRoute.Segments;
Assert.DoesNotContain(segments, s => s.TaxiwayName == "M2");
```

**Examples:** Issue #53 (taxi overshoot into ramp), Issue #54 (wrong hold-short position)

## Testing Without a Recording

Not every bug needs a recording. For issues with command parsing, procedure resolution, or pathfinding, you can test the logic directly using real data.

### CIFP procedure queries

```csharp
TestVnasData.EnsureInitialized();
var navDb = TestVnasData.NavigationDb;
if (navDb is null) return;

var procedure = navDb.GetApproach("KSFO", "I19L");
var star = navDb.GetStar("KSFO", "ALWYS3");
var fixPos = navDb.GetFixPosition("BERKS");
```

### Ground pathfinding

```csharp
var layout = new TestAirportGroundData().GetLayout("OAK");
if (layout is null) return;

var route = TaxiPathfinder.ResolveExplicitPath(
    layout,
    fromNodeId: parkingNode.Id,
    taxiwayNames: ["D", "C", "B", "W"],
    out string? failReason,
    destinationRunway: "30",
    airportId: "OAK");

Assert.NotNull(route);
Assert.Null(failReason);
```

## Rules

- **Real data only.** Never fabricate navdata, approach procedures, fixes, or STARs. Use the real NavData.dat and FAACIFP18.gz from TestData. Devs can't verify synthetic data against real charts.
- **Silent skip on missing data.** Return early if recording, NavData, or GeoJSON is absent. No `Assert.Skip`, no exceptions — just `return`. CI stays green.
- **One test class per issue.** File name: `Issue{N}{ShortDescription}Tests.cs`. Class doc comment explains the bug, recording, and aircraft.
- **Wire SimLog to xunit output.** Always initialize `SimLog` with `AddXUnit(output)` so Yaat.Sim's internal logs appear in test results. This is invaluable when diagnosing why a test fails.
- **Watch out for WAIT presets.** Aircraft with `WAIT` preset commands are sensitive to dispatch timing. If WAIT behavior changes, recordings made before the change will produce different state at any given time `t`. Prefer testing aircraft without WAIT presets, or re-record after the fix.

## Test Data

All test data lives in `tests/Yaat.Sim.Tests/TestData/`.

### Shared data files

| File | Purpose |
|------|---------|
| `NavData.dat` | Real navigation database (protobuf) |
| `FAACIFP18.gz` | Real CIFP procedures (SID/STAR/approach) |
| `AircraftSpecs.json` | Aircraft type → category mapping |
| `AircraftCwt.json` | Type code → wake turbulence category |
| `FaaAcd.json` | FAA aircraft data (approach speeds) |
| `oak.geojson` | OAK airport ground layout |
| `sfo.geojson` | SFO airport ground layout |

### Recordings

| File | Issue | Aircraft | Bug |
|------|-------|----------|-----|
| `issue58-jstar-intermediate-fix-recording.json` | #58 | KFB7 | JSTAR intermediate fix joining — backwards STAR route |
| `issue58-star-180-recording.json` | #58 | SWA797 | NavigationPath STAR token causes 180 reversal |
| `issue62-altitude-profile-recording.json` | #62 | — | onAltitudeProfile flag ignored, DVIA not working |
| `issue67-procedure-version-recording.json` | #67 | — | Outdated STAR version (BDEGA3 → BDEGA4) |
| `issue67-dvia-recording.json` | #67 | — | DVIA constraints not applied without runway suffix |
| `issue70-route-following-recording.json` | #70 | EVA18 | Fix-to-fix routing ignored (PIRAT → SAU) |
| `issue74-capp-wrong-transition-recording.json` | #74 | UAL238 | CAPP picks CCR transition via BERKS false positive |
| `issue77-alwys-descent-recording.json` | #77 | SKW5456 | ALWYS3 STAR descent — AtOrAbove constraint not triggering descent |
| `oak-taxi-recording.json` | — | NKS2904 | OAK taxi routing baseline |

### Adding a new airport layout

Download from the vNAS training API:

```
curl -o tests/Yaat.Sim.Tests/TestData/mia.geojson \
  https://data-api.vnas.vatsim.net/api/training/airports/MIA/map
```

`TestAirportGroundData` picks up any `{airportid}.geojson` in TestData automatically.

## Key Helpers

| Class | Location | Purpose |
|-------|----------|---------|
| `RecordingLoader` | `tests/Yaat.Sim.Tests/Helpers/RecordingLoader.cs` | Loads `SessionRecording` from `.json` or `.yaat-bug-report-bundle.zip` |
| `TestVnasData` | `tests/Yaat.Sim.Tests/TestVnasData.cs` | Thread-safe singleton loader for NavData + CIFP + aircraft specs |
| `TestAirportGroundData` | `tests/Yaat.Sim.Tests/Helpers/TestAirportGroundData.cs` | `IAirportGroundData` impl that loads from TestData GeoJSON |
| `SimLog` | `src/Yaat.Sim/SimLog.cs` | Static logger facade — wire to xunit output via `SimLog.Initialize(loggerFactory)` |
| `SimulationEngine.Replay()` | `src/Yaat.Sim/Simulation/SimulationEngine.cs` | Loads scenario, applies weather, replays actions to target time |
| `SimulationEngine.TickOneSecond()` | same | Advance simulation by 1 second (4 physics sub-ticks) |
| `SimulationEngine.FindAircraft()` | same | Look up live aircraft state by callsign |
| `SimulationEngine.SendCommand()` | same | Dispatch a command string to an aircraft mid-replay |
