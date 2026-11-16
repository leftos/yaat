using Xunit;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// How the Scope reads a typed entry for its kind label: a leading dot is a dot command, a first word that is a dialect verb
/// of the profile's kind is a STARS or ERAM entry (Mixed reads as ERAM, or as STARS while a STARS-owned track is selected), a
/// leading <c>PILOT</c> word forces a pilot read, anything that parses as a pilot command is one, and the rest reads as nothing.
/// </summary>
public class ScopeEntryClassifierTests
{
    private static ScopeEntryReading Read(string text, ScopeProfileKind profile) => ScopeEntryClassifier.Classify(text, profile, null);

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
    public void AFlidThenAPilotVerb_OnEram_StaysAPilotCommand() =>
        AssertReads(Read("UAL238 FH 270", ScopeProfileKind.Eram), ScopeEntryKind.Pilot, "PILOT FH");

    private static void AssertReads(ScopeEntryReading reading, ScopeEntryKind kind, string labelStart)
    {
        Assert.Equal(kind, reading.Kind);
        Assert.StartsWith(labelStart, reading.Label, StringComparison.Ordinal);
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

    [Fact]
    public void CallsignThenPilotVerb_OnStars_ReadsAsPilot() =>
        AssertReads(Read("UAL238 FH 270", ScopeProfileKind.Stars), ScopeEntryKind.Pilot, "PILOT FH");

    [Fact]
    public void CallsignThenPilotVerb_OnEram_ReadsAsPilot() =>
        AssertReads(Read("UAL238 FH 270", ScopeProfileKind.Eram), ScopeEntryKind.Pilot, "PILOT FH");

    [Fact]
    public void EramVerb_OnEram_ReadsAsEram() => AssertReads(Read("QQ 250 UAL238", ScopeProfileKind.Eram), ScopeEntryKind.Eram, "ERAM QQ");

    [Fact]
    public void EramVerb_OnMixed_ReadsAsEram() => AssertReads(Read("QQ 250 UAL238", ScopeProfileKind.Mixed), ScopeEntryKind.Eram, "ERAM QQ");

    [Fact]
    public void EramVerb_OnStars_ReadsAsThePilotAlias() => AssertReads(Read("QQ 250", ScopeProfileKind.Stars), ScopeEntryKind.Pilot, "PILOT QQ");

    [Fact]
    public void PilotPrefix_ForcesAPilotRead_OnEram() => AssertReads(Read("PILOT QQ 250", ScopeProfileKind.Eram), ScopeEntryKind.Pilot, "PILOT QQ");

    [Fact]
    public void LowercaseEramVerb_ReadsAsEram() => AssertReads(Read("qq 250 ual238", ScopeProfileKind.Eram), ScopeEntryKind.Eram, "ERAM QQ");

    [Fact]
    public void UnknownWord_ReadsAsNothing()
    {
        ScopeEntryReading reading = Read("xyzzy", ScopeProfileKind.Stars);
        Assert.Equal(ScopeEntryKind.None, reading.Kind);
        Assert.Equal("", reading.Label);
    }

    [Fact]
    public void BarePilotWord_ReadsAsNothing_AndSendsNothing()
    {
        ScopeEntryReading reading = Read("PILOT", ScopeProfileKind.Eram);
        Assert.Equal(ScopeEntryKind.None, reading.Kind);
        Assert.Equal("", reading.Label);
        Assert.Equal("", reading.SendText);
    }

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
        Assert.Equal(
            new ScopeEntryReading(ScopeEntryKind.Stars, "STARS MF scope ptl", ".scope ptl UAL238", true),
            Read("MF R UAL238", ScopeProfileKind.Stars)
        );

    [Theory]
    [InlineData(ScopeProfileKind.Eram)]
    [InlineData(ScopeProfileKind.Mixed)]
    public void MfR_OnEramOrMixed_IsNoStarsEntry(ScopeProfileKind profile) =>
        Assert.NotEqual(ScopeEntryKind.Stars, Read("MF R UAL238", profile).Kind);

    [Fact]
    public void PilotUn_ReadsAsUnpause_AndUnOnStars_ReadsAsStars()
    {
        AssertReads(Read("PILOT UN", ScopeProfileKind.Stars), ScopeEntryKind.Pilot, "PILOT UN");
        AssertReads(Read("UN", ScopeProfileKind.Stars), ScopeEntryKind.Stars, "STARS UN");
    }

    [Fact]
    public void ADotCommand_OnStars_StaysADotCommand() =>
        Assert.Equal(
            new ScopeEntryReading(ScopeEntryKind.Dot, "DOT .scope", ".scope range 40", true),
            Read(".scope range 40", ScopeProfileKind.Stars)
        );

    // The selected track's owner decides the dialect on Mixed (a STARS-owned track reads STARS and pilot entries).

    private static ScopeEntryReading ReadWithSelected(string text, ScopeProfileKind profile, string ownerTemplateId) =>
        ScopeEntryClassifier.Classify(text, profile, ownerTemplateId);

    [Fact]
    public void Qq_OnMixed_WithAStarsOwnedTrackSelected_ReadsAsThePilotTempAltAlias() =>
        AssertReads(ReadWithSelected("QQ 250", ScopeProfileKind.Mixed, ScopeTemplates.StarsId), ScopeEntryKind.Pilot, "PILOT QQ");

    [Fact]
    public void Qq_OnMixed_WithAnEramOwnedTrackSelected_ReadsAsEram() =>
        AssertReads(ReadWithSelected("QQ 250", ScopeProfileKind.Mixed, ScopeTemplates.EramId), ScopeEntryKind.Eram, "ERAM QQ");

    [Fact]
    public void Qq_OnEram_WithAStarsOwnedTrackSelected_StillReadsAsEram() =>
        AssertReads(ReadWithSelected("QQ 250", ScopeProfileKind.Eram, ScopeTemplates.StarsId), ScopeEntryKind.Eram, "ERAM QQ");

    // The shared ERAM verbs win over the YAAT pilot aliases on ERAM and Mixed; PILOT or a callsign first reaches the pilot command.

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
    [InlineData("PILOT CA", "PILOT CA")]
    [InlineData("UAL238 CA", "PILOT CA")]
    [InlineData("PILOT SP ABC", "PILOT SP")]
    [InlineData("UAL238 SP ABC", "PILOT SP")]
    [InlineData("PILOT RD", "PILOT RD")]
    [InlineData("UAL238 RD", "PILOT RD")]
    public void ASharedVerb_AfterPilotOrACallsign_ReadsAsThePilotCommand(string entry, string label)
    {
        AssertReads(Read(entry, ScopeProfileKind.Eram), ScopeEntryKind.Pilot, label);
        AssertReads(Read(entry, ScopeProfileKind.Mixed), ScopeEntryKind.Pilot, label);
    }

    [Theory]
    [InlineData("AR")]
    [InlineData("MR")]
    [InlineData("QD")]
    [InlineData("WR")]
    public void AVerbTheServerDoesNotDispatch_IsNoEramVerb(string verb) =>
        Assert.DoesNotContain(verb, (IReadOnlySet<string>)Yaat.Sim.Commands.EramEntryVerbs.All);

    // The bare-FLID fallback: a lone FLID, or a sector then a FLID (an implied handoff), reads as ERAM; a mistyped pilot entry does not.

    [Fact]
    public void ALoneFlid_OnEram_ReadsAsEram() => AssertReads(Read("UAL238", ScopeProfileKind.Eram), ScopeEntryKind.Eram, "ERAM UAL238");

    [Fact]
    public void ASectorThenAFlid_OnEram_ReadsAsAnImpliedHandoff() =>
        AssertReads(Read("44 UAL238", ScopeProfileKind.Eram), ScopeEntryKind.Eram, "ERAM 44");

    [Fact]
    public void AFlidThenAMistypedPilotVerb_OnEram_IsNoEramEntry() =>
        Assert.NotEqual(ScopeEntryKind.Eram, Read("UAL238 FHH 270", ScopeProfileKind.Eram).Kind);

    [Fact]
    public void AFlidThenAnUnknownWord_OnEram_IsNoEramEntry() => Assert.NotEqual(ScopeEntryKind.Eram, Read("UAL238 FHH", ScopeProfileKind.Eram).Kind);

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
    public void PilotPrefix_ForcesAPilotRead_OnMixed() => AssertReads(Read("PILOT QQ 250", ScopeProfileKind.Mixed), ScopeEntryKind.Pilot, "PILOT QQ");

    [Fact]
    public void ADotCommand_OnEram_StaysADotCommand() =>
        AssertReads(Read(".scope range 40", ScopeProfileKind.Eram), ScopeEntryKind.Dot, "DOT .scope");

    [Fact]
    public void AnOverrideEntry_OnEram_ReadsAsItsPilotCommand()
    {
        ScopeEntryReading reading = Read("** QQ 250", ScopeProfileKind.Eram);

        AssertReads(reading, ScopeEntryKind.Pilot, "PILOT QQ");
        Assert.Equal("** QQ 250", reading.SendText);
    }

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
    public void AnOverrideEntry_ReadsAsItsPilotCommand_AndSendsTheOverride()
    {
        ScopeEntryReading reading = Read("** FH 270", ScopeProfileKind.Stars);

        AssertReads(reading, ScopeEntryKind.Pilot, "PILOT FH");
        Assert.Equal("** FH 270", reading.SendText);
    }
}
