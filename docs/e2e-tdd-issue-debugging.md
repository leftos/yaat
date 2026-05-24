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

### Recording Format (v4)

Recordings use a ZIP archive (`.zip`) with individually Brotli-compressed entries:

| Entry | Purpose |
|-------|---------|
| `manifest.json` | Version, metadata, snapshot timestamps |
| `scenario.json.br` | Full scenario JSON |
| `actions.json.br` | All recorded actions with timestamps |
| `weather.json.br` | Weather profile |
| `snapshots/{t}.json.br` | State snapshot at time `t` (on-demand loading) |
| `layouts/{airportId}.json.br` | Ground layouts (deduplicated, shared across aircraft) |

Each snapshot (`StateSnapshotDto`) captures: all aircraft (position, physics, control targets, command queue, phases, track ownership, scratchpads, procedures), scenario state (queues, generators, settings, coordination), and RNG state. Snapshots are versioned via `SchemaVersion` with a migration chain (`SnapshotSchemaMigrator`).

**Snapshots enable:**
1. **Exact state restore** — load the snapshot at time T and get the precise state the user saw
2. **Hybrid replay** — restore snapshot at T, then replay commands from T onward with current (fixed) code
3. **Faster rewind** — restore nearest snapshot instead of replaying from t=0

**Loading:** `RecordingLoader.Load()` uses `ToBaseSessionRecording()` (zero snapshots). Tests that need snapshots use `RecordingLoader.OpenArchive()` and `ReadSnapshotAt()`.

**Generating snapshots:** Generated at export time, not runtime. The server replays through a temporary isolated room and captures a snapshot every second. Zero runtime memory overhead.

## Bug Report Bundles

A `.yaat-bug-report-bundle.zip` packages a recording with client and server logs into a single file. Created via **Scenario > Save Bug Report Bundle...** in the client.

**Contents:**
| Entry | Description |
|-------|-------------|
| `recording.yaat-recording.zip` | The session recording (v4 archive) |
| `yaat-client.log` | Client log at the time of the report |
| `yaat-server.log` | Server log (only included when connected to a local server) |

**Using bundles in tests:** Place the bundle directly in TestData — no need to extract the recording manually. `RecordingLoader.Load()` handles all formats transparently (v4 archives, legacy bundles, plain JSON).

## Step-by-Step: From Issue to Test

### 1. Get the recording into TestData

Download the recording or bug report bundle from the issue and place it in TestData. Bug report bundles (`.yaat-bug-report-bundle.zip`) can be placed directly — `RecordingLoader` handles them transparently.

Convention: `issue{N}-{short-description}-recording.zip` or `-recording.yaat-bug-report-bundle.zip`. Including the issue number makes it easy to trace back to the GitHub thread.

**Use `tools/bug_bundle.py` to install and triage.** Requires `brotli` (`pip install brotli`).

```bash
# Install from a local download
python tools/bug_bundle.py install /path/to/bundle.zip --issue 77 --desc alwys-descent

# ...or fetch the first .zip attachment from the GitHub issue directly (uses gh)
python tools/bug_bundle.py install --issue 77 --desc alwys-descent

# Quick triage: duration, ARTCC, aircraft at t=0, logs present?
python tools/bug_bundle.py info tests/Yaat.Sim.Tests/TestData/issue77-alwys-descent-recording.yaat-bug-report-bundle.zip

# Aircraft state at the moment the bug manifests
python tools/bug_bundle.py snapshot <bundle> --at 182 --callsign UAL238 --out .tmp/ual238-182.json

# Actions the user took (to find the triggering command)
python tools/bug_bundle.py actions <bundle>

# Extract logs for grepping
python tools/bug_bundle.py logs <bundle>
```

See `.claude/skills/bug-bundle/SKILL.md` for the full subcommand reference.

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
private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

private SimulationEngine? BuildEngine()
{
    TestVnasData.EnsureInitialized();
    var navDb = TestVnasData.NavigationDb;
    if (navDb is null) return null;

    var groundData = new TestAirportGroundData();
    var loggerFactory = LoggerFactory.Create(builder =>
        builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
    SimLog.Initialize(loggerFactory);

    return new SimulationEngine(groundData);
}
```

Both return `null` when data files are missing. Tests that get `null` silently skip — CI stays green on machines without test data.

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

**Prefer `ReplayOneSecond()` over `TickOneSecond()`** when continuing a recording. `TickOneSecond()` only advances physics — it does **not** apply recording actions. `ReplayOneSecond()` ticks physics **and** applies any recorded commands/settings at the new time, so the replay stays faithful to what the user did. Call `engine.Replay(recording, startTime)` first (which sets up the replay cursor), then `engine.ReplayOneSecond()` in a loop.

**Never call `ReplayFromStartTo(t, recording.Actions)` inside a per-tick loop.** It re-runs the recording from t=0 every call (re-applying every action and re-ticking every prior second), so each iteration is a stacked replay — actions fire repeatedly, conditional commands re-trigger every tick, and aircraft can drift wildly off course before crashing in unrelated code paths (e.g. `MagneticDeclination.GetDeclination` on an out-of-range longitude). Use `ReplayOneSecond()` for the loop, `FastForwardTo(target, actions)` to advance from the current time to a later one, or `ReplayRange(prev, target, actions)` for an explicit start/end window. `ReplayFromStartTo` is only for one-shot rewinds back to scratch.

**Always include nearest-node context for ground bugs.** Use `NearestNodeHelper` from `tests/Yaat.Sim.Tests/Helpers/NearestNodeHelper.cs` to report the 3 closest ground layout nodes at each tick. This immediately shows whether the aircraft is on the expected taxiway edges or has drifted onto grass:

```csharp
// In your diagnostic loop (requires a ground layout reference):
var layout = new TestAirportGroundData().GetLayout("SFO");
if (layout is not null)
{
    NearestNodeHelper.Log(output, $"t={t}:", ac, layout);
    // Output: t=397: nearestNodes=[#231 Twy[T] 42ft, #232 Twy[T] 118ft, #230 Twy[T/RWY10L/28R/E] 134ft]
}
```

The helper reports each node's ID, type (HS/Twy/Parking), taxiway names, and distance in feet. If the nearest nodes are all on the expected taxiway, the aircraft is following the path correctly. If the nearest node is a RWY node or on a different taxiway, the aircraft has strayed.

**What to log depends on the bug type:**

| Bug type | Log these fields |
|----------|-----------------|
| Wrong heading / 180 reversal | `Heading`, `Targets.NavigationRoute[0].Name`, lat/lon |
| Stays too high / wrong descent | `Altitude`, `VerticalSpeed`, `Targets.TargetAltitude`, next fix |
| Doesn't follow route | `Targets.NavigationRoute` (all fixes), `Heading`, bearing to next fix |
| Wrong approach / transition | `Phases.ActiveApproach`, `Phases.Phases` list, nav targets |
| Ground taxi overshoot | `AssignedTaxiRoute.Segments`, lat/lon, current segment index, **nearest nodes** |
| Runway exit wrong path | Phase name, gs, hdg, **nearest nodes**, exit waypoint IDs |

### 5b. Full replay from t=0 vs hybrid replay with snapshots

Every replay test has to choose one of two strategies. Pick deliberately — the wrong choice either hides the bug or makes the test impossible to write.

**Full replay from t=0** ticks the engine from the scenario start, applying every recorded command in order with the *current* code (fix included). This is the default and what most tests in this repo do.

**Hybrid replay** restores a snapshot captured during recording at time T, then replays commands from T onward with current code. The pre-T state is frozen at what the user actually saw; only behavior from T onward is exercised by the fixed code.

#### Decision rule

Use **full replay from t=0** when the fix only changes behavior *at or after* the buggy moment. The aircraft still reaches the buggy state the same way the user saw, and your assertion fires there. This is the stronger test — it proves the fix works end-to-end, including that earlier phases still reach the moment of interest.

Use **hybrid replay** when the fix changes behavior *before* the buggy moment — physics, command semantics, phase transitions, navigation, anything that alters the path through the session. The snapshot freezes pre-T state so your assertion at time T still has the setup it needs.

#### What "localized" means

A bug is localized when the fix only affects code paths executed at or after time T. Typical examples: a single command handler, a phase's exit condition, an approach transition selector, a descent-profile calculation invoked near the bug. If you can point at the file(s) the fix touches and confirm none of them run during the aircraft's earlier trajectory, full replay is fine.

A bug is *not* localized when the fix touches turn rate, thrust response, lateral navigation, fix sequencing, phase transitions, or anything else the aircraft exercises continuously. Even "small" physics tweaks compound over minutes of flight.

#### The failure mode each strategy avoids

**Full replay avoids the false-pass from hybrid.** Hybrid only tests the post-snapshot slice. If your fix accidentally breaks behavior *before* T — e.g., an aircraft that used to descend correctly now stays high — hybrid won't catch it, because the snapshot restores the pre-fix state regardless. Full replay re-exercises every earlier phase with the new code, so regressions surface.

**Hybrid avoids the unreachable-assertion trap from full replay.** If your fix changes pre-T behavior (even correctly), the aircraft may no longer reach the buggy state from t=0 the same way. It might turn earlier, descend sooner, sequence a fix at a different time, or never enter the phase where the bug lives. Your assertion at T then has nothing meaningful to check — the test either fails for the wrong reason or passes vacuously. Hybrid sidesteps this by pinning the setup.

#### Canonical case for hybrid: WAIT presets

Aircraft with `WAIT` preset commands are sensitive to dispatch timing (see Rules). If your fix touches WAIT behavior or anything that affects when a WAIT fires, full replay from t=0 will dispatch the aircraft at a different time — every downstream event shifts, and any assertion tied to a specific time `t` is now pointing at the wrong moment. Hybrid replay with a snapshot captured after the WAIT already fired is the right tool here.

#### Diagnosing why full replay diverges from the recorded snapshot

When `engine.Replay(recording, T)` lands on different state than the snapshot at `T`, use the snapshot-diff verification API to pinpoint the *first* tick where divergence began (rather than guessing at the symptom):

```csharp
using var archive = RecordingLoader.OpenArchive(RecordingPath);
var recording = archive!.ToBaseSessionRecording();
var engine = BuildEngine();
engine.Replay(recording, 0);

var result = engine.ReplayRangeWithVerification(0, 1300, recording.Actions, archive);
foreach (var (ts, drift) in result.Drifts.Select(d => (d.ElapsedSeconds, d)).Take(5))
{
    output.WriteLine($"t={ts:F0}s drifts: {drift.AircraftDrifts.Count}");
}
```

`SnapshotDiff` checks position (0.5 nm), heading (5°), altitude (100 ft), IAS (10 kt), `NavigationRoute` (exact), `AssignedAltitude/Heading/Speed`, current phase type, and `Track.Owner/HandoffPeer` at every snapshot timestamp. Empty `Drifts` ⇒ replay matches; any earlier divergence usually pinpoints the actual cause (engine-version drift, missed action, RNG change). Track commands and AS-prefixed compounds are processed during replay (see `ReplayTrackApplier`); coordination commands (RD/RDH/RDR/RDACK/RDAUTO) are skipped with a debug log because they have no Sim-side handler.

#### How to do hybrid replay

```csharp
[Fact]
public void HybridReplay_FixAppliesAfterSnapshot()
{
    var archive = RecordingLoader.OpenArchive(RecordingPath);
    if (archive is null) return;

    using (archive)
    {
        var recording = archive.ToBaseSessionRecording();
        var engine = BuildEngine();
        if (engine is null) return;

        engine.Replay(recording, 0); // load scenario + weather

        // Restore snapshot just before the bug
        var snapshot = archive.ReadSnapshotAt(1350);
        if (snapshot is null) return;

        engine.RestoreFromSnapshot(snapshot.State);

        // Replay commands from snapshot onward with CURRENT code
        int startTime = (int)snapshot.ElapsedSeconds;
        engine.ReplayRange(startTime, startTime + 120, recording.Actions);

        var aircraft = engine.FindAircraft("N427MX");
        Assert.NotNull(aircraft);
        // ... assertions ...
    }
}
```

Most issue fixes in this repo use full replay — the bugs were localized enough that earlier state still reached the buggy moment correctly. Reach for hybrid when the decision rule above tells you to, not by default.

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
- **Watch out for WAIT presets.** Aircraft with `WAIT` preset commands are sensitive to dispatch timing. If WAIT behavior changes, recordings made before the change will produce different state at any given time `t`. Prefer testing aircraft without WAIT presets, re-record after the fix, or use hybrid replay with a snapshot captured after the WAIT fires (see §5b).

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

### Recordings folder

Recordings live in `tests/Yaat.Sim.Tests/TestData/`. Install new bundles with
`python tools/bug_bundle.py install <local-or-github-issue>` (see the **Bug Report
Bundles** section above), which copies them into TestData with a descriptive name.
The `consolidate-recordings` skill periodically hash-renames bundles to `<hash12>.zip`
when duplicates are detected — names in the folder are not stable. The
authoritative description of what a recording demonstrates lives in the test
class doc comment (`tests/Yaat.Sim.Tests/Simulation/*.cs`), not here.

### Adding a new airport layout

Download from the vNAS training API:

```
curl -o tests/Yaat.Sim.Tests/TestData/mia.geojson \
  https://data-api.vnas.vatsim.net/api/training/airports/MIA/map
```

`TestAirportGroundData` picks up any `{airportid}.geojson` in TestData automatically.

## Layout Inspector Tool

The `Yaat.LayoutInspector` tool (`tools/Yaat.LayoutInspector/`) loads an airport GeoJSON and exposes detailed graph queries. Use it during bug investigation to understand airport ground layout topology without staring at raw GeoJSON.

### Running

```bash
dotnet run --project tools/Yaat.LayoutInspector -- <geojson-path> [options]
```

### Common Investigation Queries

| Query | Flag | Use case |
|-------|------|----------|
| Overview | (default) | Node/edge counts, taxiway list, runways |
| Node detail | `--node 230` | Inspect a specific node's edges and neighbors |
| Taxiway detail | `--taxiway T` | All nodes/edges on a taxiway, connected taxiways, hold-short count |
| Runway detail | `--runway 28R` | Centerline nodes and hold-short nodes for a runway |
| Exits | `--exits 28R` | All exit candidates from a runway with angles and distances |
| BFS path | `--path 230 T` | Step-by-step BFS trace from a centerline node through taxiway T to hold-short |
| Parking | `--parking` | All parking nodes with positions and headings |
| Spots | `--spots` | Taxiway intersection spots |
| JSON output | `--json` | Machine-readable output for scripting |
| Full dump | `--dump` | Everything (all nodes, taxiways, runways, exits, parking, spots) as one JSON file. Pipe to a file and grep as needed |

### When to Use

- **Runway exit bugs** — use `--exits 28R` to see all exits and `--path <node> <twy>` to trace the graph path from centerline to hold-short
- **Taxi routing bugs** — use `--taxiway T` to see connectivity, then `--node <id>` to inspect specific intersections
- **Hold-short bugs** — use `--runway 28R` to find all hold-short nodes and verify their runway IDs
- **Understanding multi-hop exits** — high-speed exits like T at SFO have 9 hops between centerline and hold-short; `--path` shows each hop with edge distances and node types

### Example: Investigating EL T at SFO

```bash
# See all exits from 28R
dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson --exits 28R

# Trace T's path from centerline node 230
dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson --path 230 T

# Inspect the branch point
dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson --node 230
```

## Key Helpers

| Class | Location | Purpose |
|-------|----------|---------|
| `NearestNodeHelper` | `tests/Yaat.Sim.Tests/Helpers/NearestNodeHelper.cs` | Reports 3 closest ground nodes to an aircraft with ID, type, taxiway names, and distance in feet. Use in all ground/exit diagnostic tests. |
| `RecordingLoader` | `tests/Yaat.Sim.Tests/Helpers/RecordingLoader.cs` | Loads `SessionRecording` from `.json`, `.zip` archive, or `.yaat-bug-report-bundle.zip`. Uses `ToBaseSessionRecording()` (zero snapshots). |
| `RecordingArchive` | `src/Yaat.Sim/Simulation/RecordingArchive.cs` | On-demand archive reader: `ReadSnapshotAt()`, `FindNearestSnapshotIndex()`, `ToBaseSessionRecording()` |
| `TestVnasData` | `tests/Yaat.Sim.Tests/TestVnasData.cs` | Thread-safe singleton loader for NavData + CIFP + aircraft specs |
| `TestAirportGroundData` | `tests/Yaat.Sim.Tests/Helpers/TestAirportGroundData.cs` | `IAirportGroundData` impl that loads from TestData GeoJSON |
| `SimLog` | `src/Yaat.Sim/SimLog.cs` | Static logger facade — wire to xunit output via `SimLog.Initialize(loggerFactory)` |
| `SimulationEngine.Replay()` | `src/Yaat.Sim/Simulation/SimulationEngine.cs` | Loads scenario, applies weather, replays actions to target time; sets up cursor for `ReplayOneSecond()` |
| `SimulationEngine.ReplayOneSecond()` | same | Continue replay by 1 second — ticks physics AND applies recorded actions. Use after `Replay()` for tick-by-tick inspection |
| `SimulationEngine.TickOneSecond()` | same | Advance physics only by 1 second (no recording actions). Use for post-replay manual ticking |
| `SimulationEngine.RestoreFromSnapshot()` | same | Restore exact simulation state from a snapshot. Use with `ReplayRange()` for hybrid replay |
| `SimulationEngine.FindAircraft()` | same | Look up live aircraft state by callsign |
| `SimulationEngine.SendCommand()` | same | Dispatch a command string to an aircraft mid-replay |
