using Avalonia.Threading;
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
        var deadline = DateTime.UtcNow + timeout;
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

    public static async Task CreateRoomAsync(MainViewModel vm, TimeSpan timeout)
    {
        await vm.CreateRoomCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.IsInRoom, timeout, "room creation");
    }

    public static async Task LoadScenarioAsync(MainViewModel vm, string scenarioPath, TimeSpan timeout)
    {
        var json = await File.ReadAllTextAsync(scenarioPath);
        var displayName = Path.GetFileNameWithoutExtension(scenarioPath);
        await vm.AutoLoadScenarioFromJsonAsync(json, displayName, displayName);
        await WaitUntilAsync(() => vm.HasScenario, timeout, "scenario load");
    }
}
