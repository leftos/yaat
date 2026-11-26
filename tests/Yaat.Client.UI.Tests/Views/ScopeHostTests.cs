using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Dock;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Client.Views.Scope.Toolbar.Dcb;
using Yaat.Client.Views.Scope.Toolbar.Eram;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope as one control: the host shows the Scope's toolbar, view and views dock over its view model wherever it sits, so
/// the main window's Scope tab and the Scope window show the same Scope.
/// </summary>
public class ScopeHostTests
{
    [AvaloniaFact]
    public async Task Host_InABareWindow_ShowsToolbarViewAndDock()
    {
        (MainViewModel _, ScopeViewModel scope) = await ScopeTestFixture.OpenScopeAsync();
        var host = new ScopeHost { DataContext = scope };
        var window = new Window
        {
            Width = 1200,
            Height = 800,
            Content = host,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        try
        {
            ScopeToolbar toolbar = Assert.Single(host.GetVisualDescendants().OfType<ScopeToolbar>());
            ScopeView view = Assert.Single(host.GetVisualDescendants().OfType<ScopeView>());
            ScopeDock dock = Assert.Single(host.GetVisualDescendants().OfType<ScopeDock>());

            Assert.Same(host.ScopeToolbar, toolbar);
            Assert.Same(host.ScopeView, view);
            Assert.Same(host.ScopeDock, dock);
            Assert.Same(scope, view.DataContext);
            Assert.Same(scope, toolbar.DataContext);
            Assert.Same(scope, dock.DataContext);
            Assert.True(view.Bounds.Height > 0);
        }
        finally
        {
            window.Close();
            scope.Dispose();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task BarKind_FollowsTheRoomProfile()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeTestFixture.OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope profile stars");
            Assert.Equal(ScopeBarKind.Stars, scope.BarKind);

            main.RunScopeCommand(".scope profile eram");
            Assert.Equal(ScopeBarKind.Eram, scope.BarKind);

            main.RunScopeCommand(".scope profile mixed");
            Assert.Equal(ScopeBarKind.Classic, scope.BarKind);
        }
        finally
        {
            scope.Dispose();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task BarKind_IgnoresATemplatePick()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeTestFixture.OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope profile stars");
            Assert.Equal(ScopeBarKind.Stars, scope.BarKind);

            scope.PickTemplateCommand.Execute("eram");

            Assert.Equal("eram", scope.TemplateId);
            Assert.Equal(ScopeBarKind.Stars, scope.BarKind);
        }
        finally
        {
            scope.Dispose();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task TheHost_ShowsTheStarsDcbUnderStars_TheEramBarUnderEram_AndTodaysBarUnderMixed()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeTestFixture.OpenScopeAsync();
        var host = new ScopeHost { DataContext = scope };
        var window = new Window
        {
            Width = 1200,
            Height = 800,
            Content = host,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        try
        {
            Assert.NotNull(host.ScopeToolbar);

            main.RunScopeCommand(".scope profile stars");
            Dispatcher.UIThread.RunJobs();
            Assert.Single(host.GetVisualDescendants().OfType<StarsDcbBar>());
            Assert.Throws<InvalidOperationException>(() => host.ScopeToolbar);

            main.RunScopeCommand(".scope profile eram");
            Dispatcher.UIThread.RunJobs();
            Assert.Single(host.GetVisualDescendants().OfType<EramBar>());
            Assert.Empty(host.GetVisualDescendants().OfType<StarsDcbBar>());
            Assert.Empty(host.GetVisualDescendants().OfType<ScopeToolbar>());

            main.RunScopeCommand(".scope profile mixed");
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(host.GetVisualDescendants().OfType<EramBar>());
            Assert.NotNull(host.ScopeToolbar);
        }
        finally
        {
            window.Close();
            scope.Dispose();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Hiding_TheClassicBar_ShowsTheNavyShowBarHalfTile_AndShowRestoresTheBar()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window) = await ShowHostAsync("mixed");
        try
        {
            ScopeButtonControl tile = HideToTheShowTile<ScopeButtonControl>(main, host, window);
            Assert.Equal(ScopeButtonId.BarShow, tile.Model.Id);
            Assert.True(tile.IsHalf);
            Assert.Equal("SHOW BAR", AutomationProperties.GetName(tile.Button));
            Assert.Contains("scopeButton", tile.Button.Classes);

            PressShowTile(scope, host, window, tile, 45);
        }
        finally
        {
            Close(window, scope);
        }
    }

    [AvaloniaFact]
    public async Task Hiding_TheStarsDcb_ShowsTheShowDcbHalfTile_AndShowRestoresTheBar()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window) = await ShowHostAsync("stars");
        try
        {
            DcbTile tile = HideToTheShowTile<DcbTile>(main, host, window);
            Assert.Equal("SHOW\nDCB", tile.Label);
            Assert.Equal(DcbTileShape.Half, tile.Shape);
            Assert.Equal(DcbTileKind.Normal, tile.Kind);

            PressShowTile(scope, host, window, tile, 45);
        }
        finally
        {
            Close(window, scope);
        }
    }

    [AvaloniaFact]
    public async Task Hiding_TheEramBar_ShowsTheShowToolbarButton_AndShowRestoresTheBar()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window) = await ShowHostAsync("eram");
        try
        {
            EramToolbarButton tile = HideToTheShowTile<EramToolbarButton>(main, host, window);
            Assert.Equal(("SHOW", "TOOLBAR"), (tile.Line1, tile.Line2));
            Assert.Equal(EramButtonKind.Command, tile.Kind);

            PressShowTile(scope, host, window, tile, EramMasterLayout.BarHeight);
        }
        finally
        {
            Close(window, scope);
        }
    }

    [AvaloniaFact]
    public async Task WhileHidden_AClickBesideOrBelowTheShowTile_ReachesTheScopeView()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window) = await ShowHostAsync("mixed");
        try
        {
            ScopeButtonControl tile = HideToTheShowTile<ScopeButtonControl>(main, host, window);

            Assert.True(HitsScopeView(window, host, new Point(tile.Bounds.Width + 40, 5)));
            Assert.True(HitsScopeView(window, host, new Point(5, tile.Bounds.Height + 20)));
            Assert.False(HitsScopeView(window, host, new Point(tile.Bounds.Width / 2, tile.Bounds.Height / 2)));
        }
        finally
        {
            Close(window, scope);
        }
    }

    [AvaloniaFact]
    public async Task AProfileSwitch_WhileHidden_KeepsTheBarHidden_AndSwapsTheShowTile()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window) = await ShowHostAsync("mixed");
        try
        {
            HideToTheShowTile<ScopeButtonControl>(main, host, window);

            main.RunScopeCommand(".scope profile stars");
            Settle(window);
            Assert.True(scope.IsBarHidden);
            Assert.Equal(0, BarRow(host).Height.Value);
            Assert.IsType<StarsDcbBar>(Slot(host, "BarSlot").Content);
            Assert.False(Slot(host, "BarSlot").IsVisible);
            Assert.IsType<DcbTile>(Slot(host, "ShowSlot").Content);

            main.RunScopeCommand(".scope profile eram");
            Settle(window);
            Assert.True(scope.IsBarHidden);
            Assert.Equal(0, BarRow(host).Height.Value);
            Assert.IsType<EramBar>(Slot(host, "BarSlot").Content);
            Assert.IsType<EramToolbarButton>(Slot(host, "ShowSlot").Content);
        }
        finally
        {
            Close(window, scope);
        }
    }

    /// <summary>A Scope host in a window, under the profile named <paramref name="profile"/>.</summary>
    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeHost Host, Window Window)> ShowHostAsync(string profile)
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeTestFixture.OpenScopeAsync();
        main.RunScopeCommand($".scope profile {profile}");
        var host = new ScopeHost { DataContext = scope };
        var window = new Window
        {
            Width = 1200,
            Height = 800,
            Content = host,
        };
        window.Show();
        Settle(window);
        return (main, scope, host, window);
    }

    /// <summary>
    /// Hides the bar with <c>.scope bar hide</c> and checks the host: the bar row collapsed, the bar kept but not shown, the
    /// scope grown up to the top, and the SHOW tile of type <typeparamref name="TTile"/> at the top-left.
    /// </summary>
    private static TTile HideToTheShowTile<TTile>(MainViewModel main, ScopeHost host, Window window)
        where TTile : Control
    {
        Control bar = Assert.IsAssignableFrom<Control>(Slot(host, "BarSlot").Content);

        main.RunScopeCommand(".scope bar hide");
        Settle(window);

        Assert.Equal(0, BarRow(host).Height.Value);
        Assert.Same(bar, Slot(host, "BarSlot").Content);
        Assert.False(bar.IsEffectivelyVisible);
        Assert.Equal(0, host.ScopeView.TranslatePoint(default, host)!.Value.Y);
        TTile tile = Assert.IsType<TTile>(Slot(host, "ShowSlot").Content);
        Assert.True(tile.IsEffectivelyVisible);
        Assert.Equal(new Point(0, 0), tile.TranslatePoint(default, host)!.Value);
        return tile;
    }

    /// <summary>Clicks the SHOW tile and checks the bar is back in a row of <paramref name="rowHeight"/> and the tile gone.</summary>
    private static void PressShowTile(ScopeViewModel scope, ScopeHost host, Window window, Control tile, double rowHeight)
    {
        Point at = tile.TranslatePoint(new Point(tile.Bounds.Width / 2, tile.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Settle(window);

        Assert.False(scope.IsBarHidden);
        Assert.Equal(rowHeight, BarRow(host).Height.Value);
        Assert.True(Slot(host, "BarSlot").IsVisible);
        Assert.Null(Slot(host, "ShowSlot").Content);
    }

    private static bool HitsScopeView(Window window, ScopeHost host, Point point)
    {
        Point inWindow = host.TranslatePoint(point, window)!.Value;
        return (window.InputHitTest(inWindow) is Visual hit) && ((hit == host.ScopeView) || hit.GetVisualAncestors().Contains(host.ScopeView));
    }

    private static RowDefinition BarRow(ScopeHost host) => host.FindControl<Grid>("HostGrid")!.RowDefinitions[0];

    private static ContentControl Slot(ScopeHost host, string name) => host.FindControl<ContentControl>(name)!;

    /// <summary>Lays out and renders the window, as hit testing reads the rendered scene.</summary>
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

    private static void Close(Window window, ScopeViewModel scope)
    {
        window.Close();
        scope.Dispose();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ScopeWindow_ShowsTheScopeThroughAHost()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeTestFixture.OpenScopeAsync();
        var window = new ScopeWindow(main.Preferences, scope);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            ScopeHost host = Assert.Single(window.GetVisualDescendants().OfType<ScopeHost>());
            Assert.Same(window.ScopeHost, host);
            Assert.Same(scope, host.DataContext);
            Assert.Single(window.GetVisualDescendants().OfType<ScopeView>());
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
