using System.Text;

namespace Yaat.Sim.Data.Airport.Precompute;

/// <summary>
/// The committed list of airports the precompute cache covers, <c>airports.txt</c> beside the entries: a <c>#</c> header
/// line naming the command that writes it, then one FAA id per line, sorted ordinal, LF line ends. The maintainer tool
/// computes the airports it lists when given none, and its check reads it too.
/// </summary>
public static class PrecomputeAirportList
{
    /// <summary>The list's file name in the cache folder.</summary>
    public const string FileName = "airports.txt";

    /// <summary>The first line of the file.</summary>
    public const string Header = "# Written by `dotnet run --project tools/Yaat.PrecomputeCache -- --refresh-airports`; not edited by hand.";

    /// <summary>Reads the listed airports; lines starting with <c>#</c> and blank lines are skipped.</summary>
    /// <param name="path">The list file.</param>
    /// <returns>The FAA ids, distinct, sorted ordinal.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file lists no airport.</exception>
    public static IReadOnlyList<string> Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No precompute airport list at {path}; `--refresh-airports` writes it", path);
        }

        IReadOnlyList<string> airports = ScenarioAirportCollector.Normalize(File.ReadAllLines(path).Where(line => !line.TrimStart().StartsWith('#')));
        return airports.Count == 0 ? throw new InvalidDataException($"The precompute airport list {path} names no airport") : airports;
    }

    /// <summary>Writes <paramref name="airportIds"/>, folded to FAA ids, distinct and sorted, under <see cref="Header"/>.</summary>
    /// <param name="path">The list file.</param>
    /// <param name="airportIds">The airports, FAA or ICAO ids.</param>
    public static void Write(string path, IEnumerable<string> airportIds)
    {
        StringBuilder text = new StringBuilder(Header).Append('\n');
        foreach (string airport in ScenarioAirportCollector.Normalize(airportIds))
        {
            text.Append(airport).Append('\n');
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
    }
}
