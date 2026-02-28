using Yaat.Sim.Data;

namespace Yaat.Sim.Commands;

public static class AltitudeResolver
{
    /// <summary>
    /// Resolves an altitude argument that may be numeric (e.g., "050", "5000")
    /// or AGL with an airport prefix (e.g., "KOAK010").
    /// Returns the final MSL altitude in feet, or null if the argument is invalid.
    /// </summary>
    public static int? Resolve(string? arg, IFixLookup? fixes)
    {
        if (arg is null)
        {
            return null;
        }

        if (int.TryParse(arg, out var value))
        {
            if (value <= 0)
            {
                return null;
            }

            return value < 1000 ? value * 100 : value;
        }

        // AGL format: {letters}{digits} e.g., "KOAK010"
        int splitIndex = -1;
        for (int i = 0; i < arg.Length; i++)
        {
            if (char.IsDigit(arg[i]))
            {
                splitIndex = i;
                break;
            }
        }

        if (splitIndex <= 0)
        {
            return null;
        }

        var airportCode = arg[..splitIndex];
        var digitsPart = arg[splitIndex..];

        if (!int.TryParse(digitsPart, out var aglValue) || aglValue <= 0)
        {
            return null;
        }

        int aglAltitude = aglValue < 1000 ? aglValue * 100 : aglValue;

        if (fixes is null)
        {
            return null;
        }

        var elevation = fixes.GetAirportElevation(airportCode);
        if (elevation is null)
        {
            return null;
        }

        return (int)Math.Round(elevation.Value) + aglAltitude;
    }
}
