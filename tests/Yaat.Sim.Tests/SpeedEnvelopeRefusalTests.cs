using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
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
        // The A320's FAA ACD approach speed is 136, named raised to the 5 kt step.
        Assert.Equal(136, AircraftPerformance.MinOperatingIasKts(A320, AircraftCategory.Jet));
        const int slowest = 140;

        CommandResult result = Send(ac, "SPD 100");

        AssertRefusedKeepingSpeed(ac, result, targetBefore, assignedBefore);
        Assert.Contains("slowest", result.Message);
        Assert.Contains($"{slowest} knots", result.Message);
        Assert.Contains($"{PhraseologyVerbalizer.SpeedWords(slowest)} knots is the slowest we can do", result.PilotUnable!.Tts);
    }

    /// <summary>
    /// The CRJ200's Vmo is 335 (FAA TCDS A21EA-1). The fastest is named floored to 5 kt, not 10, so the pilot offers 335
    /// rather than 330 (7110.65 §5-7-1.a.7; AIM 4-4-12.h NOTE).
    /// </summary>
    [Fact]
    public void SpeedPastVmo335_IsUnableNaming335()
    {
        Assert.Equal(335, AircraftPerformance.MaxOperatingSpeedKts("CRJ2"));
        AircraftState ac = Airborne("CRJ2", altitude: 15_000, ias: 280);

        CommandResult result = Send(ac, "SPD 345");

        AssertRefusedKeepingSpeed(ac, result, null, null);
        Assert.Contains("335 knots is the fastest", result.Message);
        Assert.Contains("three three five knots is the fastest we can do", result.PilotUnable!.Tts);
    }

    /// <summary>The named fastest is floored to 5 kt and the named slowest raised to 5 kt, so either is a speed the type can fly.</summary>
    [Theory]
    [InlineData(335, 335)]
    [InlineData(337, 335)]
    public void FastestNamed_IsFlooredToFiveKnots(double vmo, double named) => Assert.Equal(named, FlightCommandHandler.FastestNamedKts(vmo));

    [Theory]
    [InlineData(142, 145)]
    [InlineData(140, 140)]
    public void SlowestNamed_IsRaisedToFiveKnots(double minimum, double named) => Assert.Equal(named, FlightCommandHandler.SlowestNamedKts(minimum));

    /// <summary>The A321's final approach speed is 142 (FAA ACD): the slowest is named rounded up to 5 kt, 145 rather than 150.</summary>
    [Fact]
    public void SpeedBelowMinimum142_IsUnableNaming145()
    {
        Assert.Equal(142, AircraftPerformance.MinOperatingIasKts("A321", AircraftCategory.Jet));
        AircraftState ac = Airborne("A321", altitude: 5_000, ias: 220);

        CommandResult result = Send(ac, "SPD 120");

        AssertRefusedKeepingSpeed(ac, result, null, null);
        Assert.Contains("145 knots is the slowest", result.Message);
    }

    /// <summary>A Mach past Mmo below the crossover is still answered in Mach, the frame it was asked in (AIM 4-4-12.b).</summary>
    [Fact]
    public void MachPastMmo_BelowCrossover_IsUnableNamingMach()
    {
        AircraftState ac = Airborne(A320, altitude: 20_000, ias: 300);

        CommandResult result = Send(ac, "MACH .86");

        AssertRefusedKeepingSpeed(ac, result, null, null);
        Assert.Null(ac.Targets.TargetMach);
        Assert.Contains("Mach .82 is the fastest", result.Message);
        Assert.DoesNotContain("knots", result.Message);
    }

    /// <summary>
    /// SPD 300 with DM 240 in the same block: a comma block carries no "then", so the order is the pilot's (AIM 4-4-12.e;
    /// 7110.65 §5-7-2.c), and the speed is judged against FL240, where the A320 flies it, not the FL350 it is at with no
    /// altitude assigned yet, where it cannot.
    /// </summary>
    [Fact]
    public void SpeedBeforeADescentInTheSameBlock_IsJudgedAtTheBlocksAltitude()
    {
        AircraftState ac = Airborne(A320, altitude: 35_000, ias: 260);

        CommandResult result = Send(ac, "SPD 300, DM 240");

        Assert.True(result.Success, result.Message);
        Assert.Equal(300, ac.Targets.AssignedSpeed);
        Assert.Equal(24_000, ac.Targets.AssignedAltitude);
    }

    /// <summary>
    /// The A320 at FL200: Mach .80 is past its Vmo in IAS there and flyable at FL330. Alone it is refused; with CM 330 in the
    /// same block it is judged at FL330 and accepted.
    /// </summary>
    [Theory]
    [InlineData("MACH .80, CM 330", true)]
    [InlineData("MACH .80", false)]
    public void MachBeforeAClimbInTheSameBlock_IsJudgedAtTheBlocksAltitude(string text, bool accepted)
    {
        AircraftState ac = Airborne(A320, altitude: 20_000, ias: 300);

        CommandResult result = Send(ac, text);

        Assert.Equal(accepted, result.Success);
        Assert.Equal(accepted ? 0.80 : (double?)null, ac.Targets.TargetMach);
    }

    /// <summary>DM 240 first, then SPD 300: the descent is assigned before the speed is judged, so it is accepted.</summary>
    [Fact]
    public void DescentBeforeTheSpeedInTheSameBlock_IsAccepted()
    {
        AircraftState ac = Airborne(A320, altitude: 35_000, ias: 260);

        CommandResult result = Send(ac, "DM 240, SPD 300");

        Assert.True(result.Success, result.Message);
        Assert.Equal(300, ac.Targets.AssignedSpeed);
    }

    /// <summary>
    /// SPD 300; DM 240 sequences the speed first ("then"), so the speed is judged before the descent exists and is refused at
    /// FL350, with nothing applied.
    /// </summary>
    [Fact]
    public void SpeedThenADescentInALaterBlock_IsStillRefused()
    {
        AircraftState ac = Airborne(A320, altitude: 35_000, ias: 260);

        CommandResult result = Send(ac, "SPD 300; DM 240");

        Assert.False(result.Success, result.Message);
        Assert.Null(ac.Targets.AssignedSpeed);
    }

    /// <summary>
    /// Cleared to FL240 already, SPD 300 with CM 370 in the same block is judged at FL370, which replaces FL240, and is refused:
    /// the A320 flies 300 at neither FL350 nor FL370.
    /// </summary>
    [Fact]
    public void LaterAltitudeInTheBlock_ReplacesTheAssignedOne()
    {
        AircraftState ac = Airborne(A320, altitude: 35_000, ias: 260);
        Assert.True(Send(ac, "DM 240").Success);

        CommandResult result = Send(ac, "SPD 300, CM 370");

        Assert.False(result.Success, result.Message);
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.Equal(24_000, ac.Targets.AssignedAltitude);
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
        CifpSpeedRestriction published = ac.Targets.NavigationRoute[^1].SpeedRestriction!;
        double? targetBefore = ac.Targets.TargetSpeed;
        double? assignedBefore = ac.Targets.AssignedSpeed;

        CommandResult result = Send(ac, $"CFIX {fix} 350 360");

        AssertRefusedKeepingSpeed(ac, result, targetBefore, assignedBefore);
        Assert.Contains("Mach .82", result.Message);
        Assert.Equal(published, ac.Targets.NavigationRoute[^1].SpeedRestriction);
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
        // A STAR clearance authorizes its published speeds with no descend via (7110.65 5-7-1.d NOTE), so ANJLL4's
        // last fix carries its published 270.
        Assert.Equal(new CifpSpeedRestriction(270, CifpSpeedRestrictionType.Mandatory), ac.Targets.NavigationRoute[^1].SpeedRestriction);
        return ac;
    }
}
