using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Captures the Warning-level and higher lines <see cref="SimLog"/> emits while it is installed as the test's log factory,
/// so a restore test can assert whether the navigator dropped the playback it was restored with. Lower levels are dropped.
/// </summary>
internal sealed class WarningLogCapture : ILoggerProvider, ILogger
{
    /// <summary>The words of the navigator's warning when a restored playback does not belong to the segment set up.</summary>
    internal const string PlaybackDropped = "restored playback dropped";

    private readonly List<string> _warnings = [];

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Installs a fresh capture as the SimLog factory for the current test and returns it.</summary>
    public static WarningLogCapture Install()
    {
        var capture = new WarningLogCapture();
        SimLog.InitializeForTest(LoggerFactory.Create(builder => builder.AddProvider(capture).SetMinimumLevel(LogLevel.Warning)));
        return capture;
    }

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            _warnings.Add(formatter(state, exception));
        }
    }

    public void Dispose() { }
}

/// <summary>
/// A taxi restored from a snapshot taken in the middle of the navigator's slow turn resumes that turn at the progress it
/// had reached, rather than starting a new one from where the aircraft stands, and so goes on exactly as the run that
/// was never interrupted.
/// </summary>
public class GroundNavigatorArcRestoreTests(ITestOutputHelper output)
{
    private const string Callsign = "N346G";
    private const string AircraftType = "C560";
    private const string AirportId = "OAK";
    private const int TurnBudgetSeconds = 60;
    private const int SnapshotCompareSeconds = 30;
    private const double MinTurnedDeg = 10.0;
    private const string ArrivalCallsign = "TSTAC";
    private const int ExitBudgetSeconds = 400;
    private const double MovingKts = 1.0;

    /// <summary>
    /// A C560 standing on KOAK taxiway B square across the taxiway is cleared <c>TAXI B W 30</c>: the taxi opens with an
    /// entry-alignment slow turn onto B. A snapshot taken part-way round that turn, restored into a second engine, keeps
    /// the restored aircraft on the original's positions every second.
    /// </summary>
    [Fact]
    public void Taxi_SurvivesSnapshotRoundTripMidTurn()
    {
        var groundData = new TestAirportGroundData();
        if (TaxiCaughtMidTurn(groundData) is not { } engine)
        {
            return;
        }

        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
        StateSnapshotDto snapshot = RoundTrip(engine.CaptureSnapshot());
        SimulationEngine restoredEngine = Assert.IsType<SimulationEngine>(NewEngine(groundData));
        restoredEngine.RestoreFromSnapshot(snapshot);
        AircraftState restored = Assert.IsType<AircraftState>(restoredEngine.FindAircraft(Callsign));

        for (int second = 1; second <= SnapshotCompareSeconds; second++)
        {
            engine.TickOneSecond();
            restoredEngine.TickOneSecond();
            Assert.Equal(aircraft.Phases?.CurrentPhase?.Name, restored.Phases?.CurrentPhase?.Name);
            Assert.Equal(aircraft.Position, restored.Position);
        }

        output.WriteLine($"after {SnapshotCompareSeconds}s: {aircraft.Phases?.CurrentPhase?.Name} at {aircraft.Position}");
    }

    /// <summary>
    /// The same mid-turn snapshot with the saved playback's from-node changed to one the taxi segment does not start at:
    /// the to-node still matches, but the playback was not saved on this segment, so the restore drops it with a
    /// "restored playback dropped" warning and sets the
    /// segment up afresh from where the aircraft stands, and the restored aircraft leaves the original's path.
    /// </summary>
    [Fact]
    public void Taxi_ResumeWithAMismatchedFromNode_IsDropped()
    {
        var groundData = new TestAirportGroundData();
        if (TaxiCaughtMidTurn(groundData) is not { } engine)
        {
            return;
        }

        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
        int segmentFromNodeId = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute).CurrentSegment!.FromNodeId;
        JsonNode json = JsonSerializer.SerializeToNode(engine.CaptureSnapshot(), RecordingJsonOptions.Default)!;
        JsonObject playback = Assert.Single(PlaybackObjects(json));
        playback["FromNodeId"] = segmentFromNodeId + MismatchedNodeIdOffset;
        StateSnapshotDto snapshot = Assert.IsType<StateSnapshotDto>(json.Deserialize<StateSnapshotDto>(RecordingJsonOptions.Default));

        SimulationEngine restoredEngine = Assert.IsType<SimulationEngine>(NewEngine(groundData));
        var warnings = WarningLogCapture.Install();
        restoredEngine.RestoreFromSnapshot(snapshot);
        AircraftState restored = Assert.IsType<AircraftState>(restoredEngine.FindAircraft(Callsign));

        bool diverged = false;
        for (int second = 1; (second <= SnapshotCompareSeconds) && !diverged; second++)
        {
            engine.TickOneSecond();
            restoredEngine.TickOneSecond();
            diverged = aircraft.Position != restored.Position;
        }

        Assert.True(diverged, "the restore resumed a playback saved with a different from-node: it tracked the original exactly");
        Assert.Contains(warnings.Warnings, w => w.Contains(WarningLogCapture.PlaybackDropped, StringComparison.Ordinal));
    }

    private const int MismatchedNodeIdOffset = 100_000;

    /// <summary>Every non-null <c>Playback</c> object anywhere in a snapshot's JSON.</summary>
    internal static IEnumerable<JsonObject> PlaybackObjects(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach ((string name, JsonNode? child) in obj)
            {
                if ((name == "Playback") && (child is JsonObject playback))
                {
                    yield return playback;
                }

                foreach (JsonObject found in PlaybackObjects(child))
                {
                    yield return found;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
            {
                foreach (JsonObject found in PlaybackObjects(child))
                {
                    yield return found;
                }
            }
        }
    }

    private static StateSnapshotDto RoundTrip(StateSnapshotDto snapshot) =>
        Assert.IsType<StateSnapshotDto>(
            JsonSerializer.Deserialize<StateSnapshotDto>(
                JsonSerializer.Serialize(snapshot, RecordingJsonOptions.Default),
                RecordingJsonOptions.Default
            )
        );

    /// <summary>
    /// An engine whose C560, standing on KOAK taxiway B square across it and cleared <c>TAXI B W 30</c>, is part-way round
    /// the entry-alignment slow turn onto B; null when navdata or the KOAK layout is unavailable.
    /// </summary>
    private SimulationEngine? TaxiCaughtMidTurn(TestAirportGroundData groundData)
    {
        if ((NewEngine(groundData) is not { } engine) || (groundData.GetLayout(AirportId) is not { } layout))
        {
            output.WriteLine("SKIP: navdata or KOAK layout unavailable");
            return null;
        }

        // Stand on B short of the 28R bar, on the side away from 28L, facing square across the taxiway.
        List<GroundNode> bar28R = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28R", "B");
        List<GroundNode> bar28L = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28L", "B");
        GroundNode nearBar28R = bar28R.OrderByDescending(node => GeoMath.DistanceNm(node.Position, bar28L[0].Position)).First();
        GroundNode start = nearBar28R
            .Edges.Where(edge => string.Equals(edge.TaxiwayName, "B", StringComparison.OrdinalIgnoreCase))
            .Select(edge => edge.OtherNode(nearBar28R))
            .OrderByDescending(node => GeoMath.DistanceNm(node.Position, bar28L[0].Position))
            .First();
        var startHeading = new TrueHeading(GeoMath.BearingTo(start.Position, nearBar28R.Position) + 90.0);

        AircraftState aircraft = Spawn(start, startHeading, layout);
        engine.World.AddAircraft(aircraft);
        CommandResult taxi = engine.SendCommand(Callsign, "TAXI B W 30");
        output.WriteLine($"TAXI B W 30 -> success={taxi.Success} msg={taxi.Message}");
        Assert.True(taxi.Success, taxi.Message);

        int midTurn = -1;
        for (int second = 1; second <= TurnBudgetSeconds; second++)
        {
            engine.TickOneSecond();
            if (IsMidSlowTurn(aircraft, startHeading))
            {
                midTurn = second;
                break;
            }
        }

        Assert.True(midTurn > 0, $"never caught mid slow turn within {TurnBudgetSeconds}s: {aircraft.Phases?.CurrentPhase?.Name}");
        output.WriteLine($"mid turn at t={midTurn}s: hdg={aircraft.TrueHeading.Degrees:F1} gs={aircraft.GroundSpeed:F1} at {aircraft.Position}");
        return engine;
    }

    /// <summary>
    /// A B738 cleared to land on KOAK 30 rolls out and turns off on its exit. A snapshot taken while the runway-exit
    /// navigator is playing the exit's curve, restored into a second engine, keeps the restored aircraft on the original's
    /// positions every second.
    /// </summary>
    [Fact]
    public void RunwayExit_SurvivesSnapshotRoundTripMidCurve()
    {
        var groundData = new TestAirportGroundData();
        if ((NewEngine(groundData) is not { } engine) || (groundData.GetLayout(AirportId) is not { } layout))
        {
            output.WriteLine("SKIP: navdata or KOAK layout unavailable");
            return;
        }

        RunwayInfo runway = Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway(AirportId, "30"));
        AircraftState aircraft = SpawnOnShortFinal(runway, layout);
        engine.World.AddAircraft(aircraft);
        CommandResult land = engine.SendCommand(ArrivalCallsign, "CLAND");
        output.WriteLine($"CLAND -> success={land.Success} msg={land.Message}");
        Assert.True(land.Success, land.Message);

        int midCurve = -1;
        for (int second = 1; second <= ExitBudgetSeconds; second++)
        {
            engine.TickOneSecond();
            if (IsMidExitCurve(aircraft))
            {
                midCurve = second;
                break;
            }
        }

        Assert.True(midCurve > 0, $"never caught on the exit curve within {ExitBudgetSeconds}s: {aircraft.Phases?.CurrentPhase?.Name}");
        output.WriteLine(
            $"mid exit curve at t={midCurve}s: hdg={aircraft.TrueHeading.Degrees:F1} gs={aircraft.GroundSpeed:F1} at {aircraft.Position}"
        );

        StateSnapshotDto snapshot = RoundTrip(engine.CaptureSnapshot());
        SimulationEngine restoredEngine = Assert.IsType<SimulationEngine>(NewEngine(groundData));
        restoredEngine.RestoreFromSnapshot(snapshot);
        AircraftState restored = Assert.IsType<AircraftState>(restoredEngine.FindAircraft(ArrivalCallsign));

        for (int second = 1; second <= SnapshotCompareSeconds; second++)
        {
            engine.TickOneSecond();
            restoredEngine.TickOneSecond();
            Assert.Equal(aircraft.Phases?.CurrentPhase?.Name, restored.Phases?.CurrentPhase?.Name);
            Assert.Equal(aircraft.Position, restored.Position);
        }

        output.WriteLine($"after {SnapshotCompareSeconds}s: {aircraft.Phases?.CurrentPhase?.Name} at {aircraft.Position}");
    }

    /// <summary>
    /// Whether the runway exit is part-way round its curve: the turn off the centreline has begun, the aircraft is off the
    /// centreline and moving, and the navigator played an arc on the last tick.
    /// </summary>
    private static bool IsMidExitCurve(AircraftState aircraft) =>
        (aircraft.Phases?.CurrentPhase is RunwayExitPhase { TurnStarted: true, IsOnCenterline: false })
        && (aircraft.Ground.LastNavDiag is { OnArc: true })
        && (aircraft.GroundSpeed > MovingKts);

    private static AircraftState SpawnOnShortFinal(RunwayInfo runway, AirportGroundLayout layout)
    {
        double reciprocal = (runway.TrueHeading.Degrees + 180) % 360;
        (double lat, double lon) = GeoMath.ProjectPointRaw(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, 1.0);
        var aircraft = new AircraftState
        {
            Callsign = ArrivalCallsign,
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt + 318,
            IndicatedAirspeed = 130,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = AirportId,
                Destination = AirportId,
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
            Phases = new PhaseList { AssignedRunway = runway },
        };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        aircraft.Ground.Layout = layout;
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        return aircraft;
    }

    /// <summary>
    /// Whether the taxi is part-way round a slow turn: the navigator is playing an arc on a straight route segment (so the
    /// arc is a synthesised slow turn, not a painted fillet), and the aircraft has turned off its starting heading but is
    /// not yet aligned with the segment.
    /// </summary>
    private static bool IsMidSlowTurn(AircraftState aircraft, TrueHeading startHeading) =>
        (aircraft.Phases?.CurrentPhase is TaxiingPhase)
        && (aircraft.Ground.LastNavDiag is { OnArc: true })
        && (aircraft.Ground.AssignedTaxiRoute?.CurrentSegment is { } segment)
        && (segment.Edge.Edge is not GroundArc)
        && (startHeading.AbsAngleTo(aircraft.TrueHeading) > MinTurnedDeg)
        && (new TrueHeading(segment.Edge.DepartureBearing).AbsAngleTo(aircraft.TrueHeading) > MinTurnedDeg);

    private SimulationEngine? NewEngine(TestAirportGroundData groundData)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-oak-navigator-arc-restore",
                ScenarioName = "OAK navigator arc restore",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = AirportId,
                AutoCrossRunway = false,
            },
        };
    }

    private static AircraftState Spawn(GroundNode node, TrueHeading heading, AirportGroundLayout layout)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = AircraftType,
            Position = node.Position,
            TrueHeading = heading,
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = AirportId,
                Destination = AirportId,
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(1500),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }
}
