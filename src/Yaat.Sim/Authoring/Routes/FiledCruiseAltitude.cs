using System.Globalization;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;

namespace Yaat.Sim.Authoring.Routes;

/// <summary>
/// The cruise altitude to file for a flight plan whose cruise field is empty: the preferred route's published altitude
/// when it gives one, else the IFR direction-of-flight altitude (FAA 7110.65 §4-5-2 TBL 4-5-1, AIM §4-6-2) nearest the
/// type's usual cruise, with the usual cruise capped on a short trip at 150 ft per nautical mile and the filed altitude
/// floored 3,000 ft above the higher field.
/// </summary>
public static class FiledCruiseAltitude
{
    /// <summary>Feet of altitude per nautical mile of great-circle distance a short trip allows.</summary>
    public const double ShortTripFtPerNm = 150.0;

    /// <summary>Feet above the higher field the direction rule starts at (7110.65 TBL 4-5-1 starts it 3,000 ft above the surface).</summary>
    private const double DirectionFloorFt = 3000.0;

    /// <summary>Two airports closer together than this many nautical miles are the same airport.</summary>
    private const double SameAirportNm = 0.5;

    /// <summary>The lowest altitude worth filing: a band whose high bound is below it leaves the field empty.</summary>
    private const int MinimumFiledFt = 1000;

    /// <summary>The top of the below-FL410 bands: odd thousands eastbound to FL410, even thousands westbound to FL400.</summary>
    private const int EastTopFt = 41000;
    private const int WestTopFt = 40000;

    /// <summary>The first altitude above FL410, where each direction takes every other odd thousand: 45000 east, 43000 west.</summary>
    private const int EastAboveTopFt = 45000;
    private const int WestAboveTopFt = 43000;

    /// <summary>The highest altitude the direction search will offer, a guard on the generated levels.</summary>
    private const int HighestFt = 60000;

    /// <summary>The top of the direction search for a type with no ceiling: the top of the below-FL410 bands.</summary>
    private const int FallbackTopFt = EastTopFt;

    private static readonly ILogger Log = SimLog.CreateLogger("FiledCruiseAltitude");

    /// <summary>The phrases that open an "at or above" altitude: "AOA FL240", "AT OR ABOVE 24000".</summary>
    private static readonly string[] AbovePrefixes = ["AOA ", "AT OR ABOVE "];

    /// <summary>The phrases that open an "at or below" altitude: "AOB FL220", "AT OR BELOW 22000".</summary>
    private static readonly string[] BelowPrefixes = ["AOB ", "AT OR BELOW "];

    /// <summary>
    /// The altitude (ft MSL) to file for <paramref name="aircraftType"/> flying <paramref name="departure"/> to
    /// <paramref name="destination"/>, or null when the type has no usual cruise (a helicopter or a vehicle).
    /// A cruise altitude already on the flight plan is the caller's to keep: this returns the filed value regardless.
    /// </summary>
    /// <param name="aircraftType">The ICAO type designator, e.g. "B738".</param>
    /// <param name="departure">The departure airport: its position is where the course is converted to magnetic.</param>
    /// <param name="destination">The destination airport.</param>
    /// <param name="preferredAltitude">The preferred route's ALT_DESCRIP text, or null when there is no preferred route.</param>
    /// <param name="magneticModelDateUtc">The date the magnetic model is evaluated at, for the direction of flight.</param>
    /// <returns>The altitude in feet, or null when the type has no usual cruise, the type is unknown, or both airports are the same.</returns>
    public static int? Pick(
        string aircraftType,
        CruiseAirport departure,
        CruiseAirport destination,
        string? preferredAltitude,
        DateTime magneticModelDateUtc
    )
    {
        if ((string.IsNullOrWhiteSpace(aircraftType)) || (!AircraftCategorization.IsKnownType(aircraftType)))
        {
            LogUnknownType(aircraftType);
            return null;
        }

        double distanceNm = GeoMath.DistanceNm(departure.Position, destination.Position);
        if (distanceNm < SameAirportNm)
        {
            return null;
        }

        double usualFt = UsualCruiseFt(aircraftType, out double? ceilingFt);
        if (usualFt <= 0)
        {
            LogNoUsualCruise(aircraftType);
            return null;
        }

        AircraftCategory category = AircraftCategorization.Categorize(aircraftType);
        PreferredAltitude? preferred = ParsePreferredAltitude(preferredAltitude, category, ceilingFt);
        if (preferred is PreferredAltitude.Level level)
        {
            return level.Ft;
        }

        bool eastbound = IsEastbound(departure.Position, destination.Position, magneticModelDateUtc);
        double cappedFt = Math.Min(usualFt, ShortTripFtPerNm * distanceNm);
        if (PickListOrBand(preferred, eastbound, cappedFt, ceilingFt) is { } published)
        {
            return published;
        }

        LogNoPreferredAltitude(preferredAltitude, aircraftType);

        double floorFt = Math.Max(departure.ElevationFt, destination.ElevationFt) + DirectionFloorFt;
        return PickDirection(eastbound, cappedFt, ceilingFt, floorFt);
    }

    /// <summary>The type's usual cruise in feet, capped at its ceiling; 0 when it has none (a helicopter or a vehicle).</summary>
    private static double UsualCruiseFt(string aircraftType, out double? ceilingFt)
    {
        AircraftProfile? profile = AircraftProfileDatabase.Get(aircraftType);
        double cruiseFt = profile is { CruiseAltitude: > 0 }
            ? profile.CruiseAltitude
            : CategoryPerformance.BaselineProfile(AircraftCategorization.Categorize(aircraftType)).CruiseAltitude;
        ceilingFt = AircraftPerformance.Ceiling(aircraftType);
        return (ceilingFt is { } ceiling) ? Math.Min(cruiseFt, ceiling) : cruiseFt;
    }

    /// <summary>Whether the initial great-circle course, converted to magnetic at the departure, lies in 000-179.</summary>
    private static bool IsEastbound(LatLon departure, LatLon destination, DateTime magneticModelDateUtc)
    {
        double courseTrue = GeoMath.BearingTo(departure, destination);
        double courseMagnetic = MagneticDeclination.TrueToMagnetic(courseTrue, departure, magneticModelDateUtc);
        return IsEastboundMagneticCourse(courseMagnetic);
    }

    /// <summary>Whether a magnetic course, to the nearest whole degree, lies in 000-179.</summary>
    internal static bool IsEastboundMagneticCourse(double courseMagnetic) => (Math.Round(courseMagnetic) % 360.0) < 180.0;

    /// <summary>The list's or band's published altitude, or null when neither offers one the type can fly.</summary>
    private static int? PickListOrBand(PreferredAltitude? preferred, bool eastbound, double usualFt, double? ceilingFt) =>
        preferred switch
        {
            PreferredAltitude.Multiple multiple => PickFromList(multiple.Fts, eastbound, usualFt, ceilingFt),
            PreferredAltitude.Band band => PickInBand(band, eastbound, usualFt, ceilingFt),
            _ => null,
        };

    /// <summary>
    /// The direction-of-flight altitude nearest <paramref name="usualFt"/> at or above the floor and no higher than the
    /// type's ceiling, lower on a tie; with none in that range, the highest altitude at or below the ceiling.
    /// </summary>
    private static int? PickDirection(bool eastbound, double usualFt, double? ceilingFt, double floorFt)
    {
        List<int> altitudes = DirectionAltitudes(eastbound, TopFt(ceilingFt));
        if (Nearest(altitudes.Where(ft => ft >= floorFt), usualFt) is { } filed)
        {
            return filed;
        }

        if (altitudes.Count == 0)
        {
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("No direction-of-flight altitude is at or below the type's ceiling; the filed cruise altitude stays empty");
            }

            return null;
        }

        return altitudes[^1];
    }

    /// <summary>The band's direction altitudes nearest <paramref name="usualFt"/>, else the band's high bound.</summary>
    private static int? PickInBand(PreferredAltitude.Band band, bool eastbound, double usualFt, double? ceilingFt)
    {
        int highFt = (ceilingFt is { } ceiling) ? Math.Min(band.HighFt, (int)ceiling) : band.HighFt;
        if (Nearest(DirectionAltitudes(eastbound, highFt).Where(ft => ft >= band.LowFt), usualFt) is { } inBand)
        {
            return inBand;
        }

        return (highFt >= MinimumFiledFt) ? highFt : null;
    }

    /// <summary>
    /// The listed altitude matching the direction nearest <paramref name="usualFt"/>, else the nearest listed one; values
    /// above the type's ceiling are dropped first, and a list left empty offers nothing.
    /// </summary>
    private static int? PickFromList(IReadOnlyList<int> values, bool eastbound, double usualFt, double? ceilingFt)
    {
        List<int> flyable = [.. values.Where(ft => (ceilingFt is not { } ceiling) || (ft <= ceiling))];
        if (flyable.Count == 0)
        {
            return null;
        }

        List<int> matching = [.. flyable.Where(ft => IsDirectionAltitude(ft, eastbound))];
        List<int> pool = (matching.Count > 0) ? matching : flyable;
        return Nearest(pool.OrderBy(ft => ft), usualFt)!.Value;
    }

    /// <summary>The altitudes a flight on this direction may file, ascending and no higher than <paramref name="topFt"/>.</summary>
    private static List<int> DirectionAltitudes(bool eastbound, int topFt)
    {
        int limitFt = Math.Min(topFt, HighestFt);
        var altitudes = new List<int>();
        int belowTopFt = eastbound ? EastTopFt : WestTopFt;
        for (int ft = eastbound ? 1000 : 2000; (ft <= belowTopFt) && (ft <= limitFt); ft += 2000)
        {
            altitudes.Add(ft);
        }

        for (int ft = eastbound ? EastAboveTopFt : WestAboveTopFt; ft <= limitFt; ft += 4000)
        {
            altitudes.Add(ft);
        }

        return altitudes;
    }

    /// <summary>Whether <paramref name="ft"/> is a level that direction may file.</summary>
    private static bool IsDirectionAltitude(int ft, bool eastbound)
    {
        if ((ft < 1000) || (ft % 1000 != 0))
        {
            return false;
        }

        if (ft <= EastTopFt)
        {
            return eastbound ? ft % 2000 == 1000 : ft % 2000 == 0;
        }

        return eastbound ? (ft - EastAboveTopFt) % 4000 == 0 : (ft - WestAboveTopFt) % 4000 == 0;
    }

    /// <summary>The highest altitude the search may offer: the type's ceiling, else the top of the below-FL410 bands.</summary>
    private static int TopFt(double? ceilingFt) => (ceilingFt is { } ceiling) ? (int)ceiling : FallbackTopFt;

    /// <summary>The ascending altitude nearest <paramref name="usualFt"/>, the lower of two equally near; null when empty.</summary>
    private static int? Nearest(IEnumerable<int> altitudes, double usualFt)
    {
        int? nearest = null;
        double nearestDistance = double.MaxValue;
        foreach (int ft in altitudes)
        {
            double distance = Math.Abs(ft - usualFt);
            if (distance < nearestDistance)
            {
                nearest = ft;
                nearestDistance = distance;
            }
        }

        return nearest;
    }

    /// <summary>What an ALT_DESCRIP says about altitude: one value, a closed band in feet, or a list of values.</summary>
    internal abstract record PreferredAltitude
    {
        /// <summary>One altitude, filed as published.</summary>
        public sealed record Level(int Ft) : PreferredAltitude;

        /// <summary>A band from <paramref name="LowFt"/> to <paramref name="HighFt"/> inclusive.</summary>
        public sealed record Band(int LowFt, int HighFt) : PreferredAltitude;

        /// <summary>The altitudes a list offers, any one of which may be filed.</summary>
        public sealed record Multiple(IReadOnlyList<int> Fts) : PreferredAltitude;
    }

    /// <summary>
    /// The altitude rule an ALT_DESCRIP states, or null when it states none: blank, no number, or nothing the type can
    /// fly (a level, floor or band wholly above its ceiling). 2-3 digit numbers are hundreds of feet, 4-5 digit numbers
    /// are feet.
    /// </summary>
    private static PreferredAltitude? ParsePreferredAltitude(string? text, AircraftCategory category, double? ceilingFt)
    {
        string altitude = text?.Trim().ToUpperInvariant() ?? "";
        if ((altitude.Length == 0) || (!altitude.Any(char.IsAsciiDigit)))
        {
            return null;
        }

        PreferredAltitude? parsed = StatedAltitudeParsers(category, ceilingFt)
            .Select(parser => parser(altitude))
            .FirstOrDefault(result => result is not null);
        return IsFlyable(parsed, ceilingFt) ? parsed : null;
    }

    /// <summary>The parsers an ALT_DESCRIP may match, in the order their forms are tried; the first that answers wins.</summary>
    private static Func<string, PreferredAltitude?>[] StatedAltitudeParsers(AircraftCategory category, double? ceilingFt) =>
        [
            altitude => TryParseFloor(altitude, ceilingFt, out PreferredAltitude? floor) ? floor : null,
            altitude => TryParseUpper(altitude, out PreferredAltitude? upper) ? upper : null,
            altitude => HasListMarker(altitude) ? ParseList(altitude) : null,
            altitude => altitude.Contains('-') ? ParseRange(altitude) : null,
            altitude => TryParseClassCode(altitude, category, out PreferredAltitude? classCode) ? classCode : null,
            altitude => ParseSingle(altitude),
        ];

    /// <summary>Whether the text names several altitudes, split by "OR" or a slash.</summary>
    private static bool HasListMarker(string text) => (text.Contains(" OR ", StringComparison.Ordinal)) || (text.Contains('/'));

    /// <summary>Whether the type can fly what the text states; a list is filtered value by value when it is picked.</summary>
    private static bool IsFlyable(PreferredAltitude? parsed, double? ceilingFt) =>
        parsed switch
        {
            PreferredAltitude.Level level => (ceilingFt is not { } ceiling) || (level.Ft <= ceiling),
            PreferredAltitude.Band band => (ceilingFt is not { } ceiling) || (band.LowFt <= ceiling),
            PreferredAltitude.Multiple => true,
            _ => false,
        };

    /// <summary>"FL240 AND ABOVE", "AOA FL240" and "AT OR ABOVE 24000" as a band from the floor to the ceiling.</summary>
    private static bool TryParseFloor(string text, double? ceilingFt, out PreferredAltitude? parsed)
    {
        parsed = null;
        if ((PhraseNumber(text, AbovePrefixes, " AND ABOVE") is not { } numberText) || (ParseFeet(numberText) is not { } floorFt))
        {
            return false;
        }

        if ((ceilingFt is { } ceiling) && (floorFt > ceiling))
        {
            return false;
        }

        parsed = new PreferredAltitude.Band(floorFt, TopFt(ceilingFt));
        return true;
    }

    /// <summary>"AOB FL220", "AT OR BELOW 22000" and "22000 AND BELOW" as a band up to that altitude, and "BLO 110".</summary>
    private static bool TryParseUpper(string text, out PreferredAltitude? parsed)
    {
        parsed = null;
        bool belowN = text.StartsWith("BLO ", StringComparison.Ordinal);
        string? numberText = belowN ? text["BLO ".Length..] : PhraseNumber(text, BelowPrefixes, " AND BELOW");
        if ((numberText is null) || (ParseFeet(numberText) is not { } boundFt))
        {
            return false;
        }

        parsed = new PreferredAltitude.Band(0, belowN ? Math.Max(0, boundFt - 1000) : boundFt);
        return true;
    }

    /// <summary>A hyphenated range such as "70-170 INCL", "FL180-FL220", "SFC-100" or "120-FL220".</summary>
    private static PreferredAltitude.Band? ParseRange(string text)
    {
        string[] parts = text.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return null;
        }

        string highText = parts[1].Replace(" INCL", "", StringComparison.Ordinal).Trim();
        if ((ParseFeet(parts[0]) is not { } lowFt) || (ParseFeet(highText) is not { } highFt) || (lowFt > highFt))
        {
            return null;
        }

        return new PreferredAltitude.Band(lowFt, highFt);
    }

    /// <summary>A slash or "OR" list such as "6000 / 8000" or "040 OR 060".</summary>
    private static PreferredAltitude.Multiple? ParseList(string text)
    {
        string[] parts = text.Contains(" OR ", StringComparison.Ordinal)
            ? text.Split(" OR ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : text.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var values = new List<int>();
        foreach (string part in parts)
        {
            if (ParseFeet(part) is not { } ft)
            {
                return null;
            }

            values.Add(ft);
        }

        return (values.Count == 0) ? null : new PreferredAltitude.Multiple(values);
    }

    /// <summary>
    /// A TEC class code such as "J30" or "JM110PQ70", as the altitude after the aircraft's class letter (the letters
    /// <see cref="PreferredRouteSource.TecClassLetters"/> gives).
    /// </summary>
    private static bool TryParseClassCode(string text, AircraftCategory category, out PreferredAltitude? parsed)
    {
        parsed = null;
        if (!PreferredRouteSource.IsTecClassCode(text))
        {
            return false;
        }

        foreach (char letter in PreferredRouteSource.TecClassLetters(category))
        {
            int index = text.IndexOf(letter, StringComparison.Ordinal);
            string? digits = (index < 0) ? null : DigitsAfter(text, index);
            if ((digits is not null) && (ParseFeet(digits) is { } ft))
            {
                parsed = new PreferredAltitude.Level(ft);
                return true;
            }
        }

        return false;
    }

    /// <summary>The run of digits that follows <paramref name="index"/> in <paramref name="text"/>, or null when none follows.</summary>
    private static string? DigitsAfter(string text, int index)
    {
        int start = index;
        while ((start < text.Length) && (!char.IsAsciiDigit(text[start])))
        {
            start++;
        }

        int end = start;
        while ((end < text.Length) && (char.IsAsciiDigit(text[end])))
        {
            end++;
        }

        return (end > start) ? text[start..end] : null;
    }

    /// <summary>The number a phrase states: the text after a matching prefix, else the text before the suffix; null for neither.</summary>
    private static string? PhraseNumber(string text, string[] prefixes, string suffix)
    {
        foreach (string prefix in prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return text[prefix.Length..].Trim();
            }
        }

        return text.EndsWith(suffix, StringComparison.Ordinal) ? text[..^suffix.Length].Trim() : null;
    }

    /// <summary>One plain altitude token, filed as published when it parses.</summary>
    private static PreferredAltitude.Level? ParseSingle(string text) => (ParseFeet(text) is { } ft) ? new PreferredAltitude.Level(ft) : null;

    /// <summary>The feet an altitude token names: "SFC" is 0, "FL" scales by hundreds, 2-3 digits are hundreds of feet, 4-5 are feet.</summary>
    private static int? ParseFeet(string token)
    {
        string text = token.Trim();
        if (text.Equals("SFC", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (text.StartsWith("FL", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..].Trim();
        }

        if ((text.Length is < 2 or > 5) || (!text.All(char.IsAsciiDigit)))
        {
            return null;
        }

        int value = int.Parse(text, CultureInfo.InvariantCulture);
        return (text.Length <= 3) ? value * 100 : value;
    }

    private static void LogUnknownType(string? aircraftType)
    {
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Aircraft type '{Type}' is not in the aircraft tables; the filed cruise altitude stays empty", aircraftType);
        }
    }

    private static void LogNoUsualCruise(string aircraftType)
    {
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("No usual cruise altitude for type {Type}; the filed cruise altitude stays empty", aircraftType);
        }
    }

    private static void LogNoPreferredAltitude(string? preferredAltitude, string aircraftType)
    {
        if ((!string.IsNullOrWhiteSpace(preferredAltitude)) && (Log.IsEnabled(LogLevel.Debug)))
        {
            Log.LogDebug(
                "Preferred route altitude '{Altitude}' names no usable band for {Type}; using the direction-of-flight altitude",
                preferredAltitude,
                aircraftType
            );
        }
    }
}

/// <summary>An airport a filed cruise altitude is worked out between: where it is, and how high its field is.</summary>
/// <param name="Position">The airport reference point.</param>
/// <param name="ElevationFt">The field elevation in feet MSL.</param>
public sealed record CruiseAirport(LatLon Position, double ElevationFt);
