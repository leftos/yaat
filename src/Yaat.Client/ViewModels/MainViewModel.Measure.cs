using System.Globalization;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope.Entries;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Which map surface the distance measuring tool's typed <c>.rbl A B</c> places on: the last surface (a Ground or
/// Scope canvas) that took the focus or a press, the primary Scope before any, and the primary Scope again once the Scope that
/// took it closes.
/// A Scope line draws only on the Scope window that placed it, a typed one on the Scope holding the surface; the surface is
/// held as the Scope that took it, whichever has the focus.
/// </summary>
public partial class MainViewModel
{
    private RblView _activeMeasureView = ScopeViewModel.MeasureView;

    /// <summary>The Scope that last took the measuring surface, so a closed or reopened Scope does not keep it.</summary>
    private ScopeViewModel? _activeMeasureScope;

    /// <summary>
    /// The surface a typed <c>.rbl A B</c> places on: the last one that took the focus or a press; the Scope before any, which
    /// is then <see cref="ActiveMeasureScope"/>'s.
    /// </summary>
    public RblView ActiveMeasureView => _activeMeasureView;

    /// <summary>
    /// The Scope that holds the measuring surface: the one that took it while it is still one of <see cref="AllScopeViews"/>,
    /// else the primary. Null when the surface is not a Scope's, and while the constructor builds the primary.
    /// </summary>
    public ScopeViewModel? ActiveMeasureScope
    {
        get
        {
            if (_activeMeasureView != RblView.Scope)
            {
                return null;
            }

            return ((_activeMeasureScope is { } scope) && AllScopeViews.Contains(scope)) ? scope : PrimaryScope;
        }
    }

    /// <summary>The Scope recorded as having taken the measuring surface, open or not. Test seam.</summary>
    internal ScopeViewModel? HeldMeasureScope => _activeMeasureScope;

    /// <summary>Records that the Ground surface took the focus or a press.</summary>
    public void SetGroundMeasureActive()
    {
        _activeMeasureView = GroundViewModel.MeasureView;
        _activeMeasureScope = null;
    }

    /// <summary>Records that <paramref name="scope"/>'s canvas took the focus or a press, making it the measuring surface.</summary>
    public void SetActiveMeasureScope(ScopeViewModel scope)
    {
        _activeMeasureView = ScopeViewModel.MeasureView;
        _activeMeasureScope = scope;
    }

    /// <summary>
    /// The view tag, track lookup, units and owning surface a measurement placed on the <paramref name="view"/> surface takes:
    /// for the Scope, <paramref name="scope"/>, else <see cref="ActiveMeasureScope"/>, else the primary owns it; no other view
    /// has an owning surface.
    /// </summary>
    internal MeasureTarget MeasureSurface(RblView view, ScopeViewModel? scope) =>
        view switch
        {
            RblView.Ground => new MeasureTarget(GroundViewModel.MeasureView, Ground.MeasureTrackLookup, GroundViewModel.MeasureUnits, null),
            _ => (scope ?? ActiveMeasureScope ?? PrimaryScope).MeasureTarget,
        };

    // The anchor a *T entry from a typed line started (its RangeBearingViewState.PendingAnchorId) and that line's sink, whose
    // next line completes it while that anchor is still the pending one.
    private (long AnchorId, ICommandTextSink Sink)? _lineRangeBearingAnchor;

    /// <summary>
    /// True for a CRC STARS <c>*T</c> entry, any line led by <c>*T</c>: <c>*T</c>, <c>*T1</c>, <c>*TOAK</c>, and the shapes
    /// that are FORMAT.
    /// </summary>
    private static bool IsStarTCommand(string text) => text.StartsWith("*T", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs a <c>*T</c> entry from a typed line by CRC's grammar (<see cref="StarsEntryParser.ReadRangeBearing"/>), the parse the
    /// Scope runs its own on. A typed line has no slew: <c>*T</c> alone clears every measurement, <c>*T</c> and a line number
    /// removes that line, and <c>*T</c> and a beacon code or a fix/ACID anchors a line on the active measure surface, which the
    /// same sink's next line completes (<see cref="TryCompleteLineRangeBearing"/>). Refusals are CRC's error categories.
    /// </summary>
    private void RunLineRangeBearing(StarsRblEntry rbl, ICommandTextSink sink)
    {
        switch (rbl.Action)
        {
            case StarsRblAction.Bare:
                sink.ReportStatus(Measure.ClearSilently());
                break;
            case StarsRblAction.Remove:
                (bool removed, string status) = Measure.RemoveSilently(rbl.Slot);
                ReportMeasure(sink, removed, removed ? status : StarsErrorText.RblId);
                break;
            case StarsRblAction.RblId:
                sink.ReportError(StarsErrorText.RblId);
                break;
            case StarsRblAction.Beacon or StarsRblAction.Name:
                StartLineRangeBearing(rbl, sink);
                break;
            default:
                sink.ReportError(StarsErrorText.Format);
                break;
        }
    }

    // *T and a beacon code or a fix/ACID from a typed line: anchors on the active measure surface, NO FLIGHT, DUP TRK or
    // CAPACITY else.
    private void StartLineRangeBearing(StarsRblEntry rbl, ICommandTextSink sink)
    {
        (RblEndpoint? endpoint, string? error) = ResolveRangeBearingTarget(rbl);
        if (endpoint is null)
        {
            sink.ReportError(error ?? StarsErrorText.NoFlight);
            return;
        }

        (bool anchored, string status) = Measure.SetAnchorSilently(endpoint.Value, MeasureSurface(ActiveMeasureView, null));
        if (!anchored || (Measure.PendingAnchorId is not { } anchorId))
        {
            sink.ReportError(StarsErrorText.Capacity);
            return;
        }

        _lineRangeBearingAnchor = (anchorId, sink);
        sink.ReportStatus(status);
    }

    /// <summary>
    /// The line after a <c>*T</c> anchor from the same <paramref name="sink"/>, read by CRC's grammar
    /// (<see cref="StarsEntryParser.ReadRangeBearingCompletion"/>). A beacon code or a fix/ACID that names a fix or a track
    /// completes the line (DUP TRK and CAPACITY keep the anchor pending). The main line is never locked by the anchor: any other
    /// line, one-word lines that name nothing included (a global verb, a partial callsign), cancels it and runs as usual.
    /// </summary>
    /// <returns>False when no anchor of this sink's is pending or the line completes nothing, so the line runs as usual.</returns>
    private bool TryCompleteLineRangeBearing(string text, ICommandTextSink sink)
    {
        if (!HasPendingLineRangeBearing(sink))
        {
            return false;
        }

        StarsRblEntry rbl = StarsEntryParser.ReadRangeBearingCompletion(text);
        (RblEndpoint? endpoint, string? error) = (rbl.Action == StarsRblAction.Format) ? (null, null) : ResolveRangeBearingTarget(rbl);
        if ((endpoint is null) && (error != StarsErrorText.DuplicateTrack))
        {
            ResetLineRangeBearing();
            return false;
        }

        CompleteLineRangeBearing(endpoint, error, sink);
        return true;
    }

    // Whether a *T anchor sink's earlier line started is still the pending anchor.
    private bool HasPendingLineRangeBearing(ICommandTextSink sink) =>
        (_lineRangeBearingAnchor is { } pending) && Equals(pending.Sink, sink) && (Measure.PendingAnchorId == pending.AnchorId);

    // Completes the pending main-line *T anchor at endpoint, on the surface that owns the anchor; error (DUP TRK) when none.
    private void CompleteLineRangeBearing(RblEndpoint? endpoint, string? error, ICommandTextSink sink)
    {
        if ((endpoint is null) || (Measure.Anchor is not { } anchor))
        {
            sink.ReportError(error ?? StarsErrorText.Format);
            return;
        }

        (int? slot, string status) = Measure.CompleteSilently(endpoint.Value, MeasureSurface(anchor.View, anchor.Surface as ScopeViewModel));
        if (slot is null)
        {
            sink.ReportError(StarsErrorText.Capacity);
            return;
        }

        _lineRangeBearingAnchor = null;
        sink.ReportStatus(status);
    }

    /// <summary>
    /// Drops the main line's pending <c>*T</c> anchor when it sits on <paramref name="scope"/>, as that Scope leaves the STARS
    /// profile.
    /// </summary>
    internal void ResetLineRangeBearingOn(ScopeViewModel scope)
    {
        if (
            (_lineRangeBearingAnchor is { } pending)
            && (Measure.PendingAnchorId == pending.AnchorId)
            && ReferenceEquals(Measure.Anchor?.Surface, scope)
        )
        {
            ResetLineRangeBearing();
        }
    }

    // Drops the main line's pending *T anchor, the store's anchor with it while it is still that one. The lines that skip the
    // completion (chat, a CRC-prefixed line, the staged say-again transcript) call it too.
    private void ResetLineRangeBearing()
    {
        if (_lineRangeBearingAnchor is { } pending)
        {
            Measure.DropAnchor(pending.AnchorId);
        }

        _lineRangeBearingAnchor = null;
    }

    /// <summary>
    /// Drops every pending <c>*T</c> anchor, the main line's and each Scope's, as the world they were taken in goes: a scenario
    /// load, unload or reload, a rewind, a recording load, leaving the room.
    /// </summary>
    private void ResetRangeBearingEntries()
    {
        ResetLineRangeBearing();
        foreach (ScopeViewModel scope in AllScopeViews)
        {
            scope.ResetRangeBearingEntry();
        }
    }

    /// <summary>
    /// Resolves the beacon code or the fix/ACID of a <c>*T</c> entry, or of the entry completing its line, by CRC's rules: a
    /// beacon code names the track it belongs to (<see cref="BeaconTrack"/>); a token under three characters is an ACID only; a
    /// longer one is a fix or airport first (once the navigation database has loaded), then an ACID. A callsign endpoint
    /// latches to its aircraft.
    /// </summary>
    /// <returns>The endpoint, or the error: <c>NO FLIGHT</c> when nothing matches, <c>DUP TRK</c> for a code two tracks share.</returns>
    internal (RblEndpoint? Endpoint, string? Error) ResolveRangeBearingTarget(StarsRblEntry rbl)
    {
        string token = rbl.Token;
        if (rbl.Action == StarsRblAction.Beacon)
        {
            return BeaconTrack(token);
        }

        if ((token.Length >= 3) && _commandInput.NavDbReady && (NavigationDatabase.Instance.GetFixPosition(token) is { } fix))
        {
            return (RblEndpoint.AtPoint(new LatLon(fix.Lat, fix.Lon), token), null);
        }

        return TrackEndpoint(Aircraft.FirstOrDefault(ac => string.Equals(ac.Callsign, token, StringComparison.OrdinalIgnoreCase)));
    }

    // The track a beacon code names, as CRC's TrackManager.LookupAssociatedTrack finds it: the code its flight plan assigns
    // or the code it squawks; two tracks matching is DUP TRK. A missing code (0) never matches.
    private (RblEndpoint? Endpoint, string? Error) BeaconTrack(string code)
    {
        AircraftModel[] matches = [.. Aircraft.Where(ac => HasBeaconCode(ac.AssignedBeaconCode, code) || HasBeaconCode(ac.BeaconCode, code))];
        return (matches.Length > 1) ? (null, StarsErrorText.DuplicateTrack) : TrackEndpoint(matches.FirstOrDefault());
    }

    private static bool HasBeaconCode(uint beaconCode, string code) =>
        (beaconCode > 0) && (beaconCode.ToString("0000", CultureInfo.InvariantCulture) == code);

    private static (RblEndpoint? Endpoint, string? Error) TrackEndpoint(AircraftModel? track) =>
        (track is null) ? (null, StarsErrorText.NoFlight) : (RblEndpoint.OnAircraft(track.Callsign), null);
}
