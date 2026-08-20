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

    /// <summary>
    /// 1-based position among entries showing the same facility, recomputed by
    /// <c>MainViewModel.RecomputeStripsDuplicateOrdinals</c> whenever the entry
    /// collection or any entry's facility changes. 0 or 1 renders the clean
    /// title; 2+ appends a " #n" suffix so duplicate-facility tabs are
    /// distinguishable.
    /// </summary>
    [ObservableProperty]
    private int _duplicateOrdinal;

    partial void OnDuplicateOrdinalChanged(int value) => OnPropertyChanged(nameof(TabTitle));

    /// <summary>
    /// Split layout of this entry's tab/window. Anything but
    /// <see cref="StripsSplitMode.None"/> renders two full strips views —
    /// <see cref="Vm"/> and <see cref="SecondaryVm"/> — around a draggable
    /// splitter, each with its own independently selected bay.
    /// <see cref="SecondaryVm"/> is created by
    /// <c>MainViewModel.SplitStripsEntryAsync</c> before this switches away
    /// from None and cleared again on unsplit.
    /// </summary>
    [ObservableProperty]
    private StripsSplitMode _splitMode;

    /// <summary>
    /// Fraction of the split axis the first (left/top) pane occupies.
    /// Clamped so neither pane can be collapsed into unusability.
    /// </summary>
    [ObservableProperty]
    private double _splitRatio = 0.5;

    partial void OnSplitRatioChanged(double value)
    {
        var clamped = Math.Clamp(value, 0.15, 0.85);
        if (clamped != value)
        {
            SplitRatio = clamped;
        }
    }

    /// <summary>
    /// The second pane's view-model while split; null when
    /// <see cref="SplitMode"/> is <see cref="StripsSplitMode.None"/>. Shares
    /// the transport with <see cref="Vm"/> — strip broadcasts fan out to both
    /// because each VM filters by its own facility id.
    /// </summary>
    [ObservableProperty]
    private VStripsViewModel? _secondaryVm;

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
        // Pending printer strips show as a "(N) " prefix on the tab/window title, so the
        // count must re-render the title as strips print and drain.
        Vm.Printer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StripPrinterViewModel.PendingCount))
            {
                OnPropertyChanged(nameof(TabTitle));
            }
        };
    }

    /// <summary>
    /// Display string for the entry's tab header and popped-out window title:
    /// "(N) OAK - vStrips" — pending-count prefix while the printer holds
    /// strips, then facility-qualified product name (see
    /// <see cref="ClientProductTitle"/>; the browser pages add an " (YAAT)"
    /// suffix, desktop surfaces skip it). Duplicate-facility entries append
    /// " #n" from <see cref="DuplicateOrdinal"/> ("OAK - vStrips #2"). Falls
    /// back to the facility name, then to a bare "vStrips" before the first
    /// scenario load.
    /// </summary>
    public string TabTitle
    {
        get
        {
            var facility = !string.IsNullOrEmpty(Vm.FacilityId) ? Vm.FacilityId : Vm.FacilityName;
            var baseTitle = ClientProductTitle.Build(Vm.Printer.PendingCount, facility, "vStrips", includeYaatSuffix: false);
            return DuplicateOrdinal >= 2 ? $"{baseTitle} #{DuplicateOrdinal}" : baseTitle;
        }
    }
}
