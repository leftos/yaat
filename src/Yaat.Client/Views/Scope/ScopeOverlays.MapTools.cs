using SkiaSharp;

namespace Yaat.Client.Views.Scope;

/// <summary>The map tools' part of the overlays: the Ctrl-hover MVA label.</summary>
public sealed partial record ScopeOverlays
{
    /// <summary>The MVA label under the pointer while Control is held over an MVA sector, or null.</summary>
    public ScopeMvaHover? MvaHover { get; init; }
}

/// <summary>The Ctrl-hover MVA label.</summary>
/// <param name="Text">The readout (<see cref="ScopeMvaReadout.TextAt"/>).</param>
/// <param name="At">The pointer, in canvas pixels; the label sits below and right of it.</param>
public readonly record struct ScopeMvaHover(string Text, SKPoint At);
