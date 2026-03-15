using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public class VisualApproachCommandTests
{
    public VisualApproachCommandTests()
    {
        NavigationDatabase.SetInstance(MakeRunwayLookup());
    }

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
            Heading = heading,
            Track = heading,
            Altitude = altitude,
            IndicatedAirspeed = 180,
            Latitude = lat,
            Longitude = lon,
            Destination = destination,
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
        var cmd = new ClearedVisualApproachCommand("28R", null, null, "UAL456");
        var result = ApproachCommandHandler.TryClearedVisualApproach(cmd, aircraft);

        Assert.True(result.Success);
        Assert.Equal("UAL456", aircraft.FollowingCallsign);
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
        aircraft.HasReportedFieldInSight = true;
        aircraft.HasReportedTrafficInSight = true;
        aircraft.FollowingCallsign = "OLD123";

        var cmd = new ClearedVisualApproachCommand("28R", null, null, null);
        ApproachCommandHandler.TryClearedVisualApproach(cmd, aircraft);

        Assert.False(aircraft.HasReportedFieldInSight);
        Assert.False(aircraft.HasReportedTrafficInSight);
        Assert.Null(aircraft.FollowingCallsign);
    }

    // -------------------------------------------------------------------------
    // RFIS / RTIS commands
    // -------------------------------------------------------------------------

    [Fact]
    public void ReportFieldInSight_WhenFieldSeen_AddsNotification()
    {
        var aircraft = MakeAircraft();
        aircraft.HasReportedFieldInSight = true;

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), aircraft, null, Random.Shared, true);
        Assert.True(result.Success);
        Assert.Single(aircraft.PendingNotifications);
        Assert.Contains("field in sight", aircraft.PendingNotifications[0]);
    }

    [Fact]
    public void ReportFieldInSight_WhenNotSeen_Fails()
    {
        var aircraft = MakeAircraft();
        aircraft.HasReportedFieldInSight = false;

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), aircraft, null, Random.Shared, true);
        Assert.False(result.Success);
    }

    [Fact]
    public void ReportTrafficInSight_WhenTrafficSeen_AddsNotification()
    {
        var aircraft = MakeAircraft();
        aircraft.HasReportedTrafficInSight = true;

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("UAL456"), aircraft, null, Random.Shared, true);
        Assert.True(result.Success);
        Assert.Single(aircraft.PendingNotifications);
        Assert.Contains("traffic in sight", aircraft.PendingNotifications[0]);
    }

    [Fact]
    public void ReportTrafficInSight_WhenNotSeen_Fails()
    {
        var aircraft = MakeAircraft();
        aircraft.HasReportedTrafficInSight = false;

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand(null), aircraft, null, Random.Shared, true);
        Assert.False(result.Success);
    }
}
