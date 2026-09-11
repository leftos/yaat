using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
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

    /// <summary>The aviation arm's decide-then-defer pair for a fresh human command, without the dispatch that follows it.</summary>
    private static double? Defer(SimulationEngine engine, AircraftState ac, CompoundCommand compound)
    {
        var delay = ReactionDelayPolicy.Decide(engine.Scenario!, engine.World, ac, compound, baked: null);
        if (delay is double seconds)
        {
            engine.DeferForReaction(ac, compound, seconds, DispatchOrigin.Human);
        }

        return delay;
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

        var delay = Defer(engine, ac, CommandParser.ParseCompound("FH 270").Value!);

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

        var delay = Defer(engine, ac, CommandParser.ParseCompound("FH 270").Value!);

        double expected = new SerializableRandom(seed).Next(2, 11);
        Assert.Equal(expected, delay);
        Assert.Equal(expected, Assert.Single(ac.DeferredDispatches).RemainingSeconds);
    }

    [Fact]
    public void SoloTrainingMode_SuppressesDelayAcknowledgement()
    {
        var engine = BuildEngine(minDelay: 5, maxDelay: 5);
        engine.Scenario!.SoloTrainingMode = true;
        var ac = AddAirborne(engine);

        var result = engine.SendCommand("UAL123", "FH 270");

        // The command still lands and is deferred by the reaction delay...
        Assert.True(result.Success);
        var reaction = Assert.Single(ac.DeferredDispatches);
        Assert.True(reaction.IsReactionDelay);
        Assert.Null(ac.Targets.AssignedMagneticHeading);

        // ...but the student is never told the exact delay that was decided for this command.
        Assert.True(string.IsNullOrEmpty(result.Message));
    }

    [Fact]
    public void NonSoloMode_StillReportsDelayAcknowledgement()
    {
        var engine = BuildEngine(minDelay: 5, maxDelay: 5);
        var ac = AddAirborne(engine);

        var result = engine.SendCommand("UAL123", "FH 270");

        Assert.True(result.Success);
        Assert.Single(ac.DeferredDispatches);
        // Instructor / RPO sessions keep the explicit delay acknowledgement.
        Assert.Contains("complying", result.Message, System.StringComparison.OrdinalIgnoreCase);
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
        var delay = Defer(engine, ac, waitCompound);

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
        var delay = Defer(engine, ac, contact);

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
        var delay = Defer(engine, ac, mixed);

        Assert.Equal(5.0, delay);
        Assert.Single(ac.DeferredDispatches);
    }

    [Fact]
    public void ForceHeading_IsNotReactionDelayed()
    {
        var engine = BuildEngine(minDelay: 5, maxDelay: 5);
        var ac = AddAirborne(engine);

        // FHN is an instructor verb: it sets the state directly, with no pilot in the loop to react.
        var result = engine.SendCommand("UAL123", "FHN 270");

        Assert.True(result.Success);
        Assert.DoesNotContain("complying", result.Message, System.StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ac.DeferredDispatches);
        Assert.NotNull(ac.Targets.AssignedMagneticHeading);
        Assert.Equal(270, ac.Targets.AssignedMagneticHeading!.Value.Degrees, precision: 0);
    }

    [Theory]
    [InlineData("CMN 30")]
    [InlineData("SPDN 210")]
    [InlineData("TRATE 3")]
    [InlineData("FH 270; DEL")]
    public void InstructorVerbs_AreNotReactionDelayed(string text)
    {
        var engine = BuildEngine(minDelay: 5, maxDelay: 5);
        var ac = AddAirborne(engine);

        // Every "Sim Control" verb is exempt, and one riding in a chain exempts the whole compound.
        var parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.Value is not null, $"'{text}' failed to parse: {parsed.Reason}");

        var delay = Defer(engine, ac, parsed.Value!);

        Assert.Null(delay);
        Assert.Empty(ac.DeferredDispatches);
    }

    [Fact]
    public void Warp_IsNotReactionDelayed()
    {
        var engine = BuildEngine(minDelay: 5, maxDelay: 5);
        var ac = AddAirborne(engine);

        // A teleport has no pilot action to delay. Hand-built so this timing test stays off FRD/navdata resolution.
        var warp = new CompoundCommand([new ParsedBlock(null, [new WarpCommand("OAK090010", 37.7, -122.0, null, null, null)])]);
        var delay = Defer(engine, ac, warp);

        Assert.Null(delay);
        Assert.Empty(ac.DeferredDispatches);
    }

    [Fact]
    public void WarpGround_IsNotReactionDelayed()
    {
        var engine = BuildEngine(minDelay: 5, maxDelay: 5);
        var ac = AddAirborne(engine);

        // The ground teleport is exempt for the same reason. Hand-built so this timing test stays off ground-layout
        // resolution.
        var warp = new CompoundCommand([new ParsedBlock(null, [new WarpGroundCommand("B", "C", null, null, null)])]);
        var delay = Defer(engine, ac, warp);

        Assert.Null(delay);
        Assert.Empty(ac.DeferredDispatches);
    }

    [Fact]
    public void SimControlCategory_MembershipIsPinned()
    {
        // Every verb in this category is exempt from the reaction delay (ReactionDelayPolicy.ContainsInstructorAction),
        // so adding one here is a sim-timing decision, not a menu-grouping one — update this list deliberately.
        CanonicalCommandType[] expected =
        [
            CanonicalCommandType.Add,
            CanonicalCommandType.Assume,
            CanonicalCommandType.Bookmark,
            CanonicalCommandType.CancelAutoDelete,
            CanonicalCommandType.Cfr,
            CanonicalCommandType.Delete,
            CanonicalCommandType.DisarmHoldForRelease,
            CanonicalCommandType.ForceAltitude,
            CanonicalCommandType.ForceHeading,
            CanonicalCommandType.ForceSpeed,
            CanonicalCommandType.HoldForRelease,
            CanonicalCommandType.Pause,
            CanonicalCommandType.ReleaseDeparture,
            CanonicalCommandType.SetTurnRate,
            CanonicalCommandType.SimRate,
            CanonicalCommandType.SpawnDelay,
            CanonicalCommandType.SpawnNow,
            CanonicalCommandType.Timer,
            CanonicalCommandType.Unassume,
            CanonicalCommandType.Unpause,
            CanonicalCommandType.Wait,
            CanonicalCommandType.WaitDistance,
            CanonicalCommandType.Warp,
            CanonicalCommandType.WarpGround,
        ];

        var actual = CommandRegistry.ByCategory("Sim Control").Select(d => d.Type).OrderBy(t => t.ToString(), StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MixedInstructorAndFlight_IsImmediate()
    {
        var engine = BuildEngine(minDelay: 5, maxDelay: 5);
        var ac = AddAirborne(engine);

        // Unlike the comm exemption (pure-comm only), one instructor verb makes the whole compound immediate.
        var mixed = new CompoundCommand([
            new ParsedBlock(null, [new ForceHeadingCommand(new MagneticHeading(270)), new FlyHeadingCommand(new MagneticHeading(090))]),
        ]);
        var delay = Defer(engine, ac, mixed);

        Assert.Null(delay);
        Assert.Empty(ac.DeferredDispatches);
    }

    [Fact]
    public void UnsupportedCommand_DoesNotThrow_UnderReactionDelay()
    {
        var engine = BuildEngine(minDelay: 5, maxDelay: 5);
        var ac = AddAirborne(engine);

        // "MLS 99" parses (the count is out of range) into an UnsupportedCommand, which has no canonical type —
        // deciding the delay must not ask the describer for one. The refusal lands at once, not after the delay.
        var result = engine.SendCommand("UAL123", "MLS 99");

        Assert.False(result.Success);
        Assert.Contains("not yet supported", result.Message, System.StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ac.DeferredDispatches);
    }

    [Fact]
    public void ConditionedMidChainWait_IsStillReactionDelayed()
    {
        var engine = BuildEngine(minDelay: 5, maxDelay: 5);
        var ac = AddAirborne(engine);

        // This WAIT is not leading timing, and it shares the "Sim Control" category with the instructor verbs —
        // the WaitCommand/WaitDistanceCommand skip in ContainsInstructorAction is what keeps this compound delayed.
        var parsed = CommandParser.ParseCompound("FH 090; AT 4000 WAIT 10 FH 270");
        Assert.True(parsed.Value is not null, $"parse failed: {parsed.Reason}");
        var compound = parsed.Value!;
        Assert.True(compound.Blocks.Count >= 2);
        Assert.Null(compound.Blocks[0].Condition);
        Assert.NotNull(compound.Blocks[1].Condition);
        Assert.Contains(compound.Blocks[1].Commands, cmd => cmd is WaitCommand);

        var delay = Defer(engine, ac, compound);

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

        engine.Actions.Apply(new RecordedCommand(0, "UAL123", "FH 270", "XX", "") { ReactionDelaySeconds = 7.0 });

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

    /// <summary>
    /// Issue #420: a fresh immediate command must supersede a pending controller-authored WAIT even while the
    /// pilot-reaction delay is active. The reaction path defers the new command instead of dispatching it, so the
    /// issue-time supersede <c>DispatchCompound</c> performs has to happen in <c>DeferForReaction</c> too — otherwise
    /// the superseded WAIT still fires later and snaps the aircraft back to the heading the controller replaced.
    /// </summary>
    [Fact]
    public void FreshImmediate_SupersedesPendingWait_EvenWhenReactionDelayActive()
    {
        var engine = BuildEngine(minDelay: 3, maxDelay: 3);
        var ac = AddAirborne(engine);

        // A controller-authored WAIT carries its own timing, so it is exempt from the reaction delay and parks
        // itself as a deferral firing at t=30.
        var wait = engine.SendCommand("UAL123", "WAIT 30 FH 090");
        Assert.True(wait.Success);
        Assert.Single(ac.DeferredDispatches);

        // A fresh immediate heading — reaction-delayed 3 s — supersedes that pending WAIT at issue time.
        var fresh = engine.SendCommand("UAL123", "FH 270");
        Assert.True(fresh.Success);

        for (int i = 0; i < 35; i++)
        {
            engine.TickOneSecond();
        }

        Assert.Empty(ac.DeferredDispatches);
        Assert.Equal(270, ac.Targets.AssignedMagneticHeading!.Value.Degrees, precision: 0);
    }

    /// <summary>
    /// The mirror of the supersede rule: a conditional incoming is purely additive, exactly as it is on the
    /// dispatcher path. <c>AT 6000 FH 270</c> is still reaction-delayed (only a leading WAIT/BEHIND is exempt), and
    /// when its deferral fires the block joins the queue waiting on its altitude trigger — which never fires here,
    /// the aircraft being level at 5000 with nothing assigned. The pending WAIT is untouched and fires on schedule.
    /// </summary>
    [Fact]
    public void ConditionalIncoming_KeepsPendingWait_WhenReactionDelayActive()
    {
        var engine = BuildEngine(minDelay: 3, maxDelay: 3);
        var ac = AddAirborne(engine);

        var wait = engine.SendCommand("UAL123", "WAIT 30 FH 090");
        Assert.True(wait.Success);
        Assert.Single(ac.DeferredDispatches);

        var conditional = engine.SendCommand("UAL123", "AT 6000 FH 270");
        Assert.True(conditional.Success);
        // Both survive the issue: the WAIT deferral and the conditional's own reaction deferral.
        Assert.Equal(2, ac.DeferredDispatches.Count);

        for (int i = 0; i < 35; i++)
        {
            engine.TickOneSecond();
        }

        // The WAIT fired unsuperseded...
        Assert.Equal(90, ac.Targets.AssignedMagneticHeading!.Value.Degrees, precision: 0);
        // ...and the conditional is still queued, waiting on 6000 ft.
        Assert.Contains(ac.Queue.Blocks, b => (b.Trigger is { Type: BlockTriggerType.ReachAltitude }) && !b.IsApplied);
    }

    private sealed class NullGroundData : IAirportGroundData
    {
        public AirportGroundLayout? GetLayout(string airportId) => null;

        public string? GetSourceGeoJson(string airportId) => null;
    }
}
