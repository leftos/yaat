using System.IO.Compression;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #190: KMIA departures to runway 8R crossed the runway and held
/// short on the opposite side instead of stopping at the first valid hold-short on their current side.
///
/// Recording: T1: S2. Practical Exam (MIA East). ENY3516 receives <c>TAXIAUTO 08R</c> at t=112,
/// then later explicit taxi instructions ending in <c>RWY 08R</c>. Before the fix both routes could
/// traverse <c>RWY08R/26L</c> and target an L-side hold-short.
/// </summary>
public class Issue190Mia8RHoldShortTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/c000c2b0afc8.zip";
    private const string Callsign = "ENY3516";
    private const string DestinationRunway = "08R";
    private const int TaxiAutoStartNodeId = 304;
    private const int BeforeTaxiAutoCommand = 0;

    [Fact]
    public void Taxiauto8R_StopsAtDestinationHoldShortBeforeRunwaySurface()
    {
        var data = LoadIssueRecording();
        if (data is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        var aircraft = MakeGroundAircraftAtNode(data.MiaLayout, TaxiAutoStartNodeId);
        var result = GroundCommandHandler.TryTaxi(aircraft, new TaxiCommand([], [], DestinationRunway: DestinationRunway), data.MiaLayout);
        Assert.True(result.Success, $"TAXIAUTO 08R failed: {result.Message}");

        AssertRouteStopsAtDestinationHoldShortBeforeRunwaySurface(aircraft, data.MiaLayout, DestinationRunway);
    }

    [Fact]
    public void ExplicitTaxiTo8R_StopsAtDestinationHoldShortBeforeRunwaySurface()
    {
        var data = LoadIssueRecording();
        if (data is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        var aircraft = MakeGroundAircraftAtNode(data.MiaLayout, TaxiAutoStartNodeId);
        var result = GroundCommandHandler.TryTaxi(
            aircraft,
            new TaxiCommand(["N", "P", "M1"], [], DestinationRunway: DestinationRunway),
            data.MiaLayout
        );
        Assert.True(result.Success, $"TAXI N P M1 RWY 08R failed: {result.Message}");

        AssertRouteStopsAtDestinationHoldShortBeforeRunwaySurface(aircraft, data.MiaLayout, DestinationRunway);
    }

    [Theory]
    [InlineData("LUAW")]
    [InlineData("CTO")]
    public void DepartureClearanceFromCorrect8RHoldShortRoute_IsAccepted(string command)
    {
        var data = LoadIssueRecording();
        var engine = BuildEngine(data);
        if (data is null || engine is null || data.BeforeTaxiAutoSnapshot is null)
        {
            return;
        }

        engine.Replay(data.Recording, 0);
        engine.RestoreFromSnapshot(data.BeforeTaxiAutoSnapshot.State);

        engine.Scenario!.CommandRunDelayMaxSeconds = 0;

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        MoveAircraftToNode(aircraft, data.MiaLayout, TaxiAutoStartNodeId);

        var taxiResult = GroundCommandHandler.TryTaxi(aircraft, new TaxiCommand([], [], DestinationRunway: DestinationRunway), data.MiaLayout);
        Assert.True(taxiResult.Success, $"TAXIAUTO 08R failed: {taxiResult.Message}");

        var result = engine.SendCommand(Callsign, command);
        output.WriteLine($"{command}: success={result.Success} message={result.Message}");

        Assert.True(result.Success, $"{command} should be accepted from the assigned 8R destination hold-short route: {result.Message}");
    }

    private SimulationEngine? BuildEngine(Issue190Recording? data)
    {
        if (data is null)
        {
            return null;
        }

        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(new RecordingGroundData(data.Layouts));
    }

    private static Issue190Recording? LoadIssueRecording()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        if (recording is null || !File.Exists(RecordingPath))
        {
            return null;
        }

        using var outerZip = ZipFile.OpenRead(RecordingPath);
        if (outerZip.GetEntry("manifest.json") is not null)
        {
            using var directArchive = RecordingArchive.Open(RecordingPath);
            return LoadIssueRecording(recording, directArchive);
        }

        var nestedRecording = outerZip.GetEntry("recording.yaat-recording.zip");
        if (nestedRecording is null)
        {
            return null;
        }

        using var entryStream = nestedRecording.Open();
        using var archiveStream = new MemoryStream();
        entryStream.CopyTo(archiveStream);
        archiveStream.Position = 0;

        using var archive = RecordingArchive.Open(archiveStream);
        return LoadIssueRecording(recording, archive);
    }

    private static Issue190Recording? LoadIssueRecording(SessionRecording recording, RecordingArchive archive)
    {
        var layouts = archive.ReadAllLayouts();
        if (!layouts.TryGetValue("mia", out var miaLayout))
        {
            return null;
        }

        var beforeTaxiAuto = archive.ReadSnapshotAt(BeforeTaxiAutoCommand);
        return new Issue190Recording(recording, layouts, miaLayout, beforeTaxiAuto);
    }

    private static void AssertRouteStopsAtDestinationHoldShortBeforeRunwaySurface(AircraftState aircraft, AirportGroundLayout layout, string runwayId)
    {
        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.True(route.Segments.Count > 0, "Expected a non-empty taxi route.");

        var destinations = route.HoldShortPoints.Where(h => (h.Reason == HoldShortReason.DestinationRunway) && (h.TargetName == runwayId)).ToList();
        string holdShorts = string.Join("; ", route.HoldShortPoints.Select(h => $"node={h.NodeId} target={h.TargetName} reason={h.Reason}"));
        string routeSummary = string.Join(" ", route.Segments.Select(s => $"{s.FromNodeId}->{s.ToNodeId}:{s.TaxiwayName}"));
        Assert.True(
            destinations.Count == 1,
            $"Expected one destination hold-short for {runwayId}. Hold-shorts: [{holdShorts}], route: [{routeSummary}]"
        );

        int destinationNodeId = destinations[0].NodeId;
        Assert.Equal(destinationNodeId, route.Segments[^1].ToNodeId);
        Assert.True(
            layout.Nodes.TryGetValue(destinationNodeId, out var destinationNode),
            $"Destination hold-short node #{destinationNodeId} missing from MIA layout."
        );
        Assert.Equal(GroundNodeType.RunwayHoldShort, destinationNode.Type);

        var runwaySurfaceSegments = route.Segments.Where(s => SegmentTraversesRunwaySurface(s, runwayId)).ToList();
        Assert.Empty(runwaySurfaceSegments);
        Assert.DoesNotContain("L1", route.ToSummary(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("L11", route.ToSummary(), StringComparison.OrdinalIgnoreCase);
        Assert.True(
            destinations[0].TargetName == runwayId,
            $"Expected destination runway {runwayId}. Hold-shorts: [{holdShorts}], route: [{routeSummary}]"
        );
    }

    private static bool SegmentTraversesRunwaySurface(TaxiRouteSegment segment, string runwayId) =>
        (segment.Edge.Edge.IsRunwayCenterline) && (segment.Edge.Edge.MatchesRunway(runwayId));

    private static AircraftState MakeGroundAircraftAtNode(AirportGroundLayout layout, int nodeId)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "E75L",
            Position = layout.Nodes[nodeId].Position,
            TrueHeading = new TrueHeading(ResolveNodeHeading(layout, nodeId)),
            Altitude = 9,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "MIA", Destination = "MYAM" },
        };

        return aircraft;
    }

    private static void MoveAircraftToNode(AircraftState aircraft, AirportGroundLayout layout, int nodeId)
    {
        aircraft.Position = layout.Nodes[nodeId].Position;
        aircraft.TrueHeading = new TrueHeading(ResolveNodeHeading(layout, nodeId));
        aircraft.IndicatedAirspeed = 0;
        aircraft.IsOnGround = true;
        aircraft.Ground.AssignedTaxiRoute = null;
        aircraft.Ground.CurrentTaxiway = null;
    }

    private static double ResolveNodeHeading(AirportGroundLayout layout, int nodeId)
    {
        var node = layout.Nodes[nodeId];
        foreach (var edge in node.Edges)
        {
            if (edge.IsRunwayCenterline || edge.IsRamp)
            {
                continue;
            }

            return edge.Directed(node, edge.OtherNode(node)).DepartureBearing;
        }

        return 0;
    }

    private sealed record Issue190Recording(
        SessionRecording Recording,
        IReadOnlyDictionary<string, AirportGroundLayout> Layouts,
        AirportGroundLayout MiaLayout,
        TimedSnapshot? BeforeTaxiAutoSnapshot
    );

    private sealed class RecordingGroundData(IReadOnlyDictionary<string, AirportGroundLayout> layouts) : IAirportGroundData
    {
        private readonly TestAirportGroundData _fallback = new();

        public AirportGroundLayout? GetLayout(string airportId)
        {
            string shortId = airportId.Length == 4 && airportId[0] == 'K' ? airportId[1..] : airportId;
            return layouts.TryGetValue(shortId, out var layout) ? layout : _fallback.GetLayout(airportId);
        }

        public string? GetSourceGeoJson(string airportId)
        {
            return _fallback.GetSourceGeoJson(airportId);
        }
    }
}
