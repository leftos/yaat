using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #1356 (YAAT-1327): N7LJ (LJ45, preset <c>TAXI D C B 28R</c>, S2-OAK-3 VFR Sequencing) holds short of its
/// destination runway 28R on B, is told <c>CROSS</c>, and crosses to the far (south) 28R bar on B. The crossing route the
/// CROSS builds ends at that bar's node, so the crossing's half-length tail clearance has no onward segment to drive and
/// the aircraft stops with its centre on the bar: tail 28 ft inside the holding position marking, in
/// <see cref="HoldingInPositionPhase"/>, where RES, CROSS and BREAK cannot move it. An aircraft is not clear of a runway
/// until every part of it has crossed the holding position marking (AIM 2-3-5.a.1, AIM 4-3-21.b).
/// </summary>
public sealed class Issue1356CrossDestinationRunwayEndsClearTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue1356-oak-n7lj-cross-far-bar-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N7LJ";
    private const string Runway = "28R";

    /// <summary>The reported case: the recorded bare CROSS at t=924, given at the destination-runway bar the route completed at.</summary>
    [Fact]
    public void CrossAtDestinationRunwayBar_EndsWithTheTailPastTheFarHoldLine()
    {
        if (Restore(920) is not { } restored)
        {
            return;
        }

        using RecordingArchive archive = restored.Archive;
        AircraftState aircraft = Assert.IsType<AircraftState>(restored.Engine.FindAircraft(Callsign));
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal(HoldShortReason.DestinationRunway, hold.HoldShort.Reason);
        Assert.True(aircraft.Ground.AssignedTaxiRoute?.IsComplete, "Precondition: the route completed at the 28R bar");
        GroundNode nearBar = restored.Layout.Nodes[hold.HoldShort.NodeId];

        restored.Engine.ReplayRange(920, 969, restored.Recording.Actions);

        AssertClearOfFarBar(restored.Engine, restored.Layout, nearBar);
    }

    /// <summary>The same crossing ordered while still taxiing toward the destination-runway bar (TryExtendRouteAcrossDestinationRunway).</summary>
    [Fact]
    public void CrossWhileTaxiingTowardDestinationRunway_EndsWithTheTailPastTheFarHoldLine()
    {
        if (Restore(880) is not { } restored)
        {
            return;
        }

        using RecordingArchive archive = restored.Archive;
        AircraftState aircraft = Assert.IsType<AircraftState>(restored.Engine.FindAircraft(Callsign));
        Assert.IsType<TaxiingPhase>(aircraft.Phases?.CurrentPhase);
        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        HoldShortPoint destinationBar = Assert.Single(route.HoldShortPoints, static hs => hs.Reason == HoldShortReason.DestinationRunway);
        GroundNode nearBar = restored.Layout.Nodes[destinationBar.NodeId];

        Assert.True(restored.Engine.SendCommand(Callsign, $"CROSS {Runway}").Success);
        for (int t = 0; (t < 90) && (aircraft.Phases?.CurrentPhase is not HoldingInPositionPhase); t++)
        {
            restored.Engine.TickOneSecond();
        }

        AssertClearOfFarBar(restored.Engine, restored.Layout, nearBar);
    }

    private void AssertClearOfFarBar(SimulationEngine engine, AirportGroundLayout layout, GroundNode nearBar)
    {
        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
        Assert.IsType<HoldingInPositionPhase>(aircraft.Phases?.CurrentPhase);

        GroundRunway runway = layout.Runways.Single(static r => r.Id.Contains(Runway));
        var threshold = new LatLon(runway.Coordinates[0].Lat, runway.Coordinates[0].Lon);
        var centreline = new TrueHeading(GeoMath.BearingTo(threshold, new LatLon(runway.Coordinates[^1].Lat, runway.Coordinates[^1].Lon)));
        double CrossTrackFt(LatLon p) => GeoMath.SignedCrossTrackDistanceNm(p, threshold, centreline) * GeoMath.FeetPerNm;

        double nearSide = Math.Sign(CrossTrackFt(nearBar.Position));
        GroundNode? farBar = layout
            .GetRunwayHoldShortNodes(Runway)
            .Where(n => Math.Sign(CrossTrackFt(n.Position)) == -nearSide)
            .MinBy(n => GeoMath.DistanceNm(n.Position, aircraft.Position));
        Assert.NotNull(farBar);
        double holdLineFt = Math.Abs(CrossTrackFt(farBar.Position));

        double halfLengthNm = AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0 / GeoMath.FeetPerNm;
        var backwards = new TrueHeading((aircraft.TrueHeading.Degrees + 180.0) % 360.0);
        double tailFt = Math.Abs(CrossTrackFt(GeoMath.ProjectPoint(aircraft.Position, backwards, halfLengthNm)));
        output.WriteLine($"far bar #{farBar.Id} hold line {holdLineFt:F1} ft from the {Runway} centreline; tail {tailFt:F1} ft");

        Assert.True(
            tailFt >= holdLineFt + RunwayCrossingFarSideGate.MarginFt - 1.0,
            $"{Callsign} ended its crossing of {Runway} with its tail {tailFt:F1} ft from the centreline, "
                + $"short of the far hold line at {holdLineFt:F1} ft plus the margin"
        );
    }

    private (SimulationEngine Engine, AirportGroundLayout Layout, RecordingArchive Archive, SessionRecording Recording)? Restore(double seconds)
    {
        TestVnasData.EnsureInitialized();
        if ((RecordingLoader.OpenArchive(RecordingPath) is not { } archive) || (archive.ReadSnapshotAt(seconds) is not { } snapshot))
        {
            return null;
        }

        var groundData = RecordingGroundData.VersionPinned(RecordingPath, "N7LJ's 28R bars on B are the recorded layout's nodes");
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(groundData.GetLayout("oak"));
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        SessionRecording recording = archive.ToBaseSessionRecording();
        engine.Replay(recording, 0);
        engine.RestoreFromSnapshot(snapshot.State);
        return (engine, layout, archive, recording);
    }
}
