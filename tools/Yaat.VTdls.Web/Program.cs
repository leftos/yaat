using Avalonia;
using Avalonia.Browser;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Yaat.Client;
using Yaat.Client.Logging;
using Yaat.Sim;

namespace Yaat.VTdls.Web;

internal static class Program
{
    /// <summary>
    /// Args from <c>main.js</c>: <c>[window.location.search,
    /// window.location.origin]</c>. Origin is needed because SignalR's
    /// HubConnectionBuilder.WithUrl rejects relative URLs and yaat-server
    /// hosts /vtdls/ at the same origin as /hubs/training. Stored on
    /// <see cref="App"/> so the root view can decide between live-server
    /// connect (real query string) and the no-identity branch.
    /// </summary>
    private static Task Main(string[] args)
    {
        // Wire SimLog directly to a console-line provider so transport and
        // vTDLS view logs land in the browser DevTools console. Mirrors
        // Yaat.VStrips.Web's bootstrap.
        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new ConsoleLineLoggerProvider());
        });
        SimLog.Initialize(factory);
        App.LocationSearch = args.Length > 0 ? args[0] : "";
        App.LocationOrigin = args.Length > 1 ? args[1] : "";
        return BuildAvaloniaApp().WithInterFont().WithJetBrainsMonoFont().StartBrowserAppAsync("out");
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>();

    // Compile-time check that SignalR client API surface is reachable under
    // net10.0-browser. Not invoked — just keeps the symbols live so the
    // linker doesn't drop them in dev.
    internal static HubConnection BuildProbe() => new HubConnectionBuilder().WithUrl("http://localhost:5000/hubs/training").Build();
}
