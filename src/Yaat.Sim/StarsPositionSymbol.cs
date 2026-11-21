using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim;

/// <summary>
/// The position symbol a STARS scope draws for a track's owner, mirroring CRC's
/// <c>TrackManager.UpdateOwnerPositionSymbol</c>: the owner's sector letter in the viewer's own facility, the
/// facility's handoff number from the viewer facility's STARS handoff list for another STARS facility, the centre's
/// NAS ID for a centre, and <c>C</c> for anything else.
/// </summary>
public static class StarsPositionSymbol
{
    private static readonly ILogger Log = SimLog.CreateLogger("StarsPositionSymbol");

    /// <summary>
    /// Returns the symbol a STARS scope at <paramref name="viewer"/> draws for <paramref name="owner"/>, as CRC's
    /// <c>TrackManager.UpdateOwnerPositionSymbol</c> computes it. Null when the track is unowned.
    /// </summary>
    /// <param name="owner">The track's owner, or null for an unowned track.</param>
    /// <param name="viewer">The STARS position whose scope draws the symbol.</param>
    /// <param name="config">The room's ARTCC config; null makes every foreign owner <c>C</c>.</param>
    /// <returns>The symbol, or null when <paramref name="owner"/> is null.</returns>
    public static char? ForViewer(TrackOwner? owner, TrackOwner viewer, ArtccConfigRoot? config)
    {
        if (owner is null)
        {
            return null;
        }

        if ((!owner.IsNasPosition) || (owner.FacilityId is null))
        {
            return 'C';
        }

        if (owner.Subset is null)
        {
            return CentreSymbol(owner.FacilityId, viewer, config);
        }

        if (owner.FacilityId == viewer.FacilityId)
        {
            return owner.SectorId is { Length: > 0 } sector ? sector[0] : '?';
        }

        return HandoffSymbol(owner.FacilityId, viewer, config);
    }

    /// <summary>
    /// Returns the symbol YAAT appends to the owner's TCP (<c>3Y(1)</c>) because the student's STARS scope draws an
    /// owner in another facility with a symbol other than its TCP, per CRC's <c>TrackManager.UpdateOwnerPositionSymbol</c>.
    /// </summary>
    /// <param name="owner">The track's owner, or null for an unowned track.</param>
    /// <param name="studentPosition">The scenario's student position, or null when the room has none.</param>
    /// <param name="config">The room's ARTCC config, or null when none is loaded.</param>
    /// <returns>
    /// The one-character symbol, or null when there is no student or config, the student is not on STARS, the track is
    /// unowned, the owner is in the student's facility, or the owner is not a NAS position.
    /// </returns>
    public static string? ForeignOwnerSymbol(TrackOwner? owner, TrackOwner? studentPosition, ArtccConfigRoot? config)
    {
        if ((studentPosition is null) || (config is null) || (studentPosition.OwnerType != TrackOwnerType.Stars))
        {
            return null;
        }

        if ((owner is null) || (!owner.IsNasPosition) || (owner.FacilityId == studentPosition.FacilityId))
        {
            return null;
        }

        return ForViewer(owner, studentPosition, config)?.ToString();
    }

    private static char HandoffSymbol(string ownerFacilityId, TrackOwner viewer, ArtccConfigRoot? config)
    {
        List<StarsHandoffIdConfig>? handoffIds = viewer.FacilityId is { } viewerFacilityId
            ? config?.GetStarsConfigForFacility(viewerFacilityId)?.StarsHandoffIds
            : null;
        StarsHandoffIdConfig? entry = handoffIds?.Find(i => i.FacilityId == ownerFacilityId);
        if (entry is null)
        {
            LogNoHandoffNumber(ownerFacilityId, viewer);
            return 'C';
        }

        return entry.HandoffNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)[0];
    }

    private static void LogNoHandoffNumber(string ownerFacilityId, TrackOwner viewer)
    {
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("No STARS handoff number for {Owner} in {Viewer}'s list; symbol C", ownerFacilityId, viewer.FacilityId);
        }
    }

    private static char CentreSymbol(string centreId, TrackOwner viewer, ArtccConfigRoot? config)
    {
        if (config is null)
        {
            return 'C';
        }

        string? nasId = CentreNasId(centreId, config);
        if (string.IsNullOrEmpty(nasId))
        {
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("No NAS ID for centre {Centre}; symbol C", centreId);
            }
            return 'C';
        }

        // CRC's ZOB quirk: a facility neighbouring ZOB shows its own centre's NAS ID instead of C.
        bool neighboursZob =
            (config.Id != "ZOB")
            && (viewer.FacilityId is { } viewerFacilityId)
            && (config.FindFacility(viewerFacilityId)?.NeighboringFacilityIds.Contains("ZOB") ?? false);
        return (neighboursZob || (centreId != config.Id)) ? nasId[0] : 'C';
    }

    private static string? CentreNasId(string centreId, ArtccConfigRoot config)
    {
        if (centreId == config.Id)
        {
            return config.Facility.EramConfiguration?.NasId;
        }

        return config.NeighborCenterNasIds.TryGetValue(centreId, out string? nasId) ? nasId : null;
    }
}
