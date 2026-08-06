using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Multi-facility strips commands — opens additional <see cref="VStripsDockEntryViewModel"/>
/// instances for facilities the student position can access, and closes
/// non-student entries. The student entry itself (index 0 in
/// <see cref="StripsEntries"/>) is constructed once in the main VM constructor
/// and follows the scenario lifecycle.
///
/// Each entry is a top-level TabItem in the main window, adjacent to
/// Aircraft List / Ground View / Radar View. The code-behind watches
/// <see cref="StripsEntries"/> and materializes a TabItem per entry. Per-tab
/// pop-out mirrors the pattern used by the other views
/// (<see cref="IsDataGridPoppedOut"/> et al.) but is stored on the entry
/// itself so each facility has its own pop-out state.
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// Opens a new strips tab for the given facility id. Called from the
    /// main window's View → 'New Strips Tab…' picker, which iterates the
    /// student entry's <see cref="VStripsViewModel.AccessibleFacilities"/>.
    /// New entries start docked (<see cref="VStripsDockEntryViewModel.IsPoppedOut"/>
    /// = false) and can be popped out by the user via the header action.
    /// A facility that already has a tab gets a second independent view —
    /// e.g. two windows monitoring different bays of the same facility —
    /// distinguished by a " #n" title suffix. Strip broadcasts fan out to
    /// every view of the facility because each <see cref="VStripsViewModel"/>
    /// filters the shared connection's events by its own facility id; there
    /// is no per-facility server subscription to double up or leak.
    /// </summary>
    [RelayCommand]
    public async Task OpenStripsEntryForFacilityAsync(string facilityId)
    {
        if (string.IsNullOrEmpty(facilityId))
        {
            return;
        }

        // New entry — constructed with autoBootstrapFromScenarioLoaded=false so
        // it doesn't try to auto-apply the student's scenario config. Instead
        // we switch it to the requested facility immediately via the RPC.
        var vm = new VStripsViewModel(_connection, SendCommandForViewAsync, () => _preferences.UserInitials, autoBootstrapFromScenarioLoaded: false)
        {
            ZoomScale = _preferences.StripsZoomPercent / 100.0,
        };
        var entry = new VStripsDockEntryViewModel(vm, isStudentEntry: false);
        StripsEntries.Add(entry);

        await vm.SwitchFacilityAsync(facilityId);
        await vm.RefreshAccessibleFacilitiesAsync();
    }

    /// <summary>
    /// Closes a non-student strips tab. The student entry is kept because
    /// it's the position's own facility — the user can pop it out but not
    /// remove it. Non-student tabs can be closed via the × button on the
    /// tab header.
    /// </summary>
    [RelayCommand]
    public void CloseStripsEntry(VStripsDockEntryViewModel entry)
    {
        if (entry.IsStudentEntry)
        {
            return;
        }
        StripsEntries.Remove(entry);
    }

    /// <summary>
    /// Splits (or re-orients) an entry's tab/window into two full strips
    /// views around a draggable splitter — each pane keeps its own selected
    /// bay, so two bays of one facility can be monitored side by side.
    /// The secondary pane is created on the first split, initially showing
    /// the entry's current facility, and survives orientation changes.
    /// </summary>
    public async Task SplitStripsEntryAsync(VStripsDockEntryViewModel entry, StripsSplitMode mode)
    {
        if (mode == StripsSplitMode.None)
        {
            UnsplitStripsEntry(entry);
            return;
        }

        if (entry.SecondaryVm is null)
        {
            var vm = CreateSecondaryStripsVm();
            AttachSecondaryStripsVm(entry, vm);
            var facilityId = entry.Vm.FacilityId;
            if (!string.IsNullOrEmpty(facilityId))
            {
                await vm.SwitchFacilityAsync(facilityId);
                await vm.RefreshAccessibleFacilitiesAsync();
            }
        }

        entry.SplitMode = mode;
    }

    /// <summary>Collapses a split entry back to a single pane and discards the secondary view-model.</summary>
    public void UnsplitStripsEntry(VStripsDockEntryViewModel entry)
    {
        entry.SplitMode = StripsSplitMode.None;
        if (entry.SecondaryVm is { } secondary)
        {
            secondary.PropertyChanged -= OnStripsEntryVmPropertyChanged;
            entry.SecondaryVm = null;
        }
    }

    private VStripsViewModel CreateSecondaryStripsVm() =>
        new(_connection, SendCommandForViewAsync, () => _preferences.UserInitials, autoBootstrapFromScenarioLoaded: false)
        {
            ZoomScale = _preferences.StripsZoomPercent / 100.0,
        };

    private void AttachSecondaryStripsVm(VStripsDockEntryViewModel entry, VStripsViewModel vm)
    {
        entry.SecondaryVm = vm;
        vm.PropertyChanged += OnStripsEntryVmPropertyChanged;
    }

    /// <summary>Applies a page-zoom percent to every open strips tab. Used by the
    /// Settings live preview / apply / revert paths.</summary>
    public void ApplyStripsZoomPercent(int percent)
    {
        double scale = percent / 100.0;
        foreach (var entry in StripsEntries)
        {
            entry.Vm.ZoomScale = scale;
            if (entry.SecondaryVm is { } secondary)
            {
                secondary.ZoomScale = scale;
            }
        }
    }

    private void OnStripsEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (VStripsDockEntryViewModel added in e.NewItems)
            {
                SubscribeStripsEntry(added);
            }
        }
        if (e.OldItems is not null)
        {
            foreach (VStripsDockEntryViewModel removed in e.OldItems)
            {
                UnsubscribeStripsEntry(removed);
            }
        }
        RecomputeStripsDuplicateOrdinals();
        OnTabPoppedOutChanged();
    }

    /// <summary>
    /// Reassigns <see cref="VStripsDockEntryViewModel.DuplicateOrdinal"/> —
    /// 1..n in collection order per facility — whenever entries are added,
    /// removed, or switch facility. Self-healing: closing the first of two
    /// duplicates drops the survivor's " #2" suffix, and switching a tab to
    /// an already-open facility picks up a suffix.
    /// </summary>
    private void RecomputeStripsDuplicateOrdinals()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in StripsEntries)
        {
            var id = entry.Vm.FacilityId;
            if (string.IsNullOrEmpty(id))
            {
                entry.DuplicateOrdinal = 0;
                continue;
            }
            counts[id] = counts.TryGetValue(id, out var n) ? n + 1 : 1;
            entry.DuplicateOrdinal = counts[id];
        }
    }

    private void SubscribeStripsEntry(VStripsDockEntryViewModel entry)
    {
        entry.PropertyChanged += OnStripsEntryPropertyChanged;
        entry.Vm.PropertyChanged += OnStripsEntryVmPropertyChanged;
    }

    private void UnsubscribeStripsEntry(VStripsDockEntryViewModel entry)
    {
        entry.PropertyChanged -= OnStripsEntryPropertyChanged;
        entry.Vm.PropertyChanged -= OnStripsEntryVmPropertyChanged;
        if (entry.SecondaryVm is { } secondary)
        {
            secondary.PropertyChanged -= OnStripsEntryVmPropertyChanged;
        }
    }

    private void OnStripsEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VStripsDockEntryViewModel.IsPoppedOut))
        {
            if (sender is VStripsDockEntryViewModel entry && entry.IsStudentEntry)
            {
                _preferences.SetPoppedOut("VStrips", entry.IsPoppedOut);
            }
            OnTabPoppedOutChanged();
        }
        else if (e.PropertyName == nameof(VStripsDockEntryViewModel.SplitMode))
        {
            if (sender is VStripsDockEntryViewModel entry && entry.IsStudentEntry)
            {
                _preferences.SetVStripsSplitMode(entry.SplitMode.ToString());
            }
        }
        else if (e.PropertyName == nameof(VStripsDockEntryViewModel.SplitRatio))
        {
            if (sender is VStripsDockEntryViewModel entry && entry.IsStudentEntry)
            {
                _preferences.SetVStripsSplitRatio(entry.SplitRatio);
            }
        }
    }

    /// <summary>
    /// Watches each entry's inner <see cref="VStripsViewModel"/>: persists the
    /// strips page-zoom whenever the user adjusts it via the panel's on-panel
    /// zoom buttons (suppressed while the Settings dialog is open — those
    /// transient preview values must not be written), and recomputes duplicate
    /// ordinals when an entry switches facility.
    /// </summary>
    private void OnStripsEntryVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not VStripsViewModel vm)
        {
            return;
        }
        if (e.PropertyName == nameof(VStripsViewModel.FacilityId))
        {
            RecomputeStripsDuplicateOrdinals();
            return;
        }
        if (e.PropertyName != nameof(VStripsViewModel.ZoomScale))
        {
            return;
        }
        if (IsSettingsPreviewActive)
        {
            return;
        }
        _preferences.SetStripsZoomPercent((int)Math.Round(vm.ZoomScale * 100));
    }
}
