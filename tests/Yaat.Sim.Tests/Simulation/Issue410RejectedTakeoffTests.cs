using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

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

    private const double PavementLengthNm = 2.0;

    private static RunwayInfo Runway28R()
    {
        var end = GeoMath.ProjectPoint(37.72, -122.22, new TrueHeading(270), PavementLengthNm);
        return TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            endLat: end.Lat,
            endLon: end.Lon,
            heading: 270,
            elevationFt: 9
        );
    }

    private static AircraftState MakeRollingDeparture(RunwayInfo runway, double iasKts)
    {
        var ac = new AircraftState
        {
            Callsign = "DEP1",
            AircraftType = "B738",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = iasKts,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Altitude = PlannedAltitude.Ifr(5000) },
        };
        ac.Phases = new PhaseList { AssignedRunway = runway };
        ac.Phases.Add(new TakeoffPhase());
        return ac;
    }

    private static AircraftState MakeLuawOccupant(RunwayInfo runway, double downfieldFt)
    {
        var pos = GeoMath.ProjectPoint(runway.ThresholdLatitude, runway.ThresholdLongitude, runway.TrueHeading, downfieldFt / GeoMath.FeetPerNm);
        var occ = new AircraftState
        {
            Callsign = "OCC1",
            AircraftType = "B738",
            Position = new LatLon(pos.Lat, pos.Lon),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Altitude = PlannedAltitude.Ifr(5000) },
        };
        occ.Phases = new PhaseList { AssignedRunway = runway };
        occ.Phases.Add(new LinedUpAndWaitingPhase());
        occ.Phases.Start(CommandDispatcher.BuildMinimalContext(occ));
        return occ;
    }

    private static PhaseContext Ctx(AircraftState departure, RunwayInfo runway, AircraftState occupant, bool autoReject)
    {
        return new PhaseContext
        {
            Aircraft = departure,
            Targets = departure.Targets,
            Category = AircraftCategorization.Categorize(departure.AircraftType),
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            Logger = NullLogger.Instance,
            ListAircraft = () => [departure, occupant],
            AutoRejectTakeoffOnOccupiedRunway = autoReject,
        };
    }

    /// <summary>
    /// Ticks the departure's phase list for up to <paramref name="seconds"/>, integrating ground
    /// displacement manually (position integration is FlightPhysics' job, absent here); stops
    /// early once airborne.
    /// </summary>
    private static (bool WentAirborne, double MinSeparationFt) RunRoll(AircraftState departure, PhaseContext ctx, AircraftState occupant, int seconds)
    {
        bool airborne = false;
        double minSepFt = double.MaxValue;
        for (int t = 0; t < seconds; t++)
        {
            PhaseRunner.Tick(departure, ctx);
            IntegrateGroundDisplacement(departure);
            minSepFt = Math.Min(minSepFt, GeoMath.DistanceNm(departure.Position, occupant.Position) * GeoMath.FeetPerNm);
            if (!departure.IsOnGround)
            {
                airborne = true;
                break;
            }
        }

        return (airborne, minSepFt);
    }

    private static void IntegrateGroundDisplacement(AircraftState departure)
    {
        if (!departure.IsOnGround || (departure.GroundSpeed <= 0))
        {
            return;
        }

        var moved = GeoMath.ProjectPoint(departure.Position.Lat, departure.Position.Lon, departure.TrueHeading, departure.GroundSpeed / 3600.0);
        departure.Position = new LatLon(moved.Lat, moved.Lon);
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
        var runway = Runway28R();
        var dep = MakeRollingDeparture(runway, iasKts: 60);
        var occ = MakeLuawOccupant(runway, downfieldFt: 5000);
        var ctx = Ctx(dep, runway, occ, autoReject: true);
        dep.Phases!.Start(ctx);

        var (airborne, minSepFt) = RunRoll(dep, ctx, occ, seconds: 120);

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
        var runway = Runway28R();
        var dep = MakeRollingDeparture(runway, iasKts: 0);
        var occ = MakeLuawOccupant(runway, downfieldFt: 5000);
        var ctx = Ctx(dep, runway, occ, autoReject: true);
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
        var runway = Runway28R();
        var dep = MakeRollingDeparture(runway, iasKts: 40);
        var occ = MakeLuawOccupant(runway, downfieldFt: 11000);
        var ctx = Ctx(dep, runway, occ, autoReject: true);
        dep.Phases!.Start(ctx);

        var (airborne, _) = RunRoll(dep, ctx, occ, seconds: 120);

        Assert.False(airborne, $"Low-speed roll with a blocker ahead must reject (IAS={dep.IndicatedAirspeed:F0})");
        Assert.True(dep.IndicatedAirspeed < 1.0, $"Departure should be stopped, IAS={dep.IndicatedAirspeed:F0}");
    }

    [Fact]
    public void HighSpeedRoll_OccupantOverflyable_ContinuesToLiftoff()
    {
        // High-speed regime with the liftoff point plus climb margin comfortably short of the
        // occupant: the takeoff continues — a real crew at 100+ kt does not reject for an
        // aircraft it will overfly with thousands of feet to spare.
        var runway = Runway28R();
        var dep = MakeRollingDeparture(runway, iasKts: 100);
        var occ = MakeLuawOccupant(runway, downfieldFt: 11000);
        var ctx = Ctx(dep, runway, occ, autoReject: true);
        dep.Phases!.Start(ctx);

        var (airborne, _) = RunRoll(dep, ctx, occ, seconds: 60);

        Assert.True(airborne, $"Overflyable occupant must not trigger a reject at high speed (IAS={dep.IndicatedAirspeed:F0})");
    }

    [Fact]
    public void PastV1_OccupantOverflyable_Continues()
    {
        // At/above V1 with the occupant overflyable: committed — continue (14 CFR 25.107(a)(2)).
        var runway = Runway28R();
        var cat = AircraftCategorization.Categorize("B738");
        double v1 = AircraftPerformance.DecisionSpeed("B738", cat);
        var dep = MakeRollingDeparture(runway, iasKts: v1 + 2);
        var occ = MakeLuawOccupant(runway, downfieldFt: 11000);
        var ctx = Ctx(dep, runway, occ, autoReject: true);
        dep.Phases!.Start(ctx);

        var (airborne, _) = RunRoll(dep, ctx, occ, seconds: 30);

        Assert.True(airborne, $"Past V1 with an overflyable occupant the takeoff continues (IAS={dep.IndicatedAirspeed:F0})");
    }

    [Fact]
    public void PastV1_OccupantNotOverflyable_EmergencyRejects()
    {
        // At/above V1 but the occupant cannot be overflown (liftoff + climb margin reaches past
        // it): continuing means a certain collision, so the pilot rejects anyway (AIM 4-4-1.a,
        // 14 CFR 91.3(b)). The stop may be long — the assertion is that it never lifts off and
        // ends stopped, holding in position.
        var runway = Runway28R();
        var cat = AircraftCategorization.Categorize("B738");
        double v1 = AircraftPerformance.DecisionSpeed("B738", cat);
        var dep = MakeRollingDeparture(runway, iasKts: v1 + 2);
        var occ = MakeLuawOccupant(runway, downfieldFt: 2500);
        var ctx = Ctx(dep, runway, occ, autoReject: true);
        dep.Phases!.Start(ctx);

        var (airborne, _) = RunRoll(dep, ctx, occ, seconds: 120);

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
        var runway = Runway28R();
        var dep = MakeRollingDeparture(runway, iasKts: 0);
        var occ = MakeLuawOccupant(runway, downfieldFt: 5000);
        var ctx = Ctx(dep, runway, occ, autoReject: false);
        dep.Phases!.Start(ctx);

        var (airborne, _) = RunRoll(dep, ctx, occ, seconds: 120);

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
        var runway = Runway28R();
        var dep = MakeRollingDeparture(runway, iasKts: 80);
        var ctx = Ctx(dep, runway, MakeLuawOccupant(runway, downfieldFt: 11000), autoReject: false);
        dep.Phases!.Start(ctx);
        var takeoff = Assert.IsType<TakeoffPhase>(dep.Phases.CurrentPhase);

        var result = DepartureClearanceHandler.TryCancelTakeoff(dep, takeoff, TestDispatch.Context(Random.Shared));

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
        var runway = Runway28R();
        var cat = AircraftCategorization.Categorize("B738");
        double v1 = AircraftPerformance.DecisionSpeed("B738", cat);
        var dep = MakeRollingDeparture(runway, iasKts: v1 + 2);
        var occ = MakeLuawOccupant(runway, downfieldFt: 2500);
        var ctx = Ctx(dep, runway, occ, autoReject: false);
        dep.Phases!.Start(ctx);
        var takeoff = Assert.IsType<TakeoffPhase>(dep.Phases.CurrentPhase);

        var result = DepartureClearanceHandler.TryCancelTakeoff(dep, takeoff, TestDispatch.Context(Random.Shared, listAircraft: () => [dep, occ]));

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
        var runway = Runway28R();
        var cat = AircraftCategorization.Categorize("B738");
        double v1 = AircraftPerformance.DecisionSpeed("B738", cat);
        var dep = MakeRollingDeparture(runway, iasKts: v1 - 3);
        // Runway heading 270 → wind blowing toward east (E=+4) is a 4 kt headwind.
        dep.WindComponents = (0, 4);
        Assert.True(dep.HeadwindKts > 3, $"fixture: expected a headwind, got {dep.HeadwindKts:F1}");
        var ctx = Ctx(dep, runway, MakeLuawOccupant(runway, downfieldFt: 11000), autoReject: false);
        dep.Phases!.Start(ctx);
        var takeoff = Assert.IsType<TakeoffPhase>(dep.Phases.CurrentPhase);

        var result = DepartureClearanceHandler.TryCancelTakeoff(dep, takeoff, TestDispatch.Context(Random.Shared));

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
        var end = GeoMath.ProjectPoint(37.72, -122.22, new TrueHeading(270), 0.35);
        var runway = TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            endLat: end.Lat,
            endLon: end.Lon,
            heading: 270,
            elevationFt: 9
        );
        var cat = AircraftCategorization.Categorize("B738");
        double v1 = AircraftPerformance.DecisionSpeed("B738", cat);
        var dep = MakeRollingDeparture(runway, iasKts: v1 + 2);
        var occ = MakeLuawOccupant(runway, downfieldFt: 1000);
        var ctx = Ctx(dep, runway, occ, autoReject: true);
        dep.Phases!.Start(ctx);

        // Capture the braking phase after the trigger fires so its latched state is assertable
        // even once the phase completes into the hold.
        PhaseRunner.Tick(dep, ctx);
        IntegrateGroundDisplacement(dep);
        PhaseRunner.Tick(dep, ctx);
        IntegrateGroundDisplacement(dep);
        var reject = Assert.IsType<RejectedTakeoffPhase>(dep.Phases.CurrentPhase);

        var (airborne, _) = RunRoll(dep, ctx, occ, seconds: 120);

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
        foreach (var cat in new[] { AircraftCategory.Jet, AircraftCategory.Turboprop, AircraftCategory.Piston })
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
        var runway = Runway28R();
        var dep = MakeRollingDeparture(runway, iasKts: 80);
        var occ = MakeLuawOccupant(runway, downfieldFt: 5000);
        var ctx = Ctx(dep, runway, occ, autoReject: true);
        dep.Phases!.Start(ctx);

        // Trigger the reject and get a few ticks into the braking.
        for (int t = 0; t < 5; t++)
        {
            PhaseRunner.Tick(dep, ctx);
        }

        var reject = Assert.IsType<RejectedTakeoffPhase>(dep.Phases.CurrentPhase);

        var json = System.Text.Json.JsonSerializer.Serialize(reject.ToSnapshot());
        var dto = System.Text.Json.JsonSerializer.Deserialize<Yaat.Sim.Simulation.Snapshots.PhaseDto>(json);
        var restoredDto = Assert.IsType<Yaat.Sim.Simulation.Snapshots.RejectedTakeoffPhaseDto>(dto);
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
        var runway = Runway28R();
        var dep = MakeRollingDeparture(runway, iasKts: 0);
        var occ = MakeLuawOccupant(runway, downfieldFt: 5000);
        var ctx = Ctx(dep, runway, occ, autoReject: true);
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
        heli.Phases.Start(CommandDispatcher.BuildMinimalContext(heli));

        var result = DepartureClearanceHandler.TryCancelTakeoff(heli, takeoff, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success, $"Helicopter CTOC mid-liftoff must be accepted: {result.Message}");
        Assert.NotNull(heli.Phases);
    }
}
