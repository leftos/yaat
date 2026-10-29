using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A no-destination route's end hold never leaves the aircraft on the runway it is taxiing off: OAK N655EX (C210) is given
/// <c>TAXI G D J</c> on the 28R rollout (the issue-207 fixture). The cut before J is never moved back to the 28R bar on G the
/// route leaves the runway through, so D stays in the route, the aircraft holds short of J with its tail clear of the 28R
/// hold line, and the RPO's advisory names J.
/// </summary>
public class NoDestinationEndHoldRunwayTailTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/19801dcd5dc2.zip";
    private const string Callsign = "N655EX";

    /// <summary>The taxiway intersection node on G just north of runway 28R that the tail is measured from.</summary>
    private const int GIntersectionNorthOf28RNodeId = 361;

    /// <summary>The crossing rule's margin past the hold line on top of half a length (RunwayTailClearance.MarginFt).</summary>
    private const double TailMarginFt = 10.0;

    private const double StoppedKts = 0.5;

    [Fact]
    public void NoDestinationEndHold_SetbackReachingBackOverARunway_HoldsWithTailClearOfTheRunway()
    {
        SessionRecording? recording = RecordingLoader.Load(RecordingPath);
        TestVnasData.EnsureInitialized();
        if ((recording is null) || (TestVnasData.NavigationDb is null))
        {
            return;
        }

        var engine = new SimulationEngine(
            RecordingGroundData.Evergreen("no-destination end hold on today's OAK geometry"),
            PushPlanStore.CreateShipped()
        );
        engine.Replay(recording, 2221);
        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(engine.ResolveGroundLayout(aircraft));

        CommandResult result = engine.SendCommand(Callsign, "TAXI G D J");
        output.WriteLine($"TAXI G D J: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"segments: {string.Join(" ", route.Segments.Select(s => $"{s.FromNodeId}->{s.ToNodeId}({s.TaxiwayName})"))}");
        output.WriteLine($"holds: {string.Join(" ", route.HoldShortPoints.Select(h => $"{h.NodeId}:{h.Reason}:{h.TargetName}:{h.IsCleared}"))}");
        output.WriteLine($"warnings: {string.Join(" | ", route.Warnings)}");
        Assert.Contains(route.Segments, s => string.Equals(s.TaxiwayName, "D", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            route.Warnings,
            w =>
                w.StartsWith("holding short of J: ", StringComparison.Ordinal)
                || w.StartsWith("holding short of TWY J — wingtip clearance from ", StringComparison.Ordinal)
        );

        HoldingShortPhase hold = TickToHold(engine, ref aircraft);
        Assert.Equal("J", hold.HoldShort.TargetName);

        GroundNode bar = layout.Nodes[GIntersectionNorthOf28RNodeId];
        double lengthFt = AircraftLength.ResolveFt(aircraft.AircraftType);
        double pastBarFt = GeoMath.DistanceNm(bar.Position, aircraft.Position) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"held at ({aircraft.Position.Lat:F6}, {aircraft.Position.Lon:F6}), {pastBarFt:F0} ft from node {GIntersectionNorthOf28RNodeId}, length {lengthFt:F0} ft"
        );
        Assert.True(aircraft.Position.Lat > bar.Position.Lat, $"{Callsign} holds on the runway side of the 28R bar on G");
        Assert.True(
            pastBarFt >= (lengthFt / 2.0) + TailMarginFt - 0.5,
            $"{Callsign}'s centre is {pastBarFt:F0} ft past the 28R bar on G; its tail needs {(lengthFt / 2.0) + TailMarginFt:F0} ft"
        );
    }

    /// <summary>
    /// A B738 just off a runway at OAK, facing the runway's hold-short bar on the taxiway it leaves by, given a bare taxi
    /// along that taxiway to the next one off it, which lies closer past the bar than the length setback reaches back: the
    /// end hold never leaves the tail over the runway's hold line. The aircraft drives on until its tail is
    /// <see cref="RunwayTailClearance.MarginFt"/> past the bar and holds there, short of the taxiway, and the route
    /// warns that wingtip clearance from that taxiway is not assured.
    /// </summary>
    [Theory]
    [InlineData("28R", "B", "A")]
    public void NoDestinationEndHold_LengthSetbackReachingBackOverARunwayBar_HoldsWithTailClearOfTheBar(string runway, string along, string onto)
    {
        const string AircraftType = "B738";
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = RecordingGroundData.Evergreen("a no-destination end hold's tail clear of a runway bar on today's OAK");
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return;
        }

        // The runway's bar on the taxiway nearest the next taxiway off it, and the first node past that bar toward the runway
        // (the runway's other bar on the taxiway lies across it): the aircraft stands between the runway and the bar.
        GroundNode bar = GroundNodeLookup.HoldShortBar(layout, runway, along, GroundNodeLookup.Junction(layout, along, onto));
        GroundNode acrossBar = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, runway, along).Single(node => node.Id != bar.Id);
        GroundNode start = GroundNodeLookup.OnTaxiway(layout, along, bar, 1.0, acrossBar);
        int barNodeId = bar.Id;
        output.WriteLine($"bar #{bar.Id}, start #{start.Id}, {GeoMath.DistanceNm(start.Position, bar.Position) * GeoMath.FeetPerNm:F1} ft apart");
        Assert.Equal(GroundNodeType.RunwayHoldShort, bar.Type);
        SimulationEngine engine = BareTaxiFirstStepHeadingTests.BuildOakEngine(groundData, AutoCrossRunwayMode.All);
        AircraftState aircraft = BareTaxiFirstStepHeadingTests.SpawnAt(
            layout,
            start.Position,
            new TrueHeading(GeoMath.BearingTo(start.Position, bar.Position)),
            new HoldingInPositionPhase()
        );
        aircraft.AircraftType = AircraftType;
        aircraft.Ground.CurrentTaxiway = along;
        engine.World.AddAircraft(aircraft);

        string command = $"TAXI {along} {onto}";
        CommandResult result = engine.SendCommand(aircraft.Callsign, command);
        output.WriteLine($"{command}: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        Assert.Contains(route.Segments, s => s.ToNodeId == barNodeId);
        HoldShortPoint endHold = Assert.Single(route.HoldShortPoints, h => h.EndsNoDestinationRoute && (h.TargetName == onto));

        // The band the tail-clearance placement covers: a gap from the bar to the hold node of at least the fuselage length
        // (shorter keeps the nose-at-node stop) and inside the length + 30 ft setback that finds the crossed bar.
        double lengthFt = AircraftLength.ResolveFt(AircraftType);
        int barSegment = route.Segments.FindIndex(s => s.ToNodeId == barNodeId);
        int holdSegment = route.Segments.FindIndex(barSegment + 1, s => s.ToNodeId == endHold.NodeId);
        Assert.True(holdSegment > barSegment, $"the end hold #{endHold.NodeId} is not past bar {barNodeId} on the route");
        double gapFt = route
            .Segments.Skip(barSegment + 1)
            .Take(holdSegment - barSegment)
            .Sum(s => GeoMath.DistanceNm(s.Edge.FromNode.Position, s.Edge.ToNode.Position) * GeoMath.FeetPerNm);
        output.WriteLine($"gap bar {barNodeId} → hold #{endHold.NodeId}: {gapFt:F1} ft along the route; length {lengthFt:F0} ft");
        Assert.True(
            (gapFt >= lengthFt) && (gapFt < lengthFt + HoldShortAnnotator.TaxiwayHoldShortBufferFt),
            $"the gap from bar {barNodeId} to the end hold is {gapFt:F1} ft, outside [{lengthFt:F0}, {lengthFt + HoldShortAnnotator.TaxiwayHoldShortBufferFt:F0}) ft"
        );

        int held = SfoGroundHarness.TickUntil(
            engine,
            () =>
                (aircraft.Phases?.CurrentPhase is HoldingShortPhase hold)
                && (hold.HoldShort.TargetName == onto)
                && (aircraft.GroundSpeed < StoppedKts),
            240,
            null
        );
        Assert.True(held > 0, $"not at rest holding short of {onto} after 240 s: {aircraft.Phases?.CurrentPhase?.Name}");

        double halfFt = AircraftLength.ResolveFt(AircraftType) / 2.0;
        double centrePastBarFt = GeoMath.DistanceNm(aircraft.Position, bar.Position) * GeoMath.FeetPerNm;
        bool pastTheBar = GeoMath.DistanceNm(aircraft.Position, start.Position) > GeoMath.DistanceNm(bar.Position, start.Position);
        double tailPastBarFt = pastTheBar ? centrePastBarFt - halfFt : -(centrePastBarFt + halfFt);
        output.WriteLine(
            $"held {centrePastBarFt:F1} ft from bar {barNodeId} ({(pastTheBar ? "past" : "behind")} it); tail {tailPastBarFt:F1} ft past it"
        );
        output.WriteLine($"warnings: {string.Join(" | ", route.Warnings)}");
        // Accepted side effect (AIM 4-3-21.b): for a gap from L to L+10 past the bar, the nose may sit past the hold-short node.
        Assert.True(
            tailPastBarFt >= RunwayTailClearance.MarginFt - TailToleranceFt,
            $"the {AircraftType}'s tail ends {tailPastBarFt:F1} ft past runway bar {barNodeId}, inside {RunwayTailClearance.MarginFt:F0} ft"
        );
        Assert.Contains(route.Warnings, w => w.StartsWith($"holding short of TWY {onto} — wingtip clearance from ", StringComparison.Ordinal));
    }

    /// <summary>
    /// How far (ft) short of the margin the tail may end: the navigator's braking curve reaches zero up to
    /// <see cref="GroundNavigator.SetBackStopMarginFt"/> short of a set-back stop.
    /// </summary>
    private const double TailToleranceFt = GroundNavigator.SetBackStopMarginFt;

    private static HoldingShortPhase TickToHold(SimulationEngine engine, ref AircraftState aircraft)
    {
        for (int t = 1; t <= 240; t++)
        {
            engine.TickOneSecond();
            aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
            if ((aircraft.Phases?.CurrentPhase is HoldingShortPhase hold) && (aircraft.GroundSpeed < StoppedKts))
            {
                return hold;
            }
        }

        Assert.Fail($"{Callsign} never came to a stop holding short; phase {aircraft.Phases?.CurrentPhase?.GetType().Name}");
        throw new InvalidOperationException("unreachable");
    }
}
