using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// When an airborne aircraft sequences its last route fix and the route empties, with no approach clearance pending
/// and no FM/VM terminal course on the fix, a pilot with a cleared destination turns direct to that airport at once.
/// Every exclusion keeps the old behaviour: an assigned heading, an active military route, a phase other than the
/// departure climb, no cleared destination, or a last fix that is the destination itself.
/// </summary>
public class RouteExhaustedDirectToDestinationTests
{
    private const double StartAltitudeFt = 11000;
    private const double AssignedAltitudeFt = 9000;

    public RouteExhaustedDirectToDestinationTests() => TestVnasData.EnsureInitialized();

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

    private static LatLon AirportPosition(string airportId)
    {
        (double Lat, double Lon)? pos = NavigationDatabase.Instance.GetAirportPosition(airportId);
        Assert.NotNull(pos);
        return new LatLon(pos.Value.Lat, pos.Value.Lon);
    }

    private static LatLon KoakPosition() => AirportPosition("KOAK");

    private static LatLon FixPosition(string fixName)
    {
        (double Lat, double Lon)? pos = NavigationDatabase.Instance.GetFixPosition(fixName);
        Assert.NotNull(pos);
        return new LatLon(pos.Value.Lat, pos.Value.Lon);
    }

    /// <summary>An aircraft at <paramref name="sole"/> with it as its only route target, cleared to KOAK.</summary>
    private static AircraftState Build(NavigationTarget sole, string flightRules)
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
                Route = "OAKES3",
                Altitude = flightRules == "IFR" ? PlannedAltitude.Ifr(35000) : PlannedAltitude.Vfr(9500),
                FlightRules = flightRules,
            },
            ClearedDestination = "KOAK",
        };
        // Descending to an altitude a one-second tick cannot reach: a target already captured is cleared by the
        // altitude physics, which would hide whether the route end touched it.
        ac.Targets.TargetAltitude = AssignedAltitudeFt;
        ac.Targets.AssignedAltitude = AssignedAltitudeFt;
        ac.Targets.NavigationRoute.Add(sole);
        return ac;
    }

    private static NavigationTarget Plain(NavigationTarget hirmo) => new() { Name = "HIRMO", Position = hirmo.Position };

    /// <summary>An IFR aircraft that has just sequenced HIRMO and flies only the clearance-limit direct to KOAK.</summary>
    private static AircraftState FlyingTheDirect()
    {
        AircraftState ac = Build(Plain(Hirmo()), "IFR");
        ac.FlightPlan.Route = "";
        FlightPhysics.Update(ac, 1.0);
        Assert.True(Assert.Single(ac.Targets.NavigationRoute).IsClearanceLimitDirect);
        return ac;
    }

    private static void AssertTurnsDirect(AircraftState ac, double? altitudeBefore)
    {
        NavigationTarget target = Assert.Single(ac.Targets.NavigationRoute);
        Assert.Equal("KOAK", target.Name);
        LatLon koak = KoakPosition();
        Assert.Equal(koak.Lat, target.Position.Lat, 6);
        Assert.Equal(koak.Lon, target.Position.Lon, 6);
        Assert.Equal(altitudeBefore, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void IfrStarEnd_NoTerminalCourse_TurnsDirectToClearedDestination()
    {
        AircraftState ac = Build(Plain(Hirmo()), "IFR");
        ac.Procedure.ActiveStarId = "OAKES3";
        double? before = ac.Targets.TargetAltitude;

        FlightPhysics.Update(ac, 1.0);

        AssertTurnsDirect(ac, before);
    }

    [Fact]
    public void IfrDctDrain_TurnsDirect()
    {
        AircraftState ac = Build(Plain(Hirmo()), "IFR");
        double? before = ac.Targets.TargetAltitude;

        FlightPhysics.Update(ac, 1.0);

        AssertTurnsDirect(ac, before);
        // The turn toward the airport starts on the tick the direct is appended, not one tick later.
        Assert.NotNull(ac.Targets.TargetTrueHeading);
        var expected = new TrueHeading(GeoMath.BearingTo(ac.Position, KoakPosition()));
        Assert.True(
            ac.Targets.TargetTrueHeading!.Value.AbsAngleTo(expected) < 1.0,
            $"expected ~{expected.Degrees:F0}° true toward KOAK, got {ac.Targets.TargetTrueHeading.Value.Degrees:F0}°"
        );
    }

    [Fact]
    public void IfrSidEndAtFix_TurnsDirect()
    {
        AircraftState ac = Build(Plain(Hirmo()), "IFR");
        ac.Procedure.ActiveSidId = "NIMI5";
        double? before = ac.Targets.TargetAltitude;

        FlightPhysics.Update(ac, 1.0);

        AssertTurnsDirect(ac, before);
    }

    [Fact]
    public void VfrDrainWithDestination_TurnsDirect()
    {
        AircraftState ac = Build(Plain(Hirmo()), "VFR");
        double? before = ac.Targets.TargetAltitude;

        FlightPhysics.Update(ac, 1.0);

        AssertTurnsDirect(ac, before);
    }

    [Fact]
    public void InitialClimbPhase_StillTurnsDirect()
    {
        AircraftState ac = Build(Plain(Hirmo()), "IFR");
        ac.Phases = new PhaseList();
        ac.Phases.Add(new InitialClimbPhase());
        Assert.IsType<InitialClimbPhase>(ac.Phases.CurrentPhase);
        double? before = ac.Targets.TargetAltitude;

        FlightPhysics.Update(ac, 1.0);

        AssertTurnsDirect(ac, before);
    }

    [Fact]
    public void TerminalCourse_StillWinsOverDirect()
    {
        NavigationTarget hirmo = Hirmo();
        Assert.NotNull(hirmo.TerminalCourseMagnetic);
        AircraftState ac = Build(
            new NavigationTarget
            {
                Name = "HIRMO",
                Position = hirmo.Position,
                TerminalCourseMagnetic = 260.8,
            },
            "IFR"
        );

        FlightPhysics.Update(ac, 1.0);

        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.NotNull(ac.Targets.TargetTrueHeading);
        TrueHeading expected = new MagneticHeading(260.8).ToTrue(ac.Declination);
        Assert.True(
            ac.Targets.TargetTrueHeading!.Value.AbsAngleTo(expected) < 1.0,
            $"expected FM outbound ~{expected.Degrees:F0}° true, got {ac.Targets.TargetTrueHeading.Value.Degrees:F0}°"
        );
    }

    [Fact]
    public void AssignedHeading_KeepsTodaysBehaviour()
    {
        AircraftState ac = Build(Plain(Hirmo()), "IFR");
        ac.Targets.AssignedMagneticHeading = new MagneticHeading(270);

        FlightPhysics.Update(ac, 1.0);

        Assert.Empty(ac.Targets.NavigationRoute);
    }

    [Fact]
    public void NoClearedDestination_FliesStraight()
    {
        AircraftState ac = Build(Plain(Hirmo()), "IFR");
        ac.ClearedDestination = "";
        Assert.Equal("KOAK", ac.FlightPlan.Destination);

        FlightPhysics.Update(ac, 1.0);

        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.Null(ac.Targets.TargetTrueHeading);
    }

    [Fact]
    public void LastFixIsTheDestination_HoldsAtTheField()
    {
        AircraftState ac = Build(new NavigationTarget { Name = "KOAK", Position = KoakPosition() }, "IFR");

        FlightPhysics.Update(ac, 1.0);

        HoldingPatternPhase hold = Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
        Assert.True(hold.IsClearanceLimitHold);
        Assert.Equal("KOAK", hold.FixName);
    }

    [Fact]
    public void ActiveMilitaryRoute_KeepsTodaysBehaviour()
    {
        AircraftState ac = Build(Plain(Hirmo()), "IFR");
        ac.MilitaryRoute.Designator = "IR149";
        ac.MilitaryRoute.Status = MilitaryRouteStatus.Established;
        Assert.True(ac.MilitaryRoute.IsActive);

        FlightPhysics.Update(ac, 1.0);

        Assert.Empty(ac.Targets.NavigationRoute);
    }

    [Fact]
    public void NonDepartureNonNullPhase_KeepsTodaysBehaviour()
    {
        AircraftState ac = Build(Plain(Hirmo()), "IFR");
        ac.Phases = new PhaseList();
        ac.Phases.Add(new VfrHoldPhase { OrbitDirection = TurnDirection.Right });
        Assert.IsType<VfrHoldPhase>(ac.Phases.CurrentPhase);

        FlightPhysics.Update(ac, 1.0);

        Assert.Empty(ac.Targets.NavigationRoute);
    }

    [Fact]
    public void ActiveMilitaryRoute_RouteEndingAtTheField_DoesNotHold()
    {
        AircraftState ac = Build(new NavigationTarget { Name = "KOAK", Position = KoakPosition() }, "IFR");
        ac.MilitaryRoute.Designator = "IR149";
        ac.MilitaryRoute.Status = MilitaryRouteStatus.Established;
        Assert.True(ac.MilitaryRoute.IsActive);

        FlightPhysics.Update(ac, 1.0);

        Assert.IsNotType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
        Assert.Null(ac.Phases?.CurrentPhase);
        Assert.Empty(ac.Targets.NavigationRoute);
    }

    [Fact]
    public void NonDepartureNonNullPhase_RouteEndingAtTheField_DoesNotHold()
    {
        AircraftState ac = Build(new NavigationTarget { Name = "KOAK", Position = KoakPosition() }, "IFR");
        ac.Phases = new PhaseList();
        var orbit = new VfrHoldPhase { OrbitDirection = TurnDirection.Right };
        ac.Phases.Add(orbit);
        Assert.Same(orbit, ac.Phases.CurrentPhase);

        FlightPhysics.Update(ac, 1.0);

        Assert.IsNotType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
        Assert.Same(orbit, ac.Phases?.CurrentPhase);
        Assert.Empty(ac.Targets.NavigationRoute);
    }

    [Fact]
    public void ClimbViaInForce_AltitudeTargetUnchangedByDirect()
    {
        NavigationTarget hirmo = Hirmo();
        AircraftState withDest = ClimbViaAircraft(hirmo, "KOAK");
        AircraftState withoutDest = ClimbViaAircraft(hirmo, "");

        FlightPhysics.Update(withDest, 1.0);
        FlightPhysics.Update(withoutDest, 1.0);

        Assert.Equal("KOAK", Assert.Single(withDest.Targets.NavigationRoute).Name);
        Assert.Empty(withoutDest.Targets.NavigationRoute);
        Assert.Equal(withoutDest.Targets.TargetAltitude, withDest.Targets.TargetAltitude);
    }

    [Fact]
    public void DescendViaInForce_AltitudeTargetUnchangedByDirect()
    {
        NavigationTarget hirmo = Hirmo();
        AircraftState withDest = DescendViaAircraft(hirmo, "KOAK");
        AircraftState withoutDest = DescendViaAircraft(hirmo, "");

        FlightPhysics.Update(withDest, 1.0);
        FlightPhysics.Update(withoutDest, 1.0);

        Assert.Equal("KOAK", Assert.Single(withDest.Targets.NavigationRoute).Name);
        Assert.Empty(withoutDest.Targets.NavigationRoute);
        Assert.Equal(withoutDest.Targets.TargetAltitude, withDest.Targets.TargetAltitude);
    }

    [Fact]
    public void TheDirectTarget_IsFlagged()
    {
        AircraftState ac = Build(Plain(Hirmo()), "IFR");

        FlightPhysics.Update(ac, 1.0);

        Assert.True(Assert.Single(ac.Targets.NavigationRoute).IsClearanceLimitDirect);
    }

    [Fact]
    public void QueuedNavigationBlock_CompletesWhenTheClearedRouteRunsOut()
    {
        AircraftState ac = Build(Plain(Hirmo()), "IFR");
        ac.Targets.NavigationRoute.Clear();
        ac.FlightPlan.Route = "";
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound("DCT HIRMO; CM 150");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, TestDispatch.Context(new Random(1)));
        Assert.True(result.Success, result.Message);

        for (int i = 0; i < 5; i++)
        {
            FlightPhysics.Update(ac, 1.0);
        }

        // The DCT is flown once HIRMO is sequenced, though the direct to the airport follows it, so the queued
        // climb is issued.
        Assert.True(Assert.Single(ac.Targets.NavigationRoute).IsClearanceLimitDirect);
        Assert.Equal(15000, ac.Targets.AssignedAltitude);
    }

    [Fact]
    public void AppendDirectAfterTheDirect_FliesTheNewFixFirst()
    {
        AircraftState ac = FlyingTheDirect();
        LatLon berks = FixPosition("BERKS");

        CommandResult result = FlightCommandHandler.ApplyAppendDirectTo(
            new AppendDirectToCommand([new ResolvedFix("BERKS", berks.Lat, berks.Lon)], []),
            ac,
            validateDctFixes: false
        );

        Assert.True(result.Success, result.Message);
        NavigationTarget only = Assert.Single(ac.Targets.NavigationRoute);
        Assert.Equal("BERKS", only.Name);
        Assert.False(only.IsClearanceLimitDirect);
    }

    [Fact]
    public void ForceDirectAfterTheDirect_FliesTheNewFixFirst()
    {
        AircraftState ac = FlyingTheDirect();
        LatLon berks = FixPosition("BERKS");

        CommandResult result = FlightCommandHandler.ApplyAppendForceDirectTo(
            new AppendForceDirectToCommand([new ResolvedFix("BERKS", berks.Lat, berks.Lon)], []),
            ac
        );

        Assert.True(result.Success, result.Message);
        NavigationTarget only = Assert.Single(ac.Targets.NavigationRoute);
        Assert.Equal("BERKS", only.Name);
        Assert.False(only.IsClearanceLimitDirect);
    }

    [Fact]
    public void CrossFixAfterTheDirect_ReplacesIt()
    {
        AircraftState ac = FlyingTheDirect();
        LatLon berks = FixPosition("BERKS");

        CommandResult result = NavigationCommandHandler.DispatchCrossFix(
            new CrossFixCommand("BERKS", berks.Lat, berks.Lon, 8000, CrossFixAltitudeType.At, null),
            ac,
            TestDispatch.Context(new Random(1))
        );

        Assert.True(result.Success, result.Message);
        NavigationTarget only = Assert.Single(ac.Targets.NavigationRoute);
        Assert.Equal("BERKS", only.Name);
        Assert.False(only.IsClearanceLimitDirect);
        Assert.NotNull(only.AltitudeRestriction);
        Assert.Equal(8000, only.AltitudeRestriction.Altitude1Ft);
    }

    [Fact]
    public void AptWhileFlyingTheDirect_TurnsDirectToTheNewAirport()
    {
        AircraftState ac = FlyingTheDirect();

        CommandResult result = FlightPlanCommandHandler.TryChangeDestination(ac, "SJC");

        Assert.True(result.Success, result.Message);
        Assert.Equal("KSJC", ac.ClearedDestination);
        NavigationTarget only = Assert.Single(ac.Targets.NavigationRoute);
        Assert.Equal("KSJC", only.Name);
        Assert.True(only.IsClearanceLimitDirect);
        LatLon ksjc = AirportPosition("KSJC");
        Assert.Equal(ksjc.Lat, only.Position.Lat, 6);
        Assert.Equal(ksjc.Lon, only.Position.Lon, 6);
    }

    [Fact]
    public void DctToTheDestination_UnderClimbVia_IsAClearedFix()
    {
        AircraftState ac = FlyingTheDirect();
        ac.Procedure.ActiveSidId = "NIMI5";
        ac.Procedure.SidViaMode = true;
        ac.Procedure.ClimbViaTargetFt = 17000;
        LatLon koak = KoakPosition();

        CommandResult result = FlightCommandHandler.ApplyDirectTo(
            new DirectToCommand([new ResolvedFix("KOAK", koak.Lat, koak.Lon)], []),
            ac,
            validateDctFixes: false
        );

        Assert.True(result.Success, result.Message);
        NavigationTarget only = Assert.Single(ac.Targets.NavigationRoute);
        Assert.Equal("KOAK", only.Name);
        Assert.False(only.IsClearanceLimitDirect);
    }

    [Fact]
    public void PendingApproach_ActivatesWhenOnlyTheDirectRemains()
    {
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("KOAK", "30");
        Assert.NotNull(runway);
        AircraftState ac = FlyingTheDirect();
        ac.Approach.PendingClearance = new PendingApproachInfo
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

        FlightPhysics.Update(ac, 1.0);

        Assert.Null(ac.Approach.PendingClearance);
        Assert.DoesNotContain(ac.Targets.NavigationRoute, t => t.IsClearanceLimitDirect);
    }

    [Fact]
    public void ClimbVia_EndsAtTopWithOnlyTheDirectLeft()
    {
        AircraftState ac = Build(Plain(Hirmo()), "IFR");
        ac.Targets.NavigationRoute.Clear();
        ac.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "KOAK",
                Position = KoakPosition(),
                IsClearanceLimitDirect = true,
            }
        );
        ac.Targets.AssignedAltitude = null;
        ac.Targets.TargetAltitude = StartAltitudeFt;
        ac.Procedure.ActiveSidId = "NIMI5";
        ac.Procedure.SidViaMode = true;
        ac.Procedure.ClimbViaTargetFt = (int)StartAltitudeFt;

        FlightPhysics.Update(ac, 1.0);

        Assert.Null(ac.Procedure.ClimbViaTargetFt);
    }

    [Fact]
    public void GeneratorArrival_IsClearedToItsPlansDestination()
    {
        (SpawnRequest? request, string? parseError) = SpawnParser.Parse("I S+ J 28R 10");
        Assert.Null(parseError);
        Assert.NotNull(request);

        (AircraftState? state, string? error) = AircraftGenerator.Generate(
            request,
            "OAK",
            [],
            groundLayout: null,
            new Random(42),
            new BeaconCodePool()
        );

        Assert.Null(error);
        Assert.NotNull(state);
        Assert.False(string.IsNullOrEmpty(state.FlightPlan.Destination));
        Assert.Equal(state.FlightPlan.Destination, state.ClearedDestination);
    }

    private static AircraftState ClimbViaAircraft(NavigationTarget hirmo, string clearedDestination)
    {
        AircraftState ac = Build(Plain(hirmo), "IFR");
        ac.ClearedDestination = clearedDestination;
        ac.Targets.AssignedAltitude = null;
        ac.Procedure.ActiveSidId = "NIMI5";
        ac.Procedure.SidViaMode = true;
        ac.Procedure.ClimbViaTargetFt = 17000;
        return ac;
    }

    private static AircraftState DescendViaAircraft(NavigationTarget hirmo, string clearedDestination)
    {
        AircraftState ac = Build(Plain(hirmo), "IFR");
        ac.ClearedDestination = clearedDestination;
        ac.Targets.AssignedAltitude = null;
        ac.Procedure.ActiveStarId = "OAKES3";
        ac.Procedure.StarViaMode = true;
        ac.Procedure.StarViaFloor = 5000;
        return ac;
    }
}
