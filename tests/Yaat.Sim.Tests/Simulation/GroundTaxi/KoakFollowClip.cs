using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// The KOAK north-field ground clips of the FOLLOW montage (<c>tools/montage/follow/H1</c> and <c>H2</c>): two VFR C172s,
/// N52417 and N738SP, on the real KOAK layout, driven by the clip's script one sim-second at a time. Each clip test inlines
/// its clip's <c>scenario.json</c> and replays its <c>script.txt</c> through <see cref="RunScript"/>.
/// </summary>
internal static class KoakFollowClip
{
    internal const string Lead = "N52417";
    internal const string Follower = "N738SP";

    /// <summary>
    /// The most a C172 may lose in one sim-second on the ground: the piston taxi brake rate over the second, plus the one
    /// physics sub-tick of change the ground snap window allows on top of it.
    /// </summary>
    internal static double MaxPistonSpeedLossPerSecondKts =>
        CategoryPerformance.TaxiDecelRate(AircraftCategory.Piston) * (1.0 + (1.0 / SimulationEngine.PhysicsSubTickRate));

    /// <summary>
    /// Loads <paramref name="scenarioJson"/> on an engine over the committed KOAK layout, or null when navdata or the
    /// layout is unavailable (the repo's silent-skip convention for missing test data).
    /// </summary>
    internal static SimulationEngine? Load(ITestOutputHelper output, string scenarioJson)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("SKIP: navdata unavailable");
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            output.WriteLine("SKIP: KOAK layout unavailable");
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData);
        engine.LoadScenario(scenarioJson, 1, MagneticDeclination.EvaluationDateUtc);
        Assert.NotNull(engine.FindAircraft(Lead));
        Assert.NotNull(engine.FindAircraft(Follower));
        return engine;
    }

    /// <summary>
    /// Runs the clip from sim-second 0: at each second, sends that second's commands, then ticks one second and calls
    /// <paramref name="afterTick"/> with the second just completed. Stops when <paramref name="afterTick"/> returns true or
    /// after <paramref name="maxSeconds"/>.
    /// </summary>
    /// <returns>The second <paramref name="afterTick"/> returned true at, or -1 when it never did.</returns>
    internal static int RunScript(
        SimulationEngine engine,
        ITestOutputHelper output,
        IReadOnlyList<(int Second, string Callsign, string Command)> script,
        int maxSeconds,
        Func<int, bool> afterTick
    )
    {
        for (int second = 0; second < maxSeconds; second++)
        {
            foreach ((int at, string callsign, string command) in script.Where(line => line.Second == second))
            {
                CommandResult result = engine.SendCommand(callsign, command);
                output.WriteLine($"t={second} {callsign} {command} -> success={result.Success} msg={result.Message}");
                Assert.True(result.Success, $"t={at}: {callsign} {command} was rejected: {result.Message}");
            }

            engine.TickOneSecond();
            if (afterTick(second + 1))
            {
                return second + 1;
            }
        }

        return -1;
    }

    /// <summary>
    /// The largest one-second speed loss in <paramref name="speedsBySecond"/> (consecutive per-second ground speeds), with
    /// the second it ended at.
    /// </summary>
    internal static (double LossKts, int Second) LargestSpeedLossPerSecond(IReadOnlyList<(int Second, double SpeedKts)> speedsBySecond)
    {
        (double LossKts, int Second) worst = (0.0, -1);
        for (int i = 1; i < speedsBySecond.Count; i++)
        {
            double loss = speedsBySecond[i - 1].SpeedKts - speedsBySecond[i].SpeedKts;
            if (loss > worst.LossKts)
            {
                worst = (loss, speedsBySecond[i].Second);
            }
        }

        return worst;
    }
}
