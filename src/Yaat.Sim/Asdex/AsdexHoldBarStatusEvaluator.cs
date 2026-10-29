using System.Collections.Frozen;

namespace Yaat.Sim.Asdex;

/// <summary>
/// Which ASDE-X hold bars are Active this second, from the aircraft alone (stateless; nothing carries over between ticks). A
/// bar is Active while any runway user (<see cref="AsdexRunwayUsers"/>, the rule the Safety Logic alerts read too) of the
/// runway it protects still has it ahead. So an arrival lights the whole runway, a departure lights only the bars ahead once
/// it rolls fast, each bar clears as the user passes it, and the rest clear together when the user slows, climbs out, or
/// crosses the threshold already high. An aircraft whose alerts are individually inhibited still lights bars: the inhibit
/// gates alerts, not bars.
/// </summary>
public static class AsdexHoldBarStatusEvaluator
{
    private static readonly IReadOnlySet<string> NoActiveBars = FrozenSet<string>.Empty;

    /// <summary>
    /// The ids of the bars in <paramref name="bars"/> that are Active with <paramref name="aircraft"/> where they are.
    /// <paramref name="axes"/> holds each runway's axis, in <paramref name="runways"/>' order.
    /// </summary>
    internal static IReadOnlySet<string> ActiveBarIds(
        IReadOnlyList<AsdexHoldBarRunway> runways,
        IReadOnlyList<AsdexRunwayAxis> axes,
        IReadOnlyList<AsdexHoldBar> bars,
        IReadOnlyList<AircraftState> aircraft,
        double fieldElevationFt
    )
    {
        List<AsdexRunwayUse>? users = null;
        foreach (AircraftState ac in aircraft)
        {
            AsdexRunwayUses uses = AsdexRunwayUsers.UsesOf(ac, runways, axes, fieldElevationFt);
            if (uses.Arrival is { } arrival)
            {
                (users ??= []).Add(arrival);
            }

            if (uses.OverRunway is { } overRunway)
            {
                (users ??= []).Add(overRunway);
            }
        }

        if (users is null)
        {
            return NoActiveBars;
        }

        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (AsdexHoldBar bar in bars)
        {
            if (AnyUserHasAhead(runways, users, bar))
            {
                active.Add(bar.Id);
            }
        }

        return active;
    }

    private static bool AnyUserHasAhead(IReadOnlyList<AsdexHoldBarRunway> runways, List<AsdexRunwayUse> users, AsdexHoldBar bar)
    {
        foreach (AsdexRunwayUse user in users)
        {
            if ((runways[user.RunwayIndex].Id == bar.ProtectedRunwayId) && ((user.Direction * (bar.AlongRunwayFt - user.AlongFt)) > 0))
            {
                return true;
            }
        }

        return false;
    }
}
