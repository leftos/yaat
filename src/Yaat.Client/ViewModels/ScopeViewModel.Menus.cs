using Yaat.Client.Views.Scope;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's Display submenu as its rows reach it: the pilot lens's intent path, shown as the track's nav route, and the
/// Scope-local J-ring and cone. Hiding a datablock and taking back its position live with the view model's data-block state
/// (<c>ScopeViewModel.DataBlocks.cs</c>).
/// </summary>
public sealed partial class ScopeViewModel
{
    /// <summary>Whether the track with <paramref name="callsign"/> has its intent path toggled on in the pilot lens.</summary>
    public bool IsNavRouteShown(string callsign) => LensPathCallsigns.Contains(callsign);

    /// <summary>
    /// Toggles the intent path of the track with <paramref name="callsign"/>; turning it on also turns the pilot lens and its
    /// PATH switch on when either was off, so the path draws at once.
    /// </summary>
    public void ToggleNavRoute(string callsign)
    {
        if ((!IsNavRouteShown(callsign)) && (!Lens.DrawsPath))
        {
            Lens = Lens with { Enabled = true, Path = true };
        }

        ToggleLensPath(callsign);
    }

    /// <summary>Whether the track with <paramref name="callsign"/> carries the local ring <paramref name="tool"/> places.</summary>
    public bool HasRing(string callsign, ScopeRingTool tool) => Carries(RingsOf(callsign), tool);

    /// <summary>
    /// Toggles the local ring <paramref name="tool"/> places on the track with <paramref name="callsign"/>: a ring the track
    /// carries goes whatever its size, and a missing one arrives at the Rings tool's current SIZE.
    /// </summary>
    public void ToggleRing(string callsign, ScopeRingTool tool)
    {
        ScopeTrackRings rings = RingsOf(callsign);
        bool carries = Carries(rings, tool);
        ScopeTrackRings next = tool switch
        {
            ScopeRingTool.JRing => rings with { JRingNm = carries ? null : RingSizeNm },
            ScopeRingTool.Cone => rings with { ConeNm = carries ? null : RingSizeNm },
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "Only the J-ring and cone toggle from the menu"),
        };
        SetRings(callsign, next);
    }
}
