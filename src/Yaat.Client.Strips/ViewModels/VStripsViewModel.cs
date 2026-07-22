using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Root view-model for the vStrips clone. Subscribes to the server's flight-strip
/// broadcasts (<see cref="IStripsTransport.FlightStripsStateChanged"/> and
/// <see cref="IStripsTransport.StripItemsChanged"/>) and reconciles an observable
/// collection of <see cref="StripBayViewModel"/> + <see cref="StripPrinterViewModel"/>
/// instances. Every user action emits a canonical command via the injected
/// <c>_sendCommand</c> delegate — no new RPCs. The server applies the command and
/// broadcasts the result back, which drives a reconcile pass that reflects the new
/// authoritative state.
///
/// Instance identity is preserved across reconciliation so Avalonia bindings stay
/// stable (no flicker on drag-drop moves by remote clients).
/// </summary>
public partial class VStripsViewModel : ObservableObject
{
    private readonly ILogger _log = SimLog.CreateLogger("VStripsViewModel");

    private readonly IStripsTransport _transport;
    private readonly Func<string, string, string, Task> _sendCommand;
    private readonly Func<string>? _getUserInitials;

    private readonly Dictionary<string, StripItemViewModel> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StripBayViewModel> _baysById = new(StringComparer.Ordinal);

    // Armed when this client dispatches a create command and cleared by the next
    // reconcile that produces a matching new strip, which then gets
    // RequestFocusFirstCell so the rendered FlightStripControl focuses its first
    // editable field. Scopes the auto-focus to locally-created strips — remote/CRC
    // creates never arm a flag. Set BEFORE dispatch: the server broadcast lands on
    // the dispatcher before the await continuation resumes, so a flag set after
    // dispatch would be missed by that broadcast's reconcile.
    private bool _pendingFocusOnNewHalfStrip;
    private bool _pendingFocusOnNewSeparator;
    private bool _pendingFocusOnNewBlankField;

    public ObservableCollection<StripBayViewModel> Bays { get; } = [];
    public StripPrinterViewModel Printer { get; } = new();

    /// <summary>
    /// Tracks the SignalR transport state. Bays render their layout when this
    /// is true; on disconnect the live strip contents (per-rack lists, printer
    /// queue, item dictionary, cached broadcasts) are dumped so a stale view
    /// can't be acted on. The view binds drag/drop, right-click context menus,
    /// and keyboard shortcuts to this flag so a disconnected client is read-
    /// only — no command will dispatch into the void and produce a "command
    /// failed" log noise.
    /// </summary>
    [ObservableProperty]
    private bool _isConnected;

    /// <summary>
    /// Facilities the current position can open strips windows for. Populated
    /// once per scenario load by <see cref="RefreshAccessibleFacilitiesAsync"/>
    /// and drives the facility-switcher popup in the header.
    /// </summary>
    public ObservableCollection<AccessibleFacilityDto> AccessibleFacilities { get; } = [];

    [ObservableProperty]
    private StripBayViewModel? _selectedBay;

    [ObservableProperty]
    private StripItemViewModel? _selectedStrip;

    partial void OnSelectedStripChanged(StripItemViewModel? oldValue, StripItemViewModel? newValue)
    {
        // Keep StripItemViewModel.IsSelected in sync so FlightStripControl can
        // render a highlight ring. Without this, selection state is observable
        // only at the VM level and never flows to the visual.
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }
        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
    }

    [ObservableProperty]
    private string? _facilityId;

    [ObservableProperty]
    private string? _facilityName;

    // ── Current METAR (scoped to the displayed facility's airports) ──

    private IReadOnlyList<string> _latestMetars = [];
    private string[] _facilityAirports = [];

    /// <summary>
    /// Raw METARs for the airports of the facility currently displayed in this
    /// window, ordered to match the facility's airport list (primary first).
    /// Bound by the collapsible METAR bar in the header.
    /// </summary>
    public ObservableCollection<StripMetarEntry> Metars { get; } = [];

    /// <summary>The first/primary airport's METAR — shown on the collapsed bar.</summary>
    public StripMetarEntry? PrimaryMetar => Metars.Count > 0 ? Metars[0] : null;

    /// <summary>True when at least one in-scope METAR is available; hides the bar otherwise.</summary>
    [ObservableProperty]
    private bool _hasMetars;

    /// <summary>True when the METAR bar is expanded to list every in-scope airport.</summary>
    [ObservableProperty]
    private bool _isMetarExpanded;

    [RelayCommand]
    private void ToggleMetarExpanded() => IsMetarExpanded = !IsMetarExpanded;

    [ObservableProperty]
    private bool _separatorsLocked;

    /// <summary>
    /// Zoom scale applied to the racks host (and every strip/rack inside it)
    /// via a LayoutTransformControl. Does NOT affect the header bar. Default
    /// 0.8 — at natural 535-px strip width this renders ~428 px per strip,
    /// which fits two comfortably-sized racks side-by-side on a 1080p screen.
    /// ZoomIn/ZoomOut commands step in 0.1 increments between 0.5 and 1.5.
    /// </summary>
    [ObservableProperty]
    private double _zoomScale = 0.8;

    public string ZoomLabel => $"{(int)Math.Round(ZoomScale * 100)}%";

    partial void OnZoomScaleChanged(double value) => OnPropertyChanged(nameof(ZoomLabel));

    [RelayCommand]
    private void ZoomIn() => ZoomScale = Math.Min(1.5, Math.Round(ZoomScale + 0.1, 2));

    [RelayCommand]
    private void ZoomOut() => ZoomScale = Math.Max(0.5, Math.Round(ZoomScale - 0.1, 2));

    /// <summary>
    /// When true, this VM auto-applies <see cref="IStripsTransport.StripsConfigChanged"/>
    /// events (the student-facility bootstrap path used by the embedded tab
    /// and the standalone app's primary instance). Additional VMs opened for
    /// other facilities set this to false and get their bay config via
    /// <see cref="SwitchFacilityAsync"/>.
    /// </summary>
    private readonly bool _autoBootstrapFromScenarioLoaded;

    public VStripsViewModel(
        IStripsTransport transport,
        Func<string, string, string, Task> sendCommand,
        Func<string>? getUserInitials,
        bool autoBootstrapFromScenarioLoaded = true
    )
    {
        _transport = transport;
        _sendCommand = sendCommand;
        _getUserInitials = getUserInitials;
        _autoBootstrapFromScenarioLoaded = autoBootstrapFromScenarioLoaded;

        _transport.FlightStripsStateChanged += OnFlightStripsStateChanged;
        _transport.StripItemsChanged += OnStripItemsChanged;
        _transport.MetarsChanged += OnMetarsChanged;
        if (_autoBootstrapFromScenarioLoaded)
        {
            _transport.StripsConfigChanged += OnStripsConfigChanged;
        }

        _isConnected = _transport.IsConnected;
        _transport.Connected += () => Dispatcher.UIThread.Post(() => IsConnected = true);
        _transport.Closed += _ => Dispatcher.UIThread.Post(OnConnectionLost);
        _transport.Reconnecting += _ => Dispatcher.UIThread.Post(OnConnectionLost);
        _transport.Reconnected += _ => Dispatcher.UIThread.Post(() => IsConnected = true);
    }

    /// <summary>
    /// Called on the UI thread whenever the SignalR transport drops or starts
    /// reconnecting. Clears the strip lookup, every rack's strip list, and the
    /// printer queue so the on-screen content reflects "no live data" — the
    /// bay layout itself stays so the user still sees the workspace shape.
    /// Cached broadcasts are dropped too: when the connection comes back the
    /// server will re-broadcast scenario + state, so honoring stale snapshots
    /// would just race the fresh ones.
    /// </summary>
    private void OnConnectionLost()
    {
        IsConnected = false;
        _items.Clear();
        SelectedStrip = null;
        foreach (var bay in Bays)
        {
            foreach (var rack in bay.Racks)
            {
                rack.Strips.Clear();
            }
            bay.HasNewItem = false;
        }
        Printer.Queue.Clear();
        _lastReceivedFullState = null;
        _lastReceivedItems = null;
        _latestMetars = [];
        RebuildMetars();
    }

    /// <summary>
    /// External hook for hosts (standalone, embedded tab) that already track
    /// connection/room state and want the strip view-model to reflect it
    /// without waiting for a SignalR transport event. Idempotent.
    /// </summary>
    public void SetConnected(bool connected)
    {
        if (IsConnected == connected)
        {
            return;
        }
        if (connected)
        {
            IsConnected = true;
        }
        else
        {
            OnConnectionLost();
        }
    }

    // ── Bay config bootstrap ─────────────────────────────────────

    /// <summary>
    /// Rebuilds the bay collection from a server-supplied
    /// <see cref="FlightStripsConfigDto"/>. Called from the ScenarioLoaded hook and
    /// from <see cref="ApplyRoomState"/> when the main VM receives a RoomStateDto
    /// during JoinRoom.
    /// </summary>
    // Cache of the most recent server-supplied state / items. Join-existing-room
    // has a race: the server sends the initial strip broadcasts BEFORE
    // JoinRoom returns, so they arrive and reconcile against empty bays
    // (bays aren't populated until ApplyRoomState → ApplyBayConfig runs with
    // the RoomStateDto return value). Caching lets us re-apply the latest
    // broadcast once bays exist so racks/printer populate without requiring
    // the user to trigger a state-broadcast (e.g., "Move to Bay") first.
    private FlightStripsStateDto? _lastReceivedFullState;
    private List<StripItemDto>? _lastReceivedItems;

    public void ApplyBayConfig(FlightStripsConfigDto? config)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Bays.Clear();
            _baysById.Clear();
            SelectedBay = null;
            SelectedStrip = null;

            if (config is null)
            {
                FacilityId = null;
                FacilityName = null;
                SeparatorsLocked = false;
                _facilityAirports = [];
                RebuildMetars();
                _items.Clear();
                Printer.Queue.Clear();
                return;
            }

            FacilityId = config.FacilityId;
            FacilityName = config.FacilityName;
            SeparatorsLocked = config.SeparatorsLocked;

            // Follow the displayed facility: re-scope the METAR bar to its airports.
            _facilityAirports = config.UnderlyingAirports ?? [];
            RebuildMetars();

            foreach (var bayDto in config.Bays)
            {
                var bayVm = new StripBayViewModel(bayDto);
                Bays.Add(bayVm);
                _baysById[bayDto.Id] = bayVm;
            }

            // Initial selection must be an own bay — external bays are
            // push-only drop-zones (see SelectBayAsync).
            var firstOwnBay = Bays.FirstOrDefault(b => !b.IsExternal);
            if (firstOwnBay is not null)
            {
                SelectedBay = firstOwnBay;
                SelectedBay.IsSelected = true;
            }

            // Re-apply any cached broadcasts that arrived before this bay
            // config was in place. Items must go first so ReconcileFullState
            // can resolve their ids into the local VM lookup.
            if (_lastReceivedItems is { } pendingItems)
            {
                ReconcileItems(pendingItems);
            }
            if (_lastReceivedFullState is { } pendingState)
            {
                ReconcileFullState(pendingState);
            }
        });
    }

    // ── Server event handlers ────────────────────────────────────

    private void OnStripsConfigChanged(FlightStripsConfigDto? config)
    {
        if (config is null)
        {
            // Scenario unloaded — drop cached broadcasts so a subsequent load
            // can't replay stale items into a fresh bay layout.
            _lastReceivedFullState = null;
            _lastReceivedItems = null;
            ApplyBayConfig(null);
            return;
        }

        ApplyBayConfig(config);
        _ = RefreshAccessibleFacilitiesAsync();
    }

    private void OnFlightStripsStateChanged(FlightStripsStateDto state)
    {
        _lastReceivedFullState = state;
        Dispatcher.UIThread.Post(() => ReconcileFullState(state));
    }

    private void OnStripItemsChanged(List<StripItemDto> items)
    {
        _lastReceivedItems = items;
        Dispatcher.UIThread.Post(() => ReconcileItems(items));
    }

    private void OnMetarsChanged(IReadOnlyList<string> metars) => Dispatcher.UIThread.Post(() => ApplyMetars(metars));

    /// <summary>
    /// Replaces the cached room METARs and rebuilds the in-scope
    /// <see cref="Metars"/>. Synchronous public seam (mirrors
    /// <see cref="ReconcileItems"/>) — <see cref="OnMetarsChanged"/> marshals to
    /// the UI thread, tests call it directly.
    /// </summary>
    public void ApplyMetars(IReadOnlyList<string> metars)
    {
        _latestMetars = metars;
        RebuildMetars();
    }

    /// <summary>
    /// Rebuilds <see cref="Metars"/> from the latest broadcast, scoped to the
    /// displayed facility's airports. Parses each raw METAR once, de-dupes by
    /// station, and orders the result by the facility's airport list so the
    /// primary airport leads the collapsed bar. When the facility has no
    /// resolvable airports, shows every loaded METAR rather than a blank bar.
    /// Must run on the UI thread.
    /// </summary>
    private void RebuildMetars()
    {
        Metars.Clear();

        var byStation = new Dictionary<string, StripMetarEntry>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<StripMetarEntry>();
        foreach (var raw in _latestMetars)
        {
            var parsed = MetarParser.Parse(raw);
            if (parsed is null)
            {
                continue;
            }
            var entry = new StripMetarEntry(parsed.StationId, raw.Trim());
            if (byStation.TryAdd(parsed.StationId, entry))
            {
                ordered.Add(entry);
            }
        }

        if (_facilityAirports.Length == 0)
        {
            foreach (var entry in ordered)
            {
                Metars.Add(entry);
            }
        }
        else
        {
            foreach (var airport in _facilityAirports)
            {
                if (byStation.TryGetValue(MetarParser.ToIcao(airport), out var entry))
                {
                    Metars.Add(entry);
                }
            }
        }

        // No weather loaded → show the calm/standard default the sim applies, one per facility
        // airport, so the bar matches the desktop METAR panel rather than hiding. Gated on
        // IsConnected so the disconnect path (which also calls RebuildMetars with empty metars)
        // leaves the bar empty instead of fabricating a stale report.
        if (Metars.Count == 0 && IsConnected && _latestMetars.Count == 0 && _facilityAirports.Length > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var airport in _facilityAirports)
            {
                Metars.Add(new StripMetarEntry(MetarParser.ToIcao(airport), DefaultMetar.Build(airport, now)));
            }
        }

        HasMetars = Metars.Count > 0;
        if (!HasMetars)
        {
            IsMetarExpanded = false;
        }
        OnPropertyChanged(nameof(PrimaryMetar));
    }

    // ── Reconciliation ───────────────────────────────────────────

    /// <summary>
    /// Full reconciliation: replaces the item dictionary with whatever the server
    /// currently holds, rebuilds rack contents from <see cref="FlightStripsStateDto.BayItems"/>,
    /// and replaces the printer queue from <see cref="FlightStripsStateDto.PrinterItems"/>.
    /// Used for moves, deletes, and the initial bootstrap.
    /// </summary>
    public void ReconcileFullState(FlightStripsStateDto state)
    {
        // The FlightStripsStateDto doesn't contain full item payloads — just the
        // rack/printer ID ordering. We keep existing StripItemViewModel instances
        // for every id we still see, and drop items that are no longer referenced
        // anywhere. Fresh items arrive via the incremental StripItemsChanged channel.
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in state.PrinterItems)
        {
            referenced.Add(id);
        }
        foreach (var bay in state.BayItems)
        {
            foreach (var rack in bay.ItemIds)
            {
                foreach (var id in rack)
                {
                    referenced.Add(id);
                }
            }
        }

        // Drop items that no longer appear anywhere in the new state.
        var stale = _items.Keys.Where(id => !referenced.Contains(id)).ToList();
        foreach (var id in stale)
        {
            _items.Remove(id);
            if (SelectedStrip?.Id == id)
            {
                SelectedStrip = null;
            }
        }

        // Rebuild bay racks.
        foreach (var bayVm in Bays)
        {
            var contents = state.BayItems.FirstOrDefault(b => b.BayId == bayVm.BayId);
            if (contents is null)
            {
                foreach (var rack in bayVm.Racks)
                {
                    rack.Strips.Clear();
                }
                continue;
            }

            for (var rackIdx = 0; rackIdx < bayVm.Racks.Count; rackIdx++)
            {
                var rackVm = bayVm.Racks[rackIdx];
                if (rackIdx < contents.ItemIds.Length)
                {
                    rackVm.ReplaceAll(contents.ItemIds[rackIdx], _items);
                }
                else
                {
                    rackVm.Strips.Clear();
                }
            }

            bayVm.HasNewItem = state.NewItemInBayId == bayVm.BayId;
        }

        // Printer queue is room-wide on the server, one list across all
        // facilities. ReplaceAll already skips ids it can't resolve in
        // _items, so scoped VMs naturally drop other facilities' printer
        // strips — they never arrive as in-scope items in ReconcileItems.
        Printer.ReplaceAll(state.PrinterItems, _items);
    }

    /// <summary>
    /// Incremental reconciliation: the server sent new or updated <see cref="StripItemDto"/>
    /// payloads. Update existing <see cref="StripItemViewModel"/> instances in place
    /// and create VMs for any previously-unknown ids (e.g. a newly printed departure
    /// strip hasn't been placed in a bay yet).
    /// </summary>
    public void ReconcileItems(IReadOnlyList<StripItemDto> items)
    {
        foreach (var dto in items)
        {
            if (!IsInScope(dto))
            {
                continue;
            }
            if (_items.TryGetValue(dto.Id, out var existing))
            {
                existing.UpdateFromDto(dto);
            }
            else
            {
                var created = new StripItemViewModel(dto);
                if (ConsumePendingFocus(created))
                {
                    created.RequestFocusFirstCell = true;
                }
                _items[dto.Id] = created;
            }
        }
    }

    /// <summary>
    /// Returns true and clears the matching pending-focus flag when <paramref name="created"/>
    /// is the strip this client just created (half-strip, separator, or blank). A flag is
    /// consumed only by a strip of its own category, so a full strip or a remote create of a
    /// different type can't claim another type's pending focus.
    /// </summary>
    private bool ConsumePendingFocus(StripItemViewModel created)
    {
        if (_pendingFocusOnNewHalfStrip && created.IsHalfStrip)
        {
            _pendingFocusOnNewHalfStrip = false;
            return true;
        }
        if (_pendingFocusOnNewSeparator && created.IsSeparator)
        {
            _pendingFocusOnNewSeparator = false;
            return true;
        }
        if (_pendingFocusOnNewBlankField && created.IsBlank)
        {
            _pendingFocusOnNewBlankField = false;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Facility-scope filter. A strip item is in scope when it either lives
    /// in one of this VM's bays (own or linked external — anything in the
    /// bay layout is something we care about rendering/pushing) or comes
    /// from our own facility's printer queue (facility match on the item).
    /// External-bay items are NOT dropped here — the VM tracks them in
    /// <c>_items</c> so drag-drops onto them work; the view refuses to
    /// SELECT external bays (see <see cref="SelectBayAsync"/>), which is
    /// what makes them push-only.
    /// </summary>
    private bool IsInScope(StripItemDto dto)
    {
        if (FacilityId is null)
        {
            return true; // unscoped VM accepts everything (legacy behavior)
        }
        // Accept items whose bay belongs to this facility's bay set, OR
        // whose FacilityId matches exactly. Items with no ownership metadata
        // (both fields empty) are accepted too — used by older tests and by
        // command broadcasts that don't thread the ownership through.
        var hasBay = !string.IsNullOrEmpty(dto.BayId);
        var hasFacility = !string.IsNullOrEmpty(dto.FacilityId);
        if (!hasBay && !hasFacility)
        {
            return true;
        }
        if (hasBay && _baysById.ContainsKey(dto.BayId))
        {
            return true;
        }
        if (hasFacility && string.Equals(dto.FacilityId, FacilityId, StringComparison.Ordinal))
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Switches this VM to a different accessible facility in place. Fetches
    /// the new facility's bay layout via
    /// <see cref="IStripsTransport.GetFlightStripsConfigForFacilityAsync"/>,
    /// then drives <see cref="ApplyBayConfig"/> with the result. The server
    /// rejects out-of-scope facility ids (returns null), in which case we
    /// leave the current config untouched.
    /// </summary>
    public async Task SwitchFacilityAsync(string facilityId)
    {
        try
        {
            var config = await _transport.GetFlightStripsConfigForFacilityAsync(facilityId);
            if (config is null)
            {
                _log.LogWarning("SwitchFacilityAsync: server refused facility {FacilityId} (not in accessible set)", facilityId);
                return;
            }
            ApplyBayConfig(config);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SwitchFacilityAsync failed for {FacilityId}", facilityId);
        }
    }

    /// <summary>
    /// Populates <see cref="AccessibleFacilities"/> for the current student
    /// position. Called once on scenario load (via
    /// <see cref="OnStripsConfigChanged"/>) and any time the caller explicitly
    /// refreshes. Swallows errors — an empty list just means the switcher
    /// popup has no entries.
    /// </summary>
    public async Task RefreshAccessibleFacilitiesAsync()
    {
        try
        {
            var list = await _transport.GetAccessibleFacilitiesAsync();
            Dispatcher.UIThread.Post(() =>
            {
                AccessibleFacilities.Clear();
                foreach (var f in list)
                {
                    AccessibleFacilities.Add(f);
                }
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "RefreshAccessibleFacilitiesAsync failed");
        }
    }

    // ── Commands (every action builds a canonical and dispatches it) ──

    [RelayCommand]
    public async Task SelectBayAsync(StripBayViewModel bay)
    {
        // External bays are push-only drop-zones, never viewable — their
        // strips live on the owning facility's own window. Clicking one is
        // a no-op so the current selection (an own bay) keeps driving the
        // main rack area. See docs/crc/vstrips.md: "external bays cannot be
        // selected for viewing".
        if (bay.IsExternal)
        {
            return;
        }

        if (SelectedBay is { } prior)
        {
            prior.IsSelected = false;
        }
        SelectedBay = bay;
        bay.IsSelected = true;
        await Task.CompletedTask;
    }

    [RelayCommand]
    public async Task NextBayAsync()
    {
        var target = CycleBay(forward: true);
        if (target is not null)
        {
            await SelectBayAsync(target);
        }
    }

    [RelayCommand]
    public async Task PreviousBayAsync()
    {
        var target = CycleBay(forward: false);
        if (target is not null)
        {
            await SelectBayAsync(target);
        }
    }

    /// <summary>
    /// Moves <see cref="SelectedStrip"/> to an adjacent strip inside the
    /// currently-selected bay. Up/Down move within the same rack; Left/Right
    /// jump to the adjacent rack and keep the vertical slot (clamped to that
    /// rack's strip count). With no current selection, Down picks the first
    /// strip in the first rack so the keyboard alone can enter selection mode.
    /// </summary>
    public void SelectAdjacentStrip(NavDirection direction)
    {
        if (SelectedBay is null || SelectedBay.Racks.Count == 0)
        {
            return;
        }

        if (SelectedStrip is null)
        {
            for (var r = 0; r < SelectedBay.Racks.Count; r++)
            {
                if (SelectedBay.Racks[r].Strips.Count > 0)
                {
                    SelectedStrip = SelectedBay.Racks[r].Strips[0];
                    return;
                }
            }
            return;
        }

        // Find the current strip's rack and index.
        var curRack = -1;
        var curIdx = -1;
        for (var r = 0; r < SelectedBay.Racks.Count; r++)
        {
            var idx = SelectedBay.Racks[r].Strips.IndexOf(SelectedStrip);
            if (idx >= 0)
            {
                curRack = r;
                curIdx = idx;
                break;
            }
        }
        if (curRack < 0)
        {
            return;
        }

        switch (direction)
        {
            case NavDirection.Up:
                if (curIdx > 0)
                {
                    SelectedStrip = SelectedBay.Racks[curRack].Strips[curIdx - 1];
                }
                break;
            case NavDirection.Down:
                if (curIdx + 1 < SelectedBay.Racks[curRack].Strips.Count)
                {
                    SelectedStrip = SelectedBay.Racks[curRack].Strips[curIdx + 1];
                }
                break;
            case NavDirection.Left:
            case NavDirection.Right:
            {
                var step = direction == NavDirection.Right ? 1 : -1;
                var nextRack = curRack + step;
                if (nextRack < 0 || nextRack >= SelectedBay.Racks.Count)
                {
                    return;
                }
                var nextStrips = SelectedBay.Racks[nextRack].Strips;
                if (nextStrips.Count == 0)
                {
                    return;
                }
                SelectedStrip = nextStrips[Math.Min(curIdx, nextStrips.Count - 1)];
                break;
            }
        }
    }

    /// <summary>
    /// Move the currently-selected strip by one slot in the given direction —
    /// keyboard equivalent of a drag-drop. Up/Down shifts within the rack,
    /// Left/Right pushes to the adjacent rack at the same index.
    /// </summary>
    public async Task MoveSelectedStripAsync(NavDirection direction)
    {
        if (SelectedStrip is null || SelectedBay is null)
        {
            return;
        }

        // Find current position.
        var curRack = -1;
        var curIdx = -1;
        for (var r = 0; r < SelectedBay.Racks.Count; r++)
        {
            var idx = SelectedBay.Racks[r].Strips.IndexOf(SelectedStrip);
            if (idx >= 0)
            {
                curRack = r;
                curIdx = idx;
                break;
            }
        }
        if (curRack < 0)
        {
            return;
        }

        int destRack = curRack,
            destIdx = curIdx;
        switch (direction)
        {
            case NavDirection.Up:
                destIdx = Math.Max(0, curIdx - 1);
                break;
            case NavDirection.Down:
                destIdx = curIdx + 1;
                break;
            case NavDirection.Left:
                destRack = curRack - 1;
                break;
            case NavDirection.Right:
                destRack = curRack + 1;
                break;
        }
        if (destRack < 0 || destRack >= SelectedBay.Racks.Count)
        {
            return;
        }
        await MoveStripAsync(SelectedStrip, SelectedBay, destRack, destIdx);
    }

    /// <summary>
    /// Walk <see cref="Bays"/> from the current selection, skipping external
    /// bays (push-only, not viewable). Returns the next (or previous, when
    /// <paramref name="forward"/> is false) own bay, or null if no own bays
    /// exist at all. Wraps around.
    /// </summary>
    private StripBayViewModel? CycleBay(bool forward)
    {
        if (Bays.Count == 0)
        {
            return null;
        }
        var step = forward ? 1 : -1;
        var start = SelectedBay is null ? 0 : Bays.IndexOf(SelectedBay);
        for (var i = 1; i <= Bays.Count; i++)
        {
            var idx = ((start + step * i) % Bays.Count + Bays.Count) % Bays.Count;
            if (!Bays[idx].IsExternal)
            {
                return Bays[idx];
            }
        }
        return null;
    }

    /// <summary>
    /// Move a strip into a bay/rack/index. Callsign picked by strip type.
    /// A null <paramref name="index"/> means "append to the tail of the rack"
    /// (CRC bottom-up first-available slot) — passed through as a missing
    /// index token on the STRIP wire, interpreted server-side.
    /// </summary>
    public async Task MoveStripAsync(StripItemViewModel strip, StripBayViewModel destBay, int rack, int? index)
    {
        // No-op guard: if the strip already sits at the target slot — or the
        // target is "one slot above" it in the same rack (remove-then-insert
        // lands it right back at fromIdx) — skip the canonical dispatch
        // entirely. Without this, a drag that releases on the dragged strip's
        // own slot still emits a STRIP command — harmless server-side but it
        // echoes in the command log and terminal buffer as noise. Append
        // (null index) can't short-circuit without predicting server placement.
        if (index is int explicitIdx && IsNoOpMove(strip, destBay, rack, explicitIdx))
        {
            return;
        }

        // HSM / SEP / BLANK all take an explicit index; the "append" affordance
        // is STRIP-only for now. Default null → 0 for the other builders.
        var indexOrZero = index ?? 0;
        var canonical = strip.Type switch
        {
            StripItemType.DepartureStrip or StripItemType.ArrivalStrip => VStripsCanonicalBuilder.BuildStripMoveById(
                strip.Id,
                destBay.FacilityId,
                destBay.Name,
                rack,
                index
            ),
            StripItemType.HalfStripLeft or StripItemType.HalfStripRight => VStripsCanonicalBuilder.BuildHalfStripMove(
                strip.Id,
                destBay.FacilityId,
                destBay.Name,
                rack,
                indexOrZero
            ),
            StripItemType.HandwrittenSeparator or StripItemType.WhiteSeparator or StripItemType.RedSeparator or StripItemType.GreenSeparator =>
                VStripsCanonicalBuilder.BuildSeparatorMove(strip.Id, destBay.FacilityId, destBay.Name, rack, indexOrZero),
            StripItemType.BlankStrip => VStripsCanonicalBuilder.BuildBlankCreate(destBay.FacilityId, destBay.Name, rack, indexOrZero),
            _ => null,
        };

        if (canonical is null)
        {
            _log.LogWarning("MoveStripAsync: no canonical mapping for strip type {Type}", strip.Type);
            return;
        }

        // Optimistic local move so the strip lands in the target slot the
        // moment the user releases the drag. Without this, the strip flashes
        // back into its original position (the drag-source presenter unhide
        // in HideDragGhost) and only snaps to the new slot once the SignalR
        // round-trip + state broadcast lands — visible as a ~100-1000 ms
        // jump depending on network. BlankStrip is a CREATE, not a move, so
        // it has no source slot to relocate from.
        if (strip.Type != StripItemType.BlankStrip)
        {
            OptimisticallyMove(strip, destBay, rack, index);
        }

        // Every UI dispatch addresses the strip by id, so the callsign field
        // on the wire is purely informational. Send empty so the server
        // never falls back to callsign-keyed lookup (which would mis-target a
        // scanned copy whose callsign matches the original).
        await _sendCommand("", canonical, _getUserInitials?.Invoke() ?? "");
    }

    /// <summary>
    /// Copy a full strip into an external facility's bay while leaving the
    /// originating strip in place. Aircraft-scoped (callsign required) and
    /// only valid against external bays — non-external destinations are
    /// rejected client-side without dispatching, mirroring the server-side
    /// guard. The destination is the bay's first rack, append-to-tail (the
    /// CRC bottom-up first-available slot) when called from the context
    /// menu submenu; explicit rack/index are supported for terminal use.
    /// </summary>
    public async Task ScanStripAsync(StripItemViewModel strip, StripBayViewModel destBay, int rack, int? index)
    {
        if (!strip.IsFullStrip)
        {
            _log.LogWarning("ScanStripAsync: cannot scan non-full strip {Type}", strip.Type);
            return;
        }

        if (!destBay.IsExternal)
        {
            _log.LogWarning("ScanStripAsync: destination bay {Bay} is not external", destBay.Name);
            return;
        }

        var canonical = VStripsCanonicalBuilder.BuildStripScan(destBay.FacilityId, destBay.Name, rack, index);
        var callsign = strip.AircraftId ?? "";
        await _sendCommand(callsign, canonical, _getUserInitials?.Invoke() ?? "");
    }

    public async Task DeleteStripAsync(StripItemViewModel strip)
    {
        // Every emit form addresses the strip by id so duplicate first-line
        // text or duplicate callsign (scanned copies) round-trip correctly.
        var canonical = strip.Type switch
        {
            StripItemType.DepartureStrip or StripItemType.ArrivalStrip => VStripsCanonicalBuilder.BuildStripDeleteById(strip.Id),
            StripItemType.HalfStripLeft or StripItemType.HalfStripRight => VStripsCanonicalBuilder.BuildHalfStripDelete(strip.Id),
            StripItemType.HandwrittenSeparator or StripItemType.WhiteSeparator or StripItemType.RedSeparator or StripItemType.GreenSeparator =>
                VStripsCanonicalBuilder.BuildSeparatorDeleteById(strip.Id),
            StripItemType.BlankStrip => VStripsCanonicalBuilder.BuildBlankDeleteById(strip.Id),
            _ => null,
        };

        if (canonical is null)
        {
            _log.LogWarning("DeleteStripAsync: no canonical mapping for strip type {Type}", strip.Type);
            return;
        }

        await _sendCommand("", canonical, _getUserInitials?.Invoke() ?? "");
    }

    public async Task ToggleOffsetAsync(StripItemViewModel strip)
    {
        var canonical = strip.Type switch
        {
            StripItemType.DepartureStrip or StripItemType.ArrivalStrip => VStripsCanonicalBuilder.BuildStripOffsetById(strip.Id),
            StripItemType.HalfStripLeft or StripItemType.HalfStripRight => VStripsCanonicalBuilder.BuildHalfStripOffset(strip.Id),
            _ => null,
        };

        if (canonical is null)
        {
            return;
        }

        await _sendCommand("", canonical, _getUserInitials?.Invoke() ?? "");
    }

    /// <summary>
    /// Edits an annotation slot on a full strip — emitted by id so a scanned
    /// copy is annotated independently of its originator. <paramref name="box"/>
    /// is the canonical slot id — <c>"1"</c>..<c>"9"</c> for the 3×3 grid, or
    /// <c>"8a"</c>/<c>"8b"</c> for the col-3 freeform slots below field 8.
    /// </summary>
    public async Task AnnotateAsync(StripItemViewModel strip, string box, string? text)
    {
        if (!strip.IsFullStrip)
        {
            return;
        }

        var canonical = VStripsCanonicalBuilder.BuildAnnotateById(strip.Id, box, text);
        await _sendCommand("", canonical, _getUserInitials?.Invoke() ?? "");
    }

    /// <summary>
    /// Replace the lines of an existing half-strip. Wraps the
    /// <c>HSA {key} {line1} {line2}…</c> canonical verb. Empty lines are
    /// preserved so users can blank a line without collapsing the strip.
    /// </summary>
    public async Task AmendHalfStripAsync(StripItemViewModel strip, IReadOnlyList<string> lines)
    {
        if (!strip.IsHalfStrip)
        {
            return;
        }
        var canonical = VStripsCanonicalBuilder.BuildHalfStripAmend(strip.Id, lines);
        await _sendCommand("", canonical, _getUserInitials?.Invoke() ?? "");
    }

    /// <summary>
    /// Replace a half-strip's full FieldValues array by stripId. Used by
    /// the inline 3×2 cell grid — when a cell loses focus the view assembles
    /// the current slot values (with the edit applied) and dispatches HSE
    /// so empty cells are preserved without ambiguity around FieldValues[0]
    /// being the lookup key.
    /// </summary>
    public async Task EditHalfStripFieldsAsync(StripItemViewModel strip, IReadOnlyList<string> slots)
    {
        if (!strip.IsHalfStrip || string.IsNullOrEmpty(strip.Id))
        {
            return;
        }
        var canonical = VStripsCanonicalBuilder.BuildHalfStripEdit(strip.Id, slots);
        await _sendCommand("", canonical, _getUserInitials?.Invoke() ?? "");
    }

    /// <summary>
    /// Rename a separator atomically via the server-side SEPE command,
    /// addressed by stripId so the dispatch survives concurrent moves
    /// between the inline edit and the server round-trip. Locked
    /// facilities (SeparatorsLocked=true) drop this call to match the CRC
    /// constraint that only handwritten separators can be edited.
    /// </summary>
    public async Task EditSeparatorLabelAsync(StripItemViewModel strip, string newLabel)
    {
        if (!strip.IsSeparator || string.IsNullOrEmpty(strip.Id))
        {
            return;
        }
        if (SeparatorsLocked && strip.Type != StripItemType.HandwrittenSeparator)
        {
            return;
        }

        var canonical = VStripsCanonicalBuilder.BuildSeparatorEditById(strip.Id, newLabel);
        await _sendCommand("", canonical, _getUserInitials?.Invoke() ?? "");
    }

    public async Task SlideHalfStripAsync(StripItemViewModel strip)
    {
        if (!strip.IsHalfStrip)
        {
            return;
        }
        var canonical = VStripsCanonicalBuilder.BuildHalfStripSlide(strip.Id);
        await _sendCommand("", canonical, _getUserInitials?.Invoke() ?? "");
    }

    public async Task CreateHalfStripAsync(StripBayViewModel bay, int rack, IReadOnlyList<string> lines)
    {
        // Arm focus before dispatch: the new strip round-trips back via ReconcileItems,
        // which runs on the dispatcher before this await resumes, so a flag set afterward
        // would be missed by that reconcile.
        _pendingFocusOnNewHalfStrip = true;
        var canonical = VStripsCanonicalBuilder.BuildHalfStripCreate(bay.FacilityId, bay.Name, rack, lines);
        await _sendCommand("", canonical, _getUserInitials?.Invoke() ?? "");
    }

    public async Task CreateSeparatorAsync(SeparatorStyle style, StripBayViewModel bay, int rack, int? index, string? label)
    {
        if (SeparatorsLocked)
        {
            return;
        }
        _pendingFocusOnNewSeparator = true;
        var canonical = VStripsCanonicalBuilder.BuildSeparatorCreate(style, bay.FacilityId, bay.Name, rack, index, label);
        await _sendCommand("", canonical, _getUserInitials?.Invoke() ?? "");
    }

    public async Task CreateBlankAsync(StripBayViewModel? bay, int? rack, int? index)
    {
        _pendingFocusOnNewBlankField = true;
        var canonical = VStripsCanonicalBuilder.BuildBlankCreate(bay?.FacilityId, bay?.Name, rack, index);
        await _sendCommand("", canonical, _getUserInitials?.Invoke() ?? "");
    }

    /// <summary>
    /// Prints a blank strip directly into the printer queue. Matches the CRC
    /// "Print Blank Strip" button in docs/crc/img/printer.png — blanks go to
    /// the printer first, from where users drag them into racks. The
    /// departure carousel jumps to the newly-printed blank on the next
    /// reconcile so the user sees what they just printed without arrowing
    /// through the queue.
    /// </summary>
    public async Task PrintBlankStripAsync()
    {
        await CreateBlankAsync(bay: null, rack: null, index: null);
        Printer.RequestFocusOnNewBlank();
    }

    /// <summary>
    /// Printer-modal "Request Strip" button — invokes the server's
    /// idempotent <c>RequestFlightStripForAircraft</c> hub method. Logs the
    /// outcome (success and failure) so controllers see feedback in the
    /// terminal log without blocking on a modal dialog. Idempotent on the
    /// server, so rapid clicks won't print duplicates.
    /// </summary>
    public async Task RequestStripAsync(string aircraftId)
    {
        if (string.IsNullOrWhiteSpace(aircraftId))
        {
            return;
        }

        var trimmed = aircraftId.Trim();
        try
        {
            var result = await _transport.RequestFlightStripForAircraftAsync(trimmed);
            if (result.Success)
            {
                _log.LogInformation("RequestStripAsync({Aircraft}): {Message}", trimmed, result.Message);
                // Bring the new strip into view immediately — CRC parity so the
                // user doesn't have to scroll the carousel to find what they
                // just printed. Mark as pending in case the broadcast hasn't
                // landed yet; the next ReplaceAll will honour it.
                Printer.RequestFocusOnCallsign(trimmed);
            }
            else
            {
                _log.LogWarning("RequestStripAsync({Aircraft}) failed: {Message}", trimmed, result.Message);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "RequestStripAsync({Aircraft}) RPC threw", trimmed);
        }
    }

    /// <summary>
    /// Moves the visible printer strip (for the given queue kind) into
    /// <see cref="SelectedBay"/>'s rack 0 index 0. Matches CRC's "Move to Bay"
    /// button in docs/crc/img/printer.png. Resolves the visible strip from
    /// <see cref="StripPrinterViewModel"/> carousel pointers so users stage
    /// strips one at a time.
    /// </summary>
    public async Task MoveVisiblePrinterStripToBayAsync(PrinterQueueKind kind)
    {
        if (SelectedBay is null)
        {
            return;
        }
        var strip = kind switch
        {
            PrinterQueueKind.Departure => Printer.VisibleDepartureStrip,
            PrinterQueueKind.Arrival => Printer.VisibleArrivalStrip,
            _ => Printer.VisibleDepartureStrip,
        };
        if (strip is null)
        {
            return;
        }
        // Index null → server appends to the tail of rack 0 (CRC bottom-up
        // FIFO: first strip placed lands at the visual bottom, each new
        // arrival stacks above). The canonical emitted is "STRIP <bay> 1"
        // (no index token).
        await MoveStripAsync(strip, SelectedBay, rack: 0, index: null);
    }

    /// <summary>
    /// Moves every strip in the <paramref name="kind"/> printer section
    /// (departures or arrivals) to the first rack of <see cref="SelectedBay"/>,
    /// ordered alphabetically by callsign so the visual top-to-bottom stack
    /// reads in callsign order. Blanks (no callsign) sort to the bottom of the
    /// list. Each strip is dispatched by id via <see cref="MoveStripAsync"/>
    /// (append to tail), so arrival strips (<c>ARRIVAL_</c> ids) and scanned
    /// copies target the right strip and the server never synthesizes a phantom
    /// departure strip (issue #278). Dispatched in reverse alphabetical order so
    /// successive appends stack ascending top-to-bottom on screen.
    /// </summary>
    public async Task MoveAllPrinterStripsToBayAsync(PrinterQueueKind kind)
    {
        if (SelectedBay is null)
        {
            return;
        }
        // Snapshot the section's queue so the dispatch loop sees a stable list
        // even as the server's broadcasts (and the optimistic moves below)
        // mutate the printer collections mid-flight.
        var source = kind == PrinterQueueKind.Arrival ? Printer.ArrivalQueue : Printer.DepartureQueue;
        var pending = source.ToList();
        if (pending.Count == 0)
        {
            return;
        }
        var sorted = pending.OrderBy(s => s.AircraftId ?? "~", StringComparer.OrdinalIgnoreCase).ToList();
        for (var i = sorted.Count - 1; i >= 0; i--)
        {
            var strip = sorted[i];
            if (!strip.IsFullStrip)
            {
                continue; // half-strips and separators don't sit in the printer
            }
            await MoveStripAsync(strip, SelectedBay, rack: 0, index: null);
        }
    }

    /// <summary>
    /// Deletes the visible printer strip (for the given queue kind). Mirrors
    /// the "Delete" button next to the carousel in docs/crc/img/printer.png.
    /// </summary>
    public async Task DeleteVisiblePrinterStripAsync(PrinterQueueKind kind)
    {
        var strip = kind switch
        {
            PrinterQueueKind.Departure => Printer.VisibleDepartureStrip,
            PrinterQueueKind.Arrival => Printer.VisibleArrivalStrip,
            _ => Printer.VisibleDepartureStrip,
        };
        if (strip is null)
        {
            return;
        }
        await DeleteStripAsync(strip);
    }

    /// <summary>
    /// Dispatch an already-built canonical string with an empty callsign.
    /// Used by the keyboard-shortcut separator style-cycle path where the
    /// client computes both SEPD and SEP commands and wants them to go out
    /// via the same <c>_sendCommand</c> as the rest of the VM operations.
    /// </summary>
    public async Task DispatchRawAsync(string canonical)
    {
        await _sendCommand("", canonical, _getUserInitials?.Invoke() ?? "");
    }

    /// <summary>
    /// True when sending a STRIP move at <paramref name="index"/> in
    /// <paramref name="destBay"/>.Racks[<paramref name="rack"/>] would leave
    /// the strip in its current position — the server's remove-then-insert
    /// lands the strip back where it was. The view's drop-index math returns
    /// the post-move visual position (== post-move model index in bottom-up
    /// rendering), so a drop whose target equals the strip's current model
    /// index is a no-op. Used by <see cref="MoveStripAsync"/> to suppress
    /// the canonical dispatch so the terminal buffer and command log don't
    /// echo an already-satisfied move.
    /// </summary>
    private static bool IsNoOpMove(StripItemViewModel strip, StripBayViewModel destBay, int rack, int index)
    {
        if (rack < 0 || rack >= destBay.Racks.Count || index < 0)
        {
            return false;
        }
        var strips = destBay.Racks[rack].Strips;
        return index < strips.Count && ReferenceEquals(strips[index], strip);
    }

    /// <summary>
    /// Apply the same move locally that <see cref="MoveStripAsync"/> is about
    /// to dispatch to the server, so the strip lands in the new slot the
    /// instant the user releases the drag instead of after the SignalR
    /// roundtrip. The server's broadcast lands a moment later and the smart
    /// in-place reconciler in <see cref="StripRackViewModel.ReplaceAll"/>
    /// recognises the resulting state matches and emits no further
    /// CollectionChanged events. Skipped when the strip can't be located in
    /// any bay rack or in the printer queue (defensive — should never fire).
    /// </summary>
    private void OptimisticallyMove(StripItemViewModel strip, StripBayViewModel destBay, int rack, int? index)
    {
        if (rack < 0 || rack >= destBay.Racks.Count)
        {
            return;
        }

        StripRackViewModel? sourceRack = null;
        var sourceIdx = -1;
        var fromPrinter = false;

        var printerIdx = Printer.Queue.IndexOf(strip);
        if (printerIdx >= 0)
        {
            fromPrinter = true;
            sourceIdx = printerIdx;
        }
        else
        {
            foreach (var bay in Bays)
            {
                for (var r = 0; r < bay.Racks.Count; r++)
                {
                    var idx = bay.Racks[r].Strips.IndexOf(strip);
                    if (idx >= 0)
                    {
                        sourceRack = bay.Racks[r];
                        sourceIdx = idx;
                        break;
                    }
                }
                if (sourceRack is not null)
                {
                    break;
                }
            }
        }

        if (sourceRack is null && !fromPrinter)
        {
            return;
        }

        var destRack = destBay.Racks[rack];

        if (fromPrinter)
        {
            Printer.Queue.RemoveAt(sourceIdx);
            var insertIdx = Math.Clamp(index ?? destRack.Strips.Count, 0, destRack.Strips.Count);
            destRack.Strips.Insert(insertIdx, strip);
            return;
        }

        if (ReferenceEquals(sourceRack, destRack))
        {
            // Same rack: Move (single CollectionChanged event, no presenter rebuild).
            // Removing-then-inserting in the same rack would clamp differently
            // than the server's remove-then-insert; clamp to within-current-bounds.
            var moveTarget = Math.Clamp(index ?? destRack!.Strips.Count - 1, 0, destRack!.Strips.Count - 1);
            if (sourceIdx != moveTarget)
            {
                destRack.Strips.Move(sourceIdx, moveTarget);
            }
            return;
        }

        sourceRack!.Strips.RemoveAt(sourceIdx);
        var crossInsertIdx = Math.Clamp(index ?? destRack.Strips.Count, 0, destRack.Strips.Count);
        destRack.Strips.Insert(crossInsertIdx, strip);
    }

    /// <summary>
    /// All known strip view-models keyed by id. Exposed internally so the view
    /// code-behind can resolve drag/drop payloads (which carry only the strip
    /// id) and so reconciliation tests can inspect state without going through
    /// the Avalonia dispatcher.
    /// </summary>
    internal IReadOnlyDictionary<string, StripItemViewModel> ItemsById => _items;

    // Kept as an alias so existing test code still compiles.
    internal IReadOnlyDictionary<string, StripItemViewModel> ItemsByIdForTests => _items;
}

/// <summary>
/// Direction for keyboard navigation and movement commands.
/// </summary>
public enum NavDirection
{
    Up,
    Down,
    Left,
    Right,
}

/// <summary>
/// Which printer carousel a command targets — the departure section (departures
/// and blanks) or the arrival section. Used by
/// <see cref="VStripsViewModel.MoveVisiblePrinterStripToBayAsync"/>,
/// <see cref="VStripsViewModel.MoveAllPrinterStripsToBayAsync"/>, and
/// <see cref="VStripsViewModel.DeleteVisiblePrinterStripAsync"/>.
/// </summary>
public enum PrinterQueueKind
{
    Departure,
    Arrival,
}
