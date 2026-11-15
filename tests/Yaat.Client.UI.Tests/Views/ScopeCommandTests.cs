using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The <c>.scope</c> dot commands: <c>.scope range</c> and <c>.scope template</c> act on the open YAAT Scope and echo
/// themselves in the status line; the toolbar runs the same commands.
/// </summary>
public class ScopeCommandTests
{
    private const string Usage = "Usage: .scope range <1-256> | .scope template <stars|eram|mixed|euroscope>";

    private static async Task<MainViewModel> MainAsync()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        return main;
    }

    private static async Task<(MainViewModel Main, ScopeViewModel Scope)> OpenScopeAsync()
    {
        MainViewModel main = await MainAsync();
        var scope = new ScopeViewModel(main);
        main.OpenScope = scope;
        return (main, scope);
    }

    [AvaloniaFact]
    public async Task Range_SetsTheRange_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope range 25");

            Assert.Equal(25, scope.RangeNm);
            Assert.Equal(".scope range 25", main.StatusText);

            main.RunScopeCommand(".SCOPE RANGE 30");
            Assert.Equal(30, scope.RangeNm);
            Assert.Equal(".scope range 30", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Range_AcceptsItsBounds()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope range 1");
            Assert.Equal(1, scope.RangeNm);
            main.RunScopeCommand(".scope range 256");
            Assert.Equal(256, scope.RangeNm);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData("0")]
    [InlineData("257")]
    [InlineData("abc")]
    [InlineData("25.5")]
    public async Task Range_OutOfBoundsOrNotAWholeNumber_GivesTheUsage_AndLeavesTheRange(string value)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            double before = scope.RangeNm;

            main.RunScopeCommand($".scope range {value}");

            Assert.Equal("Usage: .scope range <1-256>", main.StatusText);
            Assert.Equal(before, scope.RangeNm);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData("eram", "eram")]
    [InlineData("ERAM", "eram")]
    [InlineData("Mixed", "mixed")]
    [InlineData("euroscope", "euroscope")]
    public async Task Template_PicksTheTemplate_AndEchoesTheCanonicalId(string typed, string expected)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            bool before = main.Preferences.EuroScopeMode;

            main.RunScopeCommand($".scope template {typed}");

            Assert.Equal(expected, scope.TemplateId);
            Assert.Equal($".scope template {expected}", main.StatusText);
            Assert.Equal(before, main.Preferences.EuroScopeMode);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Template_Unknown_SaysSo_AndLeavesTheTemplate()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            string before = scope.TemplateId;

            main.RunScopeCommand(".scope template radar");

            Assert.Equal("Unknown scope template: radar", main.StatusText);
            Assert.Equal(before, scope.TemplateId);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(".scope range 25")]
    [InlineData(".scope range abc")]
    [InlineData(".scope template eram")]
    public async Task NoOpenScope_SaysSo(string command)
    {
        MainViewModel main = await MainAsync();

        main.RunScopeCommand(command);

        Assert.Equal("No YAAT Scope is open", main.StatusText);
    }

    [AvaloniaFact]
    public async Task ScopeAlone_OrAnotherVerb_GivesTheUsage()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope");
            Assert.Equal(Usage, main.StatusText);

            main.StatusText = "";
            main.RunScopeCommand(".scope zoom 5");
            Assert.Equal(Usage, main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task TypedRange_ThroughTheCommandLine_ChangesTheRange()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.CommandText = ".scope range 30";
            await main.SendCommandCommand.ExecuteAsync(null);

            Assert.Equal(30, scope.RangeNm);
            Assert.Equal(".scope range 30", main.StatusText);

            // ".scopes" is no .scope command: it leaves the range alone and falls through to the unknown-command line.
            main.CommandText = ".scopes range 5";
            await main.SendCommandCommand.ExecuteAsync(null);
            Assert.Equal(30, scope.RangeNm);
            Assert.Equal("Unknown command or alias: .scopes", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }
}
