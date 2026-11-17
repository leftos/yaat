using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim;
using Yaat.Sim.Data;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// TDM, the Scope's top-down mode: the <c>.scope tdm</c> verb, the TDM button and Ctrl+Shift+T turn it on and off; while it is
/// on the Scope draws ground targets and the TDM-only maps; pref sets and the saved state keep it.
/// </summary>
public class ScopeTdmTests : IDisposable
{
    private const string TdmUsage = "Usage: .scope tdm <on|off>";

    private static readonly ActivePositionDto Sector44 = new(
        Callsign: "OAK_44_CTR",
        Name: "Sector 44",
        RadioName: null,
        Frequency: "135.100",
        FacilityId: "ZOA",
        FacilityName: "Oakland Center",
        PositionType: "Artcc",
        HandoffCode: "C44",
        EramSectorId: "44"
    );

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-scope-tdm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ScopeButton Button(ScopeButtonId id) => ScopeToolbarButtons.Find(id)!;

    /// <summary>A scope viewed from ZOA sector 44, saving through this test's store.</summary>
    private async Task<(MainViewModel Main, ScopeViewModel Scope)> OpenStoredScopeAsync()
    {
        MainViewModel main = await MainAsync();
        main.SetActivePositionFromServer(new PositionDisplayConfigDto([], [], [], Sector44.HandoffCode, Sector44, 3, NoStarsDisplay: true));
        return (main, Reopen(main));
    }

    private ScopeViewModel Reopen(MainViewModel main)
    {
        var scope = new ScopeViewModel(main, Store(), profileStore: null);
        main.OpenScope = scope;
        return scope;
    }

    private ScopePrefSetStore Store() => new(_root, AppLog.CreateLogger<ScopePrefSetStore>());

    private static Task<(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window)> ShowScope() => ShowScopeWith(_ => { });

    /// <summary>Shows a scope in a window, running <paramref name="beforeScope"/> on the main view model before the scope opens.</summary>
    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window)> ShowScopeWith(
        Action<MainViewModel> beforeScope
    )
    {
        MainViewModel main = await MainAsync();
        beforeScope(main);
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null);
        main.OpenScope = scope;
        var view = new ScopeView { DataContext = scope };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };
        window.Closed += (_, _) => scope.Dispose();
        window.Show();
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (main, scope, view, window);
    }

    private static AircraftModel AddAt(MainViewModel main, ScopeCanvas canvas, string callsign, double screenX, double screenY, bool onGround)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)screenX, (float)screenY);
        var ac = new AircraftModel
        {
            Callsign = callsign,
            AircraftType = "B738",
            FiledAircraftType = "B738",
            FlightRules = "IFR",
            TransponderMode = "C",
            Altitude = onGround ? 0 : 5000,
            GroundSpeed = onGround ? 0 : 250,
            IsOnGround = onGround,
            Position = new LatLon(lat, lon),
        };
        main.Aircraft.Add(ac);
        Dispatcher.UIThread.RunJobs();
        return ac;
    }

    private static bool IsDrawn(ScopeCanvas canvas, AircraftModel ac) => canvas.CurrentFrame.Tracks.Any(t => ReferenceEquals(t.Aircraft, ac));

    [AvaloniaFact]
    public async Task Verb_TurnsTdmOnAndOff_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            Assert.False(scope.IsTdm);

            main.RunScopeCommand(".scope tdm on");
            Assert.True(scope.IsTdm);
            Assert.Equal("Scope TDM ON", main.StatusText);

            main.RunScopeCommand(".scope TDM Off");
            Assert.False(scope.IsTdm);
            Assert.Equal("Scope TDM OFF", main.StatusText);

            main.RunScopeCommand(".scope tdm");
            Assert.Equal(TdmUsage, main.StatusText);
            Assert.False(scope.IsTdm);

            main.RunScopeCommand(".scope tdm on");
            main.RunScopeCommand(".scope tdm maybe");
            Assert.Equal(TdmUsage, main.StatusText);
            Assert.True(scope.IsTdm);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Tdm_ShowsGroundTargets_OnlyWhileOn()
    {
        (MainViewModel main, _, ScopeView view, Window window) = await ShowScope();
        try
        {
            await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            AircraftModel ground = AddAt(main, view.Canvas, "SWA12", 300, 300, onGround: true);
            AircraftModel airborne = AddAt(main, view.Canvas, "UAL238", 500, 200, onGround: false);

            Assert.False(IsDrawn(view.Canvas, ground));
            Assert.True(IsDrawn(view.Canvas, airborne));

            main.RunScopeCommand(".scope tdm on");
            Dispatcher.UIThread.RunJobs();

            Assert.True(view.Canvas.ShowTopDown);
            Assert.True(IsDrawn(view.Canvas, ground));
            Assert.True(IsDrawn(view.Canvas, airborne));

            main.RunScopeCommand(".scope tdm off");
            Dispatcher.UIThread.RunJobs();

            Assert.False(IsDrawn(view.Canvas, ground));
            Assert.True(IsDrawn(view.Canvas, airborne));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Centres the radar with two maps on: <paramref name="ordinary"/> (STARS id 2), the TDM-only <paramref name="diagram"/> (3).</summary>
    private static void CentreRadarWithMaps(MainViewModel main, VideoMapData ordinary, VideoMapData diagram)
    {
        main.Radar.MapToggles.Add(MapToggle(ordinary.MapId, 2, tdmOnly: false));
        main.Radar.MapToggles.Add(MapToggle(diagram.MapId, 3, tdmOnly: true));
        main.Radar.RangeNm = 60;
        main.Radar.ActiveVideoMaps = [ordinary, diagram];
        main.Radar.CenterLat = 37.62;
        main.Radar.CenterLon = -122.38;
    }

    private static VideoMapToggleItem MapToggle(string mapId, int starsId, bool tdmOnly) =>
        new()
        {
            MapId = mapId,
            ShortName = $"MAP{starsId}",
            Name = $"Map {starsId}",
            BrightnessCategory = "A",
            StarsId = starsId,
            TdmOnly = tdmOnly,
            IsEnabled = true,
        };

    [AvaloniaFact]
    public async Task ATdmOnlyMap_IsDrawnOnlyWhileTdmIsOn()
    {
        VideoMapData ordinary = new() { MapId = "m2", Lines = [] };
        VideoMapData diagram = new() { MapId = "m3", Lines = [] };
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeWith(main => CentreRadarWithMaps(main, ordinary, diagram));
        try
        {
            Assert.All(scope.MapToggles, t => Assert.True(t.IsOn));
            Assert.Equal([ordinary], view.Canvas.RenderSnapshotForTesting().VideoMaps);

            scope.IsTdm = true;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([ordinary, diagram], view.Canvas.RenderSnapshotForTesting().VideoMaps);

            scope.IsTdm = false;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([ordinary], view.Canvas.RenderSnapshotForTesting().VideoMaps);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Button_LightsWithTdm_AndClosesAnOpenTray()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            ScopeButton tdm = Button(ScopeButtonId.Tdm);
            Assert.Equal("TDM", tdm.Label);
            Assert.Equal(ScopeButtonKind.Toggle, tdm.Kind);
            Assert.False(scope.IsActive(tdm));

            scope.Press(Button(ScopeButtonId.Brite));
            Assert.Equal(ScopeButtonId.Brite, scope.OpenMenu);

            scope.Press(tdm);

            Assert.Null(scope.OpenMenu);
            Assert.True(scope.IsTdm);
            Assert.True(scope.IsActive(tdm));
            Assert.Equal("Scope TDM ON", main.StatusText);

            scope.Press(tdm);

            Assert.False(scope.IsTdm);
            Assert.False(scope.IsActive(tdm));
            Assert.Equal("Scope TDM OFF", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task CtrlShiftT_TogglesTdm_InTheScopeWindow()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        var window = new ScopeWindow(main.Preferences, scope);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        try
        {
            RawInputModifiers action = (PlatformHelper.IsMacOS ? RawInputModifiers.Meta : RawInputModifiers.Control) | RawInputModifiers.Shift;

            window.DispatchKey(Key.T, action);

            Assert.True(scope.IsTdm);
            Assert.Equal("Scope TDM ON", main.StatusText);
            Assert.Contains("active", ScopeToolbarTests.ButtonOf(window, ScopeButtonId.Tdm).Classes);

            window.DispatchKey(Key.T, action);

            Assert.False(scope.IsTdm);
            Assert.DoesNotContain("active", ScopeToolbarTests.ButtonOf(window, ScopeButtonId.Tdm).Classes);

            RawInputModifiers plainAction = PlatformHelper.IsMacOS ? RawInputModifiers.Meta : RawInputModifiers.Control;

            window.DispatchKey(Key.T, plainAction);

            Assert.False(scope.IsTdm);

            window.DispatchKey(Key.T);

            Assert.False(scope.IsTdm);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Tdm_IsSavedInAPrefSetAndTheSavedState()
    {
        (MainViewModel main, ScopeViewModel first) = await OpenStoredScopeAsync();
        Assert.False(first.CapturePrefSet(null).Tdm);

        main.RunScopeCommand(".scope tdm on");
        Assert.True(first.CapturePrefSet(null).Tdm);

        main.RunScopeCommand(".scope pref saveas TD");
        main.RunScopeCommand(".scope tdm off");
        main.RunScopeCommand(".scope pref load TD");
        Assert.True(first.IsTdm);

        first.ApplyPrefSet(first.CapturePrefSet(null) with { Tdm = false });
        Assert.False(first.IsTdm);

        main.RunScopeCommand(".scope tdm on");
        first.Dispose();

        ScopeViewModel reopened = Reopen(main);
        try
        {
            Assert.True(reopened.IsTdm);
        }
        finally
        {
            reopened.Dispose();
        }
    }
}
