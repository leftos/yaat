using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Nightly-review regression: a queued conditional block that mixes a dimensional command with a track
/// command — e.g. <c>AT OAK FH 270, HO 2W</c> — carries <see cref="CommandBlock.HasTrackCommand"/> = true
/// and keeps the track command out of its <c>ApplyAction</c> (see <c>CommandDispatcher.EnqueueBlocks</c>),
/// so <c>SimulationEngine.ProcessTriggeredTrackBlocks</c> routes the handoff to the track engine when the
/// trigger fires. When a later fresh immediate command on the same axis (<c>FH 090</c>) supersedes the
/// dimensional half, <c>SplitBlockNonConflicting</c> rebuilds the block from the non-conflicting parsed
/// commands (the track command survives in <c>ParsedCommands</c>) — but it does NOT carry
/// <c>HasTrackCommand</c> forward and rebuilds the <c>ApplyAction</c> from the kept commands (which still
/// include the track command). After the split the surviving block therefore (a) is skipped by
/// <c>ProcessTriggeredTrackBlocks</c> so the handoff never fires, and (b) drives the track command into
/// <c>ApplyCommand</c>'s no-dispatcher-arm default at fire time.
/// </summary>
public class SplitBlockTrackCommandTests : IDisposable
{
    private readonly IDisposable _navScope;

    public SplitBlockTrackCommandTests(ITestOutputHelper output)
    {
        TestVnasData.EnsureInitialized();
        _navScope = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
    }

    public void Dispose() => _navScope.Dispose();

    private static AircraftState MakeAirborne() =>
        new()
        {
            Callsign = "N435C",
            AircraftType = "B738",
            Position = new LatLon(37.62, -122.19),
            TrueHeading = new TrueHeading(340),
            Altitude = 10_000,
            IndicatedAirspeed = 250,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                FlightRules = "IFR",
                Destination = "KOAK",
            },
        };

    private static DispatchContext Ctx() => TestDispatch.Context(Random.Shared, validateDctFixes: false);

    private static void DispatchOk(AircraftState ac, string text)
    {
        var parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, Ctx());
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public void SupersedingLateral_PreservesTrackCommandFlagOnSplitConditionalBlock()
    {
        var ac = MakeAirborne();

        // Queue one conditional block that pairs a lateral command (FH, Lateral) with a handoff
        // (HO, dimension None). The block is enqueued with HasTrackCommand = true and its ApplyAction
        // excludes the track command.
        DispatchOk(ac, "AT OAK FH 270, HO 2W");
        var queued = Assert.Single(ac.Queue.Blocks);
        Assert.True(queued.HasTrackCommand, "precondition: mixed conditional block should flag its track command");
        Assert.NotNull(queued.ParsedCommands);
        Assert.Contains(queued.ParsedCommands!, TrackEngine.IsTrackCommand);

        // Fresh immediate lateral command supersedes: it conflicts with the FH half only, so the block is
        // split and rebuilt from the surviving (non-lateral) parsed commands — the HO.
        DispatchOk(ac, "FH 090");

        // The AT OAK conditional block still exists and still carries the handoff in ParsedCommands...
        var survivor = Assert.Single(ac.Queue.Blocks, b => b.Trigger is { Type: BlockTriggerType.ReachFix });
        Assert.NotNull(survivor.ParsedCommands);
        Assert.Contains(survivor.ParsedCommands!, TrackEngine.IsTrackCommand);

        // ...so HasTrackCommand MUST stay true, or ProcessTriggeredTrackBlocks skips the block and the
        // handoff silently never fires (and the track command falls into the no-dispatcher-arm default).
        Assert.True(survivor.HasTrackCommand, "split conditional block retained a track command in ParsedCommands but lost HasTrackCommand");
    }
}
