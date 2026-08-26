using Xunit;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.LiveTraffic;

/// <summary>
/// Live-traffic samples and removals are recorded actions. Samples are applied <b>pre-tick</b>
/// (before the physics of the second they were recorded in, like <see cref="RecordedAircraftSpawn"/>),
/// so a replay reproduces the live run's end-of-second positions exactly; removals apply after the second.
/// </summary>
public class LiveTrafficReplayTests(ITestOutputHelper output)
{
    private const string BundlePath = "TestData/66fd6538542e.zip";
    private static readonly LatLon Origin = new(37.0, -122.0);

    private static LiveTrafficSample Sample(double observedAt, LatLon position, double trueTrackDeg) =>
        new(observedAt, position.Lat, position.Lon, 8_000, 240, trueTrackDeg, -500, LiveTrafficSource.Stars, 4521);

    private static SessionRecording? LoadBaseline(ITestOutputHelper output)
    {
        var baseline = RecordingLoader.Load(BundlePath);
        if (baseline is null)
        {
            output.WriteLine($"Skipped: {BundlePath} not present");
            return null;
        }

        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb is null ? null : baseline;
    }

    private static SessionRecording WithActions(SessionRecording baseline, List<RecordedAction> actions, double total) =>
        new()
        {
            Version = baseline.Version,
            ScenarioJson = baseline.ScenarioJson,
            RngSeed = baseline.RngSeed,
            WeatherJson = baseline.WeatherJson,
            Actions = actions,
            TotalElapsedSeconds = total,
            ScenarioName = baseline.ScenarioName,
            ScenarioId = baseline.ScenarioId,
            ArtccId = baseline.ArtccId,
        };

    /// <summary>Runs one sim-second the way the server does: samples land in pre-physics of second <paramref name="t"/>.</summary>
    private static void LiveSecond(SimulationEngine engine, int t, Action? prePhysics)
    {
        engine.Scenario!.ElapsedSeconds = t;
        prePhysics?.Invoke();
        engine.TickPrePhysics();
        for (int sub = 0; sub < SimulationEngine.PhysicsSubTickRate; sub++)
        {
            engine.TickPhysics(1.0 / SimulationEngine.PhysicsSubTickRate);
        }

        engine.TickPostPhysics();
    }

    [Fact]
    public void RecordedSamplesAndRemoval_ReplayToTheLivePositions()
    {
        var baseline = LoadBaseline(output);
        if (baseline is null)
        {
            return;
        }

        var live = new SimulationEngine(new TestAirportGroundData());
        live.Replay(WithActions(baseline, [], 0), 0);

        var second = GeoMath.ProjectPoint(Origin, new TrueHeading(90), 0.3);
        var spawnState = LiveTrafficKinematics
            .CreateShadow("UAL123", "B738", Sample(1, Origin, 90), new AircraftFlightPlan { HasFlightPlan = true, Destination = "KOAK" })
            .ToSnapshot();

        LiveSecond(live, 1, () => live.ApplyLiveTrafficSample("UAL123", Sample(1, Origin, 90), spawnState));
        for (int t = 2; t <= 4; t++)
        {
            LiveSecond(live, t, null);
        }

        var liveAt4 = live.World.FindAircraft("UAL123")!.Position;
        LiveSecond(live, 5, () => live.ApplyLiveTrafficSample("UAL123", Sample(5, second, 120), null));
        LiveSecond(live, 6, null);
        var liveAt6 = live.World.FindAircraft("UAL123")!.Position;
        LiveSecond(live, 7, null);
        live.RemoveLiveTraffic("UAL123", LiveTrafficRemovalReason.Stale);
        Assert.Null(live.World.FindAircraft("UAL123"));

        var actions = live.Scenario!.ActionLog.ToList();
        Assert.Equal(2, actions.OfType<RecordedLiveTrafficSample>().Count());
        Assert.Single(actions.OfType<RecordedLiveTrafficRemoval>());
        Assert.NotNull(actions.OfType<RecordedLiveTrafficSample>().First().SpawnState);
        Assert.Null(actions.OfType<RecordedLiveTrafficSample>().Last().SpawnState);

        var recording = WithActions(baseline, actions, 10);
        var replay = new SimulationEngine(new TestAirportGroundData());

        replay.Replay(recording, 4);
        var replayAt4 = replay.World.FindAircraft("UAL123");
        Assert.NotNull(replayAt4);
        Assert.True(replayAt4.IsShadow);
        Assert.InRange(GeoMath.DistanceNm(liveAt4, replayAt4.Position), 0, 0.001);

        replay.Replay(recording, 6);
        var replayAt6 = replay.World.FindAircraft("UAL123")!;
        Assert.InRange(GeoMath.DistanceNm(liveAt6, replayAt6.Position), 0, 0.001);
        Assert.InRange(replayAt6.TrueTrack.Degrees, 119.5, 120.5);

        replay.Replay(recording, 8);
        Assert.Null(replay.World.FindAircraft("UAL123"));
    }

    [Fact]
    public void ReplayOneSecond_AppliesSamplesPreTick()
    {
        var baseline = LoadBaseline(output);
        if (baseline is null)
        {
            return;
        }

        var spawnState = LiveTrafficKinematics
            .CreateShadow("UAL123", "B738", Sample(1, Origin, 90), new AircraftFlightPlan { HasFlightPlan = true })
            .ToSnapshot();
        var second = GeoMath.ProjectPoint(Origin, new TrueHeading(90), 0.3);
        var recording = WithActions(
            baseline,
            [
                new RecordedLiveTrafficSample(1, "UAL123", Sample(1, Origin, 90), spawnState),
                new RecordedLiveTrafficSample(5, "UAL123", Sample(5, second, 120), null),
            ],
            10
        );

        var full = new SimulationEngine(new TestAirportGroundData());
        full.Replay(recording, 6);
        var fullAt6 = full.World.FindAircraft("UAL123")!.Position;

        var stepped = new SimulationEngine(new TestAirportGroundData());
        stepped.Replay(recording, 4);
        stepped.ReplayOneSecond();
        stepped.ReplayOneSecond();
        var steppedAt6 = stepped.World.FindAircraft("UAL123")!;

        Assert.Equal(6, stepped.Scenario!.ElapsedSeconds);
        Assert.InRange(GeoMath.DistanceNm(fullAt6, steppedAt6.Position), 0, 0.001);
        // Sample 5 was applied before second 5's physics: two seconds of motion by the end of second 6.
        Assert.InRange(steppedAt6.LiveTraffic!.SecondsSinceSample, 1.99, 2.01);
    }
}
