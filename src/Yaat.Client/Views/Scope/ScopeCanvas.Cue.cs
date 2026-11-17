using Yaat.Client.ContextMenus;

namespace Yaat.Client.Views.Scope;

/// <summary>The Scope canvas's menu cue: the cue a pointed or focused menu row shows, placed against the frame it draws.</summary>
public sealed partial class ScopeCanvas
{
    /// <summary>The map cue a pointed or focused menu row shows; null for none. Set on the UI thread by the view.</summary>
    public MapCue? MenuCue { get; set; }

    partial void CollectMenuCue(ScopeFrame frame, ref ScopeOverlays overlays) =>
        overlays = overlays with { MenuCue = MenuCue?.ResolveAgainst([.. frame.Tracks.Select(track => track.Aircraft)]) };
}
