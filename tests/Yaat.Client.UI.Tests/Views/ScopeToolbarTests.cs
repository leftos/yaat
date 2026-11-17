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
using Yaat.Client.Services;
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
    private static Task<(MainViewModel Main, ScopeViewModel Scope, ScopeWindow Window)> OpenWithSourceAsync(
        Size size,
        Action<MainViewModel> beforeScope
    ) => OpenScopeAsync(size, beforeScope);

    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeWindow Window)> OpenScopeAsync(
        Size? size,
        Action<MainViewModel> beforeScope
    )
    {
        MainViewModel main = await ScopeTestFixture.MainAsync();
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

    /// <summary>
    /// The text <paramref name="button"/> draws, read as one line: each line's runs joined, the lines joined by a space,
    /// and " ▸" after the first line when its tile shows the menu mark.
    /// </summary>
    internal static string DrawnText(Button button)
    {
        List<string> lines = [""];
        foreach (Inline inline in ((TextBlock)button.Content!).Inlines!)
        {
            if (inline is LineBreak)
            {
                lines.Add("");
            }
            else if (inline is Run run)
            {
                lines[^1] += run.Text;
            }
        }

        if (button.Parent is ScopeButtonControl { Model.Kind: ScopeButtonKind.Menu })
        {
            lines[0] += " ▸";
        }

        return string.Join(" ", lines);
    }

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
        MainViewModel main = await ScopeTestFixture.MainAsync();
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
                    ScopeButtonId.Mva,
                    ScopeButtonId.Display,
                    ScopeButtonId.Rr,
                    ScopeButtonId.Db,
                    ScopeButtonId.Views,
                    ScopeButtonId.Lens,
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
    public async Task BriteMenu_OpensATrayOfTheElevenSpinners_WithTheirValues()
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
                "LENS 100",
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

    /// <summary>Loads the Scope's map source with one map per STARS id, in the order given, centred at 37.62/-122.38.</summary>
    private static Action<MainViewModel> SourceWithToggles(params int[] starsIds) =>
        main =>
            main.ScopeMaps.ApplyVideoMapsDtoForTesting(
                new FacilityVideoMapsDto(
                    "ZOA",
                    "NCT",
                    [new StarsAreaDto("A", "Area A", 37.62, -122.38, 60, [])],
                    [.. starsIds.Select(id => new VideoMapInfoDto($"m{id}", $"Map {id}", $"MAP{id}", [], "A", id, false, false))],
                    []
                ),
                null,
                null
            );

    /// <summary>The shown tray button labelled <paramref name="label"/>; map toggles share one id, so they are found by label.</summary>
    private static Button TrayButton(Window window, string label) => TrayButtons(window).Single(b => Label(b) == label);

    [AvaloniaFact]
    public async Task MapsTray_ShowsClearAllThenTheToggles_AndAToggleClickTurnsItsMapOn()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenWithSourceAsync(new Size(900, 600), SourceWithToggles(31, 2, 10));
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
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenWithSourceAsync(new Size(900, 600), SourceWithToggles(2, 10));
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
    public async Task LensTray_TogglesTheLensAndItsPath_AndTheMenuStaysLitWhileTheLensIsOn()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            Click(ButtonOf(window, ScopeButtonId.Lens));
            Assert.Equal(["ON", "PATH", "QUEUE", "CHECKIN"], TrayButtons(window).Select(DrawnText));

            Click(ButtonOf(window, ScopeButtonId.LensOn));
            Assert.Equal("Scope lens ON", main.StatusText);
            Assert.True(scope.Lens.Enabled);
            Assert.Contains("active", ButtonOf(window, ScopeButtonId.LensOn).Classes);

            Click(ButtonOf(window, ScopeButtonId.LensPath));
            Assert.Equal("Scope lens path ON", main.StatusText);
            Assert.True(scope.Lens.Path);

            Click(ButtonOf(window, ScopeButtonId.LensPath));
            Assert.Equal("Scope lens path OFF", main.StatusText);
            Assert.False(scope.Lens.Path);
            Assert.Equal(ScopeButtonId.Lens, scope.OpenMenu);

            Click(ButtonOf(window, ScopeButtonId.Lens));
            Assert.Null(scope.OpenMenu);
            Assert.Contains("active", ButtonOf(window, ScopeButtonId.Lens).Classes);

            Click(ButtonOf(window, ScopeButtonId.Lens));
            Click(ButtonOf(window, ScopeButtonId.LensOn));
            Assert.Equal("Scope lens OFF", main.StatusText);
            Assert.False(scope.Lens.Enabled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task LensTray_QueueTogglesTheLensQueue()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            Click(ButtonOf(window, ScopeButtonId.Lens));

            Click(ButtonOf(window, ScopeButtonId.LensQueue));
            Assert.Equal("Scope lens queue ON", main.StatusText);
            Assert.True(scope.Lens.Queue);
            Assert.Contains("active", ButtonOf(window, ScopeButtonId.LensQueue).Classes);

            Click(ButtonOf(window, ScopeButtonId.LensQueue));
            Assert.Equal("Scope lens queue OFF", main.StatusText);
            Assert.False(scope.Lens.Queue);
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
    public async Task ATrayWiderThanTheWindow_WrapsIntoMoreTwoRowBlocks_AndNeverResizesTheScope()
    {
        int[] ids = [.. Enumerable.Range(1, 30)];
        (_, _, ScopeWindow window) = await OpenWithSourceAsync(new Size(600, 800), SourceWithToggles(ids));
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
            Assert.All(buttons, b => Assert.Equal(22d, b.Bounds.Height));
            Assert.All(buttons, b => Assert.True(b.TranslatePoint(new Point(b.Bounds.Width, 0), tray)!.Value.X <= tray.Bounds.Width));
            Assert.True(tray.Bounds.Height > 45, "the tray grows a second two-row block");
            Assert.Equal(bounds, canvas.Bounds);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ATrayBlockNarrowerThanTheFirst_LeavesTheTrayBackgroundBesideIt()
    {
        int[] ids = [.. Enumerable.Range(1, 20)];
        (_, _, ScopeWindow window) = await OpenWithSourceAsync(new Size(600, 800), SourceWithToggles(ids));
        try
        {
            Click(ButtonOf(window, ScopeButtonId.Maps));
            Settle(window);

            Border tray = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Tray");
            Button last = TrayButtons(window).Last();
            Point lastEnd = last.TranslatePoint(new Point(last.Bounds.Width, last.Bounds.Height / 2), tray)!.Value;
            Assert.True(lastEnd.Y > 45, "the tray wraps into a second block");
            var beside = new Point(lastEnd.X + 5, lastEnd.Y);
            Assert.True(beside.X < tray.Bounds.Width, "the second block is narrower than the first");

            var divider = Color.Parse("#22313b");
            Visual[] hits = [.. window.GetVisualsAt(tray.TranslatePoint(beside, window)!.Value)];
            Assert.Contains(tray, hits);
            Assert.DoesNotContain(
                hits,
                v =>
                    (v is Panel { Background: ISolidColorBrush p } && (p.Color == divider))
                    || (v is Border { Background: ISolidColorBrush b } && (b.Color == divider))
            );
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ClearingThePrefSet_LeavesEveryTileWhereItWas()
    {
        (_, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            scope.ActivePrefSet = "SR-DAY";
            Settle(window);
            Rect[] before = BarTiles(window);

            scope.ActivePrefSet = null;
            Settle(window);

            Assert.Equal(before, BarTiles(window));
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
        (_, _, ScopeWindow window) = await OpenAsync(new Size(500, 800));
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

    /// <summary>The bar's row of tiles.</summary>
    private static StackPanel MainRowOf(Window window) => window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "MainRow");

    /// <summary>Where <paramref name="button"/>'s top-left sits in the bar's row of tiles.</summary>
    private static Point InRow(Window window, Button button) => button.TranslatePoint(default, MainRowOf(window))!.Value;

    /// <summary>Each bar button's rectangle in the bar's row of tiles, in bar order.</summary>
    private static Rect[] BarTiles(Window window) =>
        [
            .. MainRowOf(window)
                .GetVisualDescendants()
                .OfType<Button>()
                .Where(b => b.Tag is ScopeButtonId)
                .Select(b => new Rect(InRow(window, b), b.Bounds.Size)),
        ];

    [AvaloniaFact]
    public async Task TheDefaultWindow_FitsTheWholeBar()
    {
        (_, _, ScopeWindow window) = await OpenAsync();
        try
        {
            Assert.False(Chevron(window, "ScrollLeft").IsEffectivelyVisible, "the bar fits the default window, so there is no left chevron");
            Assert.False(Chevron(window, "ScrollRight").IsEffectivelyVisible, "the bar fits the default window, so there is no right chevron");
            Grid row = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "BarRow");
            Button[] bar = [.. MainRowOf(window).GetVisualDescendants().OfType<Button>().Where(b => b.Tag is ScopeButtonId)];
            Assert.Equal(19, bar.Length);
            Assert.All(
                bar,
                b => Assert.True(b.TranslatePoint(new Point(b.Bounds.Width, 0), row)!.Value.X <= row.Bounds.Width, $"{Label(b)} ends inside the bar")
            );
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ValueButtons_AreFullTiles_TheRestPairUp()
    {
        (_, _, ScopeWindow window) = await OpenAsync();
        try
        {
            ScopeButtonId[] full =
            [
                ScopeButtonId.Range,
                ScopeButtonId.Others,
                ScopeButtonId.Vector,
                ScopeButtonId.Hist,
                ScopeButtonId.Profile,
                ScopeButtonId.Template,
                ScopeButtonId.Lens,
                ScopeButtonId.Brite,
                ScopeButtonId.Pref,
                ScopeButtonId.DeleteTearoff,
            ];
            Assert.All(full, id => Assert.Equal(45d, ButtonOf(window, id).Bounds.Height));

            (ScopeButtonId Top, ScopeButtonId Bottom)[] pairs =
            [
                (ScopeButtonId.Tools, ScopeButtonId.Auto),
                (ScopeButtonId.Maps, ScopeButtonId.Tdm),
                (ScopeButtonId.Db, ScopeButtonId.Views),
            ];
            foreach ((ScopeButtonId topId, ScopeButtonId bottomId) in pairs)
            {
                Button top = ButtonOf(window, topId);
                Button bottom = ButtonOf(window, bottomId);
                Assert.Equal(22d, top.Bounds.Height);
                Assert.Equal(22d, bottom.Bounds.Height);
                Point topAt = InRow(window, top);
                Point bottomAt = InRow(window, bottom);
                Assert.Equal(0d, topAt.Y);
                Assert.Equal(23d, bottomAt.Y);
                Assert.Equal(topAt.X, bottomAt.X);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task APrefSet_LeavesEveryTileWhereItWas()
    {
        (_, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            Settle(window);
            Rect[] before = BarTiles(window);

            scope.ActivePrefSet = "SR-DAY";
            Settle(window);

            Assert.Equal(before, BarTiles(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ANineCharacterSetName_MovesNoTile()
    {
        (_, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            Settle(window);
            Rect[] noSet = BarTiles(window);

            scope.ActivePrefSet = "SR-DAY-NT";
            Settle(window);

            Assert.Equal(noSet, BarTiles(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ATenCharacterSetName_WidensPref()
    {
        (_, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            Settle(window);
            double noSet = ButtonOf(window, ScopeButtonId.Pref).Bounds.Width;

            scope.ActivePrefSet = "SR-DAY-NTX";
            Settle(window);

            Assert.True(ButtonOf(window, ScopeButtonId.Pref).Bounds.Width > noSet, "a 10-character name widens PREF past its reserve");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ALongerSetName_StillWidensPref()
    {
        (_, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            scope.ActivePrefSet = "SR-DAY";
            Settle(window);
            double reserved = ButtonOf(window, ScopeButtonId.Pref).Bounds.Width;

            scope.ActivePrefSet = "SR-DAY-LONG1";
            Settle(window);

            Assert.True(ButtonOf(window, ScopeButtonId.Pref).Bounds.Width > reserved, "a 12-character name widens PREF past its reserve");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PressingAPartHiddenButton_DoesNotScrollTheBar()
    {
        (_, ScopeViewModel scope, ScopeWindow window) = await OpenAsync(new Size(500, 800));
        try
        {
            ScrollViewer scroller = Scroller(window);
            Button maps = ButtonOf(window, ScopeButtonId.Maps);

            // Size the window so the bar's right edge cuts MAPS in half.
            double mapsLeft = maps.TranslatePoint(default, scroller)!.Value.X;
            window.Width += mapsLeft + (maps.Bounds.Width / 2) - scroller.Viewport.Width;
            Settle(window);
            mapsLeft = maps.TranslatePoint(default, scroller)!.Value.X;
            double edge = scroller.Viewport.Width;
            Assert.True((mapsLeft < edge) && (mapsLeft + maps.Bounds.Width > edge), "MAPS starts part-hidden past the bar's right edge");
            Assert.Equal(0d, scroller.Offset.X);

            Point press = maps.TranslatePoint(new Point((edge - mapsLeft) / 2, maps.Bounds.Height / 2), window)!.Value;
            window.MouseMove(press);
            window.MouseDown(press, MouseButton.Left);
            window.MouseUp(press, MouseButton.Left);
            Settle(window);

            Assert.Equal(0d, scroller.Offset.X);
            Assert.Equal(ScopeButtonId.Maps, scope.OpenMenu);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TabFocus_BringsAHiddenButtonIntoView()
    {
        (_, _, ScopeWindow window) = await OpenAsync(new Size(500, 800));
        try
        {
            ScrollViewer scroller = Scroller(window);
            Button del = ButtonOf(window, ScopeButtonId.DeleteTearoff);
            Point delEnd = del.TranslatePoint(new Point(del.Bounds.Width, 0), scroller)!.Value;
            Assert.True(delEnd.X > scroller.Viewport.Width, "DEL TEAROFF starts past the bar's right edge");

            del.Focus(NavigationMethod.Tab);
            Settle(window);

            Point delLeft = del.TranslatePoint(default, scroller)!.Value;
            Assert.InRange(delLeft.X, 0, scroller.Viewport.Width - del.Bounds.Width);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheWheel_OverANonSpinnerButton_DoesNotScrollTheBar()
    {
        (_, _, ScopeWindow window) = await OpenAsync(new Size(500, 800));
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
        (_, _, ScopeWindow window) = await OpenAsync(new Size(500, 800));
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
            // The 1 DIP divider inside the TOOLS/AUTO pair, just above AUTO.
            Point inPair = auto.TranslatePoint(new Point(auto.Bounds.Width / 2, -0.5), window)!.Value;

            WheelAt(window, between, new Vector(0, 1), RawInputModifiers.Shift);
            Assert.Equal(offset, scroller.Offset.X);
            WheelAt(window, between, new Vector(1, 0), RawInputModifiers.None);
            Assert.Equal(offset, scroller.Offset.X);
            WheelAt(window, inPair, new Vector(0, 1), RawInputModifiers.Shift);
            Assert.Equal(offset, scroller.Offset.X);
            WheelAt(window, inPair, new Vector(1, 0), RawInputModifiers.None);
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
        (_, _, ScopeWindow window) = await OpenAsync(new Size(500, 800));
        try
        {
            ScrollViewer scroller = Scroller(window);
            PressUntilDisabled(Chevron(window, "ScrollRight"), 5);
            Assert.True(scroller.Offset.X > 0, "the bar scrolled to its right end");
            double extent = scroller.Extent.Width;

            // The bar has no padding, so its row is the window's width: 32 DIPs to spare over the bar's extent.
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
        (_, ScopeViewModel scope, ScopeWindow window) = await OpenAsync(new Size(500, 800));
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

            Point drop = layer.TranslatePoint(new Point(250, 400), window)!.Value;
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
