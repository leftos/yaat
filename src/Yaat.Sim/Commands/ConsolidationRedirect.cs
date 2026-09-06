using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Commands;

/// <summary>
/// Where a handoff or point-out addressed to an unattended TCP actually lands: the attended position whose airspace has
/// absorbed it, resolved through the facility's consolidation hierarchy and the manual overrides
/// (<see cref="ArtccConfigResolver.GetConsolidationOwner"/>). Whether a TCP is attended is the host's answer — CRC
/// attendance is room state no recording carries — so a run with no host answer (a bare or replay run, a preset or
/// chained track block) never redirects.
/// </summary>
public sealed class ConsolidationRedirect(SimScenarioState scenario, ConsolidationState overrides, Func<Tcp, bool> isAttended)
{
    /// <summary>
    /// The position a command addressed to <paramref name="target"/> is redirected to, or null when the target is
    /// attended, has no attended consolidation owner other than itself, or cannot be placed in the facility's TCP table.
    /// </summary>
    public TrackOwner? TryRedirect(TrackOwner target)
    {
        var facilityId = target.FacilityId ?? "";
        if ((scenario.ArtccConfig is not { } config) || string.IsNullOrEmpty(facilityId))
        {
            return null;
        }

        var targetTcp = TrackResolver.FindTcpForOwner(target, scenario);
        if ((targetTcp is null) || isAttended(targetTcp))
        {
            return null;
        }

        var ownerTcp = config.GetConsolidationOwner(facilityId, targetTcp, isAttended, overrides);
        if ((ownerTcp is null) || (ownerTcp.Id == targetTcp.Id))
        {
            return null;
        }

        return TrackResolver.ResolveTcpToOwner(scenario, $"{ownerTcp.Subset}{ownerTcp.SectorId}");
    }
}
