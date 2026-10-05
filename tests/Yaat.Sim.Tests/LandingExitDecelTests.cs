using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for exit-aware braking during landing rollout and exit angle/speed calculations.
/// Uses the real OAK ground layout:
/// - RWY 30 / twy W5 = high-speed exit (~30° shallow turn)
/// - RWY 28R / twy H = standard exit (~90° steep turn)
/// </summary>
public class LandingExitDecelTests
{
    public LandingExitDecelTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private const string TestDataDir = "TestData";

    private static AirportGroundLayout? LoadOakLayout()
    {
        string path = Path.Combine(TestDataDir, "oak.geojson");
        if (!File.Exists(path))
        {
            return null;
        }

        return GeoJsonParser.Parse("OAK", File.ReadAllText(path), null);
    }

    private static RunwayInfo MakeRunway(string designator, double heading, double thresholdLat, double thresholdLon) =>
        TestRunwayFactory.Make(designator: designator, heading: heading, elevationFt: 9.0, thresholdLat: thresholdLat, thresholdLon: thresholdLon);

    private static AircraftState MakeLandedAircraft(double lat, double lon, double heading, double ias)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(heading),
            Altitude = 9.0,
            IndicatedAirspeed = ias,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "TEST" },
            Phases = new PhaseList(),
        };
        return ac;
    }

    private static PhaseContext Ctx(AircraftState ac, RunwayInfo rwy, AirportGroundLayout? layout, double dt = 1.0) =>
        new()
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = dt,
            Runway = rwy,
            FieldElevation = 9.0,
            GroundLayout = layout,
            Logger = NullLogger.Instance,
        };

    private static (int ticks, double finalSpeed) SimulateRollout(LandingPhase phase, PhaseContext ctx, int maxTicks = 500)
    {
        int ticks = 0;
        while (ticks < maxTicks)
        {
            // LandingPhase writes ControlTargets and delegates integration
            // to FlightPhysics. Call Update to match the real sim loop.
            FlightPhysics.Update(ctx.Aircraft, ctx.DeltaSeconds);
            bool done = phase.OnTick(ctx);
            ticks++;
            if (done)
            {
                break;
            }
        }

        return (ticks, ctx.Aircraft.IndicatedAirspeed);
    }

    // -- OAK 28R: touchdown at east end (37.724806, -122.204721), heading 280° --

    [Fact]
    public void OAK28R_NoPreference_CompletesAtReasonableSpeed()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        RunwayInfo rwy = MakeRunway("28R", 280.0, 37.724806, -122.204721);
        AircraftState ac = MakeLandedAircraft(37.724806, -122.204721, 280.0, ias: 130);
        ac.Phases!.AssignedRunway = rwy;
        PhaseContext ctx = Ctx(ac, rwy, layout);

        var phase = new LandingPhase();
        phase.OnStart(ctx);
        ac.IsOnGround = true;

        (int _, double finalSpeed) = SimulateRollout(phase, ctx);

        // With a ground layout, the pilot plans for the first reachable exit
        // even without an explicit preference. Final speed depends on the exit's
        // turn-off speed (15-30kts for jets).
        Assert.InRange(finalSpeed, 0, 41);
    }

    [Fact]
    public void NoGroundLayout_FallsBackToDefault()
    {
        RunwayInfo rwy = MakeRunway("28R", 280.0, 37.724806, -122.204721);
        AircraftState ac = MakeLandedAircraft(37.724806, -122.204721, 280.0, ias: 130);
        ac.Phases!.RequestedExit = new ExitPreference { Side = ExitSide.Left };

        PhaseContext ctx = Ctx(ac, rwy, layout: null);
        var phase = new LandingPhase();
        phase.OnStart(ctx);

        (int _, double finalSpeed) = SimulateRollout(phase, ctx);

        // No layout → no exit → completes at coast speed (40 kts for jets)
        Assert.InRange(finalSpeed, 38, 41);
    }

    [Fact]
    public void OAK28R_ExitFarAhead_MaintainsCoastSpeed()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        RunwayInfo rwy = MakeRunway("28R", 280.0, 37.724806, -122.204721);
        AircraftState ac = MakeLandedAircraft(37.724806, -122.204721, 280.0, ias: 60);
        ac.Phases!.RequestedExit = new ExitPreference { Taxiway = "H" };
        ac.Phases.AssignedRunway = rwy;

        PhaseContext ctx = Ctx(ac, rwy, layout);
        var phase = new LandingPhase();
        phase.OnStart(ctx);

        for (int i = 0; i < 5; i++)
        {
            phase.OnTick(ctx);
        }

        Assert.True(ctx.Aircraft.IndicatedAirspeed >= 39.0, $"Speed dropped to {ctx.Aircraft.IndicatedAirspeed:F1}, expected >= 39");
    }

    [Fact]
    public void OAK28R_ExitPreferenceChanged_ReResolvesExit()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        RunwayInfo rwy = MakeRunway("28R", 280.0, 37.724806, -122.204721);
        AircraftState ac = MakeLandedAircraft(37.724806, -122.204721, 280.0, ias: 80);
        ac.Phases!.AssignedRunway = rwy;

        PhaseContext ctx = Ctx(ac, rwy, layout);
        var phase = new LandingPhase();
        phase.OnStart(ctx);

        for (int i = 0; i < 3; i++)
        {
            FlightPhysics.Update(ac, ctx.DeltaSeconds);
            phase.OnTick(ctx);
        }

        double speedBefore = ac.IndicatedAirspeed;
        ac.Phases!.RequestedExit = new ExitPreference { Taxiway = "H" };
        FlightPhysics.Update(ac, ctx.DeltaSeconds);
        phase.OnTick(ctx);

        Assert.True(ac.IndicatedAirspeed < speedBefore);
    }

    [Fact]
    public void OAK28R_ExitBehind_FallsBackToDefault()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        // Start past P — B is behind
        RunwayInfo rwy = MakeRunway("28R", 280.0, 37.724806, -122.204721);
        AircraftState ac = MakeLandedAircraft(37.729433, -122.219017, 280.0, ias: 80);
        ac.Phases!.RequestedExit = new ExitPreference { Taxiway = "B" };
        ac.Phases.AssignedRunway = rwy;

        PhaseContext ctx = Ctx(ac, rwy, layout);
        var phase = new LandingPhase();
        phase.OnStart(ctx);

        (int _, double finalSpeed) = SimulateRollout(phase, ctx);

        // B is behind → no exit resolved → completes at coast speed
        Assert.InRange(finalSpeed, 38, 41);
    }

    // -- ComputeExitAngle --

    [Fact]
    public void ComputeExitAngle_OAK30_W5_IsHighSpeed()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        List<GroundNode> hsNodes = layout.GetRunwayHoldShortNodes("30");
        GroundNode? w5Node = hsNodes.FirstOrDefault(n => n.Edges.Any(e => e.TaxiwayName == "W5"));
        if (w5Node is null)
        {
            return;
        }

        double? angle = layout.ComputeExitAngle(w5Node, "W5", new TrueHeading(310.0));
        Assert.NotNull(angle);
        Assert.InRange(angle.Value, 0, 45);
    }

    [Fact]
    public void ComputeExitAngle_NoMatchingTaxiway_ReturnsNull()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        List<GroundNode> hsNodes = layout.GetRunwayHoldShortNodes("28R");
        if (hsNodes.Count == 0)
        {
            return;
        }

        Assert.Null(layout.ComputeExitAngle(hsNodes[0], "NONEXISTENT", new TrueHeading(280.0)));
    }

    // -- FindExitAheadOnRunway (retained for EL/ER/EXIT commands) --

    [Fact]
    public void FindExitAhead_OAK28R_H_ReturnsIt()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        (GroundNode Node, string Taxiway)? result = layout.FindExitAheadOnRunway(
            37.724806,
            -122.204721,
            new TrueHeading(280.0),
            new ExitPreference { Taxiway = "H" },
            "28R"
        );
        Assert.NotNull(result);
    }

    [Fact]
    public void FindExitAhead_NoPreference_FindsNearest()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        (GroundNode Node, string Taxiway)? result = layout.FindExitAheadOnRunway(37.724806, -122.204721, new TrueHeading(280.0), null, "28R");
        Assert.NotNull(result);
    }

    [Fact]
    public void FindExitAhead_SidePreference_FiltersCorrectly()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        (GroundNode Node, string Taxiway)? rightResult = layout.FindExitAheadOnRunway(
            37.724806,
            -122.204721,
            new TrueHeading(280.0),
            new ExitPreference { Side = ExitSide.Right },
            "28R"
        );
        Assert.NotNull(rightResult);
    }

    // -- The committed-exit braking ceiling --

    /// <summary>The W5 high-speed exit on OAK 30: branch node, hold-short node, and turn-off speed. Null without W5.</summary>
    private static (GroundNode Branch, GroundNode HoldShort, double TurnOffSpeed)? Oak30W5(AirportGroundLayout layout, RunwayInfo runway)
    {
        GroundNode? holdShort = layout.GetRunwayHoldShortNodes("30").FirstOrDefault(n => n.Edges.Any(e => e.TaxiwayName == "W5"));
        if (holdShort is null)
        {
            return null;
        }

        IGroundEdge? w5 = holdShort.Edges.FirstOrDefault(e => e.TaxiwayName == "W5");
        if (w5 is null)
        {
            return null;
        }

        GroundNode? branch = w5.Nodes.FirstOrDefault(n => n.Id != holdShort.Id);
        if (branch is null)
        {
            return null;
        }

        double? angle = layout.ComputeExitAngle(holdShort, "W5", runway.TrueHeading);
        double turnOff = CategoryPerformance.ExitTurnOffSpeed(AircraftCategory.Jet, angle);
        return (branch, holdShort, turnOff);
    }

    /// <summary>
    /// A <see cref="LandingPhase"/> restored mid-rollout on OAK 30 with W5 as its committed exit, at a fixed distance
    /// short of the branch and at the ground speed whose required deceleration is exactly <paramref name="requiredDecel"/>.
    /// Restoring a hand-built snapshot is the only way to pin a candidate at an exact selection rate and start the
    /// aircraft faster than that rate assumed.
    /// </summary>
    private static (LandingPhase Phase, PhaseContext Ctx, AircraftState Aircraft) RestoredOak30Rollout(
        AirportGroundLayout layout,
        RunwayInfo runway,
        (GroundNode Branch, GroundNode HoldShort, double TurnOffSpeed) exit,
        double selectionRate,
        double requiredDecel
    )
    {
        // required = (v² − turnOff²) / (7200 · distanceNm), so v is the speed that needs exactly requiredDecel here.
        const double DistToBranchNm = 0.12;
        double groundSpeedKts = Math.Sqrt((exit.TurnOffSpeed * exit.TurnOffSpeed) + (requiredDecel * 7200.0 * DistToBranchNm));
        LatLon position = GeoMath.ProjectPoint(exit.Branch.Position, runway.TrueHeading.ToReciprocal(), DistToBranchNm);

        var dto = new LandingPhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 5.0,
            FieldElevation = runway.ElevationFt,
            RunwayHeadingDeg = runway.TrueHeading.Degrees,
            ThresholdLat = runway.ThresholdLatitude,
            ThresholdLon = runway.ThresholdLongitude,
            TouchedDown = true,
            CanGoAround = false,
            LahsoHoldShortDistNm = 0,
            HasLahso = false,
            StoppedForLahso = false,
            CurrentStateValue = (int)LandingPhase.State.Rollout,
            RunwayId = runway.Designator,
            FlareEntryAgl = CategoryPerformance.FlareAltitude(AircraftCategory.Jet),
            FlareFpm = CategoryPerformance.FlareDescentRate(AircraftCategory.Jet),
            Vref = CategoryPerformance.ApproachSpeed(AircraftCategory.Jet),
            Vtd = AircraftPerformance.TouchdownSpeed("B738", AircraftCategory.Jet),
            CoastSpeed = CategoryPerformance.RolloutCoastSpeed(AircraftCategory.Jet),
            DefaultDecel = CategoryPerformance.RolloutDecelRate(AircraftCategory.Jet),
            TouchdownAgl = 2,
            CandidateExitHoldShortId = exit.HoldShort.Id,
            CandidateExitBranchPointId = exit.Branch.Id,
            CandidateExitTaxiway = "W5",
            CandidateExitTurnOffSpeed = exit.TurnOffSpeed,
            CandidateExitPathNodeIds = [exit.Branch.Id, exit.HoldShort.Id],
            CandidateExitSelectionDecelRate = selectionRate,
        };

        var phase = LandingPhase.FromSnapshot(dto, layout);
        AircraftState aircraft = MakeLandedAircraft(position.Lat, position.Lon, runway.TrueHeading.Degrees, groundSpeedKts);
        aircraft.Phases!.AssignedRunway = runway;
        aircraft.Ground.Layout = layout;
        return (phase, Ctx(aircraft, runway, layout), aircraft);
    }

    /// <summary>
    /// The committed exit's braking ceiling is the rate that selected it plus
    /// <see cref="LandingPhase.CommittedExitDecelToleranceKtsPerSec"/> (capped by the firm rate). An aircraft braking less
    /// than the selection assumed — its required rate just above the selection rate, inside the tolerance — is still flown
    /// onto the committed exit rather than giving it up.
    /// </summary>
    [Fact]
    public void CommittedExitCeiling_AdmitsASmallBrakingLag_AndKeepsTheExit()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        RunwayInfo? runway = NavigationDatabase.InstanceOrNull?.GetRunway("OAK", "30");
        if ((layout is null) || (runway is null))
        {
            return;
        }

        (GroundNode Branch, GroundNode HoldShort, double TurnOffSpeed)? exit = Oak30W5(layout, runway);
        if (exit is null)
        {
            return;
        }

        double selectionRate = CategoryPerformance.ComfortableExitDecelRate(AircraftCategory.Jet);
        (LandingPhase phase, PhaseContext ctx, AircraftState aircraft) = RestoredOak30Rollout(
            layout,
            runway,
            exit.Value,
            selectionRate,
            requiredDecel: selectionRate + 0.2
        );
        Assert.NotNull(phase.CandidateExit);

        bool complete = phase.OnTick(ctx);

        double plannedRate = ctx.Targets.DesiredDecelRate ?? 0;
        Assert.True(
            plannedRate > selectionRate,
            $"the ceiling should admit a required rate {selectionRate + 0.2:F2} above the selection rate {selectionRate:F2}, planned {plannedRate:F2}"
        );
        Assert.True(
            plannedRate <= (selectionRate + LandingPhase.CommittedExitDecelToleranceKtsPerSec) + 0.01,
            $"the planned rate {plannedRate:F2} should not exceed the ceiling {selectionRate + LandingPhase.CommittedExitDecelToleranceKtsPerSec:F2}"
        );
        Assert.True(
            (ctx.Targets.TargetSpeed is { } speed) && (Math.Abs(speed - exit.Value.TurnOffSpeed) < 1.0),
            $"the plan should target W5's turn-off speed {exit.Value.TurnOffSpeed:F0} kt, was {ctx.Targets.TargetSpeed}"
        );

        for (int t = 1; (t < 600) && !complete; t++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            complete = phase.OnTick(ctx);
        }

        Assert.True(complete, "the rollout should have handed off");
        Assert.Equal("W5", phase.CandidateExit?.TaxiwayName);
    }

    /// <summary>
    /// The control for <see cref="CommittedExitCeiling_AdmitsASmallBrakingLag_AndKeepsTheExit"/>: a lag past the tolerance
    /// is refused. The rollout holds the default rate at coast speed and gives the exit up rather than brake harder than
    /// the rate that selected it, so W5 is no longer the candidate once the aircraft reaches it.
    /// </summary>
    [Fact]
    public void CommittedExitCeiling_RefusesABrakingLagPastTheTolerance_AndGivesUpTheExit()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        RunwayInfo? runway = NavigationDatabase.InstanceOrNull?.GetRunway("OAK", "30");
        if ((layout is null) || (runway is null))
        {
            return;
        }

        (GroundNode Branch, GroundNode HoldShort, double TurnOffSpeed)? exit = Oak30W5(layout, runway);
        if (exit is null)
        {
            return;
        }

        double selectionRate = CategoryPerformance.ComfortableExitDecelRate(AircraftCategory.Jet);
        (LandingPhase phase, PhaseContext ctx, AircraftState aircraft) = RestoredOak30Rollout(
            layout,
            runway,
            exit.Value,
            selectionRate,
            requiredDecel: selectionRate + 0.4
        );
        Assert.NotNull(phase.CandidateExit);

        bool complete = phase.OnTick(ctx);

        double plannedRate = ctx.Targets.DesiredDecelRate ?? 0;
        Assert.True(
            plannedRate <= selectionRate + 1e-6,
            $"a required rate past the ceiling should not raise the plan above the selection rate {selectionRate:F2}, planned {plannedRate:F2}"
        );

        for (int t = 1; (t < 600) && !complete; t++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            complete = phase.OnTick(ctx);
        }

        Assert.True(complete, "the rollout should still have ended");
        Assert.NotEqual("W5", phase.CandidateExit?.TaxiwayName);
    }

    // -- No usable exit: stop on the remaining runway --

    /// <summary>
    /// Outside LAHSO, a rollout whose every exit hold-short is already claimed has no exit to take: the graph planner
    /// excludes the occupied bars, the straight-line fallback hands the landing a candidate the exit phase then drops for
    /// occupancy, and the centerline re-search finds nothing. The arrival must stop on the remaining runway rather than
    /// leave the pavement or coast off its end.
    /// </summary>
    [Fact(Skip = "YAAT-30(h): the no-exit stop is not built yet; the aircraft overruns the departure end")]
    public void Rollout_WithEveryExitHoldShortOccupied_StopsOnTheRemainingRunway()
    {
        ShortFinalArrival.Spawned? spawned = ShortFinalArrival.SpawnClearedToLand("OAK", "28R", "CRJ9", "TST001");
        if (spawned is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft, RunwayInfo runway) = spawned;

        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        List<GroundNode> holdShorts = layout.GetRunwayHoldShortNodes("28R");
        if (holdShorts.Count == 0)
        {
            return;
        }

        foreach (GroundNode holdShort in holdShorts)
        {
            engine.World.AddAircraft(MakeHoldingAfterExitBlocker(runway, layout, holdShort));
        }

        LatLon threshold = LandingThreshold.Resolve(runway, layout);
        double runwayLengthFt = GeoMath.DistanceNm(threshold, new LatLon(runway.EndLatitude, runway.EndLongitude)) * GeoMath.FeetPerNm;

        bool leftTheRunway = false;
        for (int t = 1; t <= 420; t++)
        {
            engine.TickOneSecond();
            if (aircraft.Phases?.CurrentPhase is HoldingAfterExitPhase)
            {
                leftTheRunway = true;
                break;
            }

            if ((aircraft.GroundSpeed < 1.0) && (aircraft.Phases?.CurrentPhase is LandingPhase or RunwayExitPhase))
            {
                break;
            }
        }

        Assert.False(leftTheRunway, "the arrival took an exit whose hold-short was occupied");

        Assert.True(
            aircraft.IsOnGround && (aircraft.GroundSpeed < 1.0),
            $"the arrival should have stopped on the runway, phase={aircraft.Phases?.CurrentPhase?.Name} gs={aircraft.GroundSpeed:F1}"
        );

        double alongFt = GeoMath.AlongTrackDistanceNm(aircraft.Position, threshold, runway.TrueHeading) * GeoMath.FeetPerNm;
        Assert.InRange(alongFt, 0.0, runwayLengthFt);
    }

    /// <summary>
    /// An aircraft that has already cleared the runway and is holding at <paramref name="holdShort"/> — the state the
    /// engine's occupied-hold-short set reads, so the exit planner treats that bar as claimed.
    /// </summary>
    private static AircraftState MakeHoldingAfterExitBlocker(RunwayInfo runway, AirportGroundLayout layout, GroundNode holdShort)
    {
        var blocker = new AircraftState
        {
            Callsign = $"BLK{holdShort.Id}",
            AircraftType = "C172",
            Position = holdShort.Position,
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            Phases = new PhaseList { AssignedRunway = runway },
        };
        blocker.Ground.Layout = layout;
        blocker.Phases.Add(new HoldingAfterExitPhase(runway.Designator, "N", holdShort.Id));
        blocker.Phases.Start(CommandDispatcher.BuildMinimalContext(blocker, layout));
        return blocker;
    }

    // -- ExitTurnOffSpeed (pure math, no layout) --

    [Fact]
    public void ExitTurnOffSpeed_NullAngle_ReturnsFallback() =>
        Assert.Equal(CategoryPerformance.RunwayExitSpeed(AircraftCategory.Jet), CategoryPerformance.ExitTurnOffSpeed(AircraftCategory.Jet, null));

    [Fact]
    public void ExitTurnOffSpeed_SmallAngle_ReturnsHighSpeed() =>
        Assert.Equal(CategoryPerformance.HighSpeedExitSpeed(AircraftCategory.Jet), CategoryPerformance.ExitTurnOffSpeed(AircraftCategory.Jet, 30));

    [Fact]
    public void ExitTurnOffSpeed_LargeAngle_ReturnsStandard() =>
        Assert.Equal(CategoryPerformance.StandardExitSpeed(AircraftCategory.Jet), CategoryPerformance.ExitTurnOffSpeed(AircraftCategory.Jet, 80));

    [Fact]
    public void ExitTurnOffSpeed_ExactThreshold_ReturnsHighSpeed() =>
        Assert.Equal(CategoryPerformance.HighSpeedExitSpeed(AircraftCategory.Jet), CategoryPerformance.ExitTurnOffSpeed(AircraftCategory.Jet, 45));
}
