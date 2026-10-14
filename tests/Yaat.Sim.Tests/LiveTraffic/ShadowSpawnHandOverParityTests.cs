using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Coast;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.LiveTraffic;

/// <summary>
/// A live-traffic shadow skips the spawn hooks on every path: the feed's spawn and a recorded sample's spawn on replay
/// both hand the shadow to the host through <c>OnLiveTrafficSpawned</c>, never through
/// <see cref="Yaat.Sim.Simulation.Actions.IActionHost.OnAircraftSpawned"/> and never through
/// <see cref="SimulationEngine.AfterAircraftSpawned"/>, and a replay from zero runs no spawn hooks for a shadow
/// already in the world.
/// </summary>
public class ShadowSpawnHandOverParityTests
{
    private const string Callsign = "UAL1";
    private const string ControlCallsign = "SWA1234";
    private static readonly LatLon Origin = new(37.72, -122.22);

    /// <summary>An IFR departure filed at KOAK, parked on the field: the simulated aircraft a spawn hook is for.</summary>
    private const string DepartureAtOak = """
        {
          "id": "shadow-parity",
          "name": "Shadow spawn parity at OAK",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "SWA1234",
              "aircraftType": "B738",
              "transponderMode": "C",
              "startingConditions": {
                "type": "Coordinates",
                "coordinates": { "lat": 37.7213, "lon": -122.2208 },
                "altitude": 9,
                "heading": 290,
                "speed": 0
              },
              "flightplan": {
                "rules": "IFR",
                "departure": "KOAK",
                "destination": "KSFO",
                "cruiseAltitude": 35000,
                "cruiseSpeed": 250,
                "route": "",
                "remarks": "",
                "aircraftType": "B738"
              },
              "presetCommands": [],
              "spawnDelay": 0,
              "airportId": "OAK",
              "difficulty": "Easy"
            }
          ]
        }
        """;

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public ShadowSpawnHandOverParityTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static LiveTrafficSample Sample(double observedAt) =>
        new(observedAt, Origin.Lat, Origin.Lon, 3_000, 200, 300, 1_500, LiveTrafficSource.Stars, 4521);

    /// <summary>
    /// A shadow departing KOAK. On <see cref="OakTowerEngine"/>, where OAK is the primary airport with a TDLS
    /// configuration and a tower student, the spawn hooks would queue a PDC and print a strip for a simulated departure
    /// like it (<see cref="AssertOnlyTheControlGetsAPdcAndAStrip"/>).
    /// </summary>
    private static AircraftSnapshotDto DepartureSpawnState(LiveTrafficSample sample) =>
        LiveTrafficKinematics
            .CreateShadow(
                Callsign,
                "B738",
                sample,
                new AircraftFlightPlan
                {
                    HasFlightPlan = true,
                    Departure = "KOAK",
                    Destination = "KLAX",
                }
            )
            .ToSnapshot();

    private static SimulationEngine BareEngine() =>
        new(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test",
                ScenarioName = "Test",
                RngSeed = 1,
                OriginalScenarioJson = "{}",
                LiveTrafficEnabled = true,
                PrimaryAirportId = "OAK",
                ElapsedSeconds = 1,
            },
        };

    /// <summary>The host calls a spawn produced for <see cref="Callsign"/>: the live-traffic hand-overs and the aircraft spawns.</summary>
    private static (List<LiveTrafficSource> LiveTraffic, int AircraftSpawns) SpawnCalls(SpineCapturingHost host) =>
        (
            host.LiveTrafficSpawns.Where(s => s.Shadow.Callsign == Callsign).Select(s => s.Source).ToList(),
            host.AircraftSpawns.Count(a => a.Callsign == Callsign)
        );

    [Fact]
    public void ReplayedShadowSpawn_CallsOnLiveTrafficSpawned_NotOnAircraftSpawned()
    {
        SimulationEngine engine = BareEngine();
        var host = new SpineCapturingHost(engine);
        LiveTrafficSample sample = Sample(1);

        engine.Actions.ApplyRecorded(new RecordedLiveTrafficSample(1, Callsign, sample, DepartureSpawnState(sample)), host);

        AircraftState shadow = engine.World.FindAircraft(Callsign)!;
        Assert.True(shadow.IsShadow);
        (AircraftState spawned, LiveTrafficSource source) = Assert.Single(host.LiveTrafficSpawns);
        Assert.Same(shadow, spawned);
        Assert.Equal(LiveTrafficSource.Stars, source);
        Assert.Empty(host.AircraftSpawns);
    }

    /// <summary>
    /// The engine at OAK under the ZOA configuration, a tower student and OAK's TDLS configuration on it — where a spawn
    /// hook does queue a PDC and print a strip — holding <see cref="ControlCallsign"/>, a simulated KOAK departure the
    /// scenario load put in the world without the hooks. Null when the ZOA configuration is not available.
    /// </summary>
    private SimulationEngine? OakTowerEngine()
    {
        if (_zoa is null)
        {
            return null;
        }

        SimulationEngine engine = AiTestFixture.Load(DepartureAtOak, _zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
        scenario.StudentPosition = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "O");
        scenario.StudentPositionType = "TWR";
        scenario.LiveTrafficEnabled = true;
        engine.InitializeFromArtcc();
        return engine;
    }

    /// <summary>
    /// The positive control: the simulated departure's hooks run and make exactly one PDC and one strip, so the engine
    /// could have made them for the shadow too — and since these are the only ones, the shadow's spawn made none.
    /// </summary>
    private static void AssertOnlyTheControlGetsAPdcAndAStrip(SimulationEngine engine)
    {
        engine.AfterAircraftSpawned(engine.FindAircraft(ControlCallsign)!);

        Assert.Equal(ControlCallsign, Assert.Single(engine.Tdls.Items.Values).AircraftId);
        Assert.Equal(ControlCallsign, Assert.Single(engine.Strips.Items.Values).AircraftId);
    }

    [Fact]
    public void LiveAndReplayedShadowSpawn_CallTheSameHostMembers()
    {
        if ((OakTowerEngine() is not { } live) || (OakTowerEngine() is not { } replay))
        {
            return;
        }

        var liveHost = new SpineCapturingHost(live);
        var port = new OneTrackFeedPort(Callsign, Sample(0), DepartureSpawnState(Sample(0)));
        live.TickLiveTrafficSync(port, liveHost);
        RecordedLiveTrafficSample recorded = Assert.Single(live.Scenario!.ActionLog.OfType<RecordedLiveTrafficSample>());
        Assert.NotNull(recorded.SpawnState);

        var replayHost = new SpineCapturingHost(replay);
        replay.Actions.ApplyRecorded(recorded, replayHost);

        Assert.True(live.World.FindAircraft(Callsign)!.IsShadow);
        Assert.True(replay.World.FindAircraft(Callsign)!.IsShadow);
        (List<LiveTrafficSource> LiveTraffic, int AircraftSpawns) liveCalls = SpawnCalls(liveHost);
        (List<LiveTrafficSource> LiveTraffic, int AircraftSpawns) replayCalls = SpawnCalls(replayHost);
        Assert.Equal([LiveTrafficSource.Stars], liveCalls.LiveTraffic);
        Assert.Equal(liveCalls.LiveTraffic, replayCalls.LiveTraffic);
        Assert.Equal(0, liveCalls.AircraftSpawns);
        Assert.Equal(liveCalls.AircraftSpawns, replayCalls.AircraftSpawns);
        AssertOnlyTheControlGetsAPdcAndAStrip(live);
        AssertOnlyTheControlGetsAPdcAndAStrip(replay);
    }

    /// <summary>
    /// A recorded sample carrying a spawn state for a callsign whose shadow is already in the world (a world that has
    /// drifted from the recording) only refreshes it: nothing spawned, so the host is told nothing, as the live sync
    /// tells it nothing for a sample that refreshes.
    /// </summary>
    [Fact]
    public void ReplayedSampleWithSpawnState_ForAnExistingShadow_TellsTheHostNothing()
    {
        SimulationEngine engine = BareEngine();
        var host = new SpineCapturingHost(engine);
        engine.Actions.ApplyRecorded(new RecordedLiveTrafficSample(1, Callsign, Sample(1), DepartureSpawnState(Sample(1))), host);
        AircraftState shadow = engine.World.FindAircraft(Callsign)!;
        host.LiveTrafficSpawns.Clear();
        engine.Scenario!.ElapsedSeconds = 2;

        engine.Actions.ApplyRecorded(new RecordedLiveTrafficSample(2, Callsign, Sample(2), DepartureSpawnState(Sample(2))), host);

        Assert.Same(shadow, engine.World.FindAircraft(Callsign));
        Assert.Equal(2, shadow.LiveTraffic!.AppliedAtSimSeconds);
        Assert.Empty(host.LiveTrafficSpawns);
        Assert.Empty(host.AircraftSpawns);
    }

    /// <summary>
    /// The replay from zero runs the spawn hooks over the world it starts from. A simulated aircraft's hook clears the
    /// disconnect coast under its callsign; a shadow gets no hook, so its coast stands.
    /// </summary>
    [Fact]
    public void ReplayFromZero_RestoredShadow_GetsNoSpawnHooks()
    {
        SimulationEngine engine = BareEngine();
        engine.Scenario!.ElapsedSeconds = 0;
        LiveTrafficSample sample = Sample(0);
        engine.World.AddAircraft(AircraftState.FromSnapshot(DepartureSpawnState(sample), null));
        engine.World.AddAircraft(
            new AircraftState
            {
                Callsign = "AAL2",
                AircraftType = "B738",
                Position = Origin,
                Altitude = 3_000,
                IndicatedAirspeed = 200,
                FlightPlan = new AircraftFlightPlan { HasFlightPlan = true, Destination = "KLAX" },
            }
        );
        var coast = new AircraftDisconnectCoast(Origin, 300, 200, 0, []);
        engine.Scenario.DisconnectCoasts = engine.Scenario.DisconnectCoasts.Add(Callsign, coast).Add("AAL2", coast);
        Assert.True(engine.World.FindAircraft(Callsign)!.IsShadow);

        engine.ReplayFromStartTo(0, []);

        Assert.True(engine.Scenario.DisconnectCoasts.ContainsKey(Callsign));
        Assert.False(engine.Scenario.DisconnectCoasts.ContainsKey("AAL2"));
    }

    /// <summary>The feed as the sync step sees it: one in-scope track this second, every shadow present.</summary>
    private sealed class OneTrackFeedPort(string callsign, LiveTrafficSample sample, AircraftSnapshotDto spawnState) : ILiveTrafficFeedPort
    {
        public LiveTrafficFeedSecond BeginSecond() => new(true, null, [new LiveTrafficFeedTrack(callsign, sample, true, () => spawnState)]);

        public LiveTrafficShadowStatus ShadowStatus(string shadowCallsign) => LiveTrafficShadowStatus.Present;

        public void EndSecond() { }
    }
}
