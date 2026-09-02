using System.Globalization;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.ControllerAi.Knowledge;

/// <summary>
/// The facility's own runway-configuration selection, in SOP order: a partner airport's configuration forces one
/// (OAK follows SFO east flow), calm wind takes the calm configuration, and otherwise the wind-aligned candidate with
/// the best headwind over its departure runways wins (ties to the calm configuration, then the SOP's declared order).
/// A facility with no selection policy yields nothing and the generic rule decides.
/// </summary>
public static class FacilityRunwaySelector
{
    public static RunwayUseDecision? Select(
        FacilityOps ops,
        string airportId,
        SurfaceWind? wind,
        Func<string, string?> partnerConfiguration,
        IReadOnlyList<RunwayInfo> runways,
        DateTime magneticModelDateUtc
    )
    {
        if (ops.RunwaySelection is not { } policy)
        {
            return null;
        }

        foreach (var coupling in policy.PartnerCouplings)
        {
            var partner = partnerConfiguration(coupling.PartnerAirportId);
            if (string.Equals(partner, coupling.PartnerConfiguration, StringComparison.OrdinalIgnoreCase))
            {
                return Decision(
                    ops,
                    airportId,
                    coupling.UseConfiguration,
                    $"{coupling.PartnerAirportId} in {partner} ⇒ {coupling.UseConfiguration} ({coupling.Source})"
                );
            }
        }

        if (wind is null || wind.Variable || (wind.EffectiveSpeedKt < policy.CalmWindBelowKt))
        {
            string why = wind is null
                ? "no wind reported"
                : string.Format(CultureInfo.InvariantCulture, "wind {0:F0} kt below {1:F0} kt", wind.EffectiveSpeedKt, policy.CalmWindBelowKt);
            return Decision(ops, airportId, policy.CalmConfiguration, $"{why} ⇒ {policy.CalmConfiguration} ({policy.Source})");
        }

        string? best = null;
        double bestHeadwind = double.NegativeInfinity;
        foreach (var candidate in policy.WindAlignedCandidates)
        {
            double headwind = BestHeadwind(ops, airportId, candidate, wind, runways, magneticModelDateUtc);
            bool wins =
                headwind > bestHeadwind
                || ((headwind == bestHeadwind) && string.Equals(candidate, policy.CalmConfiguration, StringComparison.OrdinalIgnoreCase));
            if (wins)
            {
                best = candidate;
                bestHeadwind = headwind;
            }
        }

        if (best is null)
        {
            return null;
        }

        return Decision(
            ops,
            airportId,
            best,
            string.Format(
                CultureInfo.InvariantCulture,
                "wind {0:000} at {1:F0} kt ⇒ {2}, best headwind {3:F0} kt ({4})",
                wind.DirectionMagnetic,
                wind.SpeedKt,
                best,
                bestHeadwind,
                policy.Source
            )
        );
    }

    private static double BestHeadwind(
        FacilityOps ops,
        string airportId,
        string configuration,
        SurfaceWind wind,
        IReadOnlyList<RunwayInfo> runways,
        DateTime date
    )
    {
        var sets = ops.RunwaysAt(configuration, airportId);
        if (sets is null)
        {
            return double.NegativeInfinity;
        }

        double best = double.NegativeInfinity;
        foreach (var end in sets.Departure)
        {
            var pavement = RunwayInUseResolver.PavementOf(runways, end);
            if (pavement is null)
            {
                continue;
            }

            best = Math.Max(best, wind.HeadwindOn(RunwayInUseResolver.MagneticHeadingOf(pavement, end, date)));
        }

        return best;
    }

    private static RunwayUseDecision? Decision(FacilityOps ops, string airportId, string configuration, string rationale)
    {
        var sets = ops.RunwaysAt(configuration, airportId);
        if (sets is null || (sets.Departure.Count == 0))
        {
            return null;
        }

        return new RunwayUseDecision(
            airportId,
            sets.Departure.Select(RunwayIdentifier.NormalizeDesignator).ToList(),
            sets.Arrival.Select(RunwayIdentifier.NormalizeDesignator).ToList(),
            ops.Configuration(configuration)!.Name,
            RunwayUseSource.Knowledge,
            rationale
        );
    }
}

/// <summary>
/// Which of the configuration's departure runways a particular aircraft gets: the facility's assignment policy keeps
/// matching aircraft off listed runways (OAK: no jets on the 28s); when nothing is left the whole set stands (the SOP's
/// own deviation clause — the policy is a request). An aircraft the policy constrains gets the longest remaining pavement
/// (a configuration can still hold a 5,400 ft runway next to a 10,000 ft one); everyone else the nearest departure
/// threshold; the designator breaks the tie.
/// </summary>
public static class FacilityRunwayAssigner
{
    public static string AssignDepartureRunway(FacilityOps ops, AircraftState aircraft, RunwayUseDecision decision, IReadOnlyList<RunwayInfo> runways)
    {
        var matching = ops
            .RunwayAssignmentPolicy.Where(rule =>
                (rule.Effect == RunwayAssignmentEffect.Exclude) && SopAircraftClassifier.Matches(rule.Applies, aircraft.AircraftType)
            )
            .ToList();
        var allowed = decision.DepartureRunways.Where(end => !matching.Any(rule => rule.Runways.Any(r => SameEnd(r, end)))).ToList();
        if (allowed.Count == 0)
        {
            allowed = decision.DepartureRunways.ToList();
        }

        var ordered =
            matching.Count > 0
                ? allowed
                    .OrderByDescending(end => PavementLengthFt(end, runways))
                    .ThenBy(end => DistanceToDepartureThresholdNm(aircraft, end, runways))
                : allowed.OrderBy(end => DistanceToDepartureThresholdNm(aircraft, end, runways));
        return ordered.ThenBy(end => end, StringComparer.Ordinal).First();
    }

    private static bool SameEnd(string a, string b) =>
        string.Equals(RunwayIdentifier.NormalizeDesignator(a), RunwayIdentifier.NormalizeDesignator(b), StringComparison.OrdinalIgnoreCase);

    private static double PavementLengthFt(string end, IReadOnlyList<RunwayInfo> runways) =>
        RunwayInUseResolver.PavementOf(runways, end)?.PavementLengthFt ?? 0;

    private static double DistanceToDepartureThresholdNm(AircraftState aircraft, string end, IReadOnlyList<RunwayInfo> runways)
    {
        var pavement = RunwayInUseResolver.PavementOf(runways, end);
        if (pavement is null)
        {
            return double.MaxValue;
        }

        bool isEnd1 = string.Equals(pavement.Id.End1, RunwayIdentifier.NormalizeDesignator(end), StringComparison.OrdinalIgnoreCase);
        var threshold = isEnd1 ? new LatLon(pavement.Lat1, pavement.Lon1) : new LatLon(pavement.Lat2, pavement.Lon2);
        return GeoMath.DistanceNm(aircraft.Position, threshold);
    }
}
