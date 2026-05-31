using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for GitHub issue #166 — UAL19 issued a no-arg <c>CROSS</c> at SFO
/// to cross 01L/19R on taxiway H, but the aircraft beelined diagonally across
/// the grass instead of following the painted H line through the runway.
///
/// Recording: <c>issue166-cross-shortcuts-grass-recording.zip</c> (S1-SFO-4
/// scenario, 457s, 6 aircraft, ARTCC ZOA). User actions of interest:
///   t=249s  TAXI H B Q1 A T8 @F14   (UAL19, after vacating 19L)
///   t=314s  CROSS                    (no-arg, clears 01L/19R hold-short on H)
///
/// Assertion: while UAL19 is in <see cref="CrossingRunwayPhase"/>, its closest
/// ground node must be a node on taxiway H (or an H-named runway-crossing arc
/// node) — i.e. the aircraft tracks the H taxi line through the crossing
/// rather than cutting straight across the runway surface.
/// </summary>
public class Issue166CrossShortcutsGrassTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue166-cross-shortcuts-grass-recording.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("CrossingRunwayPhase", LogLevel.Debug)
            .EnableCategory("TaxiingPhase", LogLevel.Debug)
            .EnableCategory("GroundNavigator", LogLevel.Information)
            .InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Diagnostic: log UAL19 position, heading, current phase, and nearest
    /// nodes second-by-second from t=310 (just before CROSS at 314) through
    /// the duration of <see cref="CrossingRunwayPhase"/>. Use to inspect what
    /// went wrong before tightening the real assertion.
    /// </summary>
    [Fact]
    public void Ual19_CrossingDiagnostic()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);

        engine.Replay(recording, 310);

        for (int t = 310; t <= 360; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft("UAL19");
            if (ac is null)
            {
                continue;
            }
            string phase = ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)";
            string nearest = NearestNodeHelper.Describe(ac, layout, count: 3);
            output.WriteLine(
                $"t={t, 3} phase={phase, -22} ias={ac.IndicatedAirspeed, 5:F1} pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6}) hdg={ac.TrueHeading.Degrees, 5:F1} nearest=[{nearest}]"
            );
        }
    }

    [Fact]
    public void Ual19_FollowsHTaxiLineThroughRunwayCrossing()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);

        AssertFollowsHLineThroughCrossing(engine, layout, recording, output);
    }

    /// <summary>
    /// Replay the recording from t=310 through the 01L/19R crossing and assert the
    /// aircraft tracks the painted H line — passing within 15 ft of 6+ distinct
    /// H-named graph nodes and turning ≥30° as the line curves — rather than
    /// beelining diagonally across the runway surface (issue #166). Shared by the
    /// V1-default test above and the all-V2 variant in
    /// <see cref="Issue166CrossUnderV2Tests"/>.
    /// </summary>
    internal static void AssertFollowsHLineThroughCrossing(
        SimulationEngine engine,
        AirportGroundLayout layout,
        SessionRecording recording,
        ITestOutputHelper output
    )
    {
        engine.Replay(recording, 310);

        // Tick forward through the crossing. For each tick, accumulate which
        // H-named ground nodes the aircraft passed within 15 ft of. The fix
        // walks the painted H line node-by-node so it sweeps past 7+ distinct
        // intermediate H nodes. The pre-fix beeline only "passes near" the
        // entry HS, possibly a runway-centerline arc node, and the exit HS —
        // at most 3 distinct H nodes total.
        var closeHNodeIds = new HashSet<int>();
        var headings = new List<double>();
        bool sawCrossing = false;
        int crossingTicks = 0;

        for (int t = 311; t <= 380; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft("UAL19");
            if (ac is null)
            {
                continue;
            }

            bool inCrossing = ac.Phases?.CurrentPhase is CrossingRunwayPhase;
            if (inCrossing)
            {
                sawCrossing = true;
                crossingTicks++;
                headings.Add(ac.TrueHeading.Degrees);
                AddCloseHNodes(ac, layout, maxDistFt: 15.0, into: closeHNodeIds);

                string nearest = NearestNodeHelper.Describe(ac, layout, count: 3);
                output.WriteLine(
                    $"t={t, 3} pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6}) hdg={ac.TrueHeading.Degrees, 5:F1} closeHNodesSoFar={closeHNodeIds.Count} nearest=[{nearest}]"
                );
            }
            else if (sawCrossing)
            {
                // Already finished crossing — stop sampling.
                break;
            }
        }

        Assert.True(sawCrossing, "UAL19 should enter CrossingRunwayPhase after the no-arg CROSS at t=314");
        Assert.NotEqual(0, crossingTicks);

        // Diagnostic summary.
        double headingRange = headings.Max() - headings.Min();
        output.WriteLine($"Crossing summary: ticks={crossingTicks}, distinctCloseHNodes={closeHNodeIds.Count}, headingRange={headingRange:F1}°");

        // The painted H taxi line across 01L/19R passes through ~10 graph
        // nodes (entry HS, 4-5 H taxiway intersections, 3-4 H-named runway-
        // centerline crossing-arc tangent nodes, exit HS). The fix walks it
        // node-by-node so it lands within 15 ft of 7+ of them. The pre-fix
        // beeline cuts diagonally across, getting within 15 ft of at most
        // 3 H-named nodes (entry HS, a single runway-centerline node it
        // grazes near the midpoint, exit HS).
        Assert.True(
            closeHNodeIds.Count >= 6,
            $"Aircraft only visited {closeHNodeIds.Count} distinct H-named graph nodes within 15 ft during the crossing — "
                + "expected to track the painted H line through 6+ intermediate nodes. "
                + "Fewer than 6 indicates a beeline across the runway surface (issue #166)."
        );

        // Heading must vary during the crossing — a beeline at constant
        // bearing-to-target produces almost no heading change once aligned.
        // The painted H line curves >30° across the SFO 01L/19R crossing.
        Assert.True(
            headingRange >= 30.0,
            $"Aircraft heading varied only {headingRange:F1}° during crossing — expected ≥30° "
                + "as the painted H line curves across 01L/19R. Low variation indicates a beeline."
        );
    }

    /// <summary>
    /// Add the IDs of ground nodes whose edges carry the name "H" and lie
    /// within <paramref name="maxDistFt"/> of the aircraft's current position.
    /// "Edge name contains H" covers pure-H taxiway nodes plus the fillet-arc
    /// nodes whose edges are labelled e.g. "H - RWY01L/19R" (the painted H
    /// line across the runway surface).
    /// </summary>
    internal static void AddCloseHNodes(AircraftState ac, AirportGroundLayout layout, double maxDistFt, HashSet<int> into)
    {
        double maxDistNm = maxDistFt / 6076.12;
        foreach (var node in layout.Nodes.Values)
        {
            double distNm = GeoMath.DistanceNm(ac.Position, node.Position);
            if (distNm > maxDistNm)
            {
                continue;
            }
            bool onH = node.Edges.Any(e => e.TaxiwayName is not null && e.TaxiwayName.Split(' ', '-', '/').Contains("H"));
            if (onH)
            {
                into.Add(node.Id);
            }
        }
    }
}

/// <summary>
/// All-V2 variant of <see cref="Issue166CrossShortcutsGrassTests"/>. Under the V2 nav stack
/// UAL19 reaches the 01L/19R hold-short later than under V1, so the no-arg <c>CROSS</c> at
/// t=314 clears the crossing while it is still taxiing toward it (pre-cleared) rather than
/// while it sits holding. The fix in <see cref="CrossingRunwayPhase"/>'s entry point
/// (<c>TaxiingPhase.BuildPreClearedCrossingPhases</c>) must still hand off to a
/// <see cref="CrossingRunwayPhase"/> so the aircraft tracks the painted H line across the
/// runway instead of beelining. Runs in the parallelization-disabled "V2 Acceptance"
/// collection so the global pathfinder/navigator router flip cannot race the V1-default suite.
/// </summary>
[Collection("V2 Acceptance")]
public class Issue166CrossUnderV2Tests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue166-cross-shortcuts-grass-recording.zip";

    [Fact]
    public void Ual19_FollowsHTaxiLineThroughRunwayCrossing_OnV2()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var recording = RecordingLoader.Load(RecordingPath);
        if (recording is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData(FilletMode.V2);
        var layout = groundData.GetLayout("SFO");
        Assert.NotNull(layout);

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("CrossingRunwayPhase", LogLevel.Debug)
            .EnableCategory("TaxiingPhase", LogLevel.Debug)
            .InitializeSimLog();

        var engine = new SimulationEngine(groundData);
        Issue166CrossShortcutsGrassTests.AssertFollowsHLineThroughCrossing(engine, layout, recording, output);
    }

    /// <summary>
    /// The pre-cleared-crossing hand-off must fire only for a genuinely-new runway, never for
    /// the runway UAL19 just landed on and vacated (01R/19L, far-side hold-short node 875). Its
    /// hold-short is also a cleared <c>RunwayCrossing</c>, but the runway surface is behind the
    /// aircraft (no forward same-runway exit), so it must stay in TaxiingPhase and finish its
    /// exit. Replays across the whole post-landing taxi and asserts the only runway it enters a
    /// <see cref="CrossingRunwayPhase"/> for is 01L/19R.
    /// </summary>
    [Fact]
    public void Ual19_DoesNotEnterCrossingForVacatedRunway_OnV2()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var recording = RecordingLoader.Load(RecordingPath);
        if (recording is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData(FilletMode.V2);
        Assert.NotNull(groundData.GetLayout("SFO"));

        var engine = new SimulationEngine(groundData);
        engine.Replay(recording, 250);

        var crossedRunways = new HashSet<string>();
        for (int t = 251; t <= 360; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft("UAL19");
            if (ac?.Phases?.CurrentPhase is CrossingRunwayPhase crossing && crossing.RunwayId is { Length: > 0 } rwy)
            {
                crossedRunways.Add(rwy);
            }
        }

        output.WriteLine($"UAL19 entered CrossingRunwayPhase for: [{string.Join(", ", crossedRunways)}]");
        Assert.DoesNotContain("01R/19L", crossedRunways);
        Assert.Contains("01L/19R", crossedRunways);
    }
}
