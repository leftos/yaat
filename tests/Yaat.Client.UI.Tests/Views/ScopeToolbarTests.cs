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
                    ScopeButtonId.Auto,
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
    private static void WheelOnce(Window window, Button button, bool up)
    {
        // The wheel is routed by hit testing, which reads the rendered scene: render a freshly built tray first.
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        Point centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseWheel(centre, new Vector(0, up ? 1 : -1));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task BriteMenu_OpensATrayOfTheSevenSpinners_WithTheirValues()
    {
        (_, _, ScopeWindow window) = await OpenAsync();
        try
        {
            Assert.Equal("BRITE ▸", DrawnText(ButtonOf(window, ScopeButtonId.Brite)));

            Click(ButtonOf(window, ScopeButtonId.Brite));

            string[] expected = ["MAP A 100", "MAP B 60", "TARGETS 100", "FDB 100", "LINE 4 0", "ALERTS 100", "DWELL 20"];
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
}
