using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// One entry in <see cref="MainViewModel.TdlsEntries"/>: a facility-scoped
/// <see cref="VTdlsViewModel"/> plus the dock state that decides whether it
/// renders as a sub-tab in the main window's vTDLS tab or as a free-floating
/// <see cref="Yaat.Client.Views.VTdls.VTdlsViewWindow"/>.
///
/// Mirrors <see cref="VStripsDockEntryViewModel"/>. The user toggles between
/// docked / popped-out via a header button; the window lifecycle is managed in
/// <c>MainWindow.axaml.cs</c> and this VM just holds the state flag + a
/// convenience title binding for both tab headers and window titles.
/// </summary>
public partial class VTdlsDockEntryViewModel : ObservableObject
{
    public VTdlsViewModel Vm { get; }

    /// <summary>
    /// True for the entry auto-created from the student's scenario load (when
    /// the position has a TDLS-configured facility). Student entries follow
    /// the scenario lifecycle. Additional entries are user-opened and persist
    /// across scenario changes (though their facility list re-queries on each
    /// load).
    /// </summary>
    public bool IsStudentEntry { get; }

    [ObservableProperty]
    private bool _isPoppedOut;

    public VTdlsDockEntryViewModel(VTdlsViewModel vm, bool isStudentEntry)
    {
        Vm = vm;
        IsStudentEntry = isStudentEntry;
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VTdlsViewModel.FacilityName) || e.PropertyName == nameof(VTdlsViewModel.FacilityId))
            {
                OnPropertyChanged(nameof(TabTitle));
            }
        };
        // Pending DCLs show as a "(N) " prefix on the tab/window title, so the count
        // must re-render the title as items arrive and are sent.
        Vm.DclItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(TabTitle));
    }

    /// <summary>
    /// Display string for the entry's tab header and popped-out window title:
    /// "(N) OAK - vTDLS" — pending-DCL-count prefix while clearances await
    /// action, then facility-qualified product name (see
    /// <see cref="ClientProductTitle"/>; the browser page adds an " (YAAT)"
    /// suffix, desktop surfaces skip it). Falls back to the facility name,
    /// then to a bare "vTDLS" before the first scenario load.
    /// </summary>
    public string TabTitle
    {
        get
        {
            var facility = !string.IsNullOrEmpty(Vm.FacilityId) ? Vm.FacilityId : Vm.FacilityName;
            return ClientProductTitle.Build(Vm.DclItems.Count, facility, "vTDLS", includeYaatSuffix: false);
        }
    }
}
