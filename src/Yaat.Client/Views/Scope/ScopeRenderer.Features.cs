using SkiaSharp;
using Yaat.Client.Views.Map;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The Scope renderer's feature slots: the map tools under the tracks, the feature overlays between the pilot lens and the
/// measure layer, the map tools over everything, and the resources each feature disposes.
/// </summary>
public sealed partial class ScopeRenderer
{
    partial void DrawMapToolsUnder(SKCanvas canvas, ScopeRenderSnapshot snapshot, MapViewport vp);

    partial void DrawRouteOverlay(SKCanvas canvas, ScopeRenderSnapshot snapshot, MapViewport vp);

    partial void DrawHeadingVector(SKCanvas canvas, ScopeRenderSnapshot snapshot, MapViewport vp);

    partial void DrawBubbles(SKCanvas canvas, ScopeRenderSnapshot snapshot, MapViewport vp);

    partial void DrawMenuCue(SKCanvas canvas, ScopeRenderSnapshot snapshot, MapViewport vp);

    partial void DrawMapToolsOverlay(SKCanvas canvas, ScopeRenderSnapshot snapshot, MapViewport vp);

    /// <summary>Disposes every feature's drawing resources, with the renderer.</summary>
    private void DisposeFeatures()
    {
        DisposeMapTools();
        DisposeDrawRoute();
        DisposeHeadingVector();
        DisposeBubbles();
        DisposeMenuCue();
    }

    partial void DisposeMapTools();

    partial void DisposeDrawRoute();

    partial void DisposeHeadingVector();

    partial void DisposeBubbles();

    partial void DisposeMenuCue();
}
