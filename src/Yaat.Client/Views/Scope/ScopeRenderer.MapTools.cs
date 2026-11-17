using SkiaSharp;
using Yaat.Client.Views.Map;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The renderer's map-tools slots: the range ring, the FIX overlay and the pinned markers under the tracks, the weather lines
/// and the MVA label over every other layer, and their drawing resources.
/// </summary>
public sealed partial class ScopeRenderer
{
    private readonly ScopeMapToolsRenderer _mapTools = new();

    partial void DrawMapToolsUnder(SKCanvas canvas, ScopeRenderSnapshot snapshot, MapViewport vp)
    {
        if (snapshot.Overlays is { } overlays)
        {
            _mapTools.DrawUnder(canvas, overlays, vp, snapshot.Brightness);
        }
    }

    partial void DrawMapToolsOverlay(SKCanvas canvas, ScopeRenderSnapshot snapshot, MapViewport vp)
    {
        if (snapshot.Overlays is { } overlays)
        {
            _mapTools.DrawOverlay(canvas, overlays, vp);
        }
    }

    partial void DisposeMapTools() => _mapTools.Dispose();
}
