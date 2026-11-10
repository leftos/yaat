using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// The largest wingspan a parking stand takes, estimated from the layout's stand spacing. A neighbour counts when it is
/// another parking stand within <see cref="NeighbourRadiusFt"/> that sits beside the stand (its offset along the stand's
/// heading no larger than its offset across it) and is not an alternate of it. The nearest counting neighbour's lateral
/// offset is the gap, and the neighbour is assumed to hold the same span, so each stand owns half the gap less the wingtip
/// clearance of the group the span falls in. With no counting neighbour the airport ceiling applies; the estimate never
/// falls below ADG I's ceiling and never rises above the airport's. A sidecar <c>maxAdg</c> replaces the estimate either
/// way.
///
/// <para>The wingtip clearances (10 ft for ADG I and II, 15 ft for III, 25 ft for IV to VI) are ICAO Annex 14 stand
/// clearances by code letter, which share ADG I to VI's span bands: industry practice, not an FAA figure.</para>
/// </summary>
internal static class StandFit
{
    /// <summary>Past ADG VI's span plus its clearance (287 ft, rounded up), a neighbour caps nothing, feet.</summary>
    private const double NeighbourRadiusFt = 300.0;

    /// <summary>Two stands closer than this are alternate positions of one stand, feet: under any real side-by-side spacing.</summary>
    private const double OverlapFt = 35.0;

    /// <summary>Two stands whose names differ only by trailing letters are alternates when closer than this, feet.</summary>
    private const double SiblingFt = 150.0;

    private static readonly ILogger Log = SimLog.CreateLogger("StandFit");

    /// <summary>
    /// The largest wingspan the stand takes, feet: the sidecar override's group ceiling when there is one, else the estimate
    /// from the layout's stand spacing, at least ADG I's ceiling and at most the airport's.
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="stand">The parking stand, keyed by node: two stands of one name are estimated apart.</param>
    /// <param name="airportCeiling">The airport's design group, from its widest runway.</param>
    /// <param name="overrideAdg">The sidecar <c>maxAdg</c> covering the stand, or null.</param>
    /// <returns>The span cap, feet.</returns>
    public static double MaxWingspanFt(
        AirportGroundLayout layout,
        GroundNode stand,
        AirplaneDesignGroup airportCeiling,
        AirplaneDesignGroup? overrideAdg
    )
    {
        if (overrideAdg is { } group)
        {
            if (group > airportCeiling)
            {
                Log.LogWarning(
                    "{Airport} stand {Stand}: sidecar maxAdg {Override} is above the airport ceiling ADG {Ceiling}; using it",
                    layout.AirportId,
                    stand.Name,
                    group,
                    airportCeiling
                );
            }

            return AirplaneDesignGroups.MaxWingspanFt(group);
        }

        double ceilingFt = AirplaneDesignGroups.MaxWingspanFt(airportCeiling);
        double? gapFt = NearestGapFt(layout, stand);
        double spanFt = gapFt is { } g ? Math.Min(ceilingFt, SpanForGapFt(g)) : ceilingFt;
        Log.LogDebug("{Airport} stand {Stand}: nearest side-by-side gap {Gap} ft, span {Span:F1} ft", layout.AirportId, stand.Name, gapFt, spanFt);
        return spanFt;
    }

    /// <summary>Whether the stand takes an aircraft of the given wingspan: the span is at most <see cref="MaxWingspanFt"/>.</summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="stand">The parking stand.</param>
    /// <param name="wingspanFt">The aircraft's wingspan, feet.</param>
    /// <param name="airportCeiling">The airport's design group, from its widest runway.</param>
    /// <param name="overrideAdg">The sidecar <c>maxAdg</c> covering the stand, or null.</param>
    /// <returns>True when the aircraft fits.</returns>
    public static bool Fits(
        AirportGroundLayout layout,
        GroundNode stand,
        double wingspanFt,
        AirplaneDesignGroup airportCeiling,
        AirplaneDesignGroup? overrideAdg
    ) => wingspanFt <= MaxWingspanFt(layout, stand, airportCeiling, overrideAdg);

    /// <summary>
    /// The largest span two same-span aircraft side by side at a lateral gap take: from group VI down, the gap less the
    /// group's clearance, when that reaches the group's floor (the next group down's ceiling), capped at the group's ceiling;
    /// never below ADG I's ceiling.
    /// </summary>
    private static double SpanForGapFt(double gapFt)
    {
        double floorFt = AirplaneDesignGroups.MaxWingspanFt(AirplaneDesignGroup.I);
        for (AirplaneDesignGroup group = AirplaneDesignGroup.VI; group > AirplaneDesignGroup.I; group--)
        {
            double spanFt = gapFt - WingtipClearanceFt(group);
            if (spanFt >= AirplaneDesignGroups.MaxWingspanFt(group - 1))
            {
                return Math.Min(spanFt, AirplaneDesignGroups.MaxWingspanFt(group));
            }
        }

        return floorFt;
    }

    private static double WingtipClearanceFt(AirplaneDesignGroup group) =>
        group switch
        {
            AirplaneDesignGroup.I or AirplaneDesignGroup.II => 10.0,
            AirplaneDesignGroup.III => 15.0,
            _ => 25.0,
        };

    /// <summary>The least lateral offset of a counting neighbour, feet, or null when no stand counts.</summary>
    private static double? NearestGapFt(AirportGroundLayout layout, GroundNode stand)
    {
        var frame = new GroundOutlineFrame(stand.Position);
        double? least = null;
        foreach (GroundNode other in layout.Nodes.Values)
        {
            if ((other.Type != GroundNodeType.Parking) || ReferenceEquals(other, stand))
            {
                continue;
            }

            if (CountingGapFt(stand, other, frame.ToLocal(other.Position)) is { } gapFt)
            {
                least = Math.Min(least ?? double.PositiveInfinity, gapFt);
            }
        }

        return least;
    }

    /// <summary>
    /// The lateral offset of <paramref name="other"/> from <paramref name="stand"/> when it counts as a neighbour, feet, else
    /// null. A stand without a heading counts every neighbour in range at its full distance.
    /// </summary>
    private static double? CountingGapFt(GroundNode stand, GroundNode other, OutlinePoint offset)
    {
        double distanceFt = Math.Sqrt((offset.EastFt * offset.EastFt) + (offset.NorthFt * offset.NorthFt));
        if ((distanceFt > NeighbourRadiusFt) || IsAlternate(stand.Name, other.Name, distanceFt))
        {
            return null;
        }

        if (stand.TrueHeading is not { } heading)
        {
            return distanceFt;
        }

        double h = heading.ToRadians();
        double alongFt = (offset.EastFt * Math.Sin(h)) + (offset.NorthFt * Math.Cos(h));
        double lateralFt = Math.Abs((offset.EastFt * Math.Cos(h)) - (offset.NorthFt * Math.Sin(h)));
        return Math.Abs(alongFt) <= lateralFt ? lateralFt : null;
    }

    /// <summary>
    /// Whether two stands are alternate positions of one: closer than <see cref="OverlapFt"/>, or closer than
    /// <see cref="SiblingFt"/> with names whose stems (the name less its trailing letters) match.
    /// </summary>
    private static bool IsAlternate(string? name, string? otherName, double distanceFt) =>
        (distanceFt < OverlapFt)
        || (
            (distanceFt < SiblingFt)
            && (Stem(name) is { Length: > 0 } stem)
            && string.Equals(stem, Stem(otherName), StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>The name less its trailing letters (<c>C4U</c> to <c>C4</c>, <c>9-4B</c> to <c>9-4</c>), or null for no name.</summary>
    private static string? Stem(string? name)
    {
        if (name is null)
        {
            return null;
        }

        string trimmed = name.Trim();
        int end = trimmed.Length;
        while ((end > 0) && char.IsAsciiLetter(trimmed[end - 1]))
        {
            end--;
        }

        return trimmed[..end];
    }
}
