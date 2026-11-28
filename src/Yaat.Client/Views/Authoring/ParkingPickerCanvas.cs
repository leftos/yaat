using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SkiaSharp;
using Yaat.Client.Views.Map;
using Yaat.Sim.Authoring.Gates;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// What the render thread draws one parking picker frame from: the scene, the selected stand, the hovered one and each
/// stand's ranked status.
/// </summary>
/// <param name="Scene">The picker's scene.</param>
/// <param name="Selected">The selected stand's name, or null.</param>
/// <param name="Hover">The name of the stand under the pointer, or null.</param>
/// <param name="Status">Each ranked stand's reason by trimmed name, as the canvas held it when the frame was made.</param>
public sealed record ParkingPickerRenderSnapshot(
    ParkingPickerScene Scene,
    string? Selected,
    string? Hover,
    ImmutableDictionary<string, GateReason> Status
);

/// <summary>
/// The parking picker's map: an airport's stands over its taxiway and runway centerlines, with the aircraft's current
/// stand ringed and each ranked stand coloured by its reason. A left press on a stand selects it (a double-click also
/// activates it), the stand under the pointer is highlighted with its name, the right button pans and the wheel zooms.
/// Every frame draws only from its snapshot.
/// </summary>
public sealed class ParkingPickerCanvas : MapCanvasBase, IDisposable
{
    private const float StandRadius = 3.5f;
    private const float HoverRadius = 7f;
    private const float CurrentRadius = 8f;
    private const float SelectedRadius = 10f;
    private const float LabelGap = 12f;

    // The render thread holds the lock for the whole draw, so Dispose cannot free the paints under it.
    private readonly Lock _renderLock = new();

    private readonly SKPaint _taxiwayPaint = Stroke(new SKColor(0x8A, 0x7A, 0x40), 1.5f);
    private readonly SKPaint _runwayPaint = Stroke(new SKColor(0x70, 0x70, 0x70), 5f);
    private readonly SKPaint _parkingPaint = Fill(new SKColor(0x6C, 0xB4, 0xEE));
    private readonly SKPaint _spotPaint = Fill(new SKColor(220, 160, 40));
    private readonly SKPaint _helipadPaint = Fill(new SKColor(0x7C, 0xCF, 0x7C));
    private readonly SKPaint _preferredPaint = Fill(new SKColor(0x66, 0xBB, 0x6A));
    private readonly SKPaint _tooSmallPaint = Fill(new SKColor(0xE5, 0x39, 0x35));
    private readonly SKPaint _otherAirlinePaint = Fill(new SKColor(0xAB, 0x47, 0xBC));
    private readonly SKPaint _occupiedPaint = Fill(new SKColor(0x75, 0x75, 0x75));
    private readonly SKPaint _hoverPaint = Stroke(SKColors.White, 1.5f);
    private readonly SKPaint _currentPaint = Stroke(new SKColor(0x4D, 0xD0, 0xE1), 2f);
    private readonly SKPaint _selectedPaint = Stroke(new SKColor(0xFF, 0xC1, 0x07), 2.5f);
    private readonly SKPaint _labelPaint = Fill(SKColors.White);
    private readonly SKFont _font = new(SKTypeface.Default, 13);

    private ParkingPickerScene _scene = ParkingPickerScene.Empty;
    private string? _selected;
    private string? _hover;
    private ImmutableDictionary<string, GateReason> _status = ParkingPickerScene.NoStatus;
    private bool _fitPending;
    private bool _disposed;

    /// <summary>Raised on the UI thread when a left press selects a stand; the argument is its name.</summary>
    public event EventHandler<string>? SelectionChanged;

    /// <summary>Raised on the UI thread when a stand is double-clicked; the argument is its name.</summary>
    public event EventHandler<string>? StandActivated;

    /// <summary>
    /// Gets or sets the scene drawn; set on the UI thread. Setting it selects the scene's current stand, clears the hover and
    /// takes the scene's status map as <see cref="StandStatus"/>.
    /// </summary>
    public ParkingPickerScene Scene
    {
        get => _scene;
        set
        {
            _scene = value ?? ParkingPickerScene.Empty;
            _selected = _scene.CurrentStand;
            _status = _scene.Status;
            _hover = null;
            MarkDirty();
        }
    }

    /// <summary>
    /// Gets or sets each ranked stand's reason by trimmed name, which colours it; set on the UI thread. A frame copies the map
    /// when it is made, so a later change never reaches a frame already taken.
    /// </summary>
    public ImmutableDictionary<string, GateReason> StandStatus
    {
        get => _status;
        internal set
        {
            _status = value ?? ParkingPickerScene.NoStatus;
            MarkDirty();
        }
    }

    /// <summary>Gets the selected stand's name, or null when none is selected.</summary>
    public string? Selected => _selected;

    /// <summary>Selects the stand a name names (trimmed, any case), or clears the selection; set on the UI thread.</summary>
    /// <param name="name">The stand's name, or null for none.</param>
    public void Select(string? name)
    {
        _selected = _scene.FindAnyCase(name)?.Name;
        MarkDirty();
    }

    /// <summary>Gets the name of the stand under the pointer, or null.</summary>
    public string? Hover => _hover;

    /// <summary>
    /// Fits the view to the box around the scene's stands, with the viewport's own margin; a canvas not yet laid out fits
    /// once it has a size. Does nothing when the scene has no stand.
    /// </summary>
    public void FitToStands()
    {
        if (_scene.Bounds is not { } bounds)
        {
            return;
        }

        if ((Viewport.PixelWidth <= 0) || (Viewport.PixelHeight <= 0))
        {
            _fitPending = true;
            return;
        }

        _fitPending = false;
        Viewport.FitBounds(bounds.MinLat, bounds.MaxLat, bounds.MinLon, bounds.MaxLon);
        OnViewportChanged();
        MarkDirty();
    }

    /// <summary>Frees the paints; the canvas draws nothing afterwards.</summary>
    public void Dispose()
    {
        lock (_renderLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SKPaint[] paints =
            [
                _taxiwayPaint,
                _runwayPaint,
                _parkingPaint,
                _spotPaint,
                _helipadPaint,
                _preferredPaint,
                _tooSmallPaint,
                _otherAirlinePaint,
                _occupiedPaint,
                _hoverPaint,
                _currentPaint,
                _selectedPaint,
                _labelPaint,
            ];
            foreach (SKPaint paint in paints)
            {
                paint.Dispose();
            }

            _font.Dispose();
        }
    }

    /// <summary>Builds the frame the next render would draw. Test seam.</summary>
    /// <returns>The frame, or null once the canvas is disposed.</returns>
    internal ParkingPickerRenderSnapshot? SnapshotForTesting() => CreateRenderSnapshot() as ParkingPickerRenderSnapshot;

    /// <summary>Draws the current frame into a bitmap the canvas's size. Test seam.</summary>
    /// <returns>The frame; the caller disposes it.</returns>
    internal SKBitmap RenderForTesting() => RenderForTesting(CreateRenderSnapshot());

    /// <summary>Draws a given frame into a bitmap the canvas's size, from that frame alone. Test seam.</summary>
    /// <param name="snapshot">The frame to draw.</param>
    /// <returns>The frame; the caller disposes it.</returns>
    internal SKBitmap RenderForTesting(object? snapshot)
    {
        var bitmap = new SKBitmap(Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
        using var canvas = new SKCanvas(bitmap);
        Draw(canvas, Viewport.Clone(), snapshot);
        canvas.Flush();
        return bitmap;
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_fitPending)
        {
            FitToStands();
        }
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind.GetMouseButton() != MouseButton.Left)
        {
            return;
        }

        Focus();
        e.Handled = true;
        Point at = e.GetPosition(this);
        if (ParkingPickerHitTest.Find(_scene, Viewport, (float)at.X, (float)at.Y) is not { } stand)
        {
            return;
        }

        _selected = stand.Name;
        MarkDirty();
        SelectionChanged?.Invoke(this, stand.Name);
        if (e.ClickCount >= 2)
        {
            StandActivated?.Invoke(this, stand.Name);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Handled)
        {
            return;
        }

        Point at = e.GetPosition(this);
        SetHover(ParkingPickerHitTest.Find(_scene, Viewport, (float)at.X, (float)at.Y)?.Name);
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(null);
    }

    /// <inheritdoc />
    protected override object? CreateRenderSnapshot() => _disposed ? null : new ParkingPickerRenderSnapshot(_scene, _selected, _hover, _status);

    /// <inheritdoc />
    protected override void RenderFromSnapshot(SKCanvas canvas, MapViewport viewport, object? snapshot) => Draw(canvas, viewport, snapshot);

    private static SKPaint Stroke(SKColor color, float width) =>
        new()
        {
            Color = color,
            StrokeWidth = width,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
        };

    private static SKPaint Fill(SKColor color) =>
        new()
        {
            Color = color,
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };

    private static SKPoint Project(MapViewport viewport, double lat, double lon)
    {
        (float x, float y) = viewport.LatLonToScreen(lat, lon);
        return new SKPoint(x, y);
    }

    private void SetHover(string? name)
    {
        if (!string.Equals(_hover, name, StringComparison.Ordinal))
        {
            _hover = name;
            MarkDirty();
        }
    }

    private void Draw(SKCanvas canvas, MapViewport viewport, object? snapshot)
    {
        if (snapshot is not ParkingPickerRenderSnapshot frame)
        {
            return;
        }

        lock (_renderLock)
        {
            if (_disposed)
            {
                return;
            }

            canvas.Clear(new SKColor(0x1A, 0x1A, 0x1A));
            DrawLines(canvas, viewport, frame.Scene);
            DrawStands(canvas, viewport, frame.Scene, frame.Status);
            Ring(canvas, viewport, frame.Scene.Find(frame.Scene.CurrentStand), _currentPaint, CurrentRadius);
            Ring(canvas, viewport, frame.Scene.Find(frame.Hover), _hoverPaint, HoverRadius);
            Ring(canvas, viewport, frame.Scene.Find(frame.Selected), _selectedPaint, SelectedRadius);
            Label(canvas, viewport, frame.Scene.Find(frame.Selected));
            if (!string.Equals(frame.Hover, frame.Selected, StringComparison.Ordinal))
            {
                Label(canvas, viewport, frame.Scene.Find(frame.Hover));
            }
        }
    }

    private void DrawLines(SKCanvas canvas, MapViewport viewport, ParkingPickerScene scene)
    {
        foreach (ParkingPickerLine line in scene.Lines)
        {
            SKPaint paint = (line.Kind == ParkingPickerLineKind.Runway) ? _runwayPaint : _taxiwayPaint;
            canvas.DrawLine(Project(viewport, line.FromLat, line.FromLon), Project(viewport, line.ToLat, line.ToLon), paint);
        }
    }

    private void DrawStands(SKCanvas canvas, MapViewport viewport, ParkingPickerScene scene, ImmutableDictionary<string, GateReason> status)
    {
        foreach (ParkingPickerStand stand in scene.Stands)
        {
            SKPaint paint = status.TryGetValue(stand.Name.Trim(), out GateReason reason) ? StatusPaint(reason) : TypePaint(stand.Type);
            canvas.DrawCircle(Project(viewport, stand.Lat, stand.Lon), StandRadius, paint);
        }
    }

    private SKPaint TypePaint(GroundNodeType type) =>
        type switch
        {
            GroundNodeType.Spot => _spotPaint,
            GroundNodeType.Helipad => _helipadPaint,
            _ => _parkingPaint,
        };

    private SKPaint StatusPaint(GateReason reason) =>
        reason switch
        {
            GateReason.Preferred => _preferredPaint,
            GateReason.TooSmall => _tooSmallPaint,
            GateReason.OtherAirline => _otherAirlinePaint,
            GateReason.Occupied => _occupiedPaint,
            _ => _parkingPaint,
        };

    private static void Ring(SKCanvas canvas, MapViewport viewport, ParkingPickerStand? stand, SKPaint paint, float radius)
    {
        if (stand is not null)
        {
            canvas.DrawCircle(Project(viewport, stand.Lat, stand.Lon), radius, paint);
        }
    }

    private void Label(SKCanvas canvas, MapViewport viewport, ParkingPickerStand? stand)
    {
        if (stand is not null)
        {
            SKPoint at = Project(viewport, stand.Lat, stand.Lon);
            canvas.DrawText(stand.Name, at.X + LabelGap, at.Y - LabelGap, SKTextAlign.Left, _font, _labelPaint);
        }
    }
}
