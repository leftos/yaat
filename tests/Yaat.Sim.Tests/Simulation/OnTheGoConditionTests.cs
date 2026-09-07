using System.Linq;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// <c>OTG</c> ("on the go") is a condition prefix modelled on <c>ONHS</c>: it holds its inner command
/// until the aircraft is climbing out after its next cycle terminator — touch-and-go, stop-and-go, low
/// approach — or after a go-around. <c>OTG MLT 28L</c> issued on final therefore changes the pattern
/// only once the aircraft is airborne again on the upwind, never mid-approach and never on the runway.
///
/// Recording: S2-OAK-4 "VFR Transitions / Radar Concepts" (ZOA, OAK). N342T (DA42) flies a 28R right
/// circuit: final from t≈865 (recorded <c>COPT</c> at t=883), touch-and-go at t≈910, a fresh upwind at
/// t≈935, then a second approach that goes around at t≈1110 with its upwind at t≈1130. Every E2E test
/// restores a snapshot and drives the engine with <see cref="SimulationEngine.TickOneSecond"/>, so the
/// recorded actions (including the operator's own MLT at t=1125) never fire.
/// </summary>
public class OnTheGoConditionTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/parallel-mlt-from-upwind-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N342T";
    private const string AirportId = "OAK";

    /// <summary>Final approach, after the recorded COPT — the aircraft is committed to the touch-and-go.</summary>
    private const int FinalBeforeTouchAndGoTime = 890;

    /// <summary>The fresh 28R upwind right after the touch-and-go, with no terminator ahead of it.</summary>
    private const int UpwindAfterTouchAndGoTime = 940;

    /// <summary>Final approach on the second circuit; N152SP is rolling on 28R and forces a go-around at t≈1110.</summary>
    private const int FinalBeforeGoAroundTime = 1095;

    private const int MaxTicks = 240;

    /// <summary>The block must fire on the climb-out, not drift several legs into the new circuit.</summary>
    private const int MaxTicksFromUpwindToFire = 10;

    private static readonly CommandScheme Scheme = CommandScheme.Default();

    // ---------------------------------------------------------------------------------------------
    // Parser + canonicalizer
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void CommandParser_OtgMlt_ProducesOneBlockWithTheCondition()
    {
        TestVnasData.EnsureInitialized();

        var result = CommandParser.ParseCompound("OTG MLT 28L");

        Assert.True(result.IsSuccess, result.Reason);
        var block = Assert.Single(result.Value!.Blocks);
        Assert.IsType<OnTheGoCondition>(block.Condition);
        var mlt = Assert.IsType<MakeLeftTrafficCommand>(Assert.Single(block.Commands));
        Assert.Equal("28L", mlt.RunwayId);
        Assert.Null(mlt.Altitude);
    }

    /// <summary>
    /// <c>OTG</c> followed by another condition splits into two sequential blocks — the bare OTG gate
    /// first, then the inner condition — exactly as <c>ONHS</c> and <c>ONHO</c> do.
    /// </summary>
    [Fact]
    public void CommandParser_OtgThenAtFix_ProducesTwoBlocks()
    {
        TestVnasData.EnsureInitialized();

        var result = CommandParser.ParseCompound("OTG AT LIVVY DEL");

        Assert.True(result.IsSuccess, result.Reason);
        Assert.Equal(2, result.Value!.Blocks.Count);
        Assert.IsType<OnTheGoCondition>(result.Value!.Blocks[0].Condition);
        Assert.Empty(result.Value!.Blocks[0].Commands);

        var at = Assert.IsType<AtFixCondition>(result.Value!.Blocks[1].Condition);
        Assert.Equal("LIVVY", at.FixName);
        Assert.IsType<DeleteCommand>(Assert.Single(result.Value!.Blocks[1].Commands));
    }

    [Theory]
    [InlineData("OTG MLT 28L", "OTG MLT 28L")]
    [InlineData("otg mlt 28l", "OTG MLT 28L")]
    [InlineData("OTG AT LIVVY DEL", "OTG; AT LIVVY DEL")]
    public void SchemeParser_OtgCanonicalizes(string input, string expected)
    {
        TestVnasData.EnsureInitialized();

        var result = CommandSchemeParser.ParseCompound(input, Scheme);

        Assert.NotNull(result);
        Assert.Equal(expected, result.CanonicalString);
    }

    /// <summary>
    /// The client canonicalizer runs before anything reaches the server, so OTG has to travel every
    /// path ONHS does (issue #335). Pinning the two against each other catches a branch mirrored in
    /// one place and missed in another — including the bare verb and the failure message.
    /// </summary>
    [Theory]
    [InlineData("OTG", "ONHS")]
    [InlineData("OTG MLT 28L", "ONHS MLT 28L")]
    [InlineData("otg mlt 28l", "onhs mlt 28l")]
    [InlineData("OTG AT LIVVY DEL", "ONHS AT LIVVY DEL")]
    [InlineData("OTG ZZQQ", "ONHS ZZQQ")]
    [InlineData("OTG WAIT 30 CM 360", "ONHS WAIT 30 CM 360")]
    public void SchemeParser_OtgMirrorsOnhs(string otgInput, string onhsInput)
    {
        TestVnasData.EnsureInitialized();

        var otg = CommandSchemeParser.ParseCompound(otgInput, Scheme, out var otgFailure);
        var onhs = CommandSchemeParser.ParseCompound(onhsInput, Scheme, out var onhsFailure);

        Assert.Equal(onhs?.CanonicalString.Replace("ONHS", "OTG"), otg?.CanonicalString);
        Assert.Equal(onhsFailure?.Verb.Replace("ONHS", "OTG"), otgFailure?.Verb);
        Assert.Equal(onhsFailure?.Reason.Replace("ONHS", "OTG"), otgFailure?.Reason);
    }

    // ---------------------------------------------------------------------------------------------
    // E2E
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Queued on final for the option: the pattern change waits out the approach and the touch-and-go
    /// itself, then fires on the climb-out — landing the aircraft on the 28L transition circuit.
    /// </summary>
    [Fact]
    public void OtgMlt_QueuedOnFinal_FiresOnTheClimbOutAfterTheTouchAndGo()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            var ac = RestoreAt(engine, archive, FinalBeforeTouchAndGoTime);
            if (ac is null)
            {
                return;
            }

            Assert.IsType<FinalApproachPhase>(ac.Phases?.CurrentPhase);
            Assert.Equal("28R", ac.Phases?.AssignedRunway?.Designator);
            Assert.NotNull(ac.Phases?.LandingClearance);

            var result = engine.SendCommand(Callsign, "OTG MLT 28L");
            Assert.True(result.Success, $"OTG MLT 28L was refused: {result.Message}");
            Assert.Contains("On the go", result.Message ?? "", StringComparison.Ordinal);

            AssertQueuedBlockDescribesAndRoundTrips(ac);

            FlyUntilTheBlockFires(engine, [typeof(FinalApproachPhase), typeof(TouchAndGoPhase)]);
        }
    }

    /// <summary>
    /// A go-around is a cycle terminator too: the block latches on the go-around and fires on the
    /// upwind that follows it, never while the aircraft is still going around.
    /// </summary>
    [Fact]
    public void OtgMlt_QueuedOnFinal_FiresAfterAGoAround()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            var ac = RestoreAt(engine, archive, FinalBeforeGoAroundTime);
            if (ac is null)
            {
                return;
            }

            Assert.IsType<FinalApproachPhase>(ac.Phases?.CurrentPhase);
            Assert.Equal("28R", ac.Phases?.AssignedRunway?.Designator);

            var result = engine.SendCommand(Callsign, "OTG MLT 28L");
            Assert.True(result.Success, $"OTG MLT 28L was refused: {result.Message}");

            bool sawGoAround = FlyUntilTheBlockFires(engine, [typeof(FinalApproachPhase), typeof(GoAroundPhase)]);
            Assert.True(sawGoAround, $"{Callsign} never went around after t={FinalBeforeGoAroundTime} — the test proves nothing about a go-around");
        }
    }

    /// <summary>
    /// Issued on a climb-out with no terminator ahead of it, OTG simply waits: the aircraft flies its
    /// circuit out on the runway it was assigned, and the block stays queued for the next terminator.
    /// </summary>
    [Fact]
    public void OtgMlt_QueuedOnUpwind_WaitsForTheNextTerminator()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            var ac = RestoreAt(engine, archive, UpwindAfterTouchAndGoTime);
            if (ac is null)
            {
                return;
            }

            Assert.IsType<UpwindPhase>(ac.Phases?.CurrentPhase);
            Assert.Equal("28R", ac.Phases?.AssignedRunway?.Designator);

            var result = engine.SendCommand(Callsign, "OTG MLT 28L");
            Assert.True(result.Success, $"OTG MLT 28L was refused: {result.Message}");

            bool sawCrosswind = false;
            bool sawDownwind = false;
            for (int t = 1; t <= 60; t++)
            {
                engine.TickOneSecond();
                var live = engine.FindAircraft(Callsign);
                Assert.NotNull(live);

                sawCrosswind |= live.Phases?.CurrentPhase is CrosswindPhase;
                sawDownwind |= live.Phases?.CurrentPhase is DownwindPhase;

                Assert.Equal("28R", live.Phases?.AssignedRunway?.Designator);
                Assert.Equal(PatternDirection.Right, live.Phases?.TrafficDirection);
            }

            var final = engine.FindAircraft(Callsign);
            Assert.NotNull(final);
            Assert.False(final.Queue.IsComplete, "the OTG block fired without a cycle terminator");
            Assert.Contains(final.Queue.Blocks, b => (b.Trigger?.Type == BlockTriggerType.AfterCycleTerminator) && !b.IsApplied);
            Assert.True(sawCrosswind, "never reached the crosswind — the circuit did not advance normally");
            Assert.True(sawDownwind, "never reached the downwind — the circuit did not advance normally");
        }
    }

    /// <summary>
    /// A full stop is the end of the cycle the block was waiting on: the aircraft never climbs out, so
    /// the trigger it latched on can never fire and the block would sit in the queue for the rest of the
    /// session. Landing full stop discards it with the missed-condition warning instead, and the pattern
    /// change never happens — the aircraft keeps the runway it landed on.
    /// </summary>
    [Fact]
    public void OtgMlt_QueuedOnFinal_IsDiscardedWhenTheAircraftLandsFullStop()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            var warnings = new List<string>();
            engine.WarningEmitted += (_, warning) => warnings.Add(warning);

            var ac = RestoreAt(engine, archive, FinalBeforeTouchAndGoTime);
            if (ac is null)
            {
                return;
            }

            Assert.IsType<FinalApproachPhase>(ac.Phases?.CurrentPhase);

            // Full stop instead of the recorded option: the touch-and-go terminator never happens.
            var cland = engine.SendCommand(Callsign, "CLAND");
            Assert.True(cland.Success, $"CLAND was refused: {cland.Message}");
            var otg = engine.SendCommand(Callsign, "OTG MLT 28L");
            Assert.True(otg.Success, $"OTG MLT 28L was refused: {otg.Message}");
            Assert.Contains(ac.Queue.Blocks, b => b.Trigger?.Type == BlockTriggerType.AfterCycleTerminator);

            AircraftState? afterRollout = null;
            for (int t = 1; t <= MaxTicks; t++)
            {
                engine.TickOneSecond();
                var live = engine.FindAircraft(Callsign);
                if (live is null)
                {
                    break;
                }

                if (live.Phases?.CurrentPhase is RunwayExitPhase or HoldingAfterExitPhase)
                {
                    afterRollout = live;
                    break;
                }
            }

            Assert.NotNull(afterRollout);
            output.WriteLine($"after rollout: phase={afterRollout.Phases?.CurrentPhase?.Name} queue={afterRollout.Queue.Blocks.Count} blocks");

            Assert.DoesNotContain(afterRollout.Queue.Blocks, b => b.Trigger?.Type == BlockTriggerType.AfterCycleTerminator);
            Assert.Equal("28R", afterRollout.Phases?.AssignedRunway?.Designator);
            // P/CG UNABLE: the pilot could not comply — the controller never cancelled anything.
            Assert.Contains(warnings, w => w.Contains("unable — landed full stop", StringComparison.Ordinal));
            Assert.Contains(warnings, w => w.Contains("OTG MLT 28L", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// The warning quotes the source text verbatim. Running it through the runway de-padder — which the
    /// fire-time failure path does — would turn <c>015</c> into <c>15</c>, and in the modifier's own
    /// grammar a bare <c>15</c> is runway 15: the RPO would be told about an instruction nobody issued.
    /// </summary>
    [Fact]
    public void OtgMlt_FullStop_QuotesAThreeDigitPatternAltitudeVerbatim()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            var warnings = new List<string>();
            engine.WarningEmitted += (_, warning) => warnings.Add(warning);

            var ac = RestoreAt(engine, archive, FinalBeforeTouchAndGoTime);
            if (ac is null)
            {
                return;
            }

            Assert.True(engine.SendCommand(Callsign, "CLAND").Success);
            var otg = engine.SendCommand(Callsign, "OTG MLT 28R 015");
            Assert.True(otg.Success, $"OTG MLT 28R 015 was refused: {otg.Message}");

            var afterRollout = FlyToRollout(engine);
            Assert.NotNull(afterRollout);

            Assert.DoesNotContain(afterRollout.Queue.Blocks, b => b.Trigger?.Type == BlockTriggerType.AfterCycleTerminator);
            Assert.Contains(warnings, w => w.Contains("OTG MLT 28R 015", StringComparison.Ordinal));
            Assert.DoesNotContain(warnings, w => w.Contains("OTG MLT 28R 15:", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The rest of the transmission goes with the missed block, exactly as a fire-time failure discards
    /// its chain: the speed was issued for the circuit the aircraft is no longer flying.
    /// </summary>
    [Fact]
    public void OtgMlt_FullStop_DiscardsTheRestOfItsTransmission()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            var warnings = new List<string>();
            engine.WarningEmitted += (_, warning) => warnings.Add(warning);

            var ac = RestoreAt(engine, archive, FinalBeforeTouchAndGoTime);
            if (ac is null)
            {
                return;
            }

            Assert.True(engine.SendCommand(Callsign, "CLAND").Success);
            var otg = engine.SendCommand(Callsign, "OTG MLT 28L; SPD 200");
            Assert.True(otg.Success, $"OTG MLT 28L; SPD 200 was refused: {otg.Message}");
            Assert.Equal(2, ac.Queue.Blocks.Count);

            var afterRollout = FlyToRollout(engine);
            Assert.NotNull(afterRollout);
            output.WriteLine($"after rollout: {afterRollout.Queue.Blocks.Count} blocks, warnings: {string.Join(" | ", warnings)}");

            Assert.DoesNotContain(afterRollout.Queue.Blocks, b => (b.SourceCommandText ?? "").Contains("SPD 200", StringComparison.Ordinal));
            Assert.DoesNotContain(afterRollout.Queue.Blocks, b => b.Trigger?.Type == BlockTriggerType.AfterCycleTerminator);
            Assert.Contains(warnings, w => w.Contains("rest of transmission discarded", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The same verbatim-quoting rule on the fire-time failure path: an <c>OTG</c> block whose inner
    /// command is refused when it fires is quoted as issued. Through the runway de-padder the pattern
    /// altitude <c>015</c> would come back as <c>15</c> — a runway, in that slot — so the RPO would be
    /// shown an instruction nobody typed.
    /// </summary>
    [Fact]
    public void OtgMlt_FailingAtFireTime_QuotesTheSourceTextVerbatim()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            var warnings = new List<string>();
            engine.WarningEmitted += (_, warning) => warnings.Add(warning);

            var ac = RestoreAt(engine, archive, FinalBeforeTouchAndGoTime);
            if (ac is null)
            {
                return;
            }

            // OAK has no runway 01L, so the block fires on the climb-out and is refused there.
            var otg = engine.SendCommand(Callsign, "OTG MLT 01L 015");
            Assert.True(otg.Success, $"OTG MLT 01L 015 was refused at issue: {otg.Message}");

            for (int t = 1; (t <= MaxTicks) && (warnings.Count == 0); t++)
            {
                engine.TickOneSecond();
            }

            output.WriteLine($"warnings: {string.Join(" | ", warnings)}");
            Assert.Contains(warnings, w => w.Contains("OTG MLT 01L 015", StringComparison.Ordinal));
            Assert.DoesNotContain(warnings, w => w.Contains("MLT 1L 15", StringComparison.Ordinal));
            Assert.Equal("28R", engine.FindAircraft(Callsign)?.Phases?.AssignedRunway?.Designator);
        }
    }

    /// <summary>Ticks until the aircraft is off the runway after its full-stop landing.</summary>
    private AircraftState? FlyToRollout(SimulationEngine engine)
    {
        for (int t = 1; t <= MaxTicks; t++)
        {
            engine.TickOneSecond();
            var live = engine.FindAircraft(Callsign);
            if (live is null)
            {
                return null;
            }

            if (live.Phases?.CurrentPhase is RunwayExitPhase or HoldingAfterExitPhase)
            {
                return live;
            }
        }

        return null;
    }

    /// <summary>
    /// A rewind, a replay, or session persistence reloads the queue from a snapshot mid-cycle. Both the
    /// trigger and its latch have to survive, or a block armed during the touch-and-go re-arms against a
    /// terminator that has already happened and waits for the next one.
    /// </summary>
    [Fact]
    public void OtgMlt_QueuedBlockAndLatchSurviveSnapshotRoundTrip()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            var ac = RestoreAt(engine, archive, FinalBeforeTouchAndGoTime);
            if (ac is null)
            {
                return;
            }

            var result = engine.SendCommand(Callsign, "OTG MLT 28L");
            Assert.True(result.Success, $"OTG MLT 28L was refused: {result.Message}");

            var beforeLatch = CommandQueue.FromSnapshot(ac.Queue.ToSnapshot());
            var armed = Assert.Single(beforeLatch.Blocks, b => b.Trigger?.Type == BlockTriggerType.AfterCycleTerminator);
            Assert.False(armed.TriggerTerminatorObserved);

            AircraftState? latched = null;
            for (int t = 1; t <= MaxTicks; t++)
            {
                engine.TickOneSecond();
                var live = engine.FindAircraft(Callsign);
                Assert.NotNull(live);

                if (live.Queue.Blocks.Exists(b => b.TriggerTerminatorObserved))
                {
                    latched = live;
                    break;
                }
            }

            Assert.NotNull(latched);
            output.WriteLine($"latched during {latched.Phases?.CurrentPhase?.GetType().Name}");

            var restored = CommandQueue.FromSnapshot(latched.Queue.ToSnapshot());
            Assert.Contains(restored.Blocks, b => (b.Trigger?.Type == BlockTriggerType.AfterCycleTerminator) && b.TriggerTerminatorObserved);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("PatternCommandHandler", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData());
    }

    /// <summary>
    /// Hybrid restore: load the scenario, then drop straight into the snapshot at
    /// <paramref name="elapsedSeconds"/>. Returns null when the fixture is unavailable.
    /// </summary>
    private AircraftState? RestoreAt(SimulationEngine engine, RecordingArchive archive, int elapsedSeconds)
    {
        engine.Replay(archive.ToBaseSessionRecording(), 0);

        var snapshot = archive.ReadSnapshotAt(elapsedSeconds);
        if (snapshot is null)
        {
            output.WriteLine($"No snapshot near t={elapsedSeconds} — skipping");
            return null;
        }

        engine.RestoreFromSnapshot(snapshot.State);

        var ac = engine.FindAircraft(Callsign);
        if (ac is null)
        {
            output.WriteLine($"{Callsign} is not in the t={elapsedSeconds} snapshot — skipping");
        }

        return ac;
    }

    /// <summary>
    /// The queued block carries the OTG condition labels, and its source text re-parses into the same
    /// block — the rehydration path a restored queue depends on.
    /// </summary>
    private static void AssertQueuedBlockDescribesAndRoundTrips(AircraftState aircraft)
    {
        var block = Assert.Single(aircraft.Queue.Blocks, b => b.Trigger?.Type == BlockTriggerType.AfterCycleTerminator);

        Assert.Equal("on the go: ", block.DescriptionPrefix);
        Assert.Equal("On the go: ", block.NaturalDescriptionPrefix);
        Assert.StartsWith("on the go: ", block.Description, StringComparison.Ordinal);
        Assert.StartsWith("On the go: ", block.NaturalDescription, StringComparison.Ordinal);

        Assert.NotNull(block.SourceCommandText);
        var reparsed = CommandParser.ParseCompound(block.SourceCommandText!);
        Assert.True(reparsed.IsSuccess, reparsed.Reason);
        var reparsedBlock = Assert.Single(reparsed.Value!.Blocks);
        Assert.IsType<OnTheGoCondition>(reparsedBlock.Condition);
        Assert.Equal("28L", Assert.IsType<MakeLeftTrafficCommand>(Assert.Single(reparsedBlock.Commands)).RunwayId);
    }

    /// <summary>
    /// Ticks until the queued MLT applies, asserting it stays queued for as long as the aircraft is in
    /// one of <paramref name="phasesBeforeTheClimbOut"/>, and that it fires within
    /// <see cref="MaxTicksFromUpwindToFire"/> seconds of the upwind starting. Returns whether the last
    /// of those phases (the cycle terminator) was actually seen.
    /// </summary>
    private bool FlyUntilTheBlockFires(SimulationEngine engine, Type[] phasesBeforeTheClimbOut)
    {
        var terminatorPhase = phasesBeforeTheClimbOut[^1];
        bool sawTerminator = false;
        int upwindAt = -1;

        for (int t = 1; t <= MaxTicks; t++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);

            var phase = ac.Phases?.CurrentPhase;
            sawTerminator |= (phase is not null) && (phase.GetType() == terminatorPhase);

            if ((phase is UpwindPhase) && (upwindAt < 0))
            {
                upwindAt = t;
                output.WriteLine($"upwind at +{t}s");
            }

            if (string.Equals(ac.Phases?.AssignedRunway?.Designator, "28L", StringComparison.OrdinalIgnoreCase))
            {
                output.WriteLine($"OTG block fired at +{t}s during {phase?.GetType().Name}");
                Assert.True(upwindAt >= 0, $"the block fired during {phase?.GetType().Name}, before the climb-out upwind began");
                Assert.True(
                    (t - upwindAt) <= MaxTicksFromUpwindToFire,
                    $"the block fired {t - upwindAt}s after the upwind began, beyond the {MaxTicksFromUpwindToFire}s the climb-out allows"
                );

                AssertTransitionInstalled(ac);
                return sawTerminator;
            }

            if (Array.Exists(phasesBeforeTheClimbOut, p => (phase is not null) && (phase.GetType() == p)))
            {
                Assert.Equal(PatternDirection.Right, ac.Phases?.TrafficDirection);
                Assert.False(ac.Queue.IsComplete, $"the OTG block fired during {phase?.GetType().Name}, before the climb-out");
            }
        }

        Assert.Fail($"{Callsign} never picked up the queued MLT 28L within {MaxTicks} ticks");
        return sawTerminator;
    }

    /// <summary>
    /// The pattern change installs the leg-to-leg transition circuit: 28L left traffic, no midfield
    /// crossing, and a crosswind turn point beyond both parallel runways' departure ends.
    /// </summary>
    private void AssertTransitionInstalled(AircraftState aircraft)
    {
        var rwy28L = NavigationDatabase.Instance.GetRunway(AirportId, "28L")!;
        var rwy28R = NavigationDatabase.Instance.GetRunway(AirportId, "28R")!;

        var chain = aircraft.Phases?.Phases ?? [];
        output.WriteLine($"chain=[{string.Join(",", chain.Select(p => $"{p.GetType().Name}:{p.Status}"))}]");

        Assert.Equal("28L", aircraft.Phases?.AssignedRunway?.Designator);
        Assert.Equal(PatternDirection.Left, aircraft.Phases?.TrafficDirection);
        Assert.DoesNotContain(chain, p => p is MidfieldCrossingPhase);

        var upwind = Assert.IsType<UpwindPhase>(aircraft.Phases?.CurrentPhase);
        var waypoints = upwind.Waypoints;
        Assert.NotNull(waypoints);

        double turnAlongTrack = AlongTrack(rwy28L, waypoints.CrosswindTurnLat, waypoints.CrosswindTurnLon);
        double patternDer = AlongTrack(rwy28L, rwy28L.EndLatitude, rwy28L.EndLongitude);
        double otherDer = AlongTrack(rwy28L, rwy28R.EndLatitude, rwy28R.EndLongitude);
        output.WriteLine($"crosswind turn along-track={turnAlongTrack:F3} nm, 28L DER={patternDer:F3}, 28R DER={otherDer:F3}");

        Assert.True(
            turnAlongTrack >= patternDer - 0.001,
            $"crosswind turn point is {turnAlongTrack:F3} nm along 28L, short of its own departure end at {patternDer:F3} nm"
        );
        Assert.True(
            turnAlongTrack >= otherDer - 0.001,
            $"crosswind turn point is {turnAlongTrack:F3} nm along 28L, short of 28R's departure end at {otherDer:F3} nm"
        );
    }

    private static double AlongTrack(RunwayInfo runway, double lat, double lon) =>
        GeoMath.AlongTrackDistanceNm(new LatLon(lat, lon), new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading);
}
