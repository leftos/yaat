using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// A deferred dispatch must restore with the same payload it was created with.
///
/// <c>DeferredDispatch</c> stores the full original command text (gate included) and rebuilds its payload by
/// re-parsing that text on restore — so the restored payload carries the gate again. When it fires it re-enters the
/// deferral path instead of dispatching: a <c>WAIT</c> restarts its whole countdown, and a <c>BEHIND</c> whose target
/// has since been deleted is rejected outright, discarding the clearance. That makes a rewind or replay of a timeline
/// diverge from the live session it came from.
/// </summary>
public sealed class DeferredDispatchRestoreTests(ITestOutputHelper output)
{
    static DeferredDispatchRestoreTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeAirborneAircraft()
    {
        return new AircraftState
        {
            Callsign = "SWA100",
            AircraftType = "B738",
            Position = new LatLon(37.62, -122.38),
            TrueHeading = new TrueHeading(280),
            Altitude = 8000,
            IndicatedAirspeed = 250,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };
    }

    private static DeferredDispatch DispatchAndTakeDeferral(string command, AircraftState aircraft)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);

        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(Random.Shared));
        Assert.True(result.Success, result.Message);

        return Assert.Single(aircraft.DeferredDispatches);
    }

    [Fact]
    public void RestoredWaitDeferral_KeepsTheStrippedPayload_SoItDoesNotRestartItsCountdown()
    {
        DeferredDispatch deferral = DispatchAndTakeDeferral("WAIT 120 FH 090", MakeAirborneAircraft());

        // Precondition: the live payload has the WAIT gate stripped off, so this cannot pass vacuously.
        Assert.DoesNotContain(deferral.Payload.Blocks.SelectMany(b => b.Commands), c => c is WaitCommand);

        var restored = DeferredDispatch.FromSnapshot(deferral.ToSnapshot(), null);

        Assert.NotNull(restored);
        Assert.Equal(deferral.RemainingSeconds, restored.RemainingSeconds);
        Assert.DoesNotContain(restored.Payload.Blocks.SelectMany(b => b.Commands), c => c is WaitCommand);
    }

    [Fact]
    public void RestoredGiveWayDeferral_KeepsTheStrippedPayload_SoItDoesNotReenterTheGate()
    {
        const string SourceText = "BEHIND KLM605 TAXI A B";

        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(SourceText);
        Assert.True(parsed.IsSuccess, parsed.Reason);

        // Precondition: the stored text really does carry the gate, which is what restore has to strip.
        Assert.IsType<GiveWayCondition>(parsed.Value!.Blocks[0].Condition);

        // Mirror TryDeferGiveWay's construction — the payload has the condition stripped off its first block while
        // the deferral retains the full original text. Built directly rather than dispatched because the give-way
        // admission rules (ground, taxi state, resolvable target) are not what this test is about.
        var payload = new CompoundCommand([new ParsedBlock(null, parsed.Value.Blocks[0].Commands)]) { SourceText = SourceText };
        var deferral = new DeferredDispatch(payload, "KLM605") { SourceText = SourceText };

        Assert.IsNotType<GiveWayCondition>(deferral.Payload.Blocks[0].Condition);

        var restored = DeferredDispatch.FromSnapshot(deferral.ToSnapshot(), null);

        Assert.NotNull(restored);
        Assert.Equal(deferral.GiveWayTarget, restored.GiveWayTarget);
        Assert.IsNotType<GiveWayCondition>(restored.Payload.Blocks[0].Condition);
    }

    /// <summary>
    /// A deferred GIVEWAY's route preview keeps no state, so a session restored while it waits releases on the same second as the
    /// run it was taken from (YAAT-1266).
    /// </summary>
    [Fact]
    public void DeferredGiveWay_RestoredMidHold_ReleasesOnTheSameTick()
    {
        const string BundlePath = "TestData/sfo-gc-28rt-recording.yaat-bug-report-bundle.zip";
        var groundData = RecordingGroundData.VersionPinned(BundlePath, "UAL2237's GIVEWAY to UAL926 crossing toward F");
        if (groundData.GetLayout("SFO") is null)
        {
            return;
        }

        RecordingArchive? archive = RecordingLoader.OpenArchive(BundlePath);
        Assert.NotNull(archive);

        using (archive)
        {
            var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
            engine.Replay(archive.ToBaseSessionRecording(), 2080);
            engine.TickOneSecond();
            engine.TickOneSecond();
            AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft("UAL2237"));
            AircraftState traffic = Assert.IsType<AircraftState>(engine.FindAircraft("UAL926"));
            output.WriteLine($"t=2082 headings {held.TrueHeading.Degrees:F1}/{traffic.TrueHeading.Degrees:F1}");
            double apart = Math.Abs(held.TrueHeading.Degrees - traffic.TrueHeading.Degrees);
            Assert.True(Math.Min(apart, 360 - apart) is >= 60 and <= 120, "test setup: the pair must be in the 60-120 degree band");
            CommandResult result = engine.SendCommand("UAL2237", "GIVEWAY UAL926 TAXI GL L LF F 28L");
            Assert.True(result.Success, result.Message);
            for (int t = 0; t < 10; t++)
            {
                engine.TickOneSecond();
            }

            Assert.True(GiveWayPending(engine), "test setup: the deferral should still be waiting 10 s in");
            StateSnapshotDto snapshot = engine.CaptureSnapshot();

            int live = SecondsUntilRelease(engine);
            engine.RestoreFromSnapshot(snapshot);
            int restored = SecondsUntilRelease(engine);
            output.WriteLine($"released {live} s after the snapshot at t=2092, {restored} s after the restore");

            Assert.Equal(live, restored);
        }
    }

    private static bool GiveWayPending(SimulationEngine engine) =>
        engine.FindAircraft("UAL2237")?.DeferredDispatches.Exists(d => d.GiveWayTarget == "UAL926") == true;

    private static int SecondsUntilRelease(SimulationEngine engine)
    {
        for (int seconds = 1; seconds <= 400; seconds++)
        {
            engine.TickOneSecond();
            if (!GiveWayPending(engine))
            {
                return seconds;
            }
        }

        Assert.Fail("the deferred GIVEWAY never released");
        return 0;
    }

    private static AircraftState MakeArrivalOnStar(string route)
    {
        AircraftState aircraft = MakeAirborneAircraft();
        aircraft.FlightPlan.Route = route;
        aircraft.Procedure.ActiveStarId = "BDEGA3";
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "SUNOL", Position = new LatLon(37.59, -121.80) });
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "CEDES", Position = new LatLon(37.6, -121.6) });
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "EDDYY", Position = new LatLon(37.6, -121.4) });
        return aircraft;
    }

    /// <summary>
    /// A WAIT-deferred route-relative <c>DCT</c> must restore so it fires to the same aircraft state it would have live.
    ///
    /// Live, the payload is parsed with the aircraft's filed route (ActionArms.Aviation threads it through), so
    /// <c>DCT SUNOL</c> against a route of "SUNOL CEDES EDDYY" resolves to all three fixes. When the WAIT fires,
    /// <c>ApplyDirectTo</c> sees <c>Fixes.Count &gt; 1</c>, skips <c>TryPreserveProcedure</c>, and clears the active STAR.
    /// On restore, <c>DeferredDispatch.FromSnapshot</c> re-parses the stored text with NO route, so the payload resolves
    /// to the single typed fix; the firing then takes the <c>Fixes.Count == 1</c> branch into <c>TryPreserveProcedure</c>,
    /// which KEEPS the active STAR. The two paths diverge — a replay/determinism break on any timeline rewind or restore.
    /// </summary>
    [Fact]
    public void RestoredWaitDeferral_WithRouteRelativeDirectTo_FiresToTheSameProcedureStateAsLive()
    {
        const string Route = "SUNOL CEDES EDDYY";
        const string Command = "WAIT 60 DCT SUNOL";

        // Live deferral: parse with the aircraft route exactly as the aviation arm does before dispatch.
        AircraftState live = MakeArrivalOnStar(Route);
        ParseResult<CompoundCommand> parsedLive = CommandParser.ParseCompound(Command, live.FlightPlan.Route);
        Assert.True(parsedLive.IsSuccess, parsedLive.Reason);
        CommandResult liveResult = CommandDispatcher.DispatchCompound(
            parsedLive.Value!,
            live,
            TestDispatch.Context(Random.Shared, validateDctFixes: false)
        );
        Assert.True(liveResult.Success, liveResult.Message);
        DeferredDispatch liveDeferral = Assert.Single(live.DeferredDispatches);

        // Restore the deferral from its snapshot — the path AircraftState.FromSnapshot takes on a rewind / reconstruction.
        var restored = DeferredDispatch.FromSnapshot(liveDeferral.ToSnapshot(), live.FlightPlan.Route);
        Assert.NotNull(restored);

        // Fire each payload against an identical fresh arrival and compare the resulting procedure state.
        AircraftState afterLive = MakeArrivalOnStar(Route);
        CommandDispatcher.DispatchCompound(liveDeferral.Payload, afterLive, TestDispatch.Context(Random.Shared, validateDctFixes: false));

        AircraftState afterRestore = MakeArrivalOnStar(Route);
        CommandDispatcher.DispatchCompound(restored!.Payload, afterRestore, TestDispatch.Context(Random.Shared, validateDctFixes: false));

        Assert.Equal(afterLive.Procedure.ActiveStarId, afterRestore.Procedure.ActiveStarId);
        Assert.Equal(afterLive.Targets.NavigationRoute.Select(f => f.Name), afterRestore.Targets.NavigationRoute.Select(f => f.Name));
    }

    /// <summary>
    /// The command-run reaction delay stores the whole compound as its payload with no gate in the text, so restore
    /// must leave it intact — stripping unconditionally would eat a real command.
    /// </summary>
    [Fact]
    public void RestoredReactionDelay_KeepsItsWholePayload()
    {
        AircraftState aircraft = MakeAirborneAircraft();
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound("FH 090");
        Assert.True(parsed.IsSuccess, parsed.Reason);

        var deferral = new DeferredDispatch(5.0, parsed.Value!) { SourceText = "FH 090", IsReactionDelay = true };
        aircraft.DeferredDispatches.Add(deferral);

        var restored = DeferredDispatch.FromSnapshot(deferral.ToSnapshot(), null);

        Assert.NotNull(restored);
        Assert.True(restored.IsReactionDelay);
        Assert.Single(restored.Payload.Blocks);
        Assert.NotEmpty(restored.Payload.Blocks[0].Commands);
    }
}
