using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

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
    // window's overlay layer and CaptureRenderedFrame includes it.
    public static async Task OpenMenuAsync(Window window, string menuHeader, TimeSpan timeout)
    {
        Menu menu =
            window.GetLogicalDescendants().OfType<Menu>().FirstOrDefault()
            ?? throw new InvalidOperationException($"{window.GetType().Name} has no Menu.");
        string wanted = StripAccessKeys(menuHeader);
        MenuItem item =
            menu.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header is string header) && (StripAccessKeys(header) == wanted))
            ?? throw new InvalidOperationException($"No top-level menu '{menuHeader}' in {window.GetType().Name}.");

        item.Open();
        await WaitUntilAsync(() => IsDropdownLaidOut(item), timeout, $"menu '{menuHeader}' to open");
    }

    public static async Task LoadScenarioAsync(MainViewModel vm, string scenarioPath, TimeSpan timeout)
    {
        string json = await File.ReadAllTextAsync(scenarioPath);
        string displayName = Path.GetFileNameWithoutExtension(scenarioPath);
        await vm.AutoLoadScenarioFromJsonAsync(json, displayName, displayName);
        await WaitUntilAsync(() => vm.HasScenario, timeout, "scenario load");
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

    private static string StripAccessKeys(string header) => header.Replace("_", string.Empty, StringComparison.Ordinal);
}
