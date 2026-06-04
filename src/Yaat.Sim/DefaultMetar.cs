using System.Globalization;

namespace Yaat.Sim;

/// <summary>
/// Builds the standard fair-weather METAR shown when no weather profile is loaded
/// (calm wind, 10SM, clear sky, 29.92" altimeter) — the conditions the sim applies
/// by default. Shared by the desktop METAR panel, the radar/ground weather overlay,
/// and the vStrips METAR bar so every surface renders an identical default report.
/// </summary>
public static class DefaultMetar
{
    /// <summary>
    /// Returns the default report for <paramref name="airportId"/> (FAA or ICAO id) stamped at
    /// <paramref name="observationUtc"/>. The station id is normalized to ICAO via
    /// <see cref="MetarParser.ToIcao"/>, e.g. <c>"KSFO 031953Z AUTO 00000KT 10SM CLR A2992"</c>.
    /// </summary>
    public static string Build(string airportId, DateTime observationUtc)
    {
        var icao = MetarParser.ToIcao(airportId);
        var stamp = observationUtc.ToString("ddHHmm", CultureInfo.InvariantCulture) + "Z";
        return $"{icao} {stamp} AUTO 00000KT 10SM CLR A2992";
    }
}
