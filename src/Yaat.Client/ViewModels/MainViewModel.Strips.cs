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
    /// Idempotent: if an entry for the facility already exists, dock it.
    /// </summary>
    [RelayCommand]
    public async Task OpenStripsEntryForFacilityAsync(string facilityId)
    {
        if (string.IsNullOrEmpty(facilityId))
        {
            return;
        }

        // Existing entry? Just dock it back if it was popped out.
        var existing = StripsEntries.FirstOrDefault(e => e.Vm.FacilityId == facilityId);
        if (existing is not null)
        {
            existing.IsPoppedOut = false;
            return;
        }

        // New entry — constructed with autoBootstrapFromScenarioLoaded=false so
        // it doesn't try to auto-apply the student's scenario config. Instead
        // we switch it to the requested facility immediately via the RPC.
        var vm = new VStripsViewModel(_connection, SendCommandForViewAsync, _preferences, autoBootstrapFromScenarioLoaded: false);
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
        OnTabPoppedOutChanged();
    }

    private void SubscribeStripsEntry(VStripsDockEntryViewModel entry)
    {
        entry.PropertyChanged += OnStripsEntryPropertyChanged;
    }

    private void UnsubscribeStripsEntry(VStripsDockEntryViewModel entry)
    {
        entry.PropertyChanged -= OnStripsEntryPropertyChanged;
    }

    private void OnStripsEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VStripsDockEntryViewModel.IsPoppedOut))
        {
            OnTabPoppedOutChanged();
        }
    }
}
