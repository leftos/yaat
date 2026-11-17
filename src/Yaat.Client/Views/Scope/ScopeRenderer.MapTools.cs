using SkiaSharp;
using Yaat.Client.Views.Map;

namespace Yaat.Client.Views.Scope;

/// <summary>The renderer's map-tools slots: the overlay over every other layer, and its drawing resources.</summary>
public sealed partial class ScopeRenderer
{
    private readonly ScopeMapToolsRenderer _mapTools = new();

    partial void DrawMapToolsOverlay(SKCanvas canvas, ScopeRenderSnapshot snapshot, MapViewport vp)
    {
        if (snapshot.Overlays is { } overlays)
        {
            _mapTools.DrawOverlay(canvas, overlays);
        }
    }

    partial void DisposeMapTools() => _mapTools.Dispose();
}
