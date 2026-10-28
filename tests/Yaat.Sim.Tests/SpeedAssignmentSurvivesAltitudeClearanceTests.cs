using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests;

/// <summary>
/// YAAT-1358: an ATC speed assignment outlives a later altitude clearance. AIM 4-4-12.f lists what ends one ("resume
/// normal speed", "comply with speed restrictions", "resume published speed", "delete speed restrictions", climb/descend
/// via) and an altitude clearance is not among them; AIM 4-4-12.e sequences a speed with a descent ("then") rather than
/// letting the descent cancel it; 7110.65 §5-7-4 makes termination the controller's act.
/// DAL334 is the A320 of "ZLA 37 ANJLL Stream" (scenario 01GWNT49C2Q521MG26FWMZ3XTK), loaded with real navdata.
/// </summary>
public sealed class SpeedAssignmentSurvivesAltitudeClearanceTests
{
    public SpeedAssignmentSurvivesAltitudeClearanceTests()
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

    private static CommandResult Send(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"'{text}' should parse: {parsed.Reason}");
        return CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(new SerializableRandom(1)));
    }

    private static void Fly(AircraftState aircraft, double seconds, Action<AircraftState> eachSubTick)
    {
        for (int i = 0; i < (int)(seconds / 0.25); i++)
        {
            FlightPhysics.Update(aircraft, 0.25);
            eachSubTick(aircraft);
        }
    }

    /// <summary>Steps until <paramref name="done"/> holds, giving up after <paramref name="maxSeconds"/>.</summary>
    private static void FlyUntil(AircraftState aircraft, Func<AircraftState, bool> done, double maxSeconds, Action<AircraftState> eachSubTick)
    {
        for (int i = 0; i < (int)(maxSeconds / 0.25); i++)
        {
            FlightPhysics.Update(aircraft, 0.25);
            eachSubTick(aircraft);
            if (done(aircraft))
            {
                return;
            }
        }
    }

    /// <summary>Steps until the aircraft is level at <paramref name="altitudeFt"/>.</summary>
    private static void FlyToLevel(AircraftState aircraft, double altitudeFt, double maxSeconds) =>
        FlyUntil(aircraft, ac => (Math.Abs(ac.Altitude - altitudeFt) <= 10) && (Math.Abs(ac.VerticalSpeed) < 50), maxSeconds, _ => { });

    [Fact]
    public void DescendMaintain_AfterSpeed_KeepsFlyingTheAssignedSpeed()
    {
        AircraftState dal334 = LoadDal334();
        Assert.True(Send(dal334, "SPD 250").Success);
        Fly(dal334, 30, _ => { });
        Assert.InRange(dal334.IndicatedAirspeed, 248, 252);

        Assert.True(Send(dal334, "DM 290").Success);

        // Down to FL290 and a minute level there. AIM 4-4-12.c: an assigned speed is flown within 10 kt.
        double worst = 0;
        Fly(dal334, 300, ac => worst = Math.Max(worst, Math.Abs(ac.IndicatedAirspeed - 250)));

        Assert.InRange(dal334.Altitude, 28990, 29010);
        Assert.True(worst <= 10, $"DM 290 dropped the assigned 250 kt: flew up to {worst:F0} kt off it, ending at {dal334.IndicatedAirspeed:F0} kt");
    }

    [Fact]
    public void ClimbMaintain_AfterSpeed_KeepsFlyingTheAssignedSpeed()
    {
        AircraftState dal334 = LoadDal334();
        Assert.True(Send(dal334, "SPD 250").Success);
        Fly(dal334, 30, _ => { });
        Assert.InRange(dal334.IndicatedAirspeed, 248, 252);

        Assert.True(Send(dal334, "CM 360").Success);

        // Up to FL360 and a few minutes level there. AIM 4-4-12.c: an assigned speed is flown within 10 kt.
        double worst = 0;
        Fly(dal334, 300, ac => worst = Math.Max(worst, Math.Abs(ac.IndicatedAirspeed - 250)));

        Assert.InRange(dal334.Altitude, 35990, 36010);
        Assert.True(worst <= 10, $"CM 360 dropped the assigned 250 kt: flew up to {worst:F0} kt off it, ending at {dal334.IndicatedAirspeed:F0} kt");
    }

    [Fact]
    public void DeleteSpeedRestrictions_ThenClimb_FliesTheDefaultSchedule()
    {
        AircraftState dal334 = LoadDal334();
        Assert.True(Send(dal334, "DM 290").Success);
        FlyToLevel(dal334, 29000, 400);
        Assert.InRange(dal334.Altitude, 28990, 29010);

        Assert.True(Send(dal334, "SPD 230").Success);
        Fly(dal334, 60, _ => { });
        Assert.InRange(dal334.IndicatedAirspeed, 228, 232);

        Assert.True(Send(dal334, "DSR").Success);
        Assert.True(Send(dal334, "CM 350").Success);
        Fly(dal334, 300, _ => { });

        // AIM 4-4-12.f.4 / 7110.65 §5-7-4.d: deleting the speed restrictions ends the assignment, so the climb runs
        // on the type's own schedule instead of holding the deleted 230.
        Assert.True(
            dal334.IndicatedAirspeed >= 245,
            $"DSR left the aircraft on the deleted 230 kt: {dal334.IndicatedAirspeed:F0} kt at the end of the climb"
        );
    }

    [Fact]
    public void ClimbMaintain_DepartureSpeedBelowTenThousand_StandsThroughTheClimb()
    {
        AircraftState dal334 = LoadDal334();
        Assert.True(Send(dal334, "DM 060").Success);
        FlyToLevel(dal334, 6000, 1200);
        Assert.InRange(dal334.Altitude, 5990, 6010);

        Assert.True(Send(dal334, "SPD 250").Success);
        Assert.True(Send(dal334, "CM 230").Success);

        double worst = 0;
        FlyUntil(
            dal334,
            ac => (Math.Abs(ac.Altitude - 23000) <= 10) && (Math.Abs(ac.VerticalSpeed) < 50),
            900,
            ac =>
            {
                if (ac.Altitude >= 10000)
                {
                    worst = Math.Max(worst, Math.Abs(ac.IndicatedAirspeed - 250));
                }
            }
        );

        Assert.InRange(dal334.Altitude, 22990, 23010);
        Assert.True(
            worst <= 10,
            $"CM 230 dropped the assigned 250 kt in the climb from 10,000 ft: flew up to {worst:F0} kt off it, ending at {dal334.IndicatedAirspeed:F0} kt"
        );
    }

    [Fact]
    public void DescendMaintain_RetainedSpeedAboveTwoFifty_IsCappedBelowTenThousand()
    {
        AircraftState dal334 = LoadDal334();
        Assert.True(Send(dal334, "DM 150").Success);
        FlyToLevel(dal334, 15000, 900);
        Assert.InRange(dal334.Altitude, 14990, 15010);

        Assert.True(Send(dal334, "SPD 280").Success);
        Fly(dal334, 60, _ => { });
        Assert.InRange(dal334.IndicatedAirspeed, 278, 282);

        Assert.True(Send(dal334, "DM 080").Success);
        FlyUntil(dal334, ac => ac.Altitude <= 9000, 900, _ => { });
        Assert.True(dal334.Altitude <= 9000, $"the aircraft did not descend to 1,000 ft below 10,000: {dal334.Altitude:F0} ft");

        // 14 CFR 91.117 and 7110.65 §5-7-3.f NOTE 1 cap the retained 280 below 10,000 ft.
        double worst = 0;
        Fly(dal334, 60, ac => worst = Math.Max(worst, ac.IndicatedAirspeed));
        Assert.True(worst <= 252, $"the retained 280 kt was not capped below 10,000 ft: {worst:F0} kt at {dal334.Altitude:F0} ft");
    }
}
