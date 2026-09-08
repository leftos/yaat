using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// The shared chained non-compoundable verdict (<see cref="CompoundPolicy"/>) — consumed by both
/// the server's dispatch routing (RoomEngine) and the client's pre-send validation (MainViewModel),
/// so these tests pin the verdicts for both sides at once.
/// </summary>
public class CompoundPolicyTests
{
    [Theory]
    [InlineData("FH 090; PAUSE", typeof(PauseCommand))]
    [InlineData("FH 090; SPAWN", typeof(SpawnNowCommand))]
    [InlineData("CM 5000; SIMRATE 2", typeof(SimRateCommand))]
    // The live-traffic hand-off verbs: chained, the shadow gate would take the aircraft and the verb would then find
    // no arm, so the line is refused before anything is dispatched.
    [InlineData("ASSUME; H 180", typeof(AssumeCommand))]
    [InlineData("H 070; UNASSUME", typeof(UnassumeCommand))]
    public void ChainWithNonCompoundable_IsFound(string command, Type expectedType)
    {
        var found = CompoundPolicy.FindNonCompoundableInChain(command);
        Assert.NotNull(found);
        Assert.IsType(expectedType, found);
    }

    [Theory]
    [InlineData("FH 090; CM 5000")] // plain aviation chain
    [InlineData("CROSS 28R; DEL")] // DEL has real chain semantics (issue #311)
    [InlineData("AT 5000 APT OAK")] // DEST/APT dispatches through the queue
    [InlineData("PAUSE")] // single command, not a chain
    [InlineData("ASSUME")] // the lone hand-off verb is the supported form
    [InlineData("UNASSUME")] // ...as is the lone way back
    [InlineData("NOTE hold at gate; expect delay")] // free text the single parser accepts whole
    public void NonChains_AndChainCapableCommands_PassThrough(string command)
    {
        Assert.Null(CompoundPolicy.FindNonCompoundableInChain(command));
    }

    /// <summary>
    /// Pre-existing gap, pinned rather than fixed: with a space before the separator the leading verb's token is bare,
    /// so <c>CommandParser.Parse</c> reads the whole line as that verb with <c>"; H 180"</c> as its argument — which no
    /// arm accepts, so it comes back as an <see cref="UnsupportedCommand"/>. That is a successful single-command parse,
    /// which is where <c>TryParseGenuineCompound</c> bails, and the chain is never seen. Written <c>ASSUME; H 180</c>
    /// the token is <c>"ASSUME;"</c>, no verb matches, and the chain is found (above). This is why
    /// <c>CommandDispatcher</c> carries its own <c>ASSUME</c>/<c>UNASSUME</c> guard instead of relying on this
    /// predicate alone.
    /// </summary>
    [Fact]
    public void AChainLedByASpacedBareVerb_IsNotSeenAsAChain()
    {
        Assert.Null(CompoundPolicy.FindNonCompoundableInChain("ASSUME ; H 180"));
    }

    /// <summary>
    /// A coordination message runs to the end of the line, so the words in it are message text and never chained
    /// commands: only the head typed before the message is scanned. Scanned whole, "…RDTXT /1 HOLD, PAUSE" refused the
    /// line as a chained PAUSE. The same tail typed as a real chain is still found.
    /// </summary>
    [Fact]
    public void FreeTextCoordinationMessage_TailIsNotScannedForNonCompoundables()
    {
        Assert.Null(CompoundPolicy.FindNonCompoundableInChain("FH 090; RDTXT /1 HOLD, PAUSE"));

        var found = CompoundPolicy.FindNonCompoundableInChain("FH 090; PAUSE");
        Assert.NotNull(found);
        Assert.IsType<PauseCommand>(found);
    }

    /// <summary>
    /// A parallel block pairing a takeoff clearance with an immediate turn (`CTO, R270`) is the mis-spelling of
    /// the departure modifier `CTO MR270`: the turn is refused on the ground, so the block leaves the aircraft
    /// rolling with only half of what was typed. Both orders are found, and every takeoff clearance in the family
    /// (CTO, CTOPP, GO) counts.
    /// </summary>
    [Theory]
    [InlineData("CTO, R270", typeof(MakeRight270Command))]
    [InlineData("R270, CTO", typeof(MakeRight270Command))]
    [InlineData("CTOPP, R270", typeof(MakeRight270Command))]
    [InlineData("GO, L360", typeof(MakeLeft360Command))]
    public void TakeoffPairedWithImmediateTurn_IsFound(string command, Type expectedType)
    {
        var found = CompoundPolicy.FindTakeoffPairedWithImmediateTurn(command);
        Assert.NotNull(found);
        Assert.IsType(expectedType, found);
    }

    [Theory]
    [InlineData("CTO; R270")] // sequential: the turn queues until airborne, which is what the RPO asked for
    [InlineData("CTO MR270")] // the departure modifier itself
    [InlineData("R270")] // single command, not a pairing
    [InlineData("CTO, SQ 1234")] // a takeoff clearance may still be paired with other verbs
    public void TakeoffWithoutAPairedImmediateTurn_PassesThrough(string command)
    {
        Assert.Null(CompoundPolicy.FindTakeoffPairedWithImmediateTurn(command));
    }
}
