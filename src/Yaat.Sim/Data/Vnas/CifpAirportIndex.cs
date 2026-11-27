using System.Collections.Concurrent;
using System.Text;

namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// Per-file index of where each airport's records (section P, any area: <c>SUSAP</c>, <c>SPACP</c>, <c>SCANP</c>, …) sit in a CIFP file, so the
/// airport-scoped parsers (<see cref="CifpParser.ParseSids"/>, <see cref="CifpParser.ParseApproaches"/>, …)
/// read a few kilobytes instead of streaming the whole ~50 MB file on every call. Built once per
/// file per process (keyed by path, length and mtime); an airport's records are usually one
/// contiguous block, but a few airports split across two, so each maps to a list of byte ranges.
/// </summary>
public static class CifpAirportIndex
{
    private static readonly ConcurrentDictionary<
        (string Path, long Length, DateTime Mtime),
        Lazy<Dictionary<string, List<(long Start, long End)>>>
    > Indexes = new();

    /// <summary>
    /// Whether <paramref name="line"/> is an ARINC 424 airport-section (P) record of any area: <c>SUSAP</c> (USA),
    /// <c>SPACP</c> (Pacific: Hawaii, Guam), <c>SCANP</c> (Canada and Alaska), <c>SLAMP</c>, … The area code is
    /// columns 2-4 and the section column 5.
    /// </summary>
    public static bool IsAirportRecord(string line) => IsSectionRecord(line, 'P');

    /// <summary>Whether <paramref name="line"/> is an ARINC 424 navaid-section (D) record of any area (<c>SUSAD</c>, <c>SPACD</c>, …).</summary>
    public static bool IsNavaidRecord(string line) => IsSectionRecord(line, 'D');

    /// <summary>Whether <paramref name="line"/> is a record of the USA area (<c>SUSA</c>).</summary>
    public static bool IsUsaRecord(string line) => line.StartsWith("SUSA", StringComparison.Ordinal);

    /// <summary>
    /// The airport-section lines for <paramref name="airportIcao"/> (4-char, padded, case-insensitive),
    /// in file order, without line terminators. Empty when the airport has no records.
    /// </summary>
    public static IEnumerable<string> ReadAirportLines(string cifpFilePath, string airportIcao)
    {
        if (!GetIndex(cifpFilePath).TryGetValue(IndexKey(airportIcao), out List<(long Start, long End)>? ranges))
        {
            return [];
        }

        return ReadRanges(cifpFilePath, ranges);
    }

    /// <summary>Whether the CIFP file holds any airport-section record for <paramref name="airportIcao"/>.</summary>
    public static bool HasAirport(string cifpFilePath, string airportIcao) => GetIndex(cifpFilePath).ContainsKey(IndexKey(airportIcao));

    private static string IndexKey(string airportIcao) => airportIcao.ToUpperInvariant().PadRight(4);

    private static Dictionary<string, List<(long Start, long End)>> GetIndex(string cifpFilePath)
    {
        var info = new FileInfo(cifpFilePath);
        (string FullName, long Length, DateTime LastWriteTimeUtc) key = (info.FullName, info.Length, info.LastWriteTimeUtc);
        return Indexes.GetOrAdd(key, static k => new Lazy<Dictionary<string, List<(long, long)>>>(() => Build(k.Path))).Value;
    }

    private static bool IsSectionRecord(string line, char section) =>
        (line.Length >= 5) && (line[0] == 'S') && IsAreaCode(line[1], line[2], line[3]) && (line[4] == section);

    private static bool IsAreaCode(char a, char b, char c) => char.IsAsciiLetterUpper(a) && char.IsAsciiLetterUpper(b) && char.IsAsciiLetterUpper(c);

    private static IEnumerable<string> ReadRanges(string path, List<(long Start, long End)> ranges)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16);
        foreach ((long start, long end) in ranges)
        {
            byte[] buffer = new byte[end - start];
            stream.Seek(start, SeekOrigin.Begin);
            stream.ReadExactly(buffer);
            string text = Encoding.UTF8.GetString(buffer);
            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r');
                if (trimmed.Length > 0)
                {
                    yield return trimmed;
                }
            }
        }
    }

    private static Dictionary<string, List<(long Start, long End)>> Build(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var ranges = new Dictionary<string, List<(long Start, long End)>>(StringComparer.Ordinal);
        string? currentIcao = null;
        long currentStart = 0;
        long lineStart = 0;

        while (lineStart < bytes.Length)
        {
            int newline = Array.IndexOf(bytes, (byte)'\n', (int)lineStart);
            long lineEnd = newline < 0 ? bytes.Length : newline + 1;
            string? icao = LineAirport(bytes, lineStart, lineEnd);

            if (icao != currentIcao)
            {
                CloseRange(ranges, currentIcao, currentStart, lineStart);
                currentIcao = icao;
                currentStart = lineStart;
            }

            lineStart = lineEnd;
        }

        CloseRange(ranges, currentIcao, currentStart, bytes.Length);
        return ranges;
    }

    private static string? LineAirport(byte[] bytes, long start, long end)
    {
        // Only the record prefix and the airport field (bytes 6..10) matter here; each parser applies its
        // own minimum record length, so short synthetic fixtures still reach them.
        if (end - start < 10 || !IsAirportRecordPrefix(bytes.AsSpan((int)start, 5)))
        {
            return null;
        }

        return Encoding.ASCII.GetString(bytes, (int)start + 6, 4);
    }

    private static bool IsAirportRecordPrefix(ReadOnlySpan<byte> prefix) =>
        (prefix[0] == (byte)'S') && IsAreaCode((char)prefix[1], (char)prefix[2], (char)prefix[3]) && (prefix[4] == (byte)'P');

    private static void CloseRange(Dictionary<string, List<(long Start, long End)>> ranges, string? icao, long start, long end)
    {
        if (icao is null || end <= start)
        {
            return;
        }

        if (!ranges.TryGetValue(icao, out List<(long Start, long End)>? list))
        {
            list = [];
            ranges[icao] = list;
        }

        list.Add((start, end));
    }
}
