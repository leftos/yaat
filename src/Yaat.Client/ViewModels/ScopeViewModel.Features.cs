namespace Yaat.Client.ViewModels;

/// <summary>The Scope view model's feature hooks: the modes and tools Esc leaves, and the per-track state each feature drops.</summary>
public sealed partial class ScopeViewModel
{
    /// <summary>Leaves the view model's own modes, one per Esc. Returns whether one was left.</summary>
    internal bool EscapeModes()
    {
        bool handled = false;
        EscapeDrawRoute(ref handled);
        return handled;
    }

    partial void EscapeDrawRoute(ref bool handled);

    /// <summary>Disarms the map tools, one per Esc. Returns whether one was disarmed.</summary>
    internal bool EscapeTools()
    {
        bool handled = false;
        EscapeMapTools(ref handled);
        return handled;
    }

    partial void EscapeMapTools(ref bool handled);

    /// <summary>Drops every feature's per-track state, with <see cref="ClearTrackState"/>.</summary>
    private void ClearFeatureTrackState()
    {
        ClearDataBlocksTrackState();
        ClearMapToolsTrackState();
        ClearDrawRouteTrackState();
    }

    partial void ClearDataBlocksTrackState();

    partial void ClearMapToolsTrackState();

    partial void ClearDrawRouteTrackState();
}
