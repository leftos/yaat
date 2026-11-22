using Avalonia.Controls;
using Avalonia.Input;
using SkiaSharp;
using Yaat.Client.Views.Map;
using Yaat.Sim.Authoring.AirportMap;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// The Airport Editor's map: draws the open map's features through an <see cref="AirportEditorRenderer"/>, pans on a
/// middle drag (the right button is kept for its context menus), zooms with the wheel, and fits itself to the map.
/// </summary>
public sealed class AirportEditorCanvas : MapCanvasBase, IDisposable
{
    /// <summary>The mode the HUD shows.</summary>
    public const string ModeText = "Select";

    private readonly AirportEditorRenderer _renderer = new();

    // The render thread holds the lock for the whole draw, so Dispose cannot free the renderer's Skia objects under it.
    private readonly Lock _renderLock = new();

    private AirportMapDocument? _document;
    private AirportEditorScene _scene = AirportEditorScene.Empty;
    private bool _fitPending;
    private bool _disposed;

    /// <summary>Gets or sets the map drawn; set on the UI thread. A plain property, read into each frame's snapshot.</summary>
    public AirportMapDocument? Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(_document, value))
            {
                return;
            }

            _document = value;
            _scene = AirportEditorScene.Build(value);
            MarkDirty();
        }
    }

    /// <inheritdoc />
    protected override MouseButton PanButton => MouseButton.Middle;

    /// <summary>
    /// Fits the view to the box around every feature of the open map, with the viewport's own margin; a canvas not yet
    /// laid out fits once it has a size. Does nothing when no feature has a position.
    /// </summary>
    public void FitToFeatures()
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

    /// <summary>Frees the renderer's Skia objects; the canvas draws nothing afterwards.</summary>
    public void Dispose()
    {
        lock (_renderLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _renderer.Dispose();
        }
    }

    /// <summary>Draws the current frame into a bitmap the canvas's size. Test seam.</summary>
    /// <returns>The frame; the caller disposes it.</returns>
    internal SKBitmap RenderForTesting()
    {
        var bitmap = new SKBitmap(Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
        using var canvas = new SKCanvas(bitmap);
        Draw(canvas, Viewport.Clone(), CreateRenderSnapshot());
        canvas.Flush();
        return bitmap;
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_fitPending)
        {
            FitToFeatures();
        }
    }

    /// <inheritdoc />
    protected override object? CreateRenderSnapshot() => _disposed ? null : new AirportEditorRenderSnapshot(_scene, ModeText);

    /// <inheritdoc />
    protected override void RenderFromSnapshot(SKCanvas canvas, MapViewport viewport, object? snapshot) => Draw(canvas, viewport, snapshot);

    private void Draw(SKCanvas canvas, MapViewport viewport, object? snapshot)
    {
        if (snapshot is not AirportEditorRenderSnapshot frame)
        {
            return;
        }

        lock (_renderLock)
        {
            if (!_disposed)
            {
                _renderer.Render(canvas, viewport, frame);
            }
        }
    }
}
