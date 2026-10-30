using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Training;

namespace Yaat.Sim.Tests;

/// <summary>
/// Pilot-initiated go-around when the runway is occupied on short final (AIM 5-2-5.9: never land on
/// an occupied runway even with a clearance; AIM 5-5-5.a.2: say why). The gate is time-to-threshold
/// with the 7110.65 §3-10-3 landmark exceptions, so a landed aircraft far enough down the runway does
/// not trigger it.
/// </summary>
public class OccupiedRunwayGoAroundTests
{
    private const double ElevationFt = 100;

    private static readonly RunwayInfo Runway = MakeRunway();

    public OccupiedRunwayGoAroundTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static RunwayInfo MakeRunway()
    {
        var threshold = new LatLon(37.0, -122.0);
        LatLon end = GeoMath.ProjectPoint(threshold, new TrueHeading(280), 10_000 / GeoMath.FeetPerNm);
        return TestRunwayFactory.Make(
            designator: "28",
            thresholdLat: threshold.Lat,
            thresholdLon: threshold.Lon,
            endLat: end.Lat,
            endLon: end.Lon,
            heading: 280,
            elevationFt: ElevationFt
        );
    }

    private static LatLon Threshold => new(Runway.ThresholdLatitude, Runway.ThresholdLongitude);

    private static LatLon OnRunway(double alongFt) => GeoMath.ProjectPoint(Threshold, Runway.TrueHeading, alongFt / GeoMath.FeetPerNm);

    private static AircraftState Arrival(string type, double distanceNm, double aglFt)
    {
        var ac = new AircraftState
        {
            Callsign = "ARR1",
            AircraftType = type,
            Position = GeoMath.ProjectPoint(Threshold, Runway.TrueHeading.ToReciprocal(), distanceNm),
            TrueHeading = Runway.TrueHeading,
            TrueTrack = Runway.TrueHeading,
            Altitude = ElevationFt + aglFt,
            IndicatedAirspeed = type == "C172" ? 70 : 130,
            VerticalSpeed = -600,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "KTEST", Destination = "KTEST" },
            Phases = new PhaseList { AssignedRunway = Runway },
        };
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        return ac;
    }

    private static AircraftState Occupant(string type, LatLon position, double groundSpeedKts, Phase? phase, bool onGround)
    {
        var ac = new AircraftState
        {
            Callsign = "OCC1",
            AircraftType = type,
            Position = position,
            TrueHeading = Runway.TrueHeading,
            TrueTrack = Runway.TrueHeading,
            Altitude = ElevationFt,
            IndicatedAirspeed = groundSpeedKts,
            IsOnGround = onGround,
        };
        if (phase is not null)
        {
            ac.Phases = new PhaseList { AssignedRunway = Runway };
            ac.Phases.Add(phase);
            ac.Phases.CurrentPhase!.Status = PhaseStatus.Active;

            // The sim never carries a rolling TakeoffPhase with a zero roll clock: OnStart seeds it from
            // the speed the roll enters at, and every predictor reads that clock through
            // GroundRollProfile.RollClockSeconds. Seed it here too, or an occupant rolling at 100 kt is
            // projected as though it had just released the brakes.
            if (phase is TakeoffPhase takeoff)
            {
                takeoff.SeedRollClockIfUnset(ac, AircraftCategorization.Categorize(type));
            }
        }

        return ac;
    }

    private static PhaseContext Ctx(AircraftState arrival, IReadOnlyList<AircraftState> all, bool setting) =>
        new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = arrival,
            Targets = arrival.Targets,
            Category = AircraftCategorization.Categorize(arrival.AircraftType),
            DeltaSeconds = 1.0,
            Runway = Runway,
            FieldElevation = Runway.ElevationFt,
            Logger = NullLogger.Instance,
            AutoClearedToLand = true,
            AutoGoAroundOnOccupiedRunway = setting,
            ListAircraft = () => all,
        };

    private static bool GoesAround(AircraftState arrival, AircraftState occupant, bool setting)
    {
        PhaseContext ctx = Ctx(arrival, [arrival, occupant], setting);
        arrival.Phases!.Start(ctx);
        arrival.Phases.CurrentPhase!.OnTick(ctx);
        return arrival.Phases.CurrentPhase is GoAroundPhase;
    }

    [Fact]
    public void LinedUpOccupant_TriggersGoAroundWithSpokenReason()
    {
        AircraftState arrival = Arrival("B738", 0.6, 150);
        AircraftState occupant = Occupant("B738", OnRunway(300), 0, new LinedUpAndWaitingPhase(), onGround: true);

        Assert.True(GoesAround(arrival, occupant, setting: true));
        Assert.Contains(arrival.PendingWarnings, w => w.Contains("going around, traffic on the runway", StringComparison.Ordinal));
    }

    /// <summary>
    /// An arrival about 16 s out on a KOAK runway's final (a B738 at 130 kt 0.6 nm, a C172 at 70 kt 0.3 nm), inside the decision
    /// window, with the ground layout in its context.
    /// </summary>
    private static bool GoesAroundAtOak(
        RunwayInfo runway,
        AirportGroundLayout layout,
        AircraftState occupant,
        string arrivalType,
        out AircraftState arrival
    )
    {
        bool piston = arrivalType == "C172";
        arrival = new AircraftState
        {
            Callsign = "ARR1",
            AircraftType = arrivalType,
            Position = GeoMath.ProjectPoint(
                new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
                runway.TrueHeading.ToReciprocal(),
                piston ? 0.3 : 0.6
            ),
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = runway.ElevationFt + 200,
            IndicatedAirspeed = piston ? 70 : 130,
            VerticalSpeed = -600,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK", Destination = "KOAK" },
            Phases = new PhaseList { AssignedRunway = runway },
        };
        arrival.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        AircraftState self = arrival;
        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = arrival,
            Targets = arrival.Targets,
            Category = AircraftCategorization.Categorize(arrival.AircraftType),
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            GroundLayout = layout,
            Logger = NullLogger.Instance,
            AutoClearedToLand = true,
            AutoGoAroundOnOccupiedRunway = true,
            ListAircraft = () => [self, occupant],
        };
        arrival.Phases.Start(ctx);
        arrival.Phases.CurrentPhase!.OnTick(ctx);
        return arrival.Phases.CurrentPhase is GoAroundPhase;
    }

    /// <summary>A B738 held on a taxi with its nose <paramref name="nosePastFt"/> past runway 33's holding position marking.</summary>
    private static AircraftState HeldFouler(RunwayInfo runway, GroundNode bar, double nosePastFt)
    {
        AircraftState fouler = RunwayOccupancyTests.SquareToTheMarking(runway, bar, "B738", facingRunway: true, nosePastFt, alongFt: 0);
        fouler.Phases = new PhaseList();
        fouler.Phases.Add(new TaxiingPhase());
        fouler.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        fouler.Ground.Hold = HoldDirective.HoldPosition;
        return fouler;
    }

    [Fact]
    public void FoulerInsideTheEdgeBand_GoesAround()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        // The issue's B738, 150 ft past the 175 ft line of the 75 ft runway: its nose is 25 ft from the centreline, inside the
        // runway safety area's half-width, and a pilot on short final sees it in the landing corridor.
        AircraftState fouler = HeldFouler(oak.Runway, oak.Bar, nosePastFt: 150.0);

        Assert.Equal(RunwayUseKind.Fouling, RunwayOccupancy.Classify(fouler, oak.Runway, oak.Layout)?.Kind);
        Assert.True(GoesAroundAtOak(oak.Runway, oak.Layout, fouler, "B738", out AircraftState arrival));
        Assert.Contains(arrival.PendingWarnings, w => w.Contains($"{fouler.Callsign} on 33 (past the hold line, ", StringComparison.Ordinal));
    }

    [Fact]
    public void FoulerOutsideTheEdgeBand_NoGoAround()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        // A nose 10 ft over the line is a hold-line overrun for the controller's advisory, not traffic the pilot goes around for.
        AircraftState fouler = HeldFouler(oak.Runway, oak.Bar, nosePastFt: 10.0);

        Assert.Equal(RunwayUseKind.Fouling, RunwayOccupancy.Classify(fouler, oak.Runway, oak.Layout)?.Kind);
        Assert.False(GoesAroundAtOak(oak.Runway, oak.Layout, fouler, "B738", out _));
    }

    [Fact]
    public void HeldAfterExit_TailInside_KeepsTheLandmarkException()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "28L") is not { } oak)
        {
            return;
        }

        // A landed C172 held after its exit with its tail 140 ft from the centreline (inside the marking and the edge band), on
        // a bar that is short of 3,000 ft from one landing threshold and beyond it from the other. Behind it a C172 arrival goes
        // around from the near end and lands from the far one (3-10-3.a.1: Category I behind Category I, 3,000 ft).
        double barFt = RunwayHoldBars.CentrelineFt(oak.Runway, oak.Bar.Position);
        AircraftState held = RunwayOccupancyTests.SquareToTheRunway(oak.Runway, oak.Bar, "C172", facingRunway: false, barFt - 140.0, alongFt: 0);
        held.Phases = new PhaseList { AssignedRunway = oak.Runway };
        held.Phases.Add(new HoldingAfterExitPhase(oak.Runway.Designator, exitTaxiway: null, oak.Bar.Id, heldShortOfRunway: null));
        held.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        held.Ground.Hold = HoldDirective.HoldPosition;

        RunwayInfo[] ends = [oak.Runway, Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("OAK", "10R"))];
        double DownfieldFt(RunwayInfo end) =>
            GeoMath.AlongTrackDistanceNm(held.Position, LandingThreshold.Resolve(end, oak.Layout), end.TrueHeading) * GeoMath.FeetPerNm;
        RunwayInfo near = ends.First(e => DownfieldFt(e) < 2800.0);
        RunwayInfo far = ends.First(e => DownfieldFt(e) > 3200.0);

        Assert.Equal(RunwayUseKind.Fouling, RunwayOccupancy.Classify(held, far, oak.Layout)?.Kind);
        Assert.True(GoesAroundAtOak(near, oak.Layout, held, "C172", out _));
        Assert.False(GoesAroundAtOak(far, oak.Layout, held, "C172", out _));
    }

    [Fact]
    public void RestrictedExitingFouler_IsNeverProjectedClear()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        // A landed B738 on its exit with its tail 60 ft from the centreline, held while still rolling: its exit plan alone would
        // read it clear before the arrival's threshold, but the hold keeps its tail inside the marking (3-10-6.a).
        double barFt = RunwayHoldBars.CentrelineFt(oak.Runway, oak.Bar.Position);
        AircraftState exiting = RunwayOccupancyTests.SquareToTheRunway(oak.Runway, oak.Bar, "B738", facingRunway: false, barFt - 60.0, alongFt: 0);
        exiting.IndicatedAirspeed = 10;
        exiting.Phases = new PhaseList { AssignedRunway = oak.Runway };
        exiting.Phases.Add(
            RunwayExitPhase.FromSnapshot(
                new RunwayExitPhaseDto
                {
                    Status = (int)PhaseStatus.Active,
                    ElapsedSeconds = 20,
                    ExitNodeId = oak.Bar.Id,
                    ReachedExitNode = false,
                    ExitSpeed = 10,
                    TimeSinceLastLog = 0,
                    RunwayHeadingDeg = oak.Runway.TrueHeading.Degrees,
                    ExitStateValue = (int)RunwayExitPhase.ExitState.FollowingExitPath,
                },
                oak.Layout
            )
        );
        exiting.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        exiting.Ground.Hold = HoldDirective.HoldPosition;

        Assert.Equal(RunwayUseKind.Fouling, RunwayOccupancy.Classify(exiting, oak.Runway, oak.Layout)?.Kind);
        Assert.True(GoesAroundAtOak(oak.Runway, oak.Layout, exiting, "B738", out AircraftState arrival));
        Assert.Contains(arrival.PendingWarnings, w => w.Contains("(landing rollout, ", StringComparison.Ordinal));
    }

    /// <summary>A B738 crossing runway 33 at 15 kt with its nose 20 ft past the centreline, its centre still on the near half.</summary>
    private static AircraftState MidCrossing(AirportGroundLayout layout, RunwayInfo runway, GroundNode bar)
    {
        double barFt = RunwayHoldBars.CentrelineFt(runway, bar.Position);
        AircraftState crossing = RunwayOccupancyTests.SquareToTheRunway(runway, bar, "B738", facingRunway: true, barFt + 20.0, alongFt: 0);
        crossing.IndicatedAirspeed = 15;
        crossing.Targets.TargetSpeed = 15;
        crossing.Phases = new PhaseList();
        crossing.Phases.Add(new CrossingRunwayPhase(bar.Id, RunwayOccupancyTests.FarSideBar(layout, runway, bar).Id, runway.Designator));
        crossing.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        return crossing;
    }

    [Fact]
    public void MovingCrossing_ClearBeforeThreshold_NoGoAround()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        // About 180 ft to tail-clear of the far pavement edge at 15 kt: 7 s, against 16 s to the threshold (3-7-2.g.2(a)).
        AircraftState crossing = MidCrossing(oak.Layout, oak.Runway, oak.Bar);

        Assert.Equal(RunwayUseKind.Crossing, RunwayOccupancy.Classify(crossing, oak.Runway, oak.Layout)?.Kind);
        Assert.False(GoesAroundAtOak(oak.Runway, oak.Layout, crossing, "B738", out _));
    }

    [Fact]
    public void CrossingFarBar_LayoutOfAnotherAirport_IsNotAFarBar()
    {
        if ((RunwayBarFixture.RunwayBar("MIA", "12") is not { } mia) || (new TestAirportGroundData().GetLayout("OAK") is not { } oak))
        {
            return;
        }

        // Real layouts: a crossing on MIA's 12/30 pavement, judged with OAK's layout. OAK's hold-short bars say nothing about MIA's
        // runway, so the crossing is refused by the generic branch ("crossing the runway, N ft down") instead of the crossing
        // projection's answer, which would read it clear and let the arrival land. OAK's node with the same numeric id as MIA's far
        // bar is itself a 12/30 hold-short bar, which is what makes this guard observable on real data.
        AircraftState crossing = MidCrossing(mia.Layout, mia.Runway, mia.Bar);
        AircraftState arrival = Arrival("B738", 0.6, 150);

        Assert.Equal(RunwayUseKind.Crossing, RunwayOccupancy.Classify(crossing, mia.Runway, mia.Layout)?.Kind);
        OccupiedRunwayGoAround.BlockingOccupant? blocker = OccupiedRunwayGoAround.FindBlockingOccupant(
            [crossing],
            arrival,
            mia.Runway,
            oak,
            secondsToThreshold: 16.0
        );

        Assert.NotNull(blocker);
        Assert.Contains("crossing the runway", blocker.Reason, StringComparison.Ordinal);
        Assert.Contains(" ft down", blocker.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("not clear in", blocker.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossingTooSlowToClearBeforeThreshold_GoesAround()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        // Moving and unrestricted, but at 2 kt toward a 3 kt ceiling its tail needs well over the 16 s left to the threshold.
        AircraftState crossing = MidCrossing(oak.Layout, oak.Runway, oak.Bar);
        crossing.IndicatedAirspeed = 2;
        crossing.Targets.TargetSpeed = 3;

        Assert.Equal(RunwayUseKind.Crossing, RunwayOccupancy.Classify(crossing, oak.Runway, oak.Layout)?.Kind);
        Assert.True(GoesAroundAtOak(oak.Runway, oak.Layout, crossing, "B738", out AircraftState arrival));
        Assert.Contains(arrival.PendingWarnings, w => w.Contains("(crossing the runway, not clear in ", StringComparison.Ordinal));
    }

    [Fact]
    public void MovingCrossing_CommandedToStop_GoesAround()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        // Rolling at 15 kt but commanded to a stop that no hold or yield names: its present speed says it clears in 7 s, but it
        // is braking to a standstill on the runway.
        AircraftState crossing = MidCrossing(oak.Layout, oak.Runway, oak.Bar);
        crossing.Targets.TargetSpeed = 0;

        Assert.Equal(RunwayUseKind.Crossing, RunwayOccupancy.Classify(crossing, oak.Runway, oak.Layout)?.Kind);
        Assert.True(GoesAroundAtOak(oak.Runway, oak.Layout, crossing, "B738", out AircraftState arrival));
        Assert.Contains(arrival.PendingWarnings, w => w.Contains("(crossing the runway, not clear in ", StringComparison.Ordinal));
    }

    private const double FtPerSecondPerKt = GeoMath.FeetPerNm / 3600.0;

    [Fact]
    public void TaxiSecondsToCover_CommandedStop_NeverClears()
    {
        Assert.Equal(double.PositiveInfinity, OccupiedRunwayGoAround.TaxiSecondsToCover(300.0, 15.0, 0.0, 1.0, 5.0));
        Assert.Equal(
            double.PositiveInfinity,
            OccupiedRunwayGoAround.TaxiSecondsToCover(300.0, 15.0, RunwayOccupancy.StoppedGroundSpeedKts, 1.0, 5.0)
        );
    }

    [Fact]
    public void TaxiSecondsToCover_CommandedBelowPresentSpeed_BrakesToItAndHoldsIt()
    {
        // 15 kt braking at 5 kt/s to 8 kt: 1.4 s over the 11.5 kt mean, then the rest of 300 ft at 8 kt.
        double seconds = OccupiedRunwayGoAround.TaxiSecondsToCover(300.0, 15.0, 8.0, 1.0, 5.0);
        double brakeFt = 11.5 * 1.4 * FtPerSecondPerKt;

        Assert.True(seconds > 300.0 / (15.0 * FtPerSecondPerKt), $"{seconds:F2} s is no slower than holding 15 kt");
        Assert.Equal(1.4 + ((300.0 - brakeFt) / (8.0 * FtPerSecondPerKt)), seconds, 6);
    }

    [Fact]
    public void TaxiSecondsToCover_PathEndsBeforeTheCommandedSpeed_SolvesTheQuadratic()
    {
        // From rest toward 20 kt at 1 kt/s the speed is reached after 169 ft; a 50 ft path ends first: d = a·t²/2.
        Assert.Equal(Math.Sqrt(2.0 * 50.0 / FtPerSecondPerKt), OccupiedRunwayGoAround.TaxiSecondsToCover(50.0, 0.0, 20.0, 1.0, 5.0), 6);

        // Braking from 15 kt toward 8 kt at 5 kt/s takes 27 ft; a 10 ft path ends first, between the two speeds' times.
        double braking = OccupiedRunwayGoAround.TaxiSecondsToCover(10.0, 15.0, 8.0, 1.0, 5.0);
        double v0 = 15.0 * FtPerSecondPerKt;
        double a = -5.0 * FtPerSecondPerKt;
        Assert.Equal((Math.Sqrt((v0 * v0) + (2.0 * a * 10.0)) - v0) / a, braking, 6);
        Assert.InRange(braking, 10.0 / v0, 10.0 / (8.0 * FtPerSecondPerKt));
    }

    [Fact]
    public void HeldCrossing_GoesAround()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        AircraftState crossing = MidCrossing(oak.Layout, oak.Runway, oak.Bar);
        crossing.Ground.Hold = HoldDirective.HoldPosition;

        Assert.True(GoesAroundAtOak(oak.Runway, oak.Layout, crossing, "B738", out AircraftState arrival));
        Assert.Contains(arrival.PendingWarnings, w => w.Contains("(crossing the runway", StringComparison.Ordinal));
    }

    [Fact]
    public void ExitingArrivalMovingUnrestricted_TailInside_NoGoAround()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } oak)
        {
            return;
        }

        AircraftState exiting = RunwayOccupancyTests.SquareToTheMarking(oak.Runway, oak.Bar, "B738", facingRunway: false, 20.0, alongFt: 0);
        exiting.IndicatedAirspeed = 10;
        exiting.Phases = new PhaseList { AssignedRunway = oak.Runway };
        exiting.Phases.Add(Helpers.ExitPhaseFixture.ExitOffTheCentreline(oak.Runway));
        exiting.Phases.CurrentPhase!.Status = PhaseStatus.Active;

        Assert.False(GoesAroundAtOak(oak.Runway, oak.Layout, exiting, "B738", out _));
    }

    [Fact]
    public void EdgeBand_HoldLineBeyondTheBand_IsNotCapped()
    {
        // Real layouts author hold lines well outside their runway safety area: OAK 28L (150 ft wide, band 150) authors 250 ft and
        // OAK 33 (75 ft wide, band 75) authors 175 ft, so the cap (hold line minus 25 ft) never binds. A hold line closer than the
        // band exists in no bundled layout, so the cap branch itself is untested by real data.
        if ((RunwayBarFixture.RunwayBar("OAK", "28L") is not { } large) || (RunwayBarFixture.RunwayBar("OAK", "33", "F") is not { } medium))
        {
            return;
        }

        Assert.Equal(150.0, large.Runway.WidthFt);
        Assert.Equal(OccupiedRunwayGoAround.LargeRunwayEdgeBandFt, OccupiedRunwayGoAround.GoAroundEdgeBandFt(large.Runway, large.Layout));
        Assert.Equal(75.0, medium.Runway.WidthFt);
        Assert.Equal(OccupiedRunwayGoAround.MediumRunwayEdgeBandFt, OccupiedRunwayGoAround.GoAroundEdgeBandFt(medium.Runway, medium.Layout));
    }

    [Fact]
    public void EdgeBand_CapsAtTheStoredStandoff_NotAnOverriddenAuthoredDistance()
    {
        if (RunwayBarFixture.RunwayBar("OAK", "28R") is not { } oak)
        {
            return;
        }

        // OAK 28R authors 225 ft, which the standoff rule overrides with its S (250 + E/100): the bars sit at S, so S is the
        // hold line. Neither caps the 150 ft band, so a copy of the layout moves both inside it, keeping the override: authored
        // 150 ft (cap 125) and stored S 160 ft (cap 135). The band follows S.
        GroundRunway real = oak.Layout.Runways.Single(r => r.Id.Overlaps(oak.Runway.Id));
        Assert.Equal(225.0, real.HoldShortDistanceFt);
        Assert.True(real.HoldShortStandoffFt >= 250.0, $"OAK 28R stored S is {real.HoldShortStandoffFt} ft");

        AirportGroundLayout layout = LayoutCloner.DeepClone(oak.Layout);
        int index = layout.Runways.FindIndex(r => r.Id.Overlaps(oak.Runway.Id));
        layout.Runways[index] = new GroundRunway
        {
            Name = real.Name,
            Coordinates = [.. real.Coordinates],
            WidthFt = real.WidthFt,
            HoldShortDistanceFt = 150.0,
            HoldShortStandoffFt = 160.0,
        };

        Assert.Equal(160.0 - OccupiedRunwayGoAround.EdgeBandHoldLineMarginFt, OccupiedRunwayGoAround.GoAroundEdgeBandFt(oak.Runway, layout));
    }

    [Fact]
    public void EdgeBand_FollowsTheRunwayWidthClass()
    {
        // Real NavData runways either side of each class boundary: MDW 31L is 60 ft (below 75), OAK 33 is 75 ft (75-99), OAK 28L is
        // 150 ft (100 and above).
        RunwayInfo small = Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("MDW", "31L"));
        RunwayInfo medium = Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("OAK", "33"));
        RunwayInfo large = Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("OAK", "28L"));

        Assert.InRange(small.WidthFt, 1.0, 74.9);
        Assert.Equal(75.0, medium.WidthFt);
        Assert.True(large.WidthFt >= 100.0, $"OAK 28L is {large.WidthFt} ft wide");
        Assert.Equal(OccupiedRunwayGoAround.SmallRunwayEdgeBandFt, OccupiedRunwayGoAround.GoAroundEdgeBandFt(small, layout: null));
        Assert.Equal(OccupiedRunwayGoAround.MediumRunwayEdgeBandFt, OccupiedRunwayGoAround.GoAroundEdgeBandFt(medium, layout: null));
        Assert.Equal(OccupiedRunwayGoAround.LargeRunwayEdgeBandFt, OccupiedRunwayGoAround.GoAroundEdgeBandFt(large, layout: null));
    }

    [Fact]
    public void EdgeBand_LayoutOfAnotherAirport_IsNotCapped()
    {
        if ((RunwayBarFixture.RunwayBar("OAK", "28L") is not { } oak) || (new TestAirportGroundData().GetLayout("SFO") is not { } sfo))
        {
            return;
        }

        // A real OAK runway judged with the real SFO layout. SFO has a 28L of its own, but its metadata is not OAK's: the band is
        // OAK 28L's width-class value, never capped by another airport's authored hold line. No bundled layout authors a hold line
        // close enough for the cap to bind, so the cap branch is untested by real data.
        Assert.Equal(150.0, oak.Runway.WidthFt);
        Assert.Equal(OccupiedRunwayGoAround.LargeRunwayEdgeBandFt, OccupiedRunwayGoAround.GoAroundEdgeBandFt(oak.Runway, sfo));
    }

    [Fact]
    public void HeldAfterExitInsideTheHoldingDistance_KeepsTheLandmarkException()
    {
        // Held after its exit at a bar inside the runway's holding distance, a landed C172 is still on the runway (OnSurface), but
        // it landed here: 3-10-3.a.1's 3,000 ft (Category I behind Category I) applies to it, not "no exception at any distance".
        static AircraftState HeldAfterExit(double alongFt) =>
            Occupant(
                "C172",
                OnRunway(alongFt),
                0,
                HoldingAfterExitPhase.FromSnapshot(
                    new HoldingAfterExitPhaseDto
                    {
                        Status = (int)PhaseStatus.Active,
                        ElapsedSeconds = 10,
                        RunwayId = Runway.Designator,
                        StoppedInsideHoldingDistance = true,
                    }
                ),
                onGround: true
            );

        AircraftState beyond = HeldAfterExit(3500);
        AircraftState inside = HeldAfterExit(2000);

        Assert.Equal(RunwayUseKind.OnSurface, RunwayOccupancy.Classify(beyond, Runway, layout: null)?.Kind);
        Assert.False(GoesAround(Arrival("C172", 0.4, 120), beyond, setting: true));
        Assert.True(GoesAround(Arrival("C172", 0.4, 120), inside, setting: true));
    }

    [Fact]
    public void SettingOff_NoGoAround()
    {
        AircraftState arrival = Arrival("B738", 0.6, 150);
        AircraftState occupant = Occupant("B738", OnRunway(300), 0, new LinedUpAndWaitingPhase(), onGround: true);

        Assert.False(GoesAround(arrival, occupant, setting: false));
    }

    [Fact]
    public void ForcedLanding_NoGoAround()
    {
        AircraftState arrival = Arrival("B738", 0.6, 150);
        arrival.Phases!.ForceLanding = true;
        AircraftState occupant = Occupant("B738", OnRunway(300), 0, new LinedUpAndWaitingPhase(), onGround: true);

        Assert.False(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void ArrivalStillOutsideThirtySeconds_NoGoAround()
    {
        AircraftState arrival = Arrival("B738", 2.0, 600);
        AircraftState occupant = Occupant("B738", OnRunway(300), 0, new LinedUpAndWaitingPhase(), onGround: true);

        Assert.False(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void BelowThresholdCrossingHeight_NoGoAround()
    {
        // Under 50 ft AGL the aircraft is landing; there is no balked landing from the flare for traffic.
        AircraftState arrival = Arrival("B738", 0.1, 30);
        AircraftState occupant = Occupant("B738", OnRunway(300), 0, new LinedUpAndWaitingPhase(), onGround: true);

        Assert.False(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void HelicopterArrival_NoGoAround()
    {
        AircraftState arrival = Arrival("EC35", 0.4, 120);
        AircraftState occupant = Occupant("B738", OnRunway(300), 0, new LinedUpAndWaitingPhase(), onGround: true);

        Assert.False(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void LandedCategoryIAheadBeyondLandmark_NoGoAround()
    {
        // §3-10-3.a.1: a Category I arrival may cross the threshold with a landed Category I still
        // rolling 3,000 ft down the runway.
        AircraftState arrival = Arrival("C172", 0.4, 120);
        AircraftState occupant = Occupant("C172", OnRunway(3500), 40, new LandingPhase(), onGround: true);

        Assert.False(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void LandedCategoryIAheadInsideLandmark_ProjectedToThresholdCrossing()
    {
        // §3-10-3 is judged when the arrival crosses the threshold: a rollout still moving at 40 kt
        // 2,000 ft down the runway will be past 3,000 ft by then; one that has stopped there will not.
        AircraftState rolling = Arrival("C172", 0.4, 120);
        AircraftState rollingOccupant = Occupant("C172", OnRunway(2000), 40, new LandingPhase(), onGround: true);
        AircraftState stopped = Arrival("C172", 0.4, 120);
        AircraftState stoppedOccupant = Occupant("C172", OnRunway(2000), 0, new LandingPhase(), onGround: true);

        Assert.False(GoesAround(rolling, rollingOccupant, setting: true));
        Assert.True(GoesAround(stopped, stoppedOccupant, setting: true));
    }

    [Fact]
    public void StopAndGoStoppedBeyondLandmark_GoesAround()
    {
        // 7110.65 3-8-2 makes a stop-and-go a departure from its complete stop; YAAT classifies it at liftoff as a
        // simplification and judges its ground interval by §3-10-3.a.2, so it earns no §3-10-3.a.1 landed-distance credit
        // 3,500 ft down. Stopped before its second roll it does not meet a.2, so it blocks.
        AircraftState arrival = Arrival("C172", 0.4, 120);
        var stopped = StopAndGoPhase.FromSnapshot(
            new StopAndGoPhaseDto
            {
                Status = (int)PhaseStatus.Active,
                ElapsedSeconds = 20,
                FieldElevation = Runway.ElevationFt,
                RunwayHeadingDeg = Runway.TrueHeading.Degrees,
                ThresholdLat = Runway.ThresholdLatitude,
                ThresholdLon = Runway.ThresholdLongitude,
                PauseDuration = 5,
                PauseElapsed = 1,
                Stopped = true,
                Reaccelerating = false,
                Airborne = false,
                GoTriggered = false,
            }
        );
        AircraftState occupant = Occupant("C172", OnRunway(3500), 0, stopped, onGround: true);

        Assert.True(stopped.HasStopped);
        Assert.True(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void BrakingStopAndGo_IsJudgedByTheLandedRule()
    {
        // 7110.65 3-8-2: a stop-and-go is an arrival until it makes a complete stop, so while it is still braking it is a landed
        // aircraft judged by §3-10-3.a.1: a Category I braking 3,500 ft down is past the 3,000 ft landmark for a Category I
        // arrival; one braking 1,000 ft down will not be past it, nor clear of the runway, when the arrival crosses.
        AircraftState beyond = Arrival("C172", 0.4, 120);
        AircraftState beyondOccupant = Occupant("C172", OnRunway(3500), 30, new StopAndGoPhase(), onGround: true);
        AircraftState inside = Arrival("C172", 0.4, 120);
        AircraftState insideOccupant = Occupant("C172", OnRunway(1000), 20, new StopAndGoPhase(), onGround: true);

        Assert.False(GoesAround(beyond, beyondOccupant, setting: true));
        Assert.True(GoesAround(inside, insideOccupant, setting: true));
        Assert.Contains(inside.PendingWarnings, w => w.Contains("OCC1 on 28 (landing rollout, 1,000 ft down", StringComparison.Ordinal));
    }

    /// <summary>A touch-and-go that has lifted off, climbing out over the runway <paramref name="alongFt"/> down it.</summary>
    private static AircraftState AirborneTouchAndGo(string type, double alongFt, double groundSpeedKts)
    {
        AircraftState occupant = Occupant(type, OnRunway(alongFt), groundSpeedKts, LiftedOffOptionPhases.TouchAndGo(Runway), onGround: false);
        occupant.Altitude = ElevationFt + 150;
        occupant.VerticalSpeed = 1500;
        return occupant;
    }

    [Fact]
    public void AirborneTouchAndGoPastTheDepartureDistance_NoGoAround()
    {
        // 7110.65 3-8-2: once airborne a touch-and-go is a departure, so §3-10-3.a.2 protects the arrival behind it —
        // airborne and 6,000 ft down (Category III) when the arrival crosses the threshold. Judged as a landed occupant
        // instead, a Category III pair has no landmark exception and the arrival would go around.
        AircraftState arrival = Arrival("B738", 0.6, 150);
        AircraftState occupant = AirborneTouchAndGo("B738", 5000, 150);

        Assert.False(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void AirborneTouchAndGoShortOfTheDepartureDistance_GoesAroundAsADeparture()
    {
        // Airborne but projected short of 6,000 ft and of the runway end at the threshold crossing: §3-10-3.a.2 is not met.
        AircraftState arrival = Arrival("B738", 0.6, 150);
        AircraftState occupant = AirborneTouchAndGo("B738", 1500, 130);

        Assert.True(GoesAround(arrival, occupant, setting: true));
        Assert.Contains(arrival.PendingWarnings, w => w.Contains("OCC1 on 28 (departing, 1,500 ft down)", StringComparison.Ordinal));
    }

    [Fact]
    public void LowApproachPastTheThresholdShortOfTheDepartureDistance_GoesAroundAsADeparture()
    {
        // A low approach is a departure once it crosses the landing threshold, so §3-10-3.a.2 protects the arrival behind
        // it; projected short of 6,000 ft and of the runway end at the arrival's threshold crossing, it is not met.
        AircraftState arrival = Arrival("B738", 0.6, 150);
        AircraftState occupant = Occupant("B738", OnRunway(1500), 130, new LowApproachPhase(), onGround: false);
        occupant.Altitude = ElevationFt + 150;
        occupant.VerticalSpeed = 1500;

        Assert.True(GoesAround(arrival, occupant, setting: true));
        Assert.Contains(arrival.PendingWarnings, w => w.Contains("OCC1 on 28 (departing, 1,500 ft down)", StringComparison.Ordinal));
    }

    [Fact]
    public void TouchAndGoStillRollingBeyondLandmark_GoesAround()
    {
        // 7110.65 3-8-2 makes a touch-and-go a departure from touchdown; YAAT classifies it at liftoff as a simplification
        // and judges its ground interval by §3-10-3.a.2, so it earns no §3-10-3.a.1 landed credit however far down it is.
        // It will not be flying by the threshold crossing, so a.2 is not met and it blocks as a departure. It re-accelerates
        // from the speed it carries, so it is barely rolling: at 5 kt it is still short of Vr 60 when the arrival crosses.
        AircraftState arrival = Arrival("C172", 0.4, 120);
        AircraftState occupant = Occupant("C172", OnRunway(3500), 5, new TouchAndGoPhase(), onGround: true);

        Assert.False(SameRunwaySeparation.WillBeFlying(occupant, 20.0));
        Assert.True(GoesAround(arrival, occupant, setting: true));
        Assert.Contains(arrival.PendingWarnings, w => w.Contains("OCC1 on 28 (departing, 3,500 ft down)", StringComparison.Ordinal));
    }

    [Fact]
    public void TouchAndGoRollingProjectedAirbornePastTheLandmark_NoGoAround()
    {
        // The ground interval of a touch-and-go is judged by §3-10-3.a.2: rolling 1,000 ft down at 50 kt, it will be flying
        // and past 3,000 ft (Category I pair) when the arrival, about 28 s out, crosses the threshold.
        AircraftState arrival = Arrival("C172", 0.55, 150);
        AircraftState occupant = Occupant("C172", OnRunway(1000), 50, new TouchAndGoPhase(), onGround: true);

        Assert.False(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void TouchAndGoAheadB738_ArrivalThirtySecondsOut_ProjectsAirbornePastTheLandmark_NoGoAround()
    {
        // A touch-and-go re-accelerates from the speed it carries off its rollout dwell, not from a standstill: a B738 just
        // touched down 1,500 ft down SFO 28R slows to about 130 kt over its 10 s dwell and reaches Vr about 5 s later, so it is
        // flying and well past 6,000 ft (Category III pair, §3-10-3.a.2) when the arrival, about 30 s out, crosses the threshold.
        RunwayInfo runway = Sfo28R();
        AircraftState arrival = ArrivalSecondsOut(runway, "B738", 29.5);
        AircraftState occupant = OnRunwayIn(runway, "B738", 1500, 140, TouchAndGoInItsDwell(runway, rolloutSeconds: 10, rolloutElapsed: 0));

        Assert.InRange(RunwayOccupancy.SecondsToLandingThreshold(arrival, runway, null), 25.0, OccupiedRunwayGoAround.DecisionWindowSeconds);
        Assert.False(GoesAroundOn(runway, arrival, occupant));
    }

    /// <summary>
    /// The touch-and-go projection follows the phase as the sim flies it — the rollout dwell's deceleration, then the
    /// re-acceleration ramp from the speed carried off the dwell — so the time to Vr it projects from the touchdown tick and
    /// from a mid-dwell tick is the tick the phase actually lifts off on, to within one tick.
    /// </summary>
    [Theory]
    [InlineData("B738")]
    [InlineData("AT72")]
    [InlineData("C172")]
    public void WillBeFlying_TouchAndGo_AgreesWithTheSimulatedLiftoff(string type)
    {
        AircraftCategory category = AircraftCategorization.Categorize(type);
        int midDwellTick = (int)(CategoryPerformance.TouchAndGoRolloutSeconds(category) / 2.0);
        var touchAndGo = new TouchAndGoPhase();
        AircraftState aircraft = OnRunwayIn(Runway, type, 1000, AircraftPerformance.TouchdownSpeed(type, category), touchAndGo);
        aircraft.IsOnGround = false;
        PhaseContext ctx = new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = category,
            DeltaSeconds = 1.0,
            Runway = Runway,
            FieldElevation = Runway.ElevationFt,
            Logger = NullLogger.Instance,
            ListAircraft = () => [aircraft],
        };
        aircraft.Phases!.Start(ctx);

        double projectedFromTouchdown = SecondsUntilFlying(aircraft);
        double projectedFromMidDwell = double.NaN;
        int tick = 0;
        while (aircraft.IsOnGround && (tick < 200))
        {
            if (tick == midDwellTick)
            {
                projectedFromMidDwell = tick + SecondsUntilFlying(aircraft);
            }

            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            touchAndGo.OnTick(ctx);
            tick++;
        }

        Assert.True(touchAndGo.HasLiftedOff, $"the {type} touch-and-go never lifted off");
        Assert.InRange(projectedFromTouchdown, tick - 1.0, tick + 1.0);
        Assert.InRange(projectedFromMidDwell, tick - 1.0, tick + 1.0);
    }

    /// <summary>The first horizon, to 0.05 s, at which <see cref="SameRunwaySeparation.WillBeFlying"/> says the aircraft is flying.</summary>
    private static double SecondsUntilFlying(AircraftState aircraft)
    {
        for (int step = 0; step <= 3000; step++)
        {
            if (SameRunwaySeparation.WillBeFlying(aircraft, step * 0.05))
            {
                return step * 0.05;
            }
        }

        return double.PositiveInfinity;
    }

    private static RunwayInfo Sfo28R() => Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("SFO", "28R"));

    /// <summary>
    /// A touch-and-go on the ground in its rollout dwell, <paramref name="rolloutElapsed"/> of <paramref name="rolloutSeconds"/> in.
    /// </summary>
    private static TouchAndGoPhase TouchAndGoInItsDwell(RunwayInfo runway, double rolloutSeconds, double rolloutElapsed) =>
        TouchAndGoPhase.FromSnapshot(
            new TouchAndGoPhaseDto
            {
                Status = (int)PhaseStatus.Active,
                ElapsedSeconds = rolloutElapsed,
                FieldElevation = runway.ElevationFt,
                RunwayHeadingDeg = runway.TrueHeading.Degrees,
                ThresholdLat = runway.ThresholdLatitude,
                ThresholdLon = runway.ThresholdLongitude,
                RolloutDuration = rolloutSeconds,
                RolloutElapsed = rolloutElapsed,
                Reaccelerating = false,
                Airborne = false,
                RollElapsedSeconds = 0,
            }
        );

    /// <summary>
    /// An aircraft on the ground <paramref name="alongFt"/> down <paramref name="runway"/> at <paramref name="speedKts"/>,
    /// flying <paramref name="phase"/>.
    /// </summary>
    private static AircraftState OnRunwayIn(RunwayInfo runway, string type, double alongFt, double speedKts, Phase phase)
    {
        var occupant = new AircraftState
        {
            Callsign = "OCC1",
            AircraftType = type,
            Position = GeoMath.ProjectPoint(
                new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
                runway.TrueHeading,
                alongFt / GeoMath.FeetPerNm
            ),
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = speedKts,
            IsOnGround = true,
            Phases = new PhaseList { AssignedRunway = runway },
        };
        occupant.Phases.Add(phase);
        occupant.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        return occupant;
    }

    /// <summary>
    /// An arrival on <paramref name="runway"/>'s final that reaches its threshold in <paramref name="seconds"/> at its own
    /// ground speed.
    /// </summary>
    private static AircraftState ArrivalSecondsOut(RunwayInfo runway, string type, double seconds)
    {
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var arrival = new AircraftState
        {
            Callsign = "ARR1",
            AircraftType = type,
            Position = GeoMath.ProjectPoint(threshold, runway.TrueHeading.ToReciprocal(), 1.0),
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = runway.ElevationFt + 320,
            IndicatedAirspeed = 130,
            VerticalSpeed = -700,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "KSFO", Destination = "KSFO" },
            Phases = new PhaseList { AssignedRunway = runway },
        };
        double distanceNm = arrival.GroundSpeed * seconds / 3600.0;
        arrival.Position = GeoMath.ProjectPoint(threshold, runway.TrueHeading.ToReciprocal(), distanceNm);
        arrival.Altitude = runway.ElevationFt + (distanceNm * 318);
        arrival.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        return arrival;
    }

    private static bool GoesAroundOn(RunwayInfo runway, AircraftState arrival, AircraftState occupant)
    {
        PhaseContext ctx = new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = arrival,
            Targets = arrival.Targets,
            Category = AircraftCategorization.Categorize(arrival.AircraftType),
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            Logger = NullLogger.Instance,
            AutoClearedToLand = true,
            AutoGoAroundOnOccupiedRunway = true,
            ListAircraft = () => [arrival, occupant],
        };
        arrival.Phases!.Start(ctx);
        arrival.Phases.CurrentPhase!.OnTick(ctx);
        return arrival.Phases.CurrentPhase is GoAroundPhase;
    }

    [Fact]
    public void StopAndGoOnItsSecondRollProjectedAirbornePastTheLandmark_NoGoAround()
    {
        // A stop-and-go on its second roll is judged by §3-10-3.a.2 the same way as a rolling touch-and-go.
        AircraftState arrival = Arrival("C172", 0.55, 150);
        var secondRoll = StopAndGoPhase.FromSnapshot(
            new StopAndGoPhaseDto
            {
                Status = (int)PhaseStatus.Active,
                ElapsedSeconds = 30,
                FieldElevation = Runway.ElevationFt,
                RunwayHeadingDeg = Runway.TrueHeading.Degrees,
                ThresholdLat = Runway.ThresholdLatitude,
                ThresholdLon = Runway.ThresholdLongitude,
                PauseDuration = 5,
                PauseElapsed = 5,
                Stopped = true,
                Reaccelerating = true,
                Airborne = false,
                GoTriggered = true,
                RollElapsedSeconds = 10,
            }
        );
        AircraftState occupant = Occupant("C172", OnRunway(1000), 50, secondRoll, onGround: true);

        Assert.True(secondRoll.IsTakeoffRoll);
        Assert.False(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void AirborneLandingFloatingPastTheLandmark_NoLandedCredit_GoesAround()
    {
        // §3-10-3.a.1 credits an aircraft that has landed: one still floating in its landing phase 3,500 ft down has not, so
        // the Category I landmark does not apply to it.
        AircraftState arrival = Arrival("C172", 0.4, 120);
        AircraftState occupant = Occupant("C172", OnRunway(3500), 60, new LandingPhase(), onGround: false);
        occupant.Altitude = ElevationFt + 20;

        Assert.True(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void ExitingOffTheCenterlineInsidePavement_UsesTheLandingLandmark()
    {
        // A light twin still inside the pavement rectangle while turning off at 5,000 ft: a.1 (4,500 ft) is met.
        AircraftState arrival = Arrival("C172", 0.4, 120);
        AircraftState occupant = Occupant("BE58", OnRunway(5000), 15, new RunwayExitPhase(), onGround: true);
        occupant.TrueHeading = new TrueHeading(250);
        occupant.TrueTrack = new TrueHeading(250);

        Assert.False(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void CategoryIIIArrivalBehindLandedAircraft_GoesAround()
    {
        // No landmark exception when either aircraft is Category III: the runway must be clear.
        AircraftState arrival = Arrival("B738", 0.6, 150);
        AircraftState occupant = Occupant("B738", OnRunway(5000), 60, new LandingPhase(), onGround: true);

        Assert.True(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void LandedCategoryIIIWithNoResolvedExit_FailsClosedAndGoesAround()
    {
        // §3-10-3.a.1 is judged at the threshold crossing, so the occupant is asked whether it will be CLEAR of the
        // runway by then — but a rollout with no exit resolved (no ground layout, or the planner has not committed)
        // is an unknown, and an unknown must read as "not clear". Category III either side means no landmark
        // exception, so the arrival still goes around.
        AircraftState arrival = Arrival("B738", 0.6, 150);
        AircraftState occupant = Occupant("B738", OnRunway(5000), 90, new LandingPhase(), onGround: true);

        Assert.True(GoesAround(arrival, occupant, setting: true));
    }

    /// <summary>
    /// A <see cref="LandingPhase"/> carrying a resolved candidate exit — the only shape
    /// <c>SameRunwayArrivalProtection.TryBuildRollout</c> can predict a vacate from, and the one every other test here
    /// lacks because none of them builds a ground layout. Restored through the phase's own snapshot path rather than
    /// reaching into its state: a two-node layout (branch point on the centerline, hold-short node
    /// <paramref name="holdShortOffsetFt"/> off to the side) and those node ids in the DTO.
    /// </summary>
    private static LandingPhase RollingOutToExit(double branchAlongFt, double holdShortOffsetFt, double turnOffSpeedKts)
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        var branch = new GroundNode
        {
            Id = 1,
            Position = OnRunway(branchAlongFt),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var holdShort = new GroundNode
        {
            Id = 2,
            Position = GeoMath.ProjectPoint(branch.Position, Runway.TrueHeading + 90, holdShortOffsetFt / GeoMath.FeetPerNm),
            Type = GroundNodeType.RunwayHoldShort,
        };
        layout.Nodes[branch.Id] = branch;
        layout.Nodes[holdShort.Id] = holdShort;

        var dto = new LandingPhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 20.0,
            FieldElevation = ElevationFt,
            RunwayHeadingDeg = Runway.TrueHeading.Degrees,
            ThresholdLat = Threshold.Lat,
            ThresholdLon = Threshold.Lon,
            TouchedDown = true,
            CanGoAround = false,
            LahsoHoldShortDistNm = 0,
            HasLahso = false,
            StoppedForLahso = false,
            CurrentStateValue = (int)LandingPhase.State.Rollout,
            CandidateExitBranchPointId = branch.Id,
            CandidateExitHoldShortId = holdShort.Id,
            CandidateExitTaxiway = "A",
            CandidateExitTurnOffSpeed = turnOffSpeedKts,
            CandidateExitPathNodeIds = [],
        };

        return LandingPhase.FromSnapshot(dto, layout);
    }

    [Fact]
    public void LandedCategoryIIIProjectedClearOfTheRunwayInTime_NoGoAround()
    {
        // §3-10-6.a: separation may be anticipated when the positions say it will exist at the threshold crossing.
        // The arrival is 16.5 s out. The occupant is 74 kt at 5,000 ft with its exit 300 ft ahead: braking to the
        // 20 kt turn-off it covers that at the 47 kt mean (3.8 s), then 200 ft of exit plus its tail across the bar
        // at 20 kt (7.8 s) — clear in ~12 s, inside the crossing. Category III either side, so nothing but the
        // vacate prediction can save it.
        AircraftState arrival = Arrival("B738", 0.6, 150);
        AircraftState occupant = Occupant(
            "B738",
            OnRunway(5000),
            74,
            RollingOutToExit(branchAlongFt: 5300, holdShortOffsetFt: 200, turnOffSpeedKts: 20),
            onGround: true
        );

        Assert.False(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void LandedCategoryIIIStillOnTheRunwayAtTheCrossing_GoesAround()
    {
        // Same occupant with its exit 1,000 ft ahead instead of 300: 12.6 s of rollout at the 47 kt braking mean plus
        // 7.8 s of exit and tail clearance is ~20 s, past the arrival's 16.5 s crossing. Flying that rollout at the
        // leader's present 74 kt (8.0 s) or stopping "clear" at the hold-short bar instead of a fuselage past it
        // (5.9 s) both report it clear in time — this arm is what catches either regressing.
        AircraftState arrival = Arrival("B738", 0.6, 150);
        AircraftState occupant = Occupant(
            "B738",
            OnRunway(5000),
            74,
            RollingOutToExit(branchAlongFt: 6000, holdShortOffsetFt: 200, turnOffSpeedKts: 20),
            onGround: true
        );

        Assert.True(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void GoAround_TerminalEntryNamesTheBlockerAndWhy()
    {
        // The pilot's transmission stays generic (a pilot does not read out the blocker's callsign); the instructor
        // gets a separate terminal line saying which aircraft it was and what it was doing.
        AircraftState arrival = Arrival("B738", 0.6, 150);
        AircraftState occupant = Occupant("B738", OnRunway(5000), 60, new LandingPhase(), onGround: true);

        Assert.True(GoesAround(arrival, occupant, setting: true));
        Assert.Contains(
            arrival.PendingWarnings,
            w => w.Contains("ARR1 go-around: OCC1 on 28 (landing rollout, 5,000 ft down", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            arrival.PendingWarnings,
            w => w.Contains("OCC1", StringComparison.Ordinal) && w.Contains("going around, traffic", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void CrossingOccupant_TerminalEntrySaysCrossing()
    {
        AircraftState arrival = Arrival("B738", 0.6, 150);
        AircraftState occupant = Occupant("B738", OnRunway(6000), 15, new TaxiingPhase(), onGround: true);
        occupant.TrueHeading = new TrueHeading(10);
        occupant.TrueTrack = new TrueHeading(10);

        Assert.True(GoesAround(arrival, occupant, setting: true));
        Assert.Contains(arrival.PendingWarnings, w => w.Contains("crossing the runway", StringComparison.Ordinal));
    }

    [Fact]
    public void StoppedOccupantBeyondLandmark_GoesAround()
    {
        AircraftState arrival = Arrival("C172", 0.4, 120);
        AircraftState occupant = Occupant("C172", OnRunway(4000), 0, new HoldingInPositionPhase(), onGround: true);

        Assert.True(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void AirborneDeparture_LandmarkIs6000FtForCategoryIII()
    {
        // §3-10-3.a.2: an airborne departure need not have crossed the runway end when it is 6,000 ft
        // (either aircraft Category III) from the landing threshold — projected to the arrival's crossing.
        AircraftState near = Arrival("B738", 0.6, 150);
        AircraftState nearOccupant = Occupant("B738", OnRunway(1000), 150, new TakeoffPhase(), onGround: false);
        nearOccupant.Altitude = ElevationFt + 100;
        AircraftState far = Arrival("B738", 0.6, 150);
        AircraftState farOccupant = Occupant("B738", OnRunway(4000), 150, new TakeoffPhase(), onGround: false);
        farOccupant.Altitude = ElevationFt + 300;

        Assert.True(GoesAround(near, nearOccupant, setting: true));
        Assert.False(GoesAround(far, farOccupant, setting: true));
    }

    [Fact]
    public void RollingDeparture_ProjectedPastTheLandmark_NoGoAround()
    {
        AircraftState arrival = Arrival("B738", 0.6, 150);
        AircraftState occupant = Occupant("B738", OnRunway(5000), 100, new TakeoffPhase(), onGround: true);

        Assert.False(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void RejectedTakeoff_DeceleratingOnTheRunway_GoesAround()
    {
        // A live departure that aborted at 80 kt is still doing 60 kt 500 ft down the runway. Projected at constant
        // speed it would sit past the 3,000 ft landmark when the arrival crosses — but it is stopping, not flying.
        AircraftState arrival = Arrival("B738", 1.0, 250);
        AircraftState? rto = null;
        foreach ((double t, double gs) in new[] { (0.0, 80.0), (1.0, 75.0), (2.0, 70.0), (3.0, 65.0), (4.0, 60.0) })
        {
            LatLon pos = OnRunway(500);
            var sample = new LiveTrafficSample(t, pos.Lat, pos.Lon, ElevationFt, gs, Runway.TrueHeading.Degrees, 0, LiveTrafficSource.Asdex, 4521);
            if (rto is null)
            {
                rto = LiveTrafficKinematics.CreateShadow("OCC1", "B738", sample, new AircraftFlightPlan { HasFlightPlan = true });
            }
            else
            {
                LiveTrafficKinematics.Apply(rto, sample);
            }
        }

        Assert.True(GoesAround(arrival, rto!, setting: true));
    }

    [Fact]
    public void DepartureLinedUpAndStopped_GoesAround()
    {
        // Neither landed nor departed: no exception at any distance.
        AircraftState arrival = Arrival("B738", 0.6, 150);
        AircraftState occupant = Occupant("B738", OnRunway(4000), 0, new TakeoffPhase(), onGround: true);

        Assert.True(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void CrossingOccupant_GoesAround()
    {
        AircraftState arrival = Arrival("B738", 0.6, 150);
        AircraftState occupant = Occupant("B738", OnRunway(6000), 15, new TaxiingPhase(), onGround: true);
        occupant.TrueHeading = new TrueHeading(10);
        occupant.TrueTrack = new TrueHeading(10);

        Assert.True(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void OccupantOffThePavement_NoGoAround()
    {
        LatLon beside = GeoMath.ProjectPoint(OnRunway(2000), Runway.TrueHeading + 90, 400 / GeoMath.FeetPerNm);
        AircraftState arrival = Arrival("B738", 0.6, 150);
        AircraftState occupant = Occupant("B738", beside, 15, new TaxiingPhase(), onGround: true);

        Assert.False(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void HelicopterHoveringDownOntoTheRunwayBeyondTheLandmark_GoesAround()
    {
        // A preceding rotorcraft has no §3-10-3 exception: it has not landed (a.1) and never rolled (a.2), and the
        // landmark categories are fixed-wing classes — 4,000 ft down the runway it still blocks.
        AircraftState arrival = Arrival("C172", 0.4, 120);
        AircraftState hovering = Occupant("EC35", OnRunway(4000), 5, phase: null, onGround: false);
        hovering.Altitude = ElevationFt + 40;
        hovering.VerticalSpeed = -200;

        Assert.True(GoesAround(arrival, hovering, setting: true));
    }

    [Fact]
    public void PhaselessOccupantParkedOnThePavement_GoesAround()
    {
        AircraftState arrival = Arrival("B738", 0.6, 150);
        AircraftState occupant = Occupant("B738", OnRunway(1500), 0, phase: null, onGround: true);

        Assert.True(GoesAround(arrival, occupant, setting: true));
    }
}
