using System.Globalization;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.ControllerAi;

/// <summary>The surface wind a runway decision is made against: magnetic direction, knots, and whether it is variable.</summary>
public sealed record SurfaceWind(double DirectionMagnetic, double SpeedKt, bool Variable);

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
        return new SurfaceWind(surface.Direction, surface.Speed, surface.Variable == true);
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
        if (wind is { Variable: false, SpeedKt: >= CalmWindBelowKt })
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
/// The session's runway-in-use decisions, one per airport, shared by every brain so Ground and Local agree. Resolved on
/// first consult and held until the weather profile changes (a mid-session runway change is a supervisor decision the
/// AI does not make on its own); cleared on reset and never snapshotted.
/// </summary>
public sealed class RunwayInUseState
{
    private readonly Dictionary<string, RunwayUseDecision?> _decisions = new(StringComparer.OrdinalIgnoreCase);
    private WeatherProfile? _weather;

    public RunwayUseDecision? For(string airportId, AiTickContext context)
    {
        if (!ReferenceEquals(context.Weather, _weather))
        {
            _decisions.Clear();
            _weather = context.Weather;
        }

        var key = CacheKey(airportId);
        if (_decisions.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var scenario = context.Scenario;
        bool isPrimary = (scenario.PrimaryAirportId is { Length: > 0 } primary) && NavigationDatabase.AirportIdsMatch(airportId, primary);
        var overrideRunway = isPrimary ? scenario.ControllerAi?.RunwayInUse : null;
        var decision = RunwayInUseResolver.Resolve(
            airportId,
            overrideRunway,
            context.Weather,
            context.RunwaysFor(airportId),
            scenario.MagneticModelDateUtc
        );
        _decisions[key] = decision;
        return decision;
    }

    /// <summary>OAK and KOAK are one airport (the nav DB keys on the FAA form, as <c>RunwayOccupancy.AirportRunways</c> does).</summary>
    private static string CacheKey(string airportId) =>
        (airportId.Length == 4) && airportId.StartsWith('K') ? airportId[1..].ToUpperInvariant() : airportId.ToUpperInvariant();

    public void Clear()
    {
        _decisions.Clear();
        _weather = null;
    }
}
