using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Tests for the command-run delay (issue #180): a configurable pilot-reaction delay between the
/// controller issuing an instruction and the aircraft acting on it, simulating FMC / autopilot setup
/// time. Each pilot-actionable command is deferred a sampled [min, max] seconds; the controller gets an
/// immediate "complying in Ns" acknowledgement and the aircraft begins complying when the delay expires.
///
/// Determinism: live sampling draws from a dedicated <see cref="SimulationWorld.ReactionDelayRng"/> so it
/// never perturbs the shared RNG; replays reproduce the exact delay baked into the recorded command
/// rather than re-rolling.
/// </summary>
public class CommandRunDelayTests
{
    public CommandRunDelayTests()
    {
        // Pin data-backed singletons before any [Fact] body (physics/dispatch read profiles/categories).
        TestVnasData.EnsureInitialized();
    }

    private static SimulationEngine BuildEngine(int minDelay, int maxDelay, int rngSeed = 42)
    {
        var engine = new SimulationEngine(new NullGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "t",
                ScenarioName = "t",
                RngSeed = rngSeed,
                OriginalScenarioJson = "{}",
                CommandRunDelayMinSeconds = minDelay,
                CommandRunDelayMaxSeconds = maxDelay,
            },
        };
        engine.World.ReactionDelayRng = new SerializableRandom(rngSeed);
        return engine;
    }

    private static AircraftState AddAirborne(SimulationEngine engine, string callsign = "UAL123")
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = new LatLon(37.7, -122.2),
            TrueHeading = new TrueHeading(090),
            Altitude = 5000,
            IndicatedAirspeed = 250,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan(),
        };
        engine.World.AddAircraft(ac);
        return ac;
    }

    [Fact]
    public void Command_TakesEffectOnlyAfterDelay()
    {
        var engine = BuildEngine(minDelay: 5, maxDelay: 5);
        var ac = AddAirborne(engine);

        var result = engine.SendCommand("UAL123", "FH 270");

        Assert.True(result.Success);
        Assert.Contains("complying", result.Message, System.StringComparison.OrdinalIgnoreCase);
        // Deferred, not yet applied: one reaction deferral, no heading assigned.
        var reaction = Assert.Single(ac.DeferredDispatches);
        Assert.True(reaction.IsReactionDelay);
        Assert.Null(ac.Targets.AssignedMagneticHeading);

        // Four seconds in: still pending.
        for (int i = 0; i < 4; i++)
        {
            engine.TickOneSecond();
        }
        Assert.Null(ac.Targets.AssignedMagneticHeading);

        // Past the 5 s delay: the heading is now assigned.
        for (int i = 0; i < 2; i++)
        {
            engine.TickOneSecond();
        }
        Assert.Empty(ac.DeferredDispatches);
        Assert.NotNull(ac.Targets.AssignedMagneticHeading);
        Assert.Equal(270, ac.Targets.AssignedMagneticHeading!.Value.Degrees, precision: 0);
    }

    [Fact]
    public void FixedDelay_WhenMinEqualsMax_DoesNotConsumeRng()
    {
        var engine = BuildEngine(minDelay: 4, maxDelay: 4);
        var ac = AddAirborne(engine);

        var delay = engine.TryDeferCommandForReaction(ac, CommandParser.ParseCompound("FH 270").Value!);

        Assert.Equal(4.0, delay);
        var reaction = Assert.Single(ac.DeferredDispatches);
        Assert.Equal(4.0, reaction.RemainingSeconds);
        // min == max takes the fixed value without drawing — the RNG is untouched.
        Assert.Equal(new SerializableRandom(42).Next(0, 1000), engine.World.ReactionDelayRng.Next(0, 1000));
    }

    [Fact]
    public void RandomRange_SamplesDeterministically_FromReactionRng()
    {
        const int seed = 777;
        var engine = BuildEngine(minDelay: 2, maxDelay: 10, rngSeed: seed);
        var ac = AddAirborne(engine);

        var delay = engine.TryDeferCommandForReaction(ac, CommandParser.ParseCompound("FH 270").Value!);

        double expected = new SerializableRandom(seed).Next(2, 11);
        Assert.Equal(expected, delay);
        Assert.Equal(expected, Assert.Single(ac.DeferredDispatches).RemainingSeconds);
    }

    [Fact]
    public void Disabled_WhenMaxIsZero_DispatchesImmediately()
    {
        var engine = BuildEngine(minDelay: 0, maxDelay: 0);
        var ac = AddAirborne(engine);

        engine.SendCommand("UAL123", "FH 270");

        Assert.Empty(ac.DeferredDispatches);
        Assert.NotNull(ac.Targets.AssignedMagneticHeading);
        Assert.Equal(270, ac.Targets.AssignedMagneticHeading!.Value.Degrees, precision: 0);
    }

    [Fact]
    public void ExplicitWait_IsNotReactionDelayed()
    {
        var engine = BuildEngine(minDelay: 5, maxDelay: 5);
        var ac = AddAirborne(engine);

        // A controller-authored WAIT already models the wait — no extra reaction delay stacked on top.
        // Build the WAIT+FH structure directly (matches the parsed shape TryDeferLeadingWait detects).
        var waitCompound = new CompoundCommand([new ParsedBlock(null, [new WaitCommand(10), new FlyHeadingCommand(new MagneticHeading(270))])]);
        var delay = engine.TryDeferCommandForReaction(ac, waitCompound);

        Assert.Null(delay);
        Assert.Empty(ac.DeferredDispatches);
    }

    [Fact]
    public void FrequencyChange_IsNotReactionDelayed()
    {
        var engine = BuildEngine(minDelay: 5, maxDelay: 5);
        var ac = AddAirborne(engine);

        // A pure frequency-change / contact command switches ASAP (AIM 4-2-3) — never reaction-delayed.
        var contact = new CompoundCommand([new ParsedBlock(null, [new ContactCommand("TWR")])]);
        var delay = engine.TryDeferCommandForReaction(ac, contact);

        Assert.Null(delay);
        Assert.Empty(ac.DeferredDispatches);
    }

    [Fact]
    public void MixedFlightAndComm_IsStillReactionDelayed()
    {
        var engine = BuildEngine(minDelay: 5, maxDelay: 5);
        var ac = AddAirborne(engine);

        // A flight command riding with a contact verb is delayed as a whole — only a purely-comm
        // compound is exempt.
        var mixed = new CompoundCommand([new ParsedBlock(null, [new FlyHeadingCommand(new MagneticHeading(270)), new ContactCommand("TWR")])]);
        var delay = engine.TryDeferCommandForReaction(ac, mixed);

        Assert.Equal(5.0, delay);
        Assert.Single(ac.DeferredDispatches);
    }

    [Fact]
    public void IssueOrder_IsPreserved_UnderRandomRange()
    {
        var engine = BuildEngine(minDelay: 2, maxDelay: 12);
        var ac = AddAirborne(engine);

        engine.SendCommand("UAL123", "FH 090");
        engine.SendCommand("UAL123", "FH 270");

        Assert.Equal(2, ac.DeferredDispatches.Count);
        // The clamp guarantees the second command never fires before the first.
        Assert.True(ac.DeferredDispatches[1].RemainingSeconds >= ac.DeferredDispatches[0].RemainingSeconds);

        for (int i = 0; i < 13; i++)
        {
            engine.TickOneSecond();
        }

        // Both applied, last-issued wins (the earlier command's firing must not cancel the later one).
        Assert.Empty(ac.DeferredDispatches);
        Assert.Equal(270, ac.Targets.AssignedMagneticHeading!.Value.Degrees, precision: 0);
    }

    [Fact]
    public void Replay_UsesRecordedDelay_NotReSampled()
    {
        // Live sampling here would yield a fixed 2 s (min == max). The recorded command carries 7 s; replay
        // must reproduce the recorded value, proving it does not re-roll.
        var engine = BuildEngine(minDelay: 2, maxDelay: 2);
        var ac = AddAirborne(engine);

        engine.ReplayCommand(new RecordedCommand(0, "UAL123", "FH 270", "XX", "") { ReactionDelaySeconds = 7.0 });

        var reaction = Assert.Single(ac.DeferredDispatches);
        Assert.True(reaction.IsReactionDelay);
        Assert.Equal(7.0, reaction.RemainingSeconds);
        Assert.Null(ac.Targets.AssignedMagneticHeading);
    }

    [Fact]
    public void Settings_RoundTripThroughSnapshot()
    {
        var scenario = new SimScenarioState
        {
            ScenarioId = "s",
            ScenarioName = "s",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            CommandRunDelayMinSeconds = 3,
            CommandRunDelayMaxSeconds = 9,
        };

        var dto = scenario.ToSnapshot();

        Assert.Equal(3, dto.CommandRunDelayMinSeconds);
        Assert.Equal(9, dto.CommandRunDelayMaxSeconds);
    }

    [Fact]
    public void ReactionDeferral_RoundTripsThroughSnapshot()
    {
        var payload = CommandParser.ParseCompound("FH 270").Value!;
        var deferral = new DeferredDispatch(5.0, payload) { SourceText = "FH 270", IsReactionDelay = true };

        var restored = DeferredDispatch.FromSnapshot(deferral.ToSnapshot());

        Assert.NotNull(restored);
        Assert.True(restored!.IsReactionDelay);
        Assert.Equal(5.0, restored.RemainingSeconds);
    }

    private sealed class NullGroundData : IAirportGroundData
    {
        public AirportGroundLayout? GetLayout(string airportId) => null;

        public string? GetSourceGeoJson(string airportId) => null;
    }
}
