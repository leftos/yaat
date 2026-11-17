using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Toolbar;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope's weather overlay: the METAR lines of the position's airports (or a "No METAR for" note), green mono 14 at the
/// bottom left, growing upward; on the ERAM and Mixed profiles it sits above the Response Area and the MCA. DISPLAY's WX and
/// <c>.scope wx</c> turn it on and off; it is on by default.
/// </summary>
public class ScopeWeatherOverlayTests
{
    private const string AunMetar = "METAR KAUN 230235Z AUTO 00000KT 10SM CLR 17/08 A3002";
    private const string SfoMetar = "METAR KSFO 230236Z 29012KT 10SM FEW008 16/12 A2998";
    private const string HnlMetar = "METAR PHNL 230236Z 07012KT 10SM FEW020 27/19 A3005";

    private static void LoadWeather(MainViewModel main, params string[] metars) =>
        main.ApplyWeatherChanged(new WeatherChangedDto("Live Weather", null, null, [.. metars], null, null, null));

    private static void Position(MainViewModel main, params string[] airports) =>
        main.ScopeMaps.ApplyPositionDisplayConfig(new PositionDisplayConfigDto([], [], [.. airports], airports[0], null, 3));

    [AvaloniaFact]
    public void PositionFilterWithNoMatch_ShowsANote()
    {
        (MainViewModel main, ScopeViewModel scope) = Scope();
        using (scope)
        {
            Position(main, "SFO", "OAK");
            LoadWeather(main, AunMetar);

            Assert.Equal(["No METAR for SFO OAK"], scope.WeatherLines);
        }
    }

    [AvaloniaFact]
    public void PPrefixedMetar_MatchesTheFaaAirportId()
    {
        (MainViewModel main, ScopeViewModel scope) = Scope();
        using (scope)
        {
            Position(main, "HNL");
            LoadWeather(main, HnlMetar);

            Assert.StartsWith("HNL ", Assert.Single(scope.WeatherLines));
        }
    }

    [AvaloniaFact]
    public void NoPositionFilter_ShowsAllStations()
    {
        (MainViewModel main, ScopeViewModel scope) = Scope();
        using (scope)
        {
            LoadWeather(main, AunMetar, SfoMetar);

            Assert.Equal(2, scope.WeatherLines.Count);
        }
    }

    [AvaloniaFact]
    public void WeatherLoadedBeforePositionChange_ReFiltersAndNotes()
    {
        (MainViewModel main, ScopeViewModel scope) = Scope();
        using (scope)
        {
            LoadWeather(main, AunMetar);
            Assert.Single(scope.WeatherLines);

            Position(main, "SFO");
            main.UpdateRadarWeatherDisplay();
            Assert.Equal(["No METAR for SFO"], scope.WeatherLines);

            Position(main, "AUN");
            main.UpdateRadarWeatherDisplay();
            Assert.StartsWith("AUN ", Assert.Single(scope.WeatherLines));
        }
    }

    [AvaloniaFact]
    public async Task WxVerbAndTile_TurnTheOverlayOnAndOff_AndEcho()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            ScopeButton wx = ScopeToolbarButtons.Find(ScopeButtonId.DisplayWx)!;
            Assert.Equal(("WX", ScopeButtonKind.Toggle, ".scope wx"), (wx.Label, wx.Kind, wx.Command));
            Assert.True(scope.ShowWeather);
            Assert.True(scope.IsActive(wx));

            main.RunScopeCommand(".scope wx off");
            Assert.False(scope.ShowWeather);
            Assert.Equal("Scope WX OFF", main.StatusText);

            scope.Press(wx);
            Assert.True(scope.ShowWeather);
            Assert.Equal("Scope WX ON", main.StatusText);

            main.RunScopeCommand(".scope wx");
            Assert.Equal("Usage: .scope wx <on|off>", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public void Weather_OnStars_SitsBottomLeftFromTheCorner()
    {
        (MainViewModel main, ScopeView view, Window window) = ShowScope();
        try
        {
            main.RunScopeCommand(".scope profile stars");
            LoadWeather(main, AunMetar, SfoMetar);
            Settle(window);

            ScopeWeatherText weather = view.Canvas.Weather!;
            Assert.Equal(2, weather.Lines.Count);
            Assert.Equal(ScopeMapToolsRenderer.WeatherMargin, weather.BottomInset);

            main.RunScopeCommand(".scope wx off");
            Assert.Null(view.Canvas.Weather);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Weather_NoMetar_DrawsNothing()
    {
        (_, ScopeView view, Window window) = ShowScope();
        try
        {
            Settle(window);
            var scope = (ScopeViewModel)view.DataContext!;

            Assert.Empty(scope.WeatherLines);
            Assert.Null(view.Canvas.Weather);
            Assert.True(scope.ShowWeather);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Weather_OnEram_SitsAboveTheResponseAreaAndMcaBoxes() => AboveTheBoxes("eram");

    [AvaloniaFact]
    public void Weather_OnMixed_SitsAboveTheResponseAreaAndMcaBoxes() => AboveTheBoxes("mixed");

    private static void AboveTheBoxes(string profile)
    {
        (MainViewModel main, ScopeView view, Window window) = ShowScope();
        try
        {
            main.RunScopeCommand($".scope profile {profile}");
            LoadWeather(main, AunMetar, SfoMetar);
            Settle(window);

            ScopeWeatherText weather = view.Canvas.Weather!;
            Point boxesTop = view.EntryLayer.EramBoxes.TranslatePoint(new Point(0, 0), view.Canvas)!.Value;
            double textBottom = view.Canvas.Bounds.Height - weather.BottomInset;
            Assert.True(boxesTop.Y > 100, $"the boxes sit at the bottom ({boxesTop.Y})");
            Assert.True(textBottom <= boxesTop.Y, $"the weather ends at {textBottom}, above the boxes' top at {boxesTop.Y}");
            float[] baselines = ScopeMapToolsRenderer.WeatherBaselines(2, (float)view.Canvas.Bounds.Height, weather.BottomInset);
            float lastLineBottom = baselines[^1] + ScopeMapToolsRenderer.WeatherDescent;
            Assert.True(lastLineBottom <= boxesTop.Y, $"the last line ends at {lastLineBottom}, above the boxes' top at {boxesTop.Y}");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static (MainViewModel Main, ScopeViewModel Scope) Scope()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
        return (main, scope);
    }

    private static (MainViewModel Main, ScopeView View, Window Window) ShowScope()
    {
        (MainViewModel main, ScopeViewModel scope) = Scope();
        ScopeMapToolsCommandTests.LoadSource(main);
        var view = new ScopeView { DataContext = scope };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };
        window.Closed += (_, _) => scope.Dispose();
        window.Show();
        Settle(window);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (main, view, window);
    }

    private static void Settle(Window window)
    {
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }
}
