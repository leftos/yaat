using System.Runtime.InteropServices.JavaScript;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Client.Tdls.Services;
using Yaat.Client.Tdls.ViewModels;
using Yaat.Client.ViewModels;
using Yaat.Sim;

namespace Yaat.VTdls.Web;

/// <summary>Browser interop for the tab title — the JS side lives in wwwroot/main.js.</summary>
internal static partial class BrowserTitleInterop
{
    [JSImport("globalThis.yaatSetTitle")]
    internal static partial void SetTitle(string title);
}

public partial class MainView : UserControl
{
    private static readonly ILogger Log = SimLog.CreateLogger("Yaat.VTdls.Web.MainView");

    /// <summary>localStorage key used to persist the vTDLS dark-mode preference across reloads.</summary>
    private const string DarkModeStorageKey = "yaat-vtdls-dark-mode";

    private readonly BrowserTdlsTransport _connection = new();
    private readonly Dictionary<string, string> _queryParams;

    /// <summary>
    /// The arguments of the last room join started (forgotten when the server answers null), so a reconnect can join the
    /// same room again.
    /// </summary>
    private (string RoomId, string Initials, string Artcc)? _lastJoin;

    /// <summary>The wait for a room to become available, while no room has been joined yet.</summary>
    private Action<string>? _roomAvailableHandler;

    public MainView()
    {
        InitializeComponent();

        _queryParams = ParseQuery(App.LocationSearch);

        // Restore the dark-mode preference before the view-model reaches the VTdlsView:
        // the view applies its palette from IsDarkMode when its DataContext is set (its
        // constructor starts it light), so the editor opens in the right palette on the
        // very first render rather than flashing light-then-dark.
        bool savedDarkMode = LoadSavedDarkMode();

        var vm = new VTdlsViewModel(_connection, sendCommand: SendCommandAsync, getUserInitials: () => _queryParams.GetValueOrDefault("initials", ""))
        {
            IsDarkMode = savedDarkMode,
        };

        // Flip the Application-wide theme variant with the controller's dark-mode
        // preference so the page chrome outside the VTdlsView (the status bar's
        // themed brushes in App.axaml) matches it. The VTdlsView pins its own
        // theme variant and colours its controls itself.
        ApplyApplicationTheme(savedDarkMode);

        // Persist + propagate any subsequent toggle from the Facility Menu.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VTdlsViewModel.IsDarkMode))
            {
                SaveDarkMode(vm.IsDarkMode);
                ApplyApplicationTheme(vm.IsDarkMode);
            }
            if (e.PropertyName == nameof(VTdlsViewModel.FacilityId))
            {
                RefreshPageTitle(vm);
            }
        };

        // Reflect the pending-DCL count and facility in the browser tab title
        // ("(2) OAK - vTDLS (YAAT)") so a controller working another window sees
        // new clearance requests land.
        vm.DclItems.CollectionChanged += (_, _) => RefreshPageTitle(vm);

        // A reconnect puts the new connection in the lobby only, so the room is joined again; a scenario load or
        // unload changes the facility page and its items, so the page is fetched again.
        _connection.Reconnected += _ => Dispatcher.UIThread.Post(() => RejoinLastRoom(vm));
        _connection.ScenarioLoaded += () => Dispatcher.UIThread.Post(() => _ = BootstrapFacilityAsync(vm, "Scenario loaded"));
        _connection.ScenarioUnloaded += () => Dispatcher.UIThread.Post(() => _ = BootstrapFacilityAsync(vm, "Scenario unloaded"));

        UserControl? tdlsView = this.FindControl<UserControl>("TdlsView");
        tdlsView?.DataContext = vm;

        if (HasIdentity())
        {
            SetStatus("Initializing...");
            _ = ConnectAndAutoJoinAsync(vm);
        }
        else
        {
            // The static sign-in page in wwwroot/index.html authenticates with VATSIM and fills in the
            // initials/ARTCC params before loading the WASM, so this branch should never run via the
            // normal flow. Recovery hint for the URL-mangled case.
            SetStatus("Missing initials/ARTCC — reload /vtdls/ to sign in and set them.");
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

    private static void ApplyApplicationTheme(bool dark) =>
        Application.Current?.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;

    private static void RefreshPageTitle(VTdlsViewModel vm)
    {
        try
        {
            BrowserTitleInterop.SetTitle(ClientProductTitle.Build(vm.DclItems.Count, vm.FacilityId, "vTDLS", includeYaatSuffix: true));
        }
        catch (Exception ex)
        {
            // A missing/renamed JS hook must never take the TDLS UI down with it.
            Log.LogWarning(ex, "Setting document.title failed");
        }
    }

    private void SetStatus(string text)
    {
        TextBlock? bar = this.FindControl<TextBlock>("StatusBar");
        bar?.Text = text;
    }

    private static Dictionary<string, string> ParseQuery(string search)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(search))
        {
            return dict;
        }
        string trimmed = search.StartsWith('?') ? search[1..] : search;
        foreach (string pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }
            string key = Uri.UnescapeDataString(pair[..eq]);
            string val = Uri.UnescapeDataString(pair[(eq + 1)..]);
            dict[key] = val;
        }
        return dict;
    }

    private bool HasIdentity() =>
        !string.IsNullOrWhiteSpace(_queryParams.GetValueOrDefault("initials")) && !string.IsNullOrWhiteSpace(_queryParams.GetValueOrDefault("artcc"));

    /// <summary>Sends a command and returns the server's result, or null when not connected or the send threw.</summary>
    private async Task<CommandResultDto?> SendCommandAsync(string callsign, string command, string initials)
    {
        if (!_connection.IsConnected)
        {
            return null;
        }
        string resolvedInitials = !string.IsNullOrWhiteSpace(initials) ? initials : _queryParams.GetValueOrDefault("initials", "");
        try
        {
            return await _connection.SendCommandAsync(callsign, command, resolvedInitials);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "SendCommand failed: {Command}", command);
            return null;
        }
    }

    /// <summary>
    /// Live connect path. Server URL defaults to the current origin (yaat-server hosts /vtdls/).
    /// Override via <c>?server=...</c> for cross-origin cases (mostly dev). The VATSIM identity comes
    /// from the same-origin session cookie established by the sign-in page, so the server resolves the
    /// CID itself; we auto-join the room bound to that CID.
    /// </summary>
    private async Task ConnectAndAutoJoinAsync(VTdlsViewModel vm)
    {
        string serverUrl = _queryParams.GetValueOrDefault("server", App.LocationOrigin);
        string initials = _queryParams.GetValueOrDefault("initials", "");
        string artcc = _queryParams.GetValueOrDefault("artcc", "");
        string explicitRoomId = _queryParams.GetValueOrDefault("room", "");

        try
        {
            string what = string.IsNullOrEmpty(serverUrl) ? "(same-origin)" : serverUrl;
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
            await JoinRoomAsync(vm, explicitRoomId, initials, artcc);
            return;
        }

        try
        {
            BrowserTdlsRoomInfoDto? room = await _connection.FindRoomForMyCidAsync();
            if (room is null)
            {
                Log.LogInformation("No active room found yet; will auto-join when one becomes available");
                SetStatus("Connected — no active room yet. Waiting for one to become available...");
                _roomAvailableHandler = async roomId =>
                {
                    if (vm.IsConnected)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => JoinRoomAsync(vm, roomId, initials, artcc));
                    }
                };
                _connection.RoomAvailableForCid += _roomAvailableHandler;
                return;
            }
            SetStatus($"Connected, found room {room.RoomId} ({room.CreatorInitials}) — joining...");
            await JoinRoomAsync(vm, room.RoomId, initials, artcc);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Auto-join lookup failed");
            SetStatus($"Auto-join lookup failed: {ex.Message}");
        }
    }

    private async Task JoinRoomAsync(VTdlsViewModel vm, string roomId, string initials, string artcc)
    {
        // Remembered from the start, so a connection dropped during this very join still rejoins on reconnect.
        _lastJoin = (roomId, initials, artcc);
        try
        {
            BrowserTdlsJoinRoomResultDto? state = await _connection.JoinRoomAsync(roomId, initials, artcc, ClientKind.VTdls);
            if (state is null)
            {
                _lastJoin = null;
                Log.LogWarning("JoinRoom {RoomId} returned null state", roomId);
                SetStatus($"JoinRoom {roomId} returned null — room may have ended");
                return;
            }
            StopWaitingForRoom();
            Log.LogInformation("Joined {RoomId} as {Initials}; scenario={Scenario}", roomId, initials, state.ScenarioName ?? "(none)");
            string joined = $"Joined {roomId} ({state.ScenarioName ?? "no scenario"})";
            SetStatus($"{joined} — fetching TDLS state...");
            await BootstrapFacilityAsync(vm, joined);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "JoinRoom {RoomId} failed", roomId);
            SetStatus($"JoinRoom {roomId} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Puts the page on the facility the position can open now, after a join, a rejoin or a scenario change: the
    /// accessible list is refetched (a scenario change can change it), the page already shown is reloaded so a
    /// configuration changed meanwhile is picked up, another choice is switched to, and with nothing accessible the
    /// page is emptied. <paramref name="statusPrefix"/> leads the status line that reports the outcome.
    /// </summary>
    private async Task BootstrapFacilityAsync(VTdlsViewModel vm, string statusPrefix)
    {
        // The page and status always reflect the latest bootstrap started: after each await, one that a newer
        // bootstrap overtook leaves both alone. A page fetch already under way is dropped by the view-model itself.
        int generation = ++_bootstrapGeneration;
        await vm.RefreshAccessibleFacilitiesAsync();
        if (generation != _bootstrapGeneration)
        {
            return;
        }
        AccessibleFacilityDto? facility = ChooseFacility(vm);
        if (facility is null)
        {
            vm.Clear();
            SetStatus($"{statusPrefix} — no TDLS facility accessible");
            return;
        }
        if (string.Equals(facility.FacilityId, vm.FacilityId, StringComparison.Ordinal))
        {
            await vm.ReloadFacilityAsync();
        }
        else
        {
            await vm.SwitchFacilityAsync(facility.FacilityId);
        }
        if (generation != _bootstrapGeneration)
        {
            return;
        }
        SetStatus($"{statusPrefix} — TDLS facility {facility.FacilityName}");
    }

    /// <summary>Counts facility bootstraps started, so an older one can tell a newer one has overtaken it.</summary>
    private int _bootstrapGeneration;

    /// <summary>Drops the wait for a room to become available, so it cannot start a second join beside a rejoin.</summary>
    private void StopWaitingForRoom()
    {
        if (_roomAvailableHandler is null)
        {
            return;
        }
        _connection.RoomAvailableForCid -= _roomAvailableHandler;
        _roomAvailableHandler = null;
    }

    /// <summary>
    /// The facility a join opens: the one already shown while the position can still open it (a rejoin keeps the
    /// controller's page), else the first leaf facility. A consolidated parent is never the default — its merged
    /// page is an explicit choice, same as on the desktop.
    /// </summary>
    private static AccessibleFacilityDto? ChooseFacility(VTdlsViewModel vm) =>
        vm.AccessibleFacilities.FirstOrDefault(f => string.Equals(f.FacilityId, vm.FacilityId, StringComparison.Ordinal))
        ?? vm.AccessibleFacilities.FirstOrDefault(f => !f.IsConsolidated)
        ?? vm.AccessibleFacilities.FirstOrDefault();

    /// <summary>
    /// Joins the last room again after a reconnect (the join then bootstraps the facility page); nothing to do before a
    /// join has started.
    /// </summary>
    private void RejoinLastRoom(VTdlsViewModel vm)
    {
        if (_lastJoin is not { } last)
        {
            return;
        }
        Log.LogInformation("Reconnected; rejoining {RoomId}", last.RoomId);
        SetStatus($"Reconnected — rejoining room {last.RoomId}...");
        _ = JoinRoomAsync(vm, last.RoomId, last.Initials, last.Artcc);
    }
}
