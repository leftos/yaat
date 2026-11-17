using SkiaSharp;
using Yaat.Client.Views.Map;

namespace Yaat.Client.Views.Scope;

/// <summary>The Scope renderer's draw-route slot: the route being drawn, above the pilot lens and below the measure lines.</summary>
public sealed partial class ScopeRenderer
{
    private readonly ScopeDrawRouteRenderer _drawRouteRenderer = new();

    partial void DrawRouteOverlay(SKCanvas canvas, ScopeRenderSnapshot snapshot, MapViewport vp)
    {
        if (snapshot.Overlays?.DrawRoute is { } overlay)
        {
            _drawRouteRenderer.Draw(canvas, vp, overlay);
        }
    }

    partial void DisposeDrawRoute() => _drawRouteRenderer.Dispose();
}
