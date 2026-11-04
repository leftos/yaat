using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// YAAT-1454: a route change clears the route and with it the fix that holds a crossing's pending speed revert. A direct-to
/// (onto the active procedure too), a heading vector, a turn-direct, a constrained direct-to without speeds, a radial,
/// airway, STAR or final approach course join apply that revert as if the fix were crossed, once the command's new state is
/// in place (AIM 4-4-10.g; 7110.65 §5-7-1.b-d): after "SPD 300; CFIX PMD 110 250; DCTF HEC" the aircraft goes back to
/// 300 kt rather than keeping 250 for good. SMAX the crossing replaced is armed again and a Mach held again; an RNS, RPS
/// or DSR given with the crossing is done then, against the new route. DEPART keeps its fix and the SMAX re-arm on it;
/// WARP drops the revert silently; in a hold nothing is restored.
/// </summary>
public sealed class RouteChangeSpeedRevertTests
{
    private const double OriginalSpeedKts = 300;
    private const double CrossingSpeedKts = 250;

    /// <summary>East of PMD on its 090 radial side, west-bound: CFIX PMD and DCT HEC both lie ahead.</summary>
    private static readonly LatLon EastOfPmd = new(34.771654, -114.542825);

    private readonly ITestOutputHelper _output;

    public RouteChangeSpeedRevertTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState Airborne(double altitude, double ias, LatLon position, string destination = "KLAX")
    {
        var ac = new AircraftState
        {
            Callsign = "SWA123",
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(270),
            TrueTrack = new TrueHeading(270),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "IFR",
                HasFlightPlan = true,
                Departure = "KPHX",
                Destination = destination,
            },
        };
        ac.Targets.TargetTrueHeading = new TrueHeading(270);
        ac.Targets.TargetAltitude = altitude;
        return ac;
    }

    private static LatLon FixPosition(string fixName)
    {
        (double lat, double lon) = Assert.IsType<(double Lat, double Lon)>(NavigationDatabase.Instance.GetFixPosition(fixName));
        return new LatLon(lat, lon);
    }

    private static void Send(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"'{text}' should parse: {parsed.Reason}");
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(new SerializableRandom(1)));
        Assert.True(result.Success, $"'{text}': {result.Message}");
    }

    private static void FlySeconds(AircraftState aircraft, int seconds)
    {
        for (int tick = 0; tick < seconds * 4; tick++)
        {
            FlightPhysics.Update(aircraft, 0.25);
        }
    }

    /// <summary>
    /// SPD 300 then CFIX PMD 110 250, flying the crossing speed; then <paramref name="routeChange"/>. The 300 comes back at
    /// once and the aircraft speeds up toward it.
    /// </summary>
    private void AssertRouteChangeRestoresThePendingSpeed(string routeChange)
    {
        AircraftState ac = Airborne(altitude: 15_000, ias: OriginalSpeedKts, EastOfPmd);
        Send(ac, "SPD 300");
        Send(ac, "CFIX PMD 110 250");
        Assert.Equal((double?)CrossingSpeedKts, ac.Targets.AssignedSpeed);
        Assert.Contains(ac.Targets.NavigationRoute, fix => (fix.Name == "PMD") && (fix.RevertSpeed is not null));

        // The crossing speed is being flown when the route changes.
        ac.IndicatedAirspeed = CrossingSpeedKts;
        FlySeconds(ac, 2);
        double before = ac.IndicatedAirspeed;

        Send(ac, routeChange);
        AssertSpeedsUpTo(ac, OriginalSpeedKts, before, routeChange);
    }

    private void AssertSpeedsUpTo(AircraftState ac, double speedKts, double before, string routeChange)
    {
        Assert.Equal((double?)speedKts, ac.Targets.AssignedSpeed);

        FlySeconds(ac, 20);
        _output.WriteLine($"{routeChange}: {before:F0} KIAS at the route change, {ac.IndicatedAirspeed:F0} KIAS 20 s later");
        Assert.True(ac.IndicatedAirspeed > before + 2, $"{routeChange}: IAS did not rise past the route change, {ac.IndicatedAirspeed:F0} KIAS");
        Assert.Equal((double?)speedKts, ac.Targets.AssignedSpeed);
    }

    [Fact]
    public void CfixThenDctf_RestoresThePendingSpeed() => AssertRouteChangeRestoresThePendingSpeed("DCTF HEC");

    [Fact]
    public void CfixThenDct_RestoresThePendingSpeed() => AssertRouteChangeRestoresThePendingSpeed("DCT HEC");

    [Fact]
    public void CfixThenFh270_RestoresThePendingSpeed() => AssertRouteChangeRestoresThePendingSpeed("FH 270");

    [Fact]
    public void CfixThenTurnDirect_RestoresThePendingSpeed() => AssertRouteChangeRestoresThePendingSpeed("TLDCT HEC");

    [Fact]
    public void CfixThenConstrainedDctfWithoutSpeed_RestoresThePendingSpeed() => AssertRouteChangeRestoresThePendingSpeed("DCTF HEC/110");

    [Fact]
    public void CfixThenJrado_RestoresThePendingSpeed() => AssertRouteChangeRestoresThePendingSpeed("JRADO PMD090");

    /// <summary>DCTF HEC with a crossing at <paramref name="altitudeFt"/> and <paramref name="speedKts"/>, as the speech path builds it.</summary>
    private static ConstrainedForceDirectToCommand DirectToHecCrossingAt(int altitudeFt, int speedKts)
    {
        LatLon hec = FixPosition("HEC");
        return new ConstrainedForceDirectToCommand(
            [new ResolvedFix("HEC", hec.Lat, hec.Lon)],
            new Dictionary<int, ConstrainedFixAltitude> { [0] = new(altitudeFt, CrossFixAltitudeType.At) },
            new Dictionary<int, int> { [0] = speedKts },
            null
        );
    }

    private static void Dispatch(AircraftState aircraft, ParsedCommand command)
    {
        CommandResult result = CommandDispatcher.Dispatch(command, aircraft, TestDispatch.Context(new SerializableRandom(1)));
        Assert.True(result.Success, result.Message);
    }

    private static void FlyUntilPassed(AircraftState aircraft, string fixName)
    {
        for (int second = 0; (second < 3_600) && aircraft.Targets.NavigationRoute.Exists(fix => fix.Name == fixName); second++)
        {
            FlySeconds(aircraft, 1);
        }

        Assert.False(aircraft.Targets.NavigationRoute.Exists(fix => fix.Name == fixName), $"the aircraft never sequenced {fixName}");
    }

    /// <summary>
    /// A constrained DCTF with a crossing speed moves the pending revert to its own last speed fix: past HEC the aircraft
    /// goes back to the 300 the dropped PMD crossing replaced, not to PMD's 250.
    /// </summary>
    [Fact]
    public void CfixThenConstrainedDctfWithSpeed_RevertsToThePendingSpeedPastItsFix()
    {
        AircraftState ac = Airborne(altitude: 15_000, ias: OriginalSpeedKts, EastOfPmd);
        Send(ac, "SPD 300");
        Send(ac, "CFIX PMD 110 250");
        ac.IndicatedAirspeed = CrossingSpeedKts;
        FlySeconds(ac, 2);

        Dispatch(ac, DirectToHecCrossingAt(11_000, 210));
        FlyUntilPassed(ac, "HEC");

        // The slowdown into HEC is not under test: under the explicit SPD the planner does not slow for a crossing speed on
        // the direct-to's first fix, so HEC is crossed at the 300 flown. Past it the 300 stays assigned and flown.
        _output.WriteLine($"Sequenced HEC at {ac.Altitude:F0} ft, {ac.IndicatedAirspeed:F0} KIAS, assigned {ac.Targets.AssignedSpeed}");
        Assert.Equal((double?)OriginalSpeedKts, ac.Targets.AssignedSpeed);
        FlySeconds(ac, 20);
        Assert.Equal((double?)OriginalSpeedKts, ac.Targets.AssignedSpeed);
        Assert.True(ac.IndicatedAirspeed >= OriginalSpeedKts - 5, $"slowed past HEC to {ac.IndicatedAirspeed:F0} KIAS");
    }

    [Fact]
    public void SmaxCfixThenConstrainedDctfWithSpeed_RearmsSpeedMaxPastItsFix()
    {
        AircraftState ac = Airborne(altitude: 15_000, ias: 280, EastOfPmd);
        Send(ac, "SMAX");
        Send(ac, "CFIX PMD 110 250");
        Assert.False(ac.Targets.SpeedMaxHeld);

        Dispatch(ac, DirectToHecCrossingAt(11_000, 210));
        Assert.False(ac.Targets.SpeedMaxHeld);
        FlyUntilPassed(ac, "HEC");

        Assert.True(ac.Targets.SpeedMaxHeld);
    }

    [Fact]
    public void MachCfixThenConstrainedDctfWithSpeed_HoldsTheMachPastItsFix()
    {
        AircraftState ac = Airborne(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000), EastOfPmd);
        Send(ac, "MACH .78");
        Send(ac, "CFIX PMD 350 250");
        Assert.Null(ac.Targets.TargetMach);

        Dispatch(ac, DirectToHecCrossingAt(35_000, 250));
        Assert.Null(ac.Targets.TargetMach);
        FlyUntilPassed(ac, "HEC");

        Assert.Equal((double?)0.78, ac.Targets.TargetMach);
    }

    /// <summary>A crossing given after a JRADO is still pending when its intercept fires: the block's route change applies it.</summary>
    [Fact]
    public void JradoThenCfix_InterceptFires_RestoresThePendingSpeed()
    {
        AircraftState ac = Airborne(altitude: 15_000, ias: OriginalSpeedKts, EastOfPmd);
        Send(ac, "SPD 300");
        Send(ac, "JRADO PMD090");
        Send(ac, "CFIX PMD 110 250");
        Assert.Equal((double?)CrossingSpeedKts, ac.Targets.AssignedSpeed);
        Assert.Contains(ac.Targets.NavigationRoute, fix => (fix.Name == "PMD") && (fix.RevertSpeed is not null));
        CommandBlock intercept = Assert.Single(ac.Queue.Blocks, block => block.Trigger?.Type == BlockTriggerType.InterceptRadial);

        // The trigger's geometry is not under test: the block fires as the queue fires it on the intercept.
        Assert.True(intercept.ApplyAction!(ac, false).Success);

        Assert.Equal((double?)OriginalSpeedKts, ac.Targets.AssignedSpeed);
    }

    [Fact]
    public void SmaxCfixThenDct_RearmsSpeedMax()
    {
        AircraftState ac = Airborne(altitude: 35_000, ias: 250, EastOfPmd);
        Send(ac, "SMAX");
        Send(ac, "CFIX PMD 110 250");
        Assert.False(ac.Targets.SpeedMaxHeld);
        Assert.Contains(ac.Targets.NavigationRoute, fix => fix.RevertSpeedMax);

        Send(ac, "DCT HEC");

        Assert.True(ac.Targets.SpeedMaxHeld);
    }

    [Fact]
    public void MachCfixThenFh_HoldsTheMachAgain()
    {
        AircraftState ac = Airborne(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000), EastOfPmd);
        Send(ac, "MACH .78");
        Send(ac, "CFIX PMD 350 250");
        Assert.Null(ac.Targets.TargetMach);
        Assert.Contains(ac.Targets.NavigationRoute, fix => fix.RevertMach is not null);

        Send(ac, "FH 270");

        Assert.Equal((double?)0.78, ac.Targets.TargetMach);
    }

    /// <summary>
    /// An RNS given with the dropped crossing is done past the direct-to's own crossing, as a CFIX carries it: past HEC no
    /// ceiling holds the aircraft to HEC's 210 and no speed stays assigned.
    /// </summary>
    [Fact]
    public void CfixWithRnsThenConstrainedDctfWithSpeed_ResumesPastItsFix()
    {
        AircraftState ac = Airborne(altitude: 15_000, ias: OriginalSpeedKts, EastOfPmd);
        Send(ac, "SPD 300");
        Send(ac, "CFIX PMD 110 250, RNS");
        Assert.Contains(ac.Targets.NavigationRoute, fix => fix.RevertResume is not null);

        Dispatch(ac, DirectToHecCrossingAt(11_000, 210));
        FlyUntilPassed(ac, "HEC");

        double atHec = ac.IndicatedAirspeed;
        Assert.Null(ac.Targets.SpeedCeiling);
        FlySeconds(ac, 30);
        _output.WriteLine($"past HEC: {atHec:F0} KIAS, 30 s later {ac.IndicatedAirspeed:F0} KIAS, ceiling {ac.Targets.SpeedCeiling}");
        Assert.Null(ac.Targets.SpeedCeiling);
        Assert.Null(ac.Targets.AssignedSpeed);
    }

    /// <summary>
    /// A Mach nobody assigned, pending below FL240 where it comes back as an IAS (no Mach hold to read), still moves to the
    /// direct-to's crossing as a Mach revert: past HEC it is restored and HEC's 210 is no ceiling.
    /// </summary>
    [Fact]
    public void UnassignedMachCfixBelowFl240ThenConstrainedDctfWithSpeed_LeavesNoCeilingPastItsFix()
    {
        AircraftState ac = Airborne(altitude: 35_000, ias: WindInterpolator.MachToIas(0.78, 35_000), EastOfPmd);
        ac.Targets.TargetMach = 0.78;
        Send(ac, "CFIX PMD 200 250");
        NavigationTarget pmd = Assert.Single(ac.Targets.NavigationRoute, fix => fix.Name == "PMD");
        Assert.Equal((bool?)false, pmd.RevertMachExplicit);

        // The crossing altitude is reached with the revert still pending.
        ac.Altitude = 20_000;
        ac.Targets.TargetAltitude = 20_000;
        ac.IndicatedAirspeed = CrossingSpeedKts;

        Dispatch(ac, DirectToHecCrossingAt(11_000, 210));
        Assert.Null(ac.Targets.TargetMach);
        FlyUntilPassed(ac, "HEC");

        _output.WriteLine($"past HEC: target {ac.Targets.TargetSpeed}, assigned {ac.Targets.AssignedSpeed}, ceiling {ac.Targets.SpeedCeiling}");
        Assert.Null(ac.Targets.SpeedCeiling);
        Assert.NotEqual((double?)210, ac.Targets.TargetSpeed);
    }

    /// <summary>An RPS given with the crossing is done on the heading vector: no assigned speed and no ceiling remain.</summary>
    [Fact]
    public void CfixWithRpsThenFh_EndsTheAssignmentWithNoCeiling()
    {
        AircraftState ac = Airborne(altitude: 15_000, ias: OriginalSpeedKts, EastOfPmd);
        Send(ac, "SPD 300");
        Send(ac, "CFIX PMD 110 250, RPS");
        Assert.Contains(ac.Targets.NavigationRoute, fix => fix.RevertResume is not null);

        Send(ac, "FH 270");

        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.Null(ac.Targets.SpeedCeiling);
    }

    [Fact]
    public void DepartFix_KeepsTheSmaxRevert()
    {
        AircraftState ac = Airborne(altitude: 35_000, ias: 250, EastOfPmd);
        Send(ac, "SMAX");
        Send(ac, "CFIX PMD 110 250");
        Send(ac, "DEPART PMD 270");

        NavigationTarget fix = Assert.Single(ac.Targets.NavigationRoute);
        Assert.Equal("PMD", fix.Name);
        Assert.True(fix.RevertSpeedMax);
    }

    [Fact]
    public void CfixThenWarp_DoesNotRestore()
    {
        AircraftState ac = Airborne(altitude: 15_000, ias: OriginalSpeedKts, EastOfPmd);
        Send(ac, "SPD 300");
        Send(ac, "CFIX PMD 110 250");

        // The crossing speed is being flown; a WARP without a speed keeps the speed flown.
        ac.IndicatedAirspeed = CrossingSpeedKts;
        Send(ac, "WARP HEC 270 150");

        Assert.Equal((double?)CrossingSpeedKts, ac.Targets.AssignedSpeed);
    }

    /// <summary>A jet joined to KOAK's real OAKES3, 15 nm before OAKES, level at 16,000.</summary>
    private AircraftState OnOakes3()
    {
        LatLon oakes = FixPosition("OAKES");
        var inbound = new TrueHeading(GeoMath.BearingTo(oakes, FixPosition("GRUDG")));
        AircraftState ac = Airborne(altitude: 16_000, ias: 280, GeoMath.ProjectPoint(oakes, inbound.ToReciprocal(), 15), destination: "KOAK");
        ac.TrueHeading = inbound;
        ac.TrueTrack = inbound;
        ac.Targets.TargetTrueHeading = inbound;
        Send(ac, "JARR OAKES3");
        Assert.Equal("OAKES3", ac.Procedure.ActiveStarId);
        _output.WriteLine($"OAKES3 route: {string.Join(" ", ac.Targets.NavigationRoute.Select(fix => fix.Name))}");
        return ac;
    }

    /// <summary>
    /// A direct-to a later fix of the active STAR keeps the procedure and skips the crossing's fix: the revert on it is
    /// applied, with the STAR still active.
    /// </summary>
    [Fact]
    public void CfixThenDctOntoTheActiveStar_RestoresThePendingSpeed()
    {
        AircraftState ac = OnOakes3();
        List<NavigationTarget> route = ac.Targets.NavigationRoute;
        int grudg = route.FindIndex(fix => fix.Name == "GRUDG");
        Assert.InRange(grudg, 0, route.Count - 2);
        string later = route[grudg + 1].Name;

        Send(ac, "SPD 300");
        Send(ac, "CFIX GRUDG 160 250");
        Assert.Equal((double?)CrossingSpeedKts, ac.Targets.AssignedSpeed);

        Send(ac, $"DCT {later}");

        Assert.Equal("OAKES3", ac.Procedure.ActiveStarId);
        Assert.Equal(later, ac.Targets.NavigationRoute[0].Name);
        Assert.Equal((double?)OriginalSpeedKts, ac.Targets.AssignedSpeed);
    }

    /// <summary>
    /// An RPS given with a crossing is done once the JARR's new STAR is in place: the old STAR's last crossed published
    /// speed is not written back as the ceiling.
    /// </summary>
    [Fact]
    public void CfixWithRpsThenJarr_HoldsNoneOfTheOldStarsSpeedsAsTheCeiling()
    {
        AircraftState ac = OnOakes3();
        for (int second = 0; (second < 900) && ac.Targets.NavigationRoute.Exists(fix => fix.Name == "OAKES"); second++)
        {
            FlySeconds(ac, 1);
        }

        double crossedKts = Assert.IsType<double>(ac.Procedure.LastCrossedPublishedSpeedKts);
        var oldStarSpeeds = ac
            .Targets.NavigationRoute.Where(fix => fix.SpeedRestriction is not null)
            .Select(fix => (double)fix.SpeedRestriction!.SpeedKts)
            .ToHashSet();
        oldStarSpeeds.Add(crossedKts);
        _output.WriteLine(
            $"past OAKES: last crossed {crossedKts:F0}, ceiling {ac.Targets.SpeedCeiling}; OAKES3 speeds {string.Join(" ", oldStarSpeeds)}"
        );

        Send(ac, "CFIX GRUDG 160 250, RPS");
        Assert.Contains(ac.Targets.NavigationRoute, fix => fix.RevertResume is not null);

        Send(ac, "JARR OAKES3");

        _output.WriteLine($"after JARR: ceiling {ac.Targets.SpeedCeiling}, assigned {ac.Targets.AssignedSpeed}");
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.DoesNotContain(ac.Targets.SpeedCeiling ?? -1, oldStarSpeeds);
    }

    /// <summary>A JLOC flown on own navigation with a crossing pending goes back to the speed the crossing replaced.</summary>
    [Fact]
    public void CfixThenJloc_RestoresThePendingSpeed()
    {
        AircraftState ac = Airborne(
            altitude: 6_000,
            ias: 250,
            GeoMath.ProjectPoint(FixPosition("SUNOL"), new TrueHeading(90), 10),
            destination: "KOAK"
        );
        Send(ac, "SPD 250");
        Send(ac, "CFIX SUNOL 060 210");
        Assert.Equal((double?)210, ac.Targets.AssignedSpeed);

        Send(ac, "JLOC 28R");

        Assert.Equal((double?)250, ac.Targets.AssignedSpeed);
    }

    /// <summary>
    /// The helper's own guard: with the hold still current (<see cref="FlightCommandHandler.SpeedRevertForbidden"/>), a
    /// revert on the hold fix is dropped, not applied. A dispatched command that ends the hold clears the phase before it
    /// applies, so the helpers are called directly here.
    /// </summary>
    [Fact]
    public void CfixInHold_ApplyTakenSpeedReverts_DoesNotRestore()
    {
        LatLon position = GeoMath.ProjectPoint(FixPosition("SUNOL"), new TrueHeading(0), 1.5);
        AircraftState ac = Airborne(altitude: 6_000, ias: 210, position);
        ac.Phases = new PhaseList();
        Send(ac, "HOLD SUNOL 004 R");
        Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
        Assert.True(FlightCommandHandler.SpeedRevertForbidden(ac));

        // A CFIX ends the hold, so the crossing it would leave on the hold fix is written here.
        NavigationTarget holder = Assert.Single(ac.Targets.NavigationRoute);
        holder.SpeedRestriction = new CifpSpeedRestriction(200, CifpSpeedRestrictionType.AtOrBelow);
        holder.SpeedTransmissionId = 1;
        holder.RevertSpeed = OriginalSpeedKts;
        holder.RevertAssignedSpeed = OriginalSpeedKts;
        ac.Targets.AssignedSpeed = 200;

        NavigationCommandHandler.ApplyTakenSpeedReverts(ac, NavigationCommandHandler.TakePendingSpeedReverts(ac.Targets.NavigationRoute));

        Assert.Equal((double?)200, ac.Targets.AssignedSpeed);
        Assert.False(NavigationCommandHandler.HasPlainRevert(holder));
    }
}
