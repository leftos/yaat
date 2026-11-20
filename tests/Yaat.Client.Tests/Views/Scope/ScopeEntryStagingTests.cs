using Xunit;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.FieldMenus;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The text a Shift+row-click stages in the Scope's preview: <c>$ {callsign} {command}</c>, replacing the preview unless it
/// already holds a <c>$</c> entry for the same callsign, which gains <c>, {command}</c>; the <c>$</c> keeps a staged command
/// whose verb is also an ERAM verb reading as a YAAT command.
/// </summary>
public class ScopeEntryStagingTests
{
    [Theory]
    [InlineData("", "$ UAL1 SPD 250")]
    [InlineData("$ UAL1 CM 80", "$ UAL1 CM 80, SPD 250")]
    [InlineData("$ ual1 cm 80", "$ ual1 cm 80, SPD 250")]
    [InlineData("$UAL1 CM 80", "$UAL1 CM 80, SPD 250")]
    [InlineData("$ DAL2 CM 80", "$ UAL1 SPD 250")]
    [InlineData("PILOT UAL1 CM 80", "$ UAL1 SPD 250")]
    [InlineData("$ UAL1", "$ UAL1 SPD 250")]
    [InlineData("+100", "$ UAL1 SPD 250")]
    [InlineData(".rbl", "$ UAL1 SPD 250")]
    public void Text_ReplacesOrAppends(string preview, string expected) =>
        Assert.Equal(expected, ScopeEntryStaging.Text(preview, "UAL1", "SPD 250", CommandScheme.Default()));

    [Theory]
    [InlineData("DM 60", ScopeProfileKind.Eram)]
    [InlineData("DM 60", ScopeProfileKind.Mixed)]
    [InlineData("LA", ScopeProfileKind.Eram)]
    [InlineData("LA", ScopeProfileKind.Mixed)]
    public void StagedEramVerb_ReadsAsPilot(string command, ScopeProfileKind profile)
    {
        string staged = ScopeEntryStaging.Text("", "UAL1", command, CommandScheme.Default());

        Assert.Equal($"$ UAL1 {command}", staged);
        ScopeEntryReading reading = ScopeEntryClassifier.Classify(staged, profile, null, CommandScheme.Default());
        Assert.Equal(ScopeEntryKind.Pilot, reading.Kind);
        Assert.Equal($"UAL1 {command}", reading.SendText);
    }

    [Fact]
    public void Text_StagesTheCommandInTheUsersRenamedVerb()
    {
        var scheme = CommandScheme.Default();
        scheme.Patterns[CommandRegistry.AliasToCanonicType["SPD"]].Aliases = ["VEL"];

        string staged = ScopeEntryStaging.Text("", "UAL1", "SPD 250", scheme);

        Assert.Equal("$ UAL1 VEL 250", staged);
        ScopeEntryReading reading = ScopeEntryClassifier.Classify(staged, ScopeProfileKind.Stars, null, scheme);
        Assert.Equal(ScopeEntryKind.Pilot, reading.Kind);
        Assert.Equal("UAL1 VEL 250", reading.SendText);
    }

    [Fact]
    public void Text_AppendsACommandInItsOwnVerb()
    {
        var scheme = CommandScheme.Default();
        scheme.Patterns[CommandRegistry.AliasToCanonicType["SPD"]].Aliases = ["VEL"];

        Assert.Equal("$ UAL1 VEL 250, CM 80", ScopeEntryStaging.Text("$ UAL1 VEL 250", "UAL1", "CM 80", scheme));
    }
}
