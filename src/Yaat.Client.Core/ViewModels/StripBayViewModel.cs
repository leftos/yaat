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
        for (var i = 0; i < NumberOfRacks; i++)
        {
            Racks.Add(new StripRackViewModel(i));
        }
    }
}
