using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A CM/DM issued after a crosswind or downwind departure clearance (<c>CTO MRC/MLC/MRD/MLD</c>) amends the departure's
/// climb target, not the pattern altitude: the aircraft is leaving the pattern, so its crosswind turn stays at the
/// published pattern altitude less 300 ft (or at level-off when the assignment is lower), and the assigned altitude holds
/// through <see cref="PatternExitPhase"/> in place of the filed cruise (AIM 4-3-3.a and FIG 4-3-2 key 5/6, 4-4-10.a,
/// 4-4-3.c.1).
/// </summary>
public class CtoPatternExitAltitudeAssignmentTests
{
    private const string Callsign = "N346G";
    private const string Route = "DCT VPBDS043001 ZEYCA338001 GERYU276001";
    private const int AssignedAltitudeFt = 2000;
    private const double LevelToleranceFt = 1.0;
    private const double SnapFt = FlightPhysics.AltitudeSnapFt;

    /// <summary>One C150 lined up on KOAK 28R, VFR KOAK → KAPC with a filed cruise of 4,500 ft.</summary>
    private const string Scenario = """
        {
          "id": "cto-pattern-exit-cm",
          "name": "CTO pattern exit then CM",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N346G",
              "aircraftType": "C150",
              "transponderMode": "C",
              "airportId": "OAK",
              "startingConditions": { "type": "OnRunway", "runway": "28R" },
              "flightplan": {
                "rules": "VFR",
                "departure": "KOAK",
                "destination": "KAPC",
                "cruiseAltitude": 4500,
                "cruiseSpeed": 90,
                "route": "",
                "remarks": "",
                "aircraftType": "C150"
              },
              "presetCommands": [],
              "spawnDelay": 0,
              "difficulty": "Easy"
            }
          ]
        }
        """;

    public CtoPatternExitAltitudeAssignmentTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void CtoMrc_CmOnTheRoll_StillTurnsCrosswindWithin300FtOfPatternAltitude()
    {
        SimulationEngine engine = LoadEngine();
        Assert.True(engine.SendCommand(Callsign, $"CTO MRC; {Route}").Success);
        Tick(engine, 2);
        double patternAltitude = Upwind(engine).Waypoints!.PatternAltitude;
        Assert.True(engine.SendCommand(Callsign, "CM 020").Success);

        ClimbOut turn = UntilPatternExit(engine);

        Assert.InRange(
            turn.TurnAltitudeFt,
            patternAltitude - UpwindPhase.PatternHandoffMarginFt,
            patternAltitude - UpwindPhase.PatternHandoffMarginFt + turn.MaxGainFt + 1
        );
    }

    [Fact]
    public void CtoMrc_CmOnTheRoll_LevelsAtTheAssignedAltitude_NotTheFiledCruise()
    {
        SimulationEngine engine = ClearedThen("CTO MRC", "CM 020");

        double highest = HighestOver(engine, 300);

        Assert.InRange(highest, AssignedAltitudeFt - SnapFt, AssignedAltitudeFt + SnapFt);
    }

    [Fact]
    public void CtoMrc_CmOnTheUpwind_TurnsAtTheSameAltitudeAsOnTheRoll()
    {
        SimulationEngine engine = LoadEngine();
        Assert.True(engine.SendCommand(Callsign, $"CTO MRC; {Route}").Success);
        engine.TickOneSecond();
        double patternAltitude = Upwind(engine).Waypoints!.PatternAltitude;
        UntilCurrentPhase<UpwindPhase>(engine);
        Assert.True(engine.SendCommand(Callsign, "CM 020").Success);

        ClimbOut turn = UntilPatternExit(engine);
        double highest = HighestOver(engine, 300);

        Assert.InRange(
            turn.TurnAltitudeFt,
            patternAltitude - UpwindPhase.PatternHandoffMarginFt,
            patternAltitude - UpwindPhase.PatternHandoffMarginFt + turn.MaxGainFt + 1
        );
        Assert.InRange(highest, AssignedAltitudeFt - SnapFt, AssignedAltitudeFt + SnapFt);
    }

    [Fact]
    public void CtoMrc_CmBelowTheTurnHeight_LevelsThenTurns()
    {
        SimulationEngine engine = ClearedThen("CTO MRC", "CM 006");

        ClimbOut turn = UntilPatternExit(engine);

        Assert.InRange(turn.MaxAltitudeFt, 600 - SnapFt, 600 + SnapFt);
        Assert.InRange(turn.TurnAltitudeFt, 600 - SnapFt, 600 + SnapFt);
    }

    [Fact]
    public void CtoMrc_CmOnTheRoll_KeepsTheDctRouteAndTheAssignedTarget()
    {
        SimulationEngine engine = ClearedThen("CTO MRC", "CM 020");
        AircraftState cleared = Require(engine);
        Assert.Equal(AssignedAltitudeFt, cleared.Targets.TargetAltitude);

        UntilPatternExitCompletes(engine);
        AircraftState departing = Require(engine);
        Assert.Equal("VPBDS043001", departing.Targets.NavigationRoute.Select(t => t.Name).FirstOrDefault());

        double highest = HighestOver(engine, 300);
        Assert.InRange(highest, AssignedAltitudeFt - SnapFt, AssignedAltitudeFt + SnapFt);
    }

    [Fact]
    public void CtoMrd_CmOnTheRoll_TurnsAtTheCrosswindDepartureTurnAltitude()
    {
        SimulationEngine engine = LoadEngine();
        Assert.True(engine.SendCommand(Callsign, $"CTO MRD; {Route}").Success);
        Tick(engine, 2);
        double patternAltitude = Upwind(engine).Waypoints!.PatternAltitude;
        Assert.True(engine.SendCommand(Callsign, "CM 020").Success);

        ClimbOut turn = UntilCrosswindTurn(engine);
        double highest = HighestOver(engine, 300);

        Assert.InRange(
            turn.TurnAltitudeFt,
            patternAltitude - UpwindPhase.PatternHandoffMarginFt,
            patternAltitude - UpwindPhase.PatternHandoffMarginFt + turn.MaxGainFt + 1
        );
        Assert.InRange(highest, AssignedAltitudeFt - SnapFt, AssignedAltitudeFt + SnapFt);
    }

    [Fact]
    public void CtoMrc_CmAfterTheTurn_KeepsTheHeadingAndLevelsAtTheAssignedAltitude()
    {
        SimulationEngine engine = LoadEngine();
        Assert.True(engine.SendCommand(Callsign, "CTO MRC").Success);
        Tick(engine, 2);
        UntilCurrentPhase<PatternExitPhase>(engine);
        double? headingBefore = Require(engine).Targets.TargetTrueHeading?.Degrees;

        Assert.True(engine.SendCommand(Callsign, "CM 020").Success);
        AircraftState amended = Require(engine);
        Assert.Equal(headingBefore, amended.Targets.TargetTrueHeading?.Degrees);

        double highest = HighestOver(engine, 300);
        Assert.InRange(highest, AssignedAltitudeFt - SnapFt, AssignedAltitudeFt + SnapFt);
    }

    [Fact]
    public void CtoMrc_DmBelowTheTurnHeight_LevelsThenTurns()
    {
        SimulationEngine engine = ClearedThen("CTO MRC", "DM 006");

        ClimbOut turn = UntilPatternExit(engine);

        Assert.InRange(turn.MaxAltitudeFt, 600 - SnapFt, 600 + SnapFt);
        Assert.InRange(turn.TurnAltitudeFt, 600 - SnapFt, 600 + SnapFt);
    }

    [Fact]
    public void CtoMrt_CmStillSetsThePatternAltitude()
    {
        SimulationEngine engine = LoadEngine();
        Assert.True(engine.SendCommand(Callsign, "CTO MRT").Success);
        Tick(engine, 2);
        Assert.True(engine.SendCommand(Callsign, "CM 020").Success);

        AircraftState aircraft = Require(engine);
        Assert.Equal(AssignedAltitudeFt, aircraft.Pattern.AltitudeOverrideFt);
        Assert.InRange(Upwind(engine).Waypoints!.PatternAltitude, AssignedAltitudeFt - LevelToleranceFt, AssignedAltitudeFt + LevelToleranceFt);
    }

    private static SimulationEngine LoadEngine()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.LoadScenario(Scenario, 1, MagneticDeclination.EvaluationDateUtc);
        return engine;
    }

    /// <summary>Loads the scenario, clears it with <paramref name="cto"/> + DCT, rolls two seconds, then issues <paramref name="cm"/>.</summary>
    private static SimulationEngine ClearedThen(string cto, string cm)
    {
        SimulationEngine engine = LoadEngine();
        CommandResult cleared = engine.SendCommand(Callsign, $"{cto}; {Route}");
        Assert.True(cleared.Success, cleared.Message);
        Tick(engine, 2);
        CommandResult amend = engine.SendCommand(Callsign, cm);
        Assert.True(amend.Success, amend.Message);
        return engine;
    }

    private static void Tick(SimulationEngine engine, int seconds)
    {
        for (int t = 0; t < seconds; t++)
        {
            engine.TickOneSecond();
        }
    }

    /// <summary>The circuit's upwind leg, pending or active: it carries the pattern altitude the departure was built for.</summary>
    private static UpwindPhase Upwind(SimulationEngine engine) => Require(engine).Phases!.Phases.OfType<UpwindPhase>().Single();

    private static AircraftState Require(SimulationEngine engine)
    {
        AircraftState? aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        return aircraft;
    }

    private static double Altitude(SimulationEngine engine) => Require(engine).Altitude;

    private static void UntilCurrentPhase<T>(SimulationEngine engine, int maxSeconds = 180)
        where T : Phase
    {
        for (int t = 0; t < maxSeconds; t++)
        {
            if (Require(engine).Phases?.CurrentPhase is T)
            {
                return;
            }

            engine.TickOneSecond();
        }

        Assert.Fail($"{typeof(T).Name} never became the current phase within {maxSeconds} s");
    }

    /// <summary>Ticks until <see cref="PatternExitPhase"/> becomes current, tracking the turn altitude and the climb it took to get there.</summary>
    private static ClimbOut UntilPatternExit(SimulationEngine engine, int maxSeconds = 600) =>
        UntilPhaseTurn(engine, p => p is PatternExitPhase, "PatternExitPhase", maxSeconds);

    /// <summary>Ticks until the crosswind leg becomes current — the upwind's turn — tracking the turn altitude and the climb.</summary>
    private static ClimbOut UntilCrosswindTurn(SimulationEngine engine, int maxSeconds = 300) =>
        UntilPhaseTurn(engine, p => p is CrosswindPhase, "CrosswindPhase", maxSeconds);

    private static ClimbOut UntilPhaseTurn(SimulationEngine engine, Func<Phase, bool> reached, string name, int maxSeconds)
    {
        double previous = Altitude(engine);
        double maxAltitude = previous;
        double maxGain = 0;
        for (int t = 0; t < maxSeconds; t++)
        {
            engine.TickOneSecond();
            AircraftState aircraft = Require(engine);
            maxGain = Math.Max(maxGain, aircraft.Altitude - previous);
            maxAltitude = Math.Max(maxAltitude, aircraft.Altitude);
            previous = aircraft.Altitude;
            if ((aircraft.Phases?.CurrentPhase is { } current) && reached(current))
            {
                return new ClimbOut(aircraft.Altitude, maxAltitude, maxGain);
            }
        }

        Assert.Fail($"{name} never became current within {maxSeconds} s");
        return default;
    }

    /// <summary>Ticks until the exit leg has run its course and hands the aircraft to free flight.</summary>
    private static void UntilPatternExitCompletes(SimulationEngine engine, int maxSeconds = 900)
    {
        bool started = false;
        for (int t = 0; t < maxSeconds; t++)
        {
            Phase? current = Require(engine).Phases?.CurrentPhase;
            if (current is PatternExitPhase)
            {
                started = true;
            }
            else if (started)
            {
                return;
            }

            engine.TickOneSecond();
        }

        Assert.Fail($"PatternExitPhase never completed within {maxSeconds} s");
    }

    /// <summary>The highest altitude over <paramref name="seconds"/> of flight, from the current state.</summary>
    private static double HighestOver(SimulationEngine engine, int seconds)
    {
        double highest = Altitude(engine);
        for (int t = 0; t < seconds; t++)
        {
            engine.TickOneSecond();
            highest = Math.Max(highest, Altitude(engine));
        }

        return highest;
    }

    private readonly record struct ClimbOut(double TurnAltitudeFt, double MaxAltitudeFt, double MaxGainFt);
}
