using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// A SPD, SPEEDF, SPDN, MACH, SMAX, RNS, RPS, DSR or RFAS from a later transmission cancels the speed part of
/// every crossing restriction an earlier transmission set, as an approach clearance does: the fix gets its published speed
/// back, loses its speed stamp and every speed revert, and keeps its crossing altitude. The aircraft flies the new
/// instruction now, at the fix and past it. In one transmission ("CFIX PMD 110 250, SPD 280") the crossing stands and the
/// speed is the one restored past the fix. A plain revert sits on one fix only: the last in route order whose speed the
/// controller set.
/// </summary>
public sealed class SpeedRevertSupersededTests
{
    private const double SpeedSnapKts = 2;

    private readonly ITestOutputHelper _output;

    public SpeedRevertSupersededTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static double B738Vmo => Assert.IsType<double>(AircraftPerformance.MaxOperatingSpeedKts("B738"));

    private static AircraftState Airborne(string type, double altitude, double ias)
    {
        var ac = new AircraftState
        {
            Callsign = "SWA123",
            AircraftType = type,
            Position = new LatLon(34.771654, -114.542825),
            TrueHeading = new TrueHeading(270),
            TrueTrack = new TrueHeading(270),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "IFR",
                HasFlightPlan = true,
                Departure = "KPHX",
                Destination = "KLAX",
            },
        };
        ac.Targets.TargetTrueHeading = new TrueHeading(270);
        ac.Targets.TargetAltitude = altitude;
        return ac;
    }

    private static void Send(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"'{text}' should parse: {parsed.Reason}");
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(new SerializableRandom(1)));
        Assert.True(result.Success, $"'{text}': {result.Message}");
    }

    private static void FlyOneSecond(AircraftState aircraft)
    {
        for (int sub = 0; sub < 4; sub++)
        {
            FlightPhysics.Update(aircraft, 0.25);
        }
    }

    /// <summary>
    /// Flies until <paramref name="fixName"/> leaves the route, calling <paramref name="eachSecond"/> after every second,
    /// and returns the IAS then.
    /// </summary>
    private static double FlyUntilSequenced(AircraftState aircraft, string fixName, Action<AircraftState> eachSecond)
    {
        for (int second = 0; second < 3_600; second++)
        {
            FlyOneSecond(aircraft);
            eachSecond(aircraft);
            if (!aircraft.Targets.NavigationRoute.Exists(fix => fix.Name == fixName))
            {
                return aircraft.IndicatedAirspeed;
            }
        }

        Assert.Fail($"{fixName} was never sequenced");
        return 0;
    }

    private static void FlySeconds(AircraftState aircraft, int seconds)
    {
        for (int second = 0; second < seconds; second++)
        {
            FlyOneSecond(aircraft);
        }
    }

    /// <summary>Whether <paramref name="fix"/> holds any speed revert: plain with its bounds, Mach, SMAX's re-arm or a resume.</summary>
    private static bool HasAnySpeedRevert(NavigationTarget fix) =>
        (fix.RevertSpeed is not null)
        || (fix.RevertAssignedSpeed is not null)
        || (fix.RevertSpeedFloor is not null)
        || (fix.RevertSpeedCeiling is not null)
        || (fix.RevertMach is not null)
        || fix.RevertSpeedMax
        || (fix.RevertResume is not null);

    /// <summary>The earlier transmission's crossing speed is gone from PMD; its crossing altitude stays.</summary>
    private static void AssertCrossingSpeedCancelled(AircraftState ac, int crossingAltitudeFt)
    {
        Assert.DoesNotContain(ac.Targets.NavigationRoute, fix => fix.SpeedTransmissionId is not null);
        Assert.DoesNotContain(ac.Targets.NavigationRoute, HasAnySpeedRevert);
        NavigationTarget pmd = Assert.Single(ac.Targets.NavigationRoute, fix => fix.Name == "PMD");
        Assert.Null(pmd.SpeedRestriction);
        Assert.Equal(crossingAltitudeFt, Assert.IsType<CifpAltitudeRestriction>(pmd.AltitudeRestriction).Altitude1Ft);
    }

    [Theory]
    [InlineData("SPD 280")]
    [InlineData("SPEEDF 280")]
    [InlineData("SPDN 280")]
    public void Later_Spd_CancelsTheCrossingSpeed(string later)
    {
        AircraftState ac = Airborne("B738", altitude: 15_000, ias: 300);
        Send(ac, "SPD 300");
        Send(ac, "CFIX PMD 110 250");
        Send(ac, later);
        AssertCrossingSpeedCancelled(ac, 11_000);

        bool reached = false;
        double lowestAfterReaching = double.MaxValue;
        double crossingIas = FlyUntilSequenced(
            ac,
            "PMD",
            a =>
            {
                reached |= Math.Abs(a.IndicatedAirspeed - 280) <= SpeedSnapKts;
                if (reached)
                {
                    lowestAfterReaching = Math.Min(lowestAfterReaching, a.IndicatedAirspeed);
                }
            }
        );

        _output.WriteLine($"Crossed PMD at {crossingIas:F0} KIAS, {ac.Altitude:F0} ft; lowest after reaching 280: {lowestAfterReaching:F0}");
        Assert.True(reached, "the aircraft never reached 280 kt");
        Assert.InRange(crossingIas, 270, 290);
        Assert.True(lowestAfterReaching >= 280 - 10, $"slowed toward the cancelled 250 crossing, to {lowestAfterReaching:F0} KIAS");

        FlySeconds(ac, 5);
        Assert.Equal(280, ac.Targets.AssignedSpeed);
    }

    [Fact]
    public void Later_Smax_CancelsTheCrossingSpeed()
    {
        AircraftState ac = Airborne("B738", altitude: 15_000, ias: 300);
        Send(ac, "SPD 300");
        Send(ac, "CFIX PMD 110 250");
        Send(ac, "SMAX");
        AssertCrossingSpeedCancelled(ac, 11_000);

        bool heldThroughout = true;
        double crossingIas = FlyUntilSequenced(ac, "PMD", a => heldThroughout &= a.Targets.SpeedMaxHeld);

        _output.WriteLine($"Crossed PMD at {crossingIas:F0} KIAS, {ac.Altitude:F0} ft");
        Assert.True(heldThroughout, "SMAX was not held all the way to PMD");
        Assert.InRange(crossingIas, B738Vmo - 20, B738Vmo);

        FlySeconds(ac, 5);
        Assert.True(ac.Targets.SpeedMaxHeld);
    }

    [Fact]
    public void Later_Rns_CancelsTheCrossingSpeed()
    {
        AircraftState ac = Airborne("B738", altitude: 15_000, ias: 300);
        Send(ac, "SPD 300");
        Send(ac, "CFIX PMD 110 250");
        Send(ac, "RNS");
        AssertCrossingSpeedCancelled(ac, 11_000);

        double lowest = double.MaxValue;
        double crossingIas = FlyUntilSequenced(ac, "PMD", a => lowest = Math.Min(lowest, a.IndicatedAirspeed));

        _output.WriteLine($"Crossed PMD at {crossingIas:F0} KIAS, {ac.Altitude:F0} ft; lowest {lowest:F0}");
        Assert.True(crossingIas > 250 + 10, $"crossed PMD at the cancelled crossing speed, {crossingIas:F0} KIAS");
        Assert.True(lowest > 250 + 10, $"slowed toward the cancelled 250 crossing, to {lowest:F0} KIAS");

        FlySeconds(ac, 5);
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.False(ac.Targets.HasExplicitSpeedCommand);
    }

    /// <summary>DSR from a later transmission: the schedule speed at PMD and past it, with the published speeds deleted.</summary>
    [Fact]
    public void Later_Dsr_CancelsTheCrossingSpeed()
    {
        AircraftState ac = Airborne("B738", altitude: 15_000, ias: 300);
        Send(ac, "SPD 300");
        Send(ac, "CFIX PMD 110 250");
        Send(ac, "DSR");
        AssertCrossingSpeedCancelled(ac, 11_000);
        Assert.True(ac.Procedure.SpeedRestrictionsDeleted);

        double lowest = double.MaxValue;
        double crossingIas = FlyUntilSequenced(ac, "PMD", a => lowest = Math.Min(lowest, a.IndicatedAirspeed));

        _output.WriteLine($"Crossed PMD at {crossingIas:F0} KIAS, {ac.Altitude:F0} ft; lowest {lowest:F0}");
        Assert.True(crossingIas > 250 + 10, $"crossed PMD at the cancelled crossing speed, {crossingIas:F0} KIAS");
        Assert.True(lowest > 250 + 10, $"slowed toward the cancelled 250 crossing, to {lowest:F0} KIAS");

        FlySeconds(ac, 5);
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.False(ac.Targets.HasExplicitSpeedCommand);
        Assert.True(ac.Procedure.SpeedRestrictionsDeleted);
    }

    /// <summary>RFAS from a later transmission: the final approach speed past PMD, not the 300 the crossing replaced.</summary>
    [Fact]
    public void Later_Rfas_CancelsTheCrossingSpeed()
    {
        AircraftState ac = Airborne("B738", altitude: 15_000, ias: 300);
        Send(ac, "SPD 300");
        Send(ac, "CFIX PMD 110 250");
        Send(ac, "RFAS");
        AssertCrossingSpeedCancelled(ac, 11_000);

        double approachSpeed = AircraftPerformance.ApproachSpeed("B738", AircraftCategorization.Categorize("B738"));
        double highestAfterReaching = double.MinValue;
        bool reached = false;
        FlyUntilSequenced(
            ac,
            "PMD",
            a =>
            {
                reached |= Math.Abs(a.IndicatedAirspeed - approachSpeed) <= SpeedSnapKts;
                if (reached)
                {
                    highestAfterReaching = Math.Max(highestAfterReaching, a.IndicatedAirspeed);
                }
            }
        );

        FlySeconds(ac, 5);
        _output.WriteLine($"Past PMD at {ac.IndicatedAirspeed:F0} KIAS; final approach speed {approachSpeed:F0}");
        Assert.Equal(approachSpeed, ac.Targets.AssignedSpeed);
        Assert.InRange(ac.IndicatedAirspeed, approachSpeed - SpeedSnapKts, approachSpeed + SpeedSnapKts);
        if (reached)
        {
            Assert.True(highestAfterReaching <= approachSpeed + SpeedSnapKts, $"sped up past PMD, to {highestAfterReaching:F0} KIAS");
        }
    }

    /// <summary>
    /// RPS from a later transmission on OAKES3: the crossing fix gets the STAR's published speed back, and past it the
    /// aircraft flies the schedule with the STAR's later published speed, crossing the next published speed above 250 at
    /// that speed, with no 250 ceiling left from the cancelled crossing.
    /// </summary>
    [Fact]
    public void Later_Rps_OnAStar_FliesThePublishedSpeedPastTheFix()
    {
        if (Simulation.DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        // Joining the STAR puts its published speeds on the route fixes (DescendViaSpeedResumeTests.JoinedThen).
        SendOk(harness, "JARR OAKES3");
        AircraftState ac = harness.Aircraft;
        List<NavigationTarget> route = ac.Targets.NavigationRoute;
        _output.WriteLine(string.Join(" ", route.Select(f => $"{f.Name}({f.SpeedRestriction?.Type} {f.SpeedRestriction?.SpeedKts})")));
        // The crossing goes on the first STAR fix, which publishes a speed above 250 (GRUDG, 260): the cancelled 250 would
        // show at it. The STAR's next published speed (FFIST) is the one honoured past it.
        NavigationTarget crossing = route[0];
        CifpSpeedRestriction publishedAtCrossing = Assert.IsType<CifpSpeedRestriction>(crossing.SpeedRestriction);
        Assert.NotEqual(CifpSpeedRestrictionType.AtOrAbove, publishedAtCrossing.Type);
        Assert.True(publishedAtCrossing.SpeedKts > 250 + SpeedSnapKts, $"premise: {crossing.Name} publishes {publishedAtCrossing.SpeedKts}");
        string crossingFix = crossing.Name;
        NavigationTarget publishedFix = route.Skip(1).First(fix => fix.SpeedRestriction is { Type: not CifpSpeedRestrictionType.AtOrAbove });
        int publishedKts = publishedFix.SpeedRestriction!.SpeedKts;
        _output.WriteLine($"Crossing {crossingFix} (publishes {publishedAtCrossing.SpeedKts}), then {publishedFix.Name} publishes {publishedKts}");

        SendOk(harness, "SPD 300");
        SendOk(harness, $"CFIX {crossingFix} 110 250");
        SendOk(harness, "RPS");

        Assert.DoesNotContain(route, fix => fix.SpeedTransmissionId is not null);
        Assert.DoesNotContain(route, HasAnySpeedRevert);
        Assert.Equal(publishedAtCrossing, crossing.SpeedRestriction);
        Assert.Equal(11_000, Assert.IsType<CifpAltitudeRestriction>(crossing.AltitudeRestriction).Altitude1Ft);

        double crossingIas = TickUntilPassed(harness, crossingFix);
        _output.WriteLine($"Crossed {crossingFix} at {crossingIas:F0} KIAS, {ac.Altitude:F0} ft");
        Assert.InRange(crossingIas, publishedAtCrossing.SpeedKts - 10, publishedAtCrossing.SpeedKts + SpeedSnapKts);
        Assert.True(crossingIas > 250 + SpeedSnapKts, $"held to the cancelled 250 crossing, {crossingIas:F0} KIAS");
        Assert.False(ac.Procedure.SpeedRestrictionsDeleted);

        // Past the fix the aircraft holds what RPS alone gives it there: the same transmissions with no crossing at all.
        if (Simulation.DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output) is not { } reference)
        {
            return;
        }

        SendOk(reference, "JARR OAKES3");
        SendOk(reference, "SPD 300");
        SendOk(reference, "RPS");
        TickUntilPassed(reference, crossingFix);
        ControlTargets withCrossing = ac.Targets;
        ControlTargets withNone = reference.Aircraft.Targets;
        _output.WriteLine($"Past {crossingFix}: assigned {withCrossing.AssignedSpeed}, ceiling {withCrossing.SpeedCeiling}");
        _output.WriteLine($"With no crossing: assigned {withNone.AssignedSpeed}, ceiling {withNone.SpeedCeiling}");
        Assert.NotEqual(250, ac.Targets.AssignedSpeed);
        Assert.NotEqual(250, ac.Targets.SpeedCeiling);
        Assert.Equal(reference.Aircraft.Targets.SpeedCeiling, ac.Targets.SpeedCeiling);
        Assert.Equal(reference.Aircraft.Targets.AssignedSpeed, ac.Targets.AssignedSpeed);
        Assert.Equal(reference.Aircraft.Targets.HasExplicitSpeedCommand, ac.Targets.HasExplicitSpeedCommand);

        double publishedCrossingIas = TickUntilPassed(harness, publishedFix.Name);
        _output.WriteLine($"Crossed {publishedFix.Name} at {publishedCrossingIas:F0} KIAS, {ac.Altitude:F0} ft");
        Assert.True(publishedCrossingIas <= publishedKts + SpeedSnapKts, $"crossed {publishedFix.Name} above its published {publishedKts}");
    }

    private void SendOk(Simulation.DescendViaSupersedesEarlierAltitudeTests.Harness harness, string command)
    {
        CommandResult result = harness.Engine.SendCommand(harness.Aircraft.Callsign, command);
        _output.WriteLine($"{command} -> {result.Message}");
        Assert.True(result.Success, $"{command}: {result.Message}");
    }

    /// <summary>Ticks the engine until <paramref name="fixName"/> leaves the route and returns the IAS then.</summary>
    private static double TickUntilPassed(Simulation.DescendViaSupersedesEarlierAltitudeTests.Harness harness, string fixName)
    {
        for (int second = 0; second < 3_600; second++)
        {
            harness.Engine.TickOneSecond();
            if (!harness.Aircraft.Targets.NavigationRoute.Exists(fix => fix.Name.Equals(fixName, StringComparison.OrdinalIgnoreCase)))
            {
                return harness.Aircraft.IndicatedAirspeed;
            }
        }

        Assert.Fail($"{fixName} was never sequenced");
        return 0;
    }

    [Fact]
    public void Later_Mach_CancelsTheCrossingSpeed()
    {
        AircraftState ac = Airborne("B738", altitude: 25_000, ias: Math.Round(WindInterpolator.MachToIas(0.70, 25_000)));
        Send(ac, "MACH .70");
        Send(ac, "CFIX PMD 250 250");
        Send(ac, "MACH .74");
        AssertCrossingSpeedCancelled(ac, 25_000);

        double lowest = double.MaxValue;
        double crossingIas = FlyUntilSequenced(ac, "PMD", a => lowest = Math.Min(lowest, a.IndicatedAirspeed));

        _output.WriteLine($"Crossed PMD at {crossingIas:F0} KIAS, {ac.Altitude:F0} ft; lowest {lowest:F0}");
        Assert.True(lowest > 250 + 10, $"slowed toward the cancelled 250 crossing, to {lowest:F0} KIAS");

        FlySeconds(ac, 5);
        Assert.Equal(0.74, ac.Targets.TargetMach);
        Assert.False(ac.Targets.SpeedMaxHeld);
    }

    [Fact]
    public void SameTransmission_Cfix_Then_Spd_RestoresTheSpdPastTheFix()
    {
        AircraftState ac = Airborne("B738", altitude: 15_000, ias: 280);
        Send(ac, "CFIX PMD 110 250, SPD 280");

        NavigationTarget pmd = Assert.Single(ac.Targets.NavigationRoute, fix => fix.Name == "PMD");
        Assert.Equal(250, Assert.IsType<CifpSpeedRestriction>(pmd.SpeedRestriction).SpeedKts);

        double crossingIas = FlyUntilSequenced(ac, "PMD", _ => { });
        _output.WriteLine($"Crossed PMD at {crossingIas:F0} KIAS, {ac.Altitude:F0} ft");
        Assert.InRange(crossingIas, 240, 260);

        FlySeconds(ac, 60);
        Assert.Equal(280, ac.Targets.AssignedSpeed);
        Assert.InRange(ac.IndicatedAirspeed, 280 - SpeedSnapKts, 280 + SpeedSnapKts);
    }

    [Fact]
    public void Two_Crossings_Keep_One_Plain_Revert_On_The_Last()
    {
        AircraftState ac = Airborne("B738", altitude: 15_000, ias: 300);
        Send(ac, "SPD 300");
        Send(ac, "DCT HEC PMD");
        Send(ac, "CFIX HEC 110 250");
        Send(ac, "CFIX PMD 110 220");

        NavigationTarget holder = Assert.Single(
            ac.Targets.NavigationRoute,
            fix => (fix.RevertSpeed is not null) || (fix.RevertAssignedSpeed is not null)
        );
        Assert.Equal("PMD", holder.Name);
        Assert.Equal(300, holder.RevertSpeed);
        Assert.Equal(300, holder.RevertAssignedSpeed);
    }

    /// <summary>
    /// 7110.65 5-7-2.e Example 3, "maintain 220 until BALTR then resume published speed": the crossing is flown, then the
    /// resume applies at the fix with no assigned speed and no ceiling left from the crossing.
    /// </summary>
    [Theory]
    [InlineData("RNS", false)]
    [InlineData("RPS", false)]
    [InlineData("DSR", true)]
    public void SameTransmission_Cfix_Then_Resume_AppliesItPastTheFix(string resume, bool deleted)
    {
        AircraftState ac = Airborne("B738", altitude: 15_000, ias: 300);
        Send(ac, "SPD 300");
        Send(ac, $"CFIX PMD 110 250, {resume}");

        double crossingIas = FlyUntilSequenced(ac, "PMD", _ => { });
        _output.WriteLine($"Crossed PMD at {crossingIas:F0} KIAS, {ac.Altitude:F0} ft");
        Assert.InRange(crossingIas, 240, 260);

        FlySeconds(ac, 5);
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.Null(ac.Targets.SpeedCeiling);
        Assert.False(ac.Targets.HasExplicitSpeedCommand);
        Assert.Equal(deleted, ac.Procedure.SpeedRestrictionsDeleted);
    }

    [Theory]
    [InlineData("SPD 280+", true)]
    [InlineData("SPD 280-", false)]
    public void SameTransmission_Cfix_Then_SpeedBound_AppliesItPastTheFix(string bound, bool isFloor)
    {
        AircraftState ac = Airborne("B738", altitude: 15_000, ias: 280);
        Send(ac, $"CFIX PMD 110 250, {bound}");
        Assert.Null(ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.SpeedCeiling);

        double crossingIas = FlyUntilSequenced(ac, "PMD", _ => { });
        _output.WriteLine($"Crossed PMD at {crossingIas:F0} KIAS, {ac.Altitude:F0} ft");
        Assert.InRange(crossingIas, 240, 260);

        FlySeconds(ac, 5);
        Assert.Equal(280, ac.Targets.AssignedSpeed);
        Assert.Equal(isFloor ? 280 : null, ac.Targets.SpeedFloor);
        Assert.Equal(isFloor ? null : 280, ac.Targets.SpeedCeiling);
    }

    /// <summary>
    /// A crossing that captured no bound leaves a bound in force at its fix alone: the floor (standing in for a published
    /// "at or above" crossed between the CFIX and its fix) survives the fix, while the captured speed comes back.
    /// </summary>
    [Fact]
    public void Cfix_WithoutBound_KeepsTheLiveFloorPastTheFix()
    {
        AircraftState ac = Airborne("B738", altitude: 15_000, ias: 300);
        Send(ac, "SPD 300");
        Send(ac, "CFIX PMD 110 250");
        NavigationTarget pmd = Assert.Single(ac.Targets.NavigationRoute, fix => fix.Name == "PMD");
        Assert.Null(pmd.RevertSpeedFloor);
        Assert.Equal(300, pmd.RevertSpeed);

        const double LiveFloorKts = 230;
        ac.Targets.SpeedFloor = LiveFloorKts;
        double? floorAtSequencing = null;
        double? assignedAtSequencing = null;
        FlyUntilSequenced(
            ac,
            "PMD",
            a =>
            {
                floorAtSequencing = a.Targets.SpeedFloor;
                assignedAtSequencing = a.Targets.AssignedSpeed;
            }
        );

        Assert.Equal(LiveFloorKts, floorAtSequencing);
        Assert.Equal(300, assignedAtSequencing);
    }

    /// <summary>
    /// A resume given with a crossing, then a later crossing on a fix before it: the one pending revert stays the resume,
    /// on the last controller-set fix, and the earlier fix keeps no plain revert of its own.
    /// </summary>
    [Fact]
    public void SameTransmission_Resume_Then_A_Later_Cfix_Before_It_Leaves_One_Revert()
    {
        AircraftState ac = Airborne("B738", altitude: 15_000, ias: 300);
        Send(ac, "SPD 300");
        Send(ac, "DCT HEC PMD");
        Send(ac, "CFIX PMD 110 250, RNS");
        Send(ac, "CFIX HEC 110 260");

        NavigationTarget holder = Assert.Single(ac.Targets.NavigationRoute, HasAnySpeedRevert);
        Assert.Equal("PMD", holder.Name);
        Assert.Equal(SpeedResumeKind.Normal, holder.RevertResume);
    }

    /// <summary>
    /// A CFIX with no transmission stamp (id 0) after "SPD 280+": its fix holds only the bound and the assignment, and the
    /// placement still finds it controller-set, so the floor survives.
    /// </summary>
    [Fact]
    public void UnstampedCfix_After_A_Floor_Keeps_The_Captured_Floor()
    {
        AircraftState ac = Airborne("B738", altitude: 15_000, ias: 280);
        Send(ac, "SPD 280+");
        (double lat, double lon) = Assert.IsType<(double Lat, double Lon)>(NavigationDatabase.Instance.GetFixPosition("PMD"));
        // Straight to the handler: the dispatcher would give the transmission an id, and id 0 is the unstamped case.
        CommandResult result = NavigationCommandHandler.DispatchCrossFix(
            new CrossFixCommand("PMD", lat, lon, 11_000, CrossFixAltitudeType.At, 250),
            ac,
            TestDispatch.Context(new SerializableRandom(1))
        );
        Assert.True(result.Success, result.Message);

        NavigationTarget pmd = Assert.Single(ac.Targets.NavigationRoute, fix => fix.Name == "PMD");
        Assert.Null(pmd.SpeedTransmissionId);
        Assert.Equal(280, pmd.RevertSpeedFloor);
        Assert.Equal(280, pmd.RevertAssignedSpeed);
    }

    /// <summary>A later SPD after a crossing given with RNS drops the resume and any bound: the SPD is flown at the fix and past it.</summary>
    [Fact]
    public void Later_Spd_After_A_SameTransmission_Resume_Drops_The_Resume()
    {
        AircraftState ac = Airborne("B738", altitude: 15_000, ias: 300);
        Send(ac, "SPD 300");
        Send(ac, "CFIX PMD 110 250, RNS");
        Assert.Contains(ac.Targets.NavigationRoute, fix => fix.RevertResume is not null);

        Send(ac, "SPD 280");
        AssertCrossingSpeedCancelled(ac, 11_000);

        FlyUntilSequenced(ac, "PMD", _ => { });
        FlySeconds(ac, 5);
        Assert.Equal(280, ac.Targets.AssignedSpeed);
        Assert.True(ac.Targets.HasExplicitSpeedCommand);
        Assert.Null(ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.SpeedCeiling);
    }

    [Fact]
    public void SameTransmission_Cfix_Then_Smax_ArmsSmaxPastTheFix()
    {
        AircraftState ac = Airborne("B738", altitude: 15_000, ias: 280);
        Send(ac, "CFIX PMD 110 250, SMAX");
        Assert.False(ac.Targets.SpeedMaxHeld);

        double crossingIas = FlyUntilSequenced(ac, "PMD", _ => { });
        Assert.InRange(crossingIas, 240, 260);
        Assert.True(ac.Targets.SpeedMaxHeld);
    }

    [Fact]
    public void SameTransmission_Cfix_Then_Mach_HoldsTheMachPastTheFix()
    {
        AircraftState ac = Airborne("B738", altitude: 25_000, ias: Math.Round(WindInterpolator.MachToIas(0.70, 25_000)));
        Send(ac, "CFIX PMD 250 250, MACH .74");
        Assert.Null(ac.Targets.TargetMach);

        double crossingIas = FlyUntilSequenced(ac, "PMD", _ => { });
        Assert.InRange(crossingIas, 240, 260);
        FlySeconds(ac, 5);
        Assert.Equal(0.74, ac.Targets.TargetMach);
    }

    /// <summary>A later speed instruction cleared the stamp, so the restated crossing captures a fresh revert.</summary>
    [Fact]
    public void Restated_Cfix_After_A_Later_Spd_Captures_A_Fresh_Revert()
    {
        AircraftState ac = Airborne("B738", altitude: 15_000, ias: 300);
        Send(ac, "SPD 300");
        Send(ac, "CFIX PMD 110 280");
        Send(ac, "SPD 290");
        Send(ac, "CFIX PMD 110 250");

        NavigationTarget pmd = Assert.Single(ac.Targets.NavigationRoute, fix => fix.Name == "PMD");
        Assert.Equal(290, pmd.RevertSpeed);
        Assert.Equal(290, pmd.RevertAssignedSpeed);
    }

    /// <summary>A first crossing that captured nothing still captures nothing when restated.</summary>
    [Fact]
    public void Restated_Cfix_Keeps_A_Null_First_Capture()
    {
        AircraftState ac = Airborne("B738", altitude: 15_000, ias: 280);
        Send(ac, "CFIX PMD 110 280");
        Send(ac, "CFIX PMD 110 250");

        NavigationTarget pmd = Assert.Single(ac.Targets.NavigationRoute, fix => fix.Name == "PMD");
        Assert.Null(pmd.RevertSpeed);
        Assert.Null(pmd.RevertAssignedSpeed);
    }
}
