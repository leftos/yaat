using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope.Profiles;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// A CA inhibit run from the Scope (STARS <c>CA K</c>, F11's <c>CA </c>, or <c>$CAINH</c>) acts on the student's display too,
/// so the Scope answers it naming that display (7110.65 §5-14-6.c.4): the Sim's "inhibited" reads
/// <c>CA INHIBITED (student display)</c> and its "enabled" <c>CA ENABLED (student display)</c>. Any other entry keeps the
/// Sim's text, and so does a failed CA inhibit, even one whose text says "inhibited" or "enabled".
/// </summary>
public class ScopeCaInhibitResponseTests
{
    [AvaloniaTheory]
    [InlineData("CA K UAL238", "Conflict alert inhibited for UAL238", "CA INHIBITED (student display)")]
    [InlineData("CA K UAL238", "Conflict alert enabled for UAL238", "CA ENABLED (student display)")]
    [InlineData("CA K", "Conflict alert inhibited for UAL238", "CA INHIBITED (student display)")]
    [InlineData("$UAL238 CAINH", "Conflict alert enabled for UAL238", "CA ENABLED (student display)")]
    [InlineData("$UAL238 FH 270", "Fly heading 270", "Fly heading 270")]
    public async Task ScopeEntry_AnswersACaInhibit_NamingTheStudentDisplay(string entry, string simText, string expected)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        scope.ProfileKind = ScopeProfileKind.Stars;
        main.SelectedAircraft = AddAircraft(main, "UAL238", 5000);
        scope.YaatEntrySender = (_, sink, _) =>
        {
            sink.ReportStatus(simText);
            return Task.CompletedTask;
        };

        Assert.True(scope.TypeEntryText(entry));
        Assert.True(scope.ExecuteEntry());
        await scope.LastEntryRun;

        Assert.NotNull(scope.EntryResponse);
        Assert.Equal(expected, scope.EntryResponse.Message);
    }

    [AvaloniaTheory]
    [InlineData("CA K UAL238", "Unknown aircraft: UAL238")]
    [InlineData("$UAL238 CAINH", "Conflict alert cannot be inhibited for UAL238")]
    [InlineData("CA K", "Conflict alert cannot be enabled for UAL238")]
    public async Task ScopeEntry_LeavesAFailedCaInhibitReply_AsTheSimWroteIt(string entry, string simError)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        scope.ProfileKind = ScopeProfileKind.Stars;
        main.SelectedAircraft = AddAircraft(main, "UAL238", 5000);
        scope.YaatEntrySender = (_, sink, _) =>
        {
            sink.ReportError(simError);
            return Task.CompletedTask;
        };

        Assert.True(scope.TypeEntryText(entry));
        Assert.True(scope.ExecuteEntry());
        await scope.LastEntryRun;

        Assert.NotNull(scope.EntryResponse);
        Assert.Equal(ScopeResponseKind.Error, scope.EntryResponse.Kind);
        Assert.Equal(simError, scope.EntryResponse.Message);
    }
}
