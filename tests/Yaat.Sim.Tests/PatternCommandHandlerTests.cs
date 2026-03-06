using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;

namespace Yaat.Sim.Tests;

public class PatternCommandHandlerTests
{
    private static readonly ILogger Logger = new NullLogger<PatternCommandHandlerTests>();

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static RunwayInfo DefaultRunway() => TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 100);

    private static AircraftState MakeAircraft(
        double lat = 37.0,
        double lon = -122.0,
        double altitude = 1100,
        double groundSpeed = 200,
        bool onGround = false
    )
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Latitude = lat,
            Longitude = lon,
            Heading = 280,
            Altitude = altitude,
            GroundSpeed = groundSpeed,
            IndicatedAirspeed = groundSpeed,
            IsOnGround = onGround,
            Departure = "TEST",
        };
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };
        return ac;
    }

    // -------------------------------------------------------------------------
    // TryEnterPattern — wrong-side detection
    // -------------------------------------------------------------------------

    [Fact]
    public void TryEnterPattern_WrongSideDownwind_InsertsMidfieldCrossing()
    {
        var rwy = DefaultRunway();
        // Left pattern: crosswind heading = 280-90 = 190. Pattern is south of runway.
        // Put aircraft NORTH of threshold (wrong side for left pattern).
        var (northLat, northLon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, 10.0, 2.0);
        var ac = MakeAircraft(lat: northLat, lon: northLon, altitude: 1500);

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Downwind, Logger);

        Assert.True(result.Success);
        Assert.Contains("crossing midfield", result.Message!);
        // Should have a MidfieldCrossingPhase in the phase list
        Assert.Contains(ac.Phases!.Phases, p => p is MidfieldCrossingPhase);
    }

    [Fact]
    public void TryEnterPattern_CorrectSide_NoMidfieldCrossing()
    {
        var rwy = DefaultRunway();
        // Left pattern south of runway. Put aircraft south.
        var (southLat, southLon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, 190.0, 2.0);
        var ac = MakeAircraft(lat: southLat, lon: southLon, altitude: 1500);

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Downwind, Logger);

        Assert.True(result.Success);
        Assert.DoesNotContain("crossing midfield", result.Message!);
        Assert.DoesNotContain(ac.Phases!.Phases, p => p is MidfieldCrossingPhase);
    }

    [Fact]
    public void TryEnterPattern_WrongSideBase_InsertsMidfieldCrossing()
    {
        var rwy = DefaultRunway();
        var (northLat, northLon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, 10.0, 2.0);
        var ac = MakeAircraft(lat: northLat, lon: northLon, altitude: 1500);

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Base, Logger);

        Assert.True(result.Success);
        Assert.Contains(ac.Phases!.Phases, p => p is MidfieldCrossingPhase);
    }

    // -------------------------------------------------------------------------
    // TryEnterPattern — PatternEntryPhase for far aircraft
    // -------------------------------------------------------------------------

    [Fact]
    public void TryEnterPattern_FarAircraft_InsertsPatternEntryPhase()
    {
        var rwy = DefaultRunway();
        // Place aircraft far south (correct side for left pattern, but >1nm from entry point)
        var (farLat, farLon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, 190.0, 10.0);
        var ac = MakeAircraft(lat: farLat, lon: farLon, altitude: 3000);

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Downwind, Logger);

        Assert.True(result.Success);
        Assert.Contains(ac.Phases!.Phases, p => p is PatternEntryPhase);
    }

    [Fact]
    public void TryEnterPattern_CloseAircraft_SkipsPatternEntryPhase()
    {
        var rwy = DefaultRunway();
        // Place aircraft very close to the downwind abeam point
        var wp = PatternGeometry.Compute(rwy, AircraftCategory.Jet, PatternDirection.Left);
        var ac = MakeAircraft(lat: wp.DownwindAbeamLat, lon: wp.DownwindAbeamLon, altitude: wp.PatternAltitude);

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Downwind, Logger);

        Assert.True(result.Success);
        Assert.DoesNotContain(ac.Phases!.Phases, p => p is PatternEntryPhase);
    }

    // -------------------------------------------------------------------------
    // TryEnterPattern — no assigned runway
    // -------------------------------------------------------------------------

    [Fact]
    public void TryEnterPattern_NoRunway_Fails()
    {
        var ac = MakeAircraft();
        ac.Phases = new PhaseList(); // no runway

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Downwind, Logger);

        Assert.False(result.Success);
        Assert.Contains("No assigned runway", result.Message!);
    }

    // -------------------------------------------------------------------------
    // TryExtendPattern
    // -------------------------------------------------------------------------

    [Fact]
    public void TryExtendPattern_OnDownwind_SetsExtended()
    {
        var ac = MakeAircraft();
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left);
        var downwind = new DownwindPhase { Waypoints = wp };
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };
        ac.Phases.Add(downwind);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, NullLogger.Instance));

        var result = PatternCommandHandler.TryExtendPattern(ac);

        Assert.True(result.Success);
        Assert.True(downwind.IsExtended);
    }

    [Fact]
    public void TryExtendPattern_NotOnDownwind_Fails()
    {
        var ac = MakeAircraft();
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };
        ac.Phases.Add(new UpwindPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, NullLogger.Instance));

        var result = PatternCommandHandler.TryExtendPattern(ac);

        Assert.False(result.Success);
        Assert.Contains("downwind only", result.Message!);
    }

    // -------------------------------------------------------------------------
    // TryPatternTurnBase
    // -------------------------------------------------------------------------

    [Fact]
    public void TryPatternTurnBase_OnDownwind_AdvancesToBase()
    {
        var ac = MakeAircraft();
        var wp = PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Jet, PatternDirection.Left);
        var downwind = new DownwindPhase { Waypoints = wp };
        var basep = new BasePhase { Waypoints = wp };
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };
        ac.Phases.Add(downwind);
        ac.Phases.Add(basep);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, NullLogger.Instance));

        var result = PatternCommandHandler.TryPatternTurnBase(ac, Logger);

        Assert.True(result.Success);
        Assert.Equal(basep, ac.Phases.CurrentPhase);
    }

    [Fact]
    public void TryPatternTurnBase_NotOnDownwind_Fails()
    {
        var ac = MakeAircraft();
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };
        ac.Phases.Add(new UpwindPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, NullLogger.Instance));

        var result = PatternCommandHandler.TryPatternTurnBase(ac, Logger);

        Assert.False(result.Success);
        Assert.Contains("Not on downwind", result.Message!);
    }

    // -------------------------------------------------------------------------
    // Pattern direction inference
    // -------------------------------------------------------------------------

    [Fact]
    public void TryChangePatternDirection_SetsTrafficDirection()
    {
        var ac = MakeAircraft();

        var result = PatternCommandHandler.TryChangePatternDirection(ac, PatternDirection.Right);

        Assert.True(result.Success);
        Assert.Equal(PatternDirection.Right, ac.Phases!.TrafficDirection);
        Assert.Contains("right traffic", result.Message!);
    }

    [Fact]
    public void TryChangePatternDirection_NoRunway_Fails()
    {
        var ac = MakeAircraft();
        ac.Phases = new PhaseList(); // no runway

        var result = PatternCommandHandler.TryChangePatternDirection(ac, PatternDirection.Left);

        Assert.False(result.Success);
        Assert.Contains("No assigned runway", result.Message!);
    }
}
