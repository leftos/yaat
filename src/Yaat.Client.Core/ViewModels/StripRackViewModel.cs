using System.Collections.ObjectModel;

namespace Yaat.Client.ViewModels;

/// <summary>
/// A single horizontal rack within a strip bay. Holds the ordered list of strips
/// currently displayed in the rack. Reconciliation uses <see cref="ReplaceAll"/>
/// so existing <see cref="StripItemViewModel"/> instances are preserved across
/// server broadcasts — keeps UI selection stable during moves.
/// </summary>
public class StripRackViewModel
{
    public int RackIndex { get; }
    public ObservableCollection<StripItemViewModel> Strips { get; } = [];

    public StripRackViewModel(int rackIndex)
    {
        RackIndex = rackIndex;
    }

    /// <summary>
    /// Rebuilds <see cref="Strips"/> from <paramref name="newOrder"/> (strip ids in
    /// display order). Existing instances are resolved from
    /// <paramref name="itemLookup"/> so bindings retain identity; unknown ids are
    /// skipped silently — reconcile is driven from <see cref="Services.FlightStripsStateDto"/>
    /// which is always consistent with the item set.
    /// </summary>
    public void ReplaceAll(IEnumerable<string> newOrder, IReadOnlyDictionary<string, StripItemViewModel> itemLookup)
    {
        Strips.Clear();
        foreach (var id in newOrder)
        {
            if (itemLookup.TryGetValue(id, out var vm))
            {
                Strips.Add(vm);
            }
        }
    }
}
