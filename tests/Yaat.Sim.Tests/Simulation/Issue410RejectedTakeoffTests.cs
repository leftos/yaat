using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;
using static Yaat.Sim.Tests.Simulation.RejectedTakeoffTestRig;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #410: TakeoffPhase ignores Ground.SpeedLimit — a rolling
/// departure cannot be braked for a runway occupant. A departure on its takeoff roll must
/// reject the takeoff (below the doctrinal thresholds) when a blocking occupant sits on the
/// runway ahead, brake at the rejected-takeoff rate to a stop on the centerline, and hold in
/// position awaiting instructions. CTOC routes through the same machinery: below V1 it always
/// aborts; at or above V1 it is refused ("unable") unless the occupant ahead cannot be
/// overflown, in which case the pilot rejects anyway (AIM 4-4-1.a, 14 CFR 91.3(b)).
///
/// No recording — the repro is constructed: a rolling TakeoffPhase departure against a
/// LinedUpAndWaiting occupant at a midfield intersection (the issue's stated repro shape).
/// </summary>
public class Issue410RejectedTakeoffTests
{
    public Issue410RejectedTakeoffTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // -------------------------------------------------------------------------
    // Automatic rejected takeoff (the issue's repro shape)
    // -------------------------------------------------------------------------

    [Fact]
    public void RollingDeparture_LuawOccupantMidfield_RejectsStopsAndHolds()
    {
        // Occupant lined up at a midfield intersection 5,000 ft downfield, departure already
        // rolling at 60 kt (low-speed regime). It must reject, brake to a stop short of the
        // occupant, and hold in position — today it accelerates to Vr and lifts off straight
        // through the occupant's position.
        RunwayInfo runway = Runway28R();
        AircraftState dep = MakeRollingDeparture(runway, iasKts: 60);
        AircraftState occ = MakeLuawOccupant(runway, downfieldFt: 5000);
        PhaseContext ctx = Ctx(dep, runway, occ, autoReject: true);
        dep.Phases!.Start(ctx);

        (bool airborne, double minSepFt) = RunRoll(dep, ctx, occ, seconds: 120);

        Assert.False(airborne, $"Departure must reject, not lift off (IAS={dep.IndicatedAirspeed:F0})");
        Assert.True(dep.IndicatedAirspeed < 1.0, $"Departure should be stopped, IAS={dep.IndicatedAirspeed:F0}");
        Assert.True(minSepFt > 500, $"Departure must stop short of the occupant, min separation {minSepFt:F0} ft");
        Assert.True(
            dep.Phases?.CurrentPhase is HoldingInPositionPhase,
            $"Departure should hold in position awaiting instructions, was {dep.Phases?.CurrentPhase?.Name ?? "(none)"}"
        );
    }

    [Fact]
    public void StandstillWithBlockerAhead_DeclinesClearance_NeverRolls()
    {
        // Before the roll is underway there is no maneuver to abort (P/CG ABORT): the pilot
        // declines the clearance ("unable, traffic on the runway") and holds — the aircraft
        // must not creep forward through a reaction window first.
        RunwayInfo runway = Runway28R();
        AircraftState dep = MakeRollingDeparture(runway, iasKts: 0);
        AircraftState occ = MakeLuawOccupant(runway, downfieldFt: 5000);
        PhaseContext ctx = Ctx(dep, runway, occ, autoReject: true);
        dep.Phases!.Start(ctx);

        double maxIas = 0;
        for (int t = 0; t < 20; t++)
        {
            PhaseRunner.Tick(dep, ctx);
            IntegrateGroundDisplacement(dep);
            maxIas = Math.Max(maxIas, dep.IndicatedAirspeed);
        }

        Assert.True(dep.IsOnGround);
        Assert.Equal(0, dep.IndicatedAirspeed);
        Assert.True(maxIas < 5.0, $"A declined clearance must not roll, peak IAS={maxIas:F1}");
        Assert.True(
            dep.Phases?.CurrentPhase is HoldingInPositionPhase,
            $"Declined departure should hold in position, was {dep.Phases?.CurrentPhase?.Name ?? "(none)"}"
        );
    }

    [Fact]
    public void LowSpeedRoll_BlockerFarDownfield_StillRejects()
    {
        // Low-speed regime (below ~80 kt for a jet, roll underway): reject for ANY blocking
        // occupant ahead, even one far enough downfield to overfly — stopping is cheap, and
        // §3-9-6.a does not let a departure roll toward an occupied runway.
        RunwayInfo runway = Runway28R();
        AircraftState dep = MakeRollingDeparture(runway, iasKts: 40);
        AircraftState occ = MakeLuawOccupant(runway, downfieldFt: 11000);
        PhaseContext ctx = Ctx(dep, runway, occ, autoReject: true);
        dep.Phases!.Start(ctx);

        (bool airborne, double _) = RunRoll(dep, ctx, occ, seconds: 120);

        Assert.False(airborne, $"Low-speed roll with a blocker ahead must reject (IAS={dep.IndicatedAirspeed:F0})");
        Assert.True(dep.IndicatedAirspeed < 1.0, $"Departure should be stopped, IAS={dep.IndicatedAirspeed:F0}");
    }

    [Fact]
    public void HighSpeedRoll_OccupantOverflyable_ContinuesToLiftoff()
    {
        // High-speed regime with the liftoff point plus climb margin comfortably short of the
        // occupant: the takeoff continues — a real crew at 100+ kt does not reject for an
        // aircraft it will overfly with thousands of feet to spare.
        RunwayInfo runway = Runway28R();
        AircraftState dep = MakeRollingDeparture(runway, iasKts: 100);
        AircraftState occ = MakeLuawOccupant(runway, downfieldFt: 11000);
        PhaseContext ctx = Ctx(dep, runway, occ, autoReject: true);
        dep.Phases!.Start(ctx);

        (bool airborne, double _) = RunRoll(dep, ctx, occ, seconds: 60);

        Assert.True(airborne, $"Overflyable occupant must not trigger a reject at high speed (IAS={dep.IndicatedAirspeed:F0})");
    }

    /// <summary>
    /// A departure rolling at 60 kt (the low-speed regime) on <paramref name="runway"/> with a B738 held past the runway's hold line,
    /// <paramref name="nosePastFt"/> over the marking as <paramref name="place"/> measures it (past the marking, or toward the
    /// centreline from the bar); ticked on the real layout for up to two minutes.
    /// </summary>
    private static (bool WentAirborne, AircraftState Departure, AircraftState? Blocker) RollPastBarFouler(
        RunwayInfo runway,
        AirportGroundLayout layout,
        GroundNode bar,
        double nosePastFt,
        Func<RunwayInfo, GroundNode, string, bool, double, double, AircraftState> place
    )
    {
        AircraftState fouler = place(runway, bar, "B738", true, nosePastFt, 0.0);
        fouler.Phases = new PhaseList();
        fouler.Phases.Add(new TaxiingPhase());
        fouler.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        fouler.Ground.Hold = HoldDirective.HoldPosition;

        AircraftState dep = MakeRollingDeparture(runway, iasKts: 60);
        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = dep,
            Targets = dep.Targets,
            Category = AircraftCategorization.Categorize(dep.AircraftType),
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            GroundLayout = layout,
            Logger = NullLogger.Instance,
            ListAircraft = () => [dep, fouler],
            AutoRejectTakeoffOnOccupiedRunway = true,
        };
        Assert.Equal(RunwayUseKind.Fouling, RunwayOccupancy.Classify(fouler, runway, layout)?.Kind);
        AircraftState? blocker = RejectedTakeoff.FindBlockingOccupant([dep, fouler], dep, runway, layout, out _);
        dep.Phases!.Start(ctx);

        (bool airborne, double _) = RunRoll(dep, ctx, fouler, seconds: 120);
        return (airborne, dep, blocker);
    }

    [Fact]
    public void RollingDeparture_FoulerWithItsNoseOverThePavement_Rejects()
    {
        // The issue's B738 with its nose 25 ft from the KOAK 33 centreline, placed from the bar's own distance from that
        // centreline: part of it is over the 75 ft pavement, so it obstructs the departure's path.
        if (RunwayBarFixture.RunwayBar("OAK", "33") is not { } oak)
        {
            return;
        }

        (bool airborne, AircraftState dep, AircraftState? blocker) = RollPastBarFouler(
            oak.Runway,
            oak.Layout,
            oak.Bar,
            nosePastFt: RunwayHoldBars.CentrelineFt(oak.Runway, oak.Bar.Position) - 25.0,
            RunwayOccupancyTests.SquareToTheRunway
        );

        Assert.NotNull(blocker);
        Assert.False(airborne, $"Departure must reject for a fouler over the pavement (IAS={dep.IndicatedAirspeed:F0})");
        Assert.True(dep.IndicatedAirspeed < 1.0, $"Departure should be stopped, IAS={dep.IndicatedAirspeed:F0}");
    }

    [Fact]
    public void RollingDeparture_FoulerWithNoPartOverThePavement_ContinuesToLiftoff()
    {
        // The nose stands 10 ft outside the pavement edge, from the bar's own distance from the centreline: past the line, so a
        // hold-line overrun for the controller's advisory, but no part of it over the pavement.
        if (RunwayBarFixture.RunwayBar("OAK", "33") is not { } oak)
        {
            return;
        }

        (bool airborne, AircraftState dep, AircraftState? blocker) = RollPastBarFouler(
            oak.Runway,
            oak.Layout,
            oak.Bar,
            nosePastFt: RunwayHoldBars.CentrelineFt(oak.Runway, oak.Bar.Position) - ((oak.Runway.WidthFt / 2.0) + 10.0),
            RunwayOccupancyTests.SquareToTheRunway
        );

        Assert.Null(blocker);
        Assert.True(airborne, $"A fouler clear of the pavement must not trigger a reject (IAS={dep.IndicatedAirspeed:F0})");
    }

    [Fact]
    public void RollingDeparture_PartFifteenFeetBeyondTheEdge_Continues()
    {
        // KOAK 28L is 150 ft wide, so its pavement edge is 75 ft from the centreline. A B738's nose 15 ft past the edge (90 ft from
        // the centreline) is over the shoulder, not the pavement: the departure continues. The 30 ft lateral slack that other
        // pavement tests carry is graph-node jitter, and a body part computed from the type's dimensions has none.
        if (RunwayBarFixture.RunwayBar("OAK", "28L") is not { } oak)
        {
            return;
        }

        Assert.Equal(150.0, oak.Runway.WidthFt);
        double nosePastFt = RunwayHoldBars.CentrelineFt(oak.Runway, oak.Bar.Position) - ((oak.Runway.WidthFt / 2.0) + 15.0);
        (bool airborne, AircraftState dep, AircraftState? blocker) = RollPastBarFouler(
            oak.Runway,
            oak.Layout,
            oak.Bar,
            nosePastFt,
            RunwayOccupancyTests.SquareToTheRunway
        );

        Assert.Null(blocker);
        Assert.True(airborne, $"A part 15 ft beyond the pavement edge must not trigger a reject (IAS={dep.IndicatedAirspeed:F0})");
    }

    [Fact]
    public void RollingDeparture_PartFiveFeetInsideTheEdge_Rejects()
    {
        // The same runway with the nose 5 ft inside the edge (70 ft from the centreline): a part of it is over the pavement.
        if (RunwayBarFixture.RunwayBar("OAK", "28L") is not { } oak)
        {
            return;
        }

        Assert.Equal(150.0, oak.Runway.WidthFt);
        double nosePastFt = RunwayHoldBars.CentrelineFt(oak.Runway, oak.Bar.Position) - ((oak.Runway.WidthFt / 2.0) - 5.0);
        (bool airborne, AircraftState dep, AircraftState? blocker) = RollPastBarFouler(
            oak.Runway,
            oak.Layout,
            oak.Bar,
            nosePastFt,
            RunwayOccupancyTests.SquareToTheRunway
        );

        Assert.NotNull(blocker);
        Assert.False(airborne, $"A part 5 ft inside the pavement edge must reject (IAS={dep.IndicatedAirspeed:F0})");
        Assert.True(dep.IndicatedAirspeed < 1.0, $"Departure should be stopped, IAS={dep.IndicatedAirspeed:F0}");
    }

    [Fact]
    public void PastV1_OccupantOverflyable_Continues()
    {
        // At/above V1 with the occupant overflyable: committed — continue (14 CFR 25.107(a)(2)).
        RunwayInfo runway = Runway28R();
        AircraftCategory cat = AircraftCategorization.Categorize("B738");
        double v1 = AircraftPerformance.DecisionSpeed("B738", cat);
        AircraftState dep = MakeRollingDeparture(runway, iasKts: v1 + 2);
        AircraftState occ = MakeLuawOccupant(runway, downfieldFt: 11000);
        PhaseContext ctx = Ctx(dep, runway, occ, autoReject: true);
        dep.Phases!.Start(ctx);

        (bool airborne, double _) = RunRoll(dep, ctx, occ, seconds: 30);

        Assert.True(airborne, $"Past V1 with an overflyable occupant the takeoff continues (IAS={dep.IndicatedAirspeed:F0})");
    }

    [Fact]
    public void PastV1_OccupantNotOverflyable_EmergencyRejects()
    {
        // At/above V1 but the occupant cannot be overflown (liftoff + climb margin reaches past
        // it): continuing means a certain collision, so the pilot rejects anyway (AIM 4-4-1.a,
        // 14 CFR 91.3(b)). The stop may be long — the assertion is that it never lifts off and
        // ends stopped, holding in position.
        RunwayInfo runway = Runway28R();
        AircraftCategory cat = AircraftCategorization.Categorize("B738");
        double v1 = AircraftPerformance.DecisionSpeed("B738", cat);
        AircraftState dep = MakeRollingDeparture(runway, iasKts: v1 + 2);
        AircraftState occ = MakeLuawOccupant(runway, downfieldFt: 2500);
        PhaseContext ctx = Ctx(dep, runway, occ, autoReject: true);
        dep.Phases!.Start(ctx);

        (bool airborne, double _) = RunRoll(dep, ctx, occ, seconds: 120);

        Assert.False(airborne, $"Past V1 with an un-overflyable occupant the pilot must still reject (IAS={dep.IndicatedAirspeed:F0})");
        Assert.True(dep.IndicatedAirspeed < 1.0, $"Departure should be stopped, IAS={dep.IndicatedAirspeed:F0}");
        Assert.True(
            dep.Phases?.CurrentPhase is HoldingInPositionPhase,
            $"Departure should hold in position awaiting instructions, was {dep.Phases?.CurrentPhase?.Name ?? "(none)"}"
        );
        // At this speed and distance the stop cannot be made short of the occupant — the
        // instructor must be told the reject won't achieve separation (§3-9-6.a).
        Assert.Contains(dep.PendingWarnings, w => w.Contains("cannot stop short", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AutoRejectDisabled_DepartureLiftsOffUnimpeded()
    {
        // Session-setting gate: with AutoRejectTakeoffOnOccupiedRunway off (the replay-safety
        // default), the roll continues exactly as before this feature existed.
        RunwayInfo runway = Runway28R();
        AircraftState dep = MakeRollingDeparture(runway, iasKts: 0);
        AircraftState occ = MakeLuawOccupant(runway, downfieldFt: 5000);
        PhaseContext ctx = Ctx(dep, runway, occ, autoReject: false);
        dep.Phases!.Start(ctx);

        (bool airborne, double _) = RunRoll(dep, ctx, occ, seconds: 120);

        Assert.True(airborne, "With the setting off the departure must lift off as before");
    }

    // -------------------------------------------------------------------------
    // CTOC mid-roll (redesigned: routes through the rejected-takeoff machinery)
    // -------------------------------------------------------------------------

    [Fact]
    public void Ctoc_MidRoll_BelowV1_BrakesToStopAndHolds()
    {
        // CTOC below V1 must install the rejected-takeoff braking machinery — not clear the
        // phase list to nothing (the old behavior left the aircraft phase-less mid-runway,
        // decelerating at airborne rates with no terminal state).
        RunwayInfo runway = Runway28R();
        AircraftState dep = MakeRollingDeparture(runway, iasKts: 80);
        PhaseContext ctx = Ctx(dep, runway, MakeLuawOccupant(runway, downfieldFt: 11000), autoReject: false);
        dep.Phases!.Start(ctx);
        TakeoffPhase takeoff = Assert.IsType<TakeoffPhase>(dep.Phases.CurrentPhase);

        CommandResult result = DepartureClearanceHandler.TryCancelTakeoff(dep, takeoff, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success, result.Message);
        Assert.NotNull(dep.Phases);

        for (int t = 0; t < 60 && dep.Phases?.CurrentPhase is not HoldingInPositionPhase; t++)
        {
            PhaseRunner.Tick(dep, ctx);
        }

        Assert.True(dep.IndicatedAirspeed < 1.0, $"Aircraft should brake to a stop, IAS={dep.IndicatedAirspeed:F0}");
        Assert.True(
            dep.Phases?.CurrentPhase is HoldingInPositionPhase,
            $"Aborted departure should hold in position, was {dep.Phases?.CurrentPhase?.Name ?? "(none)"}"
        );
    }

    [Fact]
    public void Ctoc_PastV1_BlockerNotOverflyable_AcceptedAndRejects()
    {
        // CTOC at/above V1 is normally refused — but when a blocking occupant ahead cannot be
        // overflown, the pilot accepts the cancellation and rejects anyway (the same predicate
        // as the automatic emergency reject).
        RunwayInfo runway = Runway28R();
        AircraftCategory cat = AircraftCategorization.Categorize("B738");
        double v1 = AircraftPerformance.DecisionSpeed("B738", cat);
        AircraftState dep = MakeRollingDeparture(runway, iasKts: v1 + 2);
        AircraftState occ = MakeLuawOccupant(runway, downfieldFt: 2500);
        PhaseContext ctx = Ctx(dep, runway, occ, autoReject: false);
        dep.Phases!.Start(ctx);
        TakeoffPhase takeoff = Assert.IsType<TakeoffPhase>(dep.Phases.CurrentPhase);

        CommandResult result = DepartureClearanceHandler.TryCancelTakeoff(
            dep,
            takeoff,
            TestDispatch.Context(Random.Shared, listAircraft: () => [dep, occ])
        );

        Assert.True(result.Success, $"CTOC past V1 with an un-overflyable blocker must be accepted: {result.Message}");
        Assert.NotNull(dep.Phases);
    }

    [Fact]
    public void Ctoc_HeadwindV1Gate_ComparesIndicatedNotGroundspeed()
    {
        // On the ground the IAS field carries groundspeed. With a 4 kt headwind an aircraft at
        // groundspeed V1−3 is already past V1 *indicated* (V1 and Vr are 5 kt apart, so the
        // fixture stays below Vr — a reachable state) — the refusal gate must convert
        // (GroundFrame.IasForGroundSpeed) before comparing, as the rotation gate does.
        RunwayInfo runway = Runway28R();
        AircraftCategory cat = AircraftCategorization.Categorize("B738");
        double v1 = AircraftPerformance.DecisionSpeed("B738", cat);
        AircraftState dep = MakeRollingDeparture(runway, iasKts: v1 - 3);
        // Runway heading 270 → wind blowing toward east (E=+4) is a 4 kt headwind.
        dep.WindComponents = (0, 4);
        Assert.True(dep.HeadwindKts > 3, $"fixture: expected a headwind, got {dep.HeadwindKts:F1}");
        PhaseContext ctx = Ctx(dep, runway, MakeLuawOccupant(runway, downfieldFt: 11000), autoReject: false);
        dep.Phases!.Start(ctx);
        TakeoffPhase takeoff = Assert.IsType<TakeoffPhase>(dep.Phases.CurrentPhase);

        CommandResult result = DepartureClearanceHandler.TryCancelTakeoff(dep, takeoff, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success, "Past V1 indicated (groundspeed + headwind) — the abort must be refused");
    }

    // -------------------------------------------------------------------------
    // Overrun, constants, snapshots, and the post-stop occupancy advisory
    // -------------------------------------------------------------------------

    [Fact]
    public void Reject_TooLateToStop_OverrunsHonestly_AndFlagsIt()
    {
        // A short runway and a late high-speed reject: braking cannot finish on the pavement.
        // The physics stays honest — the aircraft rolls past the departure end while braking
        // (AIM 4-3-6.b.4 contemplates exactly that) — and the overrun is surfaced via the
        // Ground flag and an instructor warning.
        (double Lat, double Lon) end = GeoMath.ProjectPoint(37.72, -122.22, new TrueHeading(270), 0.35);
        RunwayInfo runway = TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            endLat: end.Lat,
            endLon: end.Lon,
            heading: 270,
            elevationFt: 9
        );
        AircraftCategory cat = AircraftCategorization.Categorize("B738");
        double v1 = AircraftPerformance.DecisionSpeed("B738", cat);
        AircraftState dep = MakeRollingDeparture(runway, iasKts: v1 + 2);
        AircraftState occ = MakeLuawOccupant(runway, downfieldFt: 1000);
        PhaseContext ctx = Ctx(dep, runway, occ, autoReject: true);
        dep.Phases!.Start(ctx);

        // Capture the braking phase after the trigger fires so its latched state is assertable
        // even once the phase completes into the hold.
        PhaseRunner.Tick(dep, ctx);
        IntegrateGroundDisplacement(dep);
        PhaseRunner.Tick(dep, ctx);
        IntegrateGroundDisplacement(dep);
        RejectedTakeoffPhase reject = Assert.IsType<RejectedTakeoffPhase>(dep.Phases.CurrentPhase);

        (bool airborne, double _) = RunRoll(dep, ctx, occ, seconds: 120);

        Assert.False(airborne, "The reject must stick even when the stop runs long");
        Assert.True(dep.IndicatedAirspeed < 1.0, $"Aircraft should eventually stop, IAS={dep.IndicatedAirspeed:F0}");
        Assert.True(reject.OverrunReported, "The overrun must be latched on the phase");
        Assert.True(reject.AutoTriggered, "A blocked-runway reject is pilot-initiated");
        Assert.Contains(dep.PendingWarnings, w => w.Contains("overran", StringComparison.OrdinalIgnoreCase));
        double alongFt =
            GeoMath.AlongTrackDistanceNm(dep.Position, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading)
            * GeoMath.FeetPerNm;
        Assert.True(alongFt > runway.PavementLengthFt, $"Aircraft should have rolled past the end ({alongFt:F0} ft of {runway.PavementLengthFt:F0})");
    }

    [Fact]
    public void BrakingRates_OrderedPerCategory()
    {
        // Rollout (normal ops) < expedite exit (autobrake MAX) < rejected takeoff (max effort).
        foreach (AircraftCategory cat in new[] { AircraftCategory.Jet, AircraftCategory.Turboprop, AircraftCategory.Piston })
        {
            double rollout = CategoryPerformance.RolloutDecelRate(cat);
            double expedite = CategoryPerformance.ExpediteExitDecelRate(cat);
            double rto = CategoryPerformance.RejectedTakeoffDecelRate(cat);
            Assert.True(rollout < expedite, $"{cat}: RolloutDecelRate {rollout} should be below ExpediteExitDecelRate {expedite}");
            Assert.True(expedite < rto, $"{cat}: ExpediteExitDecelRate {expedite} should be below RejectedTakeoffDecelRate {rto}");
        }

        Assert.Equal(0, CategoryPerformance.RejectedTakeoffDecelRate(AircraftCategory.Helicopter));
    }

    [Fact]
    public void RejectedTakeoffPhase_SurvivesSnapshotRoundTrip_MidBraking()
    {
        RunwayInfo runway = Runway28R();
        AircraftState dep = MakeRollingDeparture(runway, iasKts: 80);
        AircraftState occ = MakeLuawOccupant(runway, downfieldFt: 5000);
        PhaseContext ctx = Ctx(dep, runway, occ, autoReject: true);
        dep.Phases!.Start(ctx);

        // Trigger the reject and get a few ticks into the braking.
        for (int t = 0; t < 5; t++)
        {
            PhaseRunner.Tick(dep, ctx);
        }

        RejectedTakeoffPhase reject = Assert.IsType<RejectedTakeoffPhase>(dep.Phases.CurrentPhase);

        string json = System.Text.Json.JsonSerializer.Serialize(reject.ToSnapshot());
        PhaseDto? dto = System.Text.Json.JsonSerializer.Deserialize<Yaat.Sim.Simulation.Snapshots.PhaseDto>(json);
        RejectedTakeoffPhaseDto restoredDto = Assert.IsType<Yaat.Sim.Simulation.Snapshots.RejectedTakeoffPhaseDto>(dto);
        var restored = RejectedTakeoffPhase.FromSnapshot(restoredDto);

        Assert.Equal(reject.Status, restored.Status);

        // The restored phase must brake the same aircraft to a stop.
        dep.Phases.Phases[dep.Phases.Phases.IndexOf(reject)] = restored;
        for (int t = 0; t < 60 && dep.IndicatedAirspeed > 0; t++)
        {
            PhaseRunner.Tick(dep, ctx);
            IntegrateGroundDisplacement(dep);
        }

        Assert.True(dep.IndicatedAirspeed < 1.0, $"Restored phase should finish the stop, IAS={dep.IndicatedAirspeed:F0}");
    }

    [Fact]
    public void PostRtoHold_TripsRunwayOccupiedAdvisory_ForNextClearance()
    {
        // After the reject, the aircraft holds in position ON the runway — a subsequent landing
        // clearance for that runway must draw the existing 3-10-5.e occupied-runway advisory
        // with no new code (HoldingInPositionPhase + pavement containment).
        RunwayInfo runway = Runway28R();
        AircraftState dep = MakeRollingDeparture(runway, iasKts: 0);
        AircraftState occ = MakeLuawOccupant(runway, downfieldFt: 5000);
        PhaseContext ctx = Ctx(dep, runway, occ, autoReject: true);
        dep.Phases!.Start(ctx);
        RunRoll(dep, ctx, occ, seconds: 120);
        Assert.True(dep.Phases?.CurrentPhase is HoldingInPositionPhase, "fixture: reject should end holding in position");

        var arrival = new AircraftState
        {
            Callsign = "ARR1",
            AircraftType = "B738",
            Position = new LatLon(37.72, -122.10),
            TrueHeading = new TrueHeading(270),
            Altitude = 1500,
            IndicatedAirspeed = 140,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "OAK",
                Altitude = PlannedAltitude.Ifr(5000),
            },
        };

        RunwaySafetyAdvisor.WarnIfRunwayOccupied(arrival, runway, TestDispatch.Context(Random.Shared, listAircraft: () => [arrival, dep, occ]));

        Assert.Contains(arrival.PendingWarnings, w => w.Contains(dep.Callsign));
    }

    [Fact]
    public void Ctoc_HelicopterTakeoff_Accepted_NoV1Gate()
    {
        // Helicopters have Vr = 0, so a shared V1 gate would refuse every helicopter CTOC.
        // A helicopter can stop its departure at any point: CTOC during HelicopterTakeoffPhase
        // must be accepted and leave the aircraft holding (hover), not fail with "no takeoff
        // clearance to cancel".
        var heli = new AircraftState
        {
            Callsign = "HELO1",
            AircraftType = "EC35",
            Position = new LatLon(37.72, -122.22),
            TrueHeading = new TrueHeading(270),
            Altitude = 60,
            IndicatedAirspeed = 0,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Altitude = PlannedAltitude.Vfr(1500) },
        };
        Assert.Equal(AircraftCategory.Helicopter, AircraftCategorization.Categorize(heli.AircraftType));
        heli.Phases = new PhaseList();
        var takeoff = new HelicopterTakeoffPhase();
        heli.Phases.Add(takeoff);
        heli.Phases.Start(CommandDispatcher.BuildMinimalContext(heli, ActiveRunways.Empty));

        CommandResult result = DepartureClearanceHandler.TryCancelTakeoff(heli, takeoff, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success, $"Helicopter CTOC mid-liftoff must be accepted: {result.Message}");
        Assert.NotNull(heli.Phases);
    }
}
