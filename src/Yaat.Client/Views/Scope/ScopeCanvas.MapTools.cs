using Avalonia;
using Avalonia.Input;
using SkiaSharp;
using Yaat.Sim;
using Yaat.Sim.Data.Mva;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The canvas's map tools: the Ctrl-hover MVA readout, which shows the MVA floor under the pointer while raw Control is
/// held over the canvas, on every platform; the range ring and PLACE RR's click; and the weather lines.
/// </summary>
public sealed partial class ScopeCanvas
{
    // The pointer's last position over the canvas, or null before the first move.
    private Point? _mapToolsPointer;

    // Whether Control is held, as the last pointer move or Control key event on the canvas told it.
    private bool _mvaKeyHeld;

    private ScopeRangeRing? _rangeRing;

    private ScopeWeatherText? _weather;

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
        if (wasHeld)
        {
            // The label follows the pointer.
            MarkDirty();
        }
    }

    partial void CollectMapTools(ScopeFrame frame, ref ScopeOverlays overlays) =>
        overlays = overlays with { MvaHover = MvaHoverAtPointer(), RangeRing = _rangeRing, Weather = _weather };

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
}
