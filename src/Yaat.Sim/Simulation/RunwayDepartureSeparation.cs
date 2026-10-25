using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Simulation;

/// <summary>A consolidated wake turbulence (CWT) category, A (heaviest) to I (lightest), as 7110.65 3-9-6 and 5-5-4 tabulate them.</summary>
public enum CwtCategory
{
    A = 1,
    B = 2,
    C = 3,
    D = 4,
    E = 5,
    F = 6,
    G = 7,
    H = 8,
    I = 9,
}

/// <summary>How a succeeding operation's runway relates to the preceding one's.</summary>
public enum RunwayRelationKind
{
    /// <summary>The same runway end.</summary>
    SameActive,

    /// <summary>The same pavement, used in the opposite direction.</summary>
    OppositeDirectionSamePavement,

    /// <summary>Pavements that physically cross (7110.65 3-9-8).</summary>
    Intersecting,

    /// <summary>Non-intersecting runways whose flight paths cross within a mile of the departure ends (7110.65 3-9-9).</summary>
    ProjectedConverging,
}

/// <summary>
/// The relation between two runways, with the distance of their (projected) intersection from each runway's threshold,
/// in feet, when they cross.
/// </summary>
public sealed record RunwayRelation(RunwayRelationKind Kind, double? PrecedingIntersectionFt, double? SucceedingIntersectionFt)
{
    /// <summary>The same runway end: no intersection.</summary>
    public static RunwayRelation SameActive() => new(RunwayRelationKind.SameActive, null, null);
}

/// <summary>A wake turbulence interval between two departures, in seconds from the preceding departure's start of takeoff roll, and the paragraph that sets it.</summary>
public sealed record DepartureWakeInterval(double Seconds, string RuleReference);

/// <summary>
/// The runway relations and the departure wake turbulence intervals both the solo-training evaluator and the simulated tower
/// apply: how two runways relate (same end, opposite direction, intersecting, projected converging), and the time a
/// departure waits behind a preceding departure on the same runway or a close parallel (7110.65 3-9-6.f/g).
/// </summary>
public static class RunwayDepartureSeparation
{
    /// <summary>How far past the departure ends two non-intersecting runways' flight paths may cross and still be converging, nm.</summary>
    public const double ProjectedConvergingRunwayLimitNm = 1.0;

    /// <summary>
    /// The CWT category of <paramref name="aircraftType"/>: the wake table's, else the FAA database's, else by engine class
    /// (piston and helicopter I, turboprop G, anything else F).
    /// </summary>
    public static CwtCategory CwtCategoryOf(string aircraftType)
    {
        string? cwt = WakeTurbulenceData.GetCwt(aircraftType) ?? FaaAircraftDatabase.Get(aircraftType)?.Cwt;
        if (Enum.TryParse(cwt, ignoreCase: true, out CwtCategory category))
        {
            return category;
        }

        return AircraftCategorization.Categorize(aircraftType) switch
        {
            AircraftCategory.Piston or AircraftCategory.Helicopter => CwtCategory.I,
            AircraftCategory.Turboprop => CwtCategory.G,
            _ => CwtCategory.F,
        };
    }

    /// <summary>
    /// The wake turbulence interval a departure from the full length of the same runway, or of a parallel less than 2,500 ft
    /// away, waits behind a preceding departure (7110.65 3-9-6.f/g; NOTE 2 times it from the preceding aircraft's start of
    /// takeoff roll); null when the categories need none. Intersection departures (3-9-7) and crossing flight paths (3-9-8,
    /// 3-9-9) are not covered.
    /// </summary>
    /// <param name="preceding">The preceding departure's CWT category.</param>
    /// <param name="succeeding">The succeeding departure's CWT category.</param>
    /// <param name="sameRunwayOrParallelUnder700Ft">
    /// The same runway, or a parallel less than 700 ft away, where 3-9-6.g's two minutes behind a category E apply to a
    /// category I; false for a parallel 700 to 2,500 ft away.
    /// </param>
    public static DepartureWakeInterval? DepartureWakeIntervalFor(CwtCategory preceding, CwtCategory succeeding, bool sameRunwayOrParallelUnder700Ft)
    {
        if (sameRunwayOrParallelUnder700Ft && (preceding == CwtCategory.E) && (succeeding == CwtCategory.I))
        {
            return new DepartureWakeInterval(120.0, "7110.65 §3-9-6(g)");
        }

        if ((preceding == CwtCategory.A) && IsCategoryRange(succeeding, CwtCategory.B, CwtCategory.I))
        {
            return new DepartureWakeInterval(180.0, "7110.65 §3-9-6(f)");
        }

        bool behindBOrD = (preceding is CwtCategory.B or CwtCategory.D) && IsCategoryRange(succeeding, CwtCategory.B, CwtCategory.I);
        bool behindC = (preceding == CwtCategory.C) && IsCategoryRange(succeeding, CwtCategory.E, CwtCategory.I);
        return (behindBOrD || behindC) ? new DepartureWakeInterval(120.0, "7110.65 §3-9-6(f)") : null;
    }

    /// <summary>
    /// How <paramref name="succeeding"/> relates to <paramref name="preceding"/> at one airport: the same end, the same
    /// pavement in the opposite direction, physically intersecting (7110.65 3-9-8), or with flight paths converging within
    /// <see cref="ProjectedConvergingRunwayLimitNm"/> (3-9-9); null for runways at different airports or with none of these
    /// relations (parallels).
    /// </summary>
    public static RunwayRelation? TryResolveRunwayRelation(RunwayInfo preceding, RunwayInfo succeeding)
    {
        if (!string.Equals(preceding.AirportId, succeeding.AirportId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (IsSameRunway(preceding, succeeding))
        {
            return RunwayRelation.SameActive();
        }

        if (IsSamePhysicalRunway(preceding, succeeding))
        {
            return new RunwayRelation(RunwayRelationKind.OppositeDirectionSamePavement, null, null);
        }

        if (RunwayIntersectionCalculator.FindIntersection(preceding, succeeding) is { } intersection)
        {
            return new RunwayRelation(
                RunwayRelationKind.Intersecting,
                intersection.FirstDistFromThresholdNm * GeoMath.FeetPerNm,
                intersection.SecondDistFromThresholdNm * GeoMath.FeetPerNm
            );
        }

        if (
            RunwayIntersectionCalculator.FindProjectedFlightPathIntersection(preceding, succeeding, ProjectedConvergingRunwayLimitNm) is
            { } projectedIntersection
        )
        {
            return new RunwayRelation(
                RunwayRelationKind.ProjectedConverging,
                projectedIntersection.FirstDistFromThresholdNm * GeoMath.FeetPerNm,
                projectedIntersection.SecondDistFromThresholdNm * GeoMath.FeetPerNm
            );
        }

        return null;
    }

    /// <summary>The same runway end at the same airport.</summary>
    public static bool IsSameRunway(RunwayInfo a, RunwayInfo b) => string.Equals(RunwayKey(a), RunwayKey(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The same pavement at the same airport, either end.</summary>
    public static bool IsSamePhysicalRunway(RunwayInfo a, RunwayInfo b) =>
        string.Equals(a.AirportId, b.AirportId, StringComparison.OrdinalIgnoreCase) && a.Id.Overlaps(b.Id);

    /// <summary>Whether <paramref name="actual"/> lies in <paramref name="min"/> to <paramref name="max"/>, inclusive.</summary>
    public static bool IsCategoryRange(CwtCategory actual, CwtCategory min, CwtCategory max) => (actual >= min) && (actual <= max);

    private static string RunwayKey(RunwayInfo runway) => $"{runway.AirportId}/{runway.Designator}".ToUpperInvariant();
}
