using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.LiveTraffic;

/// <summary>
/// The live-traffic sync step on a bare engine: <see cref="SimulationEngine.TickLiveTrafficSync"/> asks the feed port what
/// is in scope this second and decides spawn vs refresh, the callsign collision with a simulated aircraft, the filter
/// gate and the three removal tiers itself, reading the engine-held <see cref="AircraftLiveTraffic.AppliedAtSimSeconds"/>.
/// The port here is a boundary fake standing in for the server's feed; what the step decided reaches the host through
/// the consumer view, captured by <see cref="SpineCapturingHost"/>.
/// </summary>
public class LiveTrafficSyncStepTests
{
    private static readonly LatLon Origin = new(37.0, -122.0);

    public LiveTrafficSyncStepTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static LiveTrafficSample Sample(double observedAt, LatLon position) =>
        new(observedAt, position.Lat, position.Lon, 8_000, 240, 90, -500, LiveTrafficSource.Stars, 4521);

    private static SimulationEngine BareEngine(params AircraftState[] aircraft)
    {
        var engine = new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test",
                ScenarioName = "Test",
                RngSeed = 1,
                OriginalScenarioJson = "{}",
                LiveTrafficEnabled = true,
            },
        };
        foreach (AircraftState ac in aircraft)
        {
            engine.World.AddAircraft(ac);
        }

        return engine;
    }

    private static AircraftState Simulated(string callsign) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = Origin,
            Altitude = 8_000,
            IndicatedAirspeed = 240,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                Departure = "KOAK",
                Destination = "KLAX",
            },
        };

    /// <summary>Runs the step at sim second <paramref name="second"/>, the way the spine does in pre-physics.</summary>
    private static void SyncAt(SimulationEngine engine, FakeFeedPort port, SpineCapturingHost host, double second)
    {
        engine.Scenario!.ElapsedSeconds = second;
        engine.TickLiveTrafficSync(port, host);
    }

    /// <summary>Spawns <paramref name="callsign"/> at second 1 and clears the port, so later seconds start from one shadow.</summary>
    private static void SpawnAtSecondOne(SimulationEngine engine, FakeFeedPort port, SpineCapturingHost host, string callsign)
    {
        port.Offer(callsign, Sample(1, Origin), matchesFilter: true);
        SyncAt(engine, port, host, 1);
        Assert.True(engine.World.FindAircraft(callsign)!.IsShadow);
        port.Tracks.Clear();
    }

    [Fact]
    public void ANewTrack_SpawnsAShadow_RecordsTheSpawn_AndTellsTheHost()
    {
        SimulationEngine engine = BareEngine();
        var port = new FakeFeedPort();
        var host = new SpineCapturingHost(engine);
        port.Offer("UAL1", Sample(1, Origin), matchesFilter: true);

        SyncAt(engine, port, host, 1);

        AircraftState shadow = engine.World.FindAircraft("UAL1")!;
        Assert.True(shadow.IsShadow);
        Assert.Equal(1, shadow.LiveTraffic!.AppliedAtSimSeconds);
        (AircraftState spawned, LiveTrafficSource source) = Assert.Single(host.LiveTrafficSpawns);
        Assert.Same(shadow, spawned);
        Assert.Equal(LiveTrafficSource.Stars, source);
        RecordedLiveTrafficSample recorded = Assert.Single(engine.Scenario!.ActionLog.OfType<RecordedLiveTrafficSample>());
        Assert.NotNull(recorded.SpawnState);
        Assert.Equal(1, port.SpawnStatesBuilt);
        Assert.Equal(1, port.SecondsEnded);
    }

    [Fact]
    public void AnExistingShadow_IsRefreshed_WithoutASpawnOrASpawnState()
    {
        SimulationEngine engine = BareEngine();
        var port = new FakeFeedPort();
        var host = new SpineCapturingHost(engine);
        SpawnAtSecondOne(engine, port, host, "UAL1");

        LatLon moved = GeoMath.ProjectPoint(Origin, new TrueHeading(90), 2);
        port.Offer("UAL1", Sample(2, moved), matchesFilter: true);
        SyncAt(engine, port, host, 2);

        AircraftState shadow = engine.World.FindAircraft("UAL1")!;
        Assert.Equal(2, shadow.LiveTraffic!.AppliedAtSimSeconds);
        Assert.Single(host.LiveTrafficSpawns);
        Assert.Equal(1, port.SpawnStatesBuilt);
        var samples = engine.Scenario!.ActionLog.OfType<RecordedLiveTrafficSample>().ToList();
        Assert.Equal(2, samples.Count);
        Assert.Null(samples[1].SpawnState);
        Assert.Empty(host.LiveTrafficRemovals);
    }

    [Fact]
    public void ATrackTheFeedEnded_IsRemovedAfterOneSecondOfGrace()
    {
        SimulationEngine engine = BareEngine();
        var port = new FakeFeedPort();
        var host = new SpineCapturingHost(engine);
        SpawnAtSecondOne(engine, port, host, "UAL1");
        port.Statuses["UAL1"] = LiveTrafficShadowStatus.Ended;

        SyncAt(engine, port, host, 2);
        Assert.NotNull(engine.World.FindAircraft("UAL1"));

        SyncAt(engine, port, host, 3);

        Assert.Null(engine.World.FindAircraft("UAL1"));
        (AircraftState removed, LiveTrafficRemovalReason reason) = Assert.Single(host.LiveTrafficRemovals);
        Assert.Equal("UAL1", removed.Callsign);
        Assert.Equal(LiveTrafficRemovalReason.Dropped, reason);
        RecordedLiveTrafficRemoval recorded = Assert.Single(engine.Scenario!.ActionLog.OfType<RecordedLiveTrafficRemoval>());
        Assert.Equal(LiveTrafficRemovalReason.Dropped, recorded.Reason);
    }

    [Fact]
    public void AShadowTheFeedPlacesOutOfScope_IsRemovedOncePast15Seconds()
    {
        SimulationEngine engine = BareEngine();
        var port = new FakeFeedPort();
        var host = new SpineCapturingHost(engine);
        SpawnAtSecondOne(engine, port, host, "UAL1");
        port.Statuses["UAL1"] = LiveTrafficShadowStatus.OutOfScope;

        SyncAt(engine, port, host, 16);
        Assert.NotNull(engine.World.FindAircraft("UAL1"));

        SyncAt(engine, port, host, 17);

        Assert.Null(engine.World.FindAircraft("UAL1"));
        Assert.Equal(LiveTrafficRemovalReason.OutOfScope, Assert.Single(host.LiveTrafficRemovals).Reason);
    }

    [Theory]
    [InlineData(LiveTrafficShadowStatus.Present, LiveTrafficRemovalReason.Stale)]
    [InlineData(LiveTrafficShadowStatus.Absent, LiveTrafficRemovalReason.Dropped)]
    public void ASilentShadow_IsRemovedOncePastItsSourcesBackstop(LiveTrafficShadowStatus status, LiveTrafficRemovalReason expected)
    {
        SimulationEngine engine = BareEngine();
        var port = new FakeFeedPort();
        var host = new SpineCapturingHost(engine);
        SpawnAtSecondOne(engine, port, host, "UAL1");
        port.Statuses["UAL1"] = status;
        double backstop = LiveTrafficKinematics.RemovalAfterSeconds(LiveTrafficSource.Stars);

        SyncAt(engine, port, host, 1 + backstop);
        Assert.NotNull(engine.World.FindAircraft("UAL1"));

        SyncAt(engine, port, host, 2 + backstop);

        Assert.Null(engine.World.FindAircraft("UAL1"));
        Assert.Equal(expected, Assert.Single(host.LiveTrafficRemovals).Reason);
    }

    [Fact]
    public void TheFilter_NeverSpawnsAnExcludedTrack_AndTearsDownAnExcludedShadowWithOneReport()
    {
        SimulationEngine engine = BareEngine();
        var port = new FakeFeedPort();
        var host = new SpineCapturingHost(engine);
        SpawnAtSecondOne(engine, port, host, "UAL1");

        port.Offer("DAL2", Sample(3, Origin), matchesFilter: false);
        port.Statuses["UAL1"] = LiveTrafficShadowStatus.FilteredOut;
        SyncAt(engine, port, host, 3);

        Assert.Null(engine.World.FindAircraft("DAL2"));
        Assert.Null(engine.World.FindAircraft("UAL1"));
        Assert.Equal(1, port.SpawnStatesBuilt);
        Assert.Equal(LiveTrafficRemovalReason.Filtered, Assert.Single(host.LiveTrafficRemovals).Reason);
        Assert.Equal([1], host.LiveTrafficFilteredOut);
    }

    [Fact]
    public void ACallsignHeldByASimulatedAircraft_IsNotSpawned_AndTheHostHearsOfTheCollision()
    {
        SimulationEngine engine = BareEngine(Simulated("AAL100"));
        var port = new FakeFeedPort();
        var host = new SpineCapturingHost(engine);
        port.Offer("AAL100", Sample(1, Origin), matchesFilter: true);

        SyncAt(engine, port, host, 1);

        Assert.False(engine.World.FindAircraft("AAL100")!.IsShadow);
        Assert.Equal(["AAL100"], host.LiveTrafficCallsignsInUse);
        Assert.Empty(host.LiveTrafficSpawns);
        Assert.Equal(0, port.SpawnStatesBuilt);
        Assert.Empty(engine.Scenario!.ActionLog.OfType<RecordedLiveTrafficSample>());
    }

    [Fact]
    public void ACallsignAssumedFromLiveTraffic_IsSkippedInSilence()
    {
        AircraftState assumed = Simulated("AAL100");
        assumed.AssumedFromLiveTraffic = true;
        SimulationEngine engine = BareEngine(assumed);
        var port = new FakeFeedPort();
        var host = new SpineCapturingHost(engine);
        port.Offer("AAL100", Sample(1, Origin), matchesFilter: true);

        SyncAt(engine, port, host, 1);

        Assert.False(engine.World.FindAircraft("AAL100")!.IsShadow);
        Assert.Empty(host.LiveTrafficCallsignsInUse);
        Assert.Empty(host.LiveTrafficSpawns);
        Assert.Empty(engine.Scenario!.ActionLog.OfType<RecordedLiveTrafficSample>());
    }

    [Fact]
    public void ASuppressedTrack_IsNeverSpawned()
    {
        SimulationEngine engine = BareEngine();
        var port = new FakeFeedPort();
        var host = new SpineCapturingHost(engine);
        engine.Scenario!.SuppressedLiveTraffic.Add("ual1");
        port.Offer("UAL1", Sample(1, Origin), matchesFilter: true);

        SyncAt(engine, port, host, 1);

        Assert.Null(engine.World.FindAircraft("UAL1"));
        Assert.Empty(host.LiveTrafficSpawns);
        Assert.Equal(0, port.SpawnStatesBuilt);
        Assert.Empty(engine.Scenario!.ActionLog.OfType<RecordedLiveTrafficSample>());
    }

    [Fact]
    public void ADisabledSecond_ClearsTheSuppressedSet()
    {
        SimulationEngine engine = BareEngine();
        engine.Scenario!.LiveTrafficEnabled = false;
        engine.Scenario!.SuppressedLiveTraffic.Add("UAL1");
        var port = new FakeFeedPort { Syncs = false };
        var host = new SpineCapturingHost(engine);

        SyncAt(engine, port, host, 1);

        Assert.Empty(engine.Scenario!.SuppressedLiveTraffic);
    }

    [Fact]
    public void AnEnabledSecond_KeepsTheSuppressedSet()
    {
        SimulationEngine engine = BareEngine();
        engine.Scenario!.SuppressedLiveTraffic.Add("UAL1");
        var port = new FakeFeedPort();
        var host = new SpineCapturingHost(engine);

        SyncAt(engine, port, host, 1);

        Assert.Equal(["UAL1"], engine.Scenario!.SuppressedLiveTraffic);
    }

    /// <summary>
    /// <c>DEL</c> hides a shadow in the scenario's own state, so a snapshot carries it: restored into a fresh engine,
    /// through the serializer a recording writes the snapshot with, the next feed second that offers the track spawns
    /// nothing.
    /// </summary>
    [Fact]
    public void Suppressed_RoundTripsThroughASnapshot()
    {
        SimulationEngine engine = BareEngine();
        var port = new FakeFeedPort();
        var host = new SpineCapturingHost(engine);
        SpawnAtSecondOne(engine, port, host, "UAL1");
        CommandResult deleted = engine.SendCommand("UAL1", "DEL");
        Assert.True(deleted.Success, deleted.Message);
        Assert.Null(engine.World.FindAircraft("UAL1"));

        StateSnapshotDto snapshot = engine.CaptureSnapshot();
        StateSnapshotDto reread = JsonSerializer.Deserialize<StateSnapshotDto>(JsonSerializer.Serialize(snapshot))!;
        Assert.Equal(["UAL1"], reread.Scenario.SuppressedLiveTraffic);

        SimulationEngine restored = BareEngine();
        restored.RestoreFromSnapshot(reread);
        var restoredPort = new FakeFeedPort();
        var restoredHost = new SpineCapturingHost(restored);
        restoredPort.Offer("UAL1", Sample(2, Origin), matchesFilter: true);
        SyncAt(restored, restoredPort, restoredHost, 2);

        Assert.Null(restored.World.FindAircraft("UAL1"));
        Assert.Empty(restoredHost.LiveTrafficSpawns);
        Assert.Equal(0, restoredPort.SpawnStatesBuilt);
    }

    [Fact]
    public void AReanchoredSecond_ClearsEveryShadowBeforeTheTracks_SoTheSameCallsignRespawns_AndReportsOneReacquire()
    {
        SimulationEngine engine = BareEngine();
        var port = new FakeFeedPort();
        var host = new SpineCapturingHost(engine);
        SpawnAtSecondOne(engine, port, host, "UAL1");

        port.ClearShadows = LiveTrafficRemovalReason.Reanchored;
        port.Offer("UAL1", Sample(2, Origin), matchesFilter: true);
        SyncAt(engine, port, host, 2);

        Assert.True(engine.World.FindAircraft("UAL1")!.IsShadow);
        (AircraftState removed, LiveTrafficRemovalReason reason) = Assert.Single(host.LiveTrafficRemovals);
        Assert.Equal("UAL1", removed.Callsign);
        Assert.Equal(LiveTrafficRemovalReason.Reanchored, reason);
        Assert.Equal(2, host.LiveTrafficSpawns.Count);
        Assert.Equal([1], host.LiveTrafficReacquired);
        var actions = engine.Scenario!.ActionLog.Where(a => a is RecordedLiveTrafficSample or RecordedLiveTrafficRemoval).ToList();
        Assert.Equal(3, actions.Count);
        Assert.IsType<RecordedLiveTrafficRemoval>(actions[1]);
        Assert.NotNull(Assert.IsType<RecordedLiveTrafficSample>(actions[2]).SpawnState);
    }

    [Fact]
    public void ADisabledSecond_ClearsEveryShadow_AndSyncsNothingElse()
    {
        SimulationEngine engine = BareEngine();
        var port = new FakeFeedPort();
        var host = new SpineCapturingHost(engine);
        port.Offer("UAL1", Sample(1, Origin), matchesFilter: true);
        port.Offer("DAL2", Sample(1, Origin), matchesFilter: true);
        SyncAt(engine, port, host, 1);
        port.Tracks.Clear();
        int endedBefore = port.SecondsEnded;

        port.Syncs = false;
        port.ClearShadows = LiveTrafficRemovalReason.Disabled;
        SyncAt(engine, port, host, 2);

        Assert.DoesNotContain(engine.World.GetSnapshot(), ac => ac.IsShadow);
        Assert.Equal(2, host.LiveTrafficRemovals.Count);
        Assert.All(host.LiveTrafficRemovals, r => Assert.Equal(LiveTrafficRemovalReason.Disabled, r.Reason));
        Assert.Equal(endedBefore, port.SecondsEnded);
        Assert.Empty(host.LiveTrafficReacquired);
    }

    [Fact]
    public void AShadowAppliedLessThanTwoSecondsAgo_IsNeverAskedForItsStatus()
    {
        SimulationEngine engine = BareEngine();
        var port = new FakeFeedPort();
        var host = new SpineCapturingHost(engine);
        SpawnAtSecondOne(engine, port, host, "UAL1");

        SyncAt(engine, port, host, 2);
        port.Offer("UAL1", Sample(3, Origin), matchesFilter: true);
        SyncAt(engine, port, host, 3);
        port.Tracks.Clear();
        SyncAt(engine, port, host, 4);
        Assert.Empty(port.StatusAsked);

        SyncAt(engine, port, host, 5);

        Assert.Equal(["UAL1"], port.StatusAsked);
    }

    /// <summary>
    /// The feed as the step sees it: this second's scoped tracks, each shadow's standing in the store, and a count of the
    /// spawn states the step asked for (it must ask only for a track it actually spawns).
    /// </summary>
    private sealed class FakeFeedPort : ILiveTrafficFeedPort
    {
        public List<LiveTrafficFeedTrack> Tracks { get; } = [];

        public Dictionary<string, LiveTrafficShadowStatus> Statuses { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Syncs { get; set; } = true;

        public LiveTrafficRemovalReason? ClearShadows { get; set; }

        /// <summary>Every callsign the step asked the status of, in order.</summary>
        public List<string> StatusAsked { get; } = [];

        public int SpawnStatesBuilt { get; private set; }

        public int SecondsEnded { get; private set; }

        public void Offer(string callsign, LiveTrafficSample sample, bool matchesFilter) =>
            Tracks.Add(new LiveTrafficFeedTrack(callsign, sample, matchesFilter, () => BuildSpawnState(callsign, sample)));

        public LiveTrafficFeedSecond BeginSecond()
        {
            LiveTrafficRemovalReason? clear = ClearShadows;
            ClearShadows = null;
            return new LiveTrafficFeedSecond(Syncs, clear, [.. Tracks]);
        }

        public LiveTrafficShadowStatus ShadowStatus(string callsign)
        {
            StatusAsked.Add(callsign);
            return Statuses.GetValueOrDefault(callsign, LiveTrafficShadowStatus.Present);
        }

        public void EndSecond() => SecondsEnded++;

        private AircraftSnapshotDto BuildSpawnState(string callsign, LiveTrafficSample sample)
        {
            SpawnStatesBuilt++;
            return LiveTrafficKinematics.CreateShadow(callsign, "B738", sample, new AircraftFlightPlan { HasFlightPlan = true }).ToSnapshot();
        }
    }
}
