using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.Views.Scope;

/// <summary>The pilot lens's check-in mark on one track: the highest that holds, or none.</summary>
internal enum ScopeCheckInState
{
    /// <summary>Nothing about the pilot's radio state needs the instructor's eye.</summary>
    None,

    /// <summary>The pilot has an open request.</summary>
    Calling,

    /// <summary>The viewer's ERAM VCI is lit while the pilot has not made initial contact.</summary>
    VciMismatch,

    /// <summary>The viewer's position owns the track and its pilot has not made initial contact.</summary>
    NotCheckedIn,

    /// <summary>On an ERAM viewer that owns the track, the pilot is in contact with no open request and the VCI is not lit.</summary>
    VciUnset,
}

/// <summary>
/// The pilot lens's check-in truth, client-only from the aircraft's <c>HasMadeInitialContact</c> and
/// <c>PendingPilotRequest</c> and the viewer's VCI and ownership. Pure: it reads only the aircraft and the viewer.
/// </summary>
internal static class ScopeCheckIn
{
    /// <summary>How far the tag's text box sits right of the symbol (its left edge) and below it (its top).</summary>
    internal const float SymbolGapPx = 6f;

    /// <summary>
    /// The mark of <paramref name="ac"/> seen from <paramref name="viewer"/>, the highest that holds: an open request
    /// (Calling); the VCI lit with no initial contact (VciMismatch); no initial contact on a track the viewer owns
    /// (NotCheckedIn); on an ERAM viewer that owns the track, initial contact with the VCI not lit (VciUnset); else None.
    /// </summary>
    internal static ScopeCheckInState StateOf(AircraftModel ac, ScopeViewer viewer)
    {
        if (ac.PendingPilotRequest is not null)
        {
            return ScopeCheckInState.Calling;
        }

        bool vciLit = EramColumn0.VciLit(ac, viewer);
        bool viewerOwns = EramColumn0.HasViewer(viewer) && EramColumn0.IsViewersTrack(ac, viewer);
        if (!ac.HasMadeInitialContact)
        {
            if (vciLit)
            {
                return ScopeCheckInState.VciMismatch;
            }

            return viewerOwns ? ScopeCheckInState.NotCheckedIn : ScopeCheckInState.None;
        }

        bool eramViewer = EramStatusLine.ViewerSector(viewer).SectorId is not null;
        return (eramViewer && viewerOwns && !vciLit) ? ScopeCheckInState.VciUnset : ScopeCheckInState.None;
    }

    /// <summary>
    /// The tag <paramref name="state"/> draws: <c>CALL</c> and the upper-cased kind of <paramref name="request"/> for Calling,
    /// <c>VCI NO CALL</c>, <c>NO CALL</c> or <c>VCI UNSET</c> for the others; null for None.
    /// </summary>
    internal static string? LabelOf(ScopeCheckInState state, PendingPilotRequestDto? request) =>
        state switch
        {
            ScopeCheckInState.Calling => "CALL " + (request?.Kind ?? "").ToUpperInvariant(),
            ScopeCheckInState.VciMismatch => "VCI NO CALL",
            ScopeCheckInState.NotCheckedIn => "NO CALL",
            ScopeCheckInState.VciUnset => "VCI UNSET",
            _ => null,
        };
}
