using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Tests for the TIMER command: a countdown reminder that, on expiry, posts a green SAY-style
/// terminal entry — the free-text message, or "timer expired" when none was given. Timers live on
/// <see cref="SimScenarioState.ActiveTimers"/>, fire in sim time (gated on ElapsedSeconds), can be
/// global (no callsign) or attributed to an aircraft, and survive snapshot round-trips.
///
/// Firing tests seed <c>ActiveTimers</c> directly and tick <see cref="SimulationEngine"/> — the
/// command itself is handled server-side in RoomEngine (mirroring hold-for-release), so the
/// replay-reproducible firing logic is exercised without the command path.
/// </summary>
public class TimerCommandTests
{
    private static SimulationEngine BuildEngine()
    {
        var engine = new SimulationEngine(new NullGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test",
                ScenarioName = "test",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
            },
        };
        return engine;
    }

    [Fact]
    public void GlobalTimer_FiresGreenSayWithDefaultText_WhenNoMessage()
    {
        var engine = BuildEngine();
        engine.Scenario!.ActiveTimers.Add(
            new ActiveTimer
            {
                Id = 1,
                Callsign = null,
                Message = null,
                FireAtSeconds = 10,
                TotalSeconds = 10,
            }
        );

        // Before the fire time: nothing, timer still pending.
        engine.Scenario.ElapsedSeconds = 5;
        engine.TickPrePhysics();
        Assert.Empty(engine.DrainTerminalEntries());
        Assert.Single(engine.Scenario.ActiveTimers);

        // At/after the fire time: a green "Say" entry labeled TIMER, defaulting to "timer expired".
        engine.Scenario.ElapsedSeconds = 10;
        engine.TickPrePhysics();
        var entry = Assert.Single(engine.DrainTerminalEntries(), e => e.Kind == "Say");
        Assert.Equal("TIMER", entry.Callsign);
        Assert.Equal("timer expired", entry.Message);
        Assert.Empty(engine.Scenario.ActiveTimers);
    }

    [Fact]
    public void GlobalTimer_FiresWithFreeTextMessage()
    {
        var engine = BuildEngine();
        engine.Scenario!.ActiveTimers.Add(
            new ActiveTimer
            {
                Id = 1,
                Callsign = null,
                Message = "CHECK STRIPS",
                FireAtSeconds = 30,
                TotalSeconds = 30,
            }
        );

        engine.Scenario.ElapsedSeconds = 30;
        engine.TickPrePhysics();

        var entry = Assert.Single(engine.DrainTerminalEntries(), e => e.Kind == "Say");
        Assert.Equal("TIMER", entry.Callsign);
        Assert.Equal("CHECK STRIPS", entry.Message);
    }

    [Fact]
    public void PerAircraftTimer_FiresAttributedToAircraft()
    {
        var engine = BuildEngine();
        engine.World.AddAircraft(new AircraftState { Callsign = "N172SP", AircraftType = "C172" });
        engine.Scenario!.ActiveTimers.Add(
            new ActiveTimer
            {
                Id = 1,
                Callsign = "N172SP",
                Message = "READY TO COPY",
                FireAtSeconds = 20,
                TotalSeconds = 20,
            }
        );

        engine.Scenario.ElapsedSeconds = 20;
        engine.TickPrePhysics();

        var entry = Assert.Single(engine.DrainTerminalEntries(), e => e.Kind == "Say");
        Assert.Equal("N172SP", entry.Callsign);
        Assert.Equal("READY TO COPY", entry.Message);
        Assert.Empty(engine.Scenario.ActiveTimers);
    }

    [Fact]
    public void PerAircraftTimer_DroppedSilently_WhenAircraftGone()
    {
        var engine = BuildEngine();
        // No aircraft added to the world — the per-aircraft timer must not fire and must be pruned.
        engine.Scenario!.ActiveTimers.Add(
            new ActiveTimer
            {
                Id = 1,
                Callsign = "N172SP",
                Message = "READY TO COPY",
                FireAtSeconds = 5,
                TotalSeconds = 5,
            }
        );

        engine.Scenario.ElapsedSeconds = 10;
        engine.TickPrePhysics();

        Assert.Empty(engine.DrainTerminalEntries());
        Assert.Empty(engine.Scenario.ActiveTimers);
    }

    [Fact]
    public void Timer_DoesNotFire_WhileElapsedFrozen_SimulatingPause()
    {
        var engine = BuildEngine();
        engine.Scenario!.ActiveTimers.Add(
            new ActiveTimer
            {
                Id = 1,
                Callsign = null,
                Message = null,
                FireAtSeconds = 10,
                TotalSeconds = 10,
            }
        );

        // ElapsedSeconds never reaches FireAtSeconds (sim paused → frozen clock). Multiple ticks,
        // no fire — proves the timer is gated on sim time, not wall-clock or tick count.
        engine.Scenario.ElapsedSeconds = 9;
        for (int i = 0; i < 5; i++)
        {
            engine.TickPrePhysics();
        }

        Assert.Empty(engine.DrainTerminalEntries());
        Assert.Single(engine.Scenario.ActiveTimers);
    }

    [Fact]
    public void ActiveTimers_RoundTripThroughSnapshot()
    {
        var scenario = new SimScenarioState
        {
            ScenarioId = "s",
            ScenarioName = "s",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            NextTimerId = 7,
        };
        scenario.ActiveTimers.Add(
            new ActiveTimer
            {
                Id = 3,
                Callsign = "N172SP",
                Message = "CALL GROUND",
                FireAtSeconds = 240,
                TotalSeconds = 300,
            }
        );

        var dto = scenario.ToSnapshot();

        Assert.Equal(7, dto.NextTimerId);
        Assert.NotNull(dto.ActiveTimers);
        var t = Assert.Single(dto.ActiveTimers!);
        Assert.Equal(3, t.Id);
        Assert.Equal("N172SP", t.Callsign);
        Assert.Equal("CALL GROUND", t.Message);
        Assert.Equal(240, t.FireAtSeconds);
        Assert.Equal(300, t.TotalSeconds);
    }

    private sealed class NullGroundData : IAirportGroundData
    {
        public AirportGroundLayout? GetLayout(string airportId) => null;
    }
}

/// <summary>
/// Parser tests for the TIMER command argument: <c>mm:ss</c> / bare-seconds durations, optional
/// greedy free-text (commas preserved), and the <c>CANCEL &lt;id|ALL&gt;</c> forms.
/// </summary>
public class TimerParseTests
{
    private static TimerCommand ParseTimer(string input)
    {
        var result = CommandParser.Parse(input);
        Assert.True(result.IsSuccess, $"Parse failed: {result.Reason}");
        return Assert.IsType<TimerCommand>(result.Value);
    }

    [Theory]
    [InlineData("TIMER 90", 90)]
    [InlineData("TIMER 5:00", 300)]
    [InlineData("TIMER 1:30", 90)]
    [InlineData("TMR 0:45", 45)]
    public void ParsesDuration(string input, int expectedSeconds)
    {
        var cmd = ParseTimer(input);
        Assert.False(cmd.IsCancel);
        Assert.Equal(expectedSeconds, cmd.Seconds);
        Assert.Null(cmd.Message);
    }

    [Fact]
    public void ParsesDurationAndMessage()
    {
        var cmd = ParseTimer("TIMER 5:00 release strips");
        Assert.Equal(300, cmd.Seconds);
        Assert.Equal("release strips", cmd.Message);
    }

    [Fact]
    public void Message_IsGreedy_PreservingCommas()
    {
        var result = CommandParser.ParseCompound("TIMER 60 check strips, then call ground");
        Assert.True(result.IsSuccess, $"Parse failed: {result.Reason}");
        var block = Assert.Single(result.Value!.Blocks);
        var cmd = Assert.IsType<TimerCommand>(Assert.Single(block.Commands));
        Assert.Equal(60, cmd.Seconds);
        Assert.Equal("check strips, then call ground", cmd.Message);
    }

    [Fact]
    public void ParsesCancelById()
    {
        var cmd = ParseTimer("TIMER CANCEL 3");
        Assert.True(cmd.IsCancel);
        Assert.Equal(3, cmd.CancelId);
        Assert.False(cmd.CancelAll);
    }

    [Fact]
    public void ParsesCancelAll()
    {
        var cmd = ParseTimer("TIMER CANCEL ALL");
        Assert.True(cmd.IsCancel);
        Assert.True(cmd.CancelAll);
        Assert.Null(cmd.CancelId);
    }

    [Theory]
    [InlineData("TIMER abc")]
    [InlineData("TIMER 0")]
    [InlineData("TIMER 1:60")]
    [InlineData("TIMER")]
    [InlineData("TIMER CANCEL xyz")]
    public void RejectsInvalid(string input)
    {
        var result = CommandParser.Parse(input);
        Assert.False(result.IsSuccess);
    }

    // The client builds the canonical string via CommandSchemeParser before sending; these guard the
    // client-facing path (global-command detection + greedy message, commas preserved).

    [Fact]
    public void SchemeParser_DetectsTimerType_ForGlobalRouting()
    {
        var parsed = CommandSchemeParser.Parse("TIMER 90", CommandScheme.Default());
        Assert.NotNull(parsed);
        Assert.Equal(CanonicalCommandType.Timer, parsed!.Type);
    }

    [Fact]
    public void SchemeParser_BuildsCanonical_PreservingCommasInMessage()
    {
        var result = CommandSchemeParser.ParseCompound("TIMER 5:00 check strips, then call ground", CommandScheme.Default());
        Assert.NotNull(result);
        Assert.Equal("TIMER 5:00 CHECK STRIPS, THEN CALL GROUND", result!.CanonicalString);
    }
}
