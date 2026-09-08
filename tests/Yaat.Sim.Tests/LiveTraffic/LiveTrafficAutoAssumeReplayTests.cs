using Xunit;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.LiveTraffic;

/// <summary>
/// The automatic assume adds no recording surface: it draws no RNG and mints no id, so the recorded command is
/// the ordinary <see cref="RecordedCommand"/> for the instruction and a replay re-derives the same hand-off at the
/// same second. Pinned because the alternative — recording the assume as its own action — is invisible until a
/// replay of a live session diverges.
/// </summary>
public class LiveTrafficAutoAssumeReplayTests(ITestOutputHelper output)
{
    private const string BundlePath = "TestData/66fd6538542e.zip";
    private const string Callsign = "UAL123";
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
    public void AnAutoAssumingCommand_ReplaysToTheSameStateAsTheLiveRun()
    {
        var baseline = LoadBaseline(output);
        if (baseline is null)
        {
            return;
        }

        var live = new SimulationEngine(new TestAirportGroundData());
        live.Replay(WithActions(baseline, [], 0), 0);

        var spawnState = LiveTrafficKinematics
            .CreateShadow(Callsign, "B738", Sample(1, Origin, 90), new AircraftFlightPlan { HasFlightPlan = true, Destination = "KOAK" })
            .ToSnapshot();

        LiveSecond(live, 1, () => live.ApplyLiveTrafficSample(Callsign, Sample(1, Origin, 90), spawnState));
        for (int t = 2; t <= 5; t++)
        {
            LiveSecond(live, t, null);
        }

        Assert.True(live.World.FindAircraft(Callsign)!.IsShadow);
        var issued = live.Actions.Issue(new ActionInput(Callsign, "FH 070", "conn-1", "XX", Baked: null));
        Assert.True(issued.Result.Success, issued.Result.Message);
        Assert.Contains($"{Callsign} assumed", issued.Result.Message, StringComparison.Ordinal);

        for (int t = 6; t <= 20; t++)
        {
            LiveSecond(live, t, null);
        }

        var liveAircraft = live.World.FindAircraft(Callsign)!;
        Assert.False(liveAircraft.IsShadow);

        // No new recording surface: one ordinary command record for the instruction, and no assume action beside it.
        var actions = live.Scenario!.ActionLog.ToList();
        var command = Assert.Single(actions.OfType<RecordedCommand>());
        Assert.Equal("FH 070", command.Command);
        Assert.True(command.Accepted);
        Assert.Equal(2, actions.Count);

        var replay = new SimulationEngine(new TestAirportGroundData());
        replay.Replay(WithActions(baseline, actions, 22), 20);
        var replayed = replay.World.FindAircraft(Callsign);

        Assert.NotNull(replayed);
        Assert.False(replayed.IsShadow);
        Assert.InRange(GeoMath.DistanceNm(liveAircraft.Position, replayed.Position), 0, 0.001);
        Assert.Equal(liveAircraft.Altitude, replayed.Altitude, 3);
        Assert.Equal(liveAircraft.TrueHeading.Degrees, replayed.TrueHeading.Degrees, 3);
        Assert.Equal(liveAircraft.IndicatedAirspeed, replayed.IndicatedAirspeed, 3);
        Assert.Equal(liveAircraft.Targets.AssignedMagneticHeading?.Degrees, replayed.Targets.AssignedMagneticHeading?.Degrees);
        Assert.Equal(liveAircraft.Targets.TargetAltitude, replayed.Targets.TargetAltitude);
        Assert.Equal(liveAircraft.Phases?.CurrentPhase?.GetType(), replayed.Phases?.CurrentPhase?.GetType());
    }
}
