using Avalonia;
using Avalonia.Input;
using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Sim;
using Yaat.Sim.Data.Mva;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The canvas's map tools: the Ctrl-hover MVA readout, which shows the MVA floor under the pointer while raw Control is
/// held over the canvas, on every platform; the range ring and PLACE RR's click; the FIX overlay, labelling the fix under the
/// pointer and the selected aircraft's programmed fixes; the pinned markers; the weather lines; and datablock deconfliction.
/// </summary>
public sealed partial class ScopeCanvas
{
    // The pointer's last position over the canvas, or null before the first move.
    private Point? _mapToolsPointer;

    // Whether Control is held, as the last pointer move or Control key event on the canvas told it.
    private bool _mvaKeyHeld;

    private ScopeRangeRing? _rangeRing;

    private ScopeWeatherText? _weather;

    private IReadOnlyList<(string Name, double Lat, double Lon)>? _shownFixes;

    private IReadOnlyList<ScopeMarker> _pinnedMarkers = [];

    // The selected aircraft's programmed fixes, and the aircraft and route fields they were resolved from.
    private ProgrammedFixesKey? _programmedKey;
    private IReadOnlySet<string> _programmedFixes = EmptyFixNames;

    private static readonly IReadOnlySet<string> EmptyFixNames = new HashSet<string>();

    private readonly ScopeDeconfliction _deconfliction = new();

    private DatablockDeconflictMode _deconflictMode;

    /// <summary>How far from a fix the pointer may sit for the FIX overlay to label it, in pixels.</summary>
    internal const float FixHoverRadiusPx = 20f;

    /// <summary>The fixes the FIX overlay draws, or null while it is off; the view sets it from its view model.</summary>
    internal IReadOnlyList<(string Name, double Lat, double Lon)>? ShownFixes
    {
        get => _shownFixes;
        set
        {
            _shownFixes = value;
            MarkDirty();
        }
    }

    /// <summary>The pinned markers the canvas draws under the tracks; the view sets them from its view model.</summary>
    internal IReadOnlyList<ScopeMarker> PinnedMarkers
    {
        get => _pinnedMarkers;
        set
        {
            _pinnedMarkers = value;
            MarkDirty();
        }
    }

    /// <summary>
    /// The name of the fix of <paramref name="fixes"/> nearest <paramref name="pointer"/> on <paramref name="vp"/>, within
    /// <see cref="FixHoverRadiusPx"/>; null when none lies that near.
    /// </summary>
    internal static string? NearestFix(IReadOnlyList<(string Name, double Lat, double Lon)> fixes, MapViewport vp, SKPoint pointer)
    {
        string? nearest = null;
        float best = FixHoverRadiusPx;
        foreach ((string name, double lat, double lon) in fixes)
        {
            (float x, float y) = vp.LatLonToScreen(lat, lon);
            float distance = SKPoint.Distance(pointer, new SKPoint(x, y));
            if (distance <= best)
            {
                best = distance;
                nearest = name;
            }
        }

        return nearest;
    }

    /// <summary>Raised when a left click places the range ring's centre while PLACE RR is armed, with the clicked point.</summary>
    internal event EventHandler<LatLon>? RangeRingPlaced;

    /// <summary>The range ring the canvas draws under the tracks, or null for none; the view sets it from its view model.</summary>
    internal ScopeRangeRing? RangeRing
    {
        get => _rangeRing;
        set
        {
            _rangeRing = value;
            MarkDirty();
        }
    }

    /// <summary>The weather lines the canvas draws over everything, or null for none; the view sets it from its view model.</summary>
    internal ScopeWeatherText? Weather
    {
        get => _weather;
        set
        {
            _weather = value;
            MarkDirty();
        }
    }

    /// <summary>Whether PLACE RR is armed, so the next left press on the canvas places the ring's centre instead.</summary>
    internal bool IsPlacingRangeRing { get; set; }

    /// <summary>
    /// How overlapping datablocks spread apart; the view sets it from its view model. A change lays the frame out again, so
    /// the hit-test follows the blocks at once.
    /// </summary>
    internal DatablockDeconflictMode DeconflictMode
    {
        get => _deconflictMode;
        set
        {
            if (value == _deconflictMode)
            {
                return;
            }

            _deconflictMode = value;
            _lastFrame = null;
            MarkDirty();
        }
    }

    /// <summary>
    /// Lays the frame of <paramref name="input"/> out with its blocks deconflicted by <see cref="DeconflictMode"/>. A render's
    /// build (<paramref name="step"/>) steps Free-form's slide once more, from the last render's offsets; any other build
    /// reuses those offsets, so the hit-test and the drawn frame read one store.
    /// </summary>
    private ScopeFrame BuildDeconflictedFrame(ScopeFrameInput input, bool step) => _deconfliction.Build(_deconflictMode, input, step);

    // A left press while PLACE RR is armed places the ring there, ahead of the entry's fill, the block gestures and the
    // aircraft, so a press on a datablock or a track places too.
    partial void PressRangeRing(in ScopePress press, ref bool handled)
    {
        if (handled || !IsPlacingRangeRing || !press.Props.IsLeftButtonPressed || (Viewport.PixelWidth < 1))
        {
            return;
        }

        (double lat, double lon) = Viewport.ScreenToLatLon((float)press.Pos.X, (float)press.Pos.Y);
        RangeRingPlaced?.Invoke(this, new LatLon(lat, lon));
        handled = true;
    }

    /// <summary>
    /// Tracks Control from a key event on the canvas: pressing or releasing a Control key shows or hides the readout at once,
    /// with no pointer move.
    /// </summary>
    internal void TrackMvaKey(KeyEventArgs e, bool isDown)
    {
        bool held = (e.Key is Key.LeftCtrl or Key.RightCtrl) ? isDown : e.KeyModifiers.HasFlag(KeyModifiers.Control);
        SetMvaKeyHeld(held);
    }

    /// <summary>Whether the canvas takes Control as held. Test seam.</summary>
    internal bool IsMvaKeyHeld => _mvaKeyHeld;

    /// <summary>Hides the readout when the canvas loses the keyboard, since a Control release then never reaches it.</summary>
    internal void ReleaseMvaKey() => SetMvaKeyHeld(false);

    partial void MoveMapTools(in ScopeMove move)
    {
        _mapToolsPointer = move.Pos;
        bool wasHeld = _mvaKeyHeld;
        SetMvaKeyHeld(move.Event.KeyModifiers.HasFlag(KeyModifiers.Control));
        if (wasHeld || (_shownFixes is not null))
        {
            // The MVA label follows the pointer, and the FIX overlay labels the fix under it.
            MarkDirty();
        }
    }

    partial void CollectMapTools(ScopeFrame frame, ref ScopeOverlays overlays) =>
        overlays = overlays with
        {
            MvaHover = MvaHoverAtPointer(),
            RangeRing = _rangeRing,
            Weather = _weather,
            Fixes = FixLayer(),
            Markers = (_pinnedMarkers.Count > 0) ? _pinnedMarkers : null,
        };

    /// <summary>The FIX overlay with its programmed and hovered fixes, or null while it is off.</summary>
    private ScopeFixLayer? FixLayer()
    {
        if (_shownFixes is not { } fixes)
        {
            return null;
        }

        string? hovered =
            ((_mapToolsPointer is { } pointer) && IsPointerOver && (Viewport.PixelWidth >= 1))
                ? NearestFix(fixes, Viewport, new SKPoint((float)pointer.X, (float)pointer.Y))
                : null;
        return new ScopeFixLayer(fixes, ProgrammedFixes(), hovered);
    }

    /// <summary>
    /// The selected aircraft's programmed fixes, resolved again only when the aircraft or its route, approach, airports, STAR or
    /// runway change.
    /// </summary>
    private IReadOnlySet<string> ProgrammedFixes()
    {
        if (SelectedAircraft is not { } ac)
        {
            _programmedKey = null;
            return EmptyFixNames;
        }

        ProgrammedFixesKey key = new(ac.Callsign, ac.Route, ac.ExpectedApproach, ac.Destination, ac.Departure, ac.ActiveStarId, ac.DestinationRunway);
        if (key != _programmedKey)
        {
            _programmedKey = key;
            _programmedFixes = ProgrammedFixResolver.Resolve(
                ac.Route,
                ac.ExpectedApproach,
                ac.Destination,
                ac.Departure,
                null,
                ac.ActiveStarId,
                ac.DestinationRunway
            );
        }

        return _programmedFixes;
    }

    private void SetMvaKeyHeld(bool held)
    {
        if (held == _mvaKeyHeld)
        {
            return;
        }

        _mvaKeyHeld = held;
        MarkDirty();
    }

    /// <summary>The MVA label at the pointer while Control is held over the canvas and an MVA sector lies under it; else null.</summary>
    private ScopeMvaHover? MvaHoverAtPointer()
    {
        if (!_mvaKeyHeld || (_mapToolsPointer is not { } pointer) || !IsPointerOver || (Viewport.PixelWidth < 1))
        {
            return null;
        }

        (double lat, double lon) = Viewport.ScreenToLatLon((float)pointer.X, (float)pointer.Y);
        string? text = ScopeMvaReadout.TextAt(MvaDatabase.Default, new LatLon(lat, lon), MvaFacilityId);
        return text is null ? null : new ScopeMvaHover(text, new SKPoint((float)pointer.X, (float)pointer.Y));
    }

    /// <summary>The selected aircraft's fields its programmed fixes are computed from; a change to any recomputes them.</summary>
    private readonly record struct ProgrammedFixesKey(
        string? Callsign,
        string? Route,
        string? Approach,
        string? Destination,
        string? Departure,
        string? Star,
        string? Runway
    );
}
