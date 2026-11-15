using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope window's toolbar: one row of buttons from the button model, a menu's children in a tray under the bar, and
/// every action run as a <c>.scope</c> command.
/// </summary>
public class ScopeToolbarTests
{
    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeWindow Window)> OpenAsync(Size? size = null)
    {
        var main = new MainViewModel(new FakeFilePickerService());
        await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var scope = new ScopeViewModel(main);
        main.OpenScope = scope;
        var window = new ScopeWindow(main.Preferences, scope);
        if (size is { } s)
        {
            window.Width = s.Width;
            window.Height = s.Height;
        }

        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (main, scope, window);
    }

    /// <summary>The text <paramref name="button"/> draws: its runs, joined.</summary>
    private static string DrawnText(Button button) => string.Concat(((TextBlock)button.Content!).Inlines!.OfType<Run>().Select(r => r.Text));

    /// <summary>Whether a pointer at <paramref name="point"/> (window coordinates) lands on the scope canvas.</summary>
    private static bool HitsCanvas(Window window, ScopeCanvas canvas, Point point)
    {
        // Hit testing reads the rendered scene, so render the current layout first.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window.InputHitTest(point) is Visual hit && ((hit == canvas) || hit.GetVisualAncestors().Contains(canvas));
    }

    /// <summary>The shown toolbar button with <paramref name="id"/>.</summary>
    internal static Button ButtonOf(Window window, ScopeButtonId id) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => (b.Tag is ScopeButtonId tag) && (tag == id) && b.IsEffectivelyVisible);

    /// <summary>The toolbar buttons shown in the tray.</summary>
    private static Button[] TrayButtons(Window window) =>
        [
            .. window
                .GetVisualDescendants()
                .OfType<Button>()
                .Where(b => (b.Tag is ScopeButtonId) && b.IsEffectivelyVisible && b.GetVisualAncestors().OfType<Border>().Any(a => a.Name == "Tray")),
        ];

    internal static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static string Label(Button button) => AutomationProperties.GetName(button) ?? "";

    [AvaloniaFact]
    public async Task TheBar_ReplacesTheMenu_AndShowsTemplateAndRange()
    {
        (_, _, ScopeWindow window) = await OpenAsync();
        try
        {
            Assert.Empty(window.GetLogicalDescendants().OfType<Menu>());
            Button template = ButtonOf(window, ScopeButtonId.Template);
            Button range = ButtonOf(window, ScopeButtonId.Range);
            Assert.Equal("TEMPLATE ▸", DrawnText(template));
            Assert.Equal("RANGE 40", DrawnText(range));
            Run value = ((TextBlock)range.Content!).Inlines!.OfType<Run>().Last();
            Assert.Equal("40", value.Text);
            Assert.Equal(FontWeight.Bold, value.FontWeight);
            Assert.Equal("TEMPLATE", Label(template));
            Assert.Equal("RANGE 40", Label(range));
            Assert.Empty(TrayButtons(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheTray_OverlaysTheScope_AndNeverResizesIt()
    {
        (_, ScopeViewModel scope, ScopeWindow window) = await OpenAsync(new Size(600, 800));
        try
        {
            ScopeCanvas canvas = window.GetVisualDescendants().OfType<ScopeCanvas>().Single();
            Rect bounds = canvas.Bounds;
            double range = scope.RangeNm;

            Click(ButtonOf(window, ScopeButtonId.Template));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(bounds, canvas.Bounds);
            Assert.Equal(range, scope.RangeNm);
            Border tray = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Tray");
            Point belowTray = tray.TranslatePoint(new Point(tray.Bounds.Width / 2, tray.Bounds.Height + 10), window)!.Value;
            Assert.True(HitsCanvas(window, canvas, belowTray), "a point below the open tray reaches the canvas");

            Click(ButtonOf(window, ScopeButtonId.Template));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(bounds, canvas.Bounds);
            Assert.Equal(range, scope.RangeNm);
            Assert.True(HitsCanvas(window, canvas, belowTray), "a point below the closed tray's edge reaches the canvas");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TemplateMenu_OpensATray_PickingEramSwitchesAndClosesIt()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            Click(ButtonOf(window, ScopeButtonId.Template));

            Assert.Equal(["STARS", "ERAM", "MIXED", "EUROSCOPE"], TrayButtons(window).Select(DrawnText));
            Assert.Equal(["STARS", "ERAM", "MIXED", "EUROSCOPE"], TrayButtons(window).Select(Label));
            Assert.True(ButtonOf(window, ScopeButtonId.Range).IsEffectivelyVisible);

            Click(ButtonOf(window, ScopeButtonId.TemplateEram));

            Assert.Equal(ScopeTemplates.EramId, scope.TemplateId);
            Assert.Empty(TrayButtons(window));
            Assert.Equal(".scope template eram", main.StatusText);

            Click(ButtonOf(window, ScopeButtonId.Template));
            Assert.Contains("active", ButtonOf(window, ScopeButtonId.TemplateEram).Classes);
            Assert.DoesNotContain("active", ButtonOf(window, ScopeButtonId.TemplateStars).Classes);

            // Clicking the open menu's button again closes its tray.
            Click(ButtonOf(window, ScopeButtonId.Template));
            Assert.Empty(TrayButtons(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Range_WheelStepsByOne_AndTheLabelFollows()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            Button range = ButtonOf(window, ScopeButtonId.Range);
            Point centre = range.TranslatePoint(new Point(range.Bounds.Width / 2, range.Bounds.Height / 2), window)!.Value;

            // Wheel up zooms in, as the Radar DCB's RANGE does.
            window.MouseWheel(centre, new Vector(0, 1));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(39, scope.RangeNm);
            Assert.Equal("RANGE 39", DrawnText(range));
            Assert.Equal("RANGE 39", Label(range));
            Assert.Equal(".scope range 39", main.StatusText);

            window.MouseWheel(centre, new Vector(0, -1));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(40, scope.RangeNm);

            // The label follows the range whichever path wrote it.
            scope.RangeNm = 60.4;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("RANGE 60", DrawnText(range));
            Assert.Equal("RANGE 60", Label(range));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Range_Click_ChangesNothing()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            main.StatusText = "";

            Click(ButtonOf(window, ScopeButtonId.Range));

            Assert.Equal(ScopeViewModel.DefaultRangeNm, scope.RangeNm);
            Assert.Equal("", main.StatusText);
            Assert.Empty(TrayButtons(window));
        }
        finally
        {
            window.Close();
        }
    }
}
