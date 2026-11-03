using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// YAAT-1446: a SPD, SPEEDF, SPDN, MACH, SMAX, RNS, RPS, DSR or RFAS from a later transmission cancels the speed part of
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

    /// <summary>The earlier transmission's crossing speed is gone from PMD; its crossing altitude stays.</summary>
    private static void AssertCrossingSpeedCancelled(AircraftState ac, int crossingAltitudeFt)
    {
        Assert.DoesNotContain(ac.Targets.NavigationRoute, fix => fix.SpeedTransmissionId is not null);
        Assert.DoesNotContain(
            ac.Targets.NavigationRoute,
            fix => (fix.RevertSpeed is not null) || (fix.RevertAssignedSpeed is not null) || (fix.RevertMach is not null) || fix.RevertSpeedMax
        );
        NavigationTarget pmd = Assert.Single(ac.Targets.NavigationRoute, fix => fix.Name == "PMD");
        Assert.Null(pmd.SpeedRestriction);
        Assert.Equal(crossingAltitudeFt, Assert.IsType<CifpAltitudeRestriction>(pmd.AltitudeRestriction).Altitude1Ft);
    }

    [Fact]
    public void Later_Spd_CancelsTheCrossingSpeed()
    {
        AircraftState ac = Airborne("B738", altitude: 15_000, ias: 300);
        Send(ac, "SPD 300");
        Send(ac, "CFIX PMD 110 250");
        Send(ac, "SPD 280");
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

    /// <summary>YAAT-1452: a later speed instruction cleared the stamp, so the restated crossing captures a fresh revert.</summary>
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

    /// <summary>YAAT-1452: a first crossing that captured nothing still captures nothing when restated.</summary>
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
