using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for the OFL/OFR pattern lateral-offset command. Aircraft on
/// upwind/crosswind/downwind doglegs perpendicular to the leg, acquires a
/// parallel track offset by the requested distance, then resumes parallel
/// flight. Resets when the leg ends. Rejected on base/final.
/// </summary>
public class PatternLateralOffsetTests
{
    public PatternLateralOffsetTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static RunwayInfo DefaultRunway() => TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 100);

    private static PatternWaypoints DefaultWaypoints(PatternDirection dir = PatternDirection.Left) =>
        PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Piston, dir, null, null, null);

    private static AircraftState MakeAircraft(double lat, double lon, double headingDeg = 100)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "C172",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(headingDeg),
            Altitude = 1100,
            IndicatedAirspeed = 90,
            FlightPlan = new AircraftFlightPlan { Departure = "TEST" },
        };
        ac.Phases = new PhaseList { AssignedRunway = DefaultRunway() };
        return ac;
    }

    private static PhaseContext Ctx(AircraftState ac, double dt = 1.0)
    {
        var rwy = DefaultRunway();
        return new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = dt,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            Logger = NullLogger.Instance,
        };
    }

    // -------------------------------------------------------------------------
    // Handler-level: TryOffsetPattern
    // -------------------------------------------------------------------------

    [Fact]
    public void Handler_BareOffset_AppliesDefault05Nm_OnDownwind()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(wp.DownwindAbeamLat, wp.DownwindAbeamLon, wp.DownwindHeading.Degrees);
        var downwind = new DownwindPhase { Waypoints = wp };
        ac.Phases!.Add(downwind);
        ac.Phases.Start(Ctx(ac));

        var result = PatternCommandHandler.TryOffsetPattern(ac, TurnDirection.Right, null);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(downwind.LateralOffset);
        Assert.Equal(0.5, downwind.LateralOffset.TargetNm);
        Assert.Equal(TurnDirection.Right, downwind.LateralOffset.Direction);
        Assert.False(downwind.LateralOffset.Acquired);
    }

    [Fact]
    public void Handler_ExplicitOffset_AppliesGivenDistance()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(wp.DownwindAbeamLat, wp.DownwindAbeamLon, wp.DownwindHeading.Degrees);
        var downwind = new DownwindPhase { Waypoints = wp };
        ac.Phases!.Add(downwind);
        ac.Phases.Start(Ctx(ac));

        var result = PatternCommandHandler.TryOffsetPattern(ac, TurnDirection.Left, 0.7);

        Assert.True(result.Success, result.Message);
        Assert.Equal(0.7, downwind.LateralOffset!.TargetNm);
        Assert.Equal(TurnDirection.Left, downwind.LateralOffset.Direction);
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    [InlineData(2.0)]
    [InlineData(10.0)]
    public void Handler_OutOfRangeOffset_Rejects(double offsetNm)
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(wp.DownwindAbeamLat, wp.DownwindAbeamLon, wp.DownwindHeading.Degrees);
        var downwind = new DownwindPhase { Waypoints = wp };
        ac.Phases!.Add(downwind);
        ac.Phases.Start(Ctx(ac));

        var result = PatternCommandHandler.TryOffsetPattern(ac, TurnDirection.Right, offsetNm);

        Assert.False(result.Success);
        Assert.Null(downwind.LateralOffset);
    }

    [Fact]
    public void Handler_OnBasePhase_SetsOffsetOnBasePhase()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(wp.BaseTurnLat, wp.BaseTurnLon, wp.BaseHeading.Degrees);
        var basePhase = new BasePhase { Waypoints = wp };
        ac.Phases!.Add(basePhase);
        ac.Phases.Start(Ctx(ac));

        var result = PatternCommandHandler.TryOffsetPattern(ac, TurnDirection.Right, 0.4);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(basePhase.LateralOffset);
        Assert.Equal(0.4, basePhase.LateralOffset.TargetNm);
        Assert.Equal(TurnDirection.Right, basePhase.LateralOffset.Direction);
    }

    [Fact]
    public void Handler_NoActivePhase_Rejects()
    {
        var ac = MakeAircraft(37.0, -122.0);

        var result = PatternCommandHandler.TryOffsetPattern(ac, TurnDirection.Right, null);

        Assert.False(result.Success);
    }

    [Fact]
    public void Handler_OnUpwind_SetsOffsetOnUpwindPhase()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(wp.DepartureEndLat, wp.DepartureEndLon, wp.UpwindHeading.Degrees);
        var upwind = new UpwindPhase { Waypoints = wp };
        ac.Phases!.Add(upwind);
        ac.Phases.Start(Ctx(ac));

        var result = PatternCommandHandler.TryOffsetPattern(ac, TurnDirection.Right, 0.4);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(upwind.LateralOffset);
        Assert.Equal(0.4, upwind.LateralOffset.TargetNm);
    }

    [Fact]
    public void Handler_OnCrosswind_SetsOffsetOnCrosswindPhase()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(wp.CrosswindTurnLat, wp.CrosswindTurnLon, wp.CrosswindHeading.Degrees);
        var crosswind = new CrosswindPhase { Waypoints = wp };
        ac.Phases!.Add(crosswind);
        ac.Phases.Start(Ctx(ac));

        var result = PatternCommandHandler.TryOffsetPattern(ac, TurnDirection.Left, 0.6);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(crosswind.LateralOffset);
        Assert.Equal(0.6, crosswind.LateralOffset.TargetNm);
        Assert.Equal(TurnDirection.Left, crosswind.LateralOffset.Direction);
    }

    // -------------------------------------------------------------------------
    // Phase OnTick behavior — heading targets during acquisition and after
    // -------------------------------------------------------------------------

    [Fact]
    public void Downwind_OffsetRight_FirstTickSetsInterceptHeading()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(wp.DownwindAbeamLat, wp.DownwindAbeamLon, wp.DownwindHeading.Degrees);
        var downwind = new DownwindPhase { Waypoints = wp };
        ac.Phases!.Add(downwind);
        ac.Phases.Start(Ctx(ac));

        // Apply offset.
        PatternCommandHandler.TryOffsetPattern(ac, TurnDirection.Right, 0.5);

        // First tick: heading should be biased +30° from downwind heading
        // (right of aircraft heading == clockwise == larger angle).
        downwind.OnTick(Ctx(ac));

        double expectedIntercept = (wp.DownwindHeading.Degrees + PatternLateralOffsetHelper.InterceptDeg) % 360;
        Assert.Equal(expectedIntercept, ac.Targets.TargetTrueHeading!.Value.Degrees, 1);
        Assert.False(downwind.LateralOffset!.Acquired);
    }

    [Fact]
    public void Downwind_OffsetLeft_FirstTickSetsInterceptHeading()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(wp.DownwindAbeamLat, wp.DownwindAbeamLon, wp.DownwindHeading.Degrees);
        var downwind = new DownwindPhase { Waypoints = wp };
        ac.Phases!.Add(downwind);
        ac.Phases.Start(Ctx(ac));

        PatternCommandHandler.TryOffsetPattern(ac, TurnDirection.Left, 0.5);

        downwind.OnTick(Ctx(ac));

        // Left = counter-clockwise = smaller angle.
        double expectedIntercept = (wp.DownwindHeading.Degrees - PatternLateralOffsetHelper.InterceptDeg + 360) % 360;
        Assert.Equal(expectedIntercept, ac.Targets.TargetTrueHeading!.Value.Degrees, 1);
        Assert.False(downwind.LateralOffset!.Acquired);
    }

    [Fact]
    public void Downwind_OffsetRight_OnceAcquired_RestoresParallelHeading()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(wp.DownwindAbeamLat, wp.DownwindAbeamLon, wp.DownwindHeading.Degrees);
        var downwind = new DownwindPhase { Waypoints = wp };
        ac.Phases!.Add(downwind);
        ac.Phases.Start(Ctx(ac));

        PatternCommandHandler.TryOffsetPattern(ac, TurnDirection.Right, 0.5);

        // Teleport the aircraft 0.6 NM to the right of the original downwind
        // track (perpendicular to downwind heading, on the right side).
        // Downwind is opposite runway: rwy heading = 280, downwind heading ~100.
        // "Right of heading 100" = heading 100 + 90 = bearing 190.
        var rightPerp = new TrueHeading((wp.DownwindHeading.Degrees + 90) % 360);
        var newPos = GeoMath.ProjectPoint(ac.Position.Lat, ac.Position.Lon, rightPerp, 0.6);
        ac.Position = new LatLon(newPos.Lat, newPos.Lon);

        downwind.OnTick(Ctx(ac));

        // Now ≥ 0.5 NM acquired → should resume parallel downwind heading.
        Assert.Equal(wp.DownwindHeading.Degrees, ac.Targets.TargetTrueHeading!.Value.Degrees, 1);
        Assert.True(downwind.LateralOffset!.Acquired);
    }

    [Fact]
    public void Downwind_NoOffsetIssued_KeepsDefaultHeading()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(wp.DownwindAbeamLat, wp.DownwindAbeamLon, wp.DownwindHeading.Degrees);
        var downwind = new DownwindPhase { Waypoints = wp };
        ac.Phases!.Add(downwind);
        ac.Phases.Start(Ctx(ac));

        downwind.OnTick(Ctx(ac));

        Assert.Equal(wp.DownwindHeading.Degrees, ac.Targets.TargetTrueHeading!.Value.Degrees, 1);
        Assert.Null(downwind.LateralOffset);
    }

    // -------------------------------------------------------------------------
    // Snapshot round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public void Downwind_LateralOffsetSurvivesSnapshotRoundTrip()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(wp.DownwindAbeamLat, wp.DownwindAbeamLon, wp.DownwindHeading.Degrees);
        var downwind = new DownwindPhase { Waypoints = wp };
        ac.Phases!.Add(downwind);
        ac.Phases.Start(Ctx(ac));

        PatternCommandHandler.TryOffsetPattern(ac, TurnDirection.Right, 0.7);

        var dto = (Yaat.Sim.Simulation.Snapshots.DownwindPhaseDto)downwind.ToSnapshot();
        var restored = DownwindPhase.FromSnapshot(dto);

        Assert.NotNull(restored.LateralOffset);
        Assert.Equal(0.7, restored.LateralOffset.TargetNm);
        Assert.Equal(TurnDirection.Right, restored.LateralOffset.Direction);
        Assert.False(restored.LateralOffset.Acquired);
    }

    [Fact]
    public void Upwind_LateralOffsetSurvivesSnapshotRoundTrip()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(wp.DepartureEndLat, wp.DepartureEndLon, wp.UpwindHeading.Degrees);
        var upwind = new UpwindPhase { Waypoints = wp };
        ac.Phases!.Add(upwind);
        ac.Phases.Start(Ctx(ac));

        PatternCommandHandler.TryOffsetPattern(ac, TurnDirection.Left, 0.4);

        var dto = (Yaat.Sim.Simulation.Snapshots.UpwindPhaseDto)upwind.ToSnapshot();
        var restored = UpwindPhase.FromSnapshot(dto);

        Assert.NotNull(restored.LateralOffset);
        Assert.Equal(0.4, restored.LateralOffset.TargetNm);
        Assert.Equal(TurnDirection.Left, restored.LateralOffset.Direction);
    }

    [Fact]
    public void Crosswind_LateralOffsetSurvivesSnapshotRoundTrip()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(wp.CrosswindTurnLat, wp.CrosswindTurnLon, wp.CrosswindHeading.Degrees);
        var crosswind = new CrosswindPhase { Waypoints = wp };
        ac.Phases!.Add(crosswind);
        ac.Phases.Start(Ctx(ac));

        PatternCommandHandler.TryOffsetPattern(ac, TurnDirection.Right, 0.6);

        var dto = (Yaat.Sim.Simulation.Snapshots.CrosswindPhaseDto)crosswind.ToSnapshot();
        var restored = CrosswindPhase.FromSnapshot(dto);

        Assert.NotNull(restored.LateralOffset);
        Assert.Equal(0.6, restored.LateralOffset.TargetNm);
        Assert.Equal(TurnDirection.Right, restored.LateralOffset.Direction);
    }

    [Fact]
    public void Base_LateralOffsetSurvivesSnapshotRoundTrip()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(wp.BaseTurnLat, wp.BaseTurnLon, wp.BaseHeading.Degrees);
        var basePhase = new BasePhase { Waypoints = wp };
        ac.Phases!.Add(basePhase);
        ac.Phases.Start(Ctx(ac));

        PatternCommandHandler.TryOffsetPattern(ac, TurnDirection.Left, 0.3);

        var dto = (Yaat.Sim.Simulation.Snapshots.BasePhaseDto)basePhase.ToSnapshot();
        var restored = BasePhase.FromSnapshot(dto);

        Assert.NotNull(restored.LateralOffset);
        Assert.Equal(0.3, restored.LateralOffset.TargetNm);
        Assert.Equal(TurnDirection.Left, restored.LateralOffset.Direction);
    }
}
