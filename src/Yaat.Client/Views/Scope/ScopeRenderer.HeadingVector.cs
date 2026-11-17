using SkiaSharp;
using Yaat.Client.Views.Map;

namespace Yaat.Client.Views.Scope;

/// <summary>The Scope renderer's heading-vector slot: the elastic vector, above the pilot lens and below the measure lines.</summary>
public sealed partial class ScopeRenderer
{
    private readonly ScopeHeadingVectorRenderer _headingVectorRenderer = new();

    partial void DrawHeadingVector(SKCanvas canvas, ScopeRenderSnapshot snapshot, MapViewport vp)
    {
        if (snapshot.Overlays?.HeadingVector is { } overlay)
        {
            _headingVectorRenderer.Draw(canvas, vp, overlay);
        }
    }

    partial void DisposeHeadingVector() => _headingVectorRenderer.Dispose();
}
