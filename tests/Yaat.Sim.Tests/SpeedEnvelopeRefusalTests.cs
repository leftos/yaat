using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests;

/// <summary>
/// YAAT-1362: a speed past what the type can fly (Vmo, or Mmo's IAS equivalent at the altitude), or below its final
/// approach speed, is refused as unable. The aircraft keeps its current speed and the pilot names the fastest (or
/// slowest) speed it can fly, in Mach above the IAS/Mach crossover and in knots below it. A scenario preset is capped
/// rather than refused, and a forced speed is exempt. The A320's figures are EASA TCDS A.064 III.10: VMO 350 kt, MMO 0.82.
/// </summary>
public sealed class SpeedEnvelopeRefusalTests
{
    private const string A320 = "A320";
    private const double A320VmoKts = 350;
    private const double SpeedSnapKts = 2;

    public SpeedEnvelopeRefusalTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private const string ScenarioJson = """
        {
          "id": "yaat-1358",
          "name": "ZLA 37 ANJLL Stream (DAL334 only)",
          "aircraft": [{
            "aircraftId": "DAL334",
            "aircraftType": "A320/L",
            "transponderMode": "C",
            "startingConditions": {
              "type": "Coordinates",
              "coordinates": { "lat": 34.771654, "lon": -114.542825 },
              "altitude": 34000,
              "mach": 0.79,
              "navigationPath": "SLLRS ANJLL4"
            },
            "flightplan": {
              "rules": "IFR", "departure": "KPHX", "destination": "KLAX", "cruiseAltitude": 34000, "cruiseSpeed": 420,
              "route": "CGNEY ANJLL4", "aircraftType": "A320/L"
            },
            "presetCommands": []
          }]
        }
        """;

    private static AircraftState LoadDal334() =>
        ScenarioLoader.Load(ScenarioJson, null, new SerializableRandom(42), MagneticDeclination.EvaluationDateUtc).ImmediateAircraft[0].State;

    private static AircraftState Airborne(string type, double altitude, double ias)
    {
        var ac = new AircraftState
        {
            Callsign = "DAL334",
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

    private static CommandResult Send(AircraftState aircraft, string text) => Dispatch(aircraft, text, scripted: false);

    private static CommandResult Dispatch(AircraftState aircraft, string text, bool scripted)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"'{text}' should parse: {parsed.Reason}");
        return CommandDispatcher.DispatchCompound(
            parsed.Value!,
            aircraft,
            TestDispatch.Context(new SerializableRandom(1), isScenarioScripted: scripted)
        );
    }

    private static void Fly(AircraftState aircraft, double seconds, Action<AircraftState> eachSubTick)
    {
        for (int i = 0; i < (int)(seconds / 0.25); i++)
        {
            FlightPhysics.Update(aircraft, 0.25);
            eachSubTick(aircraft);
        }
    }

    private static void AssertRefusedKeepingSpeed(AircraftState aircraft, CommandResult result, double? targetBefore, double? assignedBefore)
    {
        Assert.False(result.Success, $"expected a refusal, got '{result.Message}'");
        Assert.NotNull(result.PilotUnable);
        Assert.Equal(targetBefore, aircraft.Targets.TargetSpeed);
        Assert.Equal(assignedBefore, aircraft.Targets.AssignedSpeed);
    }

    [Fact]
    public void SpeedPastMmo_IsNotReadBackAndFlown()
    {
        // EASA TCDS A.064 III.10: A320 MMO 0.82, VMO 350 kt. At FL340 350 KIAS is Mach 0.98.
        AircraftState dal334 = LoadDal334();
        CommandResult result = Send(dal334, "SPD 350");

        double peakMach = 0;
        Fly(dal334, 120, ac => peakMach = Math.Max(peakMach, WindInterpolator.IasToMach(ac.IndicatedAirspeed, ac.Altitude)));

        Assert.True(peakMach <= 0.82 + 0.02, $"'{result.Message}' and then flew Mach {peakMach:F3}, past the A320's MMO 0.82");
    }

    [Fact]
    public void SpeedPastVmo_BelowCrossover_IsUnableNamingKnots()
    {
        AircraftState ac = Airborne(A320, altitude: 20_000, ias: 300);
        double? targetBefore = ac.Targets.TargetSpeed;
        double? assignedBefore = ac.Targets.AssignedSpeed;

        CommandResult result = Send(ac, "SPD 360");

        AssertRefusedKeepingSpeed(ac, result, targetBefore, assignedBefore);
        Assert.Contains("fastest", result.Message);
        Assert.Contains("350 knots", result.Message);
        Assert.Contains("three five zero knots is the fastest we can do", result.PilotUnable!.Tts);
    }

    [Fact]
    public void SpeedBelowFinalApproachSpeed_IsUnableNamingTheSlowest()
    {
        AircraftState ac = Airborne(A320, altitude: 5_000, ias: 220);
        double? targetBefore = ac.Targets.TargetSpeed;
        double? assignedBefore = ac.Targets.AssignedSpeed;
        int slowest = (int)(Math.Ceiling(AircraftPerformance.MinOperatingIasKts(A320, AircraftCategory.Jet) / 10) * 10);

        CommandResult result = Send(ac, "SPD 100");

        AssertRefusedKeepingSpeed(ac, result, targetBefore, assignedBefore);
        Assert.Contains("slowest", result.Message);
        Assert.Contains($"{slowest} knots", result.Message);
        Assert.Contains($"{PhraseologyVerbalizer.SpeedWords(slowest)} knots is the slowest we can do", result.PilotUnable!.Tts);
    }

    [Fact]
    public void MachPastMmo_IsUnableNamingMach()
    {
        AircraftState ac = Airborne(A320, altitude: 35_000, ias: 260);
        double? targetBefore = ac.Targets.TargetSpeed;
        double? assignedBefore = ac.Targets.AssignedSpeed;

        CommandResult result = Send(ac, "MACH .86");

        AssertRefusedKeepingSpeed(ac, result, targetBefore, assignedBefore);
        Assert.Null(ac.Targets.TargetMach);
        Assert.Contains("Mach .82", result.Message);
        Assert.Contains("unable, Mach point eight two is the fastest we can do", result.PilotUnable!.Tts);
    }

    /// <summary>
    /// B738 (VMO 340, MMO .82) at FL240: Mach .80 is past VMO in IAS here but flyable at FL370, the altitude it is
    /// cleared to, so it is accepted as SPD would be. Cleared down to FL200 it is unflyable at both and refused.
    /// </summary>
    [Theory]
    [InlineData("CM 370", true)]
    [InlineData("DM 200", false)]
    public void MachPastVmoHere_IsAcceptedOnlyWhenFlyableAtTheAssignedAltitude(string clearance, bool accepted)
    {
        AircraftState ac = Airborne("B738", altitude: 24_000, ias: 290);
        Assert.True(Send(ac, clearance).Success, clearance);

        CommandResult result = Send(ac, "MACH .80");

        if (accepted)
        {
            Assert.True(result.Success, result.Message);
            Assert.Equal(0.80, ac.Targets.TargetMach);
        }
        else
        {
            AssertRefusedKeepingSpeed(ac, result, null, null);
            Assert.Contains("340 knots", result.Message);
        }
    }

    [Fact]
    public void ConstrainedDirectAtASpeedPastTheLimit_IsUnableAndChangesNothing()
    {
        AircraftState ac = Airborne(A320, altitude: 20_000, ias: 300);

        CommandResult result = CommandDispatcher.Dispatch(ConstrainedDirectToSunol(20_000, 360), ac, TestDispatch.Context(new SerializableRandom(1)));

        AssertRefusedKeepingSpeed(ac, result, null, null);
        Assert.Contains("350 knots", result.Message);
        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.Equal(20_000, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void ConstrainedDirectAtAFlyableSpeed_IsAccepted()
    {
        AircraftState ac = Airborne(A320, altitude: 20_000, ias: 300);

        CommandResult result = CommandDispatcher.Dispatch(ConstrainedDirectToSunol(20_000, 300), ac, TestDispatch.Context(new SerializableRandom(1)));

        Assert.True(result.Success, result.Message);
        Assert.Equal(300, Assert.Single(ac.Targets.NavigationRoute).SpeedRestriction!.SpeedKts);
    }

    /// <summary>DCTF SUNOL with a crossing altitude and speed, SUNOL's position from the real navdata.</summary>
    internal static ConstrainedForceDirectToCommand ConstrainedDirectToSunol(int altitudeFt, int speedKts)
    {
        CrossFixCommand sunol = Assert.IsType<CrossFixCommand>(CommandParser.Parse("CFIX SUNOL 200").Value);
        return new ConstrainedForceDirectToCommand(
            [new ResolvedFix("SUNOL", sunol.FixLat, sunol.FixLon)],
            new Dictionary<int, ConstrainedFixAltitude> { [0] = new(altitudeFt, CrossFixAltitudeType.At) },
            new Dictionary<int, int> { [0] = speedKts },
            null
        );
    }

    [Fact]
    public void FloorAboveMax_IsUnable()
    {
        AircraftState ac = Airborne(A320, altitude: 20_000, ias: 300);

        CommandResult result = Send(ac, "SPD 360+");

        AssertRefusedKeepingSpeed(ac, result, null, null);
        Assert.Null(ac.Targets.SpeedFloor);
        Assert.Contains("fastest", result.Message);
    }

    [Fact]
    public void CeilingBelowMin_IsUnable()
    {
        AircraftState ac = Airborne(A320, altitude: 5_000, ias: 220);

        CommandResult result = Send(ac, "SPD 100-");

        AssertRefusedKeepingSpeed(ac, result, null, null);
        Assert.Null(ac.Targets.SpeedCeiling);
        Assert.Contains("slowest", result.Message);
    }

    [Fact]
    public void ScenarioPresetPastMax_IsCappedNotRefused()
    {
        AircraftState ac = Airborne(A320, altitude: 20_000, ias: 300);

        CommandResult result = Dispatch(ac, "SPD 360", scripted: true);
        double peakIas = 0;
        Fly(ac, 120, a => peakIas = Math.Max(peakIas, a.IndicatedAirspeed));

        Assert.True(result.Success, result.Message);
        Assert.True(peakIas <= A320VmoKts + SpeedSnapKts, $"a scripted SPD 360 flew {peakIas:F1} KIAS, past the A320's VMO {A320VmoKts}");
    }

    [Fact]
    public void Helicopter_BelowFinalApproachSpeed_IsNotRefused()
    {
        AircraftState heli = Airborne("R22", altitude: 800, ias: 70);

        CommandResult result = Send(heli, "SPD 40");

        Assert.True(result.Success, result.Message);
        Assert.Equal(60, heli.Targets.TargetSpeed);
    }

    [Fact]
    public void ForceSpeed_IsExempt()
    {
        AircraftState ac = Airborne(A320, altitude: 20_000, ias: 300);

        CommandResult result = Send(ac, "SPDN 360");

        Assert.True(result.Success, result.Message);
        Assert.True(ac.Targets.SpeedLimitsLifted);
    }

    [Fact]
    public void TypeWithNoLimit_RefusesNothing()
    {
        Assert.Null(AircraftPerformance.MaxOperatingIasKts("F16", 20_000));
        AircraftState ac = Airborne("F16", altitude: 20_000, ias: 350);

        CommandResult result = Send(ac, "SPD 450");

        Assert.True(result.Success, result.Message);
        Assert.Equal(450, ac.Targets.AssignedSpeed);

        CommandResult slow = Send(ac, "SPD 120");

        Assert.True(slow.Success, slow.Message);
        Assert.Equal(120, ac.Targets.AssignedSpeed);
    }

    [Fact]
    public void DescentClearedWithSpeedPastTheLimitHere_IsAcceptedAndCappedUntilFlyable()
    {
        AircraftState ac = Airborne(A320, altitude: 35_000, ias: 260);
        Assert.True(Send(ac, "DM 240").Success);

        CommandResult result = Send(ac, "SPD 300");
        Assert.True(result.Success, result.Message);

        double worstExcess = double.MinValue;
        Fly(ac, 900, a => worstExcess = Math.Max(worstExcess, a.IndicatedAirspeed - AircraftPerformance.MaxOperatingIasKts(A320, a.Altitude)!.Value));

        Assert.True(worstExcess <= SpeedSnapKts, $"SPD 300 in the descent flew {worstExcess:F1} kt past the cap");
        Assert.InRange(ac.Altitude, 23_990, 24_010);
        Assert.Equal(300, ac.IndicatedAirspeed, SpeedSnapKts);
        Assert.Equal(300, ac.Targets.AssignedSpeed);
    }

    [Fact]
    public void SpeedPastTheLimitAtBothAltitudes_IsUnable()
    {
        AircraftState ac = Airborne(A320, altitude: 35_000, ias: 260);
        double? targetBefore = ac.Targets.TargetSpeed;

        CommandResult result = Send(ac, "SPD 300");

        AssertRefusedKeepingSpeed(ac, result, targetBefore, null);
        Assert.Contains("Mach .82", result.Message);
    }

    [Fact]
    public void SpeedAboveCrossover_IsNamedInMach()
    {
        AircraftState ac = Airborne(A320, altitude: 35_000, ias: 260);

        CommandResult result = Send(ac, "SPD 290");

        AssertRefusedKeepingSpeed(ac, result, null, null);
        Assert.Contains("Mach .82", result.Message);
    }

    [Fact]
    public void SpeedForce_PastMax_IsUnable()
    {
        AircraftState ac = Airborne(A320, altitude: 20_000, ias: 300);

        CommandResult result = Send(ac, "SPEEDF 360");

        AssertRefusedKeepingSpeed(ac, result, null, null);
        Assert.Contains("350 knots", result.Message);
    }

    [Fact]
    public void SpeedOnTheGround_IsNotChecked()
    {
        AircraftState ac = Airborne(A320, altitude: 0, ias: 0);
        ac.IsOnGround = true;

        CommandResult result = Send(ac, "SPD 360");

        Assert.True(result.Success, result.Message);
        Assert.Equal(360, ac.Targets.AssignedSpeed);
    }

    /// <summary>The A320 at FL350, with its crossing altitude set to the altitude it is at, so only FL350 counts.</summary>
    [Fact]
    public void CrossFixAtASpeedPastTheLimit_IsUnable()
    {
        AircraftState ac = Dal334AtFl350();
        string fix = ac.Targets.NavigationRoute[^1].Name;
        double? targetBefore = ac.Targets.TargetSpeed;
        double? assignedBefore = ac.Targets.AssignedSpeed;

        CommandResult result = Send(ac, $"CFIX {fix} 350 360");

        AssertRefusedKeepingSpeed(ac, result, targetBefore, assignedBefore);
        Assert.Contains("Mach .82", result.Message);
        Assert.Null(ac.Targets.NavigationRoute[^1].SpeedRestriction);
    }

    [Fact]
    public void CrossFixAtAFlyableSpeedLowerDown_IsAccepted()
    {
        AircraftState ac = Dal334AtFl350();
        string fix = ac.Targets.NavigationRoute[^1].Name;

        CommandResult result = Send(ac, $"CFIX {fix} 110 300");

        Assert.True(result.Success, result.Message);
        Assert.Equal(300, ac.Targets.AssignedSpeed);
    }

    private static AircraftState Dal334AtFl350()
    {
        AircraftState ac = LoadDal334();
        ac.Altitude = 35_000;
        ac.Targets.TargetAltitude = 35_000;
        Assert.NotEmpty(ac.Targets.NavigationRoute);
        Assert.Null(ac.Targets.NavigationRoute[^1].SpeedRestriction);
        return ac;
    }
}
