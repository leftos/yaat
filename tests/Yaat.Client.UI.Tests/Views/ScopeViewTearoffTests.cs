using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Dock;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Scope views torn off onto the scope: <c>.view &lt;name&gt; tear &lt;corner&gt; &lt;dx&gt; &lt;dy&gt;</c> floats a view at a
/// corner, <c>.view &lt;name&gt; dock</c> returns it to the dock, a view keeps its place while closed, and the SSA opens
/// floating at the scope's top-left.
/// </summary>
public class ScopeViewTearoffTests
{
    private const string Usage = "Usage: .view <ssa|ca> <on|off|dock> | .view <ssa|ca> tear <tl|tr|bl|br> <dx> <dy>";

    private static readonly ScopePlacement SsaDefault = new(ScopeCorner.TopLeft, 8, 8);

    private static async Task<(MainViewModel Main, ScopeViewModel Scope)> OpenScopeAsync()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null);
        main.OpenScope = scope;
        return (main, scope);
    }

    /// <summary>The open view named <paramref name="name"/>, docked or floating.</summary>
    private static ScopeDockViewModel ViewOf(ScopeViewModel scope, string name) =>
        scope.DockViews.Concat(scope.FloatingViews).Single(v => v.Name == name);

    [AvaloniaFact]
    public async Task Tear_PlacesTheViewAtExactlyThatCorner_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunViewCommand(".view ca on");

            main.RunViewCommand(".view ca tear br 10 20");

            Assert.Equal(new ScopePlacement(ScopeCorner.BottomRight, 10, 20), ViewOf(scope, ScopeToolbarButtons.ViewCaName).Place);
            Assert.Equal(".view ca tear br 10 20", main.StatusText);
            Assert.Equal([ScopeToolbarButtons.ViewSsaName, ScopeToolbarButtons.ViewCaName], scope.OpenViews);
            Assert.Empty(scope.DockViews);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Tear_OnAClosedView_OpensIt()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            Assert.False(scope.IsViewOpen(ScopeToolbarButtons.ViewCaName));

            main.RunViewCommand(".VIEW CA TEAR TL 3 4");

            Assert.True(scope.IsViewOpen(ScopeToolbarButtons.ViewCaName));
            Assert.Equal(new ScopePlacement(ScopeCorner.TopLeft, 3, 4), ViewOf(scope, ScopeToolbarButtons.ViewCaName).Place);
            Assert.Equal(".view ca tear tl 3 4", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Dock_ReturnsTheViewToTheDock_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunViewCommand(".view ssa dock");

            ScopeDockViewModel ssa = ViewOf(scope, ScopeToolbarButtons.ViewSsaName);
            Assert.Null(ssa.Place);
            Assert.Equal(".view ssa dock", main.StatusText);
            Assert.True(scope.HasDockedViews);
            Assert.Equal([ssa], scope.DockViews);
            Assert.Empty(scope.FloatingViews);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task AClosedAndReopenedView_KeepsItsPlace()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunViewCommand(".view ca tear tr 5 6");
            main.RunViewCommand(".view ca off");

            // A closed view floats nowhere.
            Assert.False(scope.IsViewOpen(ScopeToolbarButtons.ViewCaName));
            Assert.DoesNotContain(scope.FloatingViews, v => v.Name == ScopeToolbarButtons.ViewCaName);

            main.RunViewCommand(".view ca on");

            Assert.Equal(new ScopePlacement(ScopeCorner.TopRight, 5, 6), ViewOf(scope, ScopeToolbarButtons.ViewCaName).Place);
            Assert.Contains(scope.FloatingViews, v => v.Name == ScopeToolbarButtons.ViewCaName);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(".view ca tear")]
    [InlineData(".view ca tear br 10")]
    [InlineData(".view ca tear br 10 20 30")]
    [InlineData(".view ca tear xx 10 20")]
    [InlineData(".view ca tear br -1 20")]
    [InlineData(".view ca tear br 10 +2")]
    [InlineData(".view ca tear br 10 2.5")]
    [InlineData(".view ca dock now")]
    [InlineData(".view tab tear br 10 20")]
    [InlineData(".view tab dock")]
    [InlineData(".view ca")]
    [InlineData(".view ca maybe")]
    [InlineData(".view")]
    public async Task ABadViewCommand_GivesTheUsage_AndChangesNothing(string command)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunViewCommand(".view ca tear bl 1 2");
            IReadOnlyList<string> open = [.. scope.OpenViews];
            ScopeDockViewModel[] docked = [.. scope.DockViews];
            ScopeDockViewModel[] floating = [.. scope.FloatingViews];
            ScopePlacement?[] places = [.. floating.Select(v => v.Place)];

            main.RunViewCommand(command);

            Assert.Equal(Usage, main.StatusText);
            Assert.Equal(open, scope.OpenViews);
            Assert.Equal(docked, scope.DockViews);
            Assert.Equal(floating, scope.FloatingViews);
            Assert.Equal(places, scope.FloatingViews.Select(v => v.Place));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task AFreshScope_FloatsTheSsaAtTheTopLeft_AndDocksNothing()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            Assert.Equal(SsaDefault, ViewOf(scope, ScopeToolbarButtons.ViewSsaName).Place);
            Assert.Empty(scope.DockViews);
            Assert.False(scope.HasDockedViews);
            Assert.Equal([ScopeToolbarButtons.ViewSsaName], scope.FloatingViews.Select(v => v.Name));
        }
        finally
        {
            scope.Dispose();
        }
    }

    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeWindow Window)> OpenWindowAsync()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        var window = new ScopeWindow(main.Preferences, scope);
        window.Show();
        Settle(window);
        return (main, scope, window);
    }

    /// <summary>
    /// Widens the window so the whole bar, DEL TEAROFF last, is on screen: the bar does not wrap yet (YAAT-2357).
    /// </summary>
    private static void Widen(Window window)
    {
        window.Width = 2000;
        Settle(window);
    }

    /// <summary>Lays out and renders the window, as hit testing reads the rendered scene.</summary>
    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static ScopeTearoffLayer LayerOf(Window window) => window.GetVisualDescendants().OfType<ScopeTearoffLayer>().Single();

    private static ScopeView ScopeViewOf(Window window) => window.GetVisualDescendants().OfType<ScopeView>().Single();

    private static ScopeDock DockOf(Window window) => window.GetVisualDescendants().OfType<ScopeDock>().Single();

    private static ScopeCanvas CanvasOf(Window window) => window.GetVisualDescendants().OfType<ScopeCanvas>().Single();

    /// <summary>The shown card of the view named <paramref name="name"/> floating in the layer.</summary>
    private static ScopeViewCard FloatingCard(Window window, string name) =>
        LayerOf(window).GetVisualDescendants().OfType<ScopeViewCard>().Single(c => c.IsEffectivelyVisible && (c.ViewName == name));

    /// <summary>The shown card of the view named <paramref name="name"/> in the dock.</summary>
    private static ScopeViewCard DockedCard(Window window, string name) =>
        DockOf(window).GetVisualDescendants().OfType<ScopeViewCard>().Single(c => c.IsEffectivelyVisible && (c.ViewName == name));

    private static Border PickAreaOf(ScopeViewCard card) => card.FindControl<Border>("PickArea")!;

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

    /// <summary>Where a press on the card's title area grabs it: the area's centre, from the card's top-left.</summary>
    private static Point TitleGrab(ScopeViewCard card)
    {
        Border pick = PickAreaOf(card);
        return pick.TranslatePoint(new Point(pick.Bounds.Width / 2, pick.Bounds.Height / 2), card)!.Value;
    }

    private static void PressTitle(Window window, ScopeViewCard card) => ClickAt(window, CentreOf(PickAreaOf(card), window));

    private static int Whole(double distance) => (int)Math.Round(distance, MidpointRounding.AwayFromZero);

    /// <summary>A view move's outline in the layer, or null when no view move runs.</summary>
    private static Border? OutlineOf(Window window) => LayerOf(window).Children.OfType<Border>().SingleOrDefault(b => !b.IsHitTestVisible);

    /// <summary>The left and right edges of <paramref name="control"/> in the layer.</summary>
    private static (double Left, double Right) EdgesInLayer(Window window, Control control)
    {
        double left = control.TranslatePoint(default, LayerOf(window))!.Value.X;
        return (left, left + control.Bounds.Width);
    }

    /// <summary>The bar's shown control for the button with <paramref name="id"/>.</summary>
    private static ScopeButtonControl BarControl(Window window, ScopeButtonId id) =>
        window.GetVisualDescendants().OfType<ScopeButtonControl>().Single(c => !c.IsTearoff && (c.Model.Id == id) && c.IsEffectivelyVisible);

    /// <summary>The shown button named <paramref name="name"/> on <paramref name="card"/>.</summary>
    private static Button CardButton(ScopeViewCard card, string name) =>
        card.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && (AutomationProperties.GetName(b) == name));

    [AvaloniaFact]
    public async Task ClosingADockedView_MidMove_EndsTheMove_AndAClickDoesNotReopenIt()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenWindowAsync();
        try
        {
            main.RunViewCommand(".view ca on");
            Settle(window);
            ScopeDockViewModel ca = ViewOf(scope, ScopeToolbarButtons.ViewCaName);
            PressTitle(window, DockedCard(window, ScopeToolbarButtons.ViewCaName));

            main.RunViewCommand(".view ca off");
            Settle(window);
            Assert.Null(OutlineOf(window));
            ClickAt(window, InLayer(window, 600, 400));

            Assert.False(scope.IsViewOpen(ScopeToolbarButtons.ViewCaName));
            Assert.Null(ca.Place);
            Assert.Equal(".view ca off", main.StatusText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ADockedCardsDrop_OnARightCorner_LandsTheCardOnTheOutlinesEdges()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenWindowAsync();
        try
        {
            // The SSA docked too keeps the dock, and so the layer's width, after the drop.
            main.RunViewCommand(".view ssa dock");
            main.RunViewCommand(".view ca on");
            Settle(window);
            ScopeViewCard card = DockedCard(window, ScopeToolbarButtons.ViewCaName);
            Point grab = TitleGrab(card);
            ScopeTearoffLayer layer = LayerOf(window);
            PressTitle(window, card);
            window.MouseMove(InLayer(window, 100, 100));
            Settle(window);
            double outlineWidth = OutlineOf(window)!.Bounds.Width;

            // The outline's right edge on a whole pixel R: drop.X = grab.X + R - outlineWidth, so Dx = width - R is whole too.
            double right = Math.Floor(layer.Bounds.Width * 0.9);
            var drop = new Point(grab.X + right - outlineWidth, layer.Bounds.Height * 3 / 4);
            window.MouseMove(InLayer(window, drop.X, drop.Y));
            Settle(window);
            (double Left, double Right) outline = EdgesInLayer(window, OutlineOf(window)!);
            ClickAt(window, InLayer(window, drop.X, drop.Y));

            (double Left, double Right) landed = EdgesInLayer(window, FloatingCard(window, ScopeToolbarButtons.ViewCaName));
            Assert.Equal(outline.Left, landed.Left, 9);
            Assert.Equal(outline.Right, landed.Right, 9);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task APressOnADockedCardsCloseButton_MidMove_EndsTheMove_AndClosesTheView()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenWindowAsync();
        try
        {
            main.RunViewCommand(".view ssa dock");
            main.RunViewCommand(".view ca on");
            Settle(window);
            ScopeViewCard card = DockedCard(window, ScopeToolbarButtons.ViewCaName);
            PressTitle(window, card);
            Assert.NotNull(OutlineOf(window));

            ClickAt(window, CentreOf(CardButton(card, "Close"), window));

            Assert.Null(OutlineOf(window));
            Assert.False(scope.IsViewOpen(ScopeToolbarButtons.ViewCaName));
            Assert.Equal(".view ca off", main.StatusText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task APressOnTheBarsBrite_MidTearoff_EndsTheMove_AndOpensItsTray()
    {
        (_, ScopeViewModel scope, ScopeWindow window) = await OpenWindowAsync();
        try
        {
            Widen(window);
            ScopeButtonControl range = BarControl(window, ScopeButtonId.Range);
            ClickAt(window, CentreOf(range.Strip!, window));
            Assert.Contains(LayerOf(window).Children.OfType<ScopeButtonControl>(), c => !c.IsHitTestVisible);

            ClickAt(window, CentreOf(BarControl(window, ScopeButtonId.Brite).Button, window));

            Assert.DoesNotContain(LayerOf(window).Children.OfType<ScopeButtonControl>(), c => !c.IsHitTestVisible);
            Assert.Empty(scope.Tearoffs);
            Assert.Equal(ScopeButtonId.Brite, scope.OpenMenu);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AFloatingCard_MovedAgain_TakesTheNearestCorner_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenWindowAsync();
        try
        {
            ScopeViewCard card = FloatingCard(window, ScopeToolbarButtons.ViewSsaName);
            Point grab = TitleGrab(card);
            Size size = card.Bounds.Size;
            ScopeTearoffLayer layer = LayerOf(window);
            double width = layer.Bounds.Width;
            double height = layer.Bounds.Height;

            PressTitle(window, card);
            var drop = new Point(width * 3 / 4, height * 3 / 4);
            ClickAt(window, InLayer(window, drop.X, drop.Y));

            // The outline's rect is (drop - grab, size), in the bottom-right quadrant:
            // Dx = width - (drop.X - grab.X + size.Width), Dy = height - (drop.Y - grab.Y + size.Height).
            int dx = Whole(width - (drop.X - grab.X + size.Width));
            int dy = Whole(height - (drop.Y - grab.Y + size.Height));
            Assert.True((dx > 0) && (dy > 0), "the drop is fully inside the layer");
            Assert.Equal(new ScopePlacement(ScopeCorner.BottomRight, dx, dy), ViewOf(scope, ScopeToolbarButtons.ViewSsaName).Place);
            Assert.StartsWith(".view ssa tear br ", main.StatusText);
            Assert.Equal(
                ScopeToolbarButtons.ViewTearCommand(ScopeToolbarButtons.ViewSsaName, new ScopePlacement(ScopeCorner.BottomRight, dx, dy)),
                main.StatusText
            );
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DockingTheFloatingSsa_MidMove_EndsTheMove()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenWindowAsync();
        try
        {
            PressTitle(window, FloatingCard(window, ScopeToolbarButtons.ViewSsaName));

            main.RunViewCommand(".view ssa dock");
            Settle(window);
            Assert.Null(OutlineOf(window));
            ClickAt(window, InLayer(window, 600, 400));

            Assert.Null(ViewOf(scope, ScopeToolbarButtons.ViewSsaName).Place);
            Assert.Equal(".view ssa dock", main.StatusText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DelTearoff_MidViewMove_EndsTheMove()
    {
        (_, ScopeViewModel scope, ScopeWindow window) = await OpenWindowAsync();
        try
        {
            Widen(window);
            PressTitle(window, FloatingCard(window, ScopeToolbarButtons.ViewSsaName));
            Assert.NotNull(OutlineOf(window));

            ClickAt(window, CentreOf(BarControl(window, ScopeButtonId.DeleteTearoff).Button, window));

            Assert.True(scope.IsDeletingTearoffs);
            Assert.Null(OutlineOf(window));
            Assert.Equal(SsaDefault, ViewOf(scope, ScopeToolbarButtons.ViewSsaName).Place);
            Assert.True(FloatingCard(window, ScopeToolbarButtons.ViewSsaName).IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AViewMovesFollower_IsA1PxGoldOutline_OfTheCardsContentSize()
    {
        (_, _, ScopeWindow window) = await OpenWindowAsync();
        try
        {
            ScopeViewCard card = FloatingCard(window, ScopeToolbarButtons.ViewSsaName);
            Size size = card.Bounds.Size;

            PressTitle(window, card);

            Border outline = OutlineOf(window)!;
            Assert.Equal(Color.Parse("#E0B84A"), Assert.IsAssignableFrom<ISolidColorBrush>(outline.BorderBrush).Color);
            Assert.Equal(new Thickness(1), outline.BorderThickness);
            Assert.Null(outline.Background);
            Assert.Equal(size, outline.Bounds.Size);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Whether a pointer at <paramref name="point"/> (window coordinates) lands on the scope canvas.</summary>
    private static bool HitsCanvas(Window window, ScopeCanvas canvas, Point point)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window.InputHitTest(point) is Visual hit && ((hit == canvas) || hit.GetVisualAncestors().Contains(canvas));
    }

    [AvaloniaFact]
    public async Task TheWindow_OpensWithTheSsaFloating8PxFromTheScopesTopLeft_AndTheDockHidden()
    {
        (_, _, ScopeWindow window) = await OpenWindowAsync();
        try
        {
            ScopeViewCard ssa = FloatingCard(window, ScopeToolbarButtons.ViewSsaName);

            Assert.Equal(new Point(8, 8), ssa.TranslatePoint(default, ScopeViewOf(window))!.Value);
            Assert.False(DockOf(window).IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheFloatingSsaCard_DrawsItsBody()
    {
        (_, ScopeViewModel scope, ScopeWindow window) = await OpenWindowAsync();
        try
        {
            SsaViewModel ssa = scope.FloatingViews.OfType<SsaViewModel>().Single();

            string[] texts = ScopeDockTests.Texts(FloatingCard(window, ScopeToolbarButtons.ViewSsaName));

            Assert.Contains("SSA", texts);
            Assert.Contains(ssa.Line1, texts);
            Assert.Contains(ssa.Status, texts);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ATitlePress_OnADockedCard_ThenAClick_FloatsItAtTheNearestCorner_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenWindowAsync();
        try
        {
            main.RunViewCommand(".view ca on");
            Settle(window);
            ScopeViewCard card = DockedCard(window, ScopeToolbarButtons.ViewCaName);
            Point grab = TitleGrab(card);
            ScopeTearoffLayer layer = LayerOf(window);
            double width = layer.Bounds.Width;
            double height = layer.Bounds.Height;

            PressTitle(window, card);
            Assert.True(DockedCard(window, ScopeToolbarButtons.ViewCaName).IsEffectivelyVisible, "a docked card stays until the drop");
            var drop = new Point(width * 3 / 4, height * 3 / 4);
            window.MouseMove(InLayer(window, drop.X, drop.Y));
            Settle(window);

            // The outline has the card's content size, not the dock column's stretched width.
            Size size = OutlineOf(window)!.Bounds.Size;
            Assert.True(size.Width < card.Bounds.Width, "the outline is narrower than the stretched docked card");
            ClickAt(window, InLayer(window, drop.X, drop.Y));

            // The outline's rect is (drop - grab, content size), in the bottom-right quadrant; its distances run from the
            // layer's far edges: Dx = width - (drop.X - grab.X + size.Width), Dy = height - (drop.Y - grab.Y + size.Height).
            int dx = Whole(width - (drop.X - grab.X + size.Width));
            int dy = Whole(height - (drop.Y - grab.Y + size.Height));
            Assert.True((dx > 0) && (dy > 0), "the drop is fully inside the layer");
            ScopeDockViewModel ca = ViewOf(scope, ScopeToolbarButtons.ViewCaName);
            Assert.Equal(new ScopePlacement(ScopeCorner.BottomRight, dx, dy), ca.Place);
            Assert.Equal(ScopeToolbarButtons.ViewTearCommand(ScopeToolbarButtons.ViewCaName, ca.Place!), main.StatusText);
            Assert.StartsWith(".view ca tear br ", main.StatusText);
            ScopeViewCard floating = FloatingCard(window, ScopeToolbarButtons.ViewCaName);
            Assert.True(floating.IsEffectivelyVisible);
            Assert.Equal(size, floating.Bounds.Size);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Esc_MidMove_LeavesThePlace_AndShowsTheCardAgain()
    {
        (_, ScopeViewModel scope, ScopeWindow window) = await OpenWindowAsync();
        try
        {
            PressTitle(window, FloatingCard(window, ScopeToolbarButtons.ViewSsaName));
            window.MouseMove(InLayer(window, 600, 400));
            Settle(window);
            Assert.DoesNotContain(
                LayerOf(window).GetVisualDescendants().OfType<ScopeViewCard>(),
                c => c.IsEffectivelyVisible && (c.ViewName == ScopeToolbarButtons.ViewSsaName)
            );

            window.DispatchKey(Key.Escape);
            Settle(window);

            Assert.Equal(SsaDefault, ViewOf(scope, ScopeToolbarButtons.ViewSsaName).Place);
            ScopeViewCard ssa = FloatingCard(window, ScopeToolbarButtons.ViewSsaName);
            Assert.Equal(new Point(8, 8), ssa.TranslatePoint(default, ScopeViewOf(window))!.Value);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AFloatingCardsDockButton_DocksIt_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenWindowAsync();
        try
        {
            Button dock = FloatingCard(window, ScopeToolbarButtons.ViewSsaName)
                .GetVisualDescendants()
                .OfType<Button>()
                .Single(b => AutomationProperties.GetName(b) == "Dock");
            Assert.Equal("▸", dock.Content);

            ClickAt(window, CentreOf(dock, window));

            Assert.Null(ViewOf(scope, ScopeToolbarButtons.ViewSsaName).Place);
            Assert.Equal(".view ssa dock", main.StatusText);
            Assert.True(DockOf(window).IsEffectivelyVisible);
            Assert.DoesNotContain(
                DockedCard(window, ScopeToolbarButtons.ViewSsaName).GetVisualDescendants().OfType<Button>(),
                b => b.IsEffectivelyVisible && (AutomationProperties.GetName(b) == "Dock")
            );
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AClickOnTheEmptyLayer_StillSelectsAnAircraft()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenWindowAsync();
        try
        {
            ScopeCanvas canvas = CanvasOf(window);
            Point ssaCentre = CentreOf(FloatingCard(window, ScopeToolbarButtons.ViewSsaName), window);
            Assert.False(HitsCanvas(window, canvas, ssaCentre), "a floating card is hit, not the canvas");
            Assert.True(HitsCanvas(window, canvas, InLayer(window, 500, 400)), "the layer's empty area reaches the canvas");

            (double lat, double lon) = canvas.Viewport.ScreenToLatLon(400f, 400f);
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
            FieldRect callsign = Assert.Single(canvas.CurrentFrame.Tracks).Block.Fields.First(f => f.Slot == ScopeFieldSlot.Callsign);
            Point point = canvas.TranslatePoint(new Point(callsign.Rect.MidX, callsign.Rect.MidY), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
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
    public async Task ATitlePress_InDelTearoffsMode_StartsNoMove()
    {
        (_, ScopeViewModel scope, ScopeWindow window) = await OpenWindowAsync();
        try
        {
            Widen(window);
            ScopeButtonControl delete = window
                .GetVisualDescendants()
                .OfType<ScopeButtonControl>()
                .Single(c => !c.IsTearoff && (c.Model.Id == ScopeButtonId.DeleteTearoff) && c.IsEffectivelyVisible);
            ClickAt(window, CentreOf(delete.Button, window));
            Assert.True(scope.IsDeletingTearoffs);

            PressTitle(window, FloatingCard(window, ScopeToolbarButtons.ViewSsaName));
            Assert.True(FloatingCard(window, ScopeToolbarButtons.ViewSsaName).IsEffectivelyVisible, "no move hides the card");
            ClickAt(window, InLayer(window, 600, 400));

            Assert.Equal(SsaDefault, ViewOf(scope, ScopeToolbarButtons.ViewSsaName).Place);
            Assert.True(scope.IsDeletingTearoffs);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AResize_KeepsABottomRightCardsCornerDistance()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenWindowAsync();
        try
        {
            main.RunViewCommand(".view ssa tear br 20 10");
            Settle(window);
            ScopeTearoffLayer layer = LayerOf(window);

            (double Right, double Bottom) Gaps()
            {
                ScopeViewCard card = FloatingCard(window, ScopeToolbarButtons.ViewSsaName);
                Point far = card.TranslatePoint(new Point(card.Bounds.Width, card.Bounds.Height), layer)!.Value;
                return (layer.Bounds.Width - far.X, layer.Bounds.Height - far.Y);
            }

            Assert.Equal((20d, 10d), Gaps());
            double width = layer.Bounds.Width;

            window.Width -= 200;
            window.Height -= 100;
            Settle(window);

            Assert.Equal(width - 200, layer.Bounds.Width, 0);
            Assert.Equal((20d, 10d), Gaps());
        }
        finally
        {
            window.Close();
        }
    }
}
