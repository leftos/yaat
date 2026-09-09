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
    // A chain led by a scoped special whose argument arm takes any tail: "HO 3G; PAUSE" parses whole as a handoff to
    // the TCP "3G; PAUSE", so a guard that treats any successful single parse as "not a chain" never sees the PAUSE.
    [InlineData("HO 3G; PAUSE", typeof(PauseCommand))]
    [InlineData("SP1 ABC; PAUSE", typeof(PauseCommand))]
    [InlineData("ACCEPT 3G; PAUSE", typeof(PauseCommand))]
    // The bare form was already found — "ACCEPT;" matches no alias, so the whole-line parse fails and the walk runs.
    [InlineData("ACCEPT; PAUSE", typeof(PauseCommand))]
    // A strip annotation is deliberately not free text (the pinned "AN 1 X; SQVFR" chain), so a chained PAUSE behind
    // one is refused rather than swallowed into the annotation as the text "X, PAUSE".
    [InlineData("AN 1 X, PAUSE", typeof(PauseCommand))]
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
    /// With a space before the separator the leading verb's token is bare, so <c>CommandParser.Parse</c> reads the whole
    /// line as that verb with <c>"; H 180"</c> as its argument — which no arm accepts, so it comes back as an
    /// <see cref="UnsupportedCommand"/>. A successful whole-line parse is not enough to call the line one command: only
    /// a genuinely free-text command (NOTE/RMK, a coordination message) owns its separators, so the walk runs and finds
    /// the chained <c>ASSUME</c>.
    /// </summary>
    [Fact]
    public void AChainLedByASpacedBareVerb_IsSeenAsAChain()
    {
        var found = CompoundPolicy.FindNonCompoundableInChain("ASSUME ; H 180");

        Assert.NotNull(found);
        Assert.IsType<AssumeCommand>(found);
    }

    /// <summary>
    /// <c>THEN</c> and <c>AND</c> are the separators spelled as words, so a line written with them gets the verdict the
    /// same line written with <c>;</c>/<c>,</c> gets. The <c>AND</c> rows are the shapes that broke while the head
    /// typed before a free-text message was sliced by an offset into the typed line: the head kept the alias word, and
    /// <c>"PAUSE AND"</c> re-parses as a block ending in a separator with nothing after it ("empty command"), so the
    /// refusal was lost and the PAUSE dispatched. (A trailing <c>;</c> is tolerated by the parser and a trailing
    /// <c>,</c> is not, which is the only reason the <c>THEN</c> rows survived it.)
    /// </summary>
    [Theory]
    [InlineData("PAUSE; RDTXT /1 HOLD", "PAUSE THEN RDTXT /1 HOLD", typeof(PauseCommand))]
    [InlineData("PAUSE, RDTXT /1 HOLD", "PAUSE AND RDTXT /1 HOLD", typeof(PauseCommand))]
    [InlineData("HO 3G, PAUSE, RDTXT /1 HOLD", "HO 3G AND PAUSE AND RDTXT /1 HOLD", typeof(PauseCommand))]
    [InlineData("FH 090; PAUSE", "FH 090 THEN PAUSE", typeof(PauseCommand))]
    [InlineData("SP1 ABC, PAUSE", "SP1 ABC AND PAUSE", typeof(PauseCommand))]
    public void NonCompoundableVerdict_IsTheSameForAliasSeparators(string typed, string aliased, Type expectedType)
    {
        Assert.IsType(expectedType, CompoundPolicy.FindNonCompoundableInChain(typed));
        Assert.IsType(expectedType, CompoundPolicy.FindNonCompoundableInChain(aliased));
    }

    /// <summary>The paired-turn refusal is the same verdict either way — the alias form must not clear for takeoff.</summary>
    [Theory]
    [InlineData("CTO, R270; RDTXT /1 HOLD", "CTO, R270 THEN RDTXT /1 HOLD", typeof(MakeRight270Command))]
    [InlineData("CTO, R270, RDTXT /1 HOLD", "CTO, R270 AND RDTXT /1 HOLD", typeof(MakeRight270Command))]
    [InlineData("CTO, R270", "CTO AND R270", typeof(MakeRight270Command))]
    public void PairedTurnVerdict_IsTheSameForAliasSeparators(string typed, string aliased, Type expectedType)
    {
        Assert.IsType(expectedType, CompoundPolicy.FindTakeoffPairedWithImmediateTurn(typed));
        Assert.IsType(expectedType, CompoundPolicy.FindTakeoffPairedWithImmediateTurn(aliased));
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
    // Behind a scoped special that swallows the tail on a whole-line parse, the pairing still has to be found.
    [InlineData("SP1 ABC; CTO, R270", typeof(MakeRight270Command))]
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
