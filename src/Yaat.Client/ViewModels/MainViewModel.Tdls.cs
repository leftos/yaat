using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Multi-facility vTDLS commands — opens additional
/// <see cref="VTdlsDockEntryViewModel"/> instances for facilities the student
/// position can access, and closes non-student entries. The student entry
/// itself (index 0 in <see cref="TdlsEntries"/>) is constructed once in the
/// main VM constructor and follows the scenario lifecycle.
///
/// Each entry is a top-level TabItem in the main window, adjacent to the
/// Strips tabs. Mirrors the Strips multi-instance pattern.
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// Opens a new vTDLS tab for the given facility id. Called from the main
    /// window's View → 'New vTDLS Tab…' picker, which iterates the student
    /// entry's <see cref="VTdlsViewModel.AccessibleFacilities"/>. New entries
    /// start docked (<see cref="VTdlsDockEntryViewModel.IsPoppedOut"/> = false)
    /// and can be popped out via the header action.
    /// Idempotent: if an entry for the facility already exists, dock it.
    /// </summary>
    [RelayCommand]
    public async Task OpenTdlsEntryForFacilityAsync(string facilityId)
    {
        if (string.IsNullOrEmpty(facilityId))
        {
            return;
        }

        // Existing entry? Just dock it back if it was popped out.
        var existing = TdlsEntries.FirstOrDefault(e => e.Vm.FacilityId == facilityId);
        if (existing is not null)
        {
            existing.IsPoppedOut = false;
            return;
        }

        var vm = new VTdlsViewModel(_connection, SendCommandForViewAsync, () => _preferences.UserInitials)
        {
            IsDarkMode = _preferences.IsVTdlsDarkMode,
            ZoomScale = _preferences.TdlsZoomPercent / 100.0,
        };
        var entry = new VTdlsDockEntryViewModel(vm, isStudentEntry: false);
        TdlsEntries.Add(entry);

        await vm.SwitchFacilityAsync(facilityId);
        await vm.RefreshAccessibleFacilitiesAsync();
    }

    /// <summary>Applies a page-zoom percent to every open vTDLS tab. Used by the
    /// Settings live preview / apply / revert paths.</summary>
    public void ApplyTdlsZoomPercent(int percent)
    {
        double scale = percent / 100.0;
        foreach (var entry in TdlsEntries)
        {
            entry.Vm.ZoomScale = scale;
        }
    }

    private void OnTdlsDarkModeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VTdlsViewModel.IsDarkMode) || sender is not VTdlsViewModel changed)
        {
            return;
        }

        // Persist (idempotent — Save() runs only when the value actually changed).
        _preferences.SetVTdlsDarkMode(changed.IsDarkMode);

        // Fan out to every other open vTDLS tab so they stay in sync — upstream
        // treats Dark Mode as a per-user global, not per-tab.
        foreach (var entry in TdlsEntries)
        {
            if (entry.Vm.IsDarkMode != changed.IsDarkMode)
            {
                entry.Vm.IsDarkMode = changed.IsDarkMode;
            }
        }
    }

    /// <summary>
    /// Closes a non-student vTDLS tab. The student entry stays because it's the
    /// position's own facility — the user can pop it out but not remove it.
    /// Non-student tabs are closed via the × button on the tab header.
    /// </summary>
    [RelayCommand]
    public void CloseTdlsEntry(VTdlsDockEntryViewModel entry)
    {
        if (entry.IsStudentEntry)
        {
            return;
        }
        TdlsEntries.Remove(entry);
    }

    private void OnTdlsEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (VTdlsDockEntryViewModel added in e.NewItems)
            {
                SubscribeTdlsEntry(added);
            }
        }
        if (e.OldItems is not null)
        {
            foreach (VTdlsDockEntryViewModel removed in e.OldItems)
            {
                UnsubscribeTdlsEntry(removed);
            }
        }
        OnTabPoppedOutChanged();
    }

    private void SubscribeTdlsEntry(VTdlsDockEntryViewModel entry)
    {
        entry.PropertyChanged += OnTdlsEntryPropertyChanged;
        entry.Vm.PropertyChanged += OnTdlsDarkModeChanged;
    }

    private void UnsubscribeTdlsEntry(VTdlsDockEntryViewModel entry)
    {
        entry.PropertyChanged -= OnTdlsEntryPropertyChanged;
        entry.Vm.PropertyChanged -= OnTdlsDarkModeChanged;
    }

    private void OnTdlsEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VTdlsDockEntryViewModel.IsPoppedOut))
        {
            if (sender is VTdlsDockEntryViewModel entry && entry.IsStudentEntry)
            {
                _preferences.SetPoppedOut("VTdls", entry.IsPoppedOut);
            }
            OnTabPoppedOutChanged();
        }
    }
}
