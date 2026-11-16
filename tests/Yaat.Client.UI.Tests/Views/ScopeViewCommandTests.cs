using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope.Toolbar;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The <c>.view</c> dot command: <c>.view &lt;ssa|ca|rpoq&gt; &lt;on|off&gt;</c> opens or closes a view of the open YAAT Scope and
/// echoes itself in the status line; the toolbar's VIEWS toggles and the cards' close buttons run the same command.
/// </summary>
public class ScopeViewCommandTests
{
    private const string Usage = "Usage: .view <ssa|ca|rpoq> <on|off|dock> | .view <ssa|ca|rpoq> tear <tl|tr|bl|br> <dx> <dy>";

    [AvaloniaFact]
    public async Task On_OpensTheView_AndOff_ClosesIt_CaseInsensitive()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            Assert.False(scope.IsViewOpen(ScopeToolbarButtons.ViewCaName));

            main.RunViewCommand(".view ca on");
            Assert.True(scope.IsViewOpen(ScopeToolbarButtons.ViewCaName));
            Assert.Equal(".view ca on", main.StatusText);

            main.RunViewCommand(".VIEW CA OFF");
            Assert.False(scope.IsViewOpen(ScopeToolbarButtons.ViewCaName));
            Assert.Equal(".view ca off", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task On_Twice_EchoesTwice_AndStaysOpen()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunViewCommand(".view ca on");
            main.StatusText = "";

            main.RunViewCommand(".view ca on");

            Assert.True(scope.IsViewOpen(ScopeToolbarButtons.ViewCaName));
            Assert.Equal(".view ca on", main.StatusText);
            Assert.Equal([ScopeToolbarButtons.ViewSsaName, ScopeToolbarButtons.ViewCaName], scope.OpenViews);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(".view tab on")]
    [InlineData(".view ca")]
    [InlineData(".view ca maybe")]
    [InlineData(".view ca on x")]
    [InlineData(".view")]
    public async Task UnknownViewOrWrongState_GivesTheUsage_AndChangesNothing(string command)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            IReadOnlyList<string> before = [.. scope.OpenViews];

            main.RunViewCommand(command);

            Assert.Equal(Usage, main.StatusText);
            Assert.Equal(before, scope.OpenViews);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(".view ssa on")]
    [InlineData(".view bogus")]
    public async Task NoOpenScope_SaysSo(string command)
    {
        MainViewModel main = await MainAsync();

        main.RunViewCommand(command);

        Assert.Equal("No YAAT Scope is open", main.StatusText);
    }

    [AvaloniaFact]
    public async Task TypedView_ThroughTheCommandLine_ChangesTheView()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.CommandText = ".view ca on";
            await main.SendCommandCommand.ExecuteAsync(null);

            Assert.True(scope.IsViewOpen(ScopeToolbarButtons.ViewCaName));
            Assert.Equal(".view ca on", main.StatusText);

            // ".views" is no .view command: it leaves the views alone and falls through to the unknown-command line.
            main.CommandText = ".views ca off";
            await main.SendCommandCommand.ExecuteAsync(null);
            Assert.True(scope.IsViewOpen(ScopeToolbarButtons.ViewCaName));
            Assert.Equal("Unknown command or alias: .views", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }
}
