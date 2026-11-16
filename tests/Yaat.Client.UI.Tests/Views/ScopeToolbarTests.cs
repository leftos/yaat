using System.Text.Json.Nodes;
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
using Yaat.Client.Logging;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope window's toolbar: one row of buttons from the button model, a menu's children in a tray under the bar, and
/// every action run as a <c>.scope</c> command.
/// </summary>
public class ScopeToolbarTests
{
    private static Task<(MainViewModel Main, ScopeViewModel Scope, ScopeWindow Window)> OpenAsync(Size? size = null) =>
        OpenScopeAsync(size, _ => { });

    /// <summary>Opens the Scope at <paramref name="size"/> after <paramref name="beforeScope"/> has set the radar up.</summary>
    private static Task<(MainViewModel Main, ScopeViewModel Scope, ScopeWindow Window)> OpenWithRadarAsync(
        Size size,
        Action<MainViewModel> beforeScope
    ) => OpenScopeAsync(size, beforeScope);

    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeWindow Window)> OpenScopeAsync(
        Size? size,
        Action<MainViewModel> beforeScope
    )
    {
        var main = new MainViewModel(new FakeFilePickerService());
        await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        beforeScope(main);
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null);
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

    /// <summary>The bar's sideways scroller.</summary>
    private static ScrollViewer Scroller(Window window) => window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "MainScroll");

    /// <summary>The bar's chevron button named <paramref name="name"/>.</summary>
    private static Button Chevron(Window window, string name) => window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    /// <summary>Lays out and renders the window, as hit testing reads the rendered scene.</summary>
    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Presses <paramref name="chevron"/> until it disables itself, at most <paramref name="most"/> times.</summary>
    private static void PressUntilDisabled(Button chevron, int most)
    {
        for (int i = 0; (i < most) && chevron.IsEnabled; i++)
        {
            Click(chevron);
        }
    }

    /// <summary>Turns the wheel once over <paramref name="control"/>, by <paramref name="delta"/>.</summary>
    private static void WheelOver(Window window, Control control, Vector delta)
    {
        // The wheel is routed by hit testing, which reads the rendered scene: render the current layout first.
        Settle(window);
        Point centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        WheelAt(window, centre, delta, RawInputModifiers.None);
    }

    /// <summary>Turns the wheel once at <paramref name="point"/> (window coordinates), by <paramref name="delta"/>.</summary>
    private static void WheelAt(Window window, Point point, Vector delta, RawInputModifiers modifiers)
    {
        window.MouseWheel(point, delta, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

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
    public async Task ProfileMenu_ShowsTheProfile_AndPickingEramSwitchesAndLightsIt()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            Button profile = ButtonOf(window, ScopeButtonId.Profile);
            Assert.Equal("PROFILE MIXED", Label(profile));
            Assert.Equal("MIXED", ((TextBlock)profile.Content!).Inlines!.OfType<Run>().Last().Text);
            Assert.Equal("MIXED", scope.SpinnerValue(ScopeToolbarButtons.Find(ScopeButtonId.Profile)!));

            Click(profile);
            Assert.Equal(["STARS", "ERAM", "MIXED"], TrayButtons(window).Select(DrawnText));
            Assert.Contains("active", ButtonOf(window, ScopeButtonId.ProfileMixed).Classes);

            Click(ButtonOf(window, ScopeButtonId.ProfileEram));

            Assert.Equal(ScopeProfileKind.Eram, main.ScopeProfile);
            Assert.Equal("Scope profile ERAM", main.StatusText);
            Assert.Empty(TrayButtons(window));
            Assert.Equal("ERAM", ((TextBlock)ButtonOf(window, ScopeButtonId.Profile).Content!).Inlines!.OfType<Run>().Last().Text);
            Click(ButtonOf(window, ScopeButtonId.Profile));
            Assert.Contains("active", ButtonOf(window, ScopeButtonId.ProfileEram).Classes);
            Assert.DoesNotContain("active", ButtonOf(window, ScopeButtonId.ProfileMixed).Classes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheBar_FollowsTheProfilesToolbarOrder_WithEveryOtherButtonAfter()
    {
        string root = Path.Combine(Path.GetTempPath(), "yaat-scope-toolbar-profile-tests", Guid.NewGuid().ToString("N"));
        var store = new ScopeProfileStore(root, AppLog.CreateLogger<ScopeProfileStore>());
        store.Get(ScopeProfileKind.Mixed);
        string mixedFile = Path.Combine(root, "mixed.json");
        JsonObject edited = JsonNode.Parse(File.ReadAllText(mixedFile))!.AsObject();
        edited["toolbar"] = new JsonArray("maps", "range", "wx");
        File.WriteAllText(mixedFile, edited.ToJsonString());
        var main = new MainViewModel(new FakeFilePickerService());
        await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: store);
        main.OpenScope = scope;
        var window = new ScopeWindow(main.Preferences, scope);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            ScopeButtonId[] bar =
            [
                .. window
                    .GetVisualDescendants()
                    .OfType<Button>()
                    .Where(b => b.IsEffectivelyVisible && (b.Tag is ScopeButtonId))
                    .Select(b => (ScopeButtonId)b.Tag!),
            ];

            Assert.Equal(
                [
                    ScopeButtonId.Maps,
                    ScopeButtonId.Range,
                    ScopeButtonId.Profile,
                    ScopeButtonId.Template,
                    ScopeButtonId.Others,
                    ScopeButtonId.Vector,
                    ScopeButtonId.Hist,
                    ScopeButtonId.Tools,
                    ScopeButtonId.Auto,
                    ScopeButtonId.Tdm,
                    ScopeButtonId.Db,
                    ScopeButtonId.Views,
                    ScopeButtonId.Brite,
                    ScopeButtonId.Pref,
                    ScopeButtonId.DeleteTearoff,
                ],
                bar
            );
        }
        finally
        {
            window.Close();
            Directory.Delete(root, recursive: true);
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
            // Halfway across the scope, left of the views dock.
            Point belowTray = tray.TranslatePoint(new Point(canvas.Bounds.Width / 2, tray.Bounds.Height + 10), window)!.Value;
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

    /// <summary>Turns the wheel once over <paramref name="button"/>, up when <paramref name="up"/>.</summary>
    private static void WheelOnce(Window window, Button button, bool up) => WheelOver(window, button, new Vector(0, up ? 1 : -1));

    [AvaloniaFact]
    public async Task BriteMenu_OpensATrayOfTheTenSpinners_WithTheirValues()
    {
        (_, _, ScopeWindow window) = await OpenAsync();
        try
        {
            Assert.Equal("BRITE ▸", DrawnText(ButtonOf(window, ScopeButtonId.Brite)));

            Click(ButtonOf(window, ScopeButtonId.Brite));

            string[] expected =
            [
                "MAP A 100",
                "MAP B 60",
                "TARGETS 100",
                "FDB 100",
                "LINE 4 0",
                "ALERTS 100",
                "DWELL 20",
                "HIST 100",
                "VECTOR 100",
                "RINGS 100",
            ];
            Assert.Equal(expected, TrayButtons(window).Select(DrawnText));
            Assert.Equal(expected, TrayButtons(window).Select(Label));
            Run value = ((TextBlock)ButtonOf(window, ScopeButtonId.BriteMapB).Content!).Inlines!.OfType<Run>().Last();
            Assert.Equal("60", value.Text);
            Assert.Equal(FontWeight.Bold, value.FontWeight);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task BriteSpinner_WheelStepsItsGroup_AndRunsTheCommand()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            Click(ButtonOf(window, ScopeButtonId.Brite));

            WheelOnce(window, ButtonOf(window, ScopeButtonId.BriteMapB), up: true);

            Assert.Equal(65, scope.Brightness.MapB);
            Assert.Equal(".scope brite mapb 65", main.StatusText);
            Assert.Equal("MAP B 65", DrawnText(ButtonOf(window, ScopeButtonId.BriteMapB)));

            WheelOnce(window, ButtonOf(window, ScopeButtonId.BriteDwell), up: false);

            Assert.Equal(19, scope.Brightness.Dwell);
            Assert.Equal(".scope brite dwell 19", main.StatusText);
            Assert.Equal("DWELL 19", DrawnText(ButtonOf(window, ScopeButtonId.BriteDwell)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task BriteSpinner_AtItsLimit_RunsNothing()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            Click(ButtonOf(window, ScopeButtonId.Brite));

            // The wheel reaches FDB: down a step and back up.
            WheelOnce(window, ButtonOf(window, ScopeButtonId.BriteFdb), up: false);
            Assert.Equal(95, scope.Brightness.Fdb);
            Assert.Equal(".scope brite fdb 95", main.StatusText);
            WheelOnce(window, ButtonOf(window, ScopeButtonId.BriteFdb), up: true);
            Assert.Equal(100, scope.Brightness.Fdb);
            Assert.Equal(".scope brite fdb 100", main.StatusText);
            main.StatusText = "";

            WheelOnce(window, ButtonOf(window, ScopeButtonId.BriteFdb), up: true);

            Assert.Equal(100, scope.Brightness.Fdb);
            Assert.Equal("", main.StatusText);
            Assert.Equal("FDB 100", DrawnText(ButtonOf(window, ScopeButtonId.BriteFdb)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task BriteSpinner_OffTheStepGrid_ClampsToItsLimit()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunScopeCommand(".scope brite fdb 97");
            Click(ButtonOf(window, ScopeButtonId.Brite));

            WheelOnce(window, ButtonOf(window, ScopeButtonId.BriteFdb), up: true);

            Assert.Equal(100, scope.Brightness.Fdb);
            Assert.Equal(".scope brite fdb 100", main.StatusText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task BriteSpinners_EachKeepTheirOwnPartWheelStep()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            main.Preferences.SetScrollSensitivity(0.5);
            Click(ButtonOf(window, ScopeButtonId.Brite));

            // Same direction on both, so a shared accumulator would complete the step on TARGETS.
            WheelOnce(window, ButtonOf(window, ScopeButtonId.BriteMapB), up: false);
            WheelOnce(window, ButtonOf(window, ScopeButtonId.BriteTargets), up: false);

            Assert.Equal(ScopeBrightness.Default, scope.Brightness);

            WheelOnce(window, ButtonOf(window, ScopeButtonId.BriteTargets), up: false);

            Assert.Equal(ScopeBrightness.Default with { Targets = 95 }, scope.Brightness);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task BriteCommand_ReachesTheCanvas_WithoutDroppingItsFrame()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenAsync();
        try
        {
            ScopeCanvas canvas = window.GetVisualDescendants().OfType<ScopeCanvas>().Single();
            ScopeFrame frame = canvas.CurrentFrame;

            // No RunJobs here: a render builds a fresh frame by design, so only the change handler may touch the kept one.
            main.RunScopeCommand(".scope brite fdb 50");

            Assert.Equal(50, canvas.Brightness.Fdb);
            Assert.Same(frame, canvas.CurrentFrame);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Gives the radar a centre and one map toggle per STARS id, in the order given, every map off.</summary>
    private static Action<MainViewModel> RadarWithToggles(params int[] starsIds) =>
        main =>
        {
            RadarMapToggles.Add(main.Radar, starsIds);
            main.Radar.CenterLat = 37.62;
            main.Radar.CenterLon = -122.38;
        };

    /// <summary>The shown tray button labelled <paramref name="label"/>; map toggles share one id, so they are found by label.</summary>
    private static Button TrayButton(Window window, string label) => TrayButtons(window).Single(b => Label(b) == label);

    [AvaloniaFact]
    public async Task MapsTray_ShowsClearAllThenTheToggles_AndAToggleClickTurnsItsMapOn()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenWithRadarAsync(new Size(900, 600), RadarWithToggles(31, 2, 10));
        try
        {
            Assert.Equal("MAPS ▸", DrawnText(ButtonOf(window, ScopeButtonId.Maps)));

            Click(ButtonOf(window, ScopeButtonId.Maps));

            Assert.Equal(["CLR ALL", "2 MAP2", "10 MAP10", "31 MAP31"], TrayButtons(window).Select(DrawnText));
            Assert.DoesNotContain(TrayButtons(window), b => b.Classes.Contains("active"));

            Click(TrayButton(window, "10 MAP10"));

            Assert.Equal(".scope map 10 on", main.StatusText);
            Assert.True(scope.MapToggles.Single(t => t.StarsId == 10).IsOn);
            Assert.Contains("active", TrayButton(window, "10 MAP10").Classes);
            Assert.DoesNotContain("active", TrayButton(window, "2 MAP2").Classes);
            Assert.Equal(ScopeButtonId.Maps, scope.OpenMenu);

            Click(TrayButton(window, "10 MAP10"));
            Assert.Equal(".scope map 10 off", main.StatusText);
            Assert.False(scope.MapToggles.Single(t => t.StarsId == 10).IsOn);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task MapsTray_ClearAll_TurnsEveryMapOff()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenWithRadarAsync(new Size(900, 600), RadarWithToggles(2, 10));
        try
        {
            main.RunScopeCommand(".scope map 2 on");
            main.RunScopeCommand(".scope map 10 on");
            Click(ButtonOf(window, ScopeButtonId.Maps));

            Click(ButtonOf(window, ScopeButtonId.MapClear));

            Assert.Equal(".scope map clear", main.StatusText);
            Assert.All(scope.MapToggles, t => Assert.False(t.IsOn));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DbTray_ShowsItsTogglesActive_AndTheCharSize()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            Assert.Equal("DB ▸", DrawnText(ButtonOf(window, ScopeButtonId.Db)));

            Click(ButtonOf(window, ScopeButtonId.Db));

            string charLabel = $"CHAR {main.Preferences.RadarDatablockFontSize}";
            Assert.Equal(["4TH LINE", "DWELL", "ATPA DIST", charLabel], TrayButtons(window).Select(DrawnText));
            Assert.Contains("active", ButtonOf(window, ScopeButtonId.Db4thLine).Classes);
            Assert.Contains("active", ButtonOf(window, ScopeButtonId.DbDwell).Classes);
            Assert.Equal(main.Preferences.ShowAtpa, ButtonOf(window, ScopeButtonId.DbAtpa).Classes.Contains("active"));

            Click(ButtonOf(window, ScopeButtonId.Db4thLine));

            Assert.Equal(".scope db 4thline off", main.StatusText);
            Assert.False(scope.DbSettings.Line4);
            Assert.DoesNotContain("active", ButtonOf(window, ScopeButtonId.Db4thLine).Classes);
            Assert.Equal(ScopeButtonId.Db, scope.OpenMenu);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DbChar_WheelStepsByOne_AndClampsAtItsLimits()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunScopeCommand(".scope db char 23");
            Click(ButtonOf(window, ScopeButtonId.Db));

            WheelOnce(window, ButtonOf(window, ScopeButtonId.DbChar), up: true);
            Assert.Equal(24, scope.DbSettings.CharSize);
            Assert.Equal(".scope db char 24", main.StatusText);
            Assert.Equal("CHAR 24", DrawnText(ButtonOf(window, ScopeButtonId.DbChar)));

            main.StatusText = "";
            WheelOnce(window, ButtonOf(window, ScopeButtonId.DbChar), up: true);
            Assert.Equal(24, scope.DbSettings.CharSize);
            Assert.Equal("", main.StatusText);

            main.RunScopeCommand(".scope db char 8");
            WheelOnce(window, ButtonOf(window, ScopeButtonId.DbChar), up: false);
            Assert.Equal(8, scope.DbSettings.CharSize);
            WheelOnce(window, ButtonOf(window, ScopeButtonId.DbChar), up: true);
            Assert.Equal(9, scope.DbSettings.CharSize);
            Assert.Equal(".scope db char 9", main.StatusText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ATrayWiderThanTheWindow_WrapsOntoMoreRows_AndNeverResizesTheScope()
    {
        int[] ids = [.. Enumerable.Range(1, 30)];
        (_, _, ScopeWindow window) = await OpenWithRadarAsync(new Size(600, 800), RadarWithToggles(ids));
        try
        {
            ScopeCanvas canvas = window.GetVisualDescendants().OfType<ScopeCanvas>().Single();
            Rect bounds = canvas.Bounds;

            Click(ButtonOf(window, ScopeButtonId.Maps));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Border tray = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Tray");
            Button[] buttons = TrayButtons(window);
            Assert.Equal(31, buttons.Length);
            double[] rows = [.. buttons.Select(b => b.TranslatePoint(default, window)!.Value.Y).Distinct()];
            Assert.True(rows.Length > 1, "the tray's buttons wrap onto a second row");
            Assert.All(buttons, b => Assert.True(b.TranslatePoint(new Point(b.Bounds.Width, 0), tray)!.Value.X <= tray.Bounds.Width));
            Assert.True(tray.Bounds.Height > 45, "the tray grows to hold its rows");
            Assert.Equal(bounds, canvas.Bounds);
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

    [AvaloniaFact]
    public async Task ANarrowWindow_ShowsBothChevrons_AndScrollsDelTearoffIntoView()
    {
        (_, _, ScopeWindow window) = await OpenAsync(new Size(1000, 800));
        try
        {
            ScrollViewer scroller = Scroller(window);
            Button left = Chevron(window, "ScrollLeft");
            Button right = Chevron(window, "ScrollRight");
            Assert.True(left.IsEffectivelyVisible, "the left chevron shows while the bar is wider than the window");
            Assert.True(right.IsEffectivelyVisible, "the right chevron shows while the bar is wider than the window");
            Assert.False(left.IsEnabled, "the left chevron is disabled at the bar's left end");
            Assert.True(right.IsEnabled, "the right chevron is enabled at the bar's left end");

            Button del = ButtonOf(window, ScopeButtonId.DeleteTearoff);
            Point delEnd = del.TranslatePoint(new Point(del.Bounds.Width, 0), scroller)!.Value;
            Assert.True(delEnd.X > scroller.Viewport.Width, "DEL TEAROFF starts past the bar's right edge");

            PressUntilDisabled(right, 5);

            Assert.False(right.IsEnabled, "the right chevron disables at the bar's right end");
            Point delLeft = del.TranslatePoint(default, scroller)!.Value;
            Assert.InRange(delLeft.X, 0, scroller.Viewport.Width - del.Bounds.Width);
            Assert.True(left.IsEnabled, "the left chevron enables once the bar is scrolled");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AWideWindow_ShowsNoChevrons()
    {
        (_, _, ScopeWindow window) = await OpenAsync(new Size(2000, 800));
        try
        {
            Assert.False(Chevron(window, "ScrollLeft").IsEffectivelyVisible, "the bar fits, so there is no left chevron");
            Assert.False(Chevron(window, "ScrollRight").IsEffectivelyVisible, "the bar fits, so there is no right chevron");
            Assert.Equal(0d, Scroller(window).Offset.X);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheDefaultWindow_ShowsTheChevrons()
    {
        (_, _, ScopeWindow window) = await OpenAsync();
        try
        {
            Assert.True(Chevron(window, "ScrollLeft").IsEffectivelyVisible, "the bar with TDM is wider than the default window");
            Assert.True(Chevron(window, "ScrollRight").IsEffectivelyVisible, "the bar with TDM is wider than the default window");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheWheel_OverANonSpinnerButton_DoesNotScrollTheBar()
    {
        (_, _, ScopeWindow window) = await OpenAsync(new Size(1000, 800));
        try
        {
            ScrollViewer scroller = Scroller(window);
            PressUntilDisabled(Chevron(window, "ScrollRight"), 5);
            double offset = scroller.Offset.X;
            Assert.True(offset > 0, "the bar scrolled to its right end");

            // The wheel up and the wheel right would both walk the bar back if the scroller read them.
            WheelOver(window, ButtonOf(window, ScopeButtonId.Auto), new Vector(0, 1));
            Assert.Equal(offset, scroller.Offset.X);
            WheelOver(window, ButtonOf(window, ScopeButtonId.Auto), new Vector(1, 0));
            Assert.Equal(offset, scroller.Offset.X);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheWheel_OverAGapBetweenButtons_DoesNotScrollTheBar()
    {
        (_, _, ScopeWindow window) = await OpenAsync(new Size(1000, 800));
        try
        {
            ScrollViewer scroller = Scroller(window);
            PressUntilDisabled(Chevron(window, "ScrollRight"), 5);
            double offset = scroller.Offset.X;
            Assert.True(offset > 0, "the bar scrolled to its right end");
            Settle(window);

            Button auto = ButtonOf(window, ScopeButtonId.Auto);
            Button maps = ButtonOf(window, ScopeButtonId.Maps);
            Point autoEnd = auto.TranslatePoint(new Point(auto.Bounds.Width, auto.Bounds.Height / 2), window)!.Value;
            Point mapsStart = maps.TranslatePoint(new Point(0, maps.Bounds.Height / 2), window)!.Value;
            var between = new Point((autoEnd.X + mapsStart.X) / 2, autoEnd.Y);
            Point below = auto.TranslatePoint(new Point(auto.Bounds.Width / 2, auto.Bounds.Height + 3), window)!.Value;

            WheelAt(window, between, new Vector(0, 1), RawInputModifiers.Shift);
            Assert.Equal(offset, scroller.Offset.X);
            WheelAt(window, between, new Vector(1, 0), RawInputModifiers.None);
            Assert.Equal(offset, scroller.Offset.X);
            WheelAt(window, below, new Vector(0, 1), RawInputModifiers.Shift);
            Assert.Equal(offset, scroller.Offset.X);
            WheelAt(window, below, new Vector(1, 0), RawInputModifiers.None);
            Assert.Equal(offset, scroller.Offset.X);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Widening_UntilTheBarFits_HidesTheChevrons_AndResetsTheOffset()
    {
        (_, _, ScopeWindow window) = await OpenAsync(new Size(1000, 800));
        try
        {
            ScrollViewer scroller = Scroller(window);
            PressUntilDisabled(Chevron(window, "ScrollRight"), 5);
            Assert.True(scroller.Offset.X > 0, "the bar scrolled to its right end");
            double extent = scroller.Extent.Width;

            // The bar's row is the window less the bar's 12 DIPs of padding.
            window.Width = extent + 32;
            Settle(window);

            Grid row = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "BarRow");
            string measured = $"bar {extent}, row {row.Bounds.Width}, window {window.Width}";
            Assert.False(Chevron(window, "ScrollLeft").IsEffectivelyVisible, $"the bar fits, so there is no left chevron ({measured})");
            Assert.False(Chevron(window, "ScrollRight").IsEffectivelyVisible, $"the bar fits, so there is no right chevron ({measured})");
            Assert.Equal(0d, scroller.Offset.X);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheWheel_OverRange_StillStepsIt()
    {
        (_, ScopeViewModel scope, ScopeWindow window) = await OpenAsync(new Size(1000, 800));
        try
        {
            ScrollViewer scroller = Scroller(window);
            double before = scope.RangeNm;

            WheelOver(window, ButtonOf(window, ScopeButtonId.Range), new Vector(0, 1));

            Assert.Equal(before - 1, scope.RangeNm);
            Assert.Equal(0d, scroller.Offset.X);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ATearoff_FromAScrolledBar_GrabsTheButtonUnderThePointer()
    {
        (_, ScopeViewModel scope, ScopeWindow window) = await OpenAsync(new Size(1000, 800));
        try
        {
            ScrollViewer scroller = Scroller(window);
            PressUntilDisabled(Chevron(window, "ScrollRight"), 5);
            double offset = scroller.Offset.X;
            Assert.True(offset > 0, "the bar scrolled to its right end");
            Settle(window);

            ScopeButtonControl brite = window
                .GetVisualDescendants()
                .OfType<ScopeButtonControl>()
                .Single(c => !c.IsTearoff && (c.Model.Id == ScopeButtonId.Brite) && c.IsEffectivelyVisible);
            Border strip = brite.Strip!;
            var stripCentre = new Point(strip.Bounds.Width / 2, strip.Bounds.Height / 2);
            Point grab = strip.TranslatePoint(stripCentre, brite)!.Value;
            Point press = strip.TranslatePoint(stripCentre, window)!.Value;

            window.MouseMove(press);
            window.MouseDown(press, MouseButton.Left);
            window.MouseUp(press, MouseButton.Left);
            Settle(window);

            ScopeTearoffLayer layer = window.GetVisualDescendants().OfType<ScopeTearoffLayer>().Single();
            ScopeButtonControl follower = layer.Children.OfType<ScopeButtonControl>().Single(c => !c.IsHitTestVisible && c.IsTearoff);
            Point at = follower.TranslatePoint(default, window)!.Value;
            Assert.Equal(press.X - grab.X, at.X, 1);
            Assert.Equal(press.Y - grab.Y, at.Y, 1);
            Assert.Equal(offset, scroller.Offset.X);

            Point drop = layer.TranslatePoint(new Point(600, 400), window)!.Value;
            window.MouseMove(drop);
            window.MouseDown(drop, MouseButton.Left);
            window.MouseUp(drop, MouseButton.Left);
            Settle(window);

            ScopeTearoff tearoff = Assert.Single(scope.Tearoffs);
            Assert.Equal(new ScopeButtonKey(ScopeButtonId.Brite, null), tearoff.Key);
        }
        finally
        {
            window.Close();
        }
    }
}
