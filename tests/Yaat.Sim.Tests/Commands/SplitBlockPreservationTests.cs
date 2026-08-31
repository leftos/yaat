using System.Reflection;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// <c>CommandDispatcher.SplitBlockNonConflicting</c> is a second construction path parallel to
/// <c>CreateBlock</c>/<c>EnqueueBlocks</c>: when a supersede partially conflicts with a queued
/// conditional block, the survivors are rebuilt into a fresh block. Two production bugs (#281
/// HasTrackCommand loss, the condition-label loss) came from fields this rebuild forgot. These
/// tests pin the full contract: every <see cref="CommandBlock"/> property is either re-derived by
/// CreateBlock from the kept commands or explicitly copied from the block being replaced — and a
/// NEW property added to CommandBlock fails the reflection pin below until someone decides which.
/// </summary>
public class SplitBlockPreservationTests : IDisposable
{
    private readonly IDisposable _navScope;

    public SplitBlockPreservationTests(ITestOutputHelper output)
    {
        TestVnasData.EnsureInitialized();
        _navScope = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
    }

    public void Dispose() => _navScope.Dispose();

    /// <summary>
    /// Every settable/init property of CommandBlock, each tagged with how the split rebuild covers
    /// it. Adding a property to CommandBlock fails this test until it is added here AND handled in
    /// SplitBlockNonConflicting (copied) or CreateBlock (re-derived) — the drift that produced
    /// issue #281.
    /// </summary>
    private static readonly Dictionary<string, string> SplitCoverage = new()
    {
        // Re-derived by CreateBlock from (keptParsed, trigger, labels, sourceText):
        ["Trigger"] = "CreateBlock",
        ["Commands"] = "CreateBlock",
        ["Dimensions"] = "CreateBlock",
        ["ParsedCommands"] = "CreateBlock",
        ["IsWaitBlock"] = "CreateBlock",
        ["Description"] = "CreateBlock",
        ["NaturalDescription"] = "CreateBlock",
        ["DescriptionPrefix"] = "CreateBlock",
        ["NaturalDescriptionPrefix"] = "CreateBlock",
        ["ApplyAction"] = "CreateBlock",
        ["SourceCommandText"] = "CreateBlock",
        ["HasTrackCommand"] = "CreateBlock",
        ["HasDeleteCommand"] = "CreateBlock",
        // Runtime state explicitly copied by SplitBlockNonConflicting:
        ["WaitRemainingSeconds"] = "copied",
        ["WaitRemainingDistanceNm"] = "copied",
        ["TrackApplied"] = "copied",
        ["IsApplied"] = "copied",
        ["TriggerMet"] = "copied",
        ["TriggerCrossingObserved"] = "copied",
        ["TriggerMissed"] = "copied",
        ["TriggerClosestApproach"] = "copied",
    };

    [Fact]
    public void EveryCommandBlockProperty_HasDeclaredSplitCoverage()
    {
        var settable = typeof(CommandBlock)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(settable, SplitCoverage.Keys.OrderBy(n => n).ToList());
    }

    [Fact]
    public void SplitConditionalBlock_PreservesEveryField()
    {
        var ac = new AircraftState
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

        var ctx = TestDispatch.Context(Random.Shared, validateDctFixes: false);
        var parsed = CommandParser.ParseCompound("AT OAK FH 270, HO 2W");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var dispatchResult = CommandDispatcher.DispatchCompound(parsed.Value!, ac, ctx);
        Assert.True(dispatchResult.Success, dispatchResult.Message);

        var original = Assert.Single(ac.Queue.Blocks);
        var sourceText = original.SourceCommandText;
        Assert.False(string.IsNullOrEmpty(sourceText));

        // Distinctive runtime state the rebuild must carry over verbatim.
        original.TriggerMet = true;
        original.TriggerCrossingObserved = true;
        original.TriggerMissed = true;
        original.TriggerClosestApproach = 3.25;
        original.TrackApplied = true;

        // Fresh immediate lateral supersede: conflicts with the FH half only → block is split.
        var supersede = CommandParser.ParseCompound("FH 090");
        Assert.True(supersede.IsSuccess, supersede.Reason);
        var supersedeResult = CommandDispatcher.DispatchCompound(supersede.Value!, ac, ctx);
        Assert.True(supersedeResult.Success, supersedeResult.Message);

        var survivor = Assert.Single(ac.Queue.Blocks, b => b.Trigger is { Type: BlockTriggerType.ReachFix });
        Assert.NotSame(original, survivor);

        // Re-derived by CreateBlock from the surviving HO:
        Assert.NotNull(survivor.ApplyAction);
        Assert.NotNull(survivor.ParsedCommands);
        var kept = Assert.Single(survivor.ParsedCommands!);
        Assert.True(TrackEngine.IsTrackCommand(kept));
        Assert.True(survivor.HasTrackCommand);
        Assert.False(survivor.HasDeleteCommand);
        Assert.False(survivor.IsWaitBlock);
        Assert.Equal(sourceText, survivor.SourceCommandText);
        Assert.StartsWith("at OAK: ", survivor.Description, StringComparison.Ordinal);
        Assert.Equal("at OAK: ", survivor.DescriptionPrefix);
        Assert.Single(survivor.Commands);

        // Runtime state explicitly copied:
        Assert.True(survivor.TriggerMet, "TriggerMet latch lost — the rebuilt block would re-arm against a passed fix");
        Assert.True(survivor.TriggerCrossingObserved);
        Assert.True(survivor.TriggerMissed);
        Assert.Equal(3.25, survivor.TriggerClosestApproach);
        Assert.True(survivor.TrackApplied, "TrackApplied guard lost — an already-fired handoff would re-dispatch");
        Assert.False(survivor.IsApplied);
    }
}
