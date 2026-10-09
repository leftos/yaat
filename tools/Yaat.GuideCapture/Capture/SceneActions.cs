using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Radar;
using Yaat.Sim;

namespace Yaat.GuideCapture.Capture;

// Helpers scenes call from AfterShowAsync to drive MainViewModel state through
// the same code paths the real UI uses (commands + public ViewModel methods).
// Every poll loop pumps the dispatcher so async continuations (SignalR
// callbacks marshalled to UIThread, [ObservableProperty] notifications) run
// before the predicate is re-checked.
internal static class SceneActions
{
    public static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, string description)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }

        if (!predicate())
        {
            throw new TimeoutException($"Timeout waiting for {description} after {timeout.TotalSeconds:0}s.");
        }
    }

    public static Task WaitForConnectionAsync(MainViewModel vm, TimeSpan timeout) =>
        WaitUntilAsync(() => vm.IsConnected, timeout, "SignalR connection");

    // The client logs "Connected to <url>" on connect. The guide never shows
    // the in-process server's loopback URL (its intro docs show the public
    // server), and the URL's port changes every run, so the line is removed
    // from the terminal once it lands.
    public static async Task RemoveConnectLineAsync(MainViewModel vm, string serverUrl, TimeSpan timeout)
    {
        await WaitUntilAsync(() => vm.TerminalEntries.Any(e => IsConnectLine(e, serverUrl)), timeout, "the terminal's connect line");
        foreach (TerminalEntry entry in vm.TerminalEntries.Where(e => IsConnectLine(e, serverUrl)).ToList())
        {
            vm.TerminalEntries.Remove(entry);
        }
        Dispatcher.UIThread.RunJobs();
    }

    // Closes the scenario load report the way the user would. A report with
    // warnings (S3-NCTC-3's) stays open over the tabs until it is closed.
    public static async Task CloseLoadReportAsync(MainViewModel vm, TimeSpan timeout)
    {
        await WaitUntilAsync(() => vm.LoadOverlay.IsComplete, timeout, "the scenario load to complete");
        if (vm.LoadOverlay.IsOpen)
        {
            vm.LoadOverlay.CloseCommand.Execute(null);
        }
        await WaitUntilAsync(() => !vm.LoadOverlay.IsOpen, timeout, "the load report overlay to close");
    }

    public static async Task CreateRoomAsync(MainViewModel vm, TimeSpan timeout)
    {
        await vm.CreateRoomCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.IsInRoom, timeout, "room creation");
    }

    // Opens the top-level menu whose header matches menuHeader (access-key
    // underscores ignored, so "_File" and "File" both find the "_File" item)
    // and waits until every visible item in its dropdown is laid out. The
    // headless platform has no popup windows, so the dropdown opens in the
    // window's overlay layer and CaptureRenderedFrame includes it. Returns the
    // opened item, so a scene can open one of its submenus next.
    public static async Task<MenuItem> OpenMenuAsync(Window window, string menuHeader, TimeSpan timeout)
    {
        Menu menu =
            window.GetLogicalDescendants().OfType<Menu>().FirstOrDefault()
            ?? throw new InvalidOperationException($"{window.GetType().Name} has no Menu.");
        MenuItem item =
            FindItem(menu.Items, menuHeader) ?? throw new InvalidOperationException($"No top-level menu '{menuHeader}' in {window.GetType().Name}.");

        item.Open();
        await WaitUntilAsync(() => IsDropdownLaidOut(item), timeout, $"menu '{menuHeader}' to open");
        ArrangeDropdownHost(window);
        return item;
    }

    // Opens the submenu whose header matches submenuHeader inside an open
    // menu (OpenMenuAsync's result, or another submenu) and waits until its
    // dropdown is laid out; it draws in the overlay layer like its parent.
    // The submenu is placed from its item's window position, which is only
    // right once the parent dropdown's host has been arranged.
    public static async Task<MenuItem> OpenSubmenuAsync(MenuItem parent, string submenuHeader, TimeSpan timeout)
    {
        MenuItem item =
            FindItem(parent.Items, submenuHeader)
            ?? throw new InvalidOperationException($"No submenu '{submenuHeader}' under menu '{parent.Header}'.");
        TopLevel top = TopLevel.GetTopLevel(parent) ?? throw new InvalidOperationException($"Menu '{parent.Header}' is not in a window.");

        item.Open();
        await WaitUntilAsync(() => IsDropdownLaidOut(item), timeout, $"submenu '{submenuHeader}' to open");
        ArrangeDropdownHost(top);
        return item;
    }

    // A dropdown's overlay host takes its position from the overlay layer's
    // next arrange; until then it sits at the window's origin, and so does
    // every window position measured inside it.
    private static void ArrangeDropdownHost(TopLevel top)
    {
        top.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    // Opens the flyout attached to the window's button whose content is
    // buttonContent and waits until its content is laid out. Like a menu's
    // dropdown, the headless flyout opens in the window's overlay layer, so
    // CaptureRenderedFrame includes it.
    public static async Task<Control> OpenButtonFlyoutAsync(Window window, string buttonContent, TimeSpan timeout)
    {
        Button button =
            window.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => (b.Content is string content) && (content == buttonContent))
            ?? throw new InvalidOperationException($"No button '{buttonContent}' in {window.GetType().Name}.");
        Flyout flyout = button.Flyout as Flyout ?? throw new InvalidOperationException($"Button '{buttonContent}' has no Flyout.");
        Control content = flyout.Content as Control ?? throw new InvalidOperationException($"The flyout of button '{buttonContent}' has no content.");

        flyout.ShowAt(button);
        await WaitUntilAsync(
            () => flyout.IsOpen && (TopLevel.GetTopLevel(content) is not null) && content.IsArrangeValid && (content.Bounds.Width > 0),
            timeout,
            $"the flyout of button '{buttonContent}' to open"
        );
        return content;
    }

    // Sends a command to the selected aircraft and returns the first terminal
    // line about that aircraft that answers it: its response, or the warning
    // or error a refused command produces.
    public static async Task<TerminalEntry> SendCommandAsync(MainViewModel vm, string callsign, string command)
    {
        long sentAfter = TerminalEntry.LastSequence;
        vm.CommandText = command;
        await vm.SendCommandCommand.ExecuteAsync(null);
        await WaitUntilAsync(
            () => vm.TerminalEntries.Any(e => IsReply(e, sentAfter, callsign)),
            TimeSpan.FromSeconds(10),
            $"the terminal reply to '{command}' for {callsign}"
        );
        return vm.TerminalEntries.First(e => IsReply(e, sentAfter, callsign));
    }

    // Answers the active-runways prompt a mentor's scenario load opens, the way
    // the mentor would: with the server's guess, or by leaving the room
    // unanswered when the guess does not read. The prompt sits over the tabs
    // and takes the pointer input a scene sends to a view under it.
    public static async Task AnswerActiveRunwaysPromptAsync(MainViewModel vm, TimeSpan timeout)
    {
        if (!vm.ShowActiveRunwaysPrompt)
        {
            return;
        }

        if (vm.ConfirmActiveRunwaysPromptCommand.CanExecute(null))
        {
            await vm.ConfirmActiveRunwaysPromptCommand.ExecuteAsync(null);
        }
        else
        {
            vm.CancelActiveRunwaysPromptCommand.Execute(null);
        }

        await WaitUntilAsync(() => !vm.ShowActiveRunwaysPrompt, timeout, "the active runways prompt to close");
    }

    // Selects the aircraft and opens the menu a right-click on it opens in the
    // shown radar or ground view: the view's own right-click builder
    // (BuildAircraftRightClickMenu), opened on the view's canvas at the
    // aircraft as its right-click handler opens it. A pointer right-click
    // cannot open it in the main window: the headless window's light-dismiss
    // layer takes every press there.
    public static Task<ContextMenu> OpenAircraftMenuAsync(Window window, MainViewModel vm, AircraftModel aircraft, TimeSpan timeout)
    {
        vm.SelectedAircraft = aircraft;
        Dispatcher.UIThread.RunJobs();
        RenderOnce(window);
        string callsign = aircraft.Callsign;
        string what = $"the aircraft menu of {callsign}";
        if (
            (ShownCanvas<RadarCanvas>(window) is { } radarCanvas)
            && (radarCanvas.FindAncestorOfType<RadarView>() is { DataContext: RadarViewModel radarVm } radar)
        )
        {
            ContextMenu menu = radar.BuildAircraftRightClickMenu(radarVm, aircraft, aircraft, callsign);
            return OpenAtAsync(menu, radarCanvas, aircraft.Position, timeout, what);
        }

        if (
            (ShownCanvas<GroundCanvas>(window) is { } groundCanvas)
            && (groundCanvas.FindAncestorOfType<GroundView>() is { DataContext: GroundViewModel groundVm } ground)
        )
        {
            ContextMenu menu = ground.BuildAircraftRightClickMenu(groundVm, aircraft, aircraft, callsign);
            return OpenAtAsync(menu, groundCanvas, aircraft.Position, timeout, what);
        }

        throw new InvalidOperationException($"{window.GetType().Name} shows neither the radar nor the ground view.");
    }

    // Selects the aircraft and opens the menu a right-click on the ground node
    // opens with it selected (the point menu, by GroundView.BuildNodeContextMenu),
    // on the ground canvas at the node.
    public static Task<ContextMenu> OpenGroundPointMenuAsync(Window window, MainViewModel vm, AircraftModel aircraft, int nodeId, TimeSpan timeout)
    {
        vm.SelectedAircraft = aircraft;
        Dispatcher.UIThread.RunJobs();
        RenderOnce(window);
        GroundCanvas canvas =
            ShownCanvas<GroundCanvas>(window) ?? throw new InvalidOperationException($"{window.GetType().Name} shows no ground view.");
        GroundView ground =
            canvas.FindAncestorOfType<GroundView>()
            ?? throw new InvalidOperationException($"The shown {nameof(GroundCanvas)} is not in a GroundView.");
        GroundNodeDto node = vm.Ground.GetNode(nodeId) ?? throw new InvalidOperationException($"The ground layout has no node {nodeId}.");
        var place = new LatLon(node.Latitude, node.Longitude);
        (float x, float y) = canvas.Viewport.LatLonToScreen(place.Lat, place.Lon);
        ContextMenu menu =
            ground.BuildNodeContextMenu(nodeId, new Point(x, y))
            ?? throw new InvalidOperationException($"The ground view builds no menu for node {nodeId}.");
        return OpenAtAsync(menu, canvas, place, timeout, $"the point menu of {aircraft.Callsign} at node {nodeId}");
    }

    // Opens the menu on the canvas with its top-left corner at position, the
    // pointer moved there as a right-click leaves it, and waits until it is
    // laid out. The headless platform opens it in the window's overlay layer,
    // so CaptureRenderedFrame includes it.
    private static async Task<ContextMenu> OpenAtAsync(ContextMenu menu, MapCanvasBase canvas, LatLon position, TimeSpan timeout, string what)
    {
        Window window = TopLevel.GetTopLevel(canvas) as Window ?? throw new InvalidOperationException($"{canvas.GetType().Name} is not in a window.");
        (float x, float y) = canvas.Viewport.LatLonToScreen(position.Lat, position.Lon);
        Point inWindow =
            canvas.TranslatePoint(new Point(x, y), window)
            ?? throw new InvalidOperationException($"{canvas.GetType().Name} is not in {window.GetType().Name}.");
        window.MouseMove(inWindow);
        menu.PlacementTarget = canvas;
        menu.Placement = PlacementMode.AnchorAndGravity;
        menu.PlacementRect = new Rect(x, y, 1, 1);
        menu.PlacementAnchor = PopupAnchor.TopLeft;
        menu.PlacementGravity = PopupGravity.BottomRight;
        menu.Open(canvas);
        await WaitUntilAsync(() => menu.IsOpen && IsLaidOut(menu) && AreItemsLaidOut(menu.Items), timeout, what);
        ArrangeDropdownHost(window);
        return menu;
    }

    // The window's map canvas of type T that is on screen (a hidden tab's is
    // skipped), or null when the window shows none.
    public static T? ShownCanvas<T>(Window window)
        where T : MapCanvasBase => window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.IsEffectivelyVisible);

    // Adds an aircraft with an ADD command and returns it once the aircraft
    // list holds it. The scenario's aircraft are waited for first, so none of
    // them is taken for the new one.
    public static async Task<AircraftModel> SpawnAsync(MainViewModel vm, string addCommand, TimeSpan timeout)
    {
        await WaitUntilAsync(() => vm.Aircraft.Count > 0, timeout, $"the scenario's aircraft before '{addCommand}'");
        HashSet<string> before = [.. vm.Aircraft.Select(a => a.Callsign)];

        vm.SelectedAircraft = null;
        Dispatcher.UIThread.RunJobs();
        vm.CommandText = addCommand;
        await vm.SendCommandCommand.ExecuteAsync(null);
        await WaitUntilAsync(
            () => vm.Aircraft.Any(a => !before.Contains(a.Callsign)),
            timeout,
            $"the aircraft '{addCommand}' adds to appear in the aircraft list"
        );
        return vm.Aircraft.First(a => !before.Contains(a.Callsign));
    }

    public static AircraftModel Find(MainViewModel vm, string callsign) =>
        vm.Aircraft.FirstOrDefault(a => a.Callsign == callsign) ?? throw new InvalidOperationException($"{callsign} left the aircraft list.");

    // On the ground and in the sim now, not a delayed spawn waiting to appear.
    public static bool IsParked(AircraftModel aircraft) => aircraft.IsOnGround && (!aircraft.IsDelayed);

    // Moves the pointer over the control's centre, as hovering it does. Its
    // tooltip is switched off first so it does not cover the controls around
    // it; a strip button's label row names it instead.
    public static void PointAt(Window window, Control control)
    {
        ToolTip.SetServiceEnabled(control, false);
        window.MouseMove(CenterInWindow(window, control));
        Dispatcher.UIThread.RunJobs();
    }

    // A real left click on the control's centre. A click on a quick-command
    // strip icon closes the headless menu before the icon's flyout shows, so
    // use it only for a control that opens its own popup.
    public static void Click(Window window, Control control)
    {
        Point center = CenterInWindow(window, control);
        window.MouseMove(center);
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    // The quick-command strip's buttons in an open menu, in strip order; each
    // button's Tag is its catalog entry id.
    public static List<Button> StripButtons(ContextMenu menu) => [.. menu.GetVisualDescendants().OfType<Button>().Where(b => b.Tag is string)];

    public static Button StripButton(ContextMenu menu, string entryId) =>
        StripButtons(menu).FirstOrDefault(b => (b.Tag is string id) && (id == entryId))
        ?? throw new InvalidOperationException(
            $"The menu's strip has no '{entryId}' button; it has {string.Join(", ", StripButtons(menu).Select(b => b.Tag))}."
        );

    // Waits until the window's overlay layer (where the headless platform opens
    // menus, flyouts and popups) holds a laid-out control of type T that meets
    // ready, and returns it.
    public static async Task<T> WaitForOverlayAsync<T>(Window window, Func<T, bool> ready, TimeSpan timeout, string description)
        where T : Control
    {
        T? found = null;
        try
        {
            await WaitUntilAsync(
                () =>
                {
                    found = OverlayContent(window).OfType<T>().FirstOrDefault(c => IsLaidOut(c) && ready(c));
                    return found is not null;
                },
                timeout,
                description
            );
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message} {DescribeOverlay(window)}", ex);
        }

        ArrangeDropdownHost(window);
        return found!;
    }

    // Everything the overlay layer shows: its own visual tree, and the content
    // of each open Popup placed in it (MenuPopups adds its pickers to the
    // layer as Popup controls, whose content is hosted apart from them).
    private static IEnumerable<Visual> OverlayContent(Window window)
    {
        if (OverlayLayer.GetOverlayLayer(window) is not { } overlay)
        {
            return [];
        }

        IEnumerable<Visual> popupContent = overlay
            .Children.OfType<Popup>()
            .Where(p => p.IsOpen && (p.Child is not null))
            .SelectMany(p => p.Child!.GetSelfAndVisualDescendants());
        IEnumerable<Visual> hostedPopups = window.GetVisualDescendants().OfType<OverlayPopupHost>().SelectMany(h => h.GetVisualDescendants());
        return overlay.GetVisualDescendants().Concat(popupContent).Concat(hostedPopups);
    }

    // What the window's overlay layer holds, for a control that never appeared there.
    private static string DescribeOverlay(Window window)
    {
        IEnumerable<string> hosted = OverlayLayer.GetOverlayLayer(window)?.Children.Select(DescribeHosted) ?? ["no overlay layer"];
        return $"The overlay layer holds: {string.Join(", ", hosted.DefaultIfEmpty("nothing"))}.";
    }

    // One overlay child: its type, whether it is an open popup, and its first few descendants.
    private static string DescribeHosted(Control hosted)
    {
        string descendants = string.Join(" > ", hosted.GetVisualDescendants().Take(5).Select(d => d.GetType().Name));
        return $"{hosted.GetType().Name}({(hosted as Popup)?.IsOpen}: {descendants})";
    }

    public static bool AreItemsLaidOut(ItemCollection items)
    {
        List<MenuItem> visibleItems = [.. items.OfType<MenuItem>().Where(m => m.IsVisible)];
        return (visibleItems.Count > 0) && visibleItems.All(IsLaidOut);
    }

    // Renders one frame, which brings each map canvas's viewport up to date.
    public static void RenderOnce(Window window)
    {
        using WriteableBitmap? frame = window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
    }

    private static Point CenterInWindow(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
        ?? throw new InvalidOperationException($"{control.GetType().Name} is not in {window.GetType().Name}.");

    private static bool IsLaidOut(Control control) =>
        (TopLevel.GetTopLevel(control) is not null) && control.IsArrangeValid && (control.Bounds.Width > 0);

    public static async Task LoadScenarioAsync(MainViewModel vm, string scenarioPath, TimeSpan timeout)
    {
        string json = await File.ReadAllTextAsync(scenarioPath);
        string displayName = Path.GetFileNameWithoutExtension(scenarioPath);
        await vm.AutoLoadScenarioFromJsonAsync(json, displayName, displayName);
        await WaitUntilAsync(() => vm.HasScenario, timeout, "scenario load");
    }

    // Access-key underscores are ignored, so "_File" and "File" both find the "_File" item.
    private static MenuItem? FindItem(ItemCollection items, string header)
    {
        string wanted = StripAccessKeys(header);
        return items.OfType<MenuItem>().FirstOrDefault(m => (m.Header is string itemHeader) && (StripAccessKeys(itemHeader) == wanted));
    }

    private static bool IsDropdownLaidOut(MenuItem item)
    {
        if (!item.IsSubMenuOpen)
        {
            return false;
        }

        List<MenuItem> visibleItems = [.. item.Items.OfType<MenuItem>().Where(m => m.IsVisible)];
        return (visibleItems.Count > 0) && visibleItems.All(m => (TopLevel.GetTopLevel(m) is not null) && m.IsArrangeValid && (m.Bounds.Width > 0));
    }

    private static bool IsConnectLine(TerminalEntry entry, string serverUrl) =>
        (entry.Kind == TerminalEntryKind.System)
        && (entry.Message.StartsWith("Connected to ", StringComparison.Ordinal))
        && (entry.Message.Contains(serverUrl, StringComparison.Ordinal));

    private static bool IsReply(TerminalEntry entry, long sentAfter, string callsign) =>
        (entry.Sequence > sentAfter)
        && (entry.Kind is TerminalEntryKind.Response or TerminalEntryKind.Warning or TerminalEntryKind.Error)
        && (entry.Callsign == callsign);

    private static string StripAccessKeys(string header) => header.Replace("_", string.Empty, StringComparison.Ordinal);
}
