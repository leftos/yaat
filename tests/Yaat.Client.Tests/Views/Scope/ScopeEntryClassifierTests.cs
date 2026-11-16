using Xunit;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// How the Scope reads a typed entry for its kind label: a leading dot is a dot command, a first word that is a dialect verb
/// of the profile's kind is a STARS or ERAM entry (Mixed reads as ERAM), a leading <c>PILOT</c> word forces a pilot read,
/// anything that parses as a pilot command is one, and the rest reads as nothing.
/// </summary>
public class ScopeEntryClassifierTests
{
    private static ScopeEntryReading Read(string text, ScopeProfileKind profile) => ScopeEntryClassifier.Classify(text, profile);

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
}
