using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

[Collection("NavDbMutator")]
public class VisualApproachCommandTests : IDisposable
{
    private readonly IDisposable _navDbScope;

    public VisualApproachCommandTests()
    {
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(MakeRunwayLookup());
    }

    public void Dispose() => _navDbScope.Dispose();

    // Runway 28R at OAK: heading 280°, threshold at (37.72, -122.22)
    private static RunwayInfo MakeRunway(string designator = "28R", double heading = 280)
    {
        return TestRunwayFactory.Make(
            designator: designator,
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            endLat: 37.73,
            endLon: -122.27,
            heading: heading,
            elevationFt: 9
        );
    }

    private static AircraftState MakeAircraft(
        double heading = 280,
        double altitude = 3000,
        double lat = 37.75,
        double lon = -122.35,
        string destination = "OAK"
    )
    {
        return new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = altitude,
            IndicatedAirspeed = 180,
            Position = new LatLon(lat, lon),
            FlightPlan = new AircraftFlightPlan { Destination = destination },
        };
    }

    private static NavigationDatabase MakeRunwayLookup(RunwayInfo? runway = null)
    {
        runway ??= MakeRunway();
        return TestNavDbFactory.WithRunways(runway);
    }

    // -------------------------------------------------------------------------
    // Straight-in (angle off ≤ 30°)
    // -------------------------------------------------------------------------

    [Fact]
    public void Cva_StraightIn_CreatesFinalAndLanding()
    {
        // Aircraft heading ~280 toward runway heading 280 → angle off = 0°
        var aircraft = MakeAircraft(heading: 280);
        var cmd = new ClearedVisualApproachCommand("28R", null, null, null);
        var result = ApproachCommandHandler.TryClearedVisualApproach(cmd, aircraft);

        Assert.True(result.Success);
        Assert.NotNull(aircraft.Phases);
        Assert.Contains("visual approach", result.Message);
        Assert.Equal("VIS28R", aircraft.Phases.ActiveApproach!.ApproachId);

        // Should have FinalApproachPhase + LandingPhase
        var phases = aircraft.Phases.Phases.Where(p => p.Status is PhaseStatus.Active or PhaseStatus.Pending).ToList();
        Assert.Contains(phases, p => p is FinalApproachPhase);
        Assert.Contains(phases, p => p is LandingPhase);
        Assert.DoesNotContain(phases, p => p is DownwindPhase);
    }

    // -------------------------------------------------------------------------
    // Angled join (30°–90° off)
    // -------------------------------------------------------------------------

    [Fact]
    public void Cva_AngledJoin_CreatesNavigationAndFinal()
    {
        // Aircraft heading 220° toward runway heading 280° → 60° off
        var aircraft = MakeAircraft(heading: 220);
        var cmd = new ClearedVisualApproachCommand("28R", null, null, null);
        var result = ApproachCommandHandler.TryClearedVisualApproach(cmd, aircraft);

        Assert.True(result.Success);
        var phases = aircraft.Phases!.Phases.Where(p => p.Status is PhaseStatus.Active or PhaseStatus.Pending).ToList();
        Assert.Contains(phases, p => p is ApproachNavigationPhase);
        Assert.Contains(phases, p => p is FinalApproachPhase);
    }

    // -------------------------------------------------------------------------
    // Pattern entry (> 90° off)
    // -------------------------------------------------------------------------

    [Fact]
    public void Cva_PatternEntry_CreatesDownwindBasePattern()
    {
        // Aircraft heading 100° toward runway heading 280° → 180° off
        var aircraft = MakeAircraft(heading: 100);
        var cmd = new ClearedVisualApproachCommand("28R", null, null, null);
        var result = ApproachCommandHandler.TryClearedVisualApproach(cmd, aircraft);

        Assert.True(result.Success);
        var phases = aircraft.Phases!.Phases.Where(p => p.Status is PhaseStatus.Active or PhaseStatus.Pending).ToList();
        Assert.Contains(phases, p => p is DownwindPhase);
        Assert.Contains(phases, p => p is BasePhase);
        Assert.Contains(phases, p => p is FinalApproachPhase);
    }

    // -------------------------------------------------------------------------
    // FOLLOW variant
    // -------------------------------------------------------------------------

    [Fact]
    public void Cva_WithFollow_SetsFollowingCallsign()
    {
        var aircraft = MakeAircraft(heading: 280);
        aircraft.Approach.HasReportedTrafficInSight = true;
        var cmd = new ClearedVisualApproachCommand("28R", null, null, "UAL456");
        var result = ApproachCommandHandler.TryClearedVisualApproach(cmd, aircraft);

        Assert.True(result.Success);
        Assert.Equal("UAL456", aircraft.Approach.FollowingCallsign);
        Assert.Contains("follow UAL456", result.Message);
    }

    // -------------------------------------------------------------------------
    // Traffic direction override
    // -------------------------------------------------------------------------

    [Fact]
    public void Cva_PatternEntry_RespectsTrafficDirection()
    {
        var aircraft = MakeAircraft(heading: 100);
        var cmd = new ClearedVisualApproachCommand("28R", null, PatternDirection.Right, null);
        var result = ApproachCommandHandler.TryClearedVisualApproach(cmd, aircraft);

        Assert.True(result.Success);
    }

    // -------------------------------------------------------------------------
    // Error: VFR aircraft (CVA is for IFR only)
    // -------------------------------------------------------------------------

    [Fact]
    public void Cva_VfrAircraft_Rejected()
    {
        var aircraft = MakeAircraft();
        aircraft.FlightPlan.FlightRules = "VFR";
        var cmd = new ClearedVisualApproachCommand("28R", null, null, null);
        var result = ApproachCommandHandler.TryClearedVisualApproach(cmd, aircraft);

        Assert.False(result.Success);
        Assert.Contains("IFR", result.Message);
    }

    // -------------------------------------------------------------------------
    // Error: unknown runway
    // -------------------------------------------------------------------------

    [Fact]
    public void Cva_UnknownRunway_Fails()
    {
        var aircraft = MakeAircraft();
        var cmd = new ClearedVisualApproachCommand("99L", null, null, null);
        var result = ApproachCommandHandler.TryClearedVisualApproach(cmd, aircraft);

        Assert.False(result.Success);
        Assert.Contains("Unknown runway", result.Message);
    }

    // -------------------------------------------------------------------------
    // Speed restrictions cancelled
    // -------------------------------------------------------------------------

    [Fact]
    public void Cva_CancelsExplicitSpeedRestriction()
    {
        // FinalApproachPhase sets its own speed target on start, so we verify
        // the explicit 210kt assignment is gone (replaced by phase speed, not 210)
        var aircraft = MakeAircraft(heading: 280);
        aircraft.Targets.TargetSpeed = 210;
        var cmd = new ClearedVisualApproachCommand("28R", null, null, null);
        ApproachCommandHandler.TryClearedVisualApproach(cmd, aircraft);

        Assert.NotEqual(210, aircraft.Targets.TargetSpeed);
    }

    // -------------------------------------------------------------------------
    // Visual state cleared on new approach
    // -------------------------------------------------------------------------

    [Fact]
    public void Cva_ClearsPreviousVisualState()
    {
        var aircraft = MakeAircraft(heading: 280);
        aircraft.Approach.HasReportedFieldInSight = true;
        aircraft.Approach.HasReportedTrafficInSight = true;
        aircraft.Approach.FollowingCallsign = "OLD123";

        var cmd = new ClearedVisualApproachCommand("28R", null, null, null);
        ApproachCommandHandler.TryClearedVisualApproach(cmd, aircraft);

        Assert.False(aircraft.Approach.HasReportedFieldInSight);
        Assert.False(aircraft.Approach.HasReportedTrafficInSight);
        Assert.Null(aircraft.Approach.FollowingCallsign);
    }

    // -------------------------------------------------------------------------
    // RFIS / RTIS commands
    // -------------------------------------------------------------------------

    [Fact]
    public void ReportFieldInSight_WhenFieldSeen_AddsReadback()
    {
        var aircraft = MakeAircraft();
        aircraft.Approach.HasReportedFieldInSight = true;

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), aircraft, TestDispatch.Context(Random.Shared));
        Assert.True(result.Success);
        // Field acquisition routes through PendingPilotReadbacks (SAY channel) — gates the visual approach.
        Assert.Equal("field in sight.", Assert.Single(aircraft.PendingPilotReadbacks));
    }

    // Soft-fail behavior (visual reasons) and hard-fail (no destination / not in
    // nav database) are exhaustively covered in RfisSoftFailLookingTests and
    // ReportInSightTests using a real NavDb. The scoped runway-only NavDb used
    // here cannot exercise the airport visual-acquisition path.

    [Fact]
    public void ReportTrafficInSight_WhenTrafficSeen_AddsReadback()
    {
        var aircraft = MakeAircraft();
        aircraft.Approach.HasReportedTrafficInSight = true;

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("UAL456"), aircraft, TestDispatch.Context(Random.Shared));
        Assert.True(result.Success);
        // Traffic acquisition routes through PendingPilotReadbacks (SAY channel).
        Assert.Equal("traffic (UAL456) in sight.", Assert.Single(aircraft.PendingPilotReadbacks));
    }

    [Fact]
    public void ReportTrafficInSight_WhenNotSeen_Fails()
    {
        var aircraft = MakeAircraft();
        aircraft.Approach.HasReportedTrafficInSight = false;

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand(null), aircraft, TestDispatch.Context(Random.Shared));
        Assert.False(result.Success);
    }
}
