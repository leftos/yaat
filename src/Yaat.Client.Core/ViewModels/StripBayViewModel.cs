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
    public ObservableCollection<StripRackViewModel> Racks { get; } = [];

    /// <summary>
    /// True when this bay belongs to a different facility exposed via an
    /// external-bay link. External bays render as header drop-zones (for
    /// push-by-drag-drop + the right-click Push-to menu) but are not
    /// viewable — <see cref="VStripsViewModel.SelectBayAsync"/> refuses to
    /// select them so the main rack area never binds to their (unknown, lives
    /// on the other facility's window) contents.
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
        IsExternal = config.IsExternal;
        for (var i = 0; i < NumberOfRacks; i++)
        {
            Racks.Add(new StripRackViewModel(i));
        }
    }
}
