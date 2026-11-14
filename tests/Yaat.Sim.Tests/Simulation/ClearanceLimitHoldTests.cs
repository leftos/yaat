using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// An IFR aircraft flying the clearance-limit direct to its cleared destination holds over the airport when it gets there
/// (AIM 5-3-8: the airport reference point, the course it approached on, right turns, the altitude standard leg), slows to
/// holding speed from three minutes out, and stays in the hold for an altitude change but leaves it for a heading. A VFR
/// aircraft flies straight on.
/// </summary>
public class ClearanceLimitHoldTests
{
    private const double StartAltitudeFt = 11000;
    private const double AssignedAltitudeFt = 9000;
    private const int MaxTicks = 1800;

    public ClearanceLimitHoldTests() => TestVnasData.EnsureInitialized();

    /// <summary>OAKES3 RW12's last fix, HIRMO, as the bundled CIFP resolves it.</summary>
    private static NavigationTarget Hirmo()
    {
        CifpStarProcedure? star = NavigationDatabase.Instance.GetStar("KOAK", "OAKES3");
        Assert.NotNull(star);
        Assert.NotEmpty(star.CommonLegs);
        Assert.True(star.RunwayTransitions.TryGetValue("RW12", out CifpTransition? rw12));

        var orderedLegs = new List<CifpLeg>();
        orderedLegs.AddRange(star.CommonLegs);
        orderedLegs.AddRange(rw12.Legs);
        List<NavigationTarget> targets = DepartureClearanceHandler.ResolveLegsToTargets(orderedLegs);
        Assert.NotEmpty(targets);
        NavigationTarget last = targets[^1];
        Assert.Equal("HIRMO", last.Name);
        return last;
    }

    private static LatLon KoakPosition()
    {
        (double Lat, double Lon)? pos = NavigationDatabase.Instance.GetAirportPosition("KOAK");
        Assert.NotNull(pos);
        return new LatLon(pos.Value.Lat, pos.Value.Lon);
    }

    private static LatLon FixPosition(string fixName)
    {
        (double Lat, double Lon)? pos = NavigationDatabase.Instance.GetFixPosition(fixName);
        Assert.NotNull(pos);
        return new LatLon(pos.Value.Lat, pos.Value.Lon);
    }

    /// <summary>An aircraft at <paramref name="sole"/> with it as its only route target, cleared to KOAK.</summary>
    private static AircraftState Build(NavigationTarget sole, string flightRules, double assignedAltitudeFt)
    {
        var ac = new AircraftState
        {
            Callsign = "SWA22",
            AircraftType = "B738",
            Position = sole.Position,
            TrueHeading = new TrueHeading(90),
            Altitude = StartAltitudeFt,
            IndicatedAirspeed = 250,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                Departure = "KSLC",
                Destination = "KOAK",
                Route = "",
                Altitude = flightRules == "IFR" ? PlannedAltitude.Ifr(35000) : PlannedAltitude.Vfr(9500),
                FlightRules = flightRules,
            },
            ClearedDestination = "KOAK",
        };
        ac.Targets.TargetAltitude = assignedAltitudeFt;
        ac.Targets.AssignedAltitude = assignedAltitudeFt;
        ac.Targets.NavigationRoute.Add(sole);
        return ac;
    }

    private static NavigationTarget Plain(NavigationTarget hirmo) => new() { Name = "HIRMO", Position = hirmo.Position };

    /// <summary>An aircraft that has just sequenced HIRMO and flies only the clearance-limit direct to KOAK.</summary>
    private static AircraftState FlyingTheDirect(string flightRules, double assignedAltitudeFt = AssignedAltitudeFt)
    {
        AircraftState ac = Build(Plain(Hirmo()), flightRules, assignedAltitudeFt);
        FlightPhysics.Update(ac, 1.0);
        Assert.True(Assert.Single(ac.Targets.NavigationRoute).IsClearanceLimitDirect);
        return ac;
    }

    private static void FlyUntilTheDirectIsSequenced(AircraftState ac)
    {
        for (int t = 0; t < MaxTicks; t++)
        {
            if (!ac.Targets.NavigationRoute.Exists(n => n.IsClearanceLimitDirect))
            {
                return;
            }

            FlightPhysics.Update(ac, 1.0);
        }

        Assert.Fail($"The aircraft did not reach KOAK within {MaxTicks} s");
    }

    /// <summary>
    /// A level aircraft <paramref name="distanceNm"/> east of KOAK heading west, accelerating from 270 to 280 KIAS, flying
    /// only the clearance-limit direct to KOAK.
    /// </summary>
    private static AircraftState OnTheDirectAt(
        double distanceNm,
        double altitudeFt = StartAltitudeFt,
        double? arrivalTrueCourse = 270,
        string flightRules = "IFR"
    )
    {
        LatLon koak = KoakPosition();
        var ac = new AircraftState
        {
            Callsign = "SWA22",
            AircraftType = "B738",
            Position = GeoMath.ProjectPoint(koak, new TrueHeading(90), distanceNm),
            TrueHeading = new TrueHeading(270),
            Altitude = altitudeFt,
            IndicatedAirspeed = 270,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                Departure = "KSLC",
                Destination = "KOAK",
                Altitude = PlannedAltitude.Ifr(35000),
                FlightRules = flightRules,
            },
            ClearedDestination = "KOAK",
        };
        ac.Targets.TargetAltitude = altitudeFt;
        ac.Targets.AssignedAltitude = altitudeFt;
        ac.Targets.TargetSpeed = 280;
        ac.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "KOAK",
                Position = koak,
                IsClearanceLimitDirect = true,
                ArrivalTrueCourse = arrivalTrueCourse,
            }
        );
        return ac;
    }

    private static LatLon KsjcPosition()
    {
        (double Lat, double Lon)? pos = NavigationDatabase.Instance.GetAirportPosition("KSJC");
        Assert.NotNull(pos);
        return new LatLon(pos.Value.Lat, pos.Value.Lon);
    }

    [Fact]
    public void AptInTheHold_TurnsDirectToTheNewAirport()
    {
        AircraftState ac = HoldingOverKoak();

        CommandResult result = FlightPlanCommandHandler.TryChangeDestination(ac, "SJC");
        Assert.True(result.Success, result.Message);

        Assert.Null(ac.Phases?.CurrentPhase);
        NavigationTarget only = Assert.Single(ac.Targets.NavigationRoute);
        Assert.Equal("KSJC", only.Name);
        Assert.True(only.IsClearanceLimitDirect);

        FlightPhysics.Update(ac, 1.0);
        Assert.NotNull(ac.Targets.TargetTrueHeading);
        double toKsjc = GeoMath.BearingTo(ac.Position, KsjcPosition());
        Assert.True(
            AngleDiff(ac.Targets.TargetTrueHeading!.Value.Degrees, toKsjc) < 15.0,
            $"expected a heading toward KSJC (~{toKsjc:F0}° true), got {ac.Targets.TargetTrueHeading.Value.Degrees:F0}°"
        );
    }

    [Fact]
    public void AptToTheSameAirportInTheHold_KeepsTheHold()
    {
        AircraftState ac = HoldingOverKoak();
        HoldingPatternPhase hold = Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);

        CommandResult result = FlightPlanCommandHandler.TryChangeDestination(ac, "OAK");
        Assert.True(result.Success, result.Message);

        Assert.Same(hold, ac.Phases?.CurrentPhase);
        Assert.DoesNotContain(ac.Targets.NavigationRoute, t => t.IsClearanceLimitDirect);
    }

    [Fact]
    public void UnassignedMachWithinThreeMinutes_EndsTheMachAndSlows()
    {
        AircraftState ac = OnTheDirectAt(8.0, altitudeFt: 25000);
        ac.Targets.TargetMach = 0.74;

        FlightPhysics.Update(ac, 1.0);

        Assert.Null(ac.Targets.TargetMach);
        Assert.Equal(AircraftPerformance.HoldingSpeed(ac.AircraftType, ac.Altitude), ac.Targets.TargetSpeed);
    }

    [Fact]
    public void AssignedMachWithinThreeMinutes_IsKept()
    {
        AircraftState ac = OnTheDirectAt(8.0, altitudeFt: 25000);
        ac.Targets.TargetMach = 0.74;
        ac.Targets.HasExplicitSpeedCommand = true;

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(0.74, ac.Targets.TargetMach);
    }

    [Fact]
    public void Vfr_DoesNotSlow()
    {
        AircraftState ac = OnTheDirectAt(8.0, flightRules: "VFR");

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(280, ac.Targets.TargetSpeed);
    }

    [Fact]
    public void NullArrivalCourse_HoldsOnTheTrack()
    {
        AircraftState ac = OnTheDirectAt(3.0, AssignedAltitudeFt, arrivalTrueCourse: null);

        FlyUntilTheDirectIsSequenced(ac);

        HoldingPatternPhase hold = Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
        Assert.True(hold.IsClearanceLimitHold);
        Assert.Equal((int)Math.Round(ac.TrueTrack.Degrees) % 360, hold.InboundCourse);
    }

    [Fact]
    public void DirectCreatedAtTheAirport_HasNoArrivalCourse()
    {
        LatLon koak = KoakPosition();

        NavigationTarget? direct = FlightPhysics.ClearanceLimitDirectTo("KOAK", GeoMath.ProjectPoint(koak, new TrueHeading(90), 0.2));

        Assert.NotNull(direct);
        Assert.Null(direct.ArrivalTrueCourse);
    }

    private static AircraftState HoldingOverKoak()
    {
        AircraftState ac = OnTheDirectAt(3.0, AssignedAltitudeFt);
        FlyUntilTheDirectIsSequenced(ac);
        Assert.True(Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase).IsClearanceLimitHold);
        return ac;
    }

    private static void Dispatch(AircraftState ac, string command)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, TestDispatch.Context(new Random(1)));
        Assert.True(result.Success, result.Message);
    }

    private static double AngleDiff(double a, double b)
    {
        double diff = Math.Abs(((a - b) % 360 + 360) % 360);
        return diff > 180 ? 360 - diff : diff;
    }

    [Fact]
    public void TheDirect_CarriesTheArrivalCourseFromWhereItWasCreated()
    {
        NavigationTarget hirmo = Hirmo();

        AircraftState ac = FlyingTheDirect("IFR");

        double? course = ac.Targets.NavigationRoute[0].ArrivalTrueCourse;
        Assert.NotNull(course);
        double expected = (GeoMath.BearingTo(KoakPosition(), hirmo.Position) + 180) % 360;
        Assert.True(AngleDiff(course.Value, expected) < 0.5, $"expected ~{expected:F1}° true, got {course.Value:F1}°");
    }

    [Fact]
    public void IfrReachingTheAirport_StartsTheClearanceLimitHold()
    {
        AircraftState ac = FlyingTheDirect("IFR");
        double arrivalCourse = ac.Targets.NavigationRoute[0].ArrivalTrueCourse!.Value;

        FlyUntilTheDirectIsSequenced(ac);

        HoldingPatternPhase hold = Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
        Assert.True(hold.IsClearanceLimitHold);
        Assert.Equal("KOAK", hold.FixName);
        LatLon koak = KoakPosition();
        Assert.Equal(koak.Lat, hold.FixLat, 6);
        Assert.Equal(koak.Lon, hold.FixLon, 6);
        Assert.Equal(TurnDirection.Right, hold.Direction);
        Assert.True(hold.IsMinuteBased);
        Assert.Equal(1.0, hold.LegLength);
        Assert.Equal((int)Math.Round(arrivalCourse) % 360, hold.InboundCourse);
    }

    [Fact]
    public void IfrHoldAbove14000_FliesOneAndAHalfMinuteLegs()
    {
        AircraftState ac = FlyingTheDirect("IFR", assignedAltitudeFt: 16000);

        FlyUntilTheDirectIsSequenced(ac);

        HoldingPatternPhase hold = Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
        Assert.True(hold.IsClearanceLimitHold);
        Assert.Equal(1.5, hold.LegLength);
    }

    [Fact]
    public void VfrReachingTheAirport_FliesStraightOn()
    {
        // A VFR aircraft can still be carrying a clearance-limit direct (one restored from an older snapshot, or
        // one an APT change built), but its own route end gives it none, so the direct is built here.
        AircraftState ac = OnTheDirectAt(8.0, flightRules: "VFR");

        FlyUntilTheDirectIsSequenced(ac);

        Assert.Null(ac.Phases?.CurrentPhase);
        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.Null(ac.Targets.TargetTrueHeading);
    }

    [Fact]
    public void WithinThreeMinutes_SlowsToHoldingSpeed()
    {
        AircraftState ac = OnTheDirectAt(8.0);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(AircraftPerformance.HoldingSpeed(ac.AircraftType, ac.Altitude), ac.Targets.TargetSpeed);
    }

    [Fact]
    public void BeyondThreeMinutes_KeepsItsSpeed()
    {
        AircraftState ac = OnTheDirectAt(25.0);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(280, ac.Targets.TargetSpeed);
    }

    [Fact]
    public void ExplicitSpeed_IsNotOverridden()
    {
        AircraftState ac = OnTheDirectAt(8.0);
        ac.Targets.HasExplicitSpeedCommand = true;

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(280, ac.Targets.TargetSpeed);
    }

    [Fact]
    public void AltitudeOnly_KeepsTheHold()
    {
        AircraftState ac = HoldingOverKoak();

        Dispatch(ac, "DM 070");

        HoldingPatternPhase hold = Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
        Assert.True(hold.IsClearanceLimitHold);
        Assert.Equal(7000, ac.Targets.AssignedAltitude);
    }

    [Fact]
    public void Heading_EndsTheHold()
    {
        AircraftState ac = HoldingOverKoak();

        Dispatch(ac, "FH 270");

        Assert.IsNotType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
    }

    [Fact]
    public void ClearanceLimitHold_SurvivesASnapshotRoundTrip()
    {
        var phase = new HoldingPatternPhase
        {
            FixName = "KOAK",
            FixLat = 37.72,
            FixLon = -122.22,
            InboundCourse = 270,
            LegLength = 1.0,
            IsMinuteBased = true,
            Direction = TurnDirection.Right,
            IsClearanceLimitHold = true,
            ClearanceLimitReported = true,
        };

        HoldingPatternPhaseDto dto = Assert.IsType<HoldingPatternPhaseDto>(phase.ToSnapshot());
        var restored = HoldingPatternPhase.FromSnapshot(dto);
        Assert.True(restored.IsClearanceLimitHold);
        Assert.True(restored.ClearanceLimitReported);

        HoldingPatternPhaseDto? read = JsonSerializer.Deserialize<HoldingPatternPhaseDto>(JsonSerializer.Serialize(dto));
        Assert.NotNull(read);
        var fromJson = HoldingPatternPhase.FromSnapshot(read);
        Assert.True(fromJson.IsClearanceLimitHold);
        Assert.True(fromJson.ClearanceLimitReported);
    }

    [Fact]
    public void ArrivalTrueCourse_SurvivesASnapshotRoundTrip()
    {
        var direct = new NavigationTarget
        {
            Name = "KOAK",
            Position = new LatLon(37.72, -122.22),
            IsClearanceLimitDirect = true,
            ArrivalTrueCourse = 123.4,
        };

        Assert.Equal(123.4, NavigationTarget.FromSnapshot(direct.ToSnapshot()).ArrivalTrueCourse);

        string json = JsonSerializer.Serialize(direct.ToSnapshot());
        NavigationTargetDto? read = JsonSerializer.Deserialize<NavigationTargetDto>(json);
        Assert.NotNull(read);
        Assert.Equal(123.4, NavigationTarget.FromSnapshot(read).ArrivalTrueCourse);

        var fix = new NavigationTarget { Name = "BERKS", Position = new LatLon(37.86, -122.21) };
        string plainJson = JsonSerializer.Serialize(fix.ToSnapshot());
        Assert.DoesNotContain("ArrivalTrueCourse", plainJson, StringComparison.Ordinal);
        Assert.Null(NavigationTarget.FromSnapshot(JsonSerializer.Deserialize<NavigationTargetDto>(plainJson)!).ArrivalTrueCourse);
    }

    /// <summary>
    /// <see cref="OnTheDirectAt"/> with the route's last target an ordinary KOAK fix, as a filed route ending at the field
    /// leaves it, instead of the clearance-limit direct.
    /// </summary>
    private static AircraftState OnRouteEndingAtKoak(double distanceNm, string flightRules = "IFR")
    {
        AircraftState ac = OnTheDirectAt(distanceNm, flightRules: flightRules);
        ac.Targets.NavigationRoute.Clear();
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = "KOAK", Position = KoakPosition() });
        return ac;
    }

    /// <summary>
    /// Flies until the field is sequenced (the route empties or a hold starts), asserting no hold began short of the field;
    /// returns the track flown into the field.
    /// </summary>
    private static double FlyUntilTheRouteEnds(AircraftState ac)
    {
        for (int t = 0; t < MaxTicks; t++)
        {
            double track = ac.TrueTrack.Degrees;
            FlightPhysics.Update(ac, 1.0);
            if ((ac.Targets.NavigationRoute.Count == 0) || (ac.Phases?.CurrentPhase is HoldingPatternPhase))
            {
                double fromField = GeoMath.DistanceNm(ac.Position, KoakPosition());
                Assert.True(fromField < 1.0, $"the field sequenced {fromField:F2} nm away");
                return track;
            }
        }

        Assert.Fail($"The aircraft did not reach KOAK within {MaxTicks} s");
        return 0;
    }

    private static PendingApproachInfo PendingIls30()
    {
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("KOAK", "30");
        Assert.NotNull(runway);
        return new PendingApproachInfo
        {
            Clearance = new ApproachClearance
            {
                TransitionName = null,
                ApproachId = "I30",
                AirportCode = "KOAK",
                RunwayId = "30",
                FinalApproachCourse = runway.TrueHeading,
            },
            AssignedRunway = runway,
            ConnectingFix = null,
        };
    }

    private static void AssertClearanceLimitHoldOnTrack(AircraftState ac, double trackIntoTheField)
    {
        HoldingPatternPhase hold = Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
        Assert.True(hold.IsClearanceLimitHold);
        Assert.Equal("KOAK", hold.FixName);
        Assert.Equal(TurnDirection.Right, hold.Direction);
        Assert.True(
            AngleDiff(hold.InboundCourse, trackIntoTheField) < 1.0,
            $"expected inbound ~{trackIntoTheField:F1}° true, got {hold.InboundCourse}°"
        );
    }

    [Fact]
    public void FiledRouteEndingAtTheField_Ifr_HoldsOnCrossing()
    {
        LatLon koak = KoakPosition();
        LatLon berks = FixPosition("BERKS");
        var fromField = new TrueHeading(GeoMath.BearingTo(koak, berks));
        AircraftState ac = OnRouteEndingAtKoak(3.0);
        ac.Targets.NavigationRoute.Clear();
        ac.Position = GeoMath.ProjectPoint(berks, fromField, 3.0);
        ac.TrueHeading = new TrueHeading(GeoMath.BearingTo(ac.Position, berks));
        ac.FlightPlan.Route = "BERKS KOAK";
        var warnings = new List<string>();

        ArrivalRouteResolver.PopulateNavigationRoute(ac, ac.FlightPlan.Route, warnings);

        Assert.Empty(warnings);
        Assert.Equal(["BERKS", "KOAK"], ac.Targets.NavigationRoute.Select(t => t.Name));
        Assert.False(ac.Targets.NavigationRoute[^1].IsClearanceLimitDirect);
        double track = FlyUntilTheRouteEnds(ac);

        AssertClearanceLimitHoldOnTrack(ac, track);
    }

    [Fact]
    public void DctToTheField_Ifr_HoldsOnCrossing()
    {
        AircraftState ac = OnRouteEndingAtKoak(3.0);
        ac.Targets.NavigationRoute.Clear();

        Dispatch(ac, "DCT KOAK");
        NavigationTarget only = Assert.Single(ac.Targets.NavigationRoute);
        Assert.False(only.IsClearanceLimitDirect);
        double track = FlyUntilTheRouteEnds(ac);

        AssertClearanceLimitHoldOnTrack(ac, track);
    }

    [Fact]
    public void RouteEndingAtTheField_Vfr_FliesStraightOn()
    {
        AircraftState ac = OnRouteEndingAtKoak(3.0, flightRules: "VFR");

        FlyUntilTheRouteEnds(ac);

        Assert.Null(ac.Phases?.CurrentPhase);
        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.Null(ac.Targets.TargetTrueHeading);
    }

    [Fact]
    public void RouteEndingAtTheField_AssignedHeading_DoesNotHold()
    {
        AircraftState ac = OnRouteEndingAtKoak(0.2);
        ac.Targets.AssignedMagneticHeading = new MagneticHeading(270);

        FlightPhysics.Update(ac, 1.0);

        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.IsNotType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
    }

    [Fact]
    public void RouteEndingAtTheField_PendingApproach_FliesTheApproach()
    {
        AircraftState ac = OnRouteEndingAtKoak(0.2);
        ac.Approach.PendingClearance = PendingIls30();

        FlightPhysics.Update(ac, 1.0);

        Assert.Null(ac.Approach.PendingClearance);
        Assert.IsNotType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
    }

    [Fact]
    public void RouteEndingAtTheField_SlowsToHoldingSpeedThreeMinutesOut()
    {
        AircraftState ac = OnRouteEndingAtKoak(8.0);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(AircraftPerformance.HoldingSpeed(ac.AircraftType, ac.Altitude), ac.Targets.TargetSpeed);
    }

    [Fact]
    public void NavaidNamedLikeTheDestination_GetsTheDirectNotAHold()
    {
        LatLon sacVor = FixPosition("SAC");
        (double Lat, double Lon)? ksacPos = NavigationDatabase.Instance.GetAirportPosition("KSAC");
        Assert.NotNull(ksacPos);
        var ksac = new LatLon(ksacPos.Value.Lat, ksacPos.Value.Lon);
        Assert.True(GeoMath.DistanceNm(sacVor, ksac) > 1.0, "the SAC VORTAC must lie more than 1 nm from KSAC");
        AircraftState ac = Build(new NavigationTarget { Name = "SAC", Position = sacVor }, "IFR", AssignedAltitudeFt);
        ac.FlightPlan.Destination = "KSAC";
        ac.ClearedDestination = "KSAC";

        FlightPhysics.Update(ac, 1.0);

        Assert.IsNotType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
        NavigationTarget direct = Assert.Single(ac.Targets.NavigationRoute);
        Assert.True(direct.IsClearanceLimitDirect);
        Assert.Equal("KSAC", direct.Name);
        Assert.Equal(ksac.Lat, direct.Position.Lat, 6);
        Assert.Equal(ksac.Lon, direct.Position.Lon, 6);
    }

    [Fact]
    public void HoldCommand_StillInstallsAnOrdinaryHold()
    {
        LatLon berks = FixPosition("BERKS");
        AircraftState ac = Build(new NavigationTarget { Name = "HIRMO", Position = Hirmo().Position }, "IFR", AssignedAltitudeFt);
        ac.Position = GeoMath.ProjectPoint(berks, new TrueHeading(0), 8);

        Dispatch(ac, "HOLDP BERKS 090 R");

        HoldingPatternPhase hold = Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
        Assert.False(hold.IsClearanceLimitHold);
        Assert.Equal("BERKS", hold.FixName);
    }
}
