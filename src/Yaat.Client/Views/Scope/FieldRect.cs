using SkiaSharp;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// One laid-out field: the single rect that both drawing and hit-testing read.
/// </summary>
/// <param name="Slot">Which field this is.</param>
/// <param name="Rect">Field bounds; relative to the block's text origin until placed with <see cref="ScopeBlockLayout.At"/>.</param>
/// <param name="Text">Text to draw this frame (empty while a reserved field flashes off).</param>
/// <param name="Token">Colour role of the field.</param>
/// <param name="Style">How the text draws beyond its colour.</param>
public readonly record struct FieldRect(ScopeFieldSlot Slot, SKRect Rect, string Text, ScopeColorToken Token, ScopeFieldStyle Style)
{
    /// <summary>Returns this field moved by (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
    public FieldRect Offset(float dx, float dy) => this with { Rect = new SKRect(Rect.Left + dx, Rect.Top + dy, Rect.Right + dx, Rect.Bottom + dy) };
}
