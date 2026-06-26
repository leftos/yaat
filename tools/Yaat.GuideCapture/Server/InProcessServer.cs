using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Yaat.Server;

namespace Yaat.GuideCapture.Server;

// Hosts yaat-server in the same process as the capture tool. Picks a free
// loopback TCP port, calls YaatHost.BuildAsync to wire the same WebApplication
// the standalone server uses, and starts/stops the host alongside the capture
// run. The Yaat.Client connects over real SignalR to http://127.0.0.1:<port>,
// matching production wire format byte-for-byte.
internal sealed class InProcessServer : IAsyncDisposable
{
    private WebApplication? _app;

    public string Url { get; private set; } = string.Empty;

    public async Task StartAsync()
    {
        var port = PickFreeLoopbackPort();
        Url = $"http://127.0.0.1:{port}";

        // Development + RequireVatsimAuth=false so the in-process host uses the dev JWT signing key and
        // enables the /auth/dev token issuer — the capture client then mints a session without a VATSIM
        // browser round-trip. (The appsettings.Development.json that would set this lives in the server's
        // output dir, not the capture tool's, so it's passed explicitly here.)
        var args = new[] { "--urls", Url, "--environment", "Development", "--Yaat:Auth:RequireVatsimAuth", "false" };
        _app = await YaatHost.BuildAsync(args);
        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null)
        {
            return;
        }

        try
        {
            await _app.StopAsync();
        }
        finally
        {
            await _app.DisposeAsync();
            _app = null;
        }
    }

    // TcpListener with port 0 returns an OS-assigned free port. Closing the
    // listener immediately leaves the port in TIME_WAIT but still bindable
    // by Kestrel because it sets SO_REUSEADDR. Race with another process
    // grabbing the port is theoretically possible but vanishingly rare on a
    // dev machine.
    private static int PickFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
