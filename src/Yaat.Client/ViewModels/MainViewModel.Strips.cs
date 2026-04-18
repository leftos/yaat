using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Multi-facility strips commands — opens additional <see cref="VStripsDockEntryViewModel"/>
/// instances for facilities the student position can access, and closes
/// non-student entries. The student entry itself (index 0 in
/// <see cref="StripsEntries"/>) is constructed once in the main VM constructor
/// and follows the scenario lifecycle.
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// Opens a new strips tab/window for the given facility id. Called from
    /// the main window's "Open strips window…" picker, which iterates the
    /// student entry's <see cref="VStripsViewModel.AccessibleFacilities"/>.
    /// New entries start docked (<see cref="VStripsDockEntryViewModel.IsPoppedOut"/>
    /// = false) and can be popped out by the user via the header action.
    /// Idempotent: if an entry for the facility already exists, selects it
    /// rather than creating a duplicate.
    /// </summary>
    [RelayCommand]
    public async Task OpenStripsEntryForFacilityAsync(string facilityId)
    {
        if (string.IsNullOrEmpty(facilityId))
        {
            return;
        }

        // Existing entry? Just surface it.
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
    /// Closes a non-student strips entry. The student entry is kept because
    /// it's the position's own facility — the user can pop it out but not
    /// remove it. Called from the tab's close-button affordance.
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
    /// Toggles the dock state of a strips entry — the actual window
    /// open/close is wired in MainWindow.axaml.cs via an IsPoppedOut
    /// property-changed handler on each entry. Entry-level pop-out lets
    /// each facility's window be dragged to a second monitor independently.
    /// </summary>
    [RelayCommand]
    public void ToggleStripsEntryPopOut(VStripsDockEntryViewModel entry)
    {
        entry.IsPoppedOut = !entry.IsPoppedOut;
    }

    /// <summary>
    /// Docked-entry subset — the nested TabControl in MainWindow binds to
    /// this filtered collection so popped-out entries disappear from the
    /// tab strip. Recomputed whenever <see cref="StripsEntries"/> changes
    /// or any entry toggles its pop-out state.
    /// </summary>
    public ReadOnlyObservableCollection<VStripsDockEntryViewModel> DockedStripsEntries => _dockedStripsEntriesReadOnly ??= BuildDockedStripsEntries();

    private ObservableCollection<VStripsDockEntryViewModel>? _dockedStripsEntriesBacking;
    private ReadOnlyObservableCollection<VStripsDockEntryViewModel>? _dockedStripsEntriesReadOnly;

    private ReadOnlyObservableCollection<VStripsDockEntryViewModel> BuildDockedStripsEntries()
    {
        _dockedStripsEntriesBacking = [];
        var readOnly = new ReadOnlyObservableCollection<VStripsDockEntryViewModel>(_dockedStripsEntriesBacking);

        // Seed with anything currently docked.
        foreach (var entry in StripsEntries)
        {
            if (!entry.IsPoppedOut)
            {
                _dockedStripsEntriesBacking.Add(entry);
            }
            entry.PropertyChanged += OnStripsEntryPoppedChanged;
        }

        StripsEntries.CollectionChanged += OnStripsEntriesChanged;
        return readOnly;
    }

    private void OnStripsEntriesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_dockedStripsEntriesBacking is null)
        {
            return;
        }
        if (e.OldItems is not null)
        {
            foreach (VStripsDockEntryViewModel entry in e.OldItems)
            {
                entry.PropertyChanged -= OnStripsEntryPoppedChanged;
                _dockedStripsEntriesBacking.Remove(entry);
            }
        }
        if (e.NewItems is not null)
        {
            foreach (VStripsDockEntryViewModel entry in e.NewItems)
            {
                entry.PropertyChanged += OnStripsEntryPoppedChanged;
                if (!entry.IsPoppedOut)
                {
                    _dockedStripsEntriesBacking.Add(entry);
                }
            }
        }
    }

    private void OnStripsEntryPoppedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_dockedStripsEntriesBacking is null || sender is not VStripsDockEntryViewModel entry)
        {
            return;
        }
        if (e.PropertyName != nameof(VStripsDockEntryViewModel.IsPoppedOut))
        {
            return;
        }
        if (entry.IsPoppedOut)
        {
            _dockedStripsEntriesBacking.Remove(entry);
        }
        else if (!_dockedStripsEntriesBacking.Contains(entry))
        {
            // Reinsert in the same order they appear in StripsEntries.
            var idx = StripsEntries.Take(StripsEntries.IndexOf(entry)).Count(e => !e.IsPoppedOut);
            _dockedStripsEntriesBacking.Insert(idx, entry);
        }
    }
}
