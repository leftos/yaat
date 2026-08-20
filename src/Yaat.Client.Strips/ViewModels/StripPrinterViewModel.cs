using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The strip printer surface. The server merges departure and arrival strips
/// into a single <see cref="FlightStripsStateDto.PrinterItems"/> array on the
/// wire; how the VM demuxes them follows the facility's vNAS strips config
/// (<see cref="SeparateArrivalCarousel"/>): with separate printers, arrivals
/// split into their own carousel — matching the CRC printer modal
/// (docs/crc/img/printer.png) — otherwise everything shares the single
/// departure carousel and the view hides the arrival section.
///
/// <see cref="Queue"/> is the un-split aggregate, kept only so drag-out (a
/// strip dragged from the printer onto a rack) and reset can locate/remove an
/// item without knowing its section. Each carousel tracks its own
/// <c>Visible*Index</c> pointer plus navigation commands.
/// </summary>
public partial class StripPrinterViewModel : ObservableObject
{
    public ObservableCollection<StripItemViewModel> Queue { get; } = [];
    public ObservableCollection<StripItemViewModel> DepartureQueue { get; } = [];
    public ObservableCollection<StripItemViewModel> ArrivalQueue { get; } = [];

    /// <summary>
    /// True when this facility shows arrival strips in their own carousel — the vNAS
    /// config's <c>enableArrivalStrips &amp;&amp; enableSeparateArrDepPrinters</c>.
    /// False routes every printer item into the single departure carousel and hides
    /// the arrival section. Set by <c>VStripsViewModel.ApplyBayConfig</c> before the
    /// queued broadcasts reconcile, so the split matches the facility from the start.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeText))]
    [NotifyPropertyChangedFor(nameof(DepartureSectionLabel))]
    private bool _separateArrivalCarousel = true;

    [ObservableProperty]
    private int _visibleDepartureIndex;

    [ObservableProperty]
    private int _visibleArrivalIndex;

    [ObservableProperty]
    private bool _isOpen;

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

    /// <summary>
    /// Header notification badge — departure/arrival queue sizes ("3/0") with separate
    /// printers, a single total with a unified carousel — matching the red badge on
    /// CRC's printer icon in docs/crc/img/header.png. Hidden while both queues are empty.
    /// </summary>
    public string BadgeText =>
        SeparateArrivalCarousel ? $"{DepartureQueue.Count}/{ArrivalQueue.Count}" : DepartureQueue.Count.ToString(CultureInfo.InvariantCulture);
    public bool HasQueuedStrips => (DepartureQueue.Count > 0) || (ArrivalQueue.Count > 0);

    /// <summary>
    /// Total pending strips across both carousels — drives the "(N) " tab/window/page
    /// title prefix (see <see cref="Services.ClientProductTitle"/>).
    /// </summary>
    public int PendingCount => DepartureQueue.Count + ArrivalQueue.Count;

    /// <summary>Section header over the main carousel — names the split when arrivals have their own.</summary>
    public string DepartureSectionLabel => SeparateArrivalCarousel ? "Departure Printer:" : "Printer:";

    /// <summary>
    /// Callsign the user last asked to bring into view via "Request Strip".
    /// Consumed by <see cref="ReplaceAll"/> on the next reconcile so the
    /// carousel jumps to the newly-printed strip without requiring the user
    /// to arrow through the queue. Cleared after the focus is applied (or
    /// when any other navigation overrides it).
    /// </summary>
    private string? _pendingFocusCallsign;

    // True when a "Print Blank Strip" click is awaiting its broadcast — the
    // next ReplaceAll then jumps the departure carousel to the newest blank
    // (highest index) so the user sees what they just printed without
    // arrowing through the queue. Reset on apply.
    private bool _pendingFocusOnNewBlank;

    /// <summary>
    /// Marks <paramref name="callsign"/> as the target to focus on the next
    /// reconcile. Called from <c>VStripsViewModel.RequestStripAsync</c> after
    /// the server RPC succeeds. Also attempts to focus immediately in case
    /// the queue is already up to date.
    /// </summary>
    public void RequestFocusOnCallsign(string callsign)
    {
        if (string.IsNullOrEmpty(callsign))
        {
            return;
        }
        _pendingFocusCallsign = callsign;
        TryApplyPendingFocus();
    }

    /// <summary>
    /// Marks the next reconcile as having printed a blank strip — the
    /// departure carousel jumps to the newest <c>BlankStrip</c> in the
    /// queue. Called from <c>VStripsViewModel.PrintBlankStripAsync</c>
    /// after the dispatch returns; the actual focus shift happens when
    /// the broadcast lands and <see cref="ReplaceAll"/> runs.
    /// </summary>
    public void RequestFocusOnNewBlank()
    {
        _pendingFocusOnNewBlank = true;
        TryApplyPendingFocus();
    }

    private void TryApplyPendingFocus()
    {
        if (!string.IsNullOrEmpty(_pendingFocusCallsign))
        {
            for (var i = 0; i < DepartureQueue.Count; i++)
            {
                if (string.Equals(DepartureQueue[i].AircraftId, _pendingFocusCallsign, StringComparison.OrdinalIgnoreCase))
                {
                    VisibleDepartureIndex = i;
                    _pendingFocusCallsign = null;
                    break;
                }
            }
            if (_pendingFocusCallsign is not null)
            {
                for (var i = 0; i < ArrivalQueue.Count; i++)
                {
                    if (string.Equals(ArrivalQueue[i].AircraftId, _pendingFocusCallsign, StringComparison.OrdinalIgnoreCase))
                    {
                        VisibleArrivalIndex = i;
                        _pendingFocusCallsign = null;
                        break;
                    }
                }
            }
        }

        if (_pendingFocusOnNewBlank)
        {
            // Newest blank = highest-index BlankStrip in the departure queue.
            // Walk the queue tail-first so the most recently appended blank
            // wins even when the server merged it with other items.
            for (var i = DepartureQueue.Count - 1; i >= 0; i--)
            {
                if (DepartureQueue[i].Type == StripItemType.BlankStrip)
                {
                    VisibleDepartureIndex = i;
                    _pendingFocusOnNewBlank = false;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Empties every queue and resets both carousels — the disconnect /
    /// facility-cleared reset path. Raises the same notifications as
    /// <see cref="ReplaceAll"/> so the counters and the header badge drop to
    /// zero instead of showing stale strips until the next broadcast.
    /// </summary>
    public void Clear()
    {
        Queue.Clear();
        DepartureQueue.Clear();
        ArrivalQueue.Clear();
        VisibleDepartureIndex = 0;
        VisibleArrivalIndex = 0;
        _pendingFocusCallsign = null;
        _pendingFocusOnNewBlank = false;
        OnPropertyChanged(nameof(VisibleDepartureStrip));
        OnPropertyChanged(nameof(VisibleArrivalStrip));
        OnPropertyChanged(nameof(DepartureCounter));
        OnPropertyChanged(nameof(ArrivalCounter));
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(HasQueuedStrips));
    }

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
            // With separate printers, arrival strips go to the arrival carousel and
            // everything else (departure strips, blanks) to the departure carousel;
            // a unified facility routes everything into the single carousel.
            if (SeparateArrivalCarousel && vm.Type == StripItemType.ArrivalStrip)
            {
                ArrivalQueue.Add(vm);
            }
            else
            {
                DepartureQueue.Add(vm);
            }
        }

        if (VisibleDepartureIndex >= DepartureQueue.Count)
        {
            VisibleDepartureIndex = Math.Max(0, DepartureQueue.Count - 1);
        }
        if (VisibleArrivalIndex >= ArrivalQueue.Count)
        {
            VisibleArrivalIndex = Math.Max(0, ArrivalQueue.Count - 1);
        }

        // Apply any outstanding focus request (from the "Request Strip"
        // button) now that the queue reflects the server's latest state.
        TryApplyPendingFocus();

        OnPropertyChanged(nameof(VisibleDepartureStrip));
        OnPropertyChanged(nameof(VisibleArrivalStrip));
        OnPropertyChanged(nameof(DepartureCounter));
        OnPropertyChanged(nameof(ArrivalCounter));
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(HasQueuedStrips));
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
