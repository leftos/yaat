using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;

namespace Yaat.VTdls.Web;

public partial class MainView : UserControl
{
    private static readonly ILogger Log = SimLog.CreateLogger("Yaat.VTdls.Web.MainView");

    /// <summary>localStorage key used to persist the vTDLS dark-mode preference across reloads.</summary>
    private const string DarkModeStorageKey = "yaat-vtdls-dark-mode";

    private readonly BrowserTdlsTransport _connection = new();
    private readonly Dictionary<string, string> _queryParams;

    public MainView()
    {
        InitializeComponent();

        _queryParams = ParseQuery(App.LocationSearch);

        // Restore the dark-mode preference before the VTdlsView's AttachedToVisualTree
        // fires its ApplyTheme(), so the editor opens in the right palette on the very
        // first render rather than flashing light-then-dark.
        var savedDarkMode = LoadSavedDarkMode();

        var vm = new VTdlsViewModel(_connection, sendCommand: SendCommandAsync, getUserInitials: () => _queryParams.GetValueOrDefault("initials", ""))
        {
            IsDarkMode = savedDarkMode,
        };

        // Flip the Application-wide theme variant so the Avalonia FluentTheme
        // brushes (ComboBox / Button / Border defaults) match the controller's
        // dark-mode preference. Without this the outer chrome stays the
        // Light-default light-grey while only the inner VTdls* themed brushes
        // flip to dark — visible as light dropdowns + invisible buttons.
        ApplyApplicationTheme(savedDarkMode);

        // Persist + propagate any subsequent toggle from the Facility Menu.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VTdlsViewModel.IsDarkMode))
            {
                SaveDarkMode(vm.IsDarkMode);
                ApplyApplicationTheme(vm.IsDarkMode);
            }
        };

        var tdlsView = this.FindControl<UserControl>("TdlsView");
        if (tdlsView is not null)
        {
            tdlsView.DataContext = vm;
        }

        if (HasIdentity())
        {
            SetStatus("Initializing...");
            _ = ConnectAndAutoJoinAsync(vm);
        }
        else
        {
            // The static landing form in wwwroot/index.html gates the WASM
            // load on having all three identity params, so this branch should
            // never run via the normal flow. Recovery hint for the
            // URL-mangled case.
            SetStatus("Missing identity in URL — reload /vtdls/ to fill in CID, initials, and ARTCC.");
        }
    }

    private static bool LoadSavedDarkMode()
    {
        try
        {
            return string.Equals(BrowserStorage.GetItem(DarkModeStorageKey), "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // localStorage can throw under private-browsing modes or strict
            // cookie policies. Fall back to the default (light) and continue;
            // the toggle still works in-session, it just won't persist.
            Log.LogDebug(ex, "Reading {Key} from localStorage failed; defaulting to light", DarkModeStorageKey);
            return false;
        }
    }

    private static void SaveDarkMode(bool value)
    {
        try
        {
            BrowserStorage.SetItem(DarkModeStorageKey, value ? "true" : "false");
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "Writing {Key} to localStorage failed", DarkModeStorageKey);
        }
    }

    private static void ApplyApplicationTheme(bool dark)
    {
        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
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
    /// same-origin, which is what we want when yaat-server hosts /vtdls/).
    /// Override via <c>?server=...</c> for cross-origin cases (mostly dev).
    /// Auto-joins the room bound to the supplied CID, mirroring
    /// Yaat.VStrips.Web's ConnectAndAutoJoinAsync.
    /// </summary>
    private async Task ConnectAndAutoJoinAsync(VTdlsViewModel vm)
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

    private async Task JoinRoomAsync(VTdlsViewModel vm, string roomId, string cid, string initials, string artcc)
    {
        try
        {
            var state = await _connection.JoinRoomAsync(roomId, cid, initials, artcc, ClientKind.VTdls);
            if (state is null)
            {
                Log.LogWarning("JoinRoom {RoomId} returned null state", roomId);
                SetStatus($"JoinRoom {roomId} returned null — room may have ended");
                return;
            }
            Log.LogInformation("Joined {RoomId} as {Cid}/{Initials}; scenario={Scenario}", roomId, cid, initials, state.ScenarioName ?? "(none)");
            SetStatus($"Joined {roomId} ({state.ScenarioName ?? "no scenario"}) — fetching TDLS state...");

            // Pull accessible facilities + auto-select the first facility's
            // config so the editor dropdowns have data. RequestFullTdlsState
            // is fired automatically by SwitchFacilityAsync.
            await vm.RefreshAccessibleFacilitiesAsync();
            var firstFacility = vm.AccessibleFacilities.FirstOrDefault();
            if (firstFacility is not null)
            {
                await vm.SwitchFacilityAsync(firstFacility.FacilityId);
                SetStatus($"Joined {roomId} ({state.ScenarioName ?? "no scenario"}) — TDLS facility {firstFacility.FacilityName}");
            }
            else
            {
                SetStatus($"Joined {roomId} ({state.ScenarioName ?? "no scenario"}) — no TDLS facility accessible");
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "JoinRoom {RoomId} failed", roomId);
            SetStatus($"JoinRoom {roomId} failed: {ex.Message}");
        }
    }
}
