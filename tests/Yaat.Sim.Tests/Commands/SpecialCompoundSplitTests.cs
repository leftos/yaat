using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// <see cref="CompoundPolicy.TrySplitSpecialCompound"/>'s split/no-split verdict. The splitter is the router's gate
/// into per-unit routing (<c>ActionRouter.RouteUnits</c> re-enters <c>Route</c> for each unit), so a "split" that
/// hands back the input unchanged is an infinite recursion — the case a condition-prefixed scoped special
/// (<c>WAIT 1 AN 1 ✓</c>) produces, since the scheme expander turns one comma-less block into two commands.
/// </summary>
public class SpecialCompoundSplitTests
{
    public SpecialCompoundSplitTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// One comma-less block whose expansion is a condition plus a scoped special is one dispatch unit, not a compound:
    /// splitting it on <c>,</c> yields the input back, which the router would route again forever.
    /// </summary>
    [Fact]
    public void ConditionPrefixedScopedSpecial_IsNotASplit()
    {
        Assert.False(CompoundPolicy.TrySplitSpecialCompound("WAIT 1 AN 1 ✓", out var units));
        Assert.Empty(units);
    }

    /// <summary>Two scoped specials in one block still split on <c>,</c> — each dispatches alone.</summary>
    [Fact]
    public void CommaSeparatedScopedSpecials_SplitIntoOneUnitEach()
    {
        Assert.True(CompoundPolicy.TrySplitSpecialCompound("AN 1 X, AN 2 Y", out var units));

        Assert.Equal(2, units.Count);
        Assert.Equal("AN 1 X", units[0].Text);
        Assert.Equal("AN 2 Y", units[1].Text);
    }

    /// <summary>
    /// A genuine <c>;</c> chain around the same condition-prefixed block still splits, and its first unit is the whole
    /// block — routing that unit has to land on the single-command path via the same guard, or the recursion is only
    /// pushed down one level.
    /// </summary>
    [Fact]
    public void ConditionPrefixedScopedSpecial_ChainedWithASecondBlock_SplitsPerBlock()
    {
        Assert.True(CompoundPolicy.TrySplitSpecialCompound("WAIT 1 AN 1 ✓; HO 3G", out var units));

        Assert.Equal(2, units.Count);
        Assert.Equal("WAIT 1 AN 1 ✓", units[0].Text);
        Assert.Equal("HO 3G", units[1].Text);

        // The first unit re-entering the splitter is exactly the no-split case, so the router routes it as one command.
        Assert.False(CompoundPolicy.TrySplitSpecialCompound(units[0].Text, out var nested));
        Assert.Empty(nested);
    }

    /// <summary>The existing contract: two scoped specials in separate <c>;</c> blocks are two units.</summary>
    [Fact]
    public void ScopedSpecialsInSeparateBlocks_AreTwoUnits()
    {
        Assert.True(CompoundPolicy.TrySplitSpecialCompound("HO 3G; ACCEPT", out var units));

        Assert.Equal(2, units.Count);
        Assert.Equal("HO 3G", units[0].Text);
        Assert.Equal("ACCEPT", units[1].Text);
    }

    /// <summary>
    /// The word aliases are separators everywhere else in the parser (<c>THEN</c> → <c>;</c>, <c>AND</c> → <c>,</c>),
    /// so the walk has to see them too. Split on the raw text, "HO 3G THEN ACCEPT" walked to one unit equal to the
    /// input and the line fell to the single-command router, which swallowed "3G THEN ACCEPT" as the handoff's TCP.
    /// </summary>
    [Fact]
    public void ThenAliasedScopedSpecials_AreTwoUnits()
    {
        Assert.True(CompoundPolicy.TrySplitSpecialCompound("HO 3G THEN ACCEPT", out var units));

        Assert.Equal(2, units.Count);
        Assert.Equal("HO 3G", units[0].Text);
        Assert.Equal(0, units[0].BlockIndex);
        Assert.Equal("ACCEPT", units[1].Text);
        Assert.Equal(1, units[1].BlockIndex);
    }

    /// <summary>The <c>AND</c> alias splits a block into one unit per command exactly as a typed <c>,</c> does.</summary>
    [Fact]
    public void AndAliasedScopedSpecials_SplitIntoOneUnitEach()
    {
        Assert.True(CompoundPolicy.TrySplitSpecialCompound("SP1 ABC AND HO 3G", out var units));

        Assert.Equal(2, units.Count);
        Assert.Equal("SP1 ABC", units[0].Text);
        Assert.Equal("HO 3G", units[1].Text);
        Assert.Equal(0, units[0].BlockIndex);
        Assert.Equal(0, units[1].BlockIndex);

        Assert.True(CompoundPolicy.TrySplitSpecialCompound("AN 1 X AND AN 2 Y", out var annotateUnits));

        Assert.Equal(2, annotateUnits.Count);
        Assert.Equal("AN 1 X", annotateUnits[0].Text);
        Assert.Equal("AN 2 Y", annotateUnits[1].Text);
    }

    /// <summary>
    /// A coordination message is free text to the end of the line: a <c>,</c> or <c>;</c> the instructor typed inside
    /// the message is message content, not a chain separator. Splitting one truncates the message and dispatches its
    /// tail as a command ("RDTXT /1 HOLD, GO" sent HOLD and then flew GO), so the splitter must decline outright.
    /// </summary>
    [Theory]
    [InlineData("RDTXT /1 HOLD, GO")]
    [InlineData("RDTXT /1 A; GO")]
    [InlineData("RDTXT EXPECT DELAY, HDG 280")]
    [InlineData("RDH 1 HOLD, GO")]
    public void FreeTextCoordinationMessage_WithSeparatorInText_IsNotASplit(string line)
    {
        Assert.False(CompoundPolicy.TrySplitSpecialCompound(line, out var units), $"split into: {string.Join(" | ", units.Select(u => u.Text))}");
        Assert.Empty(units);
    }

    /// <summary>
    /// The contract the no-split verdict relies on: the single-command parser keeps the whole message, separators and
    /// all, so the line the splitter declines still reaches the coordination list intact.
    /// </summary>
    [Fact]
    public void FreeTextCoordinationMessage_KeepsWholeTextOnSingleParse()
    {
        var parsed = CommandParser.Parse("RDTXT /1 HOLD, GO");

        Assert.True(parsed.IsSuccess, parsed.Reason);
        var modify = Assert.IsType<CoordinationModifyCommand>(parsed.Value);
        Assert.Equal("1", modify.ListId);
        Assert.Equal("HOLD, GO", modify.Text);
    }

    /// <summary>
    /// A free-text message in a later <c>;</c> block still ends the chain: the blocks before it split normally, and the
    /// message keeps everything from its own start to the end of the line — including further separators.
    /// </summary>
    [Fact]
    public void FreeTextCoordinationMessage_InALaterBlock_SwallowsTheRestOfTheLine()
    {
        Assert.True(CompoundPolicy.TrySplitSpecialCompound("HO 3G; RDTXT /1 HOLD, GO", out var units));

        Assert.Equal(2, units.Count);
        Assert.Equal("HO 3G", units[0].Text);
        Assert.Equal(0, units[0].BlockIndex);
        Assert.Equal("RDTXT /1 HOLD, GO", units[1].Text);
        Assert.Equal(1, units[1].BlockIndex);

        Assert.True(CompoundPolicy.TrySplitSpecialCompound("HO 3G; RDTXT /1 A; GO", out var semicolonUnits));

        Assert.Equal(2, semicolonUnits.Count);
        Assert.Equal("HO 3G", semicolonUnits[0].Text);
        Assert.Equal("RDTXT /1 A; GO", semicolonUnits[1].Text);

        // The router re-routes each unit, so the message unit has to land on the single-command path on re-entry.
        Assert.False(CompoundPolicy.TrySplitSpecialCompound(units[1].Text, out var nested));
        Assert.Empty(nested);
        Assert.False(CompoundPolicy.TrySplitSpecialCompound(semicolonUnits[1].Text, out var nestedSemicolon));
        Assert.Empty(nestedSemicolon);

        // The alias form has to slice the message out of the line the instructor typed, not out of the normalized one:
        // "THEN" is four characters and ";" is one, so a walk over normalized text with raw offsets shifts the tail.
        Assert.True(CompoundPolicy.TrySplitSpecialCompound("HO 3G THEN RDTXT /1 HOLD, GO", out var aliasUnits));

        Assert.Equal(2, aliasUnits.Count);
        Assert.Equal("HO 3G", aliasUnits[0].Text);
        Assert.Equal(0, aliasUnits[0].BlockIndex);
        Assert.Equal("RDTXT /1 HOLD, GO", aliasUnits[1].Text);
        Assert.Equal(1, aliasUnits[1].BlockIndex);
    }

    /// <summary>
    /// A note's text is free to the end of the line, aliases included: the words after <c>NOTE</c> are content, so the
    /// line is one command and the single-command parser keeps the text exactly as it was typed.
    /// </summary>
    [Fact]
    public void NoteWithAnAliasInItsText_IsNotASplit()
    {
        Assert.False(CompoundPolicy.TrySplitSpecialCompound("NOTE fly direct THEN descend; SP1 ABC", out var units));
        Assert.Empty(units);

        var parsed = CommandParser.Parse("NOTE fly direct THEN descend; SP1 ABC");

        Assert.True(parsed.IsSuccess, parsed.Reason);
        var note = Assert.IsType<NoteCommand>(parsed.Value);
        Assert.Equal("fly direct THEN descend; SP1 ABC", note.Text);
    }

    /// <summary>
    /// A scratchpad argument is not free text: a STARS scratchpad is 3-4 alphanumerics, so a separator after one is
    /// always a chain separator and the line must keep splitting.
    /// </summary>
    [Fact]
    public void ScratchpadWithSeparator_StaysAChain()
    {
        Assert.True(CompoundPolicy.TrySplitSpecialCompound("SP1 ABC, HO 3G", out var units));

        Assert.Equal(2, units.Count);
        Assert.Equal("SP1 ABC", units[0].Text);
        Assert.Equal("HO 3G", units[1].Text);
    }

    /// <summary>
    /// A hold-release with no message carries no free text, so an aviation tail chained onto it still splits — the
    /// free-text rule keys on the parsed message, not on the verb. <c>RDH 1</c> is the discriminating form: the piece
    /// parses on its own (as a list-scoped hold with no text), so the walk has to reject it as free text on the parsed
    /// command rather than on a failed parse.
    /// </summary>
    [Fact]
    public void BareCoordinationHold_ChainedWithAviationTail_StillSplits()
    {
        Assert.True(CompoundPolicy.TrySplitSpecialCompound("RDH 1; SQVFR", out var units));

        Assert.Equal(2, units.Count);
        Assert.Equal("RDH 1", units[0].Text);
        Assert.Equal("SQVFR", units[1].Text);

        Assert.True(CompoundPolicy.TrySplitSpecialCompound("RDH; SQVFR", out var bareUnits));

        Assert.Equal(2, bareUnits.Count);
        Assert.Equal("RDH", bareUnits[0].Text);
        Assert.Equal("SQVFR", bareUnits[1].Text);
    }

    /// <summary>
    /// The alias form of that same chain splits identically. <c>RDH</c> is deliberately not one of the normalizer's
    /// free-text verbs — its text is optional, so treating it as literal would make <c>RDH 1 THEN SQVFR</c> a hold
    /// whose message is "THEN SQVFR" while <c>RDH 1; SQVFR</c> chains; an alias word typed inside an RDH message is a
    /// separator instead, and the line fails on the unknown tail rather than minting the wrong hold silently.
    /// </summary>
    [Fact]
    public void BareCoordinationHold_ChainedWithAnAliasSeparator_SplitsTheSameWay()
    {
        Assert.True(CompoundPolicy.TrySplitSpecialCompound("RDH 1 THEN SQVFR", out var units));

        Assert.Equal(2, units.Count);
        Assert.Equal("RDH 1", units[0].Text);
        Assert.Equal(0, units[0].BlockIndex);
        Assert.Equal("SQVFR", units[1].Text);
        Assert.Equal(1, units[1].BlockIndex);
    }

    /// <summary>
    /// The free-text rule is per piece, not per <c>;</c> block: a message that starts after a <c>,</c> swallows the rest
    /// of the line exactly the same way. Split per block only, "SP1 ABC, RDTXT /1 HOLD, GO" shredded into three units
    /// and flew the GO.
    /// </summary>
    [Fact]
    public void FreeTextCoordinationMessage_AfterACommaPiece_SwallowsTheRestOfTheLine()
    {
        Assert.True(CompoundPolicy.TrySplitSpecialCompound("SP1 ABC, RDTXT /1 HOLD, GO", out var units));

        Assert.Equal(2, units.Count);
        Assert.Equal("SP1 ABC", units[0].Text);
        Assert.Equal("RDTXT /1 HOLD, GO", units[1].Text);

        Assert.True(CompoundPolicy.TrySplitSpecialCompound("HO 3G, RDTXT /1 HOLD, GO", out var handoffUnits));

        Assert.Equal(2, handoffUnits.Count);
        Assert.Equal("HO 3G", handoffUnits[0].Text);
        Assert.Equal("RDTXT /1 HOLD, GO", handoffUnits[1].Text);
    }

    /// <summary>
    /// A word inside a coordination message is message text, never a command: the splitter's bail scan (DEL, PAUSE, …)
    /// has to run over the units the walk produced, not over a parse of the whole line, or a message ending in "DEL"
    /// makes the splitter decline and the aviation path shreds the message instead.
    /// </summary>
    [Fact]
    public void FreeTextCoordinationMessage_MessageWordsAreNotBailCommands()
    {
        Assert.True(CompoundPolicy.TrySplitSpecialCompound("HO 3G; RDTXT /1 HOLD, DEL", out var units));

        Assert.Equal(2, units.Count);
        Assert.Equal("HO 3G", units[0].Text);
        Assert.Equal("RDTXT /1 HOLD, DEL", units[1].Text);
    }
}
