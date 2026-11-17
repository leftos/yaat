using Yaat.Client.Models;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The ERAM sector dwell lock as the viewer sees it (CRC <c>EramSectorDisplay.IsDwellLocked</c>): a track is locked when the
/// server's list of locked sectors holds the viewer's ERAM facility and sector; the fourth-line suppression is read the same
/// way. Pure: it reads only the aircraft and the viewer.
/// </summary>
internal static class EramDwellLock
{
    /// <summary>Whether <paramref name="ac"/> is dwell-locked for <paramref name="viewer"/>'s ERAM sector; never with no sector.</summary>
    internal static bool IsDwellLocked(AircraftModel ac, ScopeViewer viewer)
    {
        (string? sectorId, string? facilityId) = EramStatusLine.ViewerSector(viewer);
        if ((sectorId is null) || (facilityId is null))
        {
            return false;
        }
        return ac.EramDwellLockedSectors.Any(key => key.Is(facilityId, sectorId));
    }

    /// <summary>
    /// Whether <paramref name="viewer"/>'s ERAM sector suppressed <paramref name="ac"/>'s fourth line with a bare
    /// <c>QS {FLID}</c> (CRC <c>SuppressFourthLine</c>); never with no sector.
    /// </summary>
    internal static bool IsFourthLineSuppressed(AircraftModel ac, ScopeViewer viewer)
    {
        (string? sectorId, string? facilityId) = EramStatusLine.ViewerSector(viewer);
        if ((sectorId is null) || (facilityId is null))
        {
            return false;
        }
        return ac.EramFourthLineSuppressedSectors.Any(key => key.Is(facilityId, sectorId));
    }

    /// <summary>Whether <paramref name="viewer"/> has an ERAM facility and sector to lock a block or preview a VCI for.</summary>
    internal static bool HasEramSector(ScopeViewer viewer)
    {
        (string? sectorId, string? facilityId) = EramStatusLine.ViewerSector(viewer);
        return (sectorId is not null) && (facilityId is not null);
    }
}
