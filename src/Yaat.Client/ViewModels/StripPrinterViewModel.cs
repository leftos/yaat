using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The strip printer surface. v1 tracks a single unified queue because the
/// server's <see cref="Services.FlightStripsStateDto.PrinterItems"/> payload merges
/// DeparturePrinterQueue and ArrivalPrinterQueue into one ordered list. When the
/// per-facility <c>HasTwoPrinters</c> flag is set the UI can render two tabs
/// bound to the same collection — the distinction doesn't reach the wire.
/// </summary>
public partial class StripPrinterViewModel : ObservableObject
{
    public ObservableCollection<StripItemViewModel> Queue { get; } = [];

    [ObservableProperty]
    private bool _hasTwoPrinters;

    [ObservableProperty]
    private int _visibleIndex;

    [ObservableProperty]
    private bool _isOpen;

    public StripItemViewModel? VisibleStrip => VisibleIndex >= 0 && VisibleIndex < Queue.Count ? Queue[VisibleIndex] : null;

    /// <summary>Reconcile the queue to match <paramref name="itemIds"/>, preserving existing VM instances.</summary>
    public void ReplaceAll(IEnumerable<string> itemIds, IReadOnlyDictionary<string, StripItemViewModel> itemLookup)
    {
        Queue.Clear();
        foreach (var id in itemIds)
        {
            if (itemLookup.TryGetValue(id, out var vm))
            {
                Queue.Add(vm);
            }
        }

        if (VisibleIndex >= Queue.Count)
        {
            VisibleIndex = Math.Max(0, Queue.Count - 1);
        }
        OnPropertyChanged(nameof(VisibleStrip));
    }
}
