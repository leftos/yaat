using System.Text.Json.Serialization;

namespace Yaat.Sim;

public class WeatherTimeline
{
    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("artccId")]
    public string ArtccId { get; set; } = "";

    [JsonPropertyName("periods")]
    public List<WeatherPeriod> Periods { get; set; } = [];

    /// <summary>
    /// Returns an interpolated <see cref="WeatherProfile"/> for the given elapsed simulation time.
    /// Wind layers interpolate smoothly during transitions; METARs and precipitation snap at transition start.
    /// </summary>
    public WeatherProfile GetWeatherAt(double elapsedSeconds)
    {
        if (Periods.Count == 0)
        {
            return new WeatherProfile();
        }

        // Find the active period: last period whose start time <= elapsedSeconds
        int activeIndex = 0;
        for (int i = 1; i < Periods.Count; i++)
        {
            if (Periods[i].StartMinutes * 60.0 <= elapsedSeconds)
            {
                activeIndex = i;
            }
            else
            {
                break;
            }
        }

        var activePeriod = Periods[activeIndex];

        // If this is the first period or transition is instant, return the active period directly
        if (activeIndex == 0 || activePeriod.TransitionMinutes <= 0)
        {
            return BuildProfile(activePeriod);
        }

        double transitionStart = activePeriod.StartMinutes * 60.0;
        double transitionEnd = transitionStart + activePeriod.TransitionMinutes * 60.0;

        // Truncate transition if the next period starts before it ends
        if (activeIndex + 1 < Periods.Count)
        {
            double nextStart = Periods[activeIndex + 1].StartMinutes * 60.0;
            if (nextStart < transitionEnd)
            {
                transitionEnd = nextStart;
            }
        }

        if (elapsedSeconds >= transitionEnd)
        {
            return BuildProfile(activePeriod);
        }

        // We're within the transition window — interpolate wind from previous period
        var previousPeriod = Periods[activeIndex - 1];
        double t = (elapsedSeconds - transitionStart) / (transitionEnd - transitionStart);
        t = Math.Clamp(t, 0.0, 1.0);

        var interpolatedLayers = InterpolateWindLayers(previousPeriod.WindLayers, activePeriod.WindLayers, t);
        var metarOverrides = InterpolateMetars(previousPeriod.Metars, activePeriod.Metars, t);

        return new WeatherProfile
        {
            Name = Name,
            ArtccId = ArtccId,
            Precipitation = activePeriod.Precipitation,
            WindLayers = interpolatedLayers,
            Metars = activePeriod.Metars,
            ParsedMetarOverrides = metarOverrides,
        };
    }

    /// <summary>
    /// Compares two weather profiles for meaningful changes (used to rate-limit broadcasts).
    /// Returns true if wind direction differs by more than 1° or speed by more than 0.5kt at any layer,
    /// or if METARs/precipitation changed.
    /// </summary>
    public static bool HasMeaningfulChange(WeatherProfile? a, WeatherProfile? b)
    {
        if (a is null && b is null)
        {
            return false;
        }

        if (a is null || b is null)
        {
            return true;
        }

        if (a.Precipitation != b.Precipitation)
        {
            return true;
        }

        if (a.Metars.Count != b.Metars.Count)
        {
            return true;
        }

        for (int i = 0; i < a.Metars.Count; i++)
        {
            if (a.Metars[i] != b.Metars[i])
            {
                return true;
            }
        }

        if (a.WindLayers.Count != b.WindLayers.Count)
        {
            return true;
        }

        for (int i = 0; i < a.WindLayers.Count; i++)
        {
            if (Math.Abs(a.WindLayers[i].Direction - b.WindLayers[i].Direction) > 1.0)
            {
                return true;
            }

            if (Math.Abs(a.WindLayers[i].Speed - b.WindLayers[i].Speed) > 0.5)
            {
                return true;
            }
        }

        return false;
    }

    private static List<WindLayer> InterpolateWindLayers(List<WindLayer> from, List<WindLayer> to, double t)
    {
        // If layer counts differ, snap to target
        if (from.Count != to.Count)
        {
            return to;
        }

        var result = new List<WindLayer>(from.Count);
        for (int i = 0; i < from.Count; i++)
        {
            var a = from[i];
            var b = to[i];

            // Interpolate speed linearly
            double speed = a.Speed + t * (b.Speed - a.Speed);

            // Interpolate direction using N/E vector decomposition (handles 360/0 wrap)
            double aRad = a.Direction * DegToRad;
            double bRad = b.Direction * DegToRad;

            double interpN = Math.Cos(aRad) + t * (Math.Cos(bRad) - Math.Cos(aRad));
            double interpE = Math.Sin(aRad) + t * (Math.Sin(bRad) - Math.Sin(aRad));
            double direction = Math.Atan2(interpE, interpN) * RadToDeg;
            if (direction < 0)
            {
                direction += 360.0;
            }

            // Interpolate gusts if both have them
            double? gusts = null;
            if (a.Gusts.HasValue && b.Gusts.HasValue)
            {
                gusts = a.Gusts.Value + t * (b.Gusts.Value - a.Gusts.Value);
            }
            else if (b.Gusts.HasValue)
            {
                gusts = b.Gusts;
            }

            result.Add(
                new WindLayer
                {
                    Id = b.Id,
                    Altitude = b.Altitude,
                    Direction = direction,
                    Speed = speed,
                    Gusts = gusts,
                }
            );
        }

        return result;
    }

    private static Dictionary<string, MetarParser.ParsedMetar>? InterpolateMetars(List<string> fromMetars, List<string> toMetars, double t)
    {
        if (fromMetars.Count == 0 || toMetars.Count == 0)
        {
            return null;
        }

        var fromParsed = ParseMetarsByStation(fromMetars);
        var toParsed = ParseMetarsByStation(toMetars);

        if (fromParsed.Count == 0 || toParsed.Count == 0)
        {
            return null;
        }

        var overrides = new Dictionary<string, MetarParser.ParsedMetar>(StringComparer.OrdinalIgnoreCase);

        foreach (var (stationId, toMetar) in toParsed)
        {
            if (!fromParsed.TryGetValue(stationId, out var fromMetar))
            {
                continue;
            }

            int? ceiling = LerpNullableInt(fromMetar.CeilingFeetAgl, toMetar.CeilingFeetAgl, t);
            double? visibility = LerpNullableDouble(fromMetar.VisibilityStatuteMiles, toMetar.VisibilityStatuteMiles, t);
            double? altimeter = LerpNullableDouble(fromMetar.AltimeterInHg, toMetar.AltimeterInHg, t);

            overrides[stationId] = new MetarParser.ParsedMetar(stationId, ceiling, visibility, AltimeterInHg: altimeter);
        }

        return overrides.Count > 0 ? overrides : null;
    }

    private static Dictionary<string, MetarParser.ParsedMetar> ParseMetarsByStation(List<string> metars)
    {
        var result = new Dictionary<string, MetarParser.ParsedMetar>(StringComparer.OrdinalIgnoreCase);
        foreach (var metarStr in metars)
        {
            var parsed = MetarParser.Parse(metarStr);
            if (parsed is not null)
            {
                result[parsed.StationId] = parsed;
            }
        }
        return result;
    }

    private static int? LerpNullableInt(int? from, int? to, double t)
    {
        if (from is null || to is null)
        {
            return to;
        }
        return (int)(from.Value + t * (to.Value - from.Value));
    }

    private static double? LerpNullableDouble(double? from, double? to, double t)
    {
        if (from is null || to is null)
        {
            return to;
        }
        return from.Value + t * (to.Value - from.Value);
    }

    private WeatherProfile BuildProfile(WeatherPeriod period)
    {
        return new WeatherProfile
        {
            Name = Name,
            ArtccId = ArtccId,
            Precipitation = period.Precipitation,
            WindLayers = period.WindLayers,
            Metars = period.Metars,
        };
    }
}
