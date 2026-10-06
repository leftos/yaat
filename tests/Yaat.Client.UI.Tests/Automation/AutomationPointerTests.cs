using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Automation.Handlers;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Automation.Tools;
using Yaat.Client.ContextMenus;
using Yaat.Client.InputSynthesis;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.UI.Tests.Views;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Settings;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Situation;

namespace Yaat.Client.UI.Tests.Automation;

/// <summary>
/// Hover and drag as raw mouse input through a window's own input path (<see cref="SyntheticMouse"/>): a hover raises
/// PointerEntered and sets IsPointerOver as a real mouse does, and a drag presses, moves with the button held past the
/// drag thresholds, routes its moves to a capture taken on the press, and releases the capture on the release.
/// </summary>
public sealed class AutomationPointerTests : AutomationHostFixture
{
    [AvaloniaFact]
    public async Task Hover_RaisesPointerEnteredOnMenuItem()
    {
        var item = new MenuItem { Header = "Preview" };
        int entered = 0;
        item.PointerEntered += (_, _) => entered++;
        var menu = new ContextMenu { Items = { item } };
        Border pad = Pad();
        ShowWindow("HoverWindow", pad, null);
        menu.Open(pad);
        Render();
        TopLevel menuRoot = TopLevel.GetTopLevel(item)!;
        Point centre = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), menuRoot)!.Value;

        await SyntheticMouse.HoverAsync(menuRoot, centre, 0, CancellationToken.None);

        Assert.Equal(1, entered);
        Assert.True(item.IsPointerOver);
        menu.Close();
    }

    [AvaloniaFact]
    public async Task Drag_CapturesAndMovesPastThreshold()
    {
        Border pad = Pad();
        Window window = ShowWindow("DragWindow", pad, null);
        bool capturedOnPress = false;
        List<(Point At, bool LeftHeld, bool Captured)> moves = [];
        Point? releasedAt = null;
        int captureLost = 0;
        pad.PointerPressed += (_, e) =>
        {
            e.Pointer.Capture(pad);
            capturedOnPress = e.Pointer.Captured == pad;
        };
        pad.PointerMoved += (_, e) =>
            moves.Add((e.GetPosition(window), e.GetCurrentPoint(pad).Properties.IsLeftButtonPressed, e.Pointer.Captured == pad));
        pad.PointerReleased += (_, e) => releasedAt = e.GetPosition(window);
        pad.PointerCaptureLost += (_, _) => captureLost++;
        Point from = pad.TranslatePoint(new Point(60, 40), window)!.Value;
        var to = new Point(20, 20);

        await SyntheticMouse.DragAsync(window, new MouseDrag(from, to, MouseButton.Left, 4, 0), CancellationToken.None);

        Assert.True(capturedOnPress);
        List<(Point At, bool LeftHeld, bool Captured)> held = [.. moves.Where(move => move.LeftHeld)];
        Assert.Equal(4, held.Count);
        // The first step already passes the 4-DIP threshold, and every move reaches the pad through its capture, outside it too.
        Assert.True(Math.Abs(held[0].At.X - from.X) >= 4);
        Assert.All(held, move => Assert.True(move.Captured));
        Assert.Equal(to, held[^1].At);
        Assert.Equal(to, releasedAt);
        Assert.Equal(1, captureLost);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task QuickCommandsSection_DragReordersARow()
    {
        using var scope = new PreferencesFileScope();
        string[] ids = [.. QuickCommandCatalog.Eligible.Where(item => item.Glyph is not null).Take(3).Select(item => item.Id)];
        new UserPreferences().SetQuickCommandList(AircraftSituation.Taxiing, [.. ids.Select(id => new CatalogQuickCommandEntry(id, null))]);
        var window = new SettingsWindow();
        window.SelectSection(SettingsSectionId.QuickCommands);
        Show(window, null);
        Settle(window);
        QuickCommandsSection section = Assert.IsType<QuickCommandsSection>(window.SectionView(SettingsSectionId.QuickCommands));
        ListBox situations = section.FindControl<ListBox>("SituationList")!;
        situations.SelectedItem = situations.Items.OfType<QuickCommandSituationRow>().Single(row => row.Situation == AircraftSituation.Taxiing);
        Settle(window);
        ItemsControl entries = section.FindControl<ItemsControl>("EntryList")!;
        List<QuickCommandEntryRow> rows = [.. entries.Items.OfType<QuickCommandEntryRow>()];
        Border handle = entries.ContainerFromItem(rows[0])!.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("drag-handle"));
        Point from = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
        // The lower half of the second row: the drop lands after it.
        Control second = entries.ContainerFromItem(rows[1])!;
        Point to = second.TranslatePoint(new Point(second.Bounds.Width / 2, second.Bounds.Height * 0.75), window)!.Value;
        var viewModel = new MainViewModel(new FakeFilePickerService());
        Func<IReadOnlyList<Window>> previousWindows = AutomationTools.OpenWindows;
        AutomationTools.OpenWindows = () => [window];
        try
        {
            using AutomationHost host = StartHost(
                () => Windows,
                () => null,
                () => new AutomationTools(viewModel, new MainViewModelAutomationState(viewModel))
            );
            await using AutomationPipeTestClient client = await Connect();
            var arguments = new
            {
                window = nameof(SettingsWindow),
                fromX = from.X,
                fromY = from.Y,
                toX = to.X,
                toY = to.Y,
                button = "left",
                steps = 4,
                holdMs = 0,
            };

            JsonElement result = Result(await Send(client, ProtocolMethods.CallAppTool, new { tool = "drag", arguments }));

            Assert.True(result.GetProperty("available").GetBoolean(), result.GetRawText());
            // The section rebuilds its list through a posted refresh, so the drop shows only once the dispatcher has run it.
            Settle(window);
            string?[] order = [.. entries.Items.OfType<QuickCommandEntryRow>().Select(row => row.CatalogId)];
            Assert.True(
                order.SequenceEqual([ids[1], ids[0], ids[2]]),
                $"{result.GetProperty("message").GetString()} Stored: {string.Join(", ", ids)}. Order: {string.Join(", ", order)}."
            );
        }
        finally
        {
            AutomationTools.OpenWindows = previousWindows;
            // Closing saves the window's geometry through its own preferences, which still hold this test's list: it must land in
            // the scope's file, so the window closes here rather than in the fixture's Dispose, after the scope has ended.
            window.Close();
            Windows.Remove(window);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task GroundRunwayMenu_HoverPreviewsRoute()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundNodeDto start = MenuGoldenFixtures.OakLayoutForClient.Nodes.First(node => (node.Type == "Spot") && (node.Name == "I30"));
        var main = new MainViewModel(new FakeFilePickerService());
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);
        main.Aircraft.Clear();
        var aircraft = new AircraftModel
        {
            Callsign = "AAL202",
            AircraftType = "B738",
            FlightRules = "IFR",
            IsOnGround = true,
            CurrentPhase = "At Parking",
            Position = new LatLon(start.Latitude, start.Longitude),
        };
        main.Aircraft.Add(aircraft);
        main.Ground.SelectedAircraft = aircraft;
        var view = new GroundView { DataContext = main.Ground };
        var viewHost = new Grid { DataContext = main };
        viewHost.Children.Add(view);
        ShowWindow("GroundWindow", viewHost, null);
        var id = RunwayIdentifier.Parse("28R/10L");
        GroundRunwayDto runway = Assert.Single(MenuGoldenFixtures.OakLayoutForClient.Runways!, r => RunwayIdentifier.Parse(r.Name) == id);
        var click = new LatLon(
            (runway.Coordinates[0][0] + runway.Coordinates[^1][0]) / 2.0,
            (runway.Coordinates[0][1] + runway.Coordinates[^1][1]) / 2.0
        );
        GroundNodeDto nearest = main.Ground.GetNode(main.Ground.DomainLayout!.FindNearestNode(click)!.Id)!;
        ContextMenu menu = view.BuildRunwaySurfaceMenu(main.Ground, [runway.Name], click, nearest, default)!;
        menu.Open(view);
        Render();
        MenuItem taxiTo = menu.Items.OfType<MenuItem>().Single(item => (item.Header as string) == "Taxi to 28R");
        MenuItem choice = FirstLeaf(taxiTo);
        Assert.Null(main.Ground.PreviewRoute);

        // Headless popups are their own top levels, outside the windows a selector searches, so the hover goes to the item's own.
        WindowPoint at = PointerTargets.CentreOf(choice)!;
        await SyntheticMouse.HoverAsync(at.TopLevel, at.Point, 0, CancellationToken.None);

        Assert.NotNull(main.Ground.PreviewRoute);
        menu.Close();
        Render();
        Assert.Null(main.Ground.PreviewRoute);
    }

    [AvaloniaFact]
    public async Task HoverPipe_WindowPoint_EntersTheElementUnderIt()
    {
        Border pad = Pad();
        int entered = 0;
        pad.PointerEntered += (_, _) => entered++;
        Window window = ShowWindow("PointerWindow", pad, null);
        Point inside = pad.TranslatePoint(new Point(10, 10), window)!.Value;
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement result = Result(
            await Send(
                client,
                ProtocolMethods.Hover,
                new
                {
                    windowSelector = "#PointerWindow",
                    x = inside.X,
                    y = inside.Y,
                    durationMs = 0,
                }
            )
        );

        Assert.Equal(nameof(Border), result.GetProperty("elementType").GetString());
        Assert.Equal("PointerWindow", result.GetProperty("site").GetProperty("window").GetString());
        Assert.Equal(inside.X, result.GetProperty("site").GetProperty("x").GetDouble());
        Assert.Equal(1, entered);
        Assert.True(pad.IsPointerOver);
    }

    [AvaloniaFact]
    public async Task HoverPipe_Selector_HoversTheElementCentre()
    {
        Border pad = Pad();
        Window window = ShowWindow("PointerWindow", pad, null);
        Point centre = pad.TranslatePoint(new Point(60, 40), window)!.Value;
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement result = Result(await Send(client, ProtocolMethods.Hover, new { selector = "#Pad", durationMs = 0 }));

        JsonElement site = result.GetProperty("site");
        Assert.Equal(centre, new Point(site.GetProperty("x").GetDouble(), site.GetProperty("y").GetDouble()));
        Assert.Equal(1, site.GetProperty("renderScaling").GetDouble());
        Assert.True(pad.IsPointerOver);
    }

    [AvaloniaFact]
    public async Task DragPipe_CapturesAndReleasesAtTheTarget()
    {
        Border pad = Pad();
        Window window = ShowWindow("PointerWindow", pad, null);
        List<Point> heldMoves = [];
        Point? releasedAt = null;
        pad.PointerPressed += (_, e) => e.Pointer.Capture(pad);
        pad.PointerMoved += (_, e) =>
        {
            if (e.GetCurrentPoint(pad).Properties.IsLeftButtonPressed)
            {
                heldMoves.Add(e.GetPosition(window));
            }
        };
        pad.PointerReleased += (_, e) => releasedAt = e.GetPosition(window);
        Point from = pad.TranslatePoint(new Point(60, 40), window)!.Value;
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement result = Result(
            await Send(
                client,
                ProtocolMethods.Drag,
                new
                {
                    windowSelector = "#PointerWindow",
                    fromX = from.X,
                    fromY = from.Y,
                    toX = 10,
                    toY = 10,
                    button = "left",
                    steps = 3,
                    holdMs = 0,
                }
            )
        );

        Assert.Equal(nameof(Border), result.GetProperty("elementType").GetString());
        Assert.Equal(3, result.GetProperty("steps").GetInt32());
        Assert.Equal("left", result.GetProperty("button").GetString());
        Assert.Equal(from.X, result.GetProperty("from").GetProperty("x").GetDouble());
        Assert.Equal(3, heldMoves.Count);
        Assert.Equal(new Point(10, 10), releasedAt);
    }

    [AvaloniaFact]
    public async Task ClickPipe_BySelector_ReturnsTheElementCentre()
    {
        Border pad = Pad();
        Window window = ShowWindow("PointerWindow", pad, null);
        Point centre = pad.TranslatePoint(new Point(60, 40), window)!.Value;
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement result = Result(await Send(client, ProtocolMethods.Click, new { selector = "#Pad" }));

        JsonElement site = result.GetProperty("site");
        Assert.Equal("PointerWindow", site.GetProperty("window").GetString());
        Assert.Equal(centre, new Point(site.GetProperty("x").GetDouble(), site.GetProperty("y").GetDouble()));
    }

    [AvaloniaFact]
    public async Task HoverPipe_SelectorCentreOutsideTheClientArea_ReturnsOutOfBounds()
    {
        Border pad = Pad();
        var canvas = new Canvas();
        // The pad's centre lands at x 410, past the 400-DIP client area, as an element scrolled or clipped out of a window does.
        Canvas.SetLeft(pad, 350);
        Canvas.SetTop(pad, 10);
        canvas.Children.Add(pad);
        ShowWindow("PointerWindow", canvas, null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        Error(await Send(client, ProtocolMethods.Hover, new { selector = "#Pad", durationMs = 0 }), AutomationErrorCodes.OutOfBounds);

        Assert.False(pad.IsPointerOver);
    }

    [AvaloniaFact]
    public async Task HoverPipe_SelectorInADisabledWindow_ReturnsElementDisabled()
    {
        Border pad = Pad();
        Window window = ShowWindow("PointerWindow", pad, null);
        window.IsEnabled = false;
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        Error(await Send(client, ProtocolMethods.Hover, new { selector = "#Pad", durationMs = 0 }), AutomationErrorCodes.ElementDisabled);

        Assert.False(pad.IsPointerOver);
    }

    [AvaloniaFact]
    public async Task HoverPipe_WhileADragIsInProgress_ReturnsUnsupportedOperation()
    {
        Border pad = Pad();
        Window window = ShowWindow("PointerWindow", pad, null);
        Point from = pad.TranslatePoint(new Point(60, 40), window)!.Value;
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();
        using var cancel = new CancellationTokenSource();
        Task drag = SyntheticMouse.DragAsync(window, new MouseDrag(from, new Point(10, 10), MouseButton.Left, 2, 10_000), cancel.Token);

        JsonElement error = Error(
            await Send(client, ProtocolMethods.Hover, new { selector = "#Pad", durationMs = 0 }),
            AutomationErrorCodes.UnsupportedOperation
        );

        Assert.Contains("A drag is still in progress", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drag);
        Assert.Null(SyntheticMouse.GestureInProgress(window));
    }

    [AvaloniaTheory]
    [InlineData(ProtocolMethods.Hover, "{\"selector\":\"#Pad\",\"windowSelector\":\"#PointerWindow\",\"x\":1,\"y\":1,\"durationMs\":0}", "selector")]
    [InlineData(ProtocolMethods.Hover, "{\"durationMs\":0}", "selector")]
    [InlineData(ProtocolMethods.Hover, "{\"selector\":\"#Pad\"}", "durationMs")]
    [InlineData(ProtocolMethods.Hover, "{\"selector\":\"#Pad\",\"durationMs\":10001}", "durationMs")]
    [InlineData(ProtocolMethods.Hover, "{\"windowSelector\":\"#PointerWindow\",\"y\":1,\"durationMs\":0}", "x")]
    [InlineData(ProtocolMethods.Hover, "{\"windowSelector\":\"#Pad\",\"x\":1,\"y\":1,\"durationMs\":0}", "windowSelector")]
    [InlineData(ProtocolMethods.Drag, DragParamsMissingToY, "toY")]
    [InlineData(ProtocolMethods.Drag, DragParamsBadButton, "button")]
    [InlineData(ProtocolMethods.Drag, DragParamsNoSteps, "steps")]
    [InlineData(ProtocolMethods.Drag, DragParamsNegativeHold, "holdMs")]
    public async Task PointerPipe_BadParams_ReturnInvalidParamNamingTheParam(string method, string paramsJson, string param)
    {
        ShowWindow("PointerWindow", Pad(), null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement error = Error(await SendRawParams(client, method, paramsJson), AutomationErrorCodes.InvalidParam);

        Assert.Equal(param, error.GetProperty("details").GetProperty("param").GetString());
    }

    [AvaloniaTheory]
    [InlineData(ProtocolMethods.Hover, "{\"windowSelector\":\"#PointerWindow\",\"x\":400,\"y\":10,\"durationMs\":0}")]
    [InlineData(ProtocolMethods.Drag, DragParamsToOutside)]
    public async Task PointerPipe_OffTheClientArea_ReturnsOutOfBounds(string method, string paramsJson)
    {
        int presses = 0;
        Border pad = Pad();
        pad.PointerPressed += (_, _) => presses++;
        ShowWindow("PointerWindow", pad, null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        Error(await SendRawParams(client, method, paramsJson), AutomationErrorCodes.OutOfBounds);

        Assert.Equal(0, presses);
    }

    private const string DragParamsMissingToY =
        "{\"windowSelector\":\"#PointerWindow\",\"fromX\":200,\"fromY\":150,\"toX\":10,\"button\":\"left\",\"steps\":2,\"holdMs\":0}";

    private const string DragParamsBadButton =
        "{\"windowSelector\":\"#PointerWindow\",\"fromX\":200,\"fromY\":150,\"toX\":10,\"toY\":10,\"button\":\"left2\",\"steps\":2,\"holdMs\":0}";

    private const string DragParamsNoSteps =
        "{\"windowSelector\":\"#PointerWindow\",\"fromX\":200,\"fromY\":150,\"toX\":10,\"toY\":10,\"button\":\"left\",\"steps\":0,\"holdMs\":0}";

    private const string DragParamsNegativeHold =
        "{\"windowSelector\":\"#PointerWindow\",\"fromX\":200,\"fromY\":150,\"toX\":10,\"toY\":10,\"button\":\"left\",\"steps\":2,\"holdMs\":-1}";

    private const string DragParamsToOutside =
        "{\"windowSelector\":\"#PointerWindow\",\"fromX\":200,\"fromY\":150,\"toX\":10,\"toY\":300,\"button\":\"left\",\"steps\":2,\"holdMs\":0}";

    /// <summary>Opens <paramref name="parent"/>'s submenu, and its first item's, down to the first item with no submenu.</summary>
    private static MenuItem FirstLeaf(MenuItem parent)
    {
        parent.Open();
        Render();
        MenuItem first = parent.Items.OfType<MenuItem>().First();
        return (first.Items.Count == 0) ? first : FirstLeaf(first);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Render();
    }

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
