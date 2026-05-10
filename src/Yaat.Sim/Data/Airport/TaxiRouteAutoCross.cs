namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Applies the AutoCrossRunway scenario toggle to a resolved taxi route's
/// <see cref="HoldShortPoint"/>s. The same logic runs at TAXI-command resolution
/// time (in <see cref="Yaat.Sim.Commands.GroundCommandHandler"/>) and when the
/// toggle is flipped mid-session by walking every active route in
/// <see cref="Yaat.Sim.SimulationWorld.ApplyAutoCrossToActiveTaxiRoutes(bool)"/>.
/// </summary>
public static class TaxiRouteAutoCross
{
    /// <summary>
    /// Aligns every <see cref="HoldShortReason.RunwayCrossing"/> hold-short on
    /// <paramref name="route"/> with the current AutoCross setting:
    /// <list type="bullet">
    ///   <item><description>When <paramref name="autoCross"/> is true, any uncleared crossing is marked
    ///     <see cref="HoldShortPoint.IsCleared"/> = true and tagged with
    ///     <see cref="HoldShortPoint.ClearedByAutoCross"/> = true. Already-cleared crossings are
    ///     left alone (their existing clearance source stays authoritative).</description></item>
    ///   <item><description>When <paramref name="autoCross"/> is false, only crossings tagged
    ///     <see cref="HoldShortPoint.ClearedByAutoCross"/> revert to uncleared. Crossings cleared
    ///     by other sources (first-crossing-resume in <c>GroundCommandHandler.TryTaxi</c>,
    ///     explicit CROSS keyword, CTO commands) keep their clearance.</description></item>
    /// </list>
    /// Hold-shorts with a non-RunwayCrossing reason (ExplicitHoldShort, DestinationRunway)
    /// are never touched.
    /// </summary>
    public static void Apply(TaxiRoute route, bool autoCross)
    {
        foreach (var hs in route.HoldShortPoints)
        {
            if (hs.Reason != HoldShortReason.RunwayCrossing)
            {
                continue;
            }

            if (autoCross)
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
}
