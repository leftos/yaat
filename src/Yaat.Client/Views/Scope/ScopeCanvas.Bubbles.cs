using Avalonia;
using Avalonia.Input;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The Scope canvas's speech bubbles: the visibility rule that surfaces a ground aircraft carrying one, the track order that
/// draws bubbled aircraft over every block, the placement the renderer fills, and the click that dismisses a bubble.
/// </summary>
public sealed partial class ScopeCanvas
{
    // The pointer may move this far between the press on a bubble and its release and still dismiss it.
    private const double BubbleClickMaxMovementSq = 25;

    // The press on a bubble the release that follows must be tested against; null when the press landed elsewhere.
    private BubblePress? _bubblePress;

    /// <summary>
    /// The airport whose ground view is open, from the view: a ground aircraft carrying a bubble is surfaced on this canvas while
    /// no ground view shows its airport. Null claims no ground view is open.
    /// </summary>
    public Func<string?>? GroundShownAirportIdProvider { get; set; }

    /// <summary>The speech bubbles the current frame draws, placed as the next frame places them. Test and hit-test seam.</summary>
    internal IReadOnlyList<ScopeBubble> CurrentBubbles => PlaceBubbles(CurrentFrame);

    /// <summary>The preferences a frame's speech bubbles are placed under.</summary>
    internal ScopeBubbleOptions BubbleOptions() => new(Preferences?.ShowSpeechBubbles ?? false, Preferences?.AlwaysShowGroundBubblesOnRadar ?? false);

    /// <summary>
    /// The aircraft this canvas draws: the shared visibility rule with the speech-bubble switches taken from the preferences, so
    /// the ground aircraft a bubble is attached to reaches the frame.
    /// </summary>
    internal IReadOnlyList<AircraftModel> FilterVisible()
    {
        ScopeBubbleOptions options = BubbleOptions();
        return AircraftVisibility.FilterAircraft(
            Aircraft,
            ShowTopDown,
            options.Show,
            options.AlwaysShowGround,
            GroundShownAirportIdProvider?.Invoke(),
            DateTime.UtcNow
        );
    }

    /// <summary>
    /// <paramref name="frame"/>'s tracks in draw order with the bubbled aircraft moved above the plain ones; every dwelled track
    /// stays last, so it keeps drawing on top and hit-testing first.
    /// </summary>
    internal ScopeFrame OrderBubbledLast(ScopeFrame frame)
    {
        if (!BubbleOptions().Show)
        {
            return frame;
        }

        DateTime nowUtc = DateTime.UtcNow;
        var plain = new List<ScopeTrack>(frame.Tracks.Count);
        var bubbled = new List<ScopeTrack>();
        var dwelled = new List<ScopeTrack>();
        foreach (ScopeTrack track in frame.Tracks)
        {
            OrderTrack(track, nowUtc, plain, bubbled, dwelled);
        }

        if (bubbled.Count == 0)
        {
            return frame;
        }

        var ordered = new List<ScopeTrack>(frame.Tracks.Count);
        ordered.AddRange(plain);
        ordered.AddRange(bubbled);
        ordered.AddRange(dwelled);
        return frame with { Tracks = ordered };
    }

    /// <summary>The bubble a point lands on, the topmost drawn first, or null when the point misses every bubble.</summary>
    internal ScopeBubble? FindBubbleAt(Point pos)
    {
        IReadOnlyList<ScopeBubble> bubbles = CurrentBubbles;
        for (int i = bubbles.Count - 1; i >= 0; i--)
        {
            if (bubbles[i].Rect.Contains((float)pos.X, (float)pos.Y))
            {
                return bubbles[i];
            }
        }

        return null;
    }

    partial void CollectBubbles(ScopeFrame frame, ref ScopeOverlays overlays) => overlays = overlays with { Bubbles = PlaceBubbles(frame) };

    // A left press on a bubble is recorded, never taken: the pan below still starts, so a drag past the threshold keeps the
    // bubble. A press a rung above already took records nothing.
    partial void PressBubble(in ScopePress press, ref bool handled)
    {
        _bubblePress = null;
        if (handled || (press.Props.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed))
        {
            return;
        }

        if (FindBubbleAt(press.Pos) is not { } bubble)
        {
            return;
        }

        _bubblePress = new BubblePress(bubble.Callsign, press.Pos);
    }

    // A release within five pixels of the press on a bubble dismisses it, the click taken so no later release rung runs; a
    // release further away is a pan and leaves the bubble alone.
    partial void ReleaseBubble(in ScopeRelease release, ref bool handled)
    {
        if (_bubblePress is not { } press)
        {
            return;
        }

        _bubblePress = null;
        if (handled || (release.Event.InitialPressMouseButton != MouseButton.Left))
        {
            return;
        }

        double dx = release.Pos.X - press.Pos.X;
        double dy = release.Pos.Y - press.Pos.Y;
        if (((dx * dx) + (dy * dy)) > BubbleClickMaxMovementSq)
        {
            return;
        }

        if (DismissBubble(press.Callsign))
        {
            handled = true;
        }
    }

    private static void OrderTrack(ScopeTrack track, DateTime nowUtc, List<ScopeTrack> plain, List<ScopeTrack> bubbled, List<ScopeTrack> dwelled)
    {
        if (track.IsDwelled)
        {
            dwelled.Add(track);
            return;
        }

        if (IsBubbleActive(track.Aircraft.SpeechBubble, nowUtc))
        {
            bubbled.Add(track);
            return;
        }

        plain.Add(track);
    }

    private static bool IsBubbleActive(AircraftSpeechBubble? bubble, DateTime nowUtc) => (bubble is not null) && (bubble.ExpiresAt > nowUtc);

    private IReadOnlyList<ScopeBubble> PlaceBubbles(ScopeFrame frame) =>
        ScopeBubbles.Place(frame.Tracks, BubbleOptions(), DateTime.UtcNow, MeasureFont(DatablockFontSize()));

    private bool DismissBubble(string callsign)
    {
        ScopeTrack? track = CurrentFrame.Tracks.FirstOrDefault(track => string.Equals(track.Aircraft.Callsign, callsign, StringComparison.Ordinal));
        if (track?.Aircraft.SpeechBubble is null)
        {
            return false;
        }

        track.Aircraft.SpeechBubble = null;
        _lastFrame = null;
        MarkDirty();
        return true;
    }

    /// <summary>A press on a bubble: whose bubble it is, and where the press landed for the release's movement test.</summary>
    private readonly record struct BubblePress(string Callsign, Point Pos);
}
