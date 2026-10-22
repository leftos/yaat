using Yaat.Sim.Simulation;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Applies the scenario's <see cref="AutoCrossRunwayMode"/> to a resolved taxi route's <see cref="HoldShortPoint"/>s:
/// <see cref="AutoCrossRunwayMode.Off"/> pre-clears no runway crossing, <see cref="AutoCrossRunwayMode.All"/> pre-clears
/// every one, and <see cref="AutoCrossRunwayMode.InactiveOnly"/> pre-clears only crossings of runways outside the airport's
/// active-runway list (any use; with none named, every runway is inactive). The same logic runs at TAXI-command resolution
/// time (in <see cref="Yaat.Sim.Commands.GroundCommandHandler"/>), when the mode is changed mid-session, and when the active
/// runways change under <see cref="AutoCrossRunwayMode.InactiveOnly"/>, by walking every active route in
/// <see cref="Yaat.Sim.SimulationWorld.ApplyAutoCrossToActiveTaxiRoutes(AutoCrossRunwayMode, ActiveRunways)"/>.
/// </summary>
public static class TaxiRouteAutoCross
{
    /// <summary>
    /// Aligns every <see cref="HoldShortReason.RunwayCrossing"/> hold-short on <paramref name="route"/> with
    /// <paramref name="mode"/>:
    /// <list type="bullet">
    ///   <item><description>A crossing the mode pre-clears (every one under <see cref="AutoCrossRunwayMode.All"/>; under
    ///     <see cref="AutoCrossRunwayMode.InactiveOnly"/>, one whose runway overlaps no end in
    ///     <paramref name="activeAtAirport"/>) is marked <see cref="HoldShortPoint.IsCleared"/> = true and tagged with
    ///     <see cref="HoldShortPoint.ClearedByAutoCross"/> = true when still uncleared. Already-cleared crossings are left
    ///     alone (their existing clearance source stays authoritative).</description></item>
    ///   <item><description>A crossing the mode does not pre-clear reverts to uncleared only when tagged
    ///     <see cref="HoldShortPoint.ClearedByAutoCross"/>. Crossings cleared by other sources (first-crossing-resume in
    ///     <c>GroundCommandHandler.TryTaxi</c>, explicit CROSS keyword, CTO commands) keep their clearance.</description></item>
    /// </list>
    /// Hold-shorts with a non-RunwayCrossing reason (ExplicitHoldShort, DestinationRunway)
    /// are never touched, nor is a re-crossing of the runway just landed on, on a route stored
    /// during the landing rollout (<see cref="HoldShortPoint.RecrossesLandingRunway"/>): only an
    /// explicit CROSS clears that one (7110.65 3-10-9.b.2).
    /// </summary>
    public static void Apply(TaxiRoute route, AutoCrossRunwayMode mode, IReadOnlyList<ActiveRunway> activeAtAirport)
    {
        foreach (HoldShortPoint hs in route.HoldShortPoints)
        {
            if ((hs.Reason != HoldShortReason.RunwayCrossing) || hs.RecrossesLandingRunway)
            {
                continue;
            }

            if (PreClears(mode, hs, activeAtAirport))
            {
                if (!hs.IsCleared)
                {
                    hs.IsCleared = true;
                    hs.ClearedByAutoCross = true;
                }
            }
            else if (hs.ClearedByAutoCross)
            {
                hs.IsCleared = false;
                hs.ClearedByAutoCross = false;
            }
        }
    }

    private static bool PreClears(AutoCrossRunwayMode mode, HoldShortPoint crossing, IReadOnlyList<ActiveRunway> activeAtAirport) =>
        mode switch
        {
            AutoCrossRunwayMode.All => true,
            AutoCrossRunwayMode.InactiveOnly => !IsActive(crossing, activeAtAirport),
            _ => false,
        };

    /// <summary>
    /// The crossing's runway overlaps an end in <paramref name="activeAtAirport"/>. A crossing with no runway name cannot be told
    /// inactive, so it counts as active whenever the airport has any active runway.
    /// </summary>
    private static bool IsActive(HoldShortPoint crossing, IReadOnlyList<ActiveRunway> activeAtAirport)
    {
        if (activeAtAirport.Count == 0)
        {
            return false;
        }

        if (crossing.TargetName is not { Length: > 0 } target)
        {
            return true;
        }

        var crossed = RunwayIdentifier.Parse(target);
        return activeAtAirport.Any(active => crossed.Overlaps(RunwayIdentifier.Parse(active.Designator)));
    }
}
