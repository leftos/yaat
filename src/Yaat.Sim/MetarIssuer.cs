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
            _stations[parsed.StationId] = new StationReport(
                raw.Trim(),
                Sample(parsed.StationId, weather, stationLocator, elapsedSeconds: 0, observationUtc: anchorUtc),
                anchorUtc
            );
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
            var current = Sample(id, weather, stationLocator, elapsedSeconds, observationUtc);

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

    private static ReportedConditions Sample(
        string stationId,
        WeatherProfile weather,
        Func<string, (double Lat, double Lon)?> stationLocator,
        double elapsedSeconds,
        DateTime observationUtc
    )
    {
        var parsed = weather.GetWeatherForAirport(stationId);
        var wind = SampleWind(stationId, weather, parsed, stationLocator, elapsedSeconds, observationUtc);

        return wind with
        {
            VisibilityStatuteMiles = parsed?.VisibilityStatuteMiles,
            Layers = parsed?.Layers ?? [],
            CeilingFeetAgl = parsed?.CeilingFeetAgl,
            AltimeterInHg = parsed?.AltimeterInHg,
            Precipitation = !string.IsNullOrWhiteSpace(weather.Precipitation),
        };
    }

    private static ReportedConditions SampleWind(
        string stationId,
        WeatherProfile weather,
        MetarParser.ParsedMetar? parsed,
        Func<string, (double Lat, double Lon)?> stationLocator,
        double elapsedSeconds,
        DateTime observationUtc
    )
    {
        // The mean and peak come from observing the simulated field (2-min means, 10-min
        // peak/lull at phase 0), converted magnetic → true here. The envelope groups
        // (gust value, dddVddd arc, VRB) code from the authored layer amplitudes: bounded
        // noise's true envelope IS the authored arc — a finite sample only ever
        // under-estimates it — so the round trip stays exact. Fall back to the base
        // METAR's own (already-true) wind for text-only weather.
        if (WindObservation.Observe(weather, elapsedSeconds) is { } obs)
        {
            double ToTrue(double magDeg)
            {
                return stationLocator(stationId) is { } location ? MagneticDeclination.MagneticToTrue(magDeg, location.Lat, location.Lon) : magDeg;
            }

            int meanSpeed = (int)Math.Round(obs.MeanSpeedKts, MidpointRounding.AwayFromZero);
            if (obs.MeanSpeedKts < WindObservation.CalmBelowKt)
            {
                return WindOnly(calm: true, dirTrue: 0, speed: meanSpeed, gust: null, variable: false, varFrom: null, varTo: null);
            }

            var surface = weather.WindLayers[0];
            bool layerVariable = surface.Variable ?? false;
            var (gustExcess, halfSpread) = WindVariation.ResolveAmplitudes(
                surface.Speed,
                surface.Gusts,
                surface.DirectionVariabilityDeg,
                layerVariable
            );
            double spread = 2.0 * halfSpread;

            // FMH-1 coding: variation ≥180° (or an authored VRB layer) codes VRB at any
            // speed; ≥60° codes VRB at ≤6 kt and a dddVddd group above 6 kt (extremes
            // clockwise around the authored mean, converted to true like the mean).
            bool variable =
                layerVariable
                || (spread >= WindObservation.VrbForcedSpreadDeg)
                || ((spread >= WindObservation.MinVariableSpreadDeg) && (obs.MeanSpeedKts <= WindObservation.VrbMaxMeanSpeedKt));
            bool hasVarGroup = (!variable) && (spread >= WindObservation.MinVariableSpreadDeg);

            int? gust = null;
            if (gustExcess >= WindObservation.MinGustExcessKt)
            {
                gust = (int)Math.Round(surface.Speed + gustExcess, MidpointRounding.AwayFromZero);
            }

            var wind = WindOnly(
                calm: false,
                dirTrue: RoundToTen(ToTrue(obs.MeanDirectionMagDeg)),
                speed: meanSpeed,
                gust: gust,
                variable: variable,
                varFrom: hasVarGroup ? RoundToTen(ToTrue(surface.Direction - halfSpread)) : null,
                varTo: hasVarGroup ? RoundToTen(ToTrue(surface.Direction + halfSpread)) : null
            );

            if (obs.PeakSpeedKts > WindObservation.PeakWindRemarkAboveKt)
            {
                wind = wind with
                {
                    PeakWindKt = (int)Math.Round(obs.PeakSpeedKts, MidpointRounding.AwayFromZero),
                    PeakWindDirTrueDeg = RoundToTen(ToTrue(obs.PeakDirectionMagDeg)),
                    PeakWindUtc = observationUtc.AddSeconds(obs.PeakElapsedSeconds - elapsedSeconds),
                };
            }

            return wind;
        }

        if (parsed?.WindSpeedKts is { } parsedSpeed)
        {
            if (parsed.WindVariable)
            {
                return WindOnly(calm: false, dirTrue: 0, speed: parsedSpeed, gust: parsed.WindGustKts, variable: true, varFrom: null, varTo: null);
            }

            if ((parsedSpeed < WindObservation.CalmBelowKt) || (parsed.WindDirectionDeg is null))
            {
                return WindOnly(calm: true, dirTrue: 0, speed: parsedSpeed, gust: null, variable: false, varFrom: null, varTo: null);
            }

            return WindOnly(
                calm: false,
                dirTrue: RoundToTen(parsed.WindDirectionDeg.Value),
                speed: parsedSpeed,
                gust: parsed.WindGustKts,
                variable: false,
                varFrom: parsed.WindVarFromDeg,
                varTo: parsed.WindVarToDeg
            );
        }

        return WindOnly(calm: true, dirTrue: 0, speed: 0, gust: null, variable: false, varFrom: null, varTo: null);
    }

    private static ReportedConditions WindOnly(bool calm, int dirTrue, int speed, int? gust, bool variable, int? varFrom, int? varTo)
    {
        return new ReportedConditions(
            Calm: calm,
            WindDirTrueDeg: dirTrue,
            WindSpeedKt: speed,
            WindGustKt: gust,
            WindVariable: variable,
            WindVarFromTrueDeg: varFrom,
            WindVarToTrueDeg: varTo,
            PeakWindKt: null,
            PeakWindDirTrueDeg: 0,
            PeakWindUtc: null,
            VisibilityStatuteMiles: null,
            Layers: [],
            CeilingFeetAgl: null,
            AltimeterInHg: null,
            Precipitation: false
        );
    }

    private static int RoundToTen(double degrees)
    {
        int rounded = (int)Math.Round(degrees / 10.0, MidpointRounding.AwayFromZero) * 10;
        rounded = ((rounded % 360) + 360) % 360;
        return rounded == 0 ? 360 : rounded;
    }

    private sealed record StationReport(string CurrentReport, ReportedConditions LastIssued, DateTime LastIssuedUtc);
}
