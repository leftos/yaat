using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// GitHub issue #191: with command-run delay enabled, a MIA ground aircraft
/// remained in the aircraft list as Holding Short after the controller tried
/// to clear it across runway 08R.
///
/// Recording fixture: T1: S2. Practical Exam (MIA East). At t=180, ENY3516
/// is holding short of 08R/26L at L1 with a completed route and only
/// HoldingInPosition pending. The issue-specific regression verifies that
/// CROSS 08R alone creates the crossing movement after the recorded-style
/// delayed execution. A companion focused route test documents that RES alone
/// resumes a held taxi route when more route remains.
/// </summary>
public sealed class Issue191CrossRunwayCommandTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/c000c2b0afc8.zip";
    private const string RecordedCallsign = "ENY3516";
    private const string FocusedCallsign = "ENY191";
    private const string CrossRunway = "08R";
    private const int M1StartNodeId = 286;
    private const int M1RunwayHoldShortNodeId = 533;
    private const int ReactionDelaySeed = 421926535;

    [Fact]
    public void CrossRunway_WithRecordedCompletedRouteAtRunwayHold_CrossesWithoutResume()
    {
        var restored = RestoreAt(180);
        if (restored is null)
        {
            return;
        }

        using var archive = restored.Value.Archive;
        var engine = restored.Value.Engine;
        var layout = restored.Value.Layout;
        var aircraft = AssertRecordedCompletedRunwayHold(engine);

        EnableRecordedCommandDelay(engine);
        var result = engine.SendCommand(RecordedCallsign, $"CROSS {CrossRunway}");
        output.WriteLine($"CROSS {CrossRunway}: success={result.Success} message={result.Message}");

        Assert.True(result.Success, $"CROSS {CrossRunway} should be accepted at the current hold-short: {result.Message}");
        var delay = Assert.Single(aircraft.DeferredDispatches);
        Assert.True(delay.IsReactionDelay);
        Assert.InRange(delay.RemainingSeconds, 3, 8);

        var observation = TickAndObserve(engine, layout, RecordedCallsign, aircraft.Position, maxSeconds: 30);

        Assert.True(observation.LeftInitialHold, $"{RecordedCallsign} should leave HoldingShort after delayed CROSS {CrossRunway}");
        Assert.True(observation.SawCrossing, $"{RecordedCallsign} should enter CrossingRunwayPhase from the completed hold-short route");
    }

    [Fact]
    public void Resume_WithPendingRouteAtRunwayHold_ContinuesRemainingTaxiRoute()
    {
        var restored = RestoreAt(0);
        if (restored is null)
        {
            return;
        }

        using var archive = restored.Value.Archive;
        var engine = restored.Value.Engine;
        var layout = restored.Value.Layout;
        var holdPhase = TaxiFocusedAircraftToM1Hold(engine, layout);

        EnableRecordedCommandDelay(engine);
        var result = engine.SendCommand(FocusedCallsign, "RES");
        output.WriteLine($"RES: success={result.Success} message={result.Message}");

        Assert.True(result.Success, $"RES should clear the current hold and resume the pending taxi route: {result.Message}");
        var delay = Assert.Single(engine.FindAircraft(FocusedCallsign)!.DeferredDispatches);
        Assert.True(delay.IsReactionDelay);
        Assert.InRange(delay.RemainingSeconds, 3, 8);

        var observation = TickAndObserve(engine, layout, FocusedCallsign, holdPhase.HoldShort.NodeId, maxSeconds: 30);

        Assert.True(observation.LeftInitialHold, $"{FocusedCallsign} should leave the current runway hold after delayed RES");
        Assert.True(observation.SawCrossing, $"{FocusedCallsign} should continue the remaining taxi route through CrossingRunwayPhase");
    }

    private (SimulationEngine Engine, AirportGroundLayout Layout, RecordingArchive Archive)? RestoreAt(double targetSeconds)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return null;
        }

        var snapshot = archive.ReadSnapshotAt(targetSeconds);
        if (snapshot is null)
        {
            archive.Dispose();
            return null;
        }

        var layout = archive.ReadLayout("mia");
        var engine = BuildEngine(layout);
        var recording = archive.ToBaseSessionRecording();
        engine.Replay(recording, 0);
        engine.RestoreFromSnapshot(snapshot.State);
        return (engine, layout, archive);
    }

    private SimulationEngine BuildEngine(AirportGroundLayout layout)
    {
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("SimulationEngine", LogLevel.Debug)
            .EnableCategory("CommandDispatcher", LogLevel.Debug)
            .EnableCategory("GroundCommandHandler", LogLevel.Debug)
            .EnableCategory("HoldingShortPhase", LogLevel.Debug)
            .EnableCategory("CrossingRunwayPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(new SingleLayoutGroundData(layout));
    }

    private static void EnableRecordedCommandDelay(SimulationEngine engine)
    {
        Assert.NotNull(engine.Scenario);
        engine.Scenario.CommandRunDelayMinSeconds = 3;
        engine.Scenario.CommandRunDelayMaxSeconds = 8;
        engine.World.ReactionDelayRng = new SerializableRandom(ReactionDelaySeed);
    }

    private AircraftState AssertRecordedCompletedRunwayHold(SimulationEngine engine)
    {
        var aircraft = engine.FindAircraft(RecordedCallsign);
        Assert.NotNull(aircraft);

        var holdPhase = Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal(HoldShortReason.RunwayCrossing, holdPhase.HoldShort.Reason);
        Assert.True(RunwayIdentifier.Parse(holdPhase.HoldShort.TargetName!).Contains(CrossRunway));

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.True(route.IsComplete, "Recorded precondition: ENY3516's route completed at the runway hold-short");
        Assert.DoesNotContain(aircraft.Phases!.Phases.Skip(aircraft.Phases.CurrentIndex + 1), p => p is CrossingRunwayPhase);
        Assert.Contains(aircraft.Phases.Phases.Skip(aircraft.Phases.CurrentIndex + 1), p => p is HoldingInPositionPhase);

        return aircraft;
    }

    private HoldingShortPhase TaxiFocusedAircraftToM1Hold(SimulationEngine engine, AirportGroundLayout layout)
    {
        var aircraft = SpawnFocusedAircraft(layout);
        engine.World.AddAircraft(aircraft);

        var result = engine.SendCommand(FocusedCallsign, "TAXI M1 RWY08R/26L L1");
        output.WriteLine($"TAXI M1 RWY08R/26L L1: success={result.Success} message={result.Message}");
        Assert.True(result.Success, $"TAXI route to the 08R crossing should resolve: {result.Message}");

        bool reachedHold = TickUntil(
            engine,
            FocusedCallsign,
            maxSeconds: 120,
            ac =>
                ac.Phases?.CurrentPhase is HoldingShortPhase hs
                && hs.HoldShort.NodeId == M1RunwayHoldShortNodeId
                && hs.HoldShort.TargetName is not null
                && RunwayIdentifier.Parse(hs.HoldShort.TargetName).Contains(CrossRunway)
        );

        Assert.True(reachedHold, $"{FocusedCallsign} should reach the first 08R/26L hold-short on M1");
        var holdPhase = Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.False(aircraft.Ground.AssignedTaxiRoute?.IsComplete ?? true, "Focused precondition: route should continue beyond the hold-short");
        return holdPhase;
    }

    private static AircraftState SpawnFocusedAircraft(AirportGroundLayout layout)
    {
        var start = layout.Nodes[M1StartNodeId];
        var holdShort = layout.Nodes[M1RunwayHoldShortNodeId];
        var heading = GeoMath.BearingTo(start.Position, holdShort.Position);
        var aircraft = new AircraftState
        {
            Callsign = FocusedCallsign,
            AircraftType = "E75L",
            AirportId = "MIA",
            Position = start.Position,
            TrueHeading = new TrueHeading(heading),
            Altitude = 9,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KMIA",
                Destination = "KMIA",
                FlightRules = "IFR",
            },
        };

        aircraft.Ground.Layout = layout;
        aircraft.Ground.CurrentTaxiway = "M1";
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        return aircraft;
    }

    private (bool LeftInitialHold, bool SawCrossing) TickAndObserve(
        SimulationEngine engine,
        AirportGroundLayout layout,
        string callsign,
        LatLon initialPosition,
        int maxSeconds
    )
    {
        bool leftInitialHold = false;
        bool sawCrossing = false;
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            var aircraft = engine.FindAircraft(callsign);
            Assert.NotNull(aircraft);

            var phase = aircraft.Phases?.CurrentPhase;
            sawCrossing |= phase is CrossingRunwayPhase;
            leftInitialHold |= phase is not HoldingShortPhase || GeoMath.DistanceNm(initialPosition, aircraft.Position) > 0.005;

            NearestNodeHelper.Log(output, $"dt={t} phase={phase?.GetType().Name ?? "null"}", aircraft, layout);
        }

        return (leftInitialHold, sawCrossing);
    }

    private (bool LeftInitialHold, bool SawCrossing) TickAndObserve(
        SimulationEngine engine,
        AirportGroundLayout layout,
        string callsign,
        int initialHoldNodeId,
        int maxSeconds
    )
    {
        bool leftInitialHold = false;
        bool sawCrossing = false;
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            var aircraft = engine.FindAircraft(callsign);
            Assert.NotNull(aircraft);

            var phase = aircraft.Phases?.CurrentPhase;
            sawCrossing |= phase is CrossingRunwayPhase;
            leftInitialHold |= phase is not HoldingShortPhase hs || hs.HoldShort.NodeId != initialHoldNodeId;

            NearestNodeHelper.Log(output, $"dt={t} phase={phase?.GetType().Name ?? "null"}", aircraft, layout);
        }

        return (leftInitialHold, sawCrossing);
    }

    private static bool TickUntil(SimulationEngine engine, string callsign, int maxSeconds, Func<AircraftState, bool> condition)
    {
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            var aircraft = engine.FindAircraft(callsign);
            if (aircraft is not null && condition(aircraft))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class SingleLayoutGroundData(AirportGroundLayout layout) : IAirportGroundData
    {
        public AirportGroundLayout? GetLayout(string airportId)
        {
            string shortId = airportId.Length == 4 && airportId[0] == 'K' ? airportId[1..] : airportId;
            return string.Equals(shortId, layout.AirportId, StringComparison.OrdinalIgnoreCase) ? layout : null;
        }

        public string? GetSourceGeoJson(string airportId) => null;
    }
}
