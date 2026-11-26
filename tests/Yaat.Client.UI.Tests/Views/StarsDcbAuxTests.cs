using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Client.Views.Scope.Toolbar.Dcb;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The STARS DCB's Aux page in the Scope host: SHIFT shows it and SHIFT again the Main page, its HISTORY and PTL LNTH
/// spinners step the Scope's history and vector, PTL OWN and PTL ALL pick the vector ownership, DWELL ON toggles hover
/// dwell, and TPA/ATPA opens its submenu, which DONE or Esc leaves for the Aux page.
/// </summary>
public class StarsDcbAuxTests : IDisposable
{
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

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-dcb-aux-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>A STARS-profile Scope host in a window, with a pref store and one map.</summary>
    private async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeHost Host, Window Window)> ShowStarsHostAsync()
    {
        MainViewModel main = await ScopeTestFixture.MainAsync();
        main.SetActivePositionFromServer(new PositionDisplayConfigDto([], [], [], "C44", Sector44, 3, NoStarsDisplay: true));
        main.ScopeMaps.ApplyVideoMapsDtoForTesting(
            new FacilityVideoMapsDto(
                "ZOA",
                "NCT",
                [new StarsAreaDto("A", "Area A", 37.62, -122.38, 60, [])],
                [new VideoMapInfoDto("m2", "Map 2", "MAP2", [], "A", 2, false, false)],
                []
            ),
            null,
            null
        );
        var store = new ScopePrefSetStore(_root, AppLog.CreateLogger<ScopePrefSetStore>());
        var scope = new ScopeViewModel(main, store, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
        var host = new ScopeHost { DataContext = scope };
        var window = new Window
        {
            Width = 1200,
            Height = 800,
            Content = host,
        };
        window.Closed += (_, _) => scope.Dispose();
        window.Show();
        main.RunScopeCommand(".scope profile stars");
        Settle(window);
        return (main, scope, host, window);
    }

    private static void Settle(Window window)
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Close(Window window)
    {
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static StarsDcbBar Bar(ScopeHost host) => Assert.Single(host.GetVisualDescendants().OfType<StarsDcbBar>());

    private static DcbTile Labelled(ScopeHost host, string firstLine)
    {
        StarsDcbBar bar = Bar(host);
        int index = bar.Entries.ToList().FindIndex(e => e.Label.Split('\n')[0] == firstLine);
        Assert.True(index >= 0);
        return bar.Tiles[index];
    }

    /// <summary>Presses SHIFT on the Main page, which shows the Aux page.</summary>
    private static void OpenAux(Window window, ScopeHost host)
    {
        Assert.Equal(StarsDcbPage.Main, Bar(host).Page);
        Press(window, Labelled(host, "SHIFT"));
        Assert.Equal(StarsDcbPage.Aux, Bar(host).Page);
    }

    /// <summary>Shows the Aux page and opens its TPA/ATPA submenu.</summary>
    private static void OpenTpaAtpa(Window window, ScopeHost host)
    {
        OpenAux(window, host);
        Press(window, Labelled(host, "TPA/"));
        Assert.Equal(StarsDcbPage.TpaAtpa, Bar(host).Page);
    }

    private static Point Centre(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Press(Window window, Control control)
    {
        Point point = Centre(window, control);
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    private static void Wheel(Window window, Control control, int notches)
    {
        window.MouseWheel(Centre(window, control), new Vector(0, notches));
        Settle(window);
    }

    private static void Esc(Window window)
    {
        window.DispatchKey(Key.Escape);
        Settle(window);
    }

    private static void FocusCanvas(Window window, ScopeView view)
    {
        Point? inWindow = view.Canvas.TranslatePoint(new Point(5, view.Canvas.Bounds.Height - 5), window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, MouseButton.Left);
        window.MouseUp(inWindow.Value, MouseButton.Left);
        Settle(window);
    }

    [AvaloniaFact]
    public async Task Shift_ShowsTheAuxPage_AndShiftAgainReturnsToMain()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            OpenAux(window, host);

            Assert.Equal(StarsSubmenus.Aux.Count, Bar(host).Tiles.Count);
            Assert.False(scope.IsBarSubmenuOpen);

            Press(window, Labelled(host, "SHIFT"));

            Assert.Equal(StarsDcbPage.Main, Bar(host).Page);
            Assert.Equal(StarsMainPage.Entries.Count, Bar(host).Tiles.Count);
            Assert.False(scope.IsBarSubmenuOpen);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task History_WheelWhileLatched_StepsTheHistoryCount_AndEscRestoresIt()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            scope.RunCommand(ScopeToolbarButtons.HistCommand(3));
            OpenAux(window, host);
            DcbTile history = Labelled(host, "HISTORY");
            Assert.Equal("HISTORY\n3", history.Label);

            Press(window, history);
            Assert.True(history.IsLatched);
            Wheel(window, history, 1);

            Assert.Equal(4, scope.EffectiveBand.History);
            Assert.Equal("HISTORY\n4", history.Label);

            Esc(window);

            Assert.False(history.IsLatched);
            Assert.Equal(3, scope.EffectiveBand.History);
            Assert.Equal(StarsDcbPage.Aux, Bar(host).Page);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task PtlLnth_WheelWhileLatched_StepsTheVectorMinutes()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            scope.RunCommand(ScopeToolbarButtons.VectorCommand(1));
            OpenAux(window, host);
            DcbTile ptl = Labelled(host, "PTL");

            Press(window, ptl);
            Wheel(window, ptl, 1);
            Press(window, ptl);

            Assert.False(ptl.IsLatched);
            Assert.Equal(1.5, scope.EffectiveBand.VectorMin);
            Assert.StartsWith("PTL\nLNTH\n1.5m", ptl.Label, StringComparison.Ordinal);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task PtlAll_LightsAndSetsAll_ItsSecondPressChangesNothing_AndPtlOwnSetsOwnBack()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            scope.RunCommand(ScopeToolbarButtons.VectorOwnershipCommand(ScopeVectorOwnership.Own));
            OpenAux(window, host);
            Assert.Equal((true, false), (Labelled(host, "PTL OWN").IsPushed, Labelled(host, "PTL ALL").IsPushed));

            Press(window, Labelled(host, "PTL ALL"));

            Assert.Equal(ScopeVectorOwnership.All, scope.VectorOwnership);
            Assert.Equal((false, true), (Labelled(host, "PTL OWN").IsPushed, Labelled(host, "PTL ALL").IsPushed));

            Press(window, Labelled(host, "PTL ALL"));

            Assert.Equal(ScopeVectorOwnership.All, scope.VectorOwnership);
            Assert.Equal((false, true), (Labelled(host, "PTL OWN").IsPushed, Labelled(host, "PTL ALL").IsPushed));

            Press(window, Labelled(host, "PTL OWN"));

            Assert.Equal(ScopeVectorOwnership.Own, scope.VectorOwnership);
            Assert.Equal((true, false), (Labelled(host, "PTL OWN").IsPushed, Labelled(host, "PTL ALL").IsPushed));
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task DwellOn_TogglesHoverDwell()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            OpenAux(window, host);
            bool before = scope.DbSettings.HoverDwell;

            Press(window, Labelled(host, "DWELL"));

            Assert.Equal(!before, scope.DbSettings.HoverDwell);
            Assert.Equal(!before, Labelled(host, "DWELL").IsPushed);

            Press(window, Labelled(host, "DWELL"));

            Assert.Equal(before, scope.DbSettings.HoverDwell);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task TpaAtpa_OpensItsSubmenu_IntrailDistanceTogglesAtpa_AndDoneReturnsToAux()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            OpenTpaAtpa(window, host);
            Assert.True(scope.IsBarSubmenuOpen);
            Assert.Equal(StarsSubmenus.TpaAtpa.Count, Bar(host).Tiles.Count);
            bool before = scope.DbSettings.Atpa;

            Press(window, Labelled(host, "INTRAIL"));

            Assert.Equal(!before, scope.DbSettings.Atpa);
            Assert.Equal(!before, Labelled(host, "INTRAIL").IsPushed);

            Press(window, Labelled(host, "DONE"));

            Assert.Equal(StarsDcbPage.Aux, Bar(host).Page);
            Assert.False(scope.IsBarSubmenuOpen);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task Esc_InTpaAtpa_WithTheScopeFocused_ReturnsToAux_AndTheScopeChainDoesNotRun()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            OpenTpaAtpa(window, host);
            FocusCanvas(window, host.ScopeView);
            scope.ArmMeasure();

            Esc(window);

            Assert.Equal(StarsDcbPage.Aux, Bar(host).Page);
            Assert.False(scope.IsBarSubmenuOpen);
            Assert.True(main.Measure.IsMeasuring);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task Esc_OnTheAuxPage_WithTheMeasuringToolArmed_DisarmsIt_AndTheAuxPageStays()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            OpenAux(window, host);
            FocusCanvas(window, host.ScopeView);
            scope.ArmMeasure();
            Assert.True(main.Measure.IsMeasuring);

            Esc(window);

            Assert.False(main.Measure.IsMeasuring);
            Assert.Equal(StarsDcbPage.Aux, Bar(host).Page);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task AProfileChange_OnTheAuxPage_ShowsMain()
    {
        (MainViewModel main, _, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            OpenAux(window, host);

            main.RunScopeCommand(".scope profile mixed");
            Settle(window);
            main.RunScopeCommand(".scope profile stars");
            Settle(window);

            Assert.Equal(StarsDcbPage.Main, Bar(host).Page);
            Assert.Equal(StarsMainPage.Entries.Count, Bar(host).Tiles.Count);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task ADataContextChange_OnTheAuxPage_ShowsMain()
    {
        (MainViewModel main, _, ScopeHost host, Window window) = await ShowStarsHostAsync();
        var second = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        try
        {
            OpenAux(window, host);
            StarsDcbBar bar = Bar(host);

            bar.DataContext = second;
            Settle(window);

            Assert.Equal(StarsDcbPage.Main, bar.Page);
            Assert.Equal(StarsMainPage.Entries.Count, bar.Tiles.Count);
        }
        finally
        {
            Close(window);
            second.Dispose();
        }
    }

    /// <summary>
    /// Takes the Scope host, bar and all, out of the window and puts it back, as docking or popping the Scope out re-parents
    /// it: the bar leaves the visual tree and comes back with its DataContext unchanged.
    /// </summary>
    private static void Reparent(Window window, ScopeHost host)
    {
        StarsDcbBar bar = Bar(host);
        window.Content = null;
        Settle(window);
        Assert.False(bar.IsAttachedToVisualTree());
        window.Content = host;
        Settle(window);
        Assert.Same(bar, Bar(host));
        Assert.True(bar.IsAttachedToVisualTree());
    }

    [AvaloniaFact]
    public async Task AReparent_OnTheAuxPage_KeepsTheAuxPage()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            OpenAux(window, host);

            Reparent(window, host);

            Assert.Equal(StarsDcbPage.Aux, Bar(host).Page);
            Assert.Equal(StarsSubmenus.Aux.Count, Bar(host).Tiles.Count);
            Assert.False(scope.IsBarSubmenuOpen);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task AReparent_WithTpaAtpaOpen_ShowsTheAuxPage_AndClearsTheSubmenuFlag()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            OpenTpaAtpa(window, host);
            Assert.True(scope.IsBarSubmenuOpen);

            Reparent(window, host);

            Assert.Equal(StarsDcbPage.Aux, Bar(host).Page);
            Assert.False(scope.IsBarSubmenuOpen);
        }
        finally
        {
            Close(window);
        }
    }
}
