using Yaat.Sim.Data;

namespace Yaat.Sim.Commands;

public static class AltitudeResolver
{
    /// <summary>
    /// Resolves an altitude argument that may be numeric (e.g., "050", "5000")
    /// or AGL with an airport prefix (e.g., "KOAK+010").
    /// Returns the final MSL altitude in feet, or null if the argument is invalid.
    /// </summary>
    public static int? Resolve(string? arg)
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

        // AGL format: {airport}+{digits} e.g., "KOAK+010"
        var plusIndex = arg.IndexOf('+');
        if (plusIndex <= 0 || plusIndex == arg.Length - 1)
        {
            return null;
        }

        var airportCode = arg[..plusIndex];
        var digitsPart = arg[(plusIndex + 1)..];

        if (!int.TryParse(digitsPart, out var aglValue) || aglValue <= 0)
        {
            return null;
        }

        int aglAltitude = aglValue < 1000 ? aglValue * 100 : aglValue;

        var elevation = NavigationDatabase.Instance.GetAirportElevation(airportCode);
        if (elevation is null)
        {
            return null;
        }

        return (int)Math.Round(elevation.Value) + aglAltitude;
    }
}
