using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
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

    private static AircraftState MakeLandedAircraft(double lat, double lon, double heading, double ias) =>
        MakeLandedAircraftOfType("B738", lat, lon, heading, ias);

    private static AircraftState MakeLandedAircraftOfType(string aircraftType, double lat, double lon, double heading, double ias)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = aircraftType,
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

    private static PhaseContext Ctx(AircraftState ac, RunwayInfo rwy, AirportGroundLayout? layout) => CtxFor(ac, rwy, layout, AircraftCategory.Jet);

    private static PhaseContext CtxFor(AircraftState ac, RunwayInfo rwy, AirportGroundLayout? layout, AircraftCategory category) =>
        new()
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = category,
            DeltaSeconds = 1.0,
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
            "28R",
            excludeTaxiways: null
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

        (GroundNode Node, string Taxiway)? result = layout.FindExitAheadOnRunway(
            37.724806,
            -122.204721,
            new TrueHeading(280.0),
            null,
            "28R",
            excludeTaxiways: null
        );
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
            "28R",
            excludeTaxiways: null
        );
        Assert.NotNull(rightResult);
    }

    // -- The committed-exit braking ceiling --

    /// <summary>The W5 high-speed exit on OAK 30: branch node, hold-short node, and turn-off speed. Null without W5.</summary>
    private static (GroundNode Branch, GroundNode HoldShort, double TurnOffSpeed)? Oak30W5(AirportGroundLayout layout, RunwayInfo runway) =>
        Oak30W5For(layout, runway, AircraftCategory.Jet);

    private static (GroundNode Branch, GroundNode HoldShort, double TurnOffSpeed)? Oak30W5For(
        AirportGroundLayout layout,
        RunwayInfo runway,
        AircraftCategory category
    )
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
        double turnOff = CategoryPerformance.ExitTurnOffSpeed(category, angle);
        return (branch, holdShort, turnOff);
    }

    /// <summary>
    /// One aircraft for <see cref="RestoredOak30Rollout"/>: its type designator, category and distance short of the branch,
    /// the rate its committed exit was selected at (null for none), and the deceleration its starting speed requires to
    /// make the exit's turn-off speed by the branch.
    /// </summary>
    private readonly record struct RestoredRolloutAircraft(
        string AircraftType,
        AircraftCategory Category,
        double DistToBranchNm,
        double? SelectionRate,
        double RequiredDecel
    );

    private static RestoredRolloutAircraft Jet738(double? selectionRate, double requiredDecel) =>
        new("B738", AircraftCategory.Jet, 0.12, selectionRate, requiredDecel);

    /// <summary>
    /// A <see cref="LandingPhase"/> restored mid-rollout on OAK 30 with W5 as its committed exit, at a fixed distance
    /// short of the branch and at the ground speed whose required deceleration is exactly
    /// <see cref="RestoredRolloutAircraft.RequiredDecel"/>. Restoring a hand-built snapshot is the only way to pin a
    /// candidate at an exact selection rate and start the aircraft faster than that rate assumed.
    /// </summary>
    private static (LandingPhase Phase, PhaseContext Ctx, AircraftState Aircraft) RestoredOak30Rollout(
        AirportGroundLayout layout,
        RunwayInfo runway,
        (GroundNode Branch, GroundNode HoldShort, double TurnOffSpeed) exit,
        RestoredRolloutAircraft type
    )
    {
        // required = (v² − turnOff²) / (7200 · distanceNm), so v is the speed that needs exactly RequiredDecel here.
        AircraftCategory category = type.Category;
        double groundSpeedKts = Math.Sqrt((exit.TurnOffSpeed * exit.TurnOffSpeed) + (type.RequiredDecel * 7200.0 * type.DistToBranchNm));
        LatLon position = GeoMath.ProjectPoint(exit.Branch.Position, runway.TrueHeading.ToReciprocal(), type.DistToBranchNm);

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
            FlareEntryAgl = CategoryPerformance.FlareAltitude(category),
            FlareFpm = CategoryPerformance.FlareDescentRate(category),
            Vref = CategoryPerformance.ApproachSpeed(category),
            Vtd = AircraftPerformance.TouchdownSpeed(type.AircraftType, category),
            CoastSpeed = CategoryPerformance.RolloutCoastSpeed(category),
            DefaultDecel = CategoryPerformance.RolloutDecelRate(category),
            TouchdownAgl = 2,
            CandidateExitHoldShortId = exit.HoldShort.Id,
            CandidateExitBranchPointId = exit.Branch.Id,
            CandidateExitTaxiway = "W5",
            CandidateExitTurnOffSpeed = exit.TurnOffSpeed,
            CandidateExitPathNodeIds = [exit.Branch.Id, exit.HoldShort.Id],
            CandidateExitSelectionDecelRate = type.SelectionRate,
        };

        var phase = LandingPhase.FromSnapshot(dto, layout);
        AircraftState aircraft = MakeLandedAircraftOfType(type.AircraftType, position.Lat, position.Lon, runway.TrueHeading.Degrees, groundSpeedKts);
        aircraft.Phases!.AssignedRunway = runway;
        aircraft.Ground.Layout = layout;
        return (phase, CtxFor(aircraft, runway, layout, category), aircraft);
    }

    /// <summary>
    /// A piston's committed-exit ceiling is its own firm rate (4.0 kt/s), not the jet's 5.0. An exit committed without a
    /// selection rate takes the firm cap — the cap the firm-braking fallback and an instructed exit also brake under — so a
    /// C172 whose exit needs 4.5 kt/s gives it up rather than brake past its firm rate.
    /// </summary>
    [Fact]
    public void PistonCommittedExitCeiling_IsThePistonFirmRate()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        RunwayInfo? runway = NavigationDatabase.InstanceOrNull?.GetRunway("OAK", "30");
        if ((layout is null) || (runway is null))
        {
            return;
        }

        (GroundNode Branch, GroundNode HoldShort, double TurnOffSpeed)? exit = Oak30W5For(layout, runway, AircraftCategory.Piston);
        if (exit is null)
        {
            return;
        }

        double firm = CategoryPerformance.FirmBrakingRate(AircraftCategory.Piston);
        (LandingPhase phase, PhaseContext ctx, AircraftState aircraft) = RestoredOak30Rollout(
            layout,
            runway,
            exit.Value,
            new RestoredRolloutAircraft("C172", AircraftCategory.Piston, 0.08, SelectionRate: null, RequiredDecel: firm + 0.5)
        );
        Assert.NotNull(phase.CandidateExit);

        bool complete = phase.OnTick(ctx);
        Assert.True(
            (ctx.Targets.TargetSpeed is not { } plannedSpeed) || (Math.Abs(plannedSpeed - exit.Value.TurnOffSpeed) >= 1.0),
            $"the first tick still plans W5's {exit.Value.TurnOffSpeed:F0} kt turn-off speed, which needs more than the firm rate"
        );

        for (int t = 1; (t < 600) && !complete; t++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            complete = phase.OnTick(ctx);
        }

        Assert.True(complete, "the rollout should have ended");
        Assert.NotEqual("W5", phase.CandidateExit?.TaxiwayName);
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
            Jet738(selectionRate, requiredDecel: selectionRate + 0.2)
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
            Jet738(selectionRate, requiredDecel: selectionRate + 0.4)
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

    // -- The exit turn brakes at the rate the rollout chose --

    /// <summary>
    /// An uninstructed DH8D on OAK 30 (QXE6184's W4 turn-off) brakes through the turn onto its exit at no more than the rate
    /// the rollout chose the exit with, not the 5.0 kt/s turboprop taxi rate: from the hand-off to <see cref="RunwayExitPhase"/>
    /// until the aircraft leaves the turn-off (<see cref="RunwayExitPhase.IsOnTurnOff"/>), neither the published braking rate
    /// nor the measured deceleration exceeds the selection rate.
    /// </summary>
    [Fact]
    public void UninstructedExitTurn_BrakesAtTheRolloutSelectionRate_NotTheTaxiRate()
    {
        ShortFinalArrival.Spawned? spawned = ShortFinalArrival.SpawnClearedToLand("OAK", "30", "DH8D", "QXE6184");
        if (spawned is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft, RunwayInfo _) = spawned;
        double taxiRate = CategoryPerformance.TaxiDecelRate(AircraftCategory.Turboprop);

        double? selectionRate = null;
        string? exitTaxiway = null;
        double maxPublishedRate = 0;
        double maxMeasuredDecel = 0;
        int turnOffTicks = 0;
        double previousGs = aircraft.GroundSpeed;
        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            double measured = previousGs - aircraft.GroundSpeed;
            previousGs = aircraft.GroundSpeed;

            if (aircraft.Phases?.CurrentPhase is LandingPhase { CandidateExit: { } candidate })
            {
                selectionRate = candidate.SelectionDecelRate;
                exitTaxiway = candidate.TaxiwayName;
                continue;
            }

            if (aircraft.Phases?.CurrentPhase is not RunwayExitPhase { IsOnTurnOff: true })
            {
                if (turnOffTicks > 0)
                {
                    break;
                }

                continue;
            }

            turnOffTicks++;
            maxPublishedRate = Math.Max(maxPublishedRate, aircraft.Targets.DesiredDecelRate ?? taxiRate);
            maxMeasuredDecel = Math.Max(maxMeasuredDecel, measured);
        }

        Assert.NotNull(selectionRate);
        Assert.Equal("W4", exitTaxiway);
        Assert.True(turnOffTicks > 0, "the arrival never flew a turn-off");
        Assert.True(
            maxPublishedRate <= selectionRate.Value + 1e-6,
            $"the turn onto {exitTaxiway} braked at up to {maxPublishedRate:F2} kt/s, above its {selectionRate:F2} kt/s selection rate"
        );
        Assert.True(
            maxMeasuredDecel <= selectionRate.Value + 0.05,
            $"the turn onto {exitTaxiway} decelerated at up to {maxMeasuredDecel:F2} kt/s, above its {selectionRate:F2} kt/s selection rate"
        );
    }

    /// <summary>
    /// A C172 told to exit at OAK 28R H: past the approach leg it never brakes above its 2.0 kt/s taxi rate, and it stops past
    /// the bar no more than 10 ft beyond half its length — the route-end stop is planned at the taxi rate whatever rate the
    /// turn-off flew. (It stops 12.9 ft past the bar, 0.7 ft short of half its 27.2 ft length: YAAT-364.)
    /// </summary>
    [Fact]
    public void C172InstructedExit_BrakesAtTheTaxiRatePastTheApproachLeg_AndStopsJustPastTheBar()
    {
        const string ExitTaxiway = "H";
        ShortFinalArrival.Spawned? spawned = ShortFinalArrival.SpawnClearedToLand("OAK", "28R", "C172", "N172SE");
        if (spawned is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft, RunwayInfo _) = spawned;
        CommandResult exitResult = engine.SendCommand(aircraft.Callsign, $"EXIT {ExitTaxiway}");
        Assert.True(exitResult.Success, $"EXIT {ExitTaxiway} failed: {exitResult.Message}");

        double taxiRate = CategoryPerformance.TaxiDecelRate(AircraftCategory.Piston);

        ResolvedExitInfo? committed = null;
        double maxRateAfterApproachLeg = 0;
        double maxMeasuredAfterApproachLeg = 0;
        int ticksAfterApproachLeg = 0;
        double previousGs = aircraft.GroundSpeed;
        for (int t = 1; t <= 400; t++)
        {
            engine.TickOneSecond();
            double gs = aircraft.GroundSpeed;
            double delta = gs - previousGs;
            previousGs = gs;

            if (aircraft.Phases?.CurrentPhase is LandingPhase { CandidateExit: { } candidate })
            {
                committed = candidate;
                continue;
            }

            if (aircraft.Phases?.CurrentPhase is not RunwayExitPhase { IsOnCenterline: false } exit)
            {
                if (aircraft.Phases?.CurrentPhase is HoldingAfterExitPhase)
                {
                    break;
                }

                continue;
            }

            if (!exit.IsOnApproachLeg)
            {
                ticksAfterApproachLeg++;
                maxRateAfterApproachLeg = Math.Max(maxRateAfterApproachLeg, aircraft.Targets.DesiredDecelRate ?? taxiRate);
                maxMeasuredAfterApproachLeg = Math.Max(maxMeasuredAfterApproachLeg, -delta);
            }
        }

        Assert.NotNull(committed);
        Assert.Equal(ExitTaxiway, committed.TaxiwayName);
        Assert.True(ticksAfterApproachLeg > 0, "the C172 never left the approach leg of its exit route");

        double halfLengthFt = AircraftLength.ResolveFt("C172") / 2.0;
        double pastBarFt = GeoMath.DistanceNm(aircraft.Position, committed.HoldShortNode.Position) * GeoMath.FeetPerNm;

        Assert.True(aircraft.GroundSpeed < 1.0, $"the C172 should have stopped at the bar, gs={aircraft.GroundSpeed:F1}");
        Assert.True(pastBarFt <= halfLengthFt + 10.0, $"stopped {pastBarFt:F1} ft past the bar, half its length {halfLengthFt:F1} ft");
        Assert.True(maxRateAfterApproachLeg <= taxiRate + 1e-6, $"braked at up to {maxRateAfterApproachLeg:F2} kt/s past the approach leg");
        Assert.True(
            maxMeasuredAfterApproachLeg <= taxiRate + 0.05,
            $"decelerated at up to {maxMeasuredAfterApproachLeg:F2} kt/s past the approach leg"
        );
    }

    // -- The stop past the bar on a two-node exit --

    /// <summary>
    /// One run of <see cref="RunB738OnTwoNodeW5Exit"/>: the exit's bar, the rate the turn-off flew at, where the aircraft
    /// stopped, the most it published and measured braking on the exit route, the speed its first tick planned beside the
    /// taxi-rate stopping curve to the tail-clear point there, and — when the run expedited — the aircraft and exit-phase
    /// snapshots taken just after the <c>EXP</c>.
    /// </summary>
    private sealed record TwoNodeExitRun(
        GroundNode HoldShort,
        double TurnOffRate,
        LatLon StopPosition,
        (double Published, double Measured) MaxBraking,
        (double Target, double StopCurve) FirstTick,
        (AircraftSnapshotDto Aircraft, RunwayExitPhaseDto Exit)? SnapshotAfterExpedite
    );

    /// <summary>
    /// A B738 restored into <see cref="RunwayExitPhase"/> on the first exit edge of OAK 30 W5, a quarter of the way from the
    /// branch to the bar on the two-node path (branch, bar), committed at its comfortable exit rate (a turn-off rate), and flown
    /// until the exit completes. It starts at the speed from which the taxi rate stops it exactly at the tail-clear point past
    /// the bar, so only a stop planned from the first exit edge on makes that point. With <paramref name="expedite"/>, a bare
    /// <c>EXP</c> (<see cref="AircraftGroundOps.IsExpeditingExit"/>) goes in after the first tick — mid-way through the edge,
    /// whose speed plan was built for the non-expedited turn-off. Null without navdata, the layout or W5.
    /// </summary>
    private static TwoNodeExitRun? RunB738OnTwoNodeW5Exit(bool expedite)
    {
        AirportGroundLayout? layout = LoadOakLayout();
        RunwayInfo? runway = NavigationDatabase.InstanceOrNull?.GetRunway("OAK", "30");
        if ((layout is null) || (runway is null) || (Oak30W5For(layout, runway, AircraftCategory.Jet) is not { } exit))
        {
            return null;
        }

        double turnOffRate = CategoryPerformance.ComfortableExitDecelRate(AircraftCategory.Jet);
        var phase = RunwayExitPhase.FromSnapshot(
            new RunwayExitPhaseDto
            {
                Status = (int)PhaseStatus.Active,
                ElapsedSeconds = 4.0,
                ReachedExitNode = true,
                ExitNodeId = exit.HoldShort.Id,
                ExitTaxiway = "W5",
                RunwayId = runway.Designator,
                ExitSpeed = CategoryPerformance.RolloutCoastSpeed(AircraftCategory.Jet),
                TimeSinceLastLog = 0.0,
                RunwayHeadingDeg = runway.TrueHeading.Degrees,
                ExitStateValue = (int)RunwayExitPhase.ExitState.FollowingExitPath,
                TurnStarted = true,
                TurnOffDecelRate = turnOffRate,
                ExitWaypointIndex = 1,
                ExitWaypointNodeIds = [exit.Branch.Id, exit.HoldShort.Id],
            },
            layout
        );

        double edgeNm = GeoMath.DistanceNm(exit.Branch.Position, exit.HoldShort.Position);
        double bearing = GeoMath.BearingTo(exit.Branch.Position, exit.HoldShort.Position);
        LatLon start = GeoMath.ProjectPoint(exit.Branch.Position, new TrueHeading(bearing), 0.25 * edgeNm);
        double toTailClearNm = (0.75 * edgeNm) + (AircraftLength.ResolveFt("B738") / 2.0 / GeoMath.FeetPerNm);
        double stopCurveKts = Math.Sqrt(2.0 * CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet) * toTailClearNm * 3600.0);
        double startKts = Math.Min(stopCurveKts, CategoryPerformance.TaxiSpeed(AircraftCategory.Jet));
        AircraftState aircraft = MakeLandedAircraftOfType("B738", start.Lat, start.Lon, bearing, startKts);
        aircraft.Ground.Layout = layout;
        PhaseContext ctx = CtxFor(aircraft, runway, layout, AircraftCategory.Jet);

        (AircraftSnapshotDto, RunwayExitPhaseDto)? snapshot = null;
        (double Target, double StopCurve)? firstTick = null;
        (double Published, double Measured) maxBraking = FlyExitToCompletion(
            phase,
            ctx,
            exitPhase =>
            {
                if (firstTick is null)
                {
                    double toStopNm = GeoMath.DistanceNm(aircraft.Position, exit.HoldShort.Position) + (toTailClearNm - (0.75 * edgeNm));
                    double curve = Math.Sqrt(2.0 * CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet) * toStopNm * 3600.0);
                    firstTick = (ctx.Targets.TargetSpeed ?? double.PositiveInfinity, curve);
                }

                if (expedite && (snapshot is null))
                {
                    aircraft.Ground.IsExpeditingExit = true;
                    snapshot = (aircraft.ToSnapshot(), Assert.IsType<RunwayExitPhaseDto>(exitPhase.ToSnapshot()));
                }
            }
        );

        Assert.NotNull(firstTick);
        return new TwoNodeExitRun(exit.HoldShort, turnOffRate, aircraft.Position, maxBraking, firstTick.Value, snapshot);
    }

    /// <summary>
    /// Ticks <paramref name="phase"/> (physics, then the phase, as the engine does) until the exit completes, calling
    /// <paramref name="afterTick"/> after every tick that leaves it running. Returns the most the phase published and the most
    /// the aircraft measurably decelerated in one tick while the exit ran. The completing tick is left out, as in
    /// <see cref="C172InstructedExit_BrakesAtTheTaxiRatePastTheApproachLeg_AndStopsJustPastTheBar"/>:
    /// <c>RunwayExitPhase.CompleteExit</c> zeroes the speed on arrival, whatever is left of it at the one-second tick.
    /// </summary>
    private static (double Published, double Measured) FlyExitToCompletion(RunwayExitPhase phase, PhaseContext ctx, Action<RunwayExitPhase> afterTick)
    {
        double taxiRate = CategoryPerformance.TaxiDecelRate(ctx.Category);
        double maxPublished = 0;
        double maxMeasured = 0;
        bool complete = false;
        for (int t = 1; (t <= 300) && !complete; t++)
        {
            double previousGs = ctx.Aircraft.GroundSpeed;
            FlightPhysics.Update(ctx.Aircraft, ctx.DeltaSeconds);
            complete = phase.OnTick(ctx);
            if (!complete)
            {
                maxMeasured = Math.Max(maxMeasured, previousGs - ctx.Aircraft.GroundSpeed);
                maxPublished = Math.Max(maxPublished, ctx.Targets.DesiredDecelRate ?? taxiRate);
                afterTick(phase);
            }
        }

        Assert.True(complete, "the exit never completed");
        return (maxPublished, maxMeasured);
    }

    private static double PastBarFt(TwoNodeExitRun run, LatLon stop) => GeoMath.DistanceNm(stop, run.HoldShort.Position) * GeoMath.FeetPerNm;

    /// <summary>
    /// On a two-node exit (branch, bar) the route-end stop past the bar is planned from the turn-off on, whatever rate the
    /// turn-off flies at: a B738 committed to OAK 30 W5 at its comfortable exit rate plans its first tick on the first exit edge
    /// no faster than the taxi rate's stopping curve to the tail-clear point, stops with its tail clear of the bar and no more
    /// than 10 ft beyond, and never brakes harder than the larger of its turn-off rate and its taxi (stop) rate on the way.
    /// </summary>
    [Fact]
    public void TwoNodeExit_WithATurnOffRate_StopsAtTheTailClearPoint_NoHarderThanTheTurnOffOrStopRate()
    {
        TwoNodeExitRun? run = RunB738OnTwoNodeW5Exit(expedite: false);
        if (run is null)
        {
            return;
        }

        double halfLengthFt = AircraftLength.ResolveFt("B738") / 2.0;
        double limit = Math.Max(run.TurnOffRate, CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet));
        double pastBarFt = PastBarFt(run, run.StopPosition);

        Assert.True(
            run.FirstTick.Target <= run.FirstTick.StopCurve + 0.5,
            $"the first exit edge planned {run.FirstTick.Target:F1} kt, above the {run.FirstTick.StopCurve:F1} kt that stops at the tail-clear point"
        );
        Assert.True(pastBarFt <= halfLengthFt + 10.0, $"stopped {pastBarFt:F1} ft past the bar, tail-clear point {halfLengthFt:F1} ft");
        Assert.True(pastBarFt >= halfLengthFt, $"stopped {pastBarFt:F1} ft past the bar, short of the {halfLengthFt:F1} ft tail-clear point");
        Assert.True(
            run.MaxBraking.Published <= limit + 1e-6,
            $"braked at up to {run.MaxBraking.Published:F2} kt/s on the exit route, limit {limit:F2}"
        );
        Assert.True(
            run.MaxBraking.Measured <= limit + 0.05,
            $"decelerated at up to {run.MaxBraking.Measured:F2} kt/s on the exit route, limit {limit:F2}"
        );
    }

    /// <summary>
    /// A bare <c>EXP</c> issued mid-way through the first exit edge of a two-node exit, whose speed plan was built for the
    /// non-expedited turn-off, still stops at the tail-clear point past the bar; and the aircraft restored from snapshots taken
    /// just after the <c>EXP</c> stops where the live run did.
    /// </summary>
    [Fact]
    public void TwoNodeExit_ExpeditedMidTurnOff_StillStopsAtTheTailClearPoint_AndARestoreStopsThereToo()
    {
        TwoNodeExitRun? run = RunB738OnTwoNodeW5Exit(expedite: true);
        if (run is null)
        {
            return;
        }

        double halfLengthFt = AircraftLength.ResolveFt("B738") / 2.0;
        double pastBarFt = PastBarFt(run, run.StopPosition);
        Assert.True(pastBarFt <= halfLengthFt + 10.0, $"stopped {pastBarFt:F1} ft past the bar, tail-clear point {halfLengthFt:F1} ft");
        Assert.True(pastBarFt >= halfLengthFt, $"stopped {pastBarFt:F1} ft past the bar, short of the {halfLengthFt:F1} ft tail-clear point");

        AirportGroundLayout layout = LoadOakLayout()!;
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("OAK", "30")!;
        (AircraftSnapshotDto aircraftDto, RunwayExitPhaseDto exitDto) = run.SnapshotAfterExpedite!.Value;
        var restored = AircraftState.FromSnapshot(aircraftDto, layout);
        restored.Ground.Layout = layout;
        FlyExitToCompletion(RunwayExitPhase.FromSnapshot(exitDto, layout), CtxFor(restored, runway, layout, AircraftCategory.Jet), _ => { });

        double driftFt = GeoMath.DistanceNm(restored.Position, run.StopPosition) * GeoMath.FeetPerNm;
        Assert.True(driftFt <= 1.0, $"the restored run stopped {driftFt:F1} ft from the live run's stop");
    }

    // -- Unable to make the exit the controller named --

    /// <summary>A C172 landing on OAK 30 in solo training, and every terminal line it has transmitted since it was watched.</summary>
    private sealed record SoloArrival(SimulationEngine Engine, AircraftState Aircraft, RunwayInfo Runway, List<TerminalEntry> Calls)
    {
        /// <summary>The pilot's lines naming <paramref name="taxiway"/> as a whole word.</summary>
        public List<string> CallsNaming(string taxiway) =>
            [.. Calls.Where(e => Regex.IsMatch(e.Message, $@"\b{Regex.Escape(taxiway)}\b")).Select(e => e.Message)];

        /// <summary>The pilot's "unable" calls, in order.</summary>
        public List<string> UnableCalls() =>
            [.. Calls.Where(e => e.Message.StartsWith("unable", StringComparison.OrdinalIgnoreCase)).Select(e => e.Message)];
    }

    private static SoloArrival? SpawnSoloOnOak30(string aircraftType) => SpawnSoloAtOak("30", aircraftType);

    private static SoloArrival? SpawnSoloAtOak(string runwayDesignator, string aircraftType) => SpawnSolo("OAK", runwayDesignator, aircraftType);

    /// <summary>
    /// Spawns <paramref name="aircraftType"/> on short final for <paramref name="runwayDesignator"/>, cleared to land, in solo training.
    /// </summary>
    private static SoloArrival? SpawnSolo(string airport, string runwayDesignator, string aircraftType)
    {
        ShortFinalArrival.Spawned? spawned = ShortFinalArrival.SpawnClearedToLand(airport, runwayDesignator, aircraftType, "N172SE");
        if (spawned is null)
        {
            return null;
        }

        (SimulationEngine engine, AircraftState aircraft, RunwayInfo runway) = spawned;
        return WatchSolo(engine, aircraft, runway);
    }

    /// <summary>Puts <paramref name="engine"/> in solo training at tower, so the pilot transmits, and records its lines.</summary>
    private static SoloArrival WatchSolo(SimulationEngine engine, AircraftState aircraft, RunwayInfo runway)
    {
        engine.Scenario!.SoloTrainingMode = true;
        engine.Scenario.StudentPositionType = "TWR";
        Assert.True(engine.Scenario.PilotContacts.AnyAnswering);

        // The engine drains the solo pilot's queued transmissions to the terminal every tick.
        List<TerminalEntry> calls = [];
        engine.TerminalEntryEmitted += entry =>
        {
            if ((entry.Callsign == aircraft.Callsign) && (entry.Kind == "SayPilot"))
            {
                calls.Add(entry);
            }
        };
        return new SoloArrival(engine, aircraft, runway, calls);
    }

    /// <summary>Restores <paramref name="snapshot"/> into a fresh solo-training engine at OAK.</summary>
    private static SoloArrival RestoreSoloArrival(AircraftSnapshotDto snapshot, RunwayInfo runway)
    {
        var groundData = new TestAirportGroundData();
        return StartSoloEngineAtOak(groundData, AircraftState.FromSnapshot(snapshot, groundData.GetLayout("OAK")!), runway);
    }

    /// <summary>Puts <paramref name="aircraft"/>, on OAK's ground layout, into a fresh solo-training engine at OAK and watches it.</summary>
    private static SoloArrival StartSoloEngineAtOak(TestAirportGroundData groundData, AircraftState aircraft, RunwayInfo runway)
    {
        aircraft.Ground.Layout = groundData.GetLayout("OAK")!;
        var engine = new SimulationEngine(groundData);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-unable-exit-restore",
            ScenarioName = "Unable exit restore",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
        };
        return WatchSolo(engine, aircraft, runway);
    }

    /// <summary>
    /// True when <paramref name="exit"/> is still ahead of the C172 and making its turn-off speed by the branch would need more
    /// than the piston firm braking rate.
    /// </summary>
    private static bool NeedsMoreThanFirm(AircraftState aircraft, RunwayInfo runway, AirportGroundLayout.CenterlineExitResult exit)
    {
        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
        double turnOff = CategoryPerformance.ExitTurnOffSpeed(category, exit.ExitAngle);
        double toBranchNm = GeoMath.AlongTrackDistanceNm(exit.Path[0].Position, aircraft.Position, runway.TrueHeading);
        return (toBranchNm > 0)
            && (aircraft.GroundSpeed > turnOff)
            && (
                RolloutBraking.RequiredDecelKtsPerSec(aircraft.GroundSpeed, turnOff, toBranchNm, category)
                > CategoryPerformance.FirmBrakingRate(category)
            );
    }

    /// <summary>The nearest exit ahead, other than <paramref name="exceptTaxiway"/>, that the C172 cannot make at its firm rate.</summary>
    private static AirportGroundLayout.CenterlineExitResult? ExitAheadBeyondFirm(SoloArrival arrival, string? exceptTaxiway) =>
        arrival.Aircraft.IsOnGround
            ? arrival.Aircraft.Ground.Layout!.FindOnSidePreferredExit(
                arrival.Aircraft.Position.Lat,
                arrival.Aircraft.Position.Lon,
                arrival.Runway.TrueHeading,
                arrival.Runway.Designator,
                preference: null,
                sidePref: null,
                filter: exit =>
                    (exit.Taxiway != exceptTaxiway) && NeedsMoreThanFirm(arrival.Aircraft, arrival.Runway, exit)
                        ? AirportGroundLayout.CandidateVerdict.Accept
                        : AirportGroundLayout.CandidateVerdict.Skip
            )
            : null;

    /// <summary>Rolls the arrival out uninstructed until an exit still ahead needs more than the firm rate to make.</summary>
    private static AirportGroundLayout.CenterlineExitResult RollUntilAnExitIsBeyondFirm(SoloArrival arrival)
    {
        AirportGroundLayout.CenterlineExitResult? found = null;
        for (int t = 1; (t <= 180) && (found is null) && (arrival.Aircraft.Phases?.CurrentPhase is not RunwayExitPhase); t++)
        {
            arrival.Engine.TickOneSecond();
            found = ExitAheadBeyondFirm(arrival, exceptTaxiway: null);
        }

        Assert.True(found is not null, "no exit ahead of the C172 on its rollout needed more than its firm rate");
        Assert.IsType<LandingPhase>(arrival.Aircraft.Phases?.CurrentPhase);
        return found.Value;
    }

    /// <summary>Ticks until the pilot has said <paramref name="call"/>, at most 30 s.</summary>
    private static void TickUntilCall(SoloArrival arrival, string call)
    {
        for (int t = 1; (t <= 30) && !arrival.UnableCalls().Contains(call); t++)
        {
            arrival.Engine.TickOneSecond();
        }

        Assert.Contains(call, arrival.UnableCalls());
    }

    /// <summary>Flies the arrival until it holds after its exit; returns the hold-short node id of the exit it took.</summary>
    private static int FlyToHoldingAfterExit(SoloArrival arrival) => FlyToHoldingAfterExitWatched(arrival).HoldShortId;

    /// <summary>
    /// What an arrival did between now and holding after its exit: the hold-short it stopped at and the taxiway of that exit, where
    /// it was when the landing handed off to the runway exit, the taxiways given up as of that hand-off (the set the runway exit
    /// reads; it is cleared when the exit completes), the hardest braking the landing roll planned, and every exit it was a
    /// candidate for.
    /// </summary>
    private sealed record ExitFlight(
        int HoldShortId,
        string? TakenTaxiway,
        LatLon HandOffPosition,
        HashSet<string> GivenUpAtHandOff,
        double MaxRolloutDecelRate,
        HashSet<string> RolloutCandidates
    );

    /// <summary>Asserts the exit <paramref name="flight"/> took is not on <paramref name="taxiway"/>, by the taxiway's name.</summary>
    private static void AssertExitNotOn(ExitFlight flight, string taxiway)
    {
        Assert.NotNull(flight.TakenTaxiway);
        Assert.False(
            string.Equals(flight.TakenTaxiway, taxiway, StringComparison.OrdinalIgnoreCase),
            $"the aircraft exited at {taxiway} (bar {flight.HoldShortId}), the exit it said it was unable to make"
        );
    }

    private static ExitFlight FlyToHoldingAfterExitWatched(SoloArrival arrival)
    {
        int? holdShortId = null;
        string? takenTaxiway = null;
        LatLon? handOff = null;
        HashSet<string>? givenUpAtHandOff = null;
        double maxRolloutDecel = 0;
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int t = 1; (t <= 400) && (arrival.Aircraft.Phases?.CurrentPhase is not HoldingAfterExitPhase); t++)
        {
            arrival.Engine.TickOneSecond();
            switch (arrival.Aircraft.Phases?.CurrentPhase)
            {
                case LandingPhase landing:
                    maxRolloutDecel = Math.Max(maxRolloutDecel, arrival.Aircraft.Targets.DesiredDecelRate ?? 0);
                    if (landing.CandidateExit is { } candidate)
                    {
                        candidates.Add(candidate.TaxiwayName);
                    }

                    break;
                case RunwayExitPhase exit:
                    handOff ??= arrival.Aircraft.Position;
                    givenUpAtHandOff ??= new HashSet<string>(arrival.Aircraft.Phases.GivenUpExitTaxiways, StringComparer.OrdinalIgnoreCase);
                    if (exit.TargetHoldShortNodeId is { } id)
                    {
                        holdShortId = id;
                        takenTaxiway = exit.ExitTaxiway;
                    }

                    break;
            }
        }

        Assert.IsType<HoldingAfterExitPhase>(arrival.Aircraft.Phases?.CurrentPhase);
        Assert.NotNull(holdShortId);
        Assert.NotNull(handOff);
        Assert.NotNull(givenUpAtHandOff);
        return new ExitFlight(holdShortId.Value, takenTaxiway, handOff.Value, givenUpAtHandOff, maxRolloutDecel, candidates);
    }

    /// <summary>
    /// Rolls the arrival to touchdown, instructs <c>EXIT <paramref name="taxiway"/></c> there and ticks once, so the rollout has the
    /// exit cached as its candidate; returns that candidate.
    /// </summary>
    private static ResolvedExitInfo InstructAndCacheExit(SoloArrival arrival, string taxiway)
    {
        RollUntil(arrival, () => arrival.Aircraft.IsOnGround ? true : (bool?)null, "touchdown");
        CommandResult exitResult = arrival.Engine.SendCommand(arrival.Aircraft.Callsign, $"EXIT {taxiway}");
        Assert.True(exitResult.Success, $"EXIT {taxiway} at touchdown was refused: {exitResult.Message}");
        arrival.Engine.TickOneSecond();

        LandingPhase landing = Assert.IsType<LandingPhase>(arrival.Aircraft.Phases!.CurrentPhase);
        ResolvedExitInfo candidate = Assert.IsType<ResolvedExitInfo>(landing.CandidateExit);
        Assert.Equal(taxiway, candidate.TaxiwayName);
        return candidate;
    }

    /// <summary>
    /// Puts the aircraft on the runway centerline <paramref name="shortFt"/> before <paramref name="branch"/>, on the runway heading
    /// at <paramref name="iasKts"/> of wheel speed: the state a rollout is in when it reaches that point at that speed.
    /// </summary>
    private static void PlaceOnCenterlineBefore(SoloArrival arrival, GroundNode branch, double shortFt, double iasKts)
    {
        arrival.Aircraft.Position = GeoMath.ProjectPoint(branch.Position, arrival.Runway.TrueHeading.ToReciprocal(), shortFt / GeoMath.FeetPerNm);
        arrival.Aircraft.TrueHeading = arrival.Runway.TrueHeading;
        GroundFrame.EnterGround(arrival.Aircraft, iasKts);
    }

    /// <summary>The nearest connection of <paramref name="taxiway"/> the centerline walk from <paramref name="position"/> reaches, if any.</summary>
    private static AirportGroundLayout.CenterlineExitResult? NamedConnectionAheadOf(SoloArrival arrival, LatLon position, string taxiway) =>
        arrival.Aircraft.Ground.Layout!.FindOnSidePreferredExit(
            position.Lat,
            position.Lon,
            arrival.Runway.TrueHeading,
            arrival.Runway.Designator,
            new ExitPreference { Taxiway = taxiway },
            sidePref: null
        );

    /// <summary>
    /// A C172 rolling out on OAK 30 told to exit at a taxiway it could make only by braking harder than its firm rate refuses
    /// the instruction up front — "unable <c>taxiway</c>", no readback — keeps no preference for it, and leaves the runway at an
    /// exit farther down than the refused exit's branch.
    /// </summary>
    [Fact]
    public void InstructedExitAlreadyPastTheFirmRate_IsRefusedUpFront_AndTheAircraftTakesALaterExit()
    {
        SoloArrival? arrival = SpawnSoloOnOak30("C172");
        if (arrival is null)
        {
            return;
        }

        AirportGroundLayout.CenterlineExitResult tooClose = RollUntilAnExitIsBeyondFirm(arrival);
        string named = tooClose.Taxiway;
        arrival.Calls.Clear();

        CommandResult exitResult = arrival.Engine.SendCommand(arrival.Aircraft.Callsign, $"EXIT {named}");
        Assert.False(exitResult.Success, $"EXIT {named} was accepted: {exitResult.Message}");
        Assert.Null(arrival.Aircraft.Phases!.RequestedExit);

        int takenHoldShortId = FlyToHoldingAfterExit(arrival);

        Assert.Equal([$"unable {named}."], arrival.CallsNaming(named));
        Assert.Equal($"unable {named}.", exitResult.Message);
        GroundNode taken = arrival.Aircraft.Ground.Layout!.Nodes[takenHoldShortId];
        double beyondBranchNm = GeoMath.AlongTrackDistanceNm(taken.Position, tooClose.Path[0].Position, arrival.Runway.TrueHeading);
        Assert.True(beyondBranchNm > 0, $"the exit taken (bar {takenHoldShortId}) is not farther down the runway than {named}'s branch");
    }

    /// <summary>
    /// An exit read back cleanly on final that is beyond the B738's firm rate once it is rolling out gets the "unable" call on
    /// the tick the rollout decides it cannot make it — before the aircraft reaches the exit's branch — and only once.
    /// </summary>
    [Fact]
    public void InstructedExitReadBackOnFinal_ThatBecomesUnmakeable_GetsTheUnableCallBeforeTheBranch()
    {
        SoloArrival? scout = SpawnSoloOnOak30("B738");
        if (scout is null)
        {
            return;
        }

        AirportGroundLayout.CenterlineExitResult tooClose = RollUntilAnExitIsBeyondFirm(scout);
        string named = tooClose.Taxiway;

        SoloArrival arrival = SpawnSoloOnOak30("B738")!;
        CommandResult exitResult = arrival.Engine.SendCommand(arrival.Aircraft.Callsign, $"EXIT {named}");
        Assert.True(exitResult.Success, $"EXIT {named} on final failed: {exitResult.Message}");

        LatLon? positionAtCall = null;
        for (int t = 1; (t <= 400) && (arrival.Aircraft.Phases?.CurrentPhase is not HoldingAfterExitPhase); t++)
        {
            arrival.Engine.TickOneSecond();
            if ((positionAtCall is null) && arrival.UnableCalls().Contains($"unable {named}."))
            {
                positionAtCall = arrival.Aircraft.Position;
            }
        }

        Assert.True(
            positionAtCall is not null,
            $"the B738 never said it was unable to make {named}; it said: {string.Join(" | ", arrival.Calls.Select(c => c.Message))}"
        );
        double toBranchNm = GeoMath.AlongTrackDistanceNm(tooClose.Path[0].Position, positionAtCall.Value, arrival.Runway.TrueHeading);
        Assert.True(toBranchNm > 0, $"the unable call came {-toBranchNm * GeoMath.FeetPerNm:F0} ft past {named}'s branch");
        Assert.Equal([$"unable {named}."], arrival.UnableCalls());
    }

    /// <summary>
    /// Two exit instructions in a row, each beyond a B738's firm rate once accepted, get one "unable" call each; and the
    /// aircraft restored from a snapshot taken after the first call, with the first exit still the one requested, rolls past
    /// it without calling again: the restored call flag, not a new instruction, is what keeps it from repeating.
    /// </summary>
    [Fact]
    public void TwoUnmakeableInstructedExits_GetOneCallEach_AndARestoreKeepsTheFirstCallMade()
    {
        SoloArrival? arrival = SpawnSoloOnOak30("B738");
        if (arrival is null)
        {
            return;
        }

        string first = RollUntilAnExitIsBeyondFirm(arrival).Taxiway;
        arrival.Aircraft.Phases!.RequestedExit = new ExitPreference { Taxiway = first };
        TickUntilCall(arrival, $"unable {first}.");

        AircraftSnapshotDto snapshot = arrival.Aircraft.ToSnapshot();
        AirportGroundLayout.CenterlineExitResult? next = ExitAheadBeyondFirm(arrival, exceptTaxiway: first);
        Assert.True(next is not null, $"no second exit past {first} was still beyond the firm rate");
        string second = next.Value.Taxiway;

        arrival.Aircraft.Phases.RequestedExit = new ExitPreference { Taxiway = second };
        FlyToHoldingAfterExit(arrival);
        Assert.Equal([$"unable {first}.", $"unable {second}."], arrival.UnableCalls());

        SoloArrival restored = RestoreSoloArrival(snapshot, arrival.Runway);
        Assert.Equal(first, restored.Aircraft.Phases!.RequestedExit?.Taxiway);
        FlyToHoldingAfterExit(restored);
        Assert.Empty(restored.UnableCalls());
    }

    /// <summary>
    /// A given-up instructed exit still ahead when the rollout reaches its hand-off is not the exit the runway exit takes: a C172
    /// restored just after its "unable" call, slowed to coast speed with the exit's branch still ahead, hands off with the branch
    /// ahead, runs the runway exit to its hold, and stops at a bar that is not the given-up taxiway's, with no second call.
    /// </summary>
    [Fact]
    public void GivenUpInstructedExit_StillAheadAtTheHandOff_IsNotTheExitTaken()
    {
        SoloArrival? arrival = SpawnSoloOnOak30("C172");
        if (arrival is null)
        {
            return;
        }

        AirportGroundLayout.CenterlineExitResult tooClose = RollUntilAnExitIsBeyondFirm(arrival);
        string named = tooClose.Taxiway;
        arrival.Aircraft.Phases!.RequestedExit = new ExitPreference { Taxiway = named };
        TickUntilCall(arrival, $"unable {named}.");

        // Staged: the rollout naturally passes the given-up exit's branch before it is down to coast speed, so the restored
        // aircraft is slowed to coast speed with the branch still ahead — the state in which the hand-off would commit it.
        SoloArrival staged = RestoreSoloArrival(arrival.Aircraft.ToSnapshot(), arrival.Runway);
        LandingPhase landing = Assert.IsType<LandingPhase>(staged.Aircraft.Phases!.CurrentPhase);
        staged.Aircraft.IndicatedAirspeed = Math.Min(staged.Aircraft.IndicatedAirspeed, landing.Plan!.CoastSpeed);

        ExitFlight flight = FlyToHoldingAfterExitWatched(staged);

        double toBranchNm = GeoMath.AlongTrackDistanceNm(tooClose.Path[0].Position, flight.HandOffPosition, arrival.Runway.TrueHeading);
        Assert.True(toBranchNm > 0, $"the hand-off came {-toBranchNm * GeoMath.FeetPerNm:F0} ft past {named}'s branch, so the case is not staged");
        AssertExitNotOn(flight, named);
        Assert.Empty(staged.UnableCalls());
    }

    /// <summary>
    /// A bare <c>EXP</c> after the crew has said it is unable to make the named exit does not revive it, even when the exit is
    /// within the B738's max-effort rate at that moment: the rollout never makes it a candidate again, brakes no harder than the
    /// exit would have needed, and leaves the runway elsewhere.
    /// </summary>
    [Fact]
    public void BareExpediteAfterTheUnableCall_DoesNotReviveTheGivenUpExit()
    {
        SoloArrival? arrival = SpawnSoloOnOak30("B738");
        if (arrival is null)
        {
            return;
        }

        AirportGroundLayout.CenterlineExitResult tooClose = RollUntilAnExitIsBeyondFirm(arrival);
        string named = tooClose.Taxiway;
        arrival.Aircraft.Phases!.RequestedExit = new ExitPreference { Taxiway = named };
        TickUntilCall(arrival, $"unable {named}.");

        ExitReachFacts reach = ReachOf(arrival.Aircraft, arrival.Runway, tooClose);
        double expediteRate = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Jet);
        Assert.True(
            reach.MakeableWithin(expediteRate),
            $"{named} already needs {reach.RequiredDecel:F2} kt/s, past the expedite rate {expediteRate:F1}, so the case is not staged"
        );

        CommandResult expedite = arrival.Engine.SendCommand(arrival.Aircraft.Callsign, "EXP");
        Assert.True(expedite.Success, $"EXP was refused: {expedite.Message}");

        ExitFlight flight = FlyToHoldingAfterExitWatched(arrival);

        Assert.DoesNotContain(named, flight.RolloutCandidates);
        AssertExitNotOn(flight, named);
        Assert.True(
            flight.MaxRolloutDecelRate < reach.RequiredDecel,
            $"after EXP the rollout braked at {flight.MaxRolloutDecelRate:F2} kt/s, at least what {named} needed ({reach.RequiredDecel:F2})"
        );
        Assert.Equal([$"unable {named}."], arrival.UnableCalls());
    }

    /// <summary>
    /// A new <c>EXIT … EXP</c> naming the exit the crew has given up is judged afresh: within the B738's max-effort rate it is
    /// accepted, read back and taken.
    /// </summary>
    [Fact]
    public void NewExpeditedInstructionForTheGivenUpExit_IsJudgedAfresh_AndTaken()
    {
        SoloArrival? arrival = SpawnSoloOnOak30("B738");
        if (arrival is null)
        {
            return;
        }

        AirportGroundLayout.CenterlineExitResult tooClose = RollUntilAnExitIsBeyondFirm(arrival);
        string named = tooClose.Taxiway;
        arrival.Aircraft.Phases!.RequestedExit = new ExitPreference { Taxiway = named };
        TickUntilCall(arrival, $"unable {named}.");

        ExitReachFacts reach = ReachOf(arrival.Aircraft, arrival.Runway, tooClose);
        double expediteRate = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Jet);
        Assert.True(
            reach.MakeableWithin(expediteRate - 0.2),
            $"{named} already needs {reach.RequiredDecel:F2} kt/s, too close to the expedite rate {expediteRate:F1} to stage the case"
        );

        CommandResult exitResult = arrival.Engine.SendCommand(arrival.Aircraft.Callsign, $"EXIT {named} EXP");
        Assert.True(exitResult.Success, $"EXIT {named} EXP after the unable call was refused: {exitResult.Message}");
        Assert.Equal(named, arrival.Aircraft.Phases.RequestedExit?.Taxiway);

        int takenHoldShortId = FlyToHoldingAfterExit(arrival);
        Assert.Equal(tooClose.HoldShort.Id, takenHoldShortId);
    }

    /// <summary>
    /// The controller repeats <c>EXIT</c> for the exit the aircraft is already braking for after it has become unmakeable: the
    /// repeat is refused up front with the crew's "unable", which is the only "unable" the crew says — the exit is given up at
    /// that moment, not again at its branch — and the aircraft takes a later exit. Staged by raising the B738's speed after the
    /// instruction is accepted and cached as its candidate.
    /// </summary>
    [Fact]
    public void RepeatedInstructionForAnExitThatBecameUnmakeable_GetsOneUnableCall_AndTheAircraftTakesALaterExit()
    {
        SoloArrival? arrival = SpawnSoloOnOak30("B738");
        if (arrival is null)
        {
            return;
        }

        double firmRate = CategoryPerformance.FirmBrakingRate(AircraftCategory.Jet);
        AirportGroundLayout.CenterlineExitResult exit = RollUntil(
            arrival,
            () =>
                ExitWhere(
                    arrival,
                    candidate =>
                    {
                        ExitReachFacts reach = ReachOf(arrival.Aircraft, arrival.Runway, candidate);
                        return reach.NeedsMoreThan(2.5) && reach.MakeableWithin(firmRate);
                    }
                ),
            "an exit makeable at its firm rate"
        );
        string named = exit.Taxiway;

        CommandResult first = arrival.Engine.SendCommand(arrival.Aircraft.Callsign, $"EXIT {named}");
        Assert.True(first.Success, $"EXIT {named} was refused while makeable: {first.Message}");
        arrival.Engine.TickOneSecond();
        LandingPhase landing = Assert.IsType<LandingPhase>(arrival.Aircraft.Phases!.CurrentPhase);
        Assert.Equal(named, landing.CandidateExit?.TaxiwayName);

        // required = (v² − turnOff²) / (7200 · distanceNm): the speed from which the exit needs half a knot per second more
        // than the firm rate from here.
        ExitReachFacts before = ReachOf(arrival.Aircraft, arrival.Runway, exit);
        double turnOff = CategoryPerformance.ExitTurnOffSpeed(AircraftCategory.Jet, exit.ExitAngle);
        arrival.Aircraft.IndicatedAirspeed = Math.Sqrt((turnOff * turnOff) + ((firmRate + 0.5) * 7200.0 * before.ToBranchNm));
        Assert.True(ReachOf(arrival.Aircraft, arrival.Runway, exit).NeedsMoreThan(firmRate), $"{named} is still makeable at the firm rate");

        CommandResult repeat = arrival.Engine.SendCommand(arrival.Aircraft.Callsign, $"EXIT {named}");
        Assert.False(repeat.Success, $"the repeated EXIT {named} was accepted: {repeat.Message}");
        Assert.Equal($"unable {named}.", repeat.Message);

        int takenHoldShortId = FlyToHoldingAfterExit(arrival);

        Assert.Equal([$"unable {named}."], arrival.UnableCalls());
        Assert.NotEqual(exit.HoldShort.Id, takenHoldShortId);
    }

    /// <summary>
    /// On a runway with hold-short data, <c>EXIT</c> naming a taxiway the aircraft has already rolled well past, or one that never
    /// touches the runway, is refused as no such exit ahead and the pilot says so.
    /// </summary>
    [Fact]
    public void NamedExitWithNoConnectionAhead_IsRefusedAsNoExitAhead()
    {
        SoloArrival? arrival = SpawnSoloOnOak30("B738");
        if (arrival is null)
        {
            return;
        }

        AirportGroundLayout.CenterlineExitResult first = RollUntil(arrival, () => ExitWhere(arrival, _ => true), "its first exit ahead");
        const double WellBehindFt = 500.0;
        RollUntil(
            arrival,
            () => (ReachOf(arrival.Aircraft, arrival.Runway, first).ToBranchNm * GeoMath.FeetPerNm) < -WellBehindFt ? true : (bool?)null,
            $"{WellBehindFt:F0} ft past {first.Taxiway}"
        );

        // C1 is an OAK 28R exit (the `--exits 30` listing has only W1-W7); it never touches runway 30.
        CommandResult behind = arrival.Engine.SendCommand(arrival.Aircraft.Callsign, $"EXIT {first.Taxiway}");
        arrival.Engine.TickOneSecond();
        CommandResult elsewhere = arrival.Engine.SendCommand(arrival.Aircraft.Callsign, "EXIT C1");
        for (int t = 1; (t <= 10) && (arrival.UnableCalls().Count < 2); t++)
        {
            // The solo pilot transmits one line a tick, after its reaction time.
            arrival.Engine.TickOneSecond();
        }

        Assert.False(behind.Success, $"EXIT {first.Taxiway} was accepted {WellBehindFt:F0} ft past it: {behind.Message}");
        Assert.Contains($"no {first.Taxiway} ahead", behind.Message);
        Assert.False(elsewhere.Success, $"EXIT C1 was accepted on runway {arrival.Runway.Designator}: {elsewhere.Message}");
        Assert.Contains("no C1 ahead", elsewhere.Message);
        Assert.Null(arrival.Aircraft.Phases!.RequestedExit);
        Assert.Equal([$"unable, no {first.Taxiway} ahead.", "unable, no C1 ahead."], arrival.UnableCalls());
    }

    /// <summary>
    /// An identical exit instruction issued again after the first was given up is a new instruction — the crew calls "unable"
    /// for it again — and a snapshot taken after the re-issue, before the next tick, restores it as a new instruction too.
    /// </summary>
    [Fact]
    public void ReissuedIdenticalExit_SnapshottedBeforeTheNextTick_RestoresAsANewInstruction()
    {
        SoloArrival? arrival = SpawnSoloOnOak30("C172");
        if (arrival is null)
        {
            return;
        }

        string named = RollUntilAnExitIsBeyondFirm(arrival).Taxiway;
        arrival.Aircraft.Phases!.RequestedExit = new ExitPreference { Taxiway = named };
        TickUntilCall(arrival, $"unable {named}.");

        arrival.Aircraft.Phases.RequestedExit = new ExitPreference { Taxiway = named };
        AircraftSnapshotDto snapshot = arrival.Aircraft.ToSnapshot();
        arrival.Calls.Clear();
        TickUntilCall(arrival, $"unable {named}.");

        SoloArrival restored = RestoreSoloArrival(snapshot, arrival.Runway);
        TickUntilCall(restored, $"unable {named}.");
    }

    /// <summary>
    /// The aircraft restored from a snapshot taken after it gave up an instructed exit keeps it given up: it takes the same
    /// later exit the live run took and makes no further call.
    /// </summary>
    [Fact]
    public void GivenUpInstructedExit_StaysGivenUpAfterARestore()
    {
        SoloArrival? arrival = SpawnSoloOnOak30("C172");
        if (arrival is null)
        {
            return;
        }

        string named = RollUntilAnExitIsBeyondFirm(arrival).Taxiway;
        arrival.Aircraft.Phases!.RequestedExit = new ExitPreference { Taxiway = named };
        TickUntilCall(arrival, $"unable {named}.");

        AircraftSnapshotDto snapshot = arrival.Aircraft.ToSnapshot();
        int liveHoldShortId = FlyToHoldingAfterExit(arrival);

        SoloArrival restored = RestoreSoloArrival(snapshot, arrival.Runway);
        int restoredHoldShortId = FlyToHoldingAfterExit(restored);

        Assert.Equal(liveHoldShortId, restoredHoldShortId);
        Assert.Empty(restored.UnableCalls());
    }

    // -- The "unable" call gives the taxiway up, whichever path makes it, and the runway exit honours that --

    /// <summary>
    /// The "unable" call made at the branch — the missed-exit path, not the up-front refusal — gives the taxiway up for the whole
    /// landing: a C172 told to exit OAK 30 at W4 that reaches W4's branch node just above coast speed, too fast for the turn-off,
    /// says "unable W4" once there, W4 is in the phase list's given-up set the runway exit reads too, and the aircraft leaves the
    /// runway at another exit.
    /// </summary>
    [Fact]
    public void InstructedExitMissedAtItsBranch_IsGivenUpWithTheUnableCall_AndNotTaken()
    {
        SoloArrival? arrival = SpawnSoloOnOak30("C172");
        if (arrival is null)
        {
            return;
        }

        ResolvedExitInfo w4 = InstructAndCacheExit(arrival, "W4");
        double coast = CategoryPerformance.RolloutCoastSpeed(AircraftCategory.Piston);
        PlaceOnCenterlineBefore(arrival, w4.BranchPointNode, shortFt: 10.0, iasKts: coast + 1.0);

        ExitFlight flight = FlyToHoldingAfterExitWatched(arrival);

        Assert.Equal(["unable W4."], arrival.UnableCalls());
        Assert.Contains("W4", flight.GivenUpAtHandOff);
        AssertExitNotOn(flight, "W4");
    }

    /// <summary>
    /// The same on a forced (<c>CLANDF</c>) rollout, whose preference the give-up has to relax itself: a C172 told to exit OAK 30 at
    /// W4 is placed where W4's turn-off speed 300 ft before its branch node needs more than the forced rollout's ceiling, says
    /// "unable W4" once, has W4 in the phase list's given-up set, and leaves the runway at another exit.
    /// </summary>
    [Fact]
    public void ForcedRollout_InstructedExitMissed_IsGivenUpWithTheUnableCall_AndNotTaken()
    {
        SoloArrival? arrival = SpawnSoloOnOak30("C172");
        if (arrival is null)
        {
            return;
        }

        // CLANDF is an RPO command; a solo session allows it only with the RPO commands switched on, as a soak recording does.
        arrival.Engine.Scenario!.SoloRpoCommandsAllowed = true;
        CommandResult forced = arrival.Engine.SendCommand(arrival.Aircraft.Callsign, "CLANDF");
        Assert.True(forced.Success, $"CLANDF was refused: {forced.Message}");
        ResolvedExitInfo w4 = InstructAndCacheExit(arrival, "W4");

        const double ShortFt = 600.0;
        double marginNm = ForcedLandingProfile.ExitBrakingMarginFt / GeoMath.FeetPerNm;
        double brakingNm = (ShortFt / GeoMath.FeetPerNm) - marginNm;
        double ceiling = ForcedLandingProfile.RolloutMaxDecelKtsPerSec;
        double ias = Math.Sqrt((w4.TurnOffSpeed * w4.TurnOffSpeed) + ((ceiling + 0.5) * 7200.0 * brakingNm));
        PlaceOnCenterlineBefore(arrival, w4.BranchPointNode, ShortFt, ias);

        ExitFlight flight = FlyToHoldingAfterExitWatched(arrival);

        Assert.Equal(["unable W4."], arrival.UnableCalls());
        Assert.Contains("W4", flight.GivenUpAtHandOff);
        AssertExitNotOn(flight, "W4");
    }

    /// <summary>
    /// The given-up set reaches the runway exit: a B738 told to exit OAK 30 at W4 while W4 is beyond its firm rate gives it up at
    /// touchdown, is slowed to coast speed so it hands off with W4's branch nodes still ahead, and finds the exit it did commit to
    /// occupied at the hand-off — so <c>RunwayExitPhase</c> searches for itself, and takes neither the occupied exit nor W4.
    /// </summary>
    [Fact]
    public void GivenUpExit_WithTheCommittedExitOccupiedAtTheHandOff_IsNotTheExitTheRunwayExitTakes()
    {
        SoloArrival? arrival = SpawnSoloOnOak30("B738");
        if (arrival is null)
        {
            return;
        }

        AirportGroundLayout.CenterlineExitResult w4 = RollUntil(
            arrival,
            () => ExitWhere(arrival, exit => string.Equals(exit.Taxiway, "W4", StringComparison.OrdinalIgnoreCase)),
            "W4 ahead on the ground"
        );
        double firmRate = CategoryPerformance.FirmBrakingRate(AircraftCategory.Jet);
        double turnOff = CategoryPerformance.ExitTurnOffSpeed(AircraftCategory.Jet, w4.ExitAngle);
        double toBranchNm = ReachOf(arrival.Aircraft, arrival.Runway, w4).ToBranchNm;
        GroundFrame.EnterGround(arrival.Aircraft, Math.Sqrt((turnOff * turnOff) + ((firmRate + 0.5) * 7200.0 * toBranchNm)));
        arrival.Aircraft.Phases!.RequestedExit = new ExitPreference { Taxiway = "W4" };
        TickUntilCall(arrival, "unable W4.");

        LandingPhase landing = Assert.IsType<LandingPhase>(arrival.Aircraft.Phases.CurrentPhase);
        ResolvedExitInfo committed = Assert.IsType<ResolvedExitInfo>(landing.CandidateExit);
        Assert.False(string.Equals(committed.TaxiwayName, "W4", StringComparison.OrdinalIgnoreCase), "the rollout still plans for W4");
        arrival.Engine.World.AddAircraft(MakeHoldingAfterExitBlocker(arrival.Runway, arrival.Aircraft.Ground.Layout!, committed.HoldShortNode));
        arrival.Aircraft.IndicatedAirspeed = Math.Min(arrival.Aircraft.IndicatedAirspeed, landing.Plan!.CoastSpeed);

        ExitFlight flight = FlyToHoldingAfterExitWatched(arrival);

        Assert.True(
            NamedConnectionAheadOf(arrival, flight.HandOffPosition, "W4") is not null,
            "no W4 connection was ahead at the hand-off, so the case is not staged"
        );
        Assert.NotEqual(committed.HoldShortNode.Id, flight.HoldShortId);
        AssertExitNotOn(flight, "W4");
        Assert.Equal(["unable W4."], arrival.UnableCalls());
    }

    /// <summary>
    /// The straight-line fallback skips a given-up taxiway too: a B738 at 150 kt 500 ft before OAK 30's W6, where no exit on the
    /// centerline graph is makeable at any rate, falls back to the nearest taxiway node ahead; with the taxiway that fallback picks
    /// given up, it picks another.
    /// </summary>
    [Fact]
    public void StraightLineFallback_SkipsAGivenUpTaxiway()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("OAK", "30");
        if ((layout is null) || (runway is null))
        {
            return;
        }

        LatLon threshold = LandingThreshold.Resolve(runway, layout);
        AirportGroundLayout.CenterlineExitResult? w6 = layout.FindOnSidePreferredExit(
            threshold.Lat,
            threshold.Lon,
            runway.TrueHeading,
            runway.Designator,
            new ExitPreference { Taxiway = "W6" },
            sidePref: null
        );
        Assert.NotNull(w6);
        LatLon start = GeoMath.ProjectPoint(w6.Value.Path[0].Position, runway.TrueHeading.ToReciprocal(), 500.0 / GeoMath.FeetPerNm);

        ResolvedExitInfo first = FallbackCandidateAt(layout, runway, start, givenUp: null);
        ResolvedExitInfo second = FallbackCandidateAt(layout, runway, start, givenUp: first.TaxiwayName);

        Assert.False(
            string.Equals(first.TaxiwayName, second.TaxiwayName, StringComparison.OrdinalIgnoreCase),
            $"the straight-line fallback still picked {first.TaxiwayName} after the crew gave it up"
        );
    }

    /// <summary>
    /// The candidate a B738 rollout at 150 kt resolves from <paramref name="start"/> with <paramref name="givenUp"/> given up — asserted
    /// to come from the straight-line fallback (a one-node path), as nothing on the graph is makeable from there at that speed.
    /// </summary>
    private static ResolvedExitInfo FallbackCandidateAt(AirportGroundLayout layout, RunwayInfo runway, LatLon start, string? givenUp)
    {
        AircraftState aircraft = MakeLandedAircraftOfType("B738", start.Lat, start.Lon, runway.TrueHeading.Degrees, ias: 150);
        aircraft.Phases!.AssignedRunway = runway;
        aircraft.Ground.Layout = layout;
        GroundFrame.EnterGround(aircraft, 150);
        PhaseContext ctx = CtxFor(aircraft, runway, layout, AircraftCategory.Jet);

        var phase = new LandingPhase();
        phase.OnStart(ctx);
        if (givenUp is not null)
        {
            aircraft.Phases.GivenUpExitTaxiways.Add(givenUp);
        }

        phase.OnTick(ctx);

        ResolvedExitInfo candidate = Assert.IsType<ResolvedExitInfo>(phase.CandidateExit);
        Assert.True(candidate.Path.Count == 1, $"the candidate {candidate.TaxiwayName} came from the graph, not the straight-line fallback");
        return candidate;
    }

    /// <summary>
    /// The given-up taxiways ride the phase list through a snapshot taken while the runway exit is running, their case-insensitivity
    /// included.
    /// </summary>
    [Fact]
    public void GivenUpTaxiways_SurviveASnapshotRoundTrip_MidRunwayExit()
    {
        SoloArrival? arrival = SpawnSoloOnOak30("C172");
        if (arrival is null)
        {
            return;
        }

        for (int t = 1; (t <= 180) && (arrival.Aircraft.Phases?.CurrentPhase is not RunwayExitPhase); t++)
        {
            arrival.Engine.TickOneSecond();
        }

        Assert.IsType<RunwayExitPhase>(arrival.Aircraft.Phases!.CurrentPhase);
        arrival.Aircraft.Phases.GivenUpExitTaxiways.Add("W3");

        AircraftSnapshotDto snapshot = arrival.Aircraft.ToSnapshot();
        var restored = AircraftState.FromSnapshot(snapshot, arrival.Aircraft.Ground.Layout!);

        Assert.IsType<RunwayExitPhase>(restored.Phases!.CurrentPhase);
        Assert.True(restored.Phases.GivenUpExitTaxiways.Contains("w3"), "the given-up taxiway did not survive the round trip");
    }

    // -- Only a fresh EXIT revives a given-up taxiway while rolling; stopped on the runway, the crew may taxi forward to it --

    /// <summary>
    /// The state both the LAHSO stop and the CLANDF end stop leave behind — the landing ended without its hand-off, the phase list's
    /// requested exit still naming the taxiway the crew gave up: a C172 stopped on OAK 30's centerline 1,000 ft short of W4,
    /// instructed W4, W4 given up, starts its runway exit and commits to W4. The "unable" was about rollout speed; a stopped
    /// aircraft, which may not reverse on the runway without ATC approval (AIM 4-3-21.a), taxis forward to it like any other exit.
    /// The stop does not clear the set.
    /// </summary>
    [Fact]
    public void StoppedOnTheRunwayWithTheInstructedExitGivenUp_TheRunwayExitCommitsToIt()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        RunwayInfo? runway = NavigationDatabase.InstanceOrNull?.GetRunway("OAK", "30");
        if ((layout is null) || (runway is null))
        {
            return;
        }

        LatLon threshold = LandingThreshold.Resolve(runway, layout);
        AirportGroundLayout.CenterlineExitResult? w4 = layout.FindOnSidePreferredExit(
            threshold.Lat,
            threshold.Lon,
            runway.TrueHeading,
            runway.Designator,
            new ExitPreference { Taxiway = "W4" },
            sidePref: null
        );
        Assert.NotNull(w4);
        LatLon stop = GeoMath.ProjectPoint(w4.Value.Path[0].Position, runway.TrueHeading.ToReciprocal(), 1000.0 / GeoMath.FeetPerNm);
        AircraftState aircraft = MakeLandedAircraftOfType("C172", stop.Lat, stop.Lon, runway.TrueHeading.Degrees, ias: 0);
        aircraft.Phases!.AssignedRunway = runway;
        aircraft.Phases.RequestedExit = new ExitPreference { Taxiway = "W4" };
        aircraft.Phases.GivenUpExitTaxiways.Add("W4");
        aircraft.Ground.Layout = layout;
        PhaseContext ctx = CtxFor(aircraft, runway, layout, AircraftCategory.Piston);

        var phase = new RunwayExitPhase();
        phase.OnStart(ctx);

        Assert.True(
            string.Equals(phase.ExitTaxiway, "W4", StringComparison.OrdinalIgnoreCase),
            $"the stopped runway exit committed to {phase.ExitTaxiway ?? "nothing"}, not the given-up W4 ahead of it"
        );
        Assert.Contains("W4", aircraft.Phases.GivenUpExitTaxiways);
    }

    /// <summary>
    /// The CLANDF end stop with the given-up exit still ahead: a C172 told to exit OAK 30 at W7, the end turnoff, reaches a point
    /// 600 ft short of it too fast for the forced rollout's ceiling, says "unable W7", gives it up, and stops on the runway short of
    /// its end with W7's connections still ahead. The runway exit that follows takes W7 ahead at taxi speed rather than turning back
    /// to an exit behind.
    /// </summary>
    [Fact]
    public void ForcedRollout_EndStopWithTheGivenUpExitStillAhead_TheRunwayExitTakesItAhead()
    {
        SoloArrival? arrival = SpawnSoloOnOak30("C172");
        if (arrival is null)
        {
            return;
        }

        arrival.Engine.Scenario!.SoloRpoCommandsAllowed = true;
        CommandResult forced = arrival.Engine.SendCommand(arrival.Aircraft.Callsign, "CLANDF");
        Assert.True(forced.Success, $"CLANDF was refused: {forced.Message}");
        ResolvedExitInfo w7 = InstructAndCacheExit(arrival, "W7");

        const double ShortFt = 600.0;
        double marginNm = ForcedLandingProfile.ExitBrakingMarginFt / GeoMath.FeetPerNm;
        double brakingNm = (ShortFt / GeoMath.FeetPerNm) - marginNm;
        double ceiling = ForcedLandingProfile.RolloutMaxDecelKtsPerSec;
        double ias = Math.Sqrt((w7.TurnOffSpeed * w7.TurnOffSpeed) + ((ceiling + 0.5) * 7200.0 * brakingNm));
        PlaceOnCenterlineBefore(arrival, w7.BranchPointNode, ShortFt, ias);

        for (int t = 1; (t <= 120) && (arrival.Aircraft.Phases?.CurrentPhase is LandingPhase); t++)
        {
            arrival.Engine.TickOneSecond();
        }

        Assert.IsType<RunwayExitPhase>(arrival.Aircraft.Phases!.CurrentPhase);
        Assert.Contains("unable W7.", arrival.UnableCalls());
        Assert.Contains("W7", arrival.Aircraft.Phases.GivenUpExitTaxiways);
        Assert.True(arrival.Aircraft.GroundSpeed < 1.0, $"the landing ended at {arrival.Aircraft.GroundSpeed:F1} kt, not stopped on the runway");
        Assert.True(
            NamedConnectionAheadOf(arrival, arrival.Aircraft.Position, "W7") is not null,
            "no W7 connection was ahead of the stop, so the case is not staged"
        );
        LatLon stop = arrival.Aircraft.Position;

        ExitFlight flight = FlyToHoldingAfterExitWatched(arrival);

        Assert.True(string.Equals("W7", flight.TakenTaxiway, StringComparison.OrdinalIgnoreCase), $"exited at {flight.TakenTaxiway}, not W7");
        GroundNode bar = arrival.Aircraft.Ground.Layout!.Nodes[flight.HoldShortId];
        double barAheadNm = GeoMath.AlongTrackDistanceNm(bar.Position, stop, arrival.Runway.TrueHeading);
        Assert.True(barAheadNm > 0, $"the W7 bar taken lies {-barAheadNm * GeoMath.FeetPerNm:F0} ft behind the stop: the aircraft turned back");
    }

    /// <summary>
    /// A stopped aircraft whose only exit ahead is one it gave up does not report that it cannot exit: a C172 stopped on OAK 30's
    /// centerline between W6's branch and W7's, the end turnoff, instructed W7 and W7 given up, commits to W7 on starting its runway
    /// exit, with no "no exit ahead" warning.
    /// </summary>
    [Fact]
    public void StoppedWithOnlyAGivenUpExitAhead_TaxiesToIt_WithoutTheUnableToExitReport()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        RunwayInfo? runway = NavigationDatabase.InstanceOrNull?.GetRunway("OAK", "30");
        if ((layout is null) || (runway is null))
        {
            return;
        }

        LatLon threshold = LandingThreshold.Resolve(runway, layout);
        AirportGroundLayout.CenterlineExitResult? w6 = ExitOf(layout, runway, threshold, "W6");
        AirportGroundLayout.CenterlineExitResult? w7 = ExitOf(layout, runway, threshold, "W7");
        Assert.NotNull(w6);
        Assert.NotNull(w7);
        double w6ToW7Nm = GeoMath.AlongTrackDistanceNm(w7.Value.Path[0].Position, w6.Value.Path[0].Position, runway.TrueHeading);
        Assert.True(w6ToW7Nm > 0, "W7's branch is not beyond W6's on runway 30, so the case is not staged");
        LatLon stop = GeoMath.ProjectPoint(w6.Value.Path[0].Position, runway.TrueHeading, w6ToW7Nm / 2);
        AirportGroundLayout.CenterlineExitResult? otherAhead = layout.FindOnSidePreferredExit(
            stop.Lat,
            stop.Lon,
            runway.TrueHeading,
            runway.Designator,
            preference: null,
            sidePref: null,
            filter: exit =>
                string.Equals(exit.Taxiway, "W7", StringComparison.OrdinalIgnoreCase)
                    ? AirportGroundLayout.CandidateVerdict.Skip
                    : AirportGroundLayout.CandidateVerdict.Accept
        );
        Assert.True(
            otherAhead is null,
            $"{otherAhead?.Taxiway} also lies ahead of the stop, so W7 is not the only exit ahead and the case is not staged"
        );

        AircraftState aircraft = MakeLandedAircraftOfType("C172", stop.Lat, stop.Lon, runway.TrueHeading.Degrees, ias: 0);
        aircraft.Phases!.AssignedRunway = runway;
        aircraft.Phases.RequestedExit = new ExitPreference { Taxiway = "W7" };
        aircraft.Phases.GivenUpExitTaxiways.Add("W7");
        aircraft.Ground.Layout = layout;
        PhaseContext ctx = CtxFor(aircraft, runway, layout, AircraftCategory.Piston);

        var phase = new RunwayExitPhase();
        phase.OnStart(ctx);
        phase.OnTick(ctx);

        Assert.True(
            string.Equals(phase.ExitTaxiway, "W7", StringComparison.OrdinalIgnoreCase),
            $"the stopped runway exit committed to {phase.ExitTaxiway ?? "nothing"}, not the given-up W7 ahead of it"
        );
        Assert.DoesNotContain(aircraft.PendingWarnings, warning => warning.Contains("no exit ahead", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The nearest connection of <paramref name="taxiway"/> ahead of <paramref name="from"/> on <paramref name="runway"/>, if any.</summary>
    private static AirportGroundLayout.CenterlineExitResult? ExitOf(AirportGroundLayout layout, RunwayInfo runway, LatLon from, string taxiway) =>
        layout.FindOnSidePreferredExit(
            from.Lat,
            from.Lon,
            runway.TrueHeading,
            runway.Designator,
            new ExitPreference { Taxiway = taxiway },
            sidePref: null
        );

    /// <summary>
    /// A fresh <c>EXIT</c> naming a given-up taxiway revives it, and a side-only instruction after it does not hide it again: a B738
    /// that gave up OAK 30's W4 at touchdown hands off with W4 still ahead, is told <c>EXIT W4</c> during its runway exit (accepted,
    /// W4 out of the given-up set), then <c>ER</c> — W4's side — and exits at W4.
    /// </summary>
    [Fact]
    public void FreshExitInstructionMidRunwayExit_RevivesTheGivenUpTaxiway_AndASideOnlyInstructionKeepsIt()
    {
        SoloArrival? arrival = SpawnSoloOnOak30("B738");
        if (arrival is null)
        {
            return;
        }

        AirportGroundLayout.CenterlineExitResult w4 = RollUntil(
            arrival,
            () => ExitWhere(arrival, exit => string.Equals(exit.Taxiway, "W4", StringComparison.OrdinalIgnoreCase)),
            "W4 ahead on the ground"
        );
        double firmRate = CategoryPerformance.FirmBrakingRate(AircraftCategory.Jet);
        double turnOff = CategoryPerformance.ExitTurnOffSpeed(AircraftCategory.Jet, w4.ExitAngle);
        double toBranchNm = ReachOf(arrival.Aircraft, arrival.Runway, w4).ToBranchNm;
        GroundFrame.EnterGround(arrival.Aircraft, Math.Sqrt((turnOff * turnOff) + ((firmRate + 0.5) * 7200.0 * toBranchNm)));
        arrival.Aircraft.Phases!.RequestedExit = new ExitPreference { Taxiway = "W4" };
        TickUntilCall(arrival, "unable W4.");
        Assert.Contains("W4", arrival.Aircraft.Phases.GivenUpExitTaxiways);

        LandingPhase landing = Assert.IsType<LandingPhase>(arrival.Aircraft.Phases.CurrentPhase);
        arrival.Aircraft.IndicatedAirspeed = Math.Min(arrival.Aircraft.IndicatedAirspeed, landing.Plan!.CoastSpeed);
        for (int t = 1; (t <= 30) && (arrival.Aircraft.Phases.CurrentPhase is not RunwayExitPhase); t++)
        {
            arrival.Engine.TickOneSecond();
        }

        Assert.IsType<RunwayExitPhase>(arrival.Aircraft.Phases.CurrentPhase);
        Assert.True(
            NamedConnectionAheadOf(arrival, arrival.Aircraft.Position, "W4") is not null,
            "no W4 connection was ahead at the hand-off, so the case is not staged"
        );

        CommandResult revive = arrival.Engine.SendCommand(arrival.Aircraft.Callsign, "EXIT W4");
        Assert.True(revive.Success, $"EXIT W4 during the runway exit was refused: {revive.Message}");
        Assert.DoesNotContain("W4", arrival.Aircraft.Phases.GivenUpExitTaxiways);
        arrival.Engine.TickOneSecond();

        CommandResult side = arrival.Engine.SendCommand(arrival.Aircraft.Callsign, "ER");
        Assert.True(side.Success, $"ER during the runway exit was refused: {side.Message}");
        Assert.DoesNotContain("W4", arrival.Aircraft.Phases.GivenUpExitTaxiways);

        ExitFlight flight = FlyToHoldingAfterExitWatched(arrival);

        Assert.True(string.Equals("W4", flight.TakenTaxiway, StringComparison.OrdinalIgnoreCase), $"exited at {flight.TakenTaxiway}, not W4");
    }

    /// <summary>
    /// The given-up taxiways snapshot in one order whatever order the crew gave them up in, so two runs that reach the same set by
    /// different histories write the same JSON.
    /// </summary>
    [Fact]
    public void GivenUpTaxiways_SnapshotInOneOrder_WhateverOrderTheyWereGivenUpIn()
    {
        var first = new PhaseList();
        first.GivenUpExitTaxiways.Add("W3");
        first.GivenUpExitTaxiways.Add("W4");

        var second = new PhaseList();
        second.GivenUpExitTaxiways.Add("W4");
        second.GivenUpExitTaxiways.Add("W5");
        second.GivenUpExitTaxiways.Add("W3");
        second.GivenUpExitTaxiways.Remove("W5");
        Assert.True(first.GivenUpExitTaxiways.SetEquals(second.GivenUpExitTaxiways));

        string firstJson = JsonSerializer.Serialize(first.ToSnapshot(), RecordingJsonOptions.Default);
        string secondJson = JsonSerializer.Serialize(second.ToSnapshot(), RecordingJsonOptions.Default);

        Assert.Equal(firstJson, secondJson);
    }

    /// <summary>The given-up taxiways end with the runway exit: once it completes, the set is empty.</summary>
    [Fact]
    public void GivenUpTaxiways_AreClearedWhenTheRunwayExitCompletes()
    {
        SoloArrival? arrival = SpawnSoloOnOak30("C172");
        if (arrival is null)
        {
            return;
        }

        for (int t = 1; (t <= 180) && (arrival.Aircraft.Phases?.CurrentPhase is not RunwayExitPhase); t++)
        {
            arrival.Engine.TickOneSecond();
        }

        Assert.IsType<RunwayExitPhase>(arrival.Aircraft.Phases!.CurrentPhase);
        arrival.Aircraft.Phases.GivenUpExitTaxiways.Add("W3");

        FlyToHoldingAfterExitWatched(arrival);

        Assert.Empty(arrival.Aircraft.Phases.GivenUpExitTaxiways);
    }

    /// <summary>
    /// <c>EXIT</c> repeated for the exit the aircraft is already instructed to take, while <c>EXIT … EXP</c> stands, is judged at the
    /// max-effort limit the crew was already given and keeps it: accepted, no "unable", the exit taken, still braking without delay.
    /// </summary>
    [Fact]
    public void RepeatedInstructionUnderAStandingExpedite_IsJudgedAtTheExpediteLimit_AndKeepsIt()
    {
        SoloArrival? arrival = SpawnSoloOnOak30("B738");
        if (arrival is null)
        {
            return;
        }

        double firmRate = CategoryPerformance.FirmBrakingRate(AircraftCategory.Jet);
        double expediteRate = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Jet);
        AirportGroundLayout.CenterlineExitResult exit = RollUntil(
            arrival,
            () =>
                ExitWhere(
                    arrival,
                    candidate =>
                    {
                        ExitReachFacts reach = ReachOf(arrival.Aircraft, arrival.Runway, candidate);
                        return reach.NeedsMoreThan(firmRate + 1.0) && reach.MakeableWithin(expediteRate - 0.2);
                    }
                ),
            "an exit well between its firm and expedite rates"
        );
        string named = exit.Taxiway;

        CommandResult first = arrival.Engine.SendCommand(arrival.Aircraft.Callsign, $"EXIT {named} EXP");
        Assert.True(first.Success, $"EXIT {named} EXP was refused: {first.Message}");
        arrival.Engine.TickOneSecond();
        ExitReachFacts afterATick = ReachOf(arrival.Aircraft, arrival.Runway, exit);
        Assert.True(
            afterATick.NeedsMoreThan(firmRate) && afterATick.MakeableWithin(expediteRate),
            $"{named} needs {afterATick.RequiredDecel:F2} kt/s a tick later, outside the firm-to-expedite band, so the case is not staged"
        );

        CommandResult repeat = arrival.Engine.SendCommand(arrival.Aircraft.Callsign, $"EXIT {named}");
        Assert.True(repeat.Success, $"EXIT {named} under a standing expedite was refused: {repeat.Message}");
        Assert.True(arrival.Aircraft.Ground.IsExpeditingExit, "the repeated EXIT cancelled the standing expedite");

        ExitFlight flight = FlyToHoldingAfterExitWatched(arrival);

        Assert.Empty(arrival.UnableCalls());
        Assert.True(string.Equals(named, flight.TakenTaxiway, StringComparison.OrdinalIgnoreCase), $"exited at {flight.TakenTaxiway}, not {named}");
    }

    // -- A late EXIT in the runway exit is judged with the expedite it will run under, and refused in the right words --

    /// <summary>
    /// A B738 in its runway exit on OAK 30, W6's route handed to its navigator and W5 — the exit before W6 — still ahead: the arrival,
    /// its exit phase and W5's connection.
    /// </summary>
    private sealed record LateExitStage(SoloArrival Arrival, RunwayExitPhase Phase, AirportGroundLayout.CenterlineExitResult Named);

    /// <summary>
    /// Stages a B738 committed to OAK 30's W6 in its runway exit, <paramref name="shortOfW5Ft"/> before W5's branch at a wheel speed
    /// from which W5 needs <paramref name="requiredDecel"/> kt/s, with the exit route rebuilt and the standing <c>EXIT W6</c> captured as
    /// the instruction it was committed under, so the next <c>EXIT</c> is a late change. Null when the OAK layout or runway is missing.
    /// </summary>
    private static LateExitStage? StageLateExitChange(double shortOfW5Ft, double requiredDecel)
    {
        var groundData = new TestAirportGroundData();
        AirportGroundLayout? layout = groundData.GetLayout("OAK");
        RunwayInfo? runway = NavigationDatabase.InstanceOrNull?.GetRunway("OAK", "30");
        if ((layout is null) || (runway is null))
        {
            return null;
        }

        LatLon threshold = LandingThreshold.Resolve(runway, layout);
        AirportGroundLayout.CenterlineExitResult? w5 = ExitOf(layout, runway, threshold, "W5");
        AirportGroundLayout.CenterlineExitResult? w6 = ExitOf(layout, runway, threshold, "W6");
        Assert.NotNull(w5);
        Assert.NotNull(w6);
        double w5ToW6Nm = GeoMath.AlongTrackDistanceNm(w6.Value.Path[0].Position, w5.Value.Path[0].Position, runway.TrueHeading);
        Assert.True(w5ToW6Nm > 0, "W6's branch is not beyond W5's on runway 30, so the case is not staged");

        LatLon start = GeoMath.ProjectPoint(w5.Value.Path[0].Position, runway.TrueHeading.ToReciprocal(), shortOfW5Ft / GeoMath.FeetPerNm);
        double coast = CategoryPerformance.RolloutCoastSpeed(AircraftCategory.Jet);
        AircraftState aircraft = MakeLandedAircraftOfType("B738", start.Lat, start.Lon, runway.TrueHeading.Degrees, ias: coast);
        aircraft.Phases!.AssignedRunway = runway;
        aircraft.Phases.RequestedExit = new ExitPreference { Taxiway = "W6" };
        aircraft.Ground.LayoutAirportId = "OAK";
        var dto = new RunwayExitPhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 4.0,
            ReachedExitNode = true,
            ExitNodeId = w6.Value.HoldShort.Id,
            ExitTaxiway = w6.Value.Taxiway,
            RunwayId = runway.Designator,
            ExitSpeed = coast,
            TimeSinceLastLog = 0.0,
            RunwayHeadingDeg = runway.TrueHeading.Degrees,
            ExitStateValue = (int)RunwayExitPhase.ExitState.FollowingExitPath,
            TurnStarted = false,
            ExitWaypointNodeIds = [.. w6.Value.Path.Select(node => node.Id)],
        };
        aircraft.Phases.Add(RunwayExitPhase.FromSnapshot(dto, layout));
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        SoloArrival arrival = StartSoloEngineAtOak(groundData, aircraft, runway);

        // The first tick rebuilds W6's route and captures EXIT W6 as the instruction the exit was committed under.
        arrival.Engine.TickOneSecond();
        RunwayExitPhase phase = Assert.IsType<RunwayExitPhase>(arrival.Aircraft.Phases!.CurrentPhase);
        Assert.False(phase.IsOnCenterline, "the runway exit dropped W6's route on its first tick");
        Assert.Equal("W6", phase.ExitTaxiway);

        double turnOff = CategoryPerformance.ExitTurnOffSpeed(AircraftCategory.Jet, w5.Value.ExitAngle);
        double toBranchNm = ReachOf(arrival.Aircraft, runway, w5.Value).ToBranchNm;
        Assert.True(toBranchNm > 0, "W5's branch is already behind the aircraft after the first tick, so the case is not staged");
        arrival.Aircraft.IndicatedAirspeed = Math.Sqrt((turnOff * turnOff) + (requiredDecel * 7200.0 * toBranchNm));
        return new LateExitStage(arrival, phase, w5.Value);
    }

    /// <summary>Asserts W5 needs more than the jet's firm rate and no more than its expedite rate from where the staged B738 is.</summary>
    private static void AssertW5IsBetweenFirmAndExpedite(LateExitStage stage)
    {
        ExitReachFacts reach = ReachOf(stage.Arrival.Aircraft, stage.Arrival.Runway, stage.Named);
        double firmRate = CategoryPerformance.FirmBrakingRate(AircraftCategory.Jet);
        double expediteRate = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Jet);
        Assert.True(
            reach.NeedsMoreThan(firmRate) && reach.MakeableWithin(expediteRate - 0.2),
            $"W5 needs {reach.RequiredDecel:F2} kt/s, outside the firm-to-expedite band ({firmRate:F1}–{expediteRate:F1}), so the case is not staged"
        );
    }

    /// <summary>
    /// A late <c>EXIT W5 EXP</c> in the runway exit, with nothing standing, is judged at the max-effort limit it will run under: W5,
    /// makeable only past the firm rate, is accepted and taken.
    /// </summary>
    [Fact]
    public void LateExpeditedExitChange_MakeableOnlyAtTheExpediteLimit_IsAcceptedAndTaken()
    {
        LateExitStage? stage = StageLateExitChange(
            shortOfW5Ft: 2000.0,
            requiredDecel: CategoryPerformance.FirmBrakingRate(AircraftCategory.Jet) + 0.8
        );
        if (stage is null)
        {
            return;
        }

        AssertW5IsBetweenFirmAndExpedite(stage);
        Assert.False(stage.Arrival.Aircraft.Ground.IsExpeditingExit);

        CommandResult result = stage.Arrival.Engine.SendCommand(stage.Arrival.Aircraft.Callsign, "EXIT W5 EXP");
        Assert.True(result.Success, $"EXIT W5 EXP was refused: {result.Message}");
        Assert.True(stage.Arrival.Aircraft.Ground.IsExpeditingExit, "EXIT W5 EXP left the expedite off");
        stage.Arrival.Engine.TickOneSecond();
        Assert.Equal("W5", stage.Phase.ExitTaxiway);

        ExitFlight flight = FlyToHoldingAfterExitWatched(stage.Arrival);

        Assert.True(string.Equals("W5", flight.TakenTaxiway, StringComparison.OrdinalIgnoreCase), $"exited at {flight.TakenTaxiway}, not W5");
    }

    /// <summary>
    /// A late <c>EXIT W5</c> — a new taxiway, no EXP — under a standing <c>EXP</c> is judged at the firm rate it will run under: W5,
    /// makeable only past that rate, is refused up front with the crew's "unable W5", the committed exit kept and the expedite still
    /// standing — never accepted, read back and dropped a tick later.
    /// </summary>
    [Fact]
    public void LateExitChangeWithoutExpedite_UnderAStandingExpedite_IsJudgedAtTheFirmRate_AndRefusedUpFront()
    {
        LateExitStage? stage = StageLateExitChange(
            shortOfW5Ft: 2000.0,
            requiredDecel: CategoryPerformance.FirmBrakingRate(AircraftCategory.Jet) + 0.8
        );
        if (stage is null)
        {
            return;
        }

        AssertW5IsBetweenFirmAndExpedite(stage);
        CommandResult expedite = stage.Arrival.Engine.SendCommand(stage.Arrival.Aircraft.Callsign, "EXP");
        Assert.True(expedite.Success, $"EXP was refused: {expedite.Message}");
        Assert.True(stage.Arrival.Aircraft.Ground.IsExpeditingExit, "EXP did not set the standing expedite");

        CommandResult result = stage.Arrival.Engine.SendCommand(stage.Arrival.Aircraft.Callsign, "EXIT W5");

        Assert.False(result.Success, $"EXIT W5 under a standing expedite was accepted: {result.Message}");
        Assert.Equal("unable W5.", result.Message);
        Assert.Equal("unable W5.", result.PilotUnable?.Terminal);
        Assert.Equal("W6", stage.Phase.ExitTaxiway);
        Assert.Equal("W6", stage.Arrival.Aircraft.Phases!.RequestedExit?.Taxiway);
        Assert.True(stage.Arrival.Aircraft.Ground.IsExpeditingExit, "the refused EXIT cancelled the standing expedite");
        stage.Arrival.Engine.TickOneSecond();
        Assert.Equal("W6", stage.Phase.ExitTaxiway);
    }

    /// <summary>
    /// The runway exit's refusal of a late <c>EXIT</c> says which it is: "unable W5" for a connection that is ahead but past the limit
    /// the crew will brake at, "unable, no C1 ahead" for a taxiway with no connection ahead on this runway (C1 is an OAK 28R exit).
    /// </summary>
    [Fact]
    public void LateExitChangeRefusals_SayUnableForAnExitAhead_AndNoExitAheadForOneThatIsNot()
    {
        LateExitStage? stage = StageLateExitChange(
            shortOfW5Ft: 2000.0,
            requiredDecel: CategoryPerformance.FirmBrakingRate(AircraftCategory.Jet) + 0.8
        );
        if (stage is null)
        {
            return;
        }

        AssertW5IsBetweenFirmAndExpedite(stage);

        CommandResult pastTheLimit = stage.Arrival.Engine.SendCommand(stage.Arrival.Aircraft.Callsign, "EXIT W5");
        CommandResult notAhead = stage.Arrival.Engine.SendCommand(stage.Arrival.Aircraft.Callsign, "EXIT C1");

        Assert.False(pastTheLimit.Success, $"EXIT W5 past the firm rate was accepted: {pastTheLimit.Message}");
        Assert.Equal("unable W5.", pastTheLimit.Message);
        Assert.Equal("unable W5.", pastTheLimit.PilotUnable?.Terminal);
        Assert.False(notAhead.Success, $"EXIT C1 was accepted on runway 30: {notAhead.Message}");
        Assert.Equal("Unable, no C1 ahead", notAhead.Message);
        Assert.Equal("unable, no C1 ahead.", notAhead.PilotUnable?.Terminal);
        Assert.Equal("W6", stage.Phase.ExitTaxiway);
    }

    /// <summary>
    /// Under LAHSO a named exit that is ahead but beyond the hold-short point is refused with the crew's plain "unable {twy}": "no
    /// {twy} ahead" is for a taxiway with no connection ahead on the runway at all.
    /// </summary>
    [Fact]
    public void LahsoNamedExitBeyondTheHoldShortPoint_IsRefusedAsUnable_NotAsNoExitAhead()
    {
        SoloArrival? arrival = SpawnSolo("SFO", "28R", "C172");
        if (arrival is null)
        {
            return;
        }

        CommandResult lahso = arrival.Engine.SendCommand(arrival.Aircraft.Callsign, "LAHSO 1L");
        Assert.True(lahso.Success, $"LAHSO 1L was refused: {lahso.Message}");
        RollUntil(arrival, () => arrival.Aircraft.IsOnGround ? true : (bool?)null, "touchdown");

        LahsoTarget? target = arrival.Aircraft.Phases!.LahsoHoldShort;
        Assert.NotNull(target);
        LatLon threshold = LandingThreshold.Resolve(arrival.Runway, arrival.Aircraft.Ground.Layout!);
        double beyondNm = target.DistFromThresholdNm + (100.0 / GeoMath.FeetPerNm);
        AirportGroundLayout.CenterlineExitResult? beyond = ExitWhere(
            arrival,
            exit => GeoMath.AlongTrackDistanceNm(exit.Path[0].Position, threshold, arrival.Runway.TrueHeading) > beyondNm
        );
        Assert.True(beyond is not null, "no exit of 28R lies beyond the 1L hold-short point");

        CommandResult exitResult = arrival.Engine.SendCommand(arrival.Aircraft.Callsign, $"EXIT {beyond.Value.Taxiway}");

        Assert.False(exitResult.Success, $"EXIT {beyond.Value.Taxiway} beyond the hold-short point was accepted: {exitResult.Message}");
        Assert.Equal($"unable {beyond.Value.Taxiway}.", exitResult.Message);
    }

    // -- One reachability test for a named exit: command time and rollout --

    /// <summary>
    /// Where an exit stands against the aircraft's speed: the along-track distance to its branch, whether the indicated airspeed
    /// is already within <see cref="RolloutBraking.TurnOffSpeedToleranceKts"/> of its turn-off speed, and the braking the ground
    /// speed needs to make that turn-off speed by the branch (infinite at or past the branch).
    /// </summary>
    private readonly record struct ExitReachFacts(double ToBranchNm, bool SlowEnoughByIas, double RequiredDecel)
    {
        public bool NeedsMoreThan(double limit) => (ToBranchNm > 0) && !SlowEnoughByIas && (RequiredDecel > limit);

        public bool MakeableWithin(double limit) => (ToBranchNm > 0) && (SlowEnoughByIas || (RequiredDecel <= limit));
    }

    private static ExitReachFacts ReachOf(AircraftState aircraft, RunwayInfo runway, AirportGroundLayout.CenterlineExitResult exit)
    {
        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
        double turnOff = CategoryPerformance.ExitTurnOffSpeed(category, exit.ExitAngle);
        double toBranchNm = GeoMath.AlongTrackDistanceNm(exit.Path[0].Position, aircraft.Position, runway.TrueHeading);
        double required =
            toBranchNm > 0 ? RolloutBraking.RequiredDecelKtsPerSec(aircraft.GroundSpeed, turnOff, toBranchNm, category) : double.PositiveInfinity;
        return new ExitReachFacts(toBranchNm, aircraft.IndicatedAirspeed <= turnOff + RolloutBraking.TurnOffSpeedToleranceKts, required);
    }

    /// <summary>The nearest exit connection, on either side and of any taxiway, for which <paramref name="pick"/> holds.</summary>
    private static AirportGroundLayout.CenterlineExitResult? ExitWhere(
        SoloArrival arrival,
        Func<AirportGroundLayout.CenterlineExitResult, bool> pick
    ) =>
        arrival.Aircraft.IsOnGround
            ? arrival.Aircraft.Ground.Layout!.FindOnSidePreferredExit(
                arrival.Aircraft.Position.Lat,
                arrival.Aircraft.Position.Lon,
                arrival.Runway.TrueHeading,
                arrival.Runway.Designator,
                preference: null,
                sidePref: null,
                filter: exit => pick(exit) ? AirportGroundLayout.CandidateVerdict.Accept : AirportGroundLayout.CandidateVerdict.Skip
            )
            : null;

    /// <summary>Rolls the arrival out uninstructed until <paramref name="find"/> returns something, while it is still in its landing roll.</summary>
    private static T RollUntil<T>(SoloArrival arrival, Func<T?> find, string what)
        where T : struct
    {
        T? found = null;
        for (int t = 1; (t <= 180) && (found is null) && (arrival.Aircraft.Phases?.CurrentPhase is not RunwayExitPhase); t++)
        {
            arrival.Engine.TickOneSecond();
            found = arrival.Aircraft.IsOnGround ? find() : null;
        }

        Assert.True(found is not null, $"the {arrival.Aircraft.AircraftType}'s rollout never reached {what}");
        Assert.IsType<LandingPhase>(arrival.Aircraft.Phases?.CurrentPhase);
        return found.Value;
    }

    /// <summary>
    /// <c>EXIT … EXP</c> for an exit the C172 could make only by braking past its max-effort (expedite) rate is refused up front,
    /// with no "without delay" readback and no preference kept for it.
    /// </summary>
    [Fact]
    public void ExpeditedInstructedExitPastTheExpediteRate_IsRefusedUpFront()
    {
        SoloArrival? arrival = SpawnSoloOnOak30("C172");
        if (arrival is null)
        {
            return;
        }

        double expediteRate = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Piston);
        AirportGroundLayout.CenterlineExitResult tooClose = RollUntil(
            arrival,
            () => ExitWhere(arrival, exit => ReachOf(arrival.Aircraft, arrival.Runway, exit).NeedsMoreThan(expediteRate)),
            "an exit beyond its expedite rate"
        );
        arrival.Calls.Clear();

        CommandResult exitResult = arrival.Engine.SendCommand(arrival.Aircraft.Callsign, $"EXIT {tooClose.Taxiway} EXP");
        arrival.Engine.TickOneSecond();

        Assert.False(exitResult.Success, $"EXIT {tooClose.Taxiway} EXP was accepted: {exitResult.Message}");
        Assert.Null(arrival.Aircraft.Phases!.RequestedExit);
        Assert.DoesNotContain(arrival.Calls, c => c.Message.Contains("without delay", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><c>EXIT … EXP</c> for an exit a B738 can make braking past its firm rate but within its expedite rate is accepted.</summary>
    [Fact]
    public void ExpeditedInstructedExitBeyondFirmWithinExpedite_IsAccepted()
    {
        SoloArrival? arrival = SpawnSoloOnOak30("B738");
        if (arrival is null)
        {
            return;
        }

        double firmRate = CategoryPerformance.FirmBrakingRate(AircraftCategory.Jet);
        double expediteRate = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Jet);
        AirportGroundLayout.CenterlineExitResult exit = RollUntil(
            arrival,
            () =>
                ExitWhere(
                    arrival,
                    candidate =>
                    {
                        ExitReachFacts reach = ReachOf(arrival.Aircraft, arrival.Runway, candidate);
                        return reach.NeedsMoreThan(firmRate) && reach.MakeableWithin(expediteRate - 0.2);
                    }
                ),
            "an exit between its firm and expedite rates"
        );

        CommandResult exitResult = arrival.Engine.SendCommand(arrival.Aircraft.Callsign, $"EXIT {exit.Taxiway} EXP");

        Assert.True(exitResult.Success, $"EXIT {exit.Taxiway} EXP was refused: {exitResult.Message}");
        Assert.Equal(exit.Taxiway, arrival.Aircraft.Phases!.RequestedExit?.Taxiway);
    }

    /// <summary>
    /// <c>EXIT</c> naming a taxiway whose nearest connection is at or just behind the aircraft (inside the centerline search's
    /// look-behind) and which has no other connection ahead is refused as no such exit ahead, not read back.
    /// </summary>
    [Fact]
    public void NamedExitWhoseOnlyConnectionIsJustBehind_IsRefusedAsNoExitAhead()
    {
        SoloArrival? arrival = SpawnSoloOnOak30("B738");
        if (arrival is null)
        {
            return;
        }

        AirportGroundLayout layout = arrival.Aircraft.Ground.Layout!;
        bool OnlyConnection(AirportGroundLayout.CenterlineExitResult exit) =>
            layout.FindOnSidePreferredExit(
                arrival.Aircraft.Position.Lat,
                arrival.Aircraft.Position.Lon,
                arrival.Runway.TrueHeading,
                arrival.Runway.Designator,
                new ExitPreference { Taxiway = exit.Taxiway },
                sidePref: null,
                excludeHoldShortNodes: [exit.HoldShort.Id]
            )
                is null;

        AirportGroundLayout.CenterlineExitResult behind = RollUntil(
            arrival,
            () => ExitWhere(arrival, exit => (ReachOf(arrival.Aircraft, arrival.Runway, exit).ToBranchNm <= 0) && OnlyConnection(exit)),
            "an exit connection just behind it"
        );

        CommandResult exitResult = arrival.Engine.SendCommand(arrival.Aircraft.Callsign, $"EXIT {behind.Taxiway}");

        Assert.False(exitResult.Success, $"EXIT {behind.Taxiway} was accepted with its branch behind the aircraft: {exitResult.Message}");
        Assert.Contains($"no {behind.Taxiway} ahead", exitResult.Message);
    }

    // -- A taxiway with two bars on the same side: a passed connection does not hide the one ahead --

    /// <summary>
    /// SMF 17L: taxiway D meets the runway at the threshold (bar 76, from centerline nodes 63, 428 and 429) and again 8,440 ft
    /// down (bar 77, from 68, 438 and 439), with D5, D7 and D9 (bar 80) between them. A B738 ten feet past node 63 still has the
    /// first connection inside the centerline walk's look-behind, where it is judged at or behind: the state the
    /// passed-connection re-search exists for. <paramref name="occupied"/> is the tick's occupied hold-short set.
    /// </summary>
    private static (LandingPhase Phase, PhaseContext Ctx)? StageB738JustPastSmf17LThresholdD(ExitPreference? requested, HashSet<int>? occupied)
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SMF");
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("SMF", "17L");
        if ((layout is null) || (runway is null))
        {
            return null;
        }

        Assert.Equal(GroundNodeType.RunwayHoldShort, layout.Nodes[76].Type);
        Assert.Equal(GroundNodeType.RunwayHoldShort, layout.Nodes[77].Type);
        LatLon position = GeoMath.ProjectPoint(layout.Nodes[63].Position, runway.TrueHeading, 10.0 / GeoMath.FeetPerNm);
        AircraftState aircraft = MakeLandedAircraftOfType("B738", position.Lat, position.Lon, runway.TrueHeading.Degrees, ias: 130);
        aircraft.Phases!.AssignedRunway = runway;
        aircraft.Phases.RequestedExit = requested;
        aircraft.Ground.Layout = layout;
        PhaseContext ctx = new()
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            GroundLayout = layout,
            Logger = NullLogger.Instance,
            OccupiedHoldShortNodes = occupied,
        };

        var phase = new LandingPhase();
        phase.OnStart(ctx);
        return (phase, ctx);
    }

    /// <summary>
    /// Uninstructed, with D9's bar occupied: the only forward exit left is D's second bar, which the search reaches only by
    /// setting the passed first bar aside and searching again. Without that it would take the 110° back-exit D7 at the firm rate.
    /// </summary>
    [Fact]
    public void UninstructedRollout_JustPastATaxiwaysFirstBar_StillFindsItsSecondBarAhead()
    {
        if (StageB738JustPastSmf17LThresholdD(requested: null, occupied: [80]) is not { } staged)
        {
            return;
        }

        staged.Phase.OnTick(staged.Ctx);

        ResolvedExitInfo candidate = Assert.IsType<ResolvedExitInfo>(staged.Phase.CandidateExit);
        Assert.Equal("D", candidate.TaxiwayName);
        Assert.Equal(77, candidate.HoldShortNode.Id);
    }

    /// <summary>
    /// <c>EXIT D</c> ten feet past D's first bar is judged on its second bar: the rollout resolves that connection, with a real
    /// path to it, and the up-front check agrees that D is ahead.
    /// </summary>
    [Fact]
    public void InstructedExit_JustPastItsFirstBar_IsResolvedAtItsSecondBarAhead()
    {
        var requested = new ExitPreference { Taxiway = "D" };
        if (StageB738JustPastSmf17LThresholdD(requested, occupied: null) is not { } staged)
        {
            return;
        }

        staged.Phase.OnTick(staged.Ctx);

        ResolvedExitInfo candidate = Assert.IsType<ResolvedExitInfo>(staged.Phase.CandidateExit);
        Assert.Equal("D", candidate.TaxiwayName);
        Assert.Equal(77, candidate.HoldShortNode.Id);
        Assert.True(candidate.Path.Count >= 2, "the candidate came from the straight-line fallback, not the graph");
        Assert.True(staged.Phase.EvaluateAndApplyNamedExitInstruction(staged.Ctx.Aircraft, requested, expedite: false).Allowed);
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
