using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;

namespace Yaat.VStrips.Web;

public partial class MainView : UserControl
{
    private static readonly ILogger Log = AppLog.CreateLogger("Yaat.VStrips.Web.MainView");

    private readonly ServerConnection _connection = new();
    private readonly Dictionary<string, string> _queryParams;

    public MainView()
    {
        InitializeComponent();

        _queryParams = ParseQuery(App.LocationSearch);

        var vm = new VStripsViewModel(_connection, sendCommand: SendCommandAsync, preferences: null, autoBootstrapFromScenarioLoaded: true);

        var stripsView = this.FindControl<UserControl>("StripsView");
        if (stripsView is not null)
        {
            stripsView.DataContext = vm;
        }

        if (HasIdentity())
        {
            _ = ConnectAndAutoJoinAsync(vm);
        }
        else
        {
            // No identity in the URL — keep the offline spike fixture so the
            // page renders something demonstrable (used during the spike and
            // by anyone loading the bare URL out of curiosity).
            SeedSpikeFixture(vm);
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

    private bool HasIdentity() => _queryParams.ContainsKey("cid") || _queryParams.ContainsKey("initials") || _queryParams.ContainsKey("artcc");

    private async Task SendCommandAsync(string callsign, string command, string initials)
    {
        if (!_connection.IsConnected)
        {
            return;
        }
        try
        {
            await _connection.SendCommandAsync(callsign, command, initials);
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
            Log.LogInformation("Connecting to {Server}", string.IsNullOrEmpty(serverUrl) ? "(same-origin)" : serverUrl);
            await _connection.ConnectAsync(serverUrl);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Connect failed");
            return;
        }

        if (!string.IsNullOrEmpty(explicitRoomId))
        {
            await JoinRoomAsync(vm, explicitRoomId, cid, initials, artcc);
            return;
        }

        if (string.IsNullOrEmpty(cid))
        {
            Log.LogInformation("No CID supplied; staying connected without a room");
            return;
        }

        try
        {
            var room = await _connection.FindRoomForMyCidAsync(cid);
            if (room is null)
            {
                Log.LogInformation("No active room found for CID {Cid}; will auto-join when one becomes available", cid);
                _connection.RoomAvailableForCid += async roomId =>
                {
                    if (vm.IsConnected)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => JoinRoomAsync(vm, roomId, cid, initials, artcc));
                    }
                };
                return;
            }
            await JoinRoomAsync(vm, room.RoomId, cid, initials, artcc);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Auto-join lookup failed");
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
                return;
            }
            vm.ApplyBayConfig(state.FlightStripsConfig);
            _ = vm.RefreshAccessibleFacilitiesAsync();
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "JoinRoom {RoomId} failed", roomId);
        }
    }

    private static void SeedSpikeFixture(VStripsViewModel vm)
    {
        // ApplyBayConfig posts its work to the dispatcher; subsequent
        // ReconcileItems/ReconcileFullState calls would otherwise race ahead
        // and run before the bays exist (silently dropping the strip
        // ordering). Queue the seed *after* the config so they execute in
        // FIFO order on the same dispatcher.
        vm.ApplyBayConfig(
            new FlightStripsConfigDto(
                FacilityId: "SPIKE",
                FacilityName: "WASM SPIKE",
                Bays: [new StripBayConfigDto("bay-gnd", "GROUND", 2), new StripBayConfigDto("bay-loc", "LOCAL", 2)],
                HasTwoPrinters: false,
                SeparatorsLocked: false
            )
        );

        Dispatcher.UIThread.Post(() =>
        {
            vm.SetConnected(true);
            vm.ReconcileItems([
                new StripItemDto(
                    Id: "spike-1",
                    AircraftId: "UAL238",
                    IsDisconnected: false,
                    Type: StripItemType.DepartureStrip,
                    IsOffset: false,
                    FieldValues: ["UAL238", "", "B738/L", "1234", "5512", "", "240", "KSFO", "DCT BERKS DCT FAIRR", ""]
                ),
                new StripItemDto(
                    Id: "spike-2",
                    AircraftId: "AAL2839",
                    IsDisconnected: false,
                    Type: StripItemType.ArrivalStrip,
                    IsOffset: false,
                    FieldValues: ["AAL2839", "", "A320/L", "5678", "6612", "", "180", "KOAK", "ALWYS3.SFO", ""]
                ),
            ]);
            vm.ReconcileFullState(
                new FlightStripsStateDto(
                    PrinterItems: [],
                    BayItems:
                    [
                        new StripBayContentsDto(
                            "bay-gnd",
                            [
                                ["spike-1"],
                                ["spike-2"],
                            ]
                        ),
                    ],
                    NewItemInPrinter: false,
                    NewItemInArrivalPrinter: false,
                    NewItemInBayId: null,
                    ItemMovedOrCreatedBySessionId: null
                )
            );
        });
    }
}
