using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Observable mirror of the shared <see cref="RangeBearingLineStore" />, bound by the Radar, Ground and
/// Scope canvases. Slot numbers are globally unique across the views, but each measurement is
/// tagged with the view it was created in and only renders there.
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

    /// <summary>
    /// The first endpoint while a measurement is half-placed, tagged with the view it was picked in so
    /// only that view shows the rubber-band preview.
    /// </summary>
    [ObservableProperty]
    private RblPendingAnchor? _anchor;

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
    public void Arm() => Report(ArmSilently().Status);

    /// <summary>Arms the tool as <see cref="Arm"/> does, returning its status instead of reporting it.</summary>
    /// <returns>Whether the tool armed (false when every slot is in use), and the status to show.</returns>
    internal (bool Armed, string Status) ArmSilently() => _store.Arm() ? (true, ArmedStatus) : (false, FullStatus);

    /// <summary>
    /// Whether a half-placed measurement waits for its second point on <paramref name="view"/>'s <paramref name="surface"/>:
    /// an anchor another Scope window picked counts as none on a Scope, and the next pick there anchors anew.
    /// </summary>
    public bool HasAnchorFor(RblView view, object? surface) => (Anchor is { } anchor) && !IsOtherScopesAnchor(anchor, view, surface);

    /// <summary>
    /// Feeds the tool a clicked endpoint on <paramref name="target"/>: the first click anchors, the second completes the
    /// measurement, which belongs to the view and surface the anchor was picked on. A pick on a Scope while another Scope
    /// window holds the anchor cancels that anchor and anchors here.
    /// </summary>
    public void Pick(RblEndpoint endpoint, MeasureTarget target)
    {
        if ((_store.PendingAnchor is not { } anchor) || IsOtherScopesAnchor(anchor, target.View, target.Surface))
        {
            Report(_store.SetAnchor(endpoint, target.View, target.Surface) ? AnchoredStatus : FullStatus);
            return;
        }

        int? slot = _store.Complete(endpoint);
        Report(slot is { } placed ? DescribePlaced(placed, target with { View = anchor.View, Surface = anchor.Surface }) : FullStatus);
    }

    /// <summary>Places a whole measurement at once on <paramref name="target"/>, for the modifier-drag and text-command paths.</summary>
    /// <returns>The slot number the measurement took, or null when every slot is in use.</returns>
    public int? Place(RblEndpoint from, RblEndpoint to, MeasureTarget target)
    {
        (int? slot, string status) = PlaceSilently(from, to, target);
        Report(status);
        return slot;
    }

    /// <summary>Places a whole measurement as <see cref="Place"/> does, returning its status instead of reporting it.</summary>
    /// <returns>The slot the measurement took (null when every slot is in use), and the status to show.</returns>
    internal (int? Slot, string Status) PlaceSilently(RblEndpoint from, RblEndpoint to, MeasureTarget target)
    {
        int? slot = _store.Add(from, to, target.View, target.Surface);
        return (slot, slot is { } placed ? DescribePlaced(placed, target) : FullStatus);
    }

    /// <summary>Drops the lines <paramref name="surface"/> placed and the anchor it holds, as its Scope window closes.</summary>
    internal void RemoveSurface(object surface) => _store.RemoveSurface(surface);

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
    /// <returns>True when the slot held a measurement; false when it was empty or out of range.</returns>
    public bool Remove(int slot)
    {
        (bool removed, string status) = RemoveSilently(slot);
        Report(status);
        return removed;
    }

    /// <summary>Removes one measurement as <see cref="Remove"/> does, returning its status instead of reporting it.</summary>
    /// <returns>Whether the slot held a measurement, and the status to show.</returns>
    internal (bool Removed, string Status) RemoveSilently(int slot)
    {
        bool removed = _store.Remove(slot);
        return (removed, removed ? $"Measurement {slot} removed" : $"No measurement {slot}");
    }

    /// <summary>Removes every measurement.</summary>
    public void Clear() => Report(ClearSilently());

    /// <summary>Removes every measurement as <see cref="Clear"/> does, returning its status instead of reporting it.</summary>
    internal string ClearSilently()
    {
        bool had = _store.HasLines;
        _store.Clear();
        return had ? "Measurements cleared" : "No measurements to clear";
    }

    /// <summary>
    /// Drops measurements latched to an aircraft that no longer exists, matching CRC's behaviour when a
    /// track is dropped. Called whenever the aircraft list changes.
    /// </summary>
    public void PruneMissing(Func<string, AircraftModel?> findAircraft) => _store.PruneMissing(callsign => findAircraft(callsign) is not null);

    /// <summary>Adapts an aircraft lookup into the position and groundspeed the resolver needs.</summary>
    public static RblTrackLookup TrackLookup(Func<string, AircraftModel?> findAircraft) =>
        callsign =>
        {
            AircraftModel? aircraft = findAircraft(callsign);
            return aircraft is null ? null : new RblTrack(aircraft.Position, aircraft.GroundSpeed);
        };

    // Whether a Scope pick on surface meets an anchor another Scope window picked.
    private static bool IsOtherScopesAnchor(RblPendingAnchor anchor, RblView view, object? surface) =>
        (view == RblView.Scope) && (anchor.View == RblView.Scope) && !ReferenceEquals(anchor.Surface, surface);

    private string DescribePlaced(int slot, MeasureTarget target)
    {
        // Report the reading in the status bar too — the on-scope label can sit under a datablock or off
        // the edge of the view, and the number is the whole point of the tool.
        foreach (ResolvedRbl resolved in RangeBearingLineResolver.Resolve(Lines, target.Lookup, target.Units, target.View, target.Surface))
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

    /// <summary>The status every placement reports when all 15 slots are in use.</summary>
    public const string FullStatus = "Measure: all 15 measurements in use — clear one first";
}

/// <summary>
/// How placing a measurement from two typed endpoints ended: the slot it took, or the error the status line shows (an
/// unresolvable or ambiguous endpoint, or every slot in use). Exactly one of the two is non-null.
/// </summary>
public readonly record struct MeasurePlacement(int? Slot, string? Error);

/// <summary>
/// Where a measurement goes: the <paramref name="View"/> it belongs to, the <paramref name="Lookup"/> its latched ends
/// resolve against, the <paramref name="Units"/> its reading is in, and the <paramref name="Surface"/> that owns it: for a
/// Scope, the Scope window; null for every other view.
/// </summary>
public readonly record struct MeasureTarget(RblView View, RblTrackLookup Lookup, RblUnits Units, object? Surface);
