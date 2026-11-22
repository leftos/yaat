using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>Finds the airport an Airport Editor map is of, for its tower-cab image.</summary>
public static class AirportEditorAirportId
{
    /// <summary>How far the map's centre may be from the airport its file stem names.</summary>
    public const double MaxStemDistanceNm = 10;

    /// <summary>
    /// The vNAS cache's FAA code when the map came from it; else the file stem as an FAA code when it is 3 or 4 ASCII
    /// letters or digits and names an airport within <see cref="MaxStemDistanceNm"/> of the map's centre; else none.
    /// </summary>
    /// <param name="faaCode">The FAA code a vNAS-cache open gave, or null for a file or folder open.</param>
    /// <param name="sourcePath">The opened file's path (a folder open's combined file).</param>
    /// <param name="centre">The map's centre, or null when no feature has a position.</param>
    /// <returns>The FAA id, or null.</returns>
    public static string? Infer(string? faaCode, string sourcePath, LatLon? centre)
    {
        if (!string.IsNullOrWhiteSpace(faaCode))
        {
            return faaCode;
        }

        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        if ((centre is not { } at) || (stem.Length is < 3 or > 4) || (!stem.All(char.IsAsciiLetterOrDigit)))
        {
            return null;
        }

        string candidate = AirportLayoutDownloader.ToFaaCode(stem);
        bool near = NavigationDatabase
            .Instance.FindAirportsWithin(at, MaxStemDistanceNm)
            .Any(a => string.Equals(AirportLayoutDownloader.ToFaaCode(a.Id), candidate, StringComparison.Ordinal));
        return near ? candidate : null;
    }
}
