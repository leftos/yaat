namespace Yaat.Sim;

/// <summary>
/// Per-room state machine that issues reported METARs from the continuously-simulated weather:
/// a routine METAR for every station once per hour at <see cref="RoutineObservationMinute"/>, and
/// a SPECI for a station whenever its conditions change significantly (see <see cref="SpeciCriteria"/>)
/// since that station's last issued report. Conditions are sampled and frozen at issuance, so the
/// reported string holds steady between issuances (a realistic observation lag); operational logic
/// must keep using the continuous weather, never these reports. The observation clock is anchored
/// at construction and advanced by elapsed sim time.
/// </summary>
public sealed class MetarIssuer
{
    public const int RoutineObservationMinute = 53;

    private readonly DateTime _anchorUtc;
    private readonly List<string> _stationOrder = [];
    private readonly Dictionary<string, StationReport> _stations = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastRoutineInstantUtc;

    /// <param name="weather">The loaded weather; its METAR strings seed the station list and baselines.</param>
    /// <param name="anchorUtc">Real-world UTC at load; the observation clock is this plus elapsed sim time.</param>
    /// <param name="stationLocator">Maps a station id to its coordinates for magnetic-variation lookup.</param>
    public MetarIssuer(WeatherProfile weather, DateTime anchorUtc, Func<string, (double Lat, double Lon)?> stationLocator)
    {
        _anchorUtc = anchorUtc;

        foreach (var raw in weather.Metars)
        {
            var parsed = MetarParser.Parse(raw);
            if (parsed is null || _stations.ContainsKey(parsed.StationId))
            {
                continue;
            }

            _stationOrder.Add(parsed.StationId);
            _stations[parsed.StationId] = new StationReport(raw.Trim(), Sample(parsed.StationId, weather, stationLocator), anchorUtc);
        }

        // Treat the most recent routine instant as already issued so loading mid-hour does not
        // immediately re-stamp every station; the next routine fires at the upcoming :53.
        _lastRoutineInstantUtc = MostRecentRoutineInstant(anchorUtc);
    }

    /// <summary>The current reported METAR string per station, in the order they were loaded.</summary>
    public IReadOnlyList<string> Reports
    {
        get
        {
            var reports = new List<string>(_stationOrder.Count);
            foreach (var id in _stationOrder)
            {
                reports.Add(_stations[id].CurrentReport);
            }

            return reports;
        }
    }

    /// <summary>
    /// Advances the observation clock to <paramref name="elapsedSeconds"/> and re-issues any station
    /// that is due (routine at :53, or a SPECI on significant change). Returns true if any report changed.
    /// </summary>
    public bool Tick(double elapsedSeconds, WeatherProfile weather, Func<string, (double Lat, double Lon)?> stationLocator)
    {
        var observationUtc = _anchorUtc.AddSeconds(elapsedSeconds);
        var routineInstant = MostRecentRoutineInstant(observationUtc);
        bool routineDue = routineInstant > _lastRoutineInstantUtc;

        bool changed = false;
        foreach (var id in _stationOrder)
        {
            var state = _stations[id];
            var current = Sample(id, weather, stationLocator);

            bool isSpeci = !routineDue && SpeciCriteria.IsSpeciWorthy(state.LastIssued, current);
            if (!routineDue && !isSpeci)
            {
                continue;
            }

            var baseMetar = FindBaseMetar(weather, id) ?? state.CurrentReport;
            var report = MetarComposer.Compose(baseMetar, current, observationUtc, isSpeci);
            _stations[id] = state with { CurrentReport = report, LastIssued = current, LastIssuedUtc = observationUtc };
            changed = true;
        }

        if (routineDue)
        {
            _lastRoutineInstantUtc = routineInstant;
        }

        return changed;
    }

    private static DateTime MostRecentRoutineInstant(DateTime time)
    {
        var thisHour = new DateTime(time.Year, time.Month, time.Day, time.Hour, RoutineObservationMinute, 0, DateTimeKind.Utc);
        return time >= thisHour ? thisHour : thisHour.AddHours(-1);
    }

    private static string? FindBaseMetar(WeatherProfile weather, string stationId)
    {
        foreach (var raw in weather.Metars)
        {
            var parsed = MetarParser.Parse(raw);
            if (parsed is not null && parsed.StationId.Equals(stationId, StringComparison.OrdinalIgnoreCase))
            {
                return raw.Trim();
            }
        }

        return null;
    }

    private static ReportedConditions Sample(string stationId, WeatherProfile weather, Func<string, (double Lat, double Lon)?> stationLocator)
    {
        var parsed = weather.GetWeatherForAirport(stationId);
        var (calm, dirTrue, speed, gust) = SampleWind(stationId, weather, parsed, stationLocator);

        return new ReportedConditions(
            Calm: calm,
            WindDirTrueDeg: dirTrue,
            WindSpeedKt: speed,
            WindGustKt: gust,
            VisibilityStatuteMiles: parsed?.VisibilityStatuteMiles,
            Layers: parsed?.Layers ?? [],
            CeilingFeetAgl: parsed?.CeilingFeetAgl,
            AltimeterInHg: parsed?.AltimeterInHg,
            Precipitation: !string.IsNullOrWhiteSpace(weather.Precipitation)
        );
    }

    private static (bool Calm, int DirTrue, int Speed, int? Gust) SampleWind(
        string stationId,
        WeatherProfile weather,
        MetarParser.ParsedMetar? parsed,
        Func<string, (double Lat, double Lon)?> stationLocator
    )
    {
        // Prefer the actual physics surface wind (lowest layer, stored magnetic) converted to true,
        // so the report matches what the sim applies to aircraft. Fall back to the base METAR's
        // (already-true) wind when no wind layers are present.
        if (weather.WindLayers.Count > 0)
        {
            var surface = weather.WindLayers[0];
            int speed = (int)Math.Round(surface.Speed, MidpointRounding.AwayFromZero);
            if (speed <= 2)
            {
                return (true, 0, speed, null);
            }

            double trueDir = surface.Direction;
            if (stationLocator(stationId) is { } location)
            {
                trueDir = MagneticDeclination.MagneticToTrue(surface.Direction, location.Lat, location.Lon);
            }

            int? gust = null;
            if (surface.Gusts is { } g)
            {
                int gustKt = (int)Math.Round(g, MidpointRounding.AwayFromZero);
                if (gustKt > speed)
                {
                    gust = gustKt;
                }
            }

            return (false, RoundToTen(trueDir), speed, gust);
        }

        if (parsed?.WindSpeedKts is { } parsedSpeed)
        {
            if (parsedSpeed <= 2 || parsed.WindDirectionDeg is null)
            {
                return (true, 0, parsedSpeed, null);
            }

            return (false, RoundToTen(parsed.WindDirectionDeg.Value), parsedSpeed, parsed.WindGustKts);
        }

        return (true, 0, 0, null);
    }

    private static int RoundToTen(double degrees)
    {
        int rounded = (int)Math.Round(degrees / 10.0, MidpointRounding.AwayFromZero) * 10;
        rounded %= 360;
        return rounded == 0 ? 360 : rounded;
    }

    private sealed record StationReport(string CurrentReport, ReportedConditions LastIssued, DateTime LastIssuedUtc);
}
