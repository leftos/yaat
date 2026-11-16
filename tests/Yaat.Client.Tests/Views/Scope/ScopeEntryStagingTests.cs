using Xunit;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.FieldMenus;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The text a Shift+row-click stages in the Scope's preview: <c>PILOT {callsign} {command}</c>, replacing the preview unless it
/// already holds a pilot entry for the same callsign, which gains <c>, {command}</c>; and the <c>PILOT</c> word keeps a staged
/// command whose verb is also an ERAM verb reading as a pilot command.
/// </summary>
public class ScopeEntryStagingTests
{
    [Theory]
    [InlineData("", "PILOT UAL1 SPD 250")]
    [InlineData("PILOT UAL1 CM 80", "PILOT UAL1 CM 80, SPD 250")]
    [InlineData("pilot ual1 cm 80", "pilot ual1 cm 80, SPD 250")]
    [InlineData("PILOT DAL2 CM 80", "PILOT UAL1 SPD 250")]
    [InlineData("+100", "PILOT UAL1 SPD 250")]
    [InlineData(".rbl", "PILOT UAL1 SPD 250")]
    public void Text_ReplacesOrAppends(string preview, string expected) => Assert.Equal(expected, ScopeEntryStaging.Text(preview, "UAL1", "SPD 250"));

    [Theory]
    [InlineData("DM 60", ScopeProfileKind.Eram)]
    [InlineData("DM 60", ScopeProfileKind.Mixed)]
    [InlineData("LA", ScopeProfileKind.Eram)]
    [InlineData("LA", ScopeProfileKind.Mixed)]
    public void StagedEramVerb_ReadsAsPilot(string command, ScopeProfileKind profile)
    {
        string staged = ScopeEntryStaging.Text("", "UAL1", command);

        Assert.Equal($"PILOT UAL1 {command}", staged);
        Assert.Equal(ScopeEntryKind.Pilot, ScopeEntryClassifier.Classify(staged, profile).Kind);
    }
}
