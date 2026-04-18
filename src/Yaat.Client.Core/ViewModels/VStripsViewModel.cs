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

    [ObservableProperty]
    private string? _facilityId;

    [ObservableProperty]
    private string? _facilityName;

    [ObservableProperty]
    private bool _separatorsLocked;

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
        });
    }

    // ── Server event handlers ────────────────────────────────────

    private void OnScenarioLoaded(ScenarioLoadedDto dto)
    {
        ApplyBayConfig(dto.FlightStripsConfig);
        _ = RefreshAccessibleFacilitiesAsync();
    }

    private void OnScenarioUnloaded() => ApplyBayConfig(null);

    private void OnFlightStripsStateChanged(FlightStripsStateDto state) => Dispatcher.UIThread.Post(() => ReconcileFullState(state));

    private void OnStripItemsChanged(List<StripItemDto> items) => Dispatcher.UIThread.Post(() => ReconcileItems(items));

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

    /// <summary>Move a strip into a bay/rack/index. Callsign picked by strip type.</summary>
    public async Task MoveStripAsync(StripItemViewModel strip, StripBayViewModel destBay, int rack, int index)
    {
        var canonical = strip.Type switch
        {
            StripItemType.DepartureStrip or StripItemType.ArrivalStrip => VStripsCanonicalBuilder.BuildStripMove(destBay.Name, rack, index),
            StripItemType.HalfStripLeft or StripItemType.HalfStripRight => VStripsCanonicalBuilder.BuildHalfStripMove(
                strip.LookupKey,
                destBay.Name,
                rack,
                index
            ),
            StripItemType.HandwrittenSeparator or StripItemType.WhiteSeparator or StripItemType.RedSeparator or StripItemType.GreenSeparator =>
                VStripsCanonicalBuilder.BuildSeparatorCreate(
                    MapSeparator(strip.Type),
                    destBay.Name,
                    rack,
                    index,
                    strip.FieldValues.Length > 0 ? strip.FieldValues[0] : null
                ),
            StripItemType.BlankStrip => VStripsCanonicalBuilder.BuildBlankCreate(destBay.Name, rack, index),
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

    public async Task AnnotateAsync(StripItemViewModel strip, int box, string? text)
    {
        if (!strip.IsFullStrip || strip.AircraftId is null)
        {
            return;
        }

        var canonical = VStripsCanonicalBuilder.BuildAnnotate(box, text);
        await _sendCommand(strip.AircraftId, canonical, _preferences?.UserInitials ?? "");
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
