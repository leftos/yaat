using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;

namespace Yaat.VStrips.Web;

public partial class MainView : UserControl
{
    private static readonly ILogger Log = SimLog.CreateLogger("Yaat.VStrips.Web.MainView");

    private readonly BrowserStripsTransport _connection = new();
    private readonly Dictionary<string, string> _queryParams;

    public MainView()
    {
        InitializeComponent();

        _queryParams = ParseQuery(App.LocationSearch);

        var vm = new VStripsViewModel(
            _connection,
            sendCommand: SendCommandAsync,
            getUserInitials: () => _queryParams.GetValueOrDefault("initials", ""),
            autoBootstrapFromScenarioLoaded: true
        );

        var stripsView = this.FindControl<UserControl>("StripsView");
        if (stripsView is not null)
        {
            stripsView.DataContext = vm;
        }

        if (HasIdentity())
        {
            SetStatus("Initializing...");
            _ = ConnectAndAutoJoinAsync(vm);
        }
        else
        {
            // The static landing form in wwwroot/index.html gates the WASM
            // load on having all three identity params, so this branch
            // should never run via the normal flow. Show a recovery hint
            // for the URL-mangled case.
            SetStatus("Missing identity in URL — reload /vstrips/ to fill in CID, initials, and ARTCC.");
        }
    }

    private void SetStatus(string text)
    {
        var bar = this.FindControl<TextBlock>("StatusBar");
        if (bar is not null)
        {
            bar.Text = text;
        }
    }

    private static Dictionary<string, string> ParseQuery(string search)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(search))
        {
            return dict;
        }
        var trimmed = search.StartsWith('?') ? search[1..] : search;
        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }
            var key = Uri.UnescapeDataString(pair[..eq]);
            var val = Uri.UnescapeDataString(pair[(eq + 1)..]);
            dict[key] = val;
        }
        return dict;
    }

    private bool HasIdentity() =>
        !string.IsNullOrWhiteSpace(_queryParams.GetValueOrDefault("cid"))
        && !string.IsNullOrWhiteSpace(_queryParams.GetValueOrDefault("initials"))
        && !string.IsNullOrWhiteSpace(_queryParams.GetValueOrDefault("artcc"));

    private async Task SendCommandAsync(string callsign, string command, string initials)
    {
        if (!_connection.IsConnected)
        {
            return;
        }
        // VStripsViewModel pulls initials from its UserPreferences argument,
        // which we pass null in WASM (no filesystem). Fall back to whatever
        // the URL query string carried so the server log doesn't show a
        // bare "(from )" on every command. Same identity that's sent on
        // JoinRoom — keeps the controller's two-letter initials consistent
        // across the join + every subsequent action.
        var resolvedInitials = !string.IsNullOrWhiteSpace(initials) ? initials : _queryParams.GetValueOrDefault("initials", "");
        try
        {
            await _connection.SendCommandAsync(callsign, command, resolvedInitials);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "SendCommand failed: {Command}", command);
        }
    }

    /// <summary>
    /// Live connect path. Server URL defaults to relative ("" resolves to the
    /// current origin since SignalR.Client treats an empty/relative URL as
    /// same-origin, which is what we want when yaat-server hosts /vstrips/).
    /// Override via <c>?server=...</c> for cross-origin cases (mostly dev).
    /// Auto-joins the room bound to the supplied CID, mirroring
    /// StandaloneViewModel.TryAutoJoinForCidAsync.
    /// </summary>
    private async Task ConnectAndAutoJoinAsync(VStripsViewModel vm)
    {
        var serverUrl = _queryParams.GetValueOrDefault("server", App.LocationOrigin);
        var cid = _queryParams.GetValueOrDefault("cid", "");
        var initials = _queryParams.GetValueOrDefault("initials", "");
        var artcc = _queryParams.GetValueOrDefault("artcc", "");
        var explicitRoomId = _queryParams.GetValueOrDefault("room", "");

        try
        {
            var what = string.IsNullOrEmpty(serverUrl) ? "(same-origin)" : serverUrl;
            Log.LogInformation("Connecting to {Server}", what);
            SetStatus($"Connecting to {what}...");
            await _connection.ConnectAsync(serverUrl);
            SetStatus($"Connected to {what} — looking up room...");
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Connect failed");
            SetStatus($"Connect failed: {ex.Message}");
            return;
        }

        if (!string.IsNullOrEmpty(explicitRoomId))
        {
            SetStatus($"Joining room {explicitRoomId}...");
            await JoinRoomAsync(vm, explicitRoomId, cid, initials, artcc);
            return;
        }

        if (string.IsNullOrEmpty(cid))
        {
            Log.LogInformation("No CID supplied; staying connected without a room");
            SetStatus("Connected. No CID supplied — pass ?cid=<your VATSIM CID> to auto-join a room.");
            return;
        }

        try
        {
            var room = await _connection.FindRoomForMyCidAsync(cid);
            if (room is null)
            {
                Log.LogInformation("No active room found for CID {Cid}; will auto-join when one becomes available", cid);
                SetStatus($"Connected, CID {cid} — no active room yet. Waiting for one to become available...");
                _connection.RoomAvailableForCid += async roomId =>
                {
                    if (vm.IsConnected)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => JoinRoomAsync(vm, roomId, cid, initials, artcc));
                    }
                };
                return;
            }
            SetStatus($"Connected, found room {room.RoomId} ({room.CreatorInitials}) — joining...");
            await JoinRoomAsync(vm, room.RoomId, cid, initials, artcc);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Auto-join lookup failed");
            SetStatus($"Auto-join lookup failed: {ex.Message}");
        }
    }

    private async Task JoinRoomAsync(VStripsViewModel vm, string roomId, string cid, string initials, string artcc)
    {
        try
        {
            var state = await _connection.JoinRoomAsync(roomId, cid, initials, artcc, ClientKind.VStrips);
            if (state is null)
            {
                Log.LogWarning("JoinRoom {RoomId} returned null state", roomId);
                SetStatus($"JoinRoom {roomId} returned null — room may have ended");
                return;
            }
            var bayCount = state.FlightStripsConfig?.Bays?.Length ?? 0;
            Log.LogInformation(
                "Joined {RoomId} as {Cid}/{Initials}; scenario={Scenario}; facility={Facility}; bays={BayCount}",
                roomId,
                cid,
                initials,
                state.ScenarioName ?? "(none)",
                state.FlightStripsConfig?.FacilityName ?? "(none)",
                bayCount
            );
            SetStatus(
                state.FlightStripsConfig is null
                    ? $"Joined {roomId} ({state.ScenarioName ?? "no scenario"}) — server returned no flight-strips config (no scenario loaded?)"
                    : $"Joined {roomId} ({state.ScenarioName ?? "no scenario"}) — facility {state.FlightStripsConfig.FacilityName}, {bayCount} bay(s)"
            );
            vm.ApplyBayConfig(state.FlightStripsConfig);
            _ = vm.RefreshAccessibleFacilitiesAsync();
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "JoinRoom {RoomId} failed", roomId);
            SetStatus($"JoinRoom {roomId} failed: {ex.Message}");
        }
    }
}
