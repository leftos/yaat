using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// Phase B smoke test: server up, client connects, no scenario loaded. Captures
// the "connected, idle" UI state — status bar should show Connected, room list
// should be visible. Validates the in-process server boot and the SignalR
// round-trip end-to-end.
internal sealed class MainWindowConnectedEmptyScene : Scene
{
    public override string Name => "main-window-connected-empty";

    public override Task BeforeWindowAsync(CaptureContext ctx)
    {
        // MainWindow's constructor reads App.AutoConnectTarget and fires a
        // background AutoConnectAsync. Setting it here is the same as passing
        // --autoconnect on the production CLI.
        App.AutoConnectTarget = ctx.ServerUrl;
        return Task.CompletedTask;
    }

    public override Window CreateWindow(CaptureContext ctx) => new MainWindow();

    public override async Task AfterShowAsync(Window window, CaptureContext ctx)
    {
        if (window.DataContext is not MainViewModel vm)
        {
            throw new InvalidOperationException("MainWindow.DataContext is not MainViewModel");
        }

        // AutoConnectAsync retries every 2s up to 30 times. Locally the server
        // is already listening, so the first attempt should succeed in <1s.
        // Cap at 15s to fail fast if SignalR negotiate breaks.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!vm.IsConnected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }

        if (!vm.IsConnected)
        {
            throw new InvalidOperationException($"Failed to connect to {ctx.ServerUrl} within 15s. Status: {vm.StatusText}");
        }
    }
}
