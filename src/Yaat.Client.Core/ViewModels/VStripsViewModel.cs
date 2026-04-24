using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Root view-model for the vStrips clone. Subscribes to the server's flight-strip
/// broadcasts (<see cref="ServerConnection.FlightStripsStateChanged"/> and
/// <see cref="ServerConnection.StripItemsChanged"/>) and reconciles an observable
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
    private readonly ILogger _log = AppLog.CreateLogger<VStripsViewModel>();

    private readonly ServerConnection _connection;
    private readonly Func<string, string, string, Task> _sendCommand;
    private readonly UserPreferences? _preferences;

    private readonly Dictionary<string, StripItemViewModel> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StripBayViewModel> _baysById = new(StringComparer.Ordinal);

    public ObservableCollection<StripBayViewModel> Bays { get; } = [];
    public StripPrinterViewModel Printer { get; } = new();

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
    /// When true, this VM auto-applies <see cref="ServerConnection.ScenarioLoaded"/>
    /// events (the student-facility bootstrap path used by the embedded tab
    /// and the standalone app's primary instance). Additional VMs opened for
    /// other facilities set this to false and get their bay config via
    /// <see cref="SwitchFacilityAsync"/>.
    /// </summary>
    private readonly bool _autoBootstrapFromScenarioLoaded;

    public VStripsViewModel(
        ServerConnection connection,
        Func<string, string, string, Task> sendCommand,
        UserPreferences? preferences,
        bool autoBootstrapFromScenarioLoaded = true
    )
    {
        _connection = connection;
        _sendCommand = sendCommand;
        _preferences = preferences;
        _autoBootstrapFromScenarioLoaded = autoBootstrapFromScenarioLoaded;

        _connection.FlightStripsStateChanged += OnFlightStripsStateChanged;
        _connection.StripItemsChanged += OnStripItemsChanged;
        if (_autoBootstrapFromScenarioLoaded)
        {
            _connection.ScenarioLoaded += OnScenarioLoaded;
            _connection.ScenarioUnloaded += OnScenarioUnloaded;
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
                _items.Clear();
                Printer.Queue.Clear();
                return;
            }

            FacilityId = config.FacilityId;
            FacilityName = config.FacilityName;
            SeparatorsLocked = config.SeparatorsLocked;
            Printer.HasTwoPrinters = config.HasTwoPrinters;

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

    private void OnScenarioLoaded(ScenarioLoadedDto dto)
    {
        ApplyBayConfig(dto.FlightStripsConfig);
        _ = RefreshAccessibleFacilitiesAsync();
    }

    private void OnScenarioUnloaded()
    {
        _lastReceivedFullState = null;
        _lastReceivedItems = null;
        ApplyBayConfig(null);
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
                _items[dto.Id] = new StripItemViewModel(dto);
            }
        }
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
    /// <see cref="ServerConnection.GetFlightStripsConfigForFacilityAsync"/>,
    /// then drives <see cref="ApplyBayConfig"/> with the result. The server
    /// rejects out-of-scope facility ids (returns null), in which case we
    /// leave the current config untouched.
    /// </summary>
    public async Task SwitchFacilityAsync(string facilityId)
    {
        try
        {
            var config = await _connection.GetFlightStripsConfigForFacilityAsync(facilityId);
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
    /// <see cref="OnScenarioLoaded"/>) and any time the caller explicitly
    /// refreshes. Swallows errors — an empty list just means the switcher
    /// popup has no entries.
    /// </summary>
    public async Task RefreshAccessibleFacilitiesAsync()
    {
        try
        {
            var list = await _connection.GetAccessibleFacilitiesAsync();
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
            StripItemType.DepartureStrip or StripItemType.ArrivalStrip => VStripsCanonicalBuilder.BuildStripMove(destBay.Name, rack, index),
            StripItemType.HalfStripLeft or StripItemType.HalfStripRight => VStripsCanonicalBuilder.BuildHalfStripMove(
                strip.LookupKey,
                destBay.Name,
                rack,
                indexOrZero
            ),
            StripItemType.HandwrittenSeparator or StripItemType.WhiteSeparator or StripItemType.RedSeparator or StripItemType.GreenSeparator =>
                VStripsCanonicalBuilder.BuildSeparatorCreate(
                    MapSeparator(strip.Type),
                    destBay.Name,
                    rack,
                    indexOrZero,
                    strip.FieldValues.Length > 0 ? strip.FieldValues[0] : null
                ),
            StripItemType.BlankStrip => VStripsCanonicalBuilder.BuildBlankCreate(destBay.Name, rack, indexOrZero),
            _ => null,
        };

        if (canonical is null)
        {
            _log.LogWarning("MoveStripAsync: no canonical mapping for strip type {Type}", strip.Type);
            return;
        }

        var callsign = strip.IsFullStrip ? (strip.AircraftId ?? "") : "";
        await _sendCommand(callsign, canonical, _preferences?.UserInitials ?? "");
    }

    public async Task DeleteStripAsync(StripItemViewModel strip)
    {
        var (callsign, canonical) = strip.Type switch
        {
            StripItemType.DepartureStrip or StripItemType.ArrivalStrip => (strip.AircraftId ?? "", VStripsCanonicalBuilder.BuildStripDelete()),
            StripItemType.HalfStripLeft or StripItemType.HalfStripRight => ("", VStripsCanonicalBuilder.BuildHalfStripDelete(strip.LookupKey)),
            _ => ((string)"", (string?)null),
        };

        if (canonical is null)
        {
            _log.LogWarning("DeleteStripAsync: no canonical mapping for strip type {Type}", strip.Type);
            return;
        }

        await _sendCommand(callsign, canonical, _preferences?.UserInitials ?? "");
    }

    public async Task ToggleOffsetAsync(StripItemViewModel strip)
    {
        var (callsign, canonical) = strip.Type switch
        {
            StripItemType.DepartureStrip or StripItemType.ArrivalStrip => (strip.AircraftId ?? "", VStripsCanonicalBuilder.BuildStripOffset()),
            StripItemType.HalfStripLeft or StripItemType.HalfStripRight => ("", VStripsCanonicalBuilder.BuildHalfStripOffset(strip.LookupKey)),
            _ => ((string)"", (string?)null),
        };

        if (canonical is null)
        {
            return;
        }

        await _sendCommand(callsign, canonical, _preferences?.UserInitials ?? "");
    }

    /// <summary>
    /// Edits an annotation slot on a full strip. <paramref name="box"/> is the
    /// canonical slot id — <c>"1"</c>..<c>"9"</c> for the 3×3 grid, or
    /// <c>"8a"</c>/<c>"8b"</c> for the col-3 freeform slots below field 8.
    /// </summary>
    public async Task AnnotateAsync(StripItemViewModel strip, string box, string? text)
    {
        if (!strip.IsFullStrip || strip.AircraftId is null)
        {
            return;
        }

        var canonical = VStripsCanonicalBuilder.BuildAnnotate(box, text);
        await _sendCommand(strip.AircraftId, canonical, _preferences?.UserInitials ?? "");
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
        var canonical = VStripsCanonicalBuilder.BuildHalfStripAmend(strip.LookupKey, lines);
        await _sendCommand("", canonical, _preferences?.UserInitials ?? "");
    }

    /// <summary>
    /// Rename a separator atomically via the server-side SEPE command.
    /// Single mutation under the state gate replaces the prior
    /// delete-then-create pair — no broadcast gap, no race with concurrent
    /// moves. Locked facilities (SeparatorsLocked=true) drop this call to
    /// match the CRC constraint that only handwritten separators can be
    /// edited.
    /// </summary>
    public async Task EditSeparatorLabelAsync(StripItemViewModel strip, string newLabel)
    {
        if (!strip.IsSeparator || SelectedBay is null)
        {
            return;
        }
        if (SeparatorsLocked && strip.Type != StripItemType.HandwrittenSeparator)
        {
            return;
        }

        // Locate the separator by scanning the selected bay's racks; the
        // server's authoritative position lives on the strip record and the
        // VM's rack ordering mirrors it, so the first match wins.
        var rackIndex = -1;
        var posIndex = -1;
        for (var r = 0; r < SelectedBay.Racks.Count; r++)
        {
            var idx = SelectedBay.Racks[r].Strips.IndexOf(strip);
            if (idx >= 0)
            {
                rackIndex = r;
                posIndex = idx;
                break;
            }
        }
        if (rackIndex < 0)
        {
            return;
        }

        var canonical = VStripsCanonicalBuilder.BuildSeparatorEdit(SelectedBay.Name, rackIndex, posIndex, newLabel);
        await _sendCommand("", canonical, _preferences?.UserInitials ?? "");
    }

    public async Task SlideHalfStripAsync(StripItemViewModel strip)
    {
        if (!strip.IsHalfStrip)
        {
            return;
        }
        var canonical = VStripsCanonicalBuilder.BuildHalfStripSlide(strip.LookupKey);
        await _sendCommand("", canonical, _preferences?.UserInitials ?? "");
    }

    public async Task CreateHalfStripAsync(StripBayViewModel bay, int rack, IReadOnlyList<string> lines)
    {
        var canonical = VStripsCanonicalBuilder.BuildHalfStripCreate(bay.Name, rack, lines);
        await _sendCommand("", canonical, _preferences?.UserInitials ?? "");
    }

    public async Task CreateSeparatorAsync(SeparatorStyle style, StripBayViewModel bay, int rack, int index, string? label)
    {
        if (SeparatorsLocked)
        {
            return;
        }
        var canonical = VStripsCanonicalBuilder.BuildSeparatorCreate(style, bay.Name, rack, index, label);
        await _sendCommand("", canonical, _preferences?.UserInitials ?? "");
    }

    public async Task CreateBlankAsync(StripBayViewModel? bay, int? rack, int? index)
    {
        var canonical = VStripsCanonicalBuilder.BuildBlankCreate(bay?.Name, rack, index);
        await _sendCommand("", canonical, _preferences?.UserInitials ?? "");
    }

    /// <summary>
    /// Prints a blank strip directly into the printer queue. Matches the CRC
    /// "Print Blank Strip" button in docs/crc/img/printer.png — blanks go to
    /// the printer first, from where users drag them into racks.
    /// </summary>
    public async Task PrintBlankStripAsync()
    {
        await CreateBlankAsync(bay: null, rack: null, index: null);
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
            var result = await _connection.RequestFlightStripForAircraftAsync(trimmed);
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
            _ => Printer.VisibleStrip,
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
    /// Deletes the visible printer strip (for the given queue kind). Mirrors
    /// the "Delete" button next to the carousel in docs/crc/img/printer.png.
    /// </summary>
    public async Task DeleteVisiblePrinterStripAsync(PrinterQueueKind kind)
    {
        var strip = kind switch
        {
            PrinterQueueKind.Departure => Printer.VisibleDepartureStrip,
            PrinterQueueKind.Arrival => Printer.VisibleArrivalStrip,
            _ => Printer.VisibleStrip,
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
        await _sendCommand("", canonical, _preferences?.UserInitials ?? "");
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

    private static SeparatorStyle MapSeparator(StripItemType type) =>
        type switch
        {
            StripItemType.WhiteSeparator => SeparatorStyle.White,
            StripItemType.RedSeparator => SeparatorStyle.Red,
            StripItemType.GreenSeparator => SeparatorStyle.Green,
            _ => SeparatorStyle.Handwritten,
        };

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
/// Which printer carousel a command targets. Used by
/// <see cref="VStripsViewModel.MoveVisiblePrinterStripToBayAsync"/> and
/// <see cref="VStripsViewModel.DeleteVisiblePrinterStripAsync"/> so a single
/// entry point services both the two-printer and the combined single-printer
/// rendering paths.
/// </summary>
public enum PrinterQueueKind
{
    Combined,
    Departure,
    Arrival,
}
