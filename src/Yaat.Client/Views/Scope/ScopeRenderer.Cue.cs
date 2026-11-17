using SkiaSharp;
using Yaat.Client.Views.Map;

namespace Yaat.Client.Views.Scope;

/// <summary>The Scope renderer's menu cue: the shared map cue renderer draws a pointed or focused menu row's cue over every track.</summary>
public sealed partial class ScopeRenderer
{
    private readonly MapCueRenderer _mapCueRenderer = new();

    partial void DrawMenuCue(SKCanvas canvas, ScopeRenderSnapshot snapshot, MapViewport vp) =>
        _mapCueRenderer.Draw(canvas, vp, snapshot.Overlays?.MenuCue);

    partial void DisposeMenuCue() => _mapCueRenderer.Dispose();
}
