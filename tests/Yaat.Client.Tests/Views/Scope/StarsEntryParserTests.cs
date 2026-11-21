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
    [InlineData("**2B  3C", "FQL 2B 3C", "**2B", "force quicklook")]
    [InlineData("+ABCD", "SP2 ABCD", "+ABCD", "scratchpad 2")]
    [InlineData("MF L 7", "LDR 7", "MF", "leader direction")]
    [InlineData("MF L 7 UAL238", "LDR 7", "MF", "leader direction")]
    [InlineData("MF L7 UAL238", "LDR 7", "MF", "leader direction")]
    [InlineData("CA K", "CAINH", "CA", "inhibit conflict alert")]
    [InlineData("CA K UAL238", "UAL238 CAINH", "CA", "inhibit conflict alert")]
    [InlineData("RANGE 40", ".scope range 40", "RANGE", "scope range")]
    [InlineData("init ual238", "UAL238 TRACK", "INIT", "track")]
    [InlineData("RANGE 12.5", ".scope range 12.5", "RANGE", "scope range")]
    [InlineData("RANGE 0.125", ".scope range 0.125", "RANGE", "scope range")]
    [InlineData("RANGE 256", ".scope range 256", "RANGE", "scope range")]
    [InlineData("MF L3", "LDR 3", "MF", "leader direction")]
    [InlineData("MF L 3 UAL238", "LDR 3", "MF", "leader direction")]
    [InlineData("MF YAB1", "SP1 AB1", "MF", "scratchpad 1")]
    [InlineData("MF YUAL238 100", "UAL238 PRA 100", "MF", "pilot reported altitude")]
    [InlineData("MF R", ".scope ptl", "MF", "scope ptl")]
    [InlineData("*J1.5", ".scope rings jring 1.5", "*J1.5", "j-ring")]
    [InlineData("*J30", ".scope rings jring 30", "*J30", "j-ring")]
    [InlineData("*P29", ".scope rings cone 29", "*P29", "cone")]
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
    public void BareHnd_AcceptsAHandoff_NeverCancelsOne(string entry, string translation, string? callsign)
    {
        StarsEntryRead read = Assert.IsType<StarsEntryRead>(StarsEntryParser.TryRead(entry));

        Assert.Equal(translation, read.Translation);
        Assert.Equal(StarsEntryNeed.HandoffAccept, read.Need);
        Assert.Equal(callsign, read.Callsign);
    }

    [Theory]
    [InlineData("*J5", false, ScopeRingTool.JRing)]
    [InlineData("*P2.5", false, ScopeRingTool.Cone)]
    [InlineData("*J", true, ScopeRingTool.JRing)]
    [InlineData("*P", true, ScopeRingTool.Cone)]
    [InlineData("*J30", false, ScopeRingTool.JRing)]
    [InlineData("*P9.9", false, ScopeRingTool.Cone)]
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
    [InlineData("MF E", "MF")]
    [InlineData("MF P", "MF")]
    [InlineData("MF I*", "MF")]
    [InlineData("MF N", "MF")]
    [InlineData("CA X", "CA")]
    [InlineData("/3 UAL238", "/3")]
    [InlineData("/0", "/0")]
    [InlineData("MAPS 12", "MAPS")]
    [InlineData("MAPS", "MAPS")]
    [InlineData("+ABCDE", "+ABCDE")]
    [InlineData("++000", "++000")]
    [InlineData("HND 1ABC", "HND")]
    [InlineData("**1A 1B 1C 1D 1E 1F 1G 1H 1J 1K 1L", "**1A")]
    [InlineData("MF Y AB1 UAL238", "MF")]
    [InlineData("MF Y 1ABCD", "MF")]
    [InlineData("4 UAL238", "4")]
    [InlineData("MF L3*", "MF")]
    [InlineData("MF L2B 3", "MF")]
    [InlineData("MF LB 3", "MF")]
    [InlineData("MF L3U", "MF")]
    [InlineData("MF L33", "MF")]
    [InlineData("MF B", "MF")]
    [InlineData("MF Q1R", "MF")]
    [InlineData("MF TV", "MF")]
    public void AStarsEntryWithNoYaatCommand_IsNotSupported(string entry, string firstWord)
    {
        StarsEntryRead read = Assert.IsType<StarsEntryRead>(StarsEntryParser.TryRead(entry));

        Assert.False(read.Supported);
        Assert.Null(read.Translation);
        Assert.Equal(firstWord, read.FirstWord);
    }

    /// <summary>
    /// CRC's quicklook shape (<c>^(\d)?([A-Z0-9])(\+)?$</c>) with no slew: quicklook has no YAAT command, so the entry is not
    /// supported. A bare digit 1-9 with a slew is the leader direction instead (<see cref="ABareDigitOrMfLDigit_IsTheSlewedTracksLeaderDirection"/>).
    /// </summary>
    [Theory]
    [InlineData("A")]
    [InlineData("0")]
    [InlineData("45")]
    [InlineData("1D")]
    [InlineData("1D+")]
    [InlineData("D+")]
    public void TheQuicklookShape_IsNotSupported(string entry)
    {
        StarsEntryRead read = Assert.IsType<StarsEntryRead>(StarsEntryParser.TryRead(entry));

        Assert.False(read.Supported);
        Assert.Null(read.Error);
        Assert.Null(read.WithoutSlew);
    }

    /// <summary>The entries CRC answers FORMAT whatever is slewed: the answer is the category, and nothing is sent.</summary>
    [Theory]
    [InlineData("RANGE")]
    [InlineData("RANGE X")]
    [InlineData("RANGE 12 5")]
    [InlineData("*J31")]
    [InlineData("*J1.0")]
    [InlineData("*J.5")]
    [InlineData("*J100")]
    [InlineData("*P0")]
    [InlineData("MF")]
    [InlineData("MF RX")]
    [InlineData("MF R UAL238")]
    [InlineData("MF L")]
    [InlineData("MF L33 AAL1")]
    [InlineData("MF L5U")]
    [InlineData("MF L2B 5")]
    [InlineData("MIN 123")]
    [InlineData("MIN UAL1 DAL2 SWA3")]
    public void AnEntryCrcAnswersFormat_IsRefusedWithFormat(string entry)
    {
        StarsEntryRead read = Assert.IsType<StarsEntryRead>(StarsEntryParser.TryRead(entry));

        Assert.False(read.Supported);
        Assert.Equal(StarsErrorText.Format, read.Error);
    }

    /// <summary><c>MF L&lt;d&gt; &lt;id&gt;</c>: the Scope finds the id's track (a callsign, a beacon code), whatever the id's shape.</summary>
    [Theory]
    [InlineData("MF L 4 U", "U")]
    [InlineData("MF L3 1234", "1234")]
    [InlineData("MF L3 ual238", "UAL238")]
    [InlineData("MF L3 12", "12")]
    public void MfLeaderForAnId_LeavesTheTrackToTheScope(string entry, string id)
    {
        StarsEntryRead read = Assert.IsType<StarsEntryRead>(StarsEntryParser.TryRead(entry));

        Assert.Equal(StarsEntryNeed.LeaderForTrack, read.Need);
        Assert.Equal(id, read.TrackId);
        Assert.Null(read.Error);
    }

    /// <summary>RANGE takes a number from 0.125 to 256 NM; a number outside that is RANGE LIMIT.</summary>
    [Theory]
    [InlineData("RANGE 0.1")]
    [InlineData("RANGE 0")]
    [InlineData("RANGE 257")]
    [InlineData("RANGE 256.5")]
    [InlineData("RANGE NaN")]
    [InlineData("RANGE Infinity")]
    public void ARangeOutsideItsLimits_IsRangeLimit(string entry) => Assert.Equal(StarsErrorText.RangeLimit, StarsEntryParser.TryRead(entry)?.Error);

    /// <summary>
    /// A bare digit and <c>MF L&lt;d&gt;</c> set the slewed track's leader direction. With nothing slewed, CRC's meaning (quicklook,
    /// the owned tracks' default direction) has no YAAT command, and <c>MF L5</c> (default) is FORMAT for the owned tracks.
    /// </summary>
    [Theory]
    [InlineData("4", "LDR 4", StarsErrorText.NotSupported)]
    [InlineData("5", "LDR 5", StarsErrorText.NotSupported)]
    [InlineData("MF L3", "LDR 3", StarsErrorText.NotSupported)]
    [InlineData("MF L 3", "LDR 3", StarsErrorText.NotSupported)]
    [InlineData("MF L5", "LDR 5", StarsErrorText.Format)]
    public void ABareDigitOrMfLDigit_IsTheSlewedTracksLeaderDirection(string entry, string translation, string withoutSlew)
    {
        StarsEntryRead read = Assert.IsType<StarsEntryRead>(StarsEntryParser.TryRead(entry));

        Assert.Equal(translation, read.Translation);
        Assert.Equal(withoutSlew, read.WithoutSlew);
        Assert.Equal(StarsEntryNeed.None, read.Need);
    }

    /// <summary>The entries that need a slewed track answer FORMAT with none, as CRC's do.</summary>
    [Theory]
    [InlineData("MF R")]
    [InlineData("*J5")]
    [InlineData("*P2.5")]
    [InlineData("**")]
    [InlineData("MF L33")]
    public void AnEntryNeedingASlew_IsFormatWithoutOne(string entry) =>
        Assert.Equal(StarsErrorText.Format, StarsEntryParser.TryRead(entry)?.WithoutSlew);

    /// <summary>Bare <c>**</c> converts the slewed track's point-out to us, else force-quicklooks it to our own position.</summary>
    [Fact]
    public void BareStarStar_NeedsTheSlewedTracksPointOutState() =>
        Assert.Equal(StarsEntryNeed.ConvertPointoutOrQuicklook, StarsEntryParser.TryRead("**")?.Need);

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

    /// <summary>Every entry led by <c>*T</c> is the Scope's range bearing line entry, run on the Scope by CRC's grammar.</summary>
    [Theory]
    [InlineData("*T", "*T")]
    [InlineData("*T1", "*T1")]
    [InlineData("*t1", "*T1")]
    [InlineData("*T 3", "*T")]
    [InlineData("*TOAK", "*TOAK")]
    [InlineData("*TX/", "*TX/")]
    public void AnyStarTEntry_ReadsAsTheRangeBearingLineEntry(string entry, string firstWord)
    {
        StarsEntryRead read = Assert.IsType<StarsEntryRead>(StarsEntryParser.TryRead(entry));

        Assert.True(read.Supported);
        Assert.Equal(StarsEntryNeed.RangeBearingLine, read.Need);
        Assert.Equal(firstWord, read.FirstWord);
        Assert.Equal("range bearing line", read.Gloss);
    }

    [Fact]
    public void StarTAlone_IsTheBareEntry() => Assert.Equal(new StarsRblEntry(StarsRblAction.Bare, 0, ""), StarsEntryParser.ReadRangeBearing("*T"));

    [Theory]
    [InlineData("*T1", 1)]
    [InlineData("*T15", 15)]
    [InlineData("*T01", 1)]
    [InlineData("*t7", 7)]
    public void StarTGluedLineNumberOneToFifteen_RemovesThatLine(string entry, int slot) =>
        Assert.Equal(new StarsRblEntry(StarsRblAction.Remove, slot, entry[2..]), StarsEntryParser.ReadRangeBearing(entry));

    [Theory]
    [InlineData("*T0")]
    [InlineData("*T00")]
    [InlineData("*T16")]
    [InlineData("*T99")]
    public void StarTGluedLineNumberOutsideOneToFifteen_IsRblId(string entry) =>
        Assert.Equal(StarsRblAction.RblId, StarsEntryParser.ReadRangeBearing(entry)?.Action);

    [Theory]
    [InlineData("*T1234", "1234")]
    [InlineData("*T0777", "0777")]
    public void StarTFourOctalDigits_StartsAtThatBeacon(string entry, string code) =>
        Assert.Equal(new StarsRblEntry(StarsRblAction.Beacon, 0, code), StarsEntryParser.ReadRangeBearing(entry));

    /// <summary>
    /// Two to ten of <c>[A-Z0-9-]</c>, after the line-number and beacon shapes in CRC's order: five digits, a four-digit code
    /// with an 8 or 9 and three digits all read as a fix or ACID.
    /// </summary>
    [Theory]
    [InlineData("*TOAK", "OAK")]
    [InlineData("*TAB", "AB")]
    [InlineData("*T12345", "12345")]
    [InlineData("*T1289", "1289")]
    [InlineData("*T123", "123")]
    [InlineData("*TN1-2", "N1-2")]
    [InlineData("*TABCDEFGHIJ", "ABCDEFGHIJ")]
    [InlineData("*tual1", "UAL1")]
    public void StarTTwoToTenCharacters_StartsAtThatFixOrAcid(string entry, string token) =>
        Assert.Equal(new StarsRblEntry(StarsRblAction.Name, 0, token), StarsEntryParser.ReadRangeBearing(entry));

    [Theory]
    [InlineData("*T 3")]
    [InlineData("*T  3")]
    [InlineData("*T A B")]
    [InlineData("*T OAK MOD")]
    [InlineData("*T OAK")]
    [InlineData("*TA")]
    [InlineData("*TABCDEFGHIJK")]
    [InlineData("*T1/2")]
    public void AnyOtherStarTEntry_IsFormat(string entry) => Assert.Equal(StarsRblAction.Format, StarsEntryParser.ReadRangeBearing(entry)?.Action);

    /// <summary>A line number is ASCII digits only: a Unicode digit is no line number, and the entry is FORMAT.</summary>
    [Fact]
    public void StarTWithAUnicodeDigit_IsFormat() => Assert.Equal(StarsRblAction.Format, StarsEntryParser.ReadRangeBearing("*T١")?.Action);

    [Theory]
    [InlineData("*J5")]
    [InlineData("**")]
    [InlineData("T1")]
    public void TextNotLedByStarT_IsNoRangeBearingEntry(string entry) => Assert.Null(StarsEntryParser.ReadRangeBearing(entry));

    /// <summary>The entry after a pending anchor: a slewed item, else a beacon, a fix or ACID, else FORMAT (CRC's CompletePendingRbl).</summary>
    [Theory]
    [InlineData("1234", StarsRblAction.Beacon, "1234")]
    [InlineData("oak", StarsRblAction.Name, "OAK")]
    [InlineData("AB", StarsRblAction.Name, "AB")]
    [InlineData("12345", StarsRblAction.Name, "12345")]
    [InlineData("A", StarsRblAction.Format, "")]
    [InlineData("*T", StarsRblAction.Format, "")]
    [InlineData("OAK MOD", StarsRblAction.Format, "")]
    [InlineData("$ FH 270", StarsRblAction.Format, "")]
    public void TheEntryCompletingAPendingLine_ReadsABeaconAFixOrAcid_ElseFormat(string entry, StarsRblAction action, string token) =>
        Assert.Equal(new StarsRblEntry(action, 0, token), StarsEntryParser.ReadRangeBearingCompletion(entry));

    [Theory]
    [InlineData("TC")]
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
    [InlineData("4 5")]
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
