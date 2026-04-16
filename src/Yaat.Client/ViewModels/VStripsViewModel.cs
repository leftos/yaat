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

    public VStripsViewModel(ServerConnection connection, Func<string, string, string, Task> sendCommand, UserPreferences? preferences)
    {
        _connection = connection;
        _sendCommand = sendCommand;
        _preferences = preferences;

        _connection.FlightStripsStateChanged += OnFlightStripsStateChanged;
        _connection.StripItemsChanged += OnStripItemsChanged;
        _connection.ScenarioLoaded += OnScenarioLoaded;
        _connection.ScenarioUnloaded += OnScenarioUnloaded;
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

            if (Bays.Count > 0)
            {
                SelectedBay = Bays[0];
                SelectedBay.IsSelected = true;
            }
        });
    }

    // ── Server event handlers ────────────────────────────────────

    private void OnScenarioLoaded(ScenarioLoadedDto dto) => ApplyBayConfig(dto.FlightStripsConfig);

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

    // ── Commands (every action builds a canonical and dispatches it) ──

    [RelayCommand]
    public async Task SelectBayAsync(StripBayViewModel bay)
    {
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
        if (Bays.Count == 0)
        {
            return;
        }
        var idx = SelectedBay is null ? 0 : (Bays.IndexOf(SelectedBay) + 1) % Bays.Count;
        await SelectBayAsync(Bays[idx]);
    }

    [RelayCommand]
    public async Task PreviousBayAsync()
    {
        if (Bays.Count == 0)
        {
            return;
        }
        var idx = SelectedBay is null ? 0 : (Bays.IndexOf(SelectedBay) - 1 + Bays.Count) % Bays.Count;
        await SelectBayAsync(Bays[idx]);
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

    // Testing hooks — exposed so the reconciliation tests can inspect state
    // without needing the Avalonia dispatcher thread.
    internal IReadOnlyDictionary<string, StripItemViewModel> ItemsByIdForTests => _items;
}
