using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Observable mirror of the shared <see cref="RangeBearingLineStore" />, bound by both the Radar and
/// Ground canvases so a measurement taken in one view appears in the other under the same slot number.
/// </summary>
/// <remarks>
/// One instance per session, owned by <c>MainViewModel</c> and handed to both map view-models. The
/// store itself is not observable, so this type republishes its state as bindable properties and
/// concentrates the tool's behaviour in one place rather than duplicating it per view.
/// </remarks>
public sealed partial class RangeBearingViewState : ObservableObject
{
    private readonly RangeBearingLineStore _store;

    public RangeBearingViewState(RangeBearingLineStore store)
    {
        _store = store;
        _store.Changed += Sync;
        Sync();
    }

    /// <summary>The placed measurements, ascending by slot number.</summary>
    [ObservableProperty]
    private IReadOnlyList<RangeBearingLine> _lines = [];

    /// <summary>The first endpoint while a measurement is half-placed, for the rubber-band preview.</summary>
    [ObservableProperty]
    private RblEndpoint? _anchor;

    /// <summary>True while the tool is intercepting map clicks to pick endpoints.</summary>
    [ObservableProperty]
    private bool _isMeasuring;

    /// <summary>True when at least one measurement is placed, for gating "clear" menu items.</summary>
    [ObservableProperty]
    private bool _hasLines;

    /// <summary>Raised with status text whenever an action needs reporting to the instructor.</summary>
    public event Action<string>? StatusReported;

    private void Sync()
    {
        Lines = _store.Lines;
        Anchor = _store.PendingAnchor;
        IsMeasuring = _store.IsArmed;
        HasLines = _store.HasLines;
    }

    /// <summary>Arms the tool so the next map click picks the first endpoint.</summary>
    public void Arm()
    {
        Report(_store.Arm() ? ArmedStatus : FullStatus);
    }

    /// <summary>
    /// Feeds the tool a clicked endpoint: the first click anchors, the second completes the measurement.
    /// </summary>
    public void Pick(RblEndpoint endpoint, RblTrackLookup lookup, RblUnits units)
    {
        if (_store.PendingAnchor is null)
        {
            Report(_store.SetAnchor(endpoint) ? AnchoredStatus : FullStatus);
            return;
        }

        var slot = _store.Complete(endpoint);
        Report(slot is { } placed ? DescribePlaced(placed, lookup, units) : FullStatus);
    }

    /// <summary>Places a whole measurement at once, for the modifier-drag path.</summary>
    public void Place(RblEndpoint from, RblEndpoint to, RblTrackLookup lookup, RblUnits units)
    {
        var slot = _store.Add(from, to);
        Report(slot is { } placed ? DescribePlaced(placed, lookup, units) : FullStatus);
    }

    /// <summary>Cancels a half-placed measurement and disarms the tool. Placed measurements stay.</summary>
    public void Cancel()
    {
        if (!_store.IsArmed && _store.PendingAnchor is null)
        {
            return;
        }

        _store.Disarm();
        Report("Measure cancelled");
    }

    /// <summary>Removes one measurement by slot number.</summary>
    public void Remove(int slot)
    {
        Report(_store.Remove(slot) ? $"Measurement {slot} removed" : $"No measurement {slot}");
    }

    /// <summary>Removes every measurement.</summary>
    public void Clear()
    {
        var had = _store.HasLines;
        _store.Clear();
        Report(had ? "Measurements cleared" : "No measurements to clear");
    }

    /// <summary>
    /// Drops measurements latched to an aircraft that no longer exists, matching CRC's behaviour when a
    /// track is dropped. Called whenever the aircraft list changes.
    /// </summary>
    public void PruneMissing(Func<string, AircraftModel?> findAircraft)
    {
        _store.PruneMissing(callsign => findAircraft(callsign) is not null);
    }

    /// <summary>Adapts an aircraft lookup into the position and groundspeed the resolver needs.</summary>
    public static RblTrackLookup TrackLookup(Func<string, AircraftModel?> findAircraft) =>
        callsign =>
        {
            var aircraft = findAircraft(callsign);
            return aircraft is null ? null : new RblTrack(aircraft.Position, aircraft.GroundSpeed);
        };

    private string DescribePlaced(int slot, RblTrackLookup lookup, RblUnits units)
    {
        // Report the reading in the status bar too — the on-scope label can sit under a datablock or off
        // the edge of the other view, and the number is the whole point of the tool.
        foreach (var resolved in RangeBearingLineResolver.Resolve(Lines, lookup, units))
        {
            if (resolved.Slot == slot)
            {
                return $"Measurement {resolved.Label}";
            }
        }

        return $"Measurement {slot} placed";
    }

    private void Report(string status) => StatusReported?.Invoke(status);

    private const string ArmedStatus = "Measure: click the first point or aircraft (Esc to cancel)";
    private const string AnchoredStatus = "Measure: click the second point or aircraft (Esc to cancel)";
    private const string FullStatus = "Measure: all 15 measurements in use — clear one first";
}
