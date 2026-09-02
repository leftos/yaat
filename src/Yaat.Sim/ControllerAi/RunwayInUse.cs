using System.Globalization;
using Yaat.Sim.ControllerAi.Knowledge;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.ControllerAi;

/// <summary>
/// The surface wind a runway decision is made against: magnetic direction, steady speed, the gust when one is reported,
/// and whether the direction is variable. Thresholds and tailwind limits read the gust (a 05G18 day is not a calm day).
/// </summary>
public sealed record SurfaceWind(double DirectionMagnetic, double SpeedKt, double? GustKt, bool Variable)
{
    /// <summary>The speed limits and thresholds are held to: the gust when reported, else the steady speed.</summary>
    public double EffectiveSpeedKt => Math.Max(SpeedKt, GustKt ?? 0);

    /// <summary>Steady headwind component (kt, negative for a tailwind) on a runway end with this magnetic heading.</summary>
    public double HeadwindOn(double runwayMagneticHeading) => SpeedKt * Math.Cos(Radians(runwayMagneticHeading));

    /// <summary>
    /// Worst-case tailwind (kt) on a runway end: the gust's tailwind component — or the whole gust for a variable wind,
    /// which can come from anywhere.
    /// </summary>
    public double WorstTailwindOn(double runwayMagneticHeading) =>
        Variable ? EffectiveSpeedKt : -EffectiveSpeedKt * Math.Cos(Radians(runwayMagneticHeading));

    private double Radians(double runwayMagneticHeading) => GeoMath.AbsBearingDifference(DirectionMagnetic, runwayMagneticHeading) * Math.PI / 180.0;
}

public enum RunwayUseSource
{
    Override,
    Knowledge,
    Generic,
}

/// <summary>
/// The runway-in-use decision for one airport: the departure and arrival runway ends in preference order, the named
/// configuration when facility knowledge chose it, where the decision came from, and a one-line rationale for the
/// decision log.
/// </summary>
public sealed record RunwayUseDecision(
    string AirportId,
    IReadOnlyList<string> DepartureRunways,
    IReadOnlyList<string> ArrivalRunways,
    string? ConfigurationName,
    RunwayUseSource Source,
    string Rationale
)
{
    public string PrimaryDepartureRunway => DepartureRunways[0];
}

/// <summary>
/// The generic runway-in-use rule (7110.65 §3-5-1): with wind of 5 kt or more, the runway end most nearly aligned with
/// the magnetic surface wind; in calm wind the facility's calm-wind runway — which nothing generic knows, so the longest
/// pavement stands in, its end picked by any residual wind and otherwise by designator. A session override (the
/// scenario/runner's runway) wins outright. Runway headings are converted to magnetic at the session's magnetic-model
/// date so a replay makes the same choice years later.
/// </summary>
public static class RunwayInUseResolver
{
    public const double CalmWindBelowKt = 5;

    public static SurfaceWind? SampleWind(WeatherProfile? weather)
    {
        if (weather?.WindLayers is not { Count: > 0 } layers)
        {
            return null;
        }

        var surface = layers[0];
        return new SurfaceWind(surface.Direction, surface.Speed, surface.Gusts, surface.Variable == true);
    }

    public static RunwayUseDecision? Resolve(
        string airportId,
        string? overrideRunway,
        WeatherProfile? weather,
        IReadOnlyList<RunwayInfo> runways,
        DateTime magneticModelDateUtc
    )
    {
        if (runways.Count == 0)
        {
            return null;
        }

        var ends = runways.SelectMany(r => new[] { new RunwayEnd(r, r.Id.End1), new RunwayEnd(r, r.Id.End2) }).ToList();
        if (overrideRunway is { Length: > 0 } wanted && ends.FirstOrDefault(e => e.Matches(wanted)) is { } chosen)
        {
            return Decision(airportId, chosen, RunwayUseSource.Override, $"runway {chosen.Designator} set for the session");
        }

        var wind = SampleWind(weather);
        if (wind is { Variable: false } && (wind.EffectiveSpeedKt >= CalmWindBelowKt))
        {
            var aligned = ends.OrderBy(e => GeoMath.AbsBearingDifference(wind.DirectionMagnetic, e.MagneticHeading(magneticModelDateUtc)))
                .ThenByDescending(e => e.Pavement.PavementLengthFt)
                .ThenBy(e => e.Designator, StringComparer.Ordinal)
                .First();
            return Decision(
                airportId,
                aligned,
                RunwayUseSource.Generic,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "wind {0:000} at {1:F0} kt: runway {2} most nearly aligned (magnetic {3:F0})",
                    wind.DirectionMagnetic,
                    wind.SpeedKt,
                    aligned.Designator,
                    aligned.MagneticHeading(magneticModelDateUtc)
                )
            );
        }

        var longest = runways.OrderByDescending(r => r.PavementLengthFt).ThenBy(r => r.Id.End1, StringComparer.Ordinal).First();
        var candidates = ends.Where(e => ReferenceEquals(e.Pavement, longest));
        bool residual = wind is { Variable: false, SpeedKt: > 0 };
        var end = residual
            ? candidates.OrderBy(e => GeoMath.AbsBearingDifference(wind!.DirectionMagnetic, e.MagneticHeading(magneticModelDateUtc))).First()
            : candidates.OrderBy(e => e.Designator, StringComparer.Ordinal).First();
        string why = residual
            ? string.Format(
                CultureInfo.InvariantCulture,
                "calm ({0:F0} kt): longest runway, end {1} toward the light wind",
                wind!.SpeedKt,
                end.Designator
            )
            : $"calm: longest runway, end {end.Designator} by designator";
        return Decision(airportId, end, RunwayUseSource.Generic, why);
    }

    /// <summary>The magnetic heading of one end of a pavement at the session's magnetic-model date.</summary>
    /// <summary>The pavement one of whose ends is <paramref name="end"/>, or null when the airport has no such runway.</summary>
    public static RunwayInfo? PavementOf(IReadOnlyList<RunwayInfo> runways, string end) => runways.FirstOrDefault(r => r.Id.Contains(end));

    public static double MagneticHeadingOf(RunwayInfo pavement, string end, DateTime modelDateUtc) =>
        new RunwayEnd(pavement, RunwayIdentifier.NormalizeDesignator(end)).MagneticHeading(modelDateUtc);

    private static RunwayUseDecision Decision(string airportId, RunwayEnd end, RunwayUseSource source, string rationale) =>
        new(airportId, [end.Designator], [end.Designator], null, source, rationale);

    private sealed record RunwayEnd(RunwayInfo Pavement, string Designator)
    {
        private bool IsEnd1 => string.Equals(Designator, Pavement.Id.End1, StringComparison.OrdinalIgnoreCase);

        public bool Matches(string designator) =>
            string.Equals(Designator, RunwayIdentifier.NormalizeDesignator(designator), StringComparison.OrdinalIgnoreCase);

        public double MagneticHeading(DateTime modelDateUtc) =>
            IsEnd1
                ? MagneticDeclination.TrueToMagnetic(Pavement.TrueHeading1.Degrees, Pavement.Lat1, Pavement.Lon1, modelDateUtc)
                : MagneticDeclination.TrueToMagnetic(Pavement.TrueHeading2.Degrees, Pavement.Lat2, Pavement.Lon2, modelDateUtc);
    }
}

/// <summary>
/// The tailwind a facility's own runway choice may not exceed before the generic rule takes over (the "more conservative
/// wins" contract of the knowledge overlay): ten knots on a dry runway — the common transport-category certificated
/// tailwind limit — and five when precipitation is reported, FAA Order 8400.9's unwaived figure. Held to the gust, and
/// to the whole gust for a variable wind. Unusable departure runways are pruned; the configuration is refused only when
/// none survives. Arrival runways are not gated.
/// </summary>
public static class RunwayUsabilityGate
{
    public const double MaxTailwindKtDry = 10;
    public const double MaxTailwindKtWet = 5;

    /// <summary>The decision with its unusable departure runways removed (null when none survives), and what was removed and why.</summary>
    public static (RunwayUseDecision? Usable, string? Removed) Apply(
        RunwayUseDecision decision,
        SurfaceWind? wind,
        bool wet,
        IReadOnlyList<RunwayInfo> runways,
        DateTime magneticModelDateUtc
    )
    {
        if (wind is null)
        {
            return (decision, null);
        }

        double limit = wet ? MaxTailwindKtWet : MaxTailwindKtDry;
        var kept = new List<string>();
        var removed = new List<string>();
        foreach (var end in decision.DepartureRunways)
        {
            var pavement = RunwayInUseResolver.PavementOf(runways, end);
            double tailwind = pavement is null ? 0 : wind.WorstTailwindOn(RunwayInUseResolver.MagneticHeadingOf(pavement, end, magneticModelDateUtc));
            if (tailwind > limit)
            {
                removed.Add(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} ({1:F0} kt tailwind over the {2:F0} kt {3} limit)",
                        end,
                        tailwind,
                        limit,
                        wet ? "wet" : "dry"
                    )
                );
            }
            else
            {
                kept.Add(end);
            }
        }

        if (removed.Count == 0)
        {
            return (decision, null);
        }

        string note = "runway " + string.Join(", ", removed) + " unusable";
        if (kept.Count == 0)
        {
            return (null, note);
        }

        return (decision with { DepartureRunways = kept, Rationale = $"{decision.Rationale}; {note}" }, note);
    }
}

/// <summary>
/// The session's runway-in-use decisions, one per airport, shared by every brain so Ground and Local agree. Resolved on
/// first consult and held while the reported wind stays within <see cref="RechooseDirectionDeg"/> / <see cref="RechooseSpeedKt"/>
/// of the wind it was made in and the precipitation state is unchanged — a runway change is a supervisor decision
/// (7110.65 §3-5-1.a), not something to redo every second a weather timeline interpolates. Cleared on reset, never
/// snapshotted. Precedence: the session's runway designator for the primary airport, a named configuration from
/// <see cref="ControllerAiConfig.RunwayConfigurations"/> (kept as set; a gate violation is filed as informational), the
/// facility's own selection (<see cref="FacilityRunwaySelector"/>) pruned by <see cref="RunwayUsabilityGate"/> — nothing
/// usable files <see cref="AiAnomalyKind.KnowledgeConflict"/> — then the generic rule.
/// </summary>
public sealed class RunwayInUseState(Func<string?, FacilityOps?> knowledge)
{
    public const double RechooseDirectionDeg = 30;
    public const double RechooseSpeedKt = 5;

    private readonly Dictionary<string, Held> _decisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _resolving = new(StringComparer.OrdinalIgnoreCase);

    public RunwayUseDecision? For(string airportId, AiTickContext context, string positionId)
    {
        var key = CacheKey(airportId);
        var wind = RunwayInUseResolver.SampleWind(context.Weather);
        bool wet = IsWet(context.Weather);
        if (_decisions.TryGetValue(key, out var held) && !WindMoved(held.Wind, wind) && (held.Wet == wet))
        {
            return held.Decision;
        }

        // Two files coupling to each other would recurse forever; the inner ask sees nothing.
        if (!_resolving.Add(key))
        {
            return null;
        }

        try
        {
            var decision = Resolve(airportId, context, positionId, wind, wet);
            _decisions[key] = new Held(decision, wind, wet);
            return decision;
        }
        finally
        {
            _resolving.Remove(key);
        }
    }

    /// <summary>
    /// The departure runway for one aircraft: the facility's assignment policy over the configuration's runways, else the
    /// decision's first.
    /// </summary>
    public string DepartureRunwayFor(AircraftState aircraft, RunwayUseDecision decision, AiTickContext context)
    {
        if ((decision.DepartureRunways.Count > 1) && knowledge(decision.AirportId) is { } ops)
        {
            return FacilityRunwayAssigner.AssignDepartureRunway(ops, aircraft, decision, context.RunwaysFor(decision.AirportId));
        }

        return decision.PrimaryDepartureRunway;
    }

    public void Clear()
    {
        _decisions.Clear();
        _resolving.Clear();
    }

    /// <summary>A change worth a new decision: the wind appeared or vanished, turned variable, veered 30° or changed 5 kt.</summary>
    public static bool WindMoved(SurfaceWind? before, SurfaceWind? now)
    {
        if (before is null || now is null)
        {
            return (before is null) != (now is null);
        }

        if (before.Variable != now.Variable)
        {
            return true;
        }

        bool veered = !now.Variable && (GeoMath.AbsBearingDifference(before.DirectionMagnetic, now.DirectionMagnetic) >= RechooseDirectionDeg);
        return veered || (Math.Abs(before.EffectiveSpeedKt - now.EffectiveSpeedKt) >= RechooseSpeedKt);
    }

    private static bool IsWet(WeatherProfile? weather) => !string.IsNullOrWhiteSpace(weather?.Precipitation);

    private RunwayUseDecision? Resolve(string airportId, AiTickContext context, string positionId, SurfaceWind? wind, bool wet)
    {
        var scenario = context.Scenario;
        var config = scenario.ControllerAi;
        var runways = context.RunwaysFor(airportId);
        var date = scenario.MagneticModelDateUtc;
        bool isPrimary = (scenario.PrimaryAirportId is { Length: > 0 } primary) && NavigationDatabase.AirportIdsMatch(airportId, primary);
        if (
            isPrimary
            && config?.RunwayInUse is { Length: > 0 } designator
            && RunwayInUseResolver.Resolve(airportId, designator, null, runways, date) is { Source: RunwayUseSource.Override } fixedRunway
        )
        {
            return fixedRunway;
        }

        var ops = knowledge(airportId);
        if (Named(config?.RunwayConfigurations, airportId) is { } named && ops?.RunwaysAt(named, airportId) is { Departure.Count: > 0 } sets)
        {
            var fixedConfiguration = new RunwayUseDecision(
                airportId,
                sets.Departure.Select(RunwayIdentifier.NormalizeDesignator).ToList(),
                sets.Arrival.Select(RunwayIdentifier.NormalizeDesignator).ToList(),
                ops.Configuration(named)!.Name,
                RunwayUseSource.Override,
                $"configuration {named} set for the session"
            );
            if (RunwayUsabilityGate.Apply(fixedConfiguration, wind, wet, runways, date).Removed is { } unusable)
            {
                context.Anomalies.Record(
                    AiAnomalyKind.KnowledgeConflict,
                    positionId,
                    airportId,
                    context.ElapsedSeconds,
                    $"session configuration {named} kept as set although {unusable}"
                );
            }

            return fixedConfiguration;
        }

        if (
            ops is not null
            && FacilityRunwaySelector.Select(ops, airportId, wind, partner => PartnerConfiguration(partner, context, positionId), runways, date)
                is { } known
        )
        {
            var (usable, removed) = RunwayUsabilityGate.Apply(known, wind, wet, runways, date);
            if (usable is not null)
            {
                return usable;
            }

            context.Anomalies.Record(
                AiAnomalyKind.KnowledgeConflict,
                positionId,
                airportId,
                context.ElapsedSeconds,
                $"{ops.FacilityId} knowledge chose {known.ConfigurationName} but {removed}; the generic rule decides"
            );
        }

        return RunwayInUseResolver.Resolve(airportId, null, context.Weather, runways, date);
    }

    /// <summary>A partner airport's configuration: the session knob, else the partner's own knowledge file's decision.</summary>
    private string? PartnerConfiguration(string partnerAirportId, AiTickContext context, string positionId)
    {
        if (Named(context.Scenario.ControllerAi?.RunwayConfigurations, partnerAirportId) is { } named)
        {
            return named;
        }

        return knowledge(partnerAirportId) is null ? null : For(partnerAirportId, context, positionId)?.ConfigurationName;
    }

    private static string? Named(IReadOnlyDictionary<string, string>? configurations, string airportId) =>
        configurations
            ?.Where(kv => NavigationDatabase.AirportIdsMatch(kv.Key, airportId))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value)
            .FirstOrDefault();

    /// <summary>OAK and KOAK are one airport (the nav DB keys on the FAA form, as <c>RunwayOccupancy.AirportRunways</c> does).</summary>
    private static string CacheKey(string airportId) =>
        (airportId.Length == 4) && airportId.StartsWith('K') ? airportId[1..].ToUpperInvariant() : airportId.ToUpperInvariant();

    private sealed record Held(RunwayUseDecision? Decision, SurfaceWind? Wind, bool Wet);
}
