using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Dock;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope window's views dock: a 360 px column right of the scope holding the docked views' cards (the SSA, then
/// LA/CA/MCI), hidden when no open view is docked. The SSA opens floating on the scope, so most tests dock it first. Each
/// view opens, closes and docks through <c>.view</c>.
/// </summary>
public class ScopeDockTests
{
    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeWindow Window)> OpenAsync()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeTestFixture.OpenScopeAsync();
        var window = new ScopeWindow(main.Preferences, scope);
        window.Show();
        Settle(window);
        return (main, scope, window);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static ScopeDock DockOf(Window window) => window.GetVisualDescendants().OfType<ScopeDock>().Single();

    private static ScopeCanvas CanvasOf(Window window) => window.GetVisualDescendants().OfType<ScopeCanvas>().Single();

    private static ScopeViewCard[] Cards(Window window) =>
        [.. window.GetVisualDescendants().OfType<ScopeViewCard>().Where(c => c.IsEffectivelyVisible)];

    private static ScopeViewCard CardOf(Window window, string name) => Cards(window).Single(c => c.ViewName == name);

    private static double ContentWidth(Window window) => ((Control)window.Content!).Bounds.Width;

    internal static string[] Texts(Control control) =>
        [.. control.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "")];

    [AvaloniaFact]
    public async Task TheWindow_Opens1360Wide_WithTheDockHidden()
    {
        (_, _, ScopeWindow window) = await OpenAsync();
        try
        {
            // The SSA floats on the scope by default (ScopeViewTearoffTests), so no view is docked.
            Assert.Equal(1360, window.Width);
            Assert.False(DockOf(window).IsEffectivelyVisible);
            Assert.Equal(ContentWidth(window), CanvasOf(window).Bounds.Width);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ClosingTheLastView_HidesTheDock_AndKeepsTheScale()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunViewCommand(".view ssa dock");
            Settle(window);
            ScopeCanvas canvas = CanvasOf(window);
            double zoom = canvas.Viewport.Zoom;

            main.RunViewCommand(".view ssa off");
            Settle(window);

            Assert.False(DockOf(window).IsEffectivelyVisible);
            Assert.Equal(ContentWidth(window), canvas.Bounds.Width);
            Assert.Equal(zoom, canvas.Viewport.Zoom);

            main.RunViewCommand(".view ssa on");
            Settle(window);

            Assert.True(DockOf(window).IsEffectivelyVisible);
            Assert.Equal(ContentWidth(window), canvas.Bounds.Width + 360);
            Assert.Equal(zoom, canvas.Viewport.Zoom);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheConflictCard_SitsBelowTheSsa_AndListsThePairs()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunViewCommand(".view ssa dock");
            Settle(window);
            Assert.False(main.Preferences.ShowConflictAlerts);
            main.RunViewCommand(".view ca on");
            Settle(window);

            Assert.Equal([ScopeToolbarButtons.ViewSsaName, ScopeToolbarButtons.ViewCaName], Cards(window).Select(c => c.ViewName));
            ScopeViewCard ssa = CardOf(window, ScopeToolbarButtons.ViewSsaName);
            ScopeViewCard ca = CardOf(window, ScopeToolbarButtons.ViewCaName);
            Assert.True(ca.TranslatePoint(new Point(0, 0), window)!.Value.Y >= ssa.TranslatePoint(new Point(0, ssa.Bounds.Height), window)!.Value.Y);
            Assert.Contains("LA/CA/MCI", Texts(ca));

            // The list shows the pairs whatever the conflict-alert preference says: an opened view is shown.
            main.ApplyConflictAlerts([new ConflictAlertDto("CA_AAL22_UAL1", "UAL1", "AAL22")]);
            Settle(window);
            Assert.Contains("AAL22*UAL1        CA", Texts(ca));

            main.ApplyConflictAlerts([]);
            Settle(window);
            Assert.DoesNotContain("AAL22*UAL1        CA", Texts(ca));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheCardsCloseButton_ClosesItsView_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunViewCommand(".view ssa dock");
            Settle(window);
            main.RunViewCommand(".view ca on");
            Settle(window);

            Button close = CardOf(window, ScopeToolbarButtons.ViewCaName).GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible);
            Assert.Equal("–", close.Content);
            // A real click: the button runs its bound command, which a raised Click event alone does not.
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Point centre = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
            Settle(window);

            Assert.False(scope.IsViewOpen(ScopeToolbarButtons.ViewCaName));
            Assert.Equal(".view ca off", main.StatusText);
            Assert.Equal([ScopeToolbarButtons.ViewSsaName], Cards(window).Select(c => c.ViewName));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheViewsTray_TogglesKeepItOpen_AndDrawTheOpenViewActive()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunViewCommand(".view ssa dock");
            Settle(window);
            ScopeToolbarTests.Click(ScopeToolbarTests.ButtonOf(window, ScopeButtonId.Views));
            Assert.Contains("active", ScopeToolbarTests.ButtonOf(window, ScopeButtonId.ViewSsa).Classes);
            Assert.DoesNotContain("active", ScopeToolbarTests.ButtonOf(window, ScopeButtonId.ViewCa).Classes);

            ScopeToolbarTests.Click(ScopeToolbarTests.ButtonOf(window, ScopeButtonId.ViewCa));

            Assert.True(scope.IsViewOpen(ScopeToolbarButtons.ViewCaName));
            Assert.Equal(".view ca on", main.StatusText);
            Assert.Equal(ScopeButtonId.Views, scope.OpenMenu);
            Assert.Contains("active", ScopeToolbarTests.ButtonOf(window, ScopeButtonId.ViewCa).Classes);

            ScopeToolbarTests.Click(ScopeToolbarTests.ButtonOf(window, ScopeButtonId.ViewSsa));

            Assert.False(scope.IsViewOpen(ScopeToolbarButtons.ViewSsaName));
            Assert.Equal(".view ssa off", main.StatusText);
            Assert.Equal(ScopeButtonId.Views, scope.OpenMenu);
            Assert.DoesNotContain("active", ScopeToolbarTests.ButtonOf(window, ScopeButtonId.ViewSsa).Classes);

            // Pressing VIEWS again closes the tray.
            ScopeToolbarTests.Click(ScopeToolbarTests.ButtonOf(window, ScopeButtonId.Views));
            Assert.Null(scope.OpenMenu);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheSsaClock_RunsOnlyWhileTheSsaIsOpen_AndStopsWithTheWindow()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            Assert.True(scope.IsClockRunning);

            main.RunViewCommand(".view ssa off");
            Assert.False(scope.IsClockRunning);

            main.RunViewCommand(".view ssa on");
            Assert.True(scope.IsClockRunning);
        }
        finally
        {
            window.Close();
        }

        Dispatcher.UIThread.RunJobs();
        Assert.False(scope.IsClockRunning);
    }

    [AvaloniaFact]
    public async Task ClearingTheScenario_EmptiesTheConflictCard()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunViewCommand(".view ca on");
            main.ApplyConflictAlerts([new ConflictAlertDto("CA_AAL22_UAL1", "UAL1", "AAL22")]);
            Settle(window);
            Assert.Contains("AAL22*UAL1        CA", Texts(CardOf(window, ScopeToolbarButtons.ViewCaName)));

            main.ClearScenarioState();
            Settle(window);

            Assert.DoesNotContain("AAL22*UAL1        CA", Texts(CardOf(window, ScopeToolbarButtons.ViewCaName)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AClosedConflictView_StopsListeningForPairs()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunViewCommand(".view ca on");
            ConflictListViewModel list = scope.DockViews.OfType<ConflictListViewModel>().Single();

            main.RunViewCommand(".view ca off");
            main.ApplyConflictAlerts([new ConflictAlertDto("CA_AAL22_UAL1", "UAL1", "AAL22")]);

            Assert.Empty(list.Rows);
        }
        finally
        {
            window.Close();
        }
    }

    // Closing the window disposes the scope, which stops every view: the open list clears its rows and stops listening.
    [AvaloniaFact]
    public async Task ClosingTheWindow_StopsTheOpenConflictView()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        main.RunViewCommand(".view ca on");
        ConflictListViewModel list = scope.DockViews.OfType<ConflictListViewModel>().Single();

        window.Close();
        Dispatcher.UIThread.RunJobs();
        main.ApplyConflictAlerts([new ConflictAlertDto("CA_AAL22_UAL1", "UAL1", "AAL22")]);

        Assert.Empty(list.Rows);
    }

    [AvaloniaFact]
    public async Task AReopenedSsa_TakesItsPlaceAboveTheConflictList()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunViewCommand(".view ssa dock");
            Settle(window);
            main.RunViewCommand(".view ssa off");
            main.RunViewCommand(".view ca on");
            main.RunViewCommand(".view ssa on");
            Settle(window);

            Assert.Equal([ScopeToolbarButtons.ViewSsaName, ScopeToolbarButtons.ViewCaName], scope.OpenViews);
            Assert.Equal([ScopeToolbarButtons.ViewSsaName, ScopeToolbarButtons.ViewCaName], Cards(window).Select(c => c.ViewName));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ApplyOpenViews_OpensTheKnownNamedViews_AndClosesTheRest()
    {
        (_, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            scope.ApplyOpenViews([ScopeToolbarButtons.ViewCaName, "bogus"]);

            Assert.Equal([ScopeToolbarButtons.ViewCaName], scope.OpenViews);
            Assert.False(scope.IsClockRunning);

            scope.ApplyOpenViews([]);

            Assert.False(scope.HasDockedViews);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheSsaCard_OutsideARoom_ShowsTheRedNaStatusAndTheRange()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunViewCommand(".view ssa dock");
            Settle(window);
            ScopeViewCard ssa = CardOf(window, ScopeToolbarButtons.ViewSsaName);

            string[] texts = Texts(ssa);
            Assert.Contains("NA/NA/NA", texts);
            Assert.Contains("FUSED 40NM", texts);
            TextBlock status = ssa.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "NA/NA/NA");
            Assert.Contains("alert", status.Classes);
        }
        finally
        {
            window.Close();
        }
    }
}
