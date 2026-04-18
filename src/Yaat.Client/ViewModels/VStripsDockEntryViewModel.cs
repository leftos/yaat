using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// One entry in <see cref="MainViewModel.StripsEntries"/>: a facility-scoped
/// <see cref="VStripsViewModel"/> plus the dock state that decides whether
/// it renders as a sub-tab in the main window's Strips tab or as a
/// free-floating <see cref="Yaat.Client.Views.VStrips.VStripsViewWindow"/>.
///
/// The user toggles between states via a "Pop out" / "Dock" button on the
/// entry's header. The actual window lifecycle is managed in
/// <c>MainWindow.axaml.cs</c> — this VM just holds the state flag and a
/// convenience title binding for both tab headers and window titles.
/// </summary>
public partial class VStripsDockEntryViewModel : ObservableObject
{
    public VStripsViewModel Vm { get; }

    /// <summary>
    /// True for the entry auto-created from the student's scenario load.
    /// Student entries follow the scenario lifecycle — they disappear on
    /// ScenarioUnloaded and re-bootstrap on a new load. Additional entries
    /// are user-opened and persist across scenario changes (though their
    /// facility-switcher re-queries accessible facilities each load).
    /// </summary>
    public bool IsStudentEntry { get; }

    [ObservableProperty]
    private bool _isPoppedOut;

    public VStripsDockEntryViewModel(VStripsViewModel vm, bool isStudentEntry)
    {
        Vm = vm;
        IsStudentEntry = isStudentEntry;
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VStripsViewModel.FacilityName) || e.PropertyName == nameof(VStripsViewModel.FacilityId))
            {
                OnPropertyChanged(nameof(TabTitle));
            }
        };
    }

    /// <summary>
    /// Display string for the entry's tab header and popped-out window title.
    /// Always prefixed with "Strips " + the facility discriminator so multiple
    /// strip tabs/windows can be told apart at a glance ("Strips (OAK)",
    /// "Strips (NCT)"). Falls back to the facility id, then to a bare
    /// "Strips" before the first scenario load.
    /// </summary>
    public string TabTitle
    {
        get
        {
            var facility = !string.IsNullOrEmpty(Vm.FacilityName) ? Vm.FacilityName : Vm.FacilityId;
            return string.IsNullOrEmpty(facility) ? "Strips" : $"Strips ({facility})";
        }
    }
}
