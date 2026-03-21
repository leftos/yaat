using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #134: Jets exiting OAK 28L at wrong spot.
///
/// Recording: S2-OAK-4 VFR Transitions Radar Concepts — N70CS (C25C) lands
/// on 28L with no exit instruction. The aircraft exits between taxiways J and P
/// into the grass area instead of at a proper taxiway intersection.
/// </summary>
public class Issue134OakRunwayExitTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue134-oak-runway-exit-recording.json";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.Initialize(loggerFactory);

        NavigationDatabase.SetInstance(navDb);
        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Unit test: FindNearestExit on OAK 28L should return a node that is a real
    /// taxiway intersection (connected to 2+ distinct taxiway names), not an
    /// intermediate routing vertex with edges to only one taxiway.
    /// </summary>
    [Fact]
    public void FindNearestExit_28L_ReturnsRealIntersection()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        var rwy28L = layout.FindGroundRunway("28L");
        Assert.NotNull(rwy28L);

        // Position along 28L near where a jet would stop after rollout
        // (roughly between taxiways J and P). Use a coordinate about 2/3 along
        // the runway to simulate where a landing aircraft would decelerate to.
        var coords = rwy28L.Coordinates;
        int idx = (int)(coords.Count * 0.65);
        double lat = coords[idx].Lat;
        double lon = coords[idx].Lon;

        var exitNode = layout.FindNearestExit(lat, lon, new TrueHeading(280.0), "28L");
        Assert.NotNull(exitNode);

        // The exit node must be a real taxiway intersection, not an intermediate
        // routing vertex. Count distinct non-runway taxiway names.
        var distinctTaxiways = exitNode
            .Edges.Where(e => !e.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.TaxiwayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        output.WriteLine(
            $"Exit node {exitNode.Id} at ({exitNode.Latitude:F6},{exitNode.Longitude:F6}): "
                + $"taxiways=[{string.Join(", ", distinctTaxiways)}], "
                + $"type={exitNode.Type}, edges={exitNode.Edges.Count}"
        );

        Assert.True(
            (distinctTaxiways.Count >= 2)
                || (exitNode.Type is GroundNodeType.Spot or GroundNodeType.RunwayHoldShort)
                || (exitNode.Edges.Count(e => !e.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)) >= 3),
            $"Exit node {exitNode.Id} is an intermediate routing vertex with taxiways [{string.Join(", ", distinctTaxiways)}] — "
                + "expected a real intersection with 2+ distinct taxiway names"
        );
    }

    /// <summary>
    /// Unit test: FindExitAheadOnRunway on OAK 28L should also return a real
    /// taxiway intersection, not an intermediate routing vertex.
    /// </summary>
    [Fact]
    public void FindExitAheadOnRunway_28L_ReturnsRealIntersection()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        var rwy28L = layout.FindGroundRunway("28L");
        Assert.NotNull(rwy28L);

        // Position early on 28L so exits ahead include J and P
        var coords = rwy28L.Coordinates;
        int idx = coords.Count / 3;
        double lat = coords[idx].Lat;
        double lon = coords[idx].Lon;

        var result = layout.FindExitAheadOnRunway(lat, lon, new TrueHeading(280.0), null, "28L");
        Assert.NotNull(result);

        var exitNode = result.Value.Node;
        var distinctTaxiways = exitNode
            .Edges.Where(e => !e.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.TaxiwayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        output.WriteLine(
            $"Exit ahead node {exitNode.Id} at ({exitNode.Latitude:F6},{exitNode.Longitude:F6}): "
                + $"taxiway={result.Value.Taxiway}, "
                + $"all taxiways=[{string.Join(", ", distinctTaxiways)}], "
                + $"type={exitNode.Type}"
        );

        Assert.True(
            (distinctTaxiways.Count >= 2)
                || (exitNode.Type is GroundNodeType.Spot or GroundNodeType.RunwayHoldShort)
                || (exitNode.Edges.Count(e => !e.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)) >= 3),
            $"Exit node {exitNode.Id} is an intermediate routing vertex with taxiways [{string.Join(", ", distinctTaxiways)}] — "
                + "expected a real intersection"
        );
    }

    /// <summary>
    /// E2E: Replay N70CS landing on OAK 28L. After runway exit completes, the
    /// aircraft should end up at a real taxiway intersection, not in the grass
    /// between J and P.
    /// </summary>
    [Fact]
    public void N70CS_ExitsAtProperTaxiway()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to just after CLAND at t=895
        engine.Replay(recording, 900);

        var ac = engine.FindAircraft("N70CS");
        Assert.NotNull(ac);

        output.WriteLine($"t=900: alt={ac.Altitude:F0} gs={ac.GroundSpeed:F0} phase={ac.Phases?.CurrentPhase?.GetType().Name}");

        // Tick until RunwayExitPhase completes (transitions to HoldingAfterExitPhase)
        bool exitedRunway = false;

        for (int t = 1; t <= 600; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("N70CS");
            if (ac is null)
            {
                output.WriteLine($"t+{t}: aircraft deleted");
                break;
            }

            string? phaseName = ac.Phases?.CurrentPhase?.GetType().Name;

            if (t % 30 == 0)
            {
                output.WriteLine($"t+{t}: alt={ac.Altitude:F0} gs={ac.GroundSpeed:F0} onGround={ac.IsOnGround} phase={phaseName}");
            }

            if (phaseName == "HoldingAfterExitPhase")
            {
                exitedRunway = true;
                output.WriteLine($"t+{t}: runway exit complete at ({ac.Latitude:F6},{ac.Longitude:F6}) hdg={ac.TrueHeading.Degrees:F0}");

                var layout = LoadOakLayout();
                Assert.NotNull(layout);

                // Aircraft should be snapped to a graph node
                var nearestNode = layout.FindNearestNode(ac.Latitude, ac.Longitude);
                Assert.NotNull(nearestNode);
                double distToNode = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, nearestNode.Latitude, nearestNode.Longitude);
                output.WriteLine($"Nearest node: {nearestNode.Id} type={nearestNode.Type} dist={distToNode:F4}nm");
                Assert.True(distToNode < 0.02, $"Aircraft is {distToNode:F4}nm from nearest node — should be snapped");

                // Aircraft should be at a RunwayHoldShort node
                Assert.True(nearestNode.Type == GroundNodeType.RunwayHoldShort, $"Expected hold-short node, got type={nearestNode.Type}");

                // Aircraft should face away from the runway (heading diverges from 280°)
                double headingDiff = GeoMath.AbsBearingDifference(ac.TrueHeading.Degrees, 280.0);
                output.WriteLine($"Heading diff from runway: {headingDiff:F0}");
                Assert.True(headingDiff > 30, $"Heading {ac.TrueHeading.Degrees:F0} too close to runway 280");

                break;
            }
        }

        Assert.True(exitedRunway, "N70CS never completed runway exit within 600 seconds");
    }

    /// <summary>
    /// Diagnostic: logs the exit nodes found along OAK 28L to understand the
    /// node graph structure around J and P taxiways.
    /// </summary>
    [Fact]
    public void Diagnostic_LogExitCandidatesAlongRunway()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        var rwy28L = layout.FindGroundRunway("28L");
        Assert.NotNull(rwy28L);

        output.WriteLine("=== OAK 28L exit candidates at various runway positions ===");
        var coords = rwy28L.Coordinates;

        for (int i = 0; i < coords.Count; i++)
        {
            double lat = coords[i].Lat;
            double lon = coords[i].Lon;

            var exit = layout.FindNearestExit(lat, lon, new TrueHeading(280.0), "28L");
            var exitAhead = layout.FindExitAheadOnRunway(lat, lon, new TrueHeading(280.0), null, "28L");

            string nearestInfo = "(none)";
            if (exit is not null)
            {
                var tws = exit
                    .Edges.Where(e => !e.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.TaxiwayName)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                double dist = GeoMath.DistanceNm(lat, lon, exit.Latitude, exit.Longitude);
                nearestInfo = $"node={exit.Id} dist={dist:F3}nm tws=[{string.Join(",", tws)}] type={exit.Type}";
            }

            string aheadInfo = "(none)";
            if (exitAhead is not null)
            {
                var n = exitAhead.Value.Node;
                var tws = n
                    .Edges.Where(e => !e.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.TaxiwayName)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                double dist = GeoMath.DistanceNm(lat, lon, n.Latitude, n.Longitude);
                aheadInfo = $"node={n.Id} dist={dist:F3}nm tw={exitAhead.Value.Taxiway} tws=[{string.Join(",", tws)}]";
            }

            output.WriteLine($"pos[{i}] ({lat:F6},{lon:F6}): nearest={nearestInfo} | ahead={aheadInfo}");
        }
    }

    private static AirportGroundLayout? LoadOakLayout()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        if (!File.Exists(path))
        {
            return null;
        }

        return GeoJsonParser.Parse("OAK", File.ReadAllText(path), null);
    }
}
