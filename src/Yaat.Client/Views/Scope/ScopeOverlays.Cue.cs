using Yaat.Client.ContextMenus;

namespace Yaat.Client.Views.Scope;

/// <summary>The menu cue a frame draws: a pointed or focused menu row's ring, bearing line and resolved ring.</summary>
public sealed partial record ScopeOverlays
{
    /// <summary>The cue resolved against this frame's tracks; null draws none.</summary>
    public ResolvedMapCue? MenuCue { get; init; }
}
