namespace Yaat.Client.Views.Scope;

/// <summary>The speech bubbles a frame draws.</summary>
public sealed partial record ScopeOverlays
{
    /// <summary>The frame's bubbles, placed on the UI thread with the frame; null or empty draws none.</summary>
    public IReadOnlyList<ScopeBubble>? Bubbles { get; init; }
}
