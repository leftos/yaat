using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.LiveTraffic;

/// <summary>
/// <c>ASSUME</c> converts a shadow in place into a controllable aircraft. It is never refused; the
/// seeded state depends on the situation (feed clearance fields first, then level/climb/descent
/// inference, established-on-final, route rejoin, hold, VFR, runway/surface kinds).
/// </summary>
public class LiveTrafficAssumeTests
{
    private const string Callsign = "UAL123";
    private static readonly LatLon EnRoute = new(37.9, -121.3);

    public LiveTrafficAssumeTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static LiveTrafficSample Sample(
        double at,
        LatLon pos,
        double altFt,
        double gs,
        double track,
        double? vs,
        LiveTrafficSource source = LiveTrafficSource.Stars
    ) => new(at, pos.Lat, pos.Lon, altFt, gs, track, vs, source, 4521);

    private static AircraftState Shadow(LiveTrafficSample sample, AircraftFlightPlan? plan = null) =>
        LiveTrafficKinematics.CreateShadow(
            Callsign,
            "B738",
            sample,
            plan
                ?? new AircraftFlightPlan
                {
                    HasFlightPlan = true,
                    Departure = "KSFO",
                    Destination = "KSMF",
                }
        );

    private static DispatchContext Ctx(AircraftState ac) =>
        TestDispatch.Context(new Random(1), findAircraft: cs => cs == Callsign ? ac : null, listAircraft: () => [ac]);

    private static CommandResult Assume(AircraftState ac) => CommandDispatcher.Dispatch(new AssumeCommand(), ac, Ctx(ac));

    private static CommandResult Send(AircraftState ac, string input)
    {
        var parsed = CommandParser.ParseCompound(input);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        return CommandDispatcher.DispatchCompound(parsed.Value!, ac, Ctx(ac));
    }

    [Fact]
    public void Assume_ClearsShadowState_AndCommandsWorkAfterwards()
    {
        var ac = Shadow(Sample(0, EnRoute, 11_000, 300, 90, 0));
        Assert.False(Send(ac, "H 180").Success);

        var result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.False(ac.IsShadow);
        Assert.Contains("assumed", result.Message, StringComparison.Ordinal);
        Assert.Equal(4521u, ac.Transponder.Code);
        Assert.True(Send(ac, "H 180").Success);
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void Assume_OnNonShadow_IsRejected()
    {
        var ac = Shadow(Sample(0, EnRoute, 11_000, 300, 90, 0));
        Assume(ac);

        var again = Assume(ac);

        Assert.False(again.Success);
        Assert.Contains("not live traffic", again.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssumeInsideACompound_IsRejectedLikeAnyOtherCommand()
    {
        var ac = Shadow(Sample(0, EnRoute, 11_000, 300, 90, 0));

        var result = Send(ac, "ASSUME ; H 180");

        Assert.False(result.Success);
        Assert.True(ac.IsShadow);
    }

    [Fact]
    public void LevelAircraft_KeepsItsAltitudeToTheHundred_NotAHemisphericSnap()
    {
        var ac = Shadow(Sample(0, EnRoute, 11_000, 300, 270, 0));
        LiveTrafficKinematics.Apply(ac, Sample(5, EnRoute, 11_100, 300, 270, null));
        LiveTrafficKinematics.Apply(ac, Sample(10, EnRoute, 11_000, 300, 270, null));

        Assume(ac);

        Assert.Equal(11_000, ac.Targets.TargetAltitude);
        Assert.Equal(11_000, ac.Targets.AssignedAltitude);
        Assert.InRange(ac.Targets.TargetSpeed!.Value, 200, 340);
    }

    [Fact]
    public void Descending_TargetsTheFeedsAssignedAltitude_AndKeepsTheRate()
    {
        var ac = Shadow(Sample(0, EnRoute, 12_000, 280, 90, -1_800) with { AssignedAltitudeFt = 6_000 });

        var result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(6_000, ac.Targets.TargetAltitude);
        Assert.Equal(1_800, ac.Targets.DesiredVerticalRate);
        Assert.Contains("descending to 6000", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Descending_InterimAltitudeBeatsAssigned()
    {
        var ac = Shadow(Sample(0, EnRoute, 12_000, 280, 90, -1_500) with { AssignedAltitudeFt = 4_000, InterimAltitudeFt = 8_000 });

        Assume(ac);

        Assert.Equal(8_000, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void Descending_WithoutAClearance_HoldsTheDescentToAFloor_NotTheNextThousand()
    {
        var ac = Shadow(Sample(0, EnRoute, 12_000, 280, 90, -2_000));

        Assume(ac);

        var target = ac.Targets.TargetAltitude!.Value;
        Assert.True(target < 12_000, $"target {target} should be below the aircraft");
        Assert.True(target <= 5_000, $"floor {target} should be near the destination, not the next hemispheric altitude");
        Assert.Equal(2_000, ac.Targets.DesiredVerticalRate);
    }

    [Fact]
    public void Climbing_TargetsTheFiledAltitude_WhenTheFeedHasNone()
    {
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KSFO",
            Destination = "KSMF",
            Altitude = new PlannedAltitude(17_000, null, false, false, false),
        };
        var ac = Shadow(Sample(0, EnRoute, 9_000, 280, 90, 2_200), plan);

        Assume(ac);

        Assert.Equal(17_000, ac.Targets.TargetAltitude);
        Assert.Equal(2_200, ac.Targets.DesiredVerticalRate);
    }

    [Fact]
    public void HighAltitude_SeedsMachFromTheAirVector()
    {
        var ac = Shadow(Sample(0, EnRoute, 35_000, 450, 90, 0, LiveTrafficSource.Eram));

        Assume(ac);

        Assert.NotNull(ac.Targets.TargetMach);
        Assert.InRange(ac.Targets.TargetMach!.Value, 0.70, 0.86);
    }

    [Fact]
    public void ClearedHeadingFromTheFeed_SeedsTheHeadingHold()
    {
        var ac = Shadow(Sample(0, EnRoute, 11_000, 300, 90, 0) with { ClearedHeadingDeg = 120 }, new AircraftFlightPlan { HasFlightPlan = true });

        var result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(ac.Targets.TargetTrueHeading);
        Assert.InRange(ac.Targets.TargetTrueHeading!.Value.ToMagnetic(ac.Declination).Degrees, 119.5, 120.5);
    }

    [Fact]
    public void NoRoute_HoldsThePresentHeading_WithAVectorsNote()
    {
        var ac = Shadow(Sample(0, EnRoute, 11_000, 300, 90, 0), new AircraftFlightPlan { HasFlightPlan = true });

        var result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.InRange(ac.Targets.TargetTrueHeading!.Value.Degrees, 89, 91);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("vectors", StringComparison.Ordinal));
    }

    [Fact]
    public void FiledRoute_RejoinsAtTheNextFixAhead_NotOneBehind()
    {
        var navDb = NavigationDatabase.Instance;
        var oak = navDb.ResolveFixOrFrd("OAK");
        var sac = navDb.ResolveFixOrFrd("SAC");
        var rbl = navDb.ResolveFixOrFrd("RBL");
        Assert.NotNull(oak);
        Assert.NotNull(sac);
        Assert.NotNull(rbl);

        var a = new LatLon(oak.Value.Lat, oak.Value.Lon);
        var b = new LatLon(sac.Value.Lat, sac.Value.Lon);
        double legNm = GeoMath.DistanceNm(a, b);
        var bearing = new TrueHeading(GeoMath.BearingTo(a, b));
        var pos = GeoMath.ProjectPoint(a, bearing, legNm * 0.4);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KRDD",
            Route = "OAK SAC RBL",
        };
        var ac = Shadow(Sample(0, pos, 11_000, 300, bearing.Degrees, 0), plan);

        var result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["SAC", "RBL"], ac.Targets.NavigationRoute.Select(t => t.Name).ToList());
        Assert.Contains("direct SAC", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NextFixAhead_SkipsAFixAbeam_ButNeverTwo()
    {
        var a = new LatLon(37.0, -122.0);
        var b = GeoMath.ProjectPoint(a, new TrueHeading(90), 10);
        var c = GeoMath.ProjectPoint(a, new TrueHeading(90), 20);
        var d = GeoMath.ProjectPoint(a, new TrueHeading(90), 30);
        List<ResolvedFix> route = [new("A", a.Lat, a.Lon), new("B", b.Lat, b.Lon), new("C", c.Lat, c.Lon), new("D", d.Lat, d.Lon)];
        var abeamB = GeoMath.ProjectPoint(b, new TrueHeading(0), 0.6);
        var ac = new AircraftState
        {
            Callsign = "N1",
            AircraftType = "B738",
            Position = abeamB,
            TrueTrack = new TrueHeading(90),
            TrueHeading = new TrueHeading(90),
            IndicatedAirspeed = 250,
            Altitude = 10_000,
        };

        // Abeam B (bearing 180 = behind) → skip once → C.
        Assert.Equal(2, LiveTrafficAssumer.NextFixAhead(ac, route));

        // Past the last fix of a two-fix route: nothing ahead, never turn back.
        ac.Position = GeoMath.ProjectPoint(b, new TrueHeading(90), 1);
        Assert.Equal(-1, LiveTrafficAssumer.NextFixAhead(ac, route.Take(2).ToList()));
    }

    [Fact]
    public void FiledRoute_OutsideTheRejoinCone_FallsBackToVectors()
    {
        var navDb = NavigationDatabase.Instance;
        var oak = navDb.ResolveFixOrFrd("OAK")!.Value;
        var sac = navDb.ResolveFixOrFrd("SAC")!.Value;
        var a = new LatLon(oak.Lat, oak.Lon);
        var b = new LatLon(sac.Lat, sac.Lon);
        var bearing = new TrueHeading(GeoMath.BearingTo(a, b));
        var pos = GeoMath.ProjectPoint(a, bearing, 20);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KRDD",
            Route = "OAK SAC RBL",
        };
        // Tracking 90° off the leg: no fix inside the ±45° cone.
        var ac = Shadow(Sample(0, pos, 11_000, 300, bearing.Degrees + 90, 0), plan);

        Assume(ac);

        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.NotNull(ac.Targets.TargetTrueHeading);
    }

    [Fact]
    public void VfrShadow_MaintainsVfr_NoRouteNoSnap()
    {
        var plan = new AircraftFlightPlan { HasFlightPlan = false, FlightRules = "VFR" };
        var ac = Shadow(Sample(0, EnRoute, 4_500, 110, 90, 0) with { BeaconCode = 1200 }, plan);

        var result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Contains("VFR", result.Message, StringComparison.Ordinal);
        Assert.Equal(4_500, ac.Targets.TargetAltitude);
        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.NotNull(ac.Targets.TargetTrueHeading);
    }

    [Fact]
    public void AirborneHoldFromTheFeed_HoldsHeadingAndAltitude_AndNamesTheFix()
    {
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KRDD",
            Route = "OAK SAC RBL",
        };
        var sac = NavigationDatabase.Instance.ResolveFixOrFrd("SAC")!.Value;
        var ac = Shadow(Sample(0, new LatLon(sac.Lat + 0.05, sac.Lon), 8_000, 210, 180, 0) with { AirborneHold = true, HoldFix = "SAC" }, plan);

        var result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.Equal(8_000, ac.Targets.TargetAltitude);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("hold at SAC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HoldInClearanceText_HoldsHeadingAndAltitude_NeverRejoins()
    {
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KRDD",
            Route = "OAK SAC RBL",
        };
        var navDb = NavigationDatabase.Instance;
        var sac = navDb.ResolveFixOrFrd("SAC")!.Value;
        var ac = Shadow(
            Sample(0, new LatLon(sac.Lat + 0.05, sac.Lon), 8_000, 210, 180, 0) with
            {
                ClearanceText = "HOLD SAC AS PUBLISHED EFC 1230",
            },
            plan
        );

        var result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.Equal(8_000, ac.Targets.TargetAltitude);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("hold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RacetrackSignature_DetectsAHold()
    {
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KRDD",
            Route = "OAK SAC RBL",
        };
        var navDb = NavigationDatabase.Instance;
        var sac = navDb.ResolveFixOrFrd("SAC")!.Value;
        var center = new LatLon(sac.Lat, sac.Lon);
        var ac = Shadow(Sample(0, center, 8_000, 210, 0, 0), plan);
        // Standard-rate turn (3°/s, AIM 5-3-8) for 72 s within a mile of the fix.
        for (int i = 1; i <= 16; i++)
        {
            double track = (i * 13.5) % 360;
            var pos = GeoMath.ProjectPoint(center, new TrueHeading(track + 90), 0.8);
            LiveTrafficKinematics.Apply(ac, Sample(i * 4.5, pos, 8_000, 210, track, null));
        }

        Assume(ac);

        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("hold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EstablishedOnFinal_ContinuesTheApproach_AndGoAroundWorks()
    {
        var runway = NavigationDatabase.Instance.GetRunway("OAK", "28R");
        Assert.NotNull(runway);
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var pos = GeoMath.ProjectPoint(threshold, runway.TrueHeading.ToReciprocal(), 3.0);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KLAX",
            Destination = "KOAK",
        };
        var ac = Shadow(Sample(0, pos, runway.ElevationFt + 950, 140, runway.TrueHeading.Degrees, -700), plan);

        var result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(ac.Phases);
        Assert.NotNull(ac.Phases.ActiveApproach);
        Assert.Equal("28R", RunwayIdentifier.ToDisplayDesignator(ac.Phases.AssignedRunway!.Designator));
        Assert.Null(ac.Phases.LandingClearance);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("no landing clearance", StringComparison.Ordinal));

        var ga = Send(ac, "GA");
        Assert.True(ga.Success, ga.Message);
    }

    [Fact]
    public void AlignedButOutsideTheGate_IsOnVectorsTowardTheRunway()
    {
        var runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var pos = GeoMath.ProjectPoint(threshold, runway.TrueHeading.ToReciprocal(), 9.0);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KLAX",
            Destination = "KOAK",
        };
        var ac = Shadow(Sample(0, pos, 3_000, 180, runway.TrueHeading.Degrees, -500), plan);

        var result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Phases?.ActiveApproach);
        Assert.NotNull(ac.Targets.TargetTrueHeading);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("vectors to the runway 28R", StringComparison.Ordinal));
    }

    [Fact]
    public void HelicopterDescendingOntoTheRunway_LandsUnderHelicopterLandingPhase()
    {
        var runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var pos = GeoMath.ProjectPoint(threshold, runway.TrueHeading, 0.2);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KLAX",
            Destination = "KOAK",
        };
        var ac = LiveTrafficKinematics.CreateShadow(
            Callsign,
            "EC35",
            Sample(0, pos, runway.ElevationFt + 40, 15, runway.TrueHeading.Degrees, -200),
            plan
        );

        var result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.IsType<HelicopterLandingPhase>(ac.Phases?.CurrentPhase);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases!.LandingClearance);
    }

    [Fact]
    public void AirborneOverTheRunwayBelow50Ft_LandsUnderLandingPhase_ClearanceImplied()
    {
        var runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var pos = GeoMath.ProjectPoint(threshold, runway.TrueHeading, 0.2);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KLAX",
            Destination = "KOAK",
        };
        var ac = Shadow(Sample(0, pos, runway.ElevationFt + 30, 130, runway.TrueHeading.Degrees, -300), plan);

        var result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.IsType<LandingPhase>(ac.Phases?.CurrentPhase);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases!.LandingClearance);
    }

    [Fact]
    public void RollingDeparture_KeepsRollingUnderTakeoffPhase()
    {
        var runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var pos = GeoMath.ProjectPoint(threshold, runway.TrueHeading, 0.3);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KLAX",
        };
        var ac = Shadow(Sample(0, pos, runway.ElevationFt, 80, runway.TrueHeading.Degrees, 0, LiveTrafficSource.Asdex), plan);

        var result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.IsType<TakeoffPhase>(ac.Phases?.CurrentPhase);
        Assert.Equal(runway.Designator, ac.Procedure.DepartureRunway);
    }

    [Fact]
    public void RolloutAbove30Kt_ExitsUnderRunwayExitPhase()
    {
        var runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var pos = GeoMath.ProjectPoint(threshold, runway.TrueHeading, 0.7);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KLAX",
            Destination = "KOAK",
        };
        var ac = Shadow(Sample(0, pos, runway.ElevationFt, 20, runway.TrueHeading.Degrees, 0, LiveTrafficSource.Asdex), plan);
        LiveTrafficKinematics.Apply(ac, Sample(1, pos, runway.ElevationFt, 34, runway.TrueHeading.Degrees, 0, LiveTrafficSource.Asdex));

        Assume(ac);

        Assert.IsType<RunwayExitPhase>(ac.Phases?.CurrentPhase);
    }

    [Fact]
    public void SlowSurfaceTarget_BecomesAPhaselessGroundAircraft()
    {
        var runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var pos = GeoMath.ProjectPoint(threshold, runway.TrueHeading, 0.7);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KLAX",
            Destination = "KOAK",
        };
        var ac = Shadow(Sample(0, pos, runway.ElevationFt, 12, runway.TrueHeading.Degrees, 0, LiveTrafficSource.Asdex), plan);

        var result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.True(ac.IsOnGround);
        Assert.Null(ac.Phases);
        Assert.Equal(12, ac.Targets.TargetSpeed);
        Assert.False(ac.IsShadow);
    }

    [Fact]
    public void Descending_NothingToDescendTo_LevelsOffWithANote()
    {
        // Mid-Pacific: no MVA coverage, no resolvable destination.
        var ac = Shadow(
            Sample(0, new LatLon(30.0, -150.0), 12_000, 280, 90, -2_000),
            new AircraftFlightPlan { HasFlightPlan = true, Destination = "ZZZZ" }
        );

        Assume(ac);

        Assert.Equal(12_000, ac.Targets.TargetAltitude);
        Assert.Null(ac.Targets.DesiredVerticalRate);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("levelled off", StringComparison.Ordinal));
    }

    [Fact]
    public void Descending_JustAboveAHundred_NeverTargetsAboveItself()
    {
        var ac = Shadow(Sample(0, EnRoute, 956, 120, 90, -500) with { AssignedAltitudeFt = 3_000 });

        Assume(ac);

        Assert.True(ac.Targets.TargetAltitude <= 956, $"target {ac.Targets.TargetAltitude} above the aircraft");
    }

    [Fact]
    public void SlowArrivalBelow10k_IsNotSpedBackUp()
    {
        var ac = Shadow(Sample(0, EnRoute, 2_000, 150, 90, -700));

        Assume(ac);

        Assert.InRange(ac.Targets.TargetSpeed!.Value, 120, 160);
    }

    [Fact]
    public void ClimbingVfr_KeepsClimbingToTheNextVfrCruisingAltitude()
    {
        var plan = new AircraftFlightPlan { HasFlightPlan = false, FlightRules = "VFR" };
        var ac = Shadow(Sample(0, EnRoute, 4_200, 110, 90, 700) with { BeaconCode = 1200 }, plan);

        Assume(ac);

        Assert.Equal(700, ac.Targets.DesiredVerticalRate);
        Assert.Equal(5_500, ac.Targets.TargetAltitude);
    }

    [Theory]
    [InlineData(4_200, 90, true, 5_500)]
    [InlineData(4_200, 270, true, 4_500)]
    [InlineData(6_600, 90, false, 5_500)]
    [InlineData(6_600, 270, false, 6_500)]
    public void NextVfrCruisingAltitude_FollowsTheHemisphericRule(double alt, double course, bool up, double expected)
    {
        Assert.Equal(expected, LiveTrafficAssumer.NextVfrCruisingAltitude(alt, course, up));
    }

    [Fact]
    public void InitialClimb_HoldsRunwayHeading_NotDirectToTheFirstFix()
    {
        var runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        var der = new LatLon(runway.EndLatitude, runway.EndLongitude);
        var pos = GeoMath.ProjectPoint(der, runway.TrueHeading, 1.5);
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KRDD",
            Route = "SAC RBL",
        };
        var ac = Shadow(Sample(0, pos, runway.ElevationFt + 900, 160, runway.TrueHeading.Degrees, 2_500), plan);

        var result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Contains("initial climb runway 28R", result.Message, StringComparison.Ordinal);
        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.InRange(Math.Abs(ac.Targets.TargetTrueHeading!.Value - runway.TrueHeading), 0, 0.5);
    }

    [Fact]
    public void EmergencySquawk_IsPreservedAndNoted()
    {
        var ac = Shadow(Sample(0, EnRoute, 11_000, 300, 90, 0) with { BeaconCode = 7600 }, new AircraftFlightPlan { HasFlightPlan = true });

        Assume(ac);

        Assert.Equal(7600u, ac.Transponder.Code);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("7600", StringComparison.Ordinal));
    }

    [Fact]
    public void Coasting_AssumesFromTheDeadReckonedPose_WithANote()
    {
        var ac = Shadow(Sample(0, EnRoute, 11_000, 300, 90, 0), new AircraftFlightPlan { HasFlightPlan = true });
        LiveTrafficKinematics.Advance(ac, 20, null, 20);
        Assert.True(ac.LiveTraffic!.IsCoasting);
        var pose = ac.Position;

        var result = Assume(ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(pose, ac.Position);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("coasting", StringComparison.Ordinal));
    }
}
