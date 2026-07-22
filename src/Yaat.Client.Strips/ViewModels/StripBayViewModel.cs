using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// One strip bay in the vStrips view. Aggregates <see cref="StripRackViewModel"/>
/// instances — one per rack configured for the bay. Bay identity is stable
/// (driven by the server-supplied <see cref="StripBayConfigDto"/>); only rack
/// contents change over the bay's lifetime.
/// </summary>
public partial class StripBayViewModel : ObservableObject
{
    public string BayId { get; }
    public string Name { get; }
    public int NumberOfRacks { get; }

    /// <summary>
    /// The bay's owning facility. Every canonical strip command addresses a bay
    /// as <c>FACILITY/BAY</c>, so this rides on the view-model rather than being
    /// re-derived from the window's facility (which differs for external bays).
    /// </summary>
    public string FacilityId { get; }

    public ObservableCollection<StripRackViewModel> Racks { get; } = [];

    /// <summary>
    /// True when this bay belongs to a different facility exposed via an
    /// external-bay link. External bays render as header drop-zones (for
    /// push-by-drag-drop + the right-click Push-to menu) but are not viewable
    /// here — <see cref="VStripsViewModel.SelectBayAsync"/> refuses to select
    /// them. Their contents are seen by opening that facility's own strips tab.
    /// </summary>
    public bool IsExternal { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isPeeked;

    /// <summary>
    /// True while reconciliation flagged this bay as the one holding a strip
    /// that was just moved or created — drives the "flash on arrival" cue in
    /// the header for strips moved by other users.
    /// </summary>
    [ObservableProperty]
    private bool _hasNewItem;

    public StripBayViewModel(StripBayConfigDto config)
    {
        BayId = config.Id;
        Name = config.Name;
        NumberOfRacks = config.NumberOfRacks;
        FacilityId = config.FacilityId;
        IsExternal = config.IsExternal;
        for (var i = 0; i < NumberOfRacks; i++)
        {
            Racks.Add(new StripRackViewModel(i));
        }
    }
}
