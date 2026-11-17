namespace Yaat.Client.Views.Scope;

/// <summary>The Scope canvas's feature hooks: the modes Esc leaves and the overlays each frame collects.</summary>
public sealed partial class ScopeCanvas
{
    /// <summary>Leaves the canvas's own pointer modes, one per Esc. Returns whether one was left.</summary>
    internal bool EscapeModes()
    {
        bool handled = false;
        EscapeHeadingVector(ref handled);
        return handled;
    }

    partial void EscapeHeadingVector(ref bool handled);

    /// <summary>Builds the feature overlays <paramref name="frame"/> draws, on the UI thread with the frame.</summary>
    private ScopeOverlays CollectOverlays(ScopeFrame frame)
    {
        var overlays = new ScopeOverlays();
        CollectMapTools(frame, ref overlays);
        CollectDrawRoute(frame, ref overlays);
        CollectHeadingVector(frame, ref overlays);
        CollectBubbles(frame, ref overlays);
        CollectMenuCue(frame, ref overlays);
        return overlays;
    }

    partial void CollectMapTools(ScopeFrame frame, ref ScopeOverlays overlays);

    partial void CollectDrawRoute(ScopeFrame frame, ref ScopeOverlays overlays);

    partial void CollectHeadingVector(ScopeFrame frame, ref ScopeOverlays overlays);

    partial void CollectBubbles(ScopeFrame frame, ref ScopeOverlays overlays);

    partial void CollectMenuCue(ScopeFrame frame, ref ScopeOverlays overlays);
}
