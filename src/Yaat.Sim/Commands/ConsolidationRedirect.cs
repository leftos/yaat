using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Commands;

/// <summary>
/// Where a handoff or point-out addressed to an unattended TCP actually lands: the attended position whose airspace has
/// absorbed it, resolved through the facility's consolidation hierarchy and the manual overrides
/// (<see cref="ArtccConfigResolver.GetConsolidationOwner"/>). Attendance is engine state every run kind carries
/// (<see cref="SimulationEngine.Attendance"/>), so a live session, a replay and a reconstruction redirect alike; a
/// dispatch that installs no redirect at all (a preset or chained track block) still means "never redirect".
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
