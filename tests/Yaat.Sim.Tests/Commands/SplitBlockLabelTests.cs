using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// A queued conditional block labels itself with its condition — <c>AT OAK FH 270, HO 2W</c> is described as
/// <c>"at OAK: FH 270, HO 2W"</c> and narrated as <c>"At OAK: Fly heading 270, Initiate handoff to 2W"</c>. Those
/// strings are what the conditional-list UI shows (<c>ConditionalList.Describe</c>) and what is broadcast when the
/// trigger fires (<c>FlightPhysics</c>). When a later same-axis immediate command supersedes only part of the block,
/// <c>SplitBlockNonConflicting</c> rebuilds it from the surviving commands — and must keep the condition label, or the
/// queue entry silently degrades to a bare <c>"HO 2W"</c> that no longer says when it will happen.
/// </summary>
public class SplitBlockLabelTests : IDisposable
{
    private readonly IDisposable _navScope;

    public SplitBlockLabelTests(ITestOutputHelper output)
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
    public void SupersedingLateral_KeepsConditionLabelOnSplitBlock()
    {
        var ac = MakeAirborne();

        DispatchOk(ac, "AT OAK FH 270, HO 2W");
        var queued = Assert.Single(ac.Queue.Blocks);
        Assert.Equal("at OAK: FH 270, HO 2W", queued.Description);
        Assert.Equal("At OAK: Fly heading 270, Initiate handoff to 2W", queued.NaturalDescription);

        // Supersede the FH half; the HO survives and the block is rebuilt around it.
        DispatchOk(ac, "FH 090");

        var survivor = Assert.Single(ac.Queue.Blocks, b => b.Trigger is { Type: BlockTriggerType.ReachFix });
        Assert.Equal("at OAK: HO 2W", survivor.Description);
        Assert.Equal("At OAK: Initiate handoff to 2W", survivor.NaturalDescription);
    }

    [Fact]
    public void SupersedingLateral_LeavesUnconditionalBlockLabelUnprefixed()
    {
        var ac = MakeAirborne();

        // No condition: the block carries no label prefix, and the split must not invent one.
        DispatchOk(ac, "CM 150, HO 2W");

        var survivor = Assert.Single(ac.Queue.Blocks, b => (b.ParsedCommands is not null) && b.ParsedCommands.Exists(TrackEngine.IsTrackCommand));
        Assert.DoesNotContain(":", survivor.Description);
    }
}
