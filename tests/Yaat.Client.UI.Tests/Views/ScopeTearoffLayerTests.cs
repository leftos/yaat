using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Button tear-offs on the Scope, driven by real pointer and key input: a strip press tears a copy off, a click drops it
/// at the nearest corner, a copy's accent bar moves it, Esc cancels, DEL TEAROFF deletes the marked copies, and the
/// layer's empty area passes clicks to the canvas.
/// </summary>
public class ScopeTearoffLayerTests
{
    private static readonly ScopeButtonKey RangeKey = new(ScopeButtonId.Range, null);

    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeWindow Window)> OpenScopeAsync()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeTestFixture.OpenScopeAsync();
        var window = new ScopeWindow(main.Preferences, scope);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Settle(window);
        return (main, scope, window);
    }

    /// <summary>Lays out and renders the window, as hit testing reads the rendered scene.</summary>
    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Whether a pointer at <paramref name="point"/> (window coordinates) lands on the scope canvas.</summary>
    private static bool HitsCanvas(Window window, ScopeCanvas canvas, Point point)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window.InputHitTest(point) is Visual hit && ((hit == canvas) || hit.GetVisualAncestors().Contains(canvas));
    }

    private static ScopeTearoffLayer LayerOf(Window window) => window.GetVisualDescendants().OfType<ScopeTearoffLayer>().Single();

    private static ScopeCanvas CanvasOf(Window window) => window.GetVisualDescendants().OfType<ScopeCanvas>().Single();

    /// <summary>The bar's (or tray's) shown control for the button with <paramref name="id"/>.</summary>
    private static ScopeButtonControl BarControl(Window window, ScopeButtonId id) =>
        window.GetVisualDescendants().OfType<ScopeButtonControl>().Single(c => !c.IsTearoff && (c.Model.Id == id) && c.IsEffectivelyVisible);

    /// <summary>The shown, settled tear-off copies on the scope (not a move's follower).</summary>
    private static ScopeButtonControl[] Copies(Window window) =>
        [
            .. LayerOf(window)
                .GetVisualDescendants()
                .OfType<ScopeButtonControl>()
                .Where(c => c.IsTearoff && c.IsHitTestVisible && c.IsEffectivelyVisible),
        ];

    private static ScopeButtonControl CopyOf(Window window, ScopeButtonKey key) => Copies(window).Single(c => c.Key == key);

    private static Point CentreOf(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static Point InLayer(Window window, double x, double y) => LayerOf(window).TranslatePoint(new Point(x, y), window)!.Value;

    /// <summary>A left click at <paramref name="point"/> (window coordinates), with the pointer moved there first.</summary>
    private static void ClickAt(Window window, Point point)
    {
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    private static void PressStrip(Window window, ScopeButtonControl control) => ClickAt(window, CentreOf(control.Strip!, window));

    /// <summary>Where <see cref="PressStrip"/> grabs <paramref name="control"/>: its strip's centre, from the control's top-left.</summary>
    private static Point StripGrab(ScopeButtonControl control)
    {
        Border strip = control.Strip!;
        return strip.TranslatePoint(new Point(strip.Bounds.Width / 2, strip.Bounds.Height / 2), control)!.Value;
    }

    /// <summary>The move's follower, or null when no move runs.</summary>
    private static ScopeButtonControl? FollowerOf(Window window) =>
        LayerOf(window).GetVisualDescendants().OfType<ScopeButtonControl>().SingleOrDefault(c => c.IsTearoff && !c.IsHitTestVisible);

    /// <summary>A distance rounded to whole pixels, as a placement rounds it.</summary>
    private static int Whole(double distance) => (int)Math.Round(distance, MidpointRounding.AwayFromZero);

    private static void WheelUp(Window window, Control control)
    {
        Settle(window);
        window.MouseWheel(CentreOf(control, window), new Vector(0, 1));
        Settle(window);
    }

    [AvaloniaFact]
    public async Task EveryTearableKey_ResolvesThroughButtonFor()
    {
        MainViewModel main = await ScopeTestFixture.MainAsync();
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);

        foreach (
            ScopeButtonId id in Enum.GetValues<ScopeButtonId>().Where(id => (id != ScopeButtonId.MapToggle) && ScopeToolbarButtons.CanTearOff(id))
        )
        {
            var key = new ScopeButtonKey(id, null);
            Assert.True(ScopeToolbarButtons.TryParseKey(ScopeToolbarButtons.NameOf(key), out ScopeButtonKey? parsed), key.ToString());

            ScopeButton button = scope.ButtonFor(parsed!);

            Assert.Equal(id, button.Id);
            Assert.Equal(ScopeToolbarButtons.NameOf(key), ScopeToolbarButtons.NameOf(ScopeToolbarButtons.KeyOf(button)));
        }

        Assert.Equal(ScopeButtonId.MapToggle, scope.ButtonFor(new ScopeButtonKey(ScopeButtonId.MapToggle, "77")).Id);
    }

    [AvaloniaFact]
    public async Task EachPrefAction_TearsOff_AndDrawsItsOwnLabel()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope tearoff prefsave tl 1 1");
            main.RunScopeCommand(".scope tearoff prefsaveas tl 80 1");
            main.RunScopeCommand(".scope tearoff prefrename tl 160 1");
            main.RunScopeCommand(".scope tearoff prefdelete tl 240 1");
            Settle(window);

            Assert.Equal(["SAVE", "SAVE AS", "RENAME", "DELETE"], Copies(window).Select(c => AutomationProperties.GetName(c.Button)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ATornOffSaveAs_TypesThePrefixAsTheTrayButtonDoes()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenScopeAsync();
        try
        {
            var key = new ScopeButtonKey(ScopeButtonId.PrefSaveAs, null);
            main.RunScopeCommand(".scope tearoff prefsaveas tl 10 10");
            Settle(window);

            ClickAt(window, CentreOf(CopyOf(window, key).Button, window));

            Assert.Equal(ScopeToolbarButtons.PrefSaveAsPrefix, main.CommandText);
            Assert.Equal(main.CommandText.Length, main.CommandCaretIndex);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ATornOffRange_WheelStepsTheRangeAsTheBarsDoes_SharingItsPartStep()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope tearoff range tl 10 10");
            Settle(window);

            WheelUp(window, CopyOf(window, RangeKey).Button);

            Assert.Equal(39, scope.RangeNm);
            Assert.Equal(".scope range 39", main.StatusText);
            Assert.Equal("RANGE 39", AutomationProperties.GetName(CopyOf(window, RangeKey).Button));

            // Half a step on the bar and half on the copy make one step: the two share RANGE's part step.
            main.Preferences.SetScrollSensitivity(0.5);
            WheelUp(window, BarControl(window, ScopeButtonId.Range).Button);
            Assert.Equal(39, scope.RangeNm);
            WheelUp(window, CopyOf(window, RangeKey).Button);

            Assert.Equal(38, scope.RangeNm);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AMapCopy_WhoseMapIsGone_ShowsItsIdAlone_AndAPressSaysTheMapIsUnknown()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenScopeAsync();
        try
        {
            var key = new ScopeButtonKey(ScopeButtonId.MapToggle, "77");
            scope.PlaceTearoff(key, new ScopePlacement(ScopeCorner.TopLeft, 10, 10));
            Settle(window);
            ScopeButtonControl copy = CopyOf(window, key);

            Assert.Equal("77", AutomationProperties.GetName(copy.Button));

            ClickAt(window, CentreOf(copy.Button, window));

            Assert.Equal("Unknown video map: 77", main.StatusText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DelTearoff_HasNoStrip()
    {
        (_, _, ScopeWindow window) = await OpenScopeAsync();
        try
        {
            ScopeButtonControl delete = BarControl(window, ScopeButtonId.DeleteTearoff);

            Assert.Null(delete.Strip);
            Assert.DoesNotContain(delete.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("scopeStrip"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DelTearoff_MidMove_CancelsTheMove_AndOneEscLeavesTheMode()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope tearoff range tl 10 10");
            Settle(window);
            ScopeTearoff[] before = [.. scope.Tearoffs];
            PressStrip(window, CopyOf(window, RangeKey));
            window.MouseMove(InLayer(window, 500, 300));
            Settle(window);

            ClickAt(window, CentreOf(BarControl(window, ScopeButtonId.DeleteTearoff).Button, window));

            Assert.True(scope.IsDeletingTearoffs);
            Assert.Null(FollowerOf(window));
            Assert.Equal(before, scope.Tearoffs);
            Assert.Equal(new Point(10, 10), CopyOf(window, RangeKey).TranslatePoint(default, LayerOf(window))!.Value);

            window.DispatchKey(Key.Escape);
            Settle(window);

            Assert.False(scope.IsDeletingTearoffs);
            Assert.Equal(before, scope.Tearoffs);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The distance from the layer's right edge to <paramref name="control"/>'s right edge.</summary>
    private static double RightGap(Window window, Control control)
    {
        ScopeTearoffLayer layer = LayerOf(window);
        return layer.Bounds.Width - control.TranslatePoint(new Point(control.Bounds.Width, 0), layer)!.Value.X;
    }

    [AvaloniaFact]
    public async Task ACopy_KeepsItsShape_FromDragToDrop()
    {
        (_, _, ScopeWindow window) = await OpenScopeAsync();
        try
        {
            AssertKeepsItsShape(window, BarControl(window, ScopeButtonId.Template), half: false, new Point(300, 200));
            AssertKeepsItsShape(window, BarControl(window, ScopeButtonId.Auto), half: true, new Point(300, 300));
            ScopeToolbarTests.Click(ScopeToolbarTests.ButtonOf(window, ScopeButtonId.Brite));
            Settle(window);
            AssertKeepsItsShape(window, BarControl(window, ScopeButtonId.BriteMapB), half: true, new Point(300, 400));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ATornOffPref_IsAFullTile_AsWideAsTheBarTile()
    {
        (_, _, ScopeWindow window) = await OpenScopeAsync();
        try
        {
            ScopeButtonControl bar = BarControl(window, ScopeButtonId.Pref);
            double barWidth = bar.Bounds.Width;
            PressStrip(window, bar);

            ClickAt(window, InLayer(window, 300, 300));

            ScopeButtonControl copy = CopyOf(window, new ScopeButtonKey(ScopeButtonId.Pref, null));
            Assert.Equal(45d, copy.Bounds.Height);
            Assert.Equal(barWidth, copy.Bounds.Width);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Tears <paramref name="source"/> off and drops it at <paramref name="drop"/> (layer coordinates), and checks the
    /// follower and the dropped copy are both a half tile (22 tall) when <paramref name="half"/>, else a full one (45),
    /// and equally wide.
    /// </summary>
    private static void AssertKeepsItsShape(Window window, ScopeButtonControl source, bool half, Point drop)
    {
        double height = half ? 22 : 45;
        PressStrip(window, source);
        window.MouseMove(InLayer(window, drop.X, drop.Y));
        Settle(window);
        ScopeButtonControl follower = FollowerOf(window)!;
        Assert.Equal(height, follower.Bounds.Height);
        double dragged = follower.Bounds.Width;

        ClickAt(window, InLayer(window, drop.X, drop.Y));

        ScopeButtonControl copy = CopyOf(window, source.Key);
        Assert.Equal(height, copy.Bounds.Height);
        Assert.Equal(dragged, copy.Bounds.Width);
    }

    [AvaloniaFact]
    public async Task ACopyDroppedNearTheRightEdge_LandsWhereItWasReleased()
    {
        (_, _, ScopeWindow window) = await OpenScopeAsync();
        try
        {
            ScopeButtonControl auto = BarControl(window, ScopeButtonId.Auto);
            PressStrip(window, auto);
            ScopeTearoffLayer layer = LayerOf(window);
            Point release = InLayer(window, layer.Bounds.Width - 80, 300);
            window.MouseMove(release);
            Settle(window);
            ScopeButtonControl follower = FollowerOf(window)!;
            Point followerEnd = follower.TranslatePoint(new Point(follower.Bounds.Width, 0), layer)!.Value;

            ClickAt(window, release);

            ScopeButtonControl copy = CopyOf(window, auto.Key);
            Point copyEnd = copy.TranslatePoint(new Point(copy.Bounds.Width, 0), layer)!.Value;
            Assert.Equal(followerEnd.X, copyEnd.X, 0.5);
            Assert.Equal(followerEnd.Y, copyEnd.Y, 0.5);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AStripPress_ThenAClickElsewhere_DropsACopyAtTheNearestCorner_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenScopeAsync();
        try
        {
            ScopeButtonControl bar = BarControl(window, ScopeButtonId.Range);
            Point grab = StripGrab(bar);
            Size size = bar.Bounds.Size;
            PressStrip(window, bar);
            ScopeTearoffLayer layer = LayerOf(window);
            double width = layer.Bounds.Width;
            double height = layer.Bounds.Height;

            // Hovering with the follower overhanging the right and bottom edges, it keeps its full size.
            window.MouseMove(InLayer(window, width - 5, height - 5));
            Settle(window);
            Assert.Equal(size, FollowerOf(window)!.Bounds.Size);

            var drop = new Point(width - 150, height - 50);
            ClickAt(window, InLayer(window, drop.X, drop.Y));

            // The follower's rect is (drop - grab, size); bottom-right distances run from the layer's far edges:
            // Dx = width - (drop.X - grab.X + size.Width), Dy = height - (drop.Y - grab.Y + size.Height).
            int dx = Whole(width - (drop.X - grab.X + size.Width));
            int dy = Whole(height - (drop.Y - grab.Y + size.Height));
            ScopeTearoff tearoff = Assert.Single(scope.Tearoffs);
            Assert.Equal(RangeKey, tearoff.Key);
            Assert.Equal(new ScopePlacement(ScopeCorner.BottomRight, dx, dy), tearoff.Place);
            Assert.True((dx > 0) && (dy > 0), "the drop is fully inside the layer");
            Assert.Equal(ScopeToolbarButtons.TearoffCommand(RangeKey, tearoff.Place), main.StatusText);
            Assert.StartsWith(".scope tearoff range br ", main.StatusText);
            ScopeButtonControl copy = CopyOf(window, RangeKey);
            Assert.Equal(tearoff.Place.Dx, RightGap(window, copy), 0);
            Assert.True(BarControl(window, ScopeButtonId.Range).IsEffectivelyVisible, "the bar keeps its button");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AStripPress_OnATornOffButton_AddsNothing()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope tearoff range tl 10 10");
            Settle(window);
            ScopeTearoff before = Assert.Single(scope.Tearoffs);

            PressStrip(window, BarControl(window, ScopeButtonId.Range));
            ClickAt(window, InLayer(window, 600, 400));

            Assert.Equal([before], scope.Tearoffs);
            Assert.Single(Copies(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ACopysAccentBar_MovesIt_KeepingItsKey()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope tearoff range tl 10 10");
            Settle(window);
            ScopeButtonControl copy = CopyOf(window, RangeKey);
            Point grab = StripGrab(copy);

            PressStrip(window, copy);
            var drop = new Point(200, 150);
            ClickAt(window, InLayer(window, drop.X, drop.Y));

            // The follower's top-left is drop - grab; top-left distances are that point's coordinates.
            ScopeTearoff moved = Assert.Single(scope.Tearoffs);
            Assert.Equal(RangeKey, moved.Key);
            Assert.Equal(new ScopePlacement(ScopeCorner.TopLeft, Whole(drop.X - grab.X), Whole(drop.Y - grab.Y)), moved.Place);
            Assert.NotEqual(new ScopePlacement(ScopeCorner.TopLeft, 10, 10), moved.Place);
            Assert.Equal(ScopeToolbarButtons.TearoffCommand(RangeKey, moved.Place), main.StatusText);
            Assert.Single(Copies(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Esc_DuringANewTearoff_LeavesNoTearoff()
    {
        (_, ScopeViewModel scope, ScopeWindow window) = await OpenScopeAsync();
        try
        {
            PressStrip(window, BarControl(window, ScopeButtonId.Range));
            window.MouseMove(InLayer(window, 500, 300));

            window.DispatchKey(Key.Escape);
            Settle(window);

            Assert.Empty(scope.Tearoffs);
            ClickAt(window, InLayer(window, 500, 300));
            Assert.Empty(scope.Tearoffs);
            Assert.Empty(Copies(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Esc_DuringAMove_KeepsTheOldPlace()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope tearoff range tl 10 10");
            Settle(window);
            ScopeTearoff before = Assert.Single(scope.Tearoffs);

            PressStrip(window, CopyOf(window, RangeKey));
            window.MouseMove(InLayer(window, 700, 500));
            window.DispatchKey(Key.Escape);
            Settle(window);

            Assert.Equal([before], scope.Tearoffs);
            ScopeButtonControl copy = CopyOf(window, RangeKey);
            Assert.Equal(new Point(10, 10), copy.TranslatePoint(default, LayerOf(window))!.Value);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An airborne aircraft whose symbol sits at the given canvas point, added to the traffic the scope draws.</summary>
    private static AircraftModel AddAircraft(MainViewModel main, ScopeCanvas canvas, double screenX, double screenY)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)screenX, (float)screenY);
        var ac = new AircraftModel
        {
            Callsign = "UAL238",
            AircraftType = "B738",
            FiledAircraftType = "B738",
            FlightRules = "IFR",
            TransponderMode = "C",
            Altitude = 5000,
            GroundSpeed = 250,
            Position = new LatLon(lat, lon),
        };
        main.Aircraft.Add(ac);
        Dispatcher.UIThread.RunJobs();
        return ac;
    }

    /// <summary>The window point of the callsign field of the scope's one track.</summary>
    private static Point CallsignPoint(Window window, ScopeCanvas canvas)
    {
        FieldRect callsign = Assert.Single(canvas.CurrentFrame.Tracks).Block.Fields.First(f => f.Slot == ScopeFieldSlot.Callsign);
        return canvas.TranslatePoint(new Point(callsign.Rect.MidX, callsign.Rect.MidY), window)!.Value;
    }

    [AvaloniaFact]
    public async Task AClickOnTheEmptyLayer_ReachesTheCanvas_AndSelectsAnAircraft()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope tearoff range tl 10 10");
            Settle(window);
            ScopeCanvas canvas = CanvasOf(window);

            Assert.True(HitsCanvas(window, canvas, InLayer(window, 500, 400)), "the layer's empty area reaches the canvas");
            Assert.False(HitsCanvas(window, canvas, CentreOf(CopyOf(window, RangeKey).Button, window)), "a copy is hit, not the canvas");

            AircraftModel ac = AddAircraft(main, canvas, 400, 400);
            Point callsign = CallsignPoint(window, canvas);
            window.MouseDown(callsign, MouseButton.Left);
            window.MouseUp(callsign, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Same(ac, main.SelectedAircraft);
            Assert.Same(ac, scope.SelectedAircraft);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task WhileAMoveRuns_AClickSelectsNoAircraft()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenScopeAsync();
        try
        {
            ScopeCanvas canvas = CanvasOf(window);
            AddAircraft(main, canvas, 400, 400);
            Point callsign = CallsignPoint(window, canvas);

            PressStrip(window, BarControl(window, ScopeButtonId.Range));
            ClickAt(window, callsign);

            Assert.Null(main.SelectedAircraft);
            Assert.Null(scope.SelectedAircraft);
            Assert.Equal(RangeKey, Assert.Single(scope.Tearoffs).Key);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ATornOffMenu_OpensTheBarsTray_AndDrawsActive()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenScopeAsync();
        try
        {
            var britekey = new ScopeButtonKey(ScopeButtonId.Brite, null);
            main.RunScopeCommand(".scope tearoff brite bl 20 20");
            Settle(window);

            ClickAt(window, CentreOf(CopyOf(window, britekey).Button, window));

            Assert.Equal(ScopeButtonId.Brite, scope.OpenMenu);
            Assert.Contains("active", CopyOf(window, britekey).Button.Classes);
            Assert.True(BarControl(window, ScopeButtonId.BriteMapA).IsEffectivelyVisible, "the bar's tray shows the menu's children");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DelTearoff_DeletesOnlyTheMarkedCopy()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope tearoff range tl 10 10");
            main.RunScopeCommand(".scope tearoff brite tr 10 10");
            Settle(window);
            ScopeTearoff brite = scope.Tearoffs[1];

            ClickAt(window, CentreOf(BarControl(window, ScopeButtonId.DeleteTearoff).Button, window));
            Assert.True(scope.IsDeletingTearoffs);
            Assert.Contains("active", BarControl(window, ScopeButtonId.DeleteTearoff).Button.Classes);

            ClickAt(window, CentreOf(CopyOf(window, RangeKey).Button, window));
            Assert.True(scope.IsTearoffMarked(RangeKey));
            Assert.True(CopyOf(window, RangeKey).IsMarkShown);
            Assert.Equal(2, scope.Tearoffs.Count);
            Assert.Null(scope.OpenMenu);

            ClickAt(window, CentreOf(BarControl(window, ScopeButtonId.DeleteTearoff).Button, window));

            Assert.Equal([brite], scope.Tearoffs);
            Assert.Equal(".scope tearoff delete range", main.StatusText);
            Assert.False(scope.IsDeletingTearoffs);
            Assert.Single(Copies(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Esc_InDelTearoffsMode_DeletesNothing()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope tearoff range tl 10 10");
            Settle(window);
            ScopeTearoff[] before = [.. scope.Tearoffs];
            ClickAt(window, CentreOf(BarControl(window, ScopeButtonId.DeleteTearoff).Button, window));
            ClickAt(window, CentreOf(CopyOf(window, RangeKey).Button, window));
            Assert.True(scope.IsTearoffMarked(RangeKey));

            window.DispatchKey(Key.Escape);
            Settle(window);

            Assert.False(scope.IsDeletingTearoffs);
            Assert.False(scope.IsTearoffMarked(RangeKey));
            Assert.Equal(before, scope.Tearoffs);
            Assert.False(CopyOf(window, RangeKey).IsMarkShown);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AResize_KeepsATopRightCopysDistanceFromTheRightEdge()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope tearoff range tr 20 10");
            Settle(window);
            double width = LayerOf(window).Bounds.Width;
            Assert.Equal(20, RightGap(window, CopyOf(window, RangeKey)), 0);

            window.Width -= 200;
            Settle(window);

            Assert.Equal(width - 200, LayerOf(window).Bounds.Width, 0);
            Assert.Equal(20, RightGap(window, CopyOf(window, RangeKey)), 0);
        }
        finally
        {
            window.Close();
        }
    }
}
