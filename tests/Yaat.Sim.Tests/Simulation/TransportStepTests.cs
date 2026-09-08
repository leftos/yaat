using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The transport verbs on the bare engine — <c>PAUSE</c>, <c>UNPAUSE</c> and <c>SIMRATE</c>, the session clock the
/// room used to own. They write <see cref="SimScenarioState.IsPaused"/> and <see cref="SimScenarioState.SimRate"/>,
/// which the snapshot already carries, and the host is only told the clock changed.
///
/// <para>
/// They are <see cref="RecordingPolicy.Never"/>: the clock is how a session is watched, not what it simulates, so a
/// legacy <c>PAUSE</c> in an older recording must stay inert or a rewind through it would pause itself.
/// </para>
/// </summary>
public class TransportStepTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public TransportStepTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>A loaded engine, running (a fresh scenario starts paused) at 1×.</summary>
    private SimulationEngine? Engine()
    {
        if (_zoa is null)
        {
            return null;
        }

        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        engine.Scenario!.IsPaused = false;
        engine.Scenario!.SimRate = 1.0;
        return engine;
    }

    private static CommandResult Issue(SimulationEngine engine, IActionHost host, string command) =>
        engine.Actions.Issue(new ActionInput("", command, "conn-1", "XX", Baked: null), host).Result;

    [Fact]
    public void Pause_StopsTheClock_AndUnpauseStartsItAgain()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        var paused = Issue(engine, host, "PAUSE");

        Assert.True(paused.Success, paused.Message);
        Assert.True(engine.Scenario!.IsPaused);

        var resumed = Issue(engine, host, "UNPAUSE");

        Assert.True(resumed.Success, resumed.Message);
        Assert.False(engine.Scenario!.IsPaused);
    }

    [Theory]
    [InlineData("SIMRATE 4", 4.0)]
    [InlineData("SIMRATE 16", 16.0)]
    [InlineData("SIMRATE 32", 16.0)]
    [InlineData("SIMRATE 0", 1.0)]
    public void SimRate_SetsTheRate_ClampedToOneThroughSixteen(string command, double expected)
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var result = Issue(engine, new AttendanceActionHost(), command);

        Assert.True(result.Success, result.Message);
        Assert.Equal(expected, engine.Scenario!.SimRate);
    }

    /// <summary>Real aircraft cannot be accelerated, so anything above 1× is refused while the feed is on.</summary>
    [Fact]
    public void SimRate_AboveOne_IsRefusedWhileLiveTrafficIsOn_AndLeavesTheRateAlone()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        engine.Scenario!.LiveTrafficEnabled = true;

        var result = Issue(engine, new AttendanceActionHost(), "SIMRATE 4");

        Assert.False(result.Success);
        Assert.Contains("live traffic", result.Message);
        Assert.Equal(1.0, engine.Scenario!.SimRate);
    }

    /// <summary>
    /// The broadcast seam: every verb that moved the clock hands the host exactly one notification through the
    /// router's drain, and the one the body refused hands it none.
    /// </summary>
    [Fact]
    public void EachAcceptedVerb_RaisesOneSimStateChange_AndARefusedOneRaisesNone()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        Assert.True(Issue(engine, host, "PAUSE").Success);
        Assert.Equal(1, host.SimStateChanges);

        Assert.True(Issue(engine, host, "UNPAUSE").Success);
        Assert.Equal(2, host.SimStateChanges);

        Assert.True(Issue(engine, host, "SIMRATE 4").Success);
        Assert.Equal(3, host.SimStateChanges);

        engine.Scenario!.LiveTrafficEnabled = true;
        Assert.False(Issue(engine, host, "SIMRATE 8").Success);
        Assert.Equal(3, host.SimStateChanges);
    }

    /// <summary>With no scenario loaded there is no clock to move, and all three verbs say so rather than reporting success.</summary>
    [Fact]
    public void WithNoScenarioLoaded_AllThreeVerbsRefuse()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        engine.Scenario = null;

        Assert.Equal(new CommandResult(false, "No active scenario"), engine.Pause());
        Assert.Equal(new CommandResult(false, "No active scenario"), engine.Resume());
        Assert.Equal(new CommandResult(false, "No active scenario"), engine.SetSimRate(4));
    }

    /// <summary>
    /// The Never policy, from the router: a <c>PAUSE</c> in an older recording is refused before any body runs, so a
    /// rewind that replays through it cannot pause itself.
    /// </summary>
    [Fact]
    public void ATransportVerbReadBackFromARecord_IsInert()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var outcome = engine.Actions.Apply(new RecordedCommand(0, "", "PAUSE", "XX", "conn-1"), new AttendanceActionHost());

        Assert.False(outcome.Result.Success);
        Assert.False(engine.Scenario!.IsPaused);
    }
}
