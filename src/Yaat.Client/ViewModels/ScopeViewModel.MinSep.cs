using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Entries;
using Yaat.Sim;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's STARS min-sep readout (<c>MIN</c>): one pair of tracks, its own state outside the measuring tool's pool, never
/// saved. <c>MIN</c> then two clicked tracks (the second click runs it) shows the pair's projected closest approach
/// (<see cref="MinimumSeparation"/>), recomputed each time either aircraft's position, ground speed or track changes; a typed
/// callsign is FORMAT, as CRC's MIN takes no text. A new <c>MIN</c> replaces the pair, and <c>MIN</c> with Enter, either
/// aircraft leaving, data the projection cannot run on, and the per-track clear (<see cref="ClearTrackState"/>) take it down.
/// </summary>
public sealed partial class ScopeViewModel
{
    /// <summary>CRC's answer to <c>MIN</c> with one track and Enter, or with a typed track.</summary>
    internal const string MinSepNeedsTwoTracksMessage = StarsErrorText.Format;

    /// <summary>CRC's answer to a second track that is the first one.</summary>
    internal const string MinSepSameTrackMessage = StarsErrorText.DuplicateTrack;

    /// <summary>CRC's answer to a second track picked when the first is no longer displayed.</summary>
    internal const string MinSepFirstNotDisplayedMessage = StarsErrorText.IllegalTrack;

    /// <summary>
    /// The answer to a pair whose data the projection cannot run on (no ground speed or track) when it is entered. YAAT's own:
    /// CRC clears an invalid pair silently.
    /// </summary>
    internal const string NoMinSepDataMessage = "No track data";

    private AircraftModel? _minSepA;

    private AircraftModel? _minSepB;

    /// <summary>The min-sep pair the Scope draws and its projection, or null when none is shown.</summary>
    [ObservableProperty]
    private ScopeMinSep? _minSep;

    /// <summary>Takes the min-sep pair down; nothing happens when none is shown.</summary>
    public void ClearMinSep()
    {
        _minSepA?.PropertyChanged -= OnMinSepAircraftChanged;

        _minSepB?.PropertyChanged -= OnMinSepAircraftChanged;

        if ((_minSepA is not null) || (_minSepB is not null))
        {
            Main.Aircraft.CollectionChanged -= OnMinSepTrafficChanged;
        }

        _minSepA = null;
        _minSepB = null;
        MinSep = null;
    }

    /// <summary>
    /// Whether a left click on the track <paramref name="callsign"/> completes a <c>MIN</c> entry in the preview: the entry,
    /// read in the clicked track's dialect, is <c>MIN</c> with one track, so the click is the second track and runs it.
    /// </summary>
    internal bool CompletesMinSep(string callsign) =>
        (StarsEntryParser.TryRead(EntryText.Trim()) is { Need: StarsEntryNeed.MinimumSeparation, Callsign: not null, SecondCallsign: null })
        && (
            ScopeEntryClassifier.Classify(EntryText, ProfileKind, OwnerTemplateIdOf(callsign), Main.Preferences.CommandScheme).Kind
            == ScopeEntryKind.Stars
        );

    /// <summary>
    /// A left click on a track that fills the entry: the second track of a <c>MIN</c> entry is appended and runs it at once
    /// (even when it is the first track again, which the run refuses); any other entry takes the track as
    /// <see cref="FillTrack"/> does.
    /// </summary>
    internal void FillTrackFromClick(string callsign)
    {
        if (!CompletesMinSep(callsign))
        {
            FillTrack(callsign);
            return;
        }

        FillFromClick(callsign, () => SetEntry($"{EntryText.Trim()} {callsign}"));
        _pickedCallsign = callsign;
        _entryHistoryIndex = -1;
        RunEntry(OwnerTemplateIdOf(callsign));
    }

    /// <summary>
    /// Runs a <c>MIN</c> entry on the Scope: bare, it clears the pair; with two clicked tracks, it shows theirs; with one, or
    /// with a callsign that no click put there (<paramref name="clicks"/>, the entry's click record), it answers FORMAT. The
    /// answer goes to the response.
    /// </summary>
    /// <returns>False when <paramref name="entry"/> is no <c>MIN</c> entry.</returns>
    private bool TryRunMinSep(string entry, IReadOnlyList<string> clicks)
    {
        if (StarsEntryParser.TryRead(entry) is not { Need: StarsEntryNeed.MinimumSeparation } read)
        {
            return false;
        }

        Main.AddHistory("", entry);
        bool slewed = new[] { read.Callsign, read.SecondCallsign }.OfType<string>().SequenceEqual(clicks, StringComparer.OrdinalIgnoreCase);
        string? refusal = read switch
        {
            _ when !slewed => StarsErrorText.Format,
            { Callsign: null } => ClearMinSepEntry(),
            { SecondCallsign: null } => MinSepNeedsTwoTracksMessage,
            _ => SetMinSep(read.Callsign, read.SecondCallsign),
        };
        EntryResponse =
            (refusal is null)
                ? new ScopeEntryResponse(ScopeResponseKind.Accept, AcceptMessage, entry, [])
                : new ScopeEntryResponse(ScopeResponseKind.Error, refusal, entry, []);
        return true;
    }

    private string? ClearMinSepEntry()
    {
        ClearMinSep();
        return null;
    }

    /// <summary>
    /// Shows the pair the two clicked tracks' callsigns name, matched as the pilot path matches one, in place of any pair shown.
    /// </summary>
    /// <returns>Null when shown, else the refusal: a track not displayed, the same track twice, or data the projection cannot run on.</returns>
    private string? SetMinSep(string first, string second)
    {
        // A first track gone between the two clicks (deleted, or no longer matched) is a first track not displayed.
        if (CallsignMatcher.Match(first, Main.Aircraft).Match is not { } a)
        {
            return MinSepFirstNotDisplayedMessage;
        }

        if (CallsignMatcher.Match(second, Main.Aircraft).Match is not { } b)
        {
            return MainViewModel.NoAircraftMatchedMessage;
        }

        if (ReferenceEquals(a, b))
        {
            return MinSepSameTrackMessage;
        }

        if (!IsDisplayed(a))
        {
            return MinSepFirstNotDisplayedMessage;
        }

        if (!IsDisplayed(b))
        {
            return MainViewModel.NoAircraftMatchedMessage;
        }

        ClearMinSep();
        _minSepA = a;
        _minSepB = b;
        a.PropertyChanged += OnMinSepAircraftChanged;
        b.PropertyChanged += OnMinSepAircraftChanged;
        Main.Aircraft.CollectionChanged += OnMinSepTrafficChanged;
        RecomputeMinSep();
        return (MinSep is null) ? NoMinSepDataMessage : null;
    }

    // Whether the Scope draws the aircraft, by the rule its canvas filters traffic with.
    private bool IsDisplayed(AircraftModel ac) => AircraftVisibility.FilterAircraft([ac], IsTdm, false, false, null, DateTime.UtcNow).Count > 0;

    // Recomputes the pair's projection; data it cannot run on takes the pair down, as CRC drops it.
    private void RecomputeMinSep()
    {
        if ((_minSepA is not { } a) || (_minSepB is not { } b))
        {
            return;
        }

        MinSepResult result = MinimumSeparation.Compute(TrackOf(a), TrackOf(b));
        if (result is MinSepInvalid)
        {
            Log.LogDebug("Min sep {A}/{B} cleared: invalid track data", a.Callsign, b.Callsign);
            ClearMinSep();
            return;
        }

        MinSep = new ScopeMinSep(a.Callsign, b.Callsign, a.Position, b.Position, result);
    }

    private static MinSepTrack TrackOf(AircraftModel ac) => new(ac.Position, ac.TrueTrack, ac.GroundSpeed);

    private void OnMinSepAircraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AircraftModel.Position) or nameof(AircraftModel.GroundSpeed) or nameof(AircraftModel.TrueTrack))
        {
            RecomputeMinSep();
        }
    }

    // Either aircraft leaving the traffic (a delete, a reset of the list) takes the pair down.
    private void OnMinSepTrafficChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if ((_minSepA is null) || (_minSepB is null) || !Main.Aircraft.Contains(_minSepA) || !Main.Aircraft.Contains(_minSepB))
        {
            ClearMinSep();
        }
    }
}
