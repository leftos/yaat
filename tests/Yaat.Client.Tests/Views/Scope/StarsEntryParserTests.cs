using Xunit;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Entries;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// How the Scope reads a STARS entry: the glued shapes and the key words that map to a YAAT command translate to it, the STARS
/// entries with no YAAT command are recognised as not supported, and everything else is no STARS entry at all.
/// </summary>
public class StarsEntryParserTests
{
    [Theory]
    [InlineData("++100", "CRUISE 100", "++100", "cruise altitude")]
    [InlineData("+100", "TEMPALT 100", "+100", "temporary altitude")]
    [InlineData("+000", "TEMPALT 0", "+000", "temporary altitude")]
    [InlineData("+AB1", "SP2 AB1", "+AB1", "scratchpad 2")]
    [InlineData("+ab1", "SP2 AB1", "+AB1", "scratchpad 2")]
    [InlineData("+", "SP2", "+", "scratchpad 2")]
    [InlineData("+1000", "SP2 1000", "+1000", "scratchpad 2")]
    [InlineData("4", "LDR 4", "4", "leader direction")]
    [InlineData("4 UAL238", "UAL238 LDR 4", "4", "leader direction")]
    [InlineData("100", "PRA 100", "100", "pilot reported altitude")]
    [InlineData("1D*", "PO 1D", "1D*", "point out")]
    [InlineData("D*", "PO D", "D*", "point out")]
    [InlineData("10A*", "PO 10A", "10A*", "point out")]
    [InlineData("UN", "PORJ", "UN", "reject pointout")]
    [InlineData("**", "POHO", "**", "convert pointout to handoff")]
    [InlineData("**2B", "FQL 2B", "**2B", "force quicklook")]
    [InlineData("**2B 3C", "FQL 2B 3C", "**2B", "force quicklook")]
    [InlineData("*J5", ".scope rings jring 5", "*J5", "j-ring")]
    [InlineData("*P2.5", ".scope rings cone 2.5", "*P2.5", "cone")]
    [InlineData("*T", "*T", "*T", "range bearing line")]
    [InlineData("*T 3", "*T 3", "*T", "range bearing line")]
    [InlineData("INIT", "TRACK", "INIT", "track")]
    [InlineData("INIT UAL238", "UAL238 TRACK", "INIT", "track")]
    [InlineData("TERM", "DROP", "TERM", "drop track")]
    [InlineData("TERM UAL238", "UAL238 DROP", "TERM", "drop track")]
    [InlineData("HND 1A", "HO 1A", "HND", "handoff")]
    [InlineData("HND 1A UAL238", "UAL238 HO 1A", "HND", "handoff")]
    [InlineData("MF Y AB1", "SP1 AB1", "MF", "scratchpad 1")]
    [InlineData("MF Y ABCD", "SP1 ABCD", "MF", "scratchpad 1")]
    [InlineData("MF Y UAL238 AB1", "UAL238 SP1 AB1", "MF", "scratchpad 1")]
    [InlineData("MF Y UAL238 100", "UAL238 PRA 100", "MF", "pilot reported altitude")]
    [InlineData("MF Y UAL238", "UAL238 SP1", "MF", "scratchpad 1")]
    [InlineData("MF Y +AB1", "SP2 AB1", "MF", "scratchpad 2")]
    [InlineData("MF Y +", "SP2", "MF", "scratchpad 2")]
    [InlineData("MF Y", "SP1", "MF", "scratchpad 1")]
    [InlineData("MF Y 100", "PRA 100", "MF", "pilot reported altitude")]
    [InlineData("*J100", ".scope rings jring 100", "*J100", "j-ring")]
    [InlineData("*J5.25", ".scope rings jring 5.25", "*J5.25", "j-ring")]
    [InlineData("*J.5", ".scope rings jring .5", "*J.5", "j-ring")]
    [InlineData("**2B  3C", "FQL 2B 3C", "**2B", "force quicklook")]
    [InlineData("+ABCD", "SP2 ABCD", "+ABCD", "scratchpad 2")]
    [InlineData("MF L 7", "LDR 7", "MF", "leader direction")]
    [InlineData("MF L 7 UAL238", "UAL238 LDR 7", "MF", "leader direction")]
    [InlineData("MF L7 UAL238", "UAL238 LDR 7", "MF", "leader direction")]
    [InlineData("MF R UAL238", ".scope ptl UAL238", "MF", "scope ptl")]
    [InlineData("mf r ual238", ".scope ptl UAL238", "MF", "scope ptl")]
    [InlineData("CA K", "CAINH", "CA", "inhibit conflict alert")]
    [InlineData("CA K UAL238", "UAL238 CAINH", "CA", "inhibit conflict alert")]
    [InlineData("RANGE 40", ".scope range 40", "RANGE", "scope range")]
    [InlineData("init ual238", "UAL238 TRACK", "INIT", "track")]
    public void ASupportedEntry_TranslatesToItsYaatCommand(string entry, string translation, string firstWord, string gloss)
    {
        StarsEntryRead read = Assert.IsType<StarsEntryRead>(StarsEntryParser.TryRead(entry));

        Assert.True(read.Supported);
        Assert.Equal(translation, read.Translation);
        Assert.Equal(firstWord, read.FirstWord);
        Assert.Equal(gloss, read.Gloss);
    }

    [Theory]
    [InlineData("HND", "ACCEPT", null)]
    [InlineData("HND UAL238", "UAL238 ACCEPT", "UAL238")]
    public void BareHnd_NeedsTheTracksHandoffState(string entry, string translation, string? callsign)
    {
        StarsEntryRead read = Assert.IsType<StarsEntryRead>(StarsEntryParser.TryRead(entry));

        Assert.Equal(translation, read.Translation);
        Assert.Equal(StarsEntryNeed.HandoffAcceptOrCancel, read.Need);
        Assert.Equal(callsign, read.Callsign);
    }

    [Theory]
    [InlineData("*J5", false, ScopeRingTool.JRing)]
    [InlineData("*P2.5", false, ScopeRingTool.Cone)]
    [InlineData("*J", true, ScopeRingTool.JRing)]
    [InlineData("*P", true, ScopeRingTool.Cone)]
    [InlineData("*J50", false, ScopeRingTool.JRing)]
    [InlineData("*J100", false, ScopeRingTool.JRing)]
    public void TpaEntries_NeedTheSelectedTrack(string entry, bool removal, ScopeRingTool ring)
    {
        StarsEntryRead read = Assert.IsType<StarsEntryRead>(StarsEntryParser.TryRead(entry));

        Assert.True(read.Supported);
        Assert.Equal(removal ? StarsEntryNeed.RingRemoval : StarsEntryNeed.RingPlacement, read.Need);
        Assert.Equal(ring, read.Ring);
    }

    [Fact]
    public void BareMfR_NeedsTheSelectedTrack()
    {
        StarsEntryRead read = Assert.IsType<StarsEntryRead>(StarsEntryParser.TryRead("MF R"));

        Assert.True(read.Supported);
        Assert.Equal(".scope ptl", read.Translation);
        Assert.Equal("scope ptl", read.Gloss);
        Assert.Equal(StarsEntryNeed.SelectedTrack, read.Need);
        Assert.Null(read.Callsign);
    }

    [Theory]
    [InlineData("TERM ALL", "TERM")]
    [InlineData("**ALL", "**ALL")]
    [InlineData("**UAL238 ALL", "**UAL238")]
    [InlineData("**J", "**J")]
    [InlineData("**P", "**P")]
    [InlineData("*D+", "*D+")]
    [InlineData("*AE", "*AE")]
    [InlineData("000", "000")]
    [InlineData("MF Y 000", "MF")]
    [InlineData("MF M 1234", "MF")]
    [InlineData("MF L 4 U", "MF")]
    [InlineData("MF E", "MF")]
    [InlineData("MF P", "MF")]
    [InlineData("MF I*", "MF")]
    [InlineData("MF R UAL238 X", "MF")]
    [InlineData("MF R 123", "MF")]
    [InlineData("MF RX", "MF")]
    [InlineData("MF N", "MF")]
    [InlineData("MF", "MF")]
    [InlineData("CA X", "CA")]
    [InlineData("/3 UAL238", "/3")]
    [InlineData("/0", "/0")]
    [InlineData("MAPS 12", "MAPS")]
    [InlineData("MAPS", "MAPS")]
    [InlineData("MIN 123", "MIN")]
    [InlineData("MIN UAL1 DAL2 SWA3", "MIN")]
    [InlineData("RANGE", "RANGE")]
    [InlineData("+ABCDE", "+ABCDE")]
    [InlineData("++000", "++000")]
    [InlineData("HND 1ABC", "HND")]
    [InlineData("**1A 1B 1C 1D 1E 1F 1G 1H 1J 1K 1L", "**1A")]
    [InlineData("MF Y AB1 UAL238", "MF")]
    [InlineData("MF Y 1ABCD", "MF")]
    public void AStarsEntryWithNoYaatCommand_IsNotSupported(string entry, string firstWord)
    {
        StarsEntryRead read = Assert.IsType<StarsEntryRead>(StarsEntryParser.TryRead(entry));

        Assert.False(read.Supported);
        Assert.Null(read.Translation);
        Assert.Equal(firstWord, read.FirstWord);
    }

    [Theory]
    [InlineData("MIN", null, null)]
    [InlineData("MIN UAL1", "UAL1", null)]
    [InlineData("min ual1 dal2", "UAL1", "DAL2")]
    public void Min_ReadsAsTheMinSepEntry_WithItsTypedTracks(string entry, string? first, string? second)
    {
        StarsEntryRead read = Assert.IsType<StarsEntryRead>(StarsEntryParser.TryRead(entry));

        Assert.True(read.Supported);
        Assert.Equal("MIN", read.FirstWord);
        Assert.Equal("minimum separation", read.Gloss);
        Assert.Equal(StarsEntryNeed.MinimumSeparation, read.Need);
        Assert.Equal(first, read.Callsign);
        Assert.Equal(second, read.SecondCallsign);
    }

    [Theory]
    [InlineData("TC")]
    [InlineData("A")]
    [InlineData("CA")]
    [InlineData("FH 270")]
    [InlineData("UAL238 FH 270")]
    [InlineData("QQ 250")]
    [InlineData("xyzzy")]
    [InlineData("TRACK")]
    [InlineData("PILOT 4")]
    [InlineData(".")]
    [InlineData("** FH 270")]
    [InlineData("** 2B")]
    [InlineData("UNPAUSE")]
    [InlineData("0")]
    [InlineData("45")]
    [InlineData("4 5")]
    [InlineData("1D")]
    [InlineData("1000")]
    [InlineData("ABCD*")]
    [InlineData("")]
    public void TextWithNoStarsShape_IsNoStarsEntry(string entry) => Assert.Null(StarsEntryParser.TryRead(entry));

    /// <summary>The parser's key words, less CA and UN, whose collisions with pilot aliases are pinned on their own.</summary>
    public static TheoryData<string> KeyWordsWithNoPilotAlias() => [.. StarsEntryParser.KeyWords.Where(w => w is not ("CA" or "UN"))];

    [Theory]
    [MemberData(nameof(KeyWordsWithNoPilotAlias))]
    public void AStarsKeyWord_IsNoPilotCommand(string word) => Assert.Null(CommandSchemeParser.Parse(word, CommandScheme.Default()));

    /// <summary><c>UN</c> is the pilot Unpause alias; on a STARS Scope, bare <c>UN</c> reads as reject point-out instead.</summary>
    [Fact]
    public void Un_IsTheUnpauseAlias_AndBareUn_ReadsAsRejectPointOut()
    {
        Assert.Equal(CanonicalCommandType.Unpause, CommandSchemeParser.Parse("UN", CommandScheme.Default())?.Type);
        Assert.Equal("PORJ", StarsEntryParser.TryRead("UN")?.Translation);
    }

    /// <summary>
    /// <c>CA</c> alone is the pilot Circle Airport, so it stays a pilot command; only <c>CA</c> followed by a letter is a STARS
    /// entry, and that is no pilot command.
    /// </summary>
    [Fact]
    public void CaAlone_IsCircleAirport_AndCaK_IsNoPilotCommand()
    {
        Assert.Equal(CanonicalCommandType.CircleAirport, CommandSchemeParser.Parse("CA", CommandScheme.Default())?.Type);
        Assert.Null(CommandSchemeParser.Parse("CA K", CommandScheme.Default()));
    }

    [Theory]
    [InlineData("4")]
    [InlineData("100")]
    [InlineData("+100")]
    [InlineData("++100")]
    [InlineData("1D*")]
    [InlineData("*J5")]
    [InlineData("**2B")]
    public void AGluedShape_IsNoPilotCommand(string entry) => Assert.Null(CommandSchemeParser.Parse(entry, CommandScheme.Default()));
}
