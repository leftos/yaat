using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The strip printer surface. The server merges departure and arrival strips
/// into a single <see cref="FlightStripsStateDto.PrinterItems"/> array on the
/// wire, so the VM demuxes by <see cref="StripItemDto.Type"/> into separate
/// carousels — departure strips and blanks on one side, arrivals on the
/// other — matching the CRC printer modal (docs/crc/img/printer.png).
///
/// Each carousel tracks its own <c>Visible*Index</c> pointer plus navigation
/// commands. The VStrips view binds the printer panel to the appropriate
/// queue + index based on <see cref="HasTwoPrinters"/>; when false, the UI
/// shows a single combined section bound to <see cref="Queue"/>.
/// </summary>
public partial class StripPrinterViewModel : ObservableObject
{
    public ObservableCollection<StripItemViewModel> Queue { get; } = [];
    public ObservableCollection<StripItemViewModel> DepartureQueue { get; } = [];
    public ObservableCollection<StripItemViewModel> ArrivalQueue { get; } = [];

    [ObservableProperty]
    private bool _hasTwoPrinters;

    [ObservableProperty]
    private int _visibleIndex;

    [ObservableProperty]
    private int _visibleDepartureIndex;

    [ObservableProperty]
    private int _visibleArrivalIndex;

    [ObservableProperty]
    private bool _isOpen;

    public StripItemViewModel? VisibleStrip => VisibleIndex >= 0 && VisibleIndex < Queue.Count ? Queue[VisibleIndex] : null;
    public StripItemViewModel? VisibleDepartureStrip =>
        VisibleDepartureIndex >= 0 && VisibleDepartureIndex < DepartureQueue.Count ? DepartureQueue[VisibleDepartureIndex] : null;
    public StripItemViewModel? VisibleArrivalStrip =>
        VisibleArrivalIndex >= 0 && VisibleArrivalIndex < ArrivalQueue.Count ? ArrivalQueue[VisibleArrivalIndex] : null;

    /// <summary>
    /// Carousel labels e.g. "1/3" — matches CRC's index indicator under each
    /// strip in docs/crc/img/printer.png. 0/0 when the queue is empty.
    /// </summary>
    public string DepartureCounter => DepartureQueue.Count == 0 ? "0/0" : $"{VisibleDepartureIndex + 1}/{DepartureQueue.Count}";
    public string ArrivalCounter => ArrivalQueue.Count == 0 ? "0/0" : $"{VisibleArrivalIndex + 1}/{ArrivalQueue.Count}";
    public string CombinedCounter => Queue.Count == 0 ? "0/0" : $"{VisibleIndex + 1}/{Queue.Count}";

    /// <summary>Reconcile the queue to match <paramref name="itemIds"/>, preserving existing VM instances.</summary>
    public void ReplaceAll(IEnumerable<string> itemIds, IReadOnlyDictionary<string, StripItemViewModel> itemLookup)
    {
        Queue.Clear();
        DepartureQueue.Clear();
        ArrivalQueue.Clear();
        foreach (var id in itemIds)
        {
            if (!itemLookup.TryGetValue(id, out var vm))
            {
                continue;
            }
            Queue.Add(vm);
            // Arrival strips go to the arrival carousel; everything else
            // (departure strips, blanks) goes to the departure carousel.
            if (vm.Type == StripItemType.ArrivalStrip)
            {
                ArrivalQueue.Add(vm);
            }
            else
            {
                DepartureQueue.Add(vm);
            }
        }

        if (VisibleIndex >= Queue.Count)
        {
            VisibleIndex = Math.Max(0, Queue.Count - 1);
        }
        if (VisibleDepartureIndex >= DepartureQueue.Count)
        {
            VisibleDepartureIndex = Math.Max(0, DepartureQueue.Count - 1);
        }
        if (VisibleArrivalIndex >= ArrivalQueue.Count)
        {
            VisibleArrivalIndex = Math.Max(0, ArrivalQueue.Count - 1);
        }

        OnPropertyChanged(nameof(VisibleStrip));
        OnPropertyChanged(nameof(VisibleDepartureStrip));
        OnPropertyChanged(nameof(VisibleArrivalStrip));
        OnPropertyChanged(nameof(DepartureCounter));
        OnPropertyChanged(nameof(ArrivalCounter));
        OnPropertyChanged(nameof(CombinedCounter));
    }

    partial void OnVisibleIndexChanged(int value)
    {
        OnPropertyChanged(nameof(VisibleStrip));
        OnPropertyChanged(nameof(CombinedCounter));
    }

    partial void OnVisibleDepartureIndexChanged(int value)
    {
        OnPropertyChanged(nameof(VisibleDepartureStrip));
        OnPropertyChanged(nameof(DepartureCounter));
    }

    partial void OnVisibleArrivalIndexChanged(int value)
    {
        OnPropertyChanged(nameof(VisibleArrivalStrip));
        OnPropertyChanged(nameof(ArrivalCounter));
    }

    public void NextDeparture()
    {
        if (DepartureQueue.Count == 0)
        {
            return;
        }
        VisibleDepartureIndex = (VisibleDepartureIndex + 1) % DepartureQueue.Count;
    }

    public void PreviousDeparture()
    {
        if (DepartureQueue.Count == 0)
        {
            return;
        }
        VisibleDepartureIndex = (VisibleDepartureIndex - 1 + DepartureQueue.Count) % DepartureQueue.Count;
    }

    public void NextArrival()
    {
        if (ArrivalQueue.Count == 0)
        {
            return;
        }
        VisibleArrivalIndex = (VisibleArrivalIndex + 1) % ArrivalQueue.Count;
    }

    public void PreviousArrival()
    {
        if (ArrivalQueue.Count == 0)
        {
            return;
        }
        VisibleArrivalIndex = (VisibleArrivalIndex - 1 + ArrivalQueue.Count) % ArrivalQueue.Count;
    }
}
