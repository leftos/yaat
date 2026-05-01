using Avalonia;
using Avalonia.Browser;
using Microsoft.AspNetCore.SignalR.Client;

namespace Yaat.VStrips.Web;

internal static class Program
{
    private static Task Main(string[] args) => BuildAvaloniaApp().WithInterFont().StartBrowserAppAsync("out");

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>();

    // Compile-time check that SignalR client API surface is reachable under net10.0-browser.
    // Not invoked — just keeps the symbols live so the linker doesn't drop them in dev.
    internal static HubConnection BuildProbe() => new HubConnectionBuilder().WithUrl("http://localhost:5000/hubs/training").Build();
}
