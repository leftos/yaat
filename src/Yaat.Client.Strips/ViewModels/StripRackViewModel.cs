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
    /// Rebuilds <see cref="Strips"/> from <paramref name="newOrder"/> (strip ids
    /// in display order) via an in-place diff. Existing instances are resolved
    /// from <paramref name="itemLookup"/> so bindings retain identity; unknown
    /// ids are skipped silently — reconcile is driven from
    /// <see cref="Services.FlightStripsStateDto"/> which is always consistent
    /// with the item set.
    ///
    /// The previous implementation called <see cref="ObservableCollection{T}.Clear"/>
    /// followed by add-each — every reconcile (e.g. after every <c>STRIP</c>
    /// move) emitted a Removed event for every existing strip and an Added event
    /// for every strip in the new order. Avalonia's <c>ItemsControl</c> handles
    /// each event by rebuilding the affected presenter, so a single move-strip
    /// command flashed every strip in the rack out and back in. The in-place
    /// diff emits Move events for strips that shifted, Insert/Remove only for
    /// strips that genuinely entered or left the rack — visuals for unchanged
    /// strips are preserved frame-perfect.
    /// </summary>
    public void ReplaceAll(IEnumerable<string> newOrder, IReadOnlyDictionary<string, StripItemViewModel> itemLookup)
    {
        var resolved = new List<StripItemViewModel>();
        foreach (var id in newOrder)
        {
            if (itemLookup.TryGetValue(id, out var vm))
            {
                resolved.Add(vm);
            }
        }

        // Drop strips that no longer appear in newOrder. Walk backward so
        // the index doesn't shift under us.
        for (var i = Strips.Count - 1; i >= 0; i--)
        {
            if (!resolved.Contains(Strips[i]))
            {
                Strips.RemoveAt(i);
            }
        }

        // Move-or-insert each strip into its target position in turn.
        for (var targetIdx = 0; targetIdx < resolved.Count; targetIdx++)
        {
            var expected = resolved[targetIdx];
            if (targetIdx < Strips.Count && ReferenceEquals(Strips[targetIdx], expected))
            {
                continue;
            }
            var currentIdx = Strips.IndexOf(expected);
            if (currentIdx >= 0)
            {
                Strips.Move(currentIdx, targetIdx);
            }
            else
            {
                Strips.Insert(targetIdx, expected);
            }
        }
    }
}
