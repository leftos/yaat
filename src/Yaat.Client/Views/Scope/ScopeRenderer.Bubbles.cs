using SkiaSharp;
using Yaat.Client.Views.Map;

namespace Yaat.Client.Views.Scope;

/// <summary>The Scope renderer's speech bubbles: the frame's bubbles drawn over every block, under the menu cue and the measure layer.</summary>
public sealed partial class ScopeRenderer
{
    private readonly ScopeBubbleRenderer _bubbleRenderer = new();

    partial void DrawBubbles(SKCanvas canvas, ScopeRenderSnapshot snapshot, MapViewport vp) =>
        _bubbleRenderer.Draw(canvas, snapshot.Overlays?.Bubbles, snapshot.FontSize);

    partial void DisposeBubbles() => _bubbleRenderer.Dispose();
}
