using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Root view-model for the vTDLS clone. One instance per active facility tab /
/// pop-out window (multi-facility support — mirrors the Strips multi-instance
/// pattern). Subscribes to the server's TDLS broadcasts and reconciles two
/// observable item collections (DCL = Pending; PDC = Sent / Wilco). User actions
/// emit canonical commands via the injected <c>_sendCommand</c> delegate — no
/// new RPCs; the server applies the command and the resulting broadcast drives
/// reconciliation back.
///
/// Mirrors <see cref="VStripsViewModel"/>. Like Strips, instance identity for
/// <see cref="TdlsItemViewModel"/> is preserved across reconciliation so
/// Avalonia bindings stay stable.
/// </summary>
public partial class VTdlsViewModel : ObservableObject
{
    private readonly ILogger _log = SimLog.CreateLogger("VTdlsViewModel");

    private readonly ITdlsTransport _transport;
    private readonly Func<string, string, string, Task> _sendCommand;
    private readonly Func<string>? _getUserInitials;

    private readonly Dictionary<string, TdlsItemViewModel> _itemsById = new(StringComparer.Ordinal);

    /// <summary>DCL list — Pending items only.</summary>
    public ObservableCollection<TdlsItemViewModel> DclItems { get; } = [];

    /// <summary>PDC list — Sent and Wilco items.</summary>
    public ObservableCollection<TdlsItemViewModel> PdcItems { get; } = [];

    /// <summary>CPDLC list — permanently empty (VATSIM does not support CPDLC); rendered for parity.</summary>
    public ObservableCollection<TdlsItemViewModel> CpdlcItems { get; } = [];

    [ObservableProperty]
    private bool _isConnected;

    /// <summary>Facilities the current room's student position can open vTDLS windows for. Populated by <see cref="RefreshAccessibleFacilitiesAsync"/>.</summary>
    public ObservableCollection<AccessibleFacilityDto> AccessibleFacilities { get; } = [];

    [ObservableProperty]
    private string? _facilityId;

    [ObservableProperty]
    private string? _facilityName;

    [ObservableProperty]
    private TdlsConfigDto? _config;

    [ObservableProperty]
    private TdlsItemViewModel? _selectedItem;

    [ObservableProperty]
    private TdlsFlightPlanEditorViewModel? _editor;

    /// <summary>
    /// Dark-mode toggle for the vTDLS view. Mirrors the upstream "Dark Mode" item
    /// in the Facility Menu (see docs/vtdls/vtdls.md §Dark Mode). False = the
    /// realistic light-themed look that controllers see in production vTDLS; true
    /// flips list backgrounds + chrome to dark for use alongside YAAT's other dark
    /// views. The host wires persistence via UserPreferences — this VM stays
    /// preference-free and only owns the observable flag.
    /// </summary>
    [ObservableProperty]
    private bool _isDarkMode;

    partial void OnSelectedItemChanged(TdlsItemViewModel? oldValue, TdlsItemViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }
        if (newValue is not null)
        {
            newValue.IsSelected = true;
            // Editor is opened only for Pending items (DCL). PDC items are
            // post-send and immutable — the controller can only dump or wait.
            if (newValue.Status == TdlsStatus.Pending && Config is not null)
            {
                Editor = new TdlsFlightPlanEditorViewModel(newValue.AircraftId, Config, seed: null) { OnSendRequested = OnEditorSendRequested };
            }
            else
            {
                Editor = null;
            }
        }
        else
        {
            Editor = null;
        }
    }

    public VTdlsViewModel(ITdlsTransport transport, Func<string, string, string, Task> sendCommand, Func<string>? getUserInitials)
    {
        _transport = transport;
        _sendCommand = sendCommand;
        _getUserInitials = getUserInitials;

        _transport.TdlsItemChanged += OnTdlsItemChanged;
        _transport.TdlsItemRemoved += OnTdlsItemRemoved;
        _transport.TdlsStateChanged += OnTdlsStateChanged;

        _isConnected = _transport.IsConnected;
        _transport.Connected += () => Dispatcher.UIThread.Post(() => IsConnected = true);
        _transport.Closed += _ => Dispatcher.UIThread.Post(OnConnectionLost);
        _transport.Reconnecting += _ => Dispatcher.UIThread.Post(OnConnectionLost);
        _transport.Reconnected += _ => Dispatcher.UIThread.Post(() => IsConnected = true);
    }

    private void OnConnectionLost()
    {
        IsConnected = false;
        _itemsById.Clear();
        DclItems.Clear();
        PdcItems.Clear();
        SelectedItem = null;
        Editor = null;
    }

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

    /// <summary>Pulls the list of facilities the current room's position can open vTDLS windows for. Called from the host on JoinRoom.</summary>
    public async Task RefreshAccessibleFacilitiesAsync()
    {
        try
        {
            var facilities = await _transport.GetAccessibleTdlsFacilitiesAsync();
            Dispatcher.UIThread.Post(() =>
            {
                AccessibleFacilities.Clear();
                foreach (var f in facilities)
                {
                    AccessibleFacilities.Add(f);
                }
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to fetch accessible TDLS facilities");
        }
    }

    /// <summary>
    /// Switches the VM to a different facility — pulls its config, clears the
    /// current item collections, and requests a fresh full-state broadcast so
    /// the new facility's items render. Idempotent for the same facility id.
    /// </summary>
    public async Task SwitchFacilityAsync(string facilityId)
    {
        if (string.Equals(FacilityId, facilityId, StringComparison.Ordinal) && Config is not null)
        {
            return;
        }

        TdlsConfigDto? cfg;
        try
        {
            cfg = await _transport.GetTdlsConfigForFacilityAsync(facilityId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to fetch TDLS config for {Facility}", facilityId);
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            FacilityId = facilityId;
            FacilityName = cfg?.FacilityName ?? facilityId;
            Config = cfg;
            // Clear list contents but keep the dictionary so re-applies of the
            // full state still produce stable VM identities for items we
            // already saw.
            DclItems.Clear();
            PdcItems.Clear();
            SelectedItem = null;
            Editor = null;
        });

        try
        {
            await _transport.RequestFullTdlsStateAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "RequestFullTdlsState failed after switch to {Facility}", facilityId);
        }
    }

    private void OnTdlsItemChanged(TdlsItemDto dto) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!MatchesActiveFacility(dto.FacilityId))
            {
                return;
            }
            ApplyItem(dto);
        });

    private void OnTdlsItemRemoved(TdlsItemRemovedDto dto) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!MatchesActiveFacility(dto.FacilityId))
            {
                return;
            }
            if (_itemsById.Remove(dto.ItemId, out var vm))
            {
                DclItems.Remove(vm);
                PdcItems.Remove(vm);
                if (ReferenceEquals(SelectedItem, vm))
                {
                    SelectedItem = null;
                }
            }
        });

    private void OnTdlsStateChanged(TdlsStateDto state) =>
        Dispatcher.UIThread.Post(() =>
        {
            // Replace the entire item set scoped to the active facility.
            var fresh = state.Items.Where(i => MatchesActiveFacility(i.FacilityId)).ToList();
            var freshIds = fresh.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);

            // Drop items no longer present.
            foreach (var staleId in _itemsById.Keys.Where(id => !freshIds.Contains(id)).ToList())
            {
                if (_itemsById.Remove(staleId, out var vm))
                {
                    DclItems.Remove(vm);
                    PdcItems.Remove(vm);
                    if (ReferenceEquals(SelectedItem, vm))
                    {
                        SelectedItem = null;
                    }
                }
            }

            foreach (var dto in fresh)
            {
                ApplyItem(dto);
            }
        });

    private bool MatchesActiveFacility(string facilityId) =>
        string.IsNullOrEmpty(FacilityId) || string.Equals(facilityId, FacilityId, StringComparison.Ordinal);

    private void ApplyItem(TdlsItemDto dto)
    {
        if (!_itemsById.TryGetValue(dto.Id, out var vm))
        {
            vm = new TdlsItemViewModel(dto);
            _itemsById[dto.Id] = vm;
        }
        else
        {
            vm.Apply(dto);
        }

        // Re-bucket based on current status.
        DclItems.Remove(vm);
        PdcItems.Remove(vm);
        if (vm.Status == TdlsStatus.Pending)
        {
            InsertSortedBySequence(DclItems, vm);
        }
        else
        {
            InsertSortedBySequence(PdcItems, vm);
        }
    }

    private static void InsertSortedBySequence(ObservableCollection<TdlsItemViewModel> list, TdlsItemViewModel vm)
    {
        var idx = 0;
        while (idx < list.Count && list[idx].Sequence <= vm.Sequence)
        {
            idx++;
        }
        list.Insert(idx, vm);
    }

    [RelayCommand]
    private async Task DumpSelectedAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }
        await _sendCommand(SelectedItem.AircraftId, VTdlsCanonicalBuilder.BuildDump(), _getUserInitials?.Invoke() ?? "");
    }

    [RelayCommand]
    private async Task ForceWilcoSelectedAsync()
    {
        if (SelectedItem is null || SelectedItem.Status != TdlsStatus.Sent)
        {
            return;
        }
        await _sendCommand(SelectedItem.AircraftId, VTdlsCanonicalBuilder.BuildWilco(), _getUserInitials?.Invoke() ?? "");
    }

    private Task OnEditorSendRequested(ClearanceDto clearance)
    {
        if (SelectedItem is null)
        {
            return Task.CompletedTask;
        }
        return _sendCommand(SelectedItem.AircraftId, VTdlsCanonicalBuilder.BuildSend(clearance), _getUserInitials?.Invoke() ?? "");
    }
}
