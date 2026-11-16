using Yaat.Client.Models;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The ERAM column-0 predicates (CRC <c>FdbRenderObject.Update</c>): the VCI is lit when the viewer's ERAM sector is on
/// frequency for the track, and <c>R</c> shows when the track's owner, or the lack of one, is not the viewer's sector. With no
/// viewer at all neither shows. Pure: it reads only the aircraft and the viewer.
/// </summary>
internal static class EramColumn0
{
    /// <summary>
    /// Whether <paramref name="viewer"/> owns <paramref name="ac"/>: its acting position matches the owner, or its ERAM
    /// facility and sector are the owner's facility and <c>C</c>-prefixed sector code.
    /// </summary>
    internal static bool IsViewersTrack(AircraftModel ac, ScopeViewer viewer)
    {
        if ((viewer.Acting is { } acting) && TrackCoordinationView.MatchesActing(ac.Owner, ac.OwnerFacilityId, ac.OwnerSectorCode, acting))
        {
            return true;
        }

        (string? sectorId, string? facilityId) = EramStatusLine.ViewerSector(viewer);
        return (sectorId is not null)
            && (facilityId is not null)
            && string.Equals(ac.OwnerFacilityId, facilityId, StringComparison.Ordinal)
            && string.Equals(ac.OwnerSectorCode, "C" + sectorId, StringComparison.Ordinal);
    }

    /// <summary>Whether the <c>R</c> shows: a viewer is known and the track, owned or not, is not its own.</summary>
    internal static bool ShowsNotYourControl(AircraftModel ac, ScopeViewer viewer) => HasViewer(viewer) && !IsViewersTrack(ac, viewer);

    /// <summary>Whether the VCI is lit: the viewer's ERAM sector id is among the track's on-frequency sector ids.</summary>
    internal static bool VciLit(AircraftModel ac, ScopeViewer viewer) =>
        (EramStatusLine.ViewerSector(viewer).SectorId is { } sectorId) && ac.EramOnFrequencySectorIds.Contains(sectorId, StringComparer.Ordinal);

    /// <summary>Whether a viewer is known: an acting position or an ERAM sector to compare the owner against.</summary>
    internal static bool HasViewer(ScopeViewer viewer) => (viewer.Acting is not null) || (EramStatusLine.ViewerSector(viewer).SectorId is not null);
}
