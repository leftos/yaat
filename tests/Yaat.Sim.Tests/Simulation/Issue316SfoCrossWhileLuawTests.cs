using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// GitHub issue #316: SKW5237 taxied across an occupied runway 28L.
///
/// Recording: S2-SFO-4 "Shared Dep/Arr on Parallel Runways". SKW5590 and SKW5237 were both queued
/// for 28L at the Foxtrot hold-short bar. At t=523 SKW5590 was given <c>LUAW</c> and lined up on 28L.
/// At t=536, with SKW5590 stopped on the runway, SKW5237 (now number 1) was re-routed to the other
/// parallel with <c>TAXI F C HS 10R RWY 28R</c>. It drove straight over 28L — passing about 57 m
/// behind SKW5590's tail — and only stopped once it reached the hold-short bar on the far (Charlie)
/// side. The controller's <c>RES</c> at t=588 is a separate, deliberate movement and is not the bug.
///
/// The hold-short must bind to the Foxtrot bar the aircraft was already parked on, so the aircraft
/// never enters 28L before it is cleared to cross.
/// </summary>
public class Issue316SfoCrossWhileLuawTests(ITestOutputHelper output)
{
    // Same session as issue #315 — two different bugs inside the same 90 seconds, so one fixture serves both.
    private const string RecordingPath = "TestData/issue315-luaw-after-crossing-recording.zip";

    /// <summary>Route re-issued at t=536; the recorded RES that authorises the crossing lands at t=588.</summary>
    private const int RerouteSeconds = 537;
    private const int LastSecondBeforeResume = 585;

    /// <summary>SKW3473 gets the same F→C re-route at t=768, but with no HS token.</summary>
    private const int ImplicitCrossSeconds = 772;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Perpendicular distance from the 10R/28L centerline in feet. Anything below half the runway
    /// width means the aircraft is physically on the runway surface.
    /// </summary>
    private static double DistanceFromRunwayCenterlineFt(AircraftState aircraft, GroundRunway runway)
    {
        var start = runway.Coordinates[0];
        var end = runway.Coordinates[^1];
        var heading = new TrueHeading(GeoMath.BearingTo(start.Lat, start.Lon, end.Lat, end.Lon));
        double crossTrackNm = GeoMath.SignedCrossTrackDistanceNm(aircraft.Position.Lat, aircraft.Position.Lon, start.Lat, start.Lon, heading);
        return Math.Abs(crossTrackNm) * GeoMath.FeetPerNm;
    }

    [Fact]
    public void Skw5237_ReroutedToTheOtherParallel_HoldsShortOfTheOccupiedRunway()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Full replay from t=0: the fix only changes hold-short side selection for a route that
        // begins at a runway hold-short bar, which first happens here at t=536. Everything before
        // that reaches the buggy moment exactly as the controller saw it.
        engine.Replay(recording, RerouteSeconds);

        var aircraft = engine.FindAircraft("SKW5237");
        Assert.NotNull(aircraft);

        var layout = aircraft.Ground.Layout;
        Assert.NotNull(layout);
        var runway28L = layout.FindRunway("28L");
        Assert.NotNull(runway28L);

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        output.WriteLine($"t={RerouteSeconds}: route={route.ToSummary()}");
        foreach (var point in route.HoldShortPoints)
        {
            output.WriteLine($"  hold-short #{point.NodeId} {point.TargetName} {point.Reason} cleared={point.IsCleared}");
        }

        var nearBar = TestLayoutNodes.RunwayHoldShortOnTaxiway(layout, "10R", "F");
        var farBar = TestLayoutNodes.RunwayHoldShortOnTaxiway(layout, "10R", "C");
        Assert.NotNull(nearBar);
        Assert.NotNull(farBar);

        var holdShort10R = Assert.Single(
            route.HoldShortPoints,
            point => (point.TargetName is not null) && RunwayIdentifier.Parse(point.TargetName).Contains("10R")
        );
        Assert.Equal(HoldShortReason.ExplicitHoldShort, holdShort10R.Reason);
        Assert.Equal(nearBar.Id, holdShort10R.NodeId);

        // SKW5590 is stopped on 28L for this whole window.
        var lineUp = engine.FindAircraft("SKW5590");
        Assert.NotNull(lineUp);
        Assert.True(DistanceFromRunwayCenterlineFt(lineUp, runway28L) < (runway28L.WidthFt / 2), "SKW5590 should be on 28L at the reroute");

        double halfWidthFt = runway28L.WidthFt / 2;
        for (int t = RerouteSeconds + 1; t <= LastSecondBeforeResume; t++)
        {
            engine.ReplayOneSecond();
            aircraft = engine.FindAircraft("SKW5237");
            Assert.NotNull(aircraft);

            double offCenterlineFt = DistanceFromRunwayCenterlineFt(aircraft, runway28L);
            if (t % 5 == 0)
            {
                output.WriteLine(
                    $"t={t}: phase={aircraft.Phases?.CurrentPhase?.GetType().Name} gs={aircraft.GroundSpeed:F0}"
                        + $" offCenterline28L={offCenterlineFt:F0}ft"
                );
                NearestNodeHelper.Log(output, $"t={t}:", aircraft, layout);
            }

            Assert.True(
                offCenterlineFt > halfWidthFt,
                $"t={t}: SKW5237 entered runway 28L ({offCenterlineFt:F0} ft from centerline, half-width {halfWidthFt:F0} ft)"
                    + " while SKW5590 was lined up on it and no crossing clearance had been issued"
            );
        }

        // Still parked at the Foxtrot bar, waiting for a crossing clearance.
        var holding = Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal("10R/28L", holding.HoldShort.TargetName);
        Assert.Equal(nearBar.Id, holding.HoldShort.NodeId);
    }

    [Fact]
    public void Skw3473_SameRerouteWithoutHoldShort_StillCrossesOnTheTaxiClearance()
    {
        // The same F→C re-route off the 28L bar, issued as "TAXI F C 28R" with no HS token, must keep
        // working: GroundCommandHandler's implicit first-crossing clearance authorises the crossing
        // the aircraft is already holding short of. That path only ever clears a RunwayCrossing point,
        // so moving the point to the near-side bar must not break it.
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, ImplicitCrossSeconds);

        var aircraft = engine.FindAircraft("SKW3473");
        Assert.NotNull(aircraft);

        var layout = aircraft.Ground.Layout;
        Assert.NotNull(layout);

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        output.WriteLine($"t={ImplicitCrossSeconds}: route={route.ToSummary()}");
        foreach (var point in route.HoldShortPoints)
        {
            output.WriteLine($"  hold-short #{point.NodeId} {point.TargetName} {point.Reason} cleared={point.IsCleared}");
        }

        var nearBar = TestLayoutNodes.RunwayHoldShortOnTaxiway(layout, "10R", "F");
        Assert.NotNull(nearBar);

        var crossing = Assert.Single(
            route.HoldShortPoints,
            point => (point.TargetName is not null) && RunwayIdentifier.Parse(point.TargetName).Contains("10R")
        );
        Assert.Equal(HoldShortReason.RunwayCrossing, crossing.Reason);
        Assert.Equal(nearBar.Id, crossing.NodeId);
        Assert.True(crossing.IsCleared, "the TAXI itself authorises the crossing the aircraft is already holding short of");
    }
}
