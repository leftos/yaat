using Xunit;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// How the Scope reads a typed entry for its kind label: a leading dot is a dot command, a first word that is a dialect verb
/// of the profile's kind is a STARS or ERAM entry (Mixed reads as ERAM, or as STARS while a STARS-owned track is selected), a
/// leading <c>$</c> makes the rest a YAAT command (sent without the <c>$</c>), and the rest reads as nothing, with nothing to send.
/// </summary>
public class ScopeEntryClassifierTests
{
    private static ScopeEntryReading Read(string text, ScopeProfileKind profile) =>
        ScopeEntryClassifier.Classify(text, profile, null, CommandScheme.Default());

    // A user scheme with TRACK renamed to TRK.
    private static CommandScheme TrkScheme()
    {
        var scheme = CommandScheme.Default();
        scheme.Patterns[CanonicalCommandType.TrackAircraft].Aliases = ["TRK"];
        return scheme;
    }

    private static string TrackGloss => CommandRegistry.Get(CanonicalCommandType.TrackAircraft)!.Label.ToLowerInvariant();

    [Fact]
    public void ADollarEntry_InTheUsersOwnVerb_ReadsAsThatCommand()
    {
        ScopeEntryReading reading = ScopeEntryClassifier.Classify("$ TRK UAL238", ScopeProfileKind.Stars, null, TrkScheme());

        Assert.Equal(new ScopeEntryReading(ScopeEntryKind.Pilot, $"$ TRK {TrackGloss}", "TRK UAL238", true), reading);
    }

    [Fact]
    public void ADollarEntry_InAVerbTheUserRenamed_ReadsWithNoCommandGloss()
    {
        ScopeEntryReading reading = ScopeEntryClassifier.Classify("$ TRACK UAL238", ScopeProfileKind.Stars, null, TrkScheme());

        Assert.Equal(new ScopeEntryReading(ScopeEntryKind.Pilot, "$ TRACK", "TRACK UAL238", true), reading);
    }

    // ERAM Table 16's implied forms, with the handoff sector shapes the server's ToHandoffCode takes.
    [Theory]
    [InlineData("3/2 UAL238", "ERAM 3/2")]
    [InlineData("UAL238/OK", "ERAM UAL238/OK")]
    [InlineData("26 UAL238/OK", "ERAM 26")]
    [InlineData("26 UAL238 /OK", "ERAM 26")]
    [InlineData("C44 UAL238", "ERAM C44")]
    [InlineData("L25 UAL238", "ERAM L25")]
    [InlineData("Q2B UAL238", "ERAM Q2B")]
    public void ATable16ImpliedForm_OnEram_ReadsAsEram(string entry, string label) =>
        AssertReads(Read(entry, ScopeProfileKind.Eram), ScopeEntryKind.Eram, label);

    [Fact]
    public void AFlidThenAPilotVerb_OnEram_ReadsAsNothing_AndSendsNothing() => AssertNothing(Read("UAL238 FH 270", ScopeProfileKind.Eram));

    private static void AssertReads(ScopeEntryReading reading, ScopeEntryKind kind, string labelStart)
    {
        Assert.Equal(kind, reading.Kind);
        Assert.StartsWith(labelStart, reading.Label, StringComparison.Ordinal);
    }

    private static void AssertNothing(ScopeEntryReading reading)
    {
        Assert.Equal(ScopeEntryKind.None, reading.Kind);
        Assert.Equal("", reading.Label);
        Assert.Equal("", reading.SendText);
    }

    [Fact]
    public void EmptyText_ReadsAsNothing_OnEveryProfile()
    {
        foreach (ScopeProfileKind profile in Enum.GetValues<ScopeProfileKind>())
        {
            ScopeEntryReading reading = Read("", profile);
            Assert.Equal(ScopeEntryKind.None, reading.Kind);
            Assert.Equal("", reading.Label);
        }
    }

    [Fact]
    public void LeadingDot_ReadsAsADotCommand() => AssertReads(Read(".scope range 40", ScopeProfileKind.Stars), ScopeEntryKind.Dot, "DOT .scope");

    [Theory]
    [InlineData(ScopeProfileKind.Stars)]
    [InlineData(ScopeProfileKind.Eram)]
    [InlineData(ScopeProfileKind.Mixed)]
    public void CallsignThenPilotVerb_WithoutDollar_ReadsAsNothing(ScopeProfileKind profile)
    {
        AssertNothing(Read("UAL238 FH 270", profile));
        AssertNothing(Read("FH 270", profile));
    }

    [Theory]
    [InlineData(ScopeProfileKind.Stars)]
    [InlineData(ScopeProfileKind.Eram)]
    [InlineData(ScopeProfileKind.Mixed)]
    public void Dollar_ThenACallsignAndAPilotVerb_ReadsAsPilot_SentWithoutTheDollar(ScopeProfileKind profile)
    {
        foreach (string entry in new[] { "$UAL238 FH 270", "$ UAL238 FH 270" })
        {
            ScopeEntryReading reading = Read(entry, profile);
            AssertReads(reading, ScopeEntryKind.Pilot, "$ FH");
            Assert.Equal("UAL238 FH 270", reading.SendText);
        }
    }

    [Theory]
    [InlineData(ScopeProfileKind.Stars)]
    [InlineData(ScopeProfileKind.Eram)]
    public void ThePilotWord_IsNoPrefix(ScopeProfileKind profile) => Assert.NotEqual(ScopeEntryKind.Pilot, Read("PILOT FH 270", profile).Kind);

    [Fact]
    public void EramVerb_OnEram_ReadsAsEram() => AssertReads(Read("QQ 250 UAL238", ScopeProfileKind.Eram), ScopeEntryKind.Eram, "ERAM QQ");

    [Fact]
    public void EramVerb_OnMixed_ReadsAsEram() => AssertReads(Read("QQ 250 UAL238", ScopeProfileKind.Mixed), ScopeEntryKind.Eram, "ERAM QQ");

    [Fact]
    public void EramVerb_OnStars_ReadsAsNothing() => AssertNothing(Read("QQ 250", ScopeProfileKind.Stars));

    [Fact]
    public void DollarPrefix_ForcesAPilotRead_OnEram() => AssertReads(Read("$QQ 250", ScopeProfileKind.Eram), ScopeEntryKind.Pilot, "$ QQ");

    [Fact]
    public void LowercaseEramVerb_ReadsAsEram() => AssertReads(Read("qq 250 ual238", ScopeProfileKind.Eram), ScopeEntryKind.Eram, "ERAM QQ");

    [Fact]
    public void UnknownWord_ReadsAsNothing() => AssertNothing(Read("xyzzy", ScopeProfileKind.Stars));

    [Theory]
    [InlineData("$", ScopeProfileKind.Eram)]
    [InlineData("$ ", ScopeProfileKind.Eram)]
    [InlineData("$", ScopeProfileKind.Stars)]
    [InlineData("$  ", ScopeProfileKind.Mixed)]
    public void BareDollar_HasNothingToSend(string entry, ScopeProfileKind profile) => Assert.Equal("", Read(entry, profile).SendText);

    [Fact]
    public void AStarsShape_OnStars_ReadsAsStars_WithItsTranslation()
    {
        ScopeEntryReading reading = Read("+100", ScopeProfileKind.Stars);

        Assert.Equal(new ScopeEntryReading(ScopeEntryKind.Stars, "STARS +100 temporary altitude", "TEMPALT 100", true), reading);
    }

    [Fact]
    public void AnUnsupportedStarsEntry_ReadsAsStars_NotSupported()
    {
        ScopeEntryReading reading = Read("MF N", ScopeProfileKind.Stars);

        Assert.Equal(ScopeEntryKind.Stars, reading.Kind);
        Assert.Equal("STARS MF", reading.Label);
        Assert.False(reading.Supported);
    }

    [Theory]
    [InlineData(ScopeProfileKind.Eram)]
    [InlineData(ScopeProfileKind.Mixed)]
    public void AStarsShape_OnEramOrMixed_IsNoStarsEntry(ScopeProfileKind profile) =>
        Assert.NotEqual(ScopeEntryKind.Stars, Read("+100", profile).Kind);

    [Fact]
    public void MfR_OnStars_ReadsAsAStarsEntryThatSends() =>
        Assert.Equal(new ScopeEntryReading(ScopeEntryKind.Stars, "STARS MF scope ptl", ".scope ptl", true), Read("MF R", ScopeProfileKind.Stars));

    [Theory]
    [InlineData(ScopeProfileKind.Eram)]
    [InlineData(ScopeProfileKind.Mixed)]
    public void MfR_OnEramOrMixed_IsNoStarsEntry(ScopeProfileKind profile) =>
        Assert.NotEqual(ScopeEntryKind.Stars, Read("MF R UAL238", profile).Kind);

    [Fact]
    public void DollarUn_ReadsAsUnpause_AndUnOnStars_ReadsAsStars()
    {
        ScopeEntryReading reading = Read("$UN", ScopeProfileKind.Stars);
        AssertReads(reading, ScopeEntryKind.Pilot, "$ UN");
        Assert.Equal("UN", reading.SendText);
        AssertReads(Read("UN", ScopeProfileKind.Stars), ScopeEntryKind.Stars, "STARS UN");
    }

    [Fact]
    public void ADotCommand_OnStars_StaysADotCommand() =>
        Assert.Equal(
            new ScopeEntryReading(ScopeEntryKind.Dot, "DOT .scope", ".scope range 40", true),
            Read(".scope range 40", ScopeProfileKind.Stars)
        );

    // The selected track's owner decides the dialect on Mixed (a STARS-owned track reads STARS entries).

    private static ScopeEntryReading ReadWithSelected(string text, ScopeProfileKind profile, string ownerTemplateId) =>
        ScopeEntryClassifier.Classify(text, profile, ownerTemplateId, CommandScheme.Default());

    [Fact]
    public void Qq_OnMixed_WithAStarsOwnedTrackSelected_ReadsAsNothing() =>
        AssertNothing(ReadWithSelected("QQ 250", ScopeProfileKind.Mixed, ScopeTemplates.StarsId));

    [Fact]
    public void Qq_OnMixed_WithAnEramOwnedTrackSelected_ReadsAsEram() =>
        AssertReads(ReadWithSelected("QQ 250", ScopeProfileKind.Mixed, ScopeTemplates.EramId), ScopeEntryKind.Eram, "ERAM QQ");

    [Fact]
    public void Qq_OnEram_WithAStarsOwnedTrackSelected_StillReadsAsEram() =>
        AssertReads(ReadWithSelected("QQ 250", ScopeProfileKind.Eram, ScopeTemplates.StarsId), ScopeEntryKind.Eram, "ERAM QQ");

    // The shared ERAM verbs are ERAM on ERAM and Mixed; only a leading $ reaches the pilot command.

    [Theory]
    [InlineData("CA", ScopeProfileKind.Eram)]
    [InlineData("SP", ScopeProfileKind.Eram)]
    [InlineData("RD", ScopeProfileKind.Eram)]
    [InlineData("FP", ScopeProfileKind.Eram)]
    [InlineData("CA", ScopeProfileKind.Mixed)]
    [InlineData("SP", ScopeProfileKind.Mixed)]
    [InlineData("RD", ScopeProfileKind.Mixed)]
    [InlineData("FP", ScopeProfileKind.Mixed)]
    public void ASharedVerb_OnEramAndMixed_ReadsAsEram(string verb, ScopeProfileKind profile) =>
        AssertReads(Read($"{verb} UAL238", profile), ScopeEntryKind.Eram, $"ERAM {verb}");

    [Theory]
    [InlineData("$CA", "$ CA")]
    [InlineData("$UAL238 CA", "$ CA")]
    [InlineData("$SP ABC", "$ SP")]
    [InlineData("$ UAL238 SP ABC", "$ SP")]
    [InlineData("$RD", "$ RD")]
    [InlineData("$UAL238 RD", "$ RD")]
    public void ASharedVerb_AfterDollar_ReadsAsThePilotCommand(string entry, string label)
    {
        AssertReads(Read(entry, ScopeProfileKind.Eram), ScopeEntryKind.Pilot, label);
        AssertReads(Read(entry, ScopeProfileKind.Mixed), ScopeEntryKind.Pilot, label);
    }

    [Theory]
    [InlineData("UAL238 CA")]
    [InlineData("UAL238 SP ABC")]
    [InlineData("UAL238 RD")]
    public void ACallsignThenASharedVerb_WithoutDollar_ReadsAsNothing(string entry)
    {
        AssertNothing(Read(entry, ScopeProfileKind.Eram));
        AssertNothing(Read(entry, ScopeProfileKind.Mixed));
    }

    // Each verb ERAM and YAAT share: bare it is the ERAM entry on an ERAM Scope; after $ it is the YAAT command, sent without the $.

    [Theory]
    [InlineData("DM", "DM 110")]
    [InlineData("LA", "LA")]
    [InlineData("CA", "CA")]
    [InlineData("FP", "FP")]
    [InlineData("VP", "VP")]
    [InlineData("RD", "RD")]
    [InlineData("SP", "SP ABC")]
    public void ASharedVerb_ReadsAsEramBare_AndAsPilotAfterDollar(string verb, string command)
    {
        AssertReads(Read($"{verb} UAL238", ScopeProfileKind.Eram), ScopeEntryKind.Eram, $"ERAM {verb}");
        foreach (string entry in new[] { $"${command}", $"$ {command}" })
        {
            ScopeEntryReading reading = Read(entry, ScopeProfileKind.Eram);
            AssertReads(reading, ScopeEntryKind.Pilot, $"$ {verb}");
            Assert.Equal(command, reading.SendText);
        }
    }

    [Theory]
    [InlineData("AR")]
    [InlineData("MR")]
    [InlineData("QD")]
    [InlineData("WR")]
    public void AVerbTheServerDoesNotDispatch_IsNoEramVerb(string verb) =>
        Assert.DoesNotContain(verb, (IReadOnlySet<string>)Yaat.Sim.Commands.EramEntryVerbs.All);

    // The bare-FLID entries: a lone FLID, or a sector then a FLID (an implied handoff), reads as ERAM; a FLID then other words does not.

    [Fact]
    public void ALoneFlid_OnEram_ReadsAsEram() => AssertReads(Read("UAL238", ScopeProfileKind.Eram), ScopeEntryKind.Eram, "ERAM UAL238");

    // A lone FLID-shaped word is tried as a FLID, as CRC's ERAM would, even when it is a YAAT verb.
    [Fact]
    public void ALonePilotVerb_OnEram_ReadsAsAnEramFlidEntry() => AssertReads(Read("FH", ScopeProfileKind.Eram), ScopeEntryKind.Eram, "ERAM FH");

    [Fact]
    public void ASectorThenAFlid_OnEram_ReadsAsAnImpliedHandoff() =>
        AssertReads(Read("44 UAL238", ScopeProfileKind.Eram), ScopeEntryKind.Eram, "ERAM 44");

    [Fact]
    public void AFlidThenAMistypedPilotVerb_OnEram_ReadsAsNothing() => AssertNothing(Read("UAL238 FHH 270", ScopeProfileKind.Eram));

    [Fact]
    public void AFlidThenAnUnknownWord_OnEram_ReadsAsNothing() => AssertNothing(Read("UAL238 FHH", ScopeProfileKind.Eram));

    // The case table for the ERAM read (profile x first word), one test per row.

    [Fact]
    public void AnEramOnlyVerb_OnStars_IsNoEramEntry() => Assert.NotEqual(ScopeEntryKind.Eram, Read("QN UAL238", ScopeProfileKind.Stars).Kind);

    [Fact]
    public void Qz_OnEram_ReadsAsEram() => AssertReads(Read("QZ 350 UAL238", ScopeProfileKind.Eram), ScopeEntryKind.Eram, "ERAM QZ");

    [Fact]
    public void Qz_OnMixed_ReadsAsEram() => AssertReads(Read("QZ 350 UAL238", ScopeProfileKind.Mixed), ScopeEntryKind.Eram, "ERAM QZ");

    [Theory]
    [InlineData("QN UAL238", "ERAM QN")]
    [InlineData("QT UAL238", "ERAM QT")]
    [InlineData("QL", "ERAM QL")]
    [InlineData("FP UAL238 B738 KSFO KSAC", "ERAM FP")]
    [InlineData("SP UAL238", "ERAM SP")]
    [InlineData("CA CA FUNCTION ON", "ERAM CA")]
    public void EveryServerVerb_OnEram_ReadsAsEram(string entry, string label)
    {
        ScopeEntryReading reading = Read(entry, ScopeProfileKind.Eram);

        Assert.Equal(new ScopeEntryReading(ScopeEntryKind.Eram, label, entry, true), reading);
    }

    [Fact]
    public void EveryVerbTheServerDispatches_ReadsAsEram()
    {
        foreach (string verb in Yaat.Sim.Commands.EramEntryVerbs.All)
        {
            AssertReads(Read($"{verb} UAL238", ScopeProfileKind.Eram), ScopeEntryKind.Eram, $"ERAM {verb}");
        }
    }

    [Fact]
    public void DollarPrefix_ForcesAPilotRead_OnMixed() => AssertReads(Read("$QQ 250", ScopeProfileKind.Mixed), ScopeEntryKind.Pilot, "$ QQ");

    [Fact]
    public void ADotCommand_OnEram_StaysADotCommand() =>
        AssertReads(Read(".scope range 40", ScopeProfileKind.Eram), ScopeEntryKind.Dot, "DOT .scope");

    [Theory]
    [InlineData("$** QQ 250")]
    [InlineData("$ ** QQ 250")]
    public void AnOverrideEntry_AfterDollar_OnEram_ReadsAsItsPilotCommand(string entry)
    {
        ScopeEntryReading reading = Read(entry, ScopeProfileKind.Eram);

        AssertReads(reading, ScopeEntryKind.Pilot, "$ QQ");
        Assert.Equal("** QQ 250", reading.SendText);
    }

    [Fact]
    public void AnOverrideEntry_AfterDollar_WithACallsign_SendsTheOverrideAndTheCallsign()
    {
        ScopeEntryReading reading = Read("$ ** UAL1 FH 270", ScopeProfileKind.Eram);

        AssertReads(reading, ScopeEntryKind.Pilot, "$ FH");
        Assert.Equal("** UAL1 FH 270", reading.SendText);
    }

    [Theory]
    [InlineData(ScopeProfileKind.Stars)]
    [InlineData(ScopeProfileKind.Eram)]
    public void AnOverrideEntry_WithoutDollar_ReadsAsNothing(ScopeProfileKind profile) => AssertNothing(Read("** FH 270", profile));

    [Fact]
    public void AVciToggle_OnEram_ReadsAsAnImpliedEramEntry() =>
        Assert.Equal(new ScopeEntryReading(ScopeEntryKind.Eram, "ERAM //UAL238", "//UAL238", true), Read("//UAL238", ScopeProfileKind.Eram));

    [Theory]
    [InlineData("1 UAL238", "ERAM 1")]
    [InlineData("/2 UAL238", "ERAM /2")]
    public void ALeaderOrOffsetEntry_OnEram_ReadsAsAnImpliedEramEntry(string entry, string label) =>
        Assert.Equal(new ScopeEntryReading(ScopeEntryKind.Eram, label, entry, true), Read(entry, ScopeProfileKind.Eram));

    [Fact]
    public void ABareFlid_OnEram_ReadsAsAnImpliedEramEntry() =>
        Assert.Equal(new ScopeEntryReading(ScopeEntryKind.Eram, "ERAM UAL238", "UAL238", true), Read("UAL238", ScopeProfileKind.Eram));

    [Fact]
    public void ABareFlid_OnStars_IsNoEramEntry() => Assert.NotEqual(ScopeEntryKind.Eram, Read("UAL238", ScopeProfileKind.Stars).Kind);

    [Fact]
    public void ImpliedForms_OnStars_AreNoEramEntry()
    {
        Assert.NotEqual(ScopeEntryKind.Eram, Read("//UAL238", ScopeProfileKind.Stars).Kind);
        Assert.NotEqual(ScopeEntryKind.Eram, Read("1 UAL238", ScopeProfileKind.Stars).Kind);
    }

    [Fact]
    public void AnOverrideEntry_AfterDollar_ReadsAsItsPilotCommand_AndSendsTheOverride()
    {
        ScopeEntryReading reading = Read("$** FH 270", ScopeProfileKind.Stars);

        AssertReads(reading, ScopeEntryKind.Pilot, "$ FH");
        Assert.Equal("** FH 270", reading.SendText);
    }

    [Theory]
    [InlineData(ScopeProfileKind.Stars)]
    [InlineData(ScopeProfileKind.Mixed)]
    public void Min_OnStarsOrMixed_ReadsAsTheStarsMinSepEntry(ScopeProfileKind profile) =>
        Assert.Equal(
            new ScopeEntryReading(ScopeEntryKind.Stars, "STARS MIN minimum separation", "MIN UAL1 DAL2", true),
            Read("MIN UAL1 DAL2", profile)
        );

    [Fact]
    public void BareMin_OnMixed_ReadsAsStars() => Assert.Equal(ScopeEntryKind.Stars, Read("MIN", ScopeProfileKind.Mixed).Kind);

    [Fact]
    public void Min_OnMixed_ForAStarsOwnedTrack_ReadsAsStars() =>
        Assert.Equal(
            ScopeEntryKind.Stars,
            ScopeEntryClassifier.Classify("MIN UAL1", ScopeProfileKind.Mixed, ScopeTemplates.StarsId, CommandScheme.Default()).Kind
        );

    [Fact]
    public void Min_OnMixed_ForAnEramOwnedTrack_IsNoStarsEntry() =>
        Assert.NotEqual(
            ScopeEntryKind.Stars,
            ScopeEntryClassifier.Classify("MIN UAL1", ScopeProfileKind.Mixed, ScopeTemplates.EramId, CommandScheme.Default()).Kind
        );

    [Fact]
    public void Min_OnEram_IsNoStarsEntry() => Assert.NotEqual(ScopeEntryKind.Stars, Read("MIN UAL1 DAL2", ScopeProfileKind.Eram).Kind);
}
