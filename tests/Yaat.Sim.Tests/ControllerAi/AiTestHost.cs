using Yaat.Sim.ControllerAi;
using Yaat.Sim.ControllerAi.Brains;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.ControllerAi;

/// <summary>Inline scenarios and an engine + controller-AI wiring shared by the ControllerAi tests.</summary>
internal static class AiTestHost
{
    public const string Callsign = "N152SP";

    public const string ParkedAtOak = """
        {
          "id": "ai-parked",
          "name": "AI parked at OAK",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N152SP",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Parking", "parking": "SIG1" },
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KOAK", "cruiseAltitude": 1500, "cruiseSpeed": 100, "route": "", "remarks": "", "aircraftType": "C172" }
            }
          ]
        }
        """;

    public const string OnFinalAtOak = """
        {
          "id": "ai-on-final",
          "name": "AI on final at OAK",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N152SP",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "OnFinal", "runway": "28R", "distanceFromRunway": 4 },
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KOAK", "cruiseAltitude": 1500, "cruiseSpeed": 100, "route": "", "remarks": "", "aircraftType": "C172" }
            }
          ]
        }
        """;

    public static readonly IReadOnlyDictionary<string, ControlRole> NoOverrides = new Dictionary<string, ControlRole>(StringComparer.Ordinal);

    /// <summary>Loads the scenario with real navdata + the OAK layout, the ZOA config, and (when positions are given) observer brains on them.</summary>
    public static SimulationEngine Load(string scenarioJson, ArtccConfigRoot zoa, int seed, IReadOnlyList<AiPositionConfig> positions) =>
        LoadWith(scenarioJson, zoa, seed, positions, null, p => new ObserverBrain(p));

    /// <summary>Same, with the brain of the caller's choice on each position and a session runway in use.</summary>
    public static SimulationEngine LoadWith(
        string scenarioJson,
        ArtccConfigRoot zoa,
        int seed,
        IReadOnlyList<AiPositionConfig> positions,
        string? runwayInUse,
        Func<AiPositionConfig, IPositionBrain> brainFor
    )
    {
        var engine = new SimulationEngine(new TestAirportGroundData());
        var warnings = engine.LoadScenario(scenarioJson, seed, MagneticDeclination.EvaluationDateUtc);
        if (warnings.Any(w => w.Contains("error", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Scenario load reported errors: " + string.Join("; ", warnings));
        }

        var scenario = engine.Scenario!;
        scenario.ArtccConfig = zoa;
        if (positions.Count > 0)
        {
            var config = new ControllerAiConfig
            {
                Seed = seed,
                EnabledPositionIds = positions.Select(p => p.PositionId).ToList(),
                RoleOverrides = NoOverrides,
                RunwayInUse = runwayInUse,
            };
            scenario.ControllerAi = config;
            engine.ControllerAi = new AiControllerService(
                positions.Select(brainFor).ToList(),
                new HeadlessAiStaffing(positions, scenario),
                new EngineAiCommandSink(engine),
                config
            );
        }

        return engine;
    }

    /// <summary>The host loop as the server runs it: one sim-second, the auto-delete sweep, then one AI tick.</summary>
    public static void Tick(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            engine.TickOneSecond();
            engine.TickAutoDelete();
            engine.TickControllerAi();
        }
    }

    /// <summary>Ticks until <paramref name="until"/> holds for the aircraft or the budget runs out (then fails).</summary>
    public static AircraftState TickUntil(SimulationEngine engine, string callsign, Func<AircraftState, bool> until, int budgetSeconds)
    {
        for (int i = 0; i < budgetSeconds; i++)
        {
            var aircraft = engine.FindAircraft(callsign) ?? throw new InvalidOperationException($"{callsign} left the world");
            if (until(aircraft))
            {
                return aircraft;
            }

            Tick(engine, 1);
        }

        var last = engine.FindAircraft(callsign);
        throw new InvalidOperationException(
            $"{callsign} never reached the expected state within {budgetSeconds}s (phase {last?.Phases?.CurrentPhase?.Name ?? "none"})"
        );
    }

    /// <summary>A hand-built AI tick context over an explicit aircraft list at an arbitrary sim time — for rule tests that never tick physics.</summary>
    public static AiTickContext Context(
        SimulationEngine engine,
        IReadOnlyList<AircraftState> aircraft,
        IReadOnlyList<AiPositionConfig> staffed,
        double now,
        IReadOnlyList<ActiveConflict> conflicts,
        IAiCommandSink sink
    )
    {
        var scenario = engine.Scenario!;
        var staffing = new HeadlessAiStaffing(staffed, scenario);
        var view = AiWorldView.Build(
            aircraft,
            staffing.ActivePositions,
            engine.ResolveGroundLayout,
            RunwayOccupancy.AirportRunways,
            staffing.IsHumanHeld,
            staffing.IsAssignedToHuman
        );
        return new AiTickContext
        {
            Snapshot = view.Snapshot,
            View = view,
            Scenario = scenario,
            World = engine.World,
            Sink = sink,
            Staffing = staffing,
            AiRng = new SerializableRandom(1),
            ElapsedSeconds = now,
            Anomalies = scenario.AiAnomalies,
            Outcomes = [],
            Weather = engine.World.Weather,
            ActiveConflicts = conflicts,
            EramConflicts = [],
            AutoAcceptDelaySeconds = scenario.AutoAcceptDelay.TotalSeconds,
            LayoutFor = engine.ResolveGroundLayout,
            RunwaysFor = RunwayOccupancy.AirportRunways,
            RunwayInUse = new RunwayInUseState(),
        };
    }

    public static AircraftState Airborne(string callsign, double lat, double lon, double altitude) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            Altitude = altitude,
            IsOnGround = false,
            AirportId = "OAK",
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "IFR",
                Departure = "KSFO",
                Destination = "KOAK",
            },
        };
}
