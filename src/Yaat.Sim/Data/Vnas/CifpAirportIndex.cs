using System.Collections.Concurrent;
using System.Text;

namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// Per-file index of where each airport's <c>SUSAP</c> records sit in a CIFP file, so the
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

    private static readonly byte[] RecordPrefix = "SUSAP"u8.ToArray();

    /// <summary>
    /// The <c>SUSAP</c> lines for <paramref name="airportIcao"/> (4-char, padded, case-insensitive),
    /// in file order, without line terminators. Empty when the airport has no records.
    /// </summary>
    public static IEnumerable<string> ReadAirportLines(string cifpFilePath, string airportIcao)
    {
        var info = new FileInfo(cifpFilePath);
        var key = (info.FullName, info.Length, info.LastWriteTimeUtc);
        var index = Indexes.GetOrAdd(key, static k => new Lazy<Dictionary<string, List<(long, long)>>>(() => Build(k.Path))).Value;
        if (!index.TryGetValue(airportIcao.ToUpperInvariant().PadRight(4), out var ranges))
        {
            return [];
        }

        return ReadRanges(cifpFilePath, ranges);
    }

    private static IEnumerable<string> ReadRanges(string path, List<(long Start, long End)> ranges)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16);
        foreach (var (start, end) in ranges)
        {
            var buffer = new byte[end - start];
            stream.Seek(start, SeekOrigin.Begin);
            stream.ReadExactly(buffer);
            var text = Encoding.UTF8.GetString(buffer);
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.Length > 0)
                {
                    yield return trimmed;
                }
            }
        }
    }

    private static Dictionary<string, List<(long Start, long End)>> Build(string path)
    {
        var bytes = File.ReadAllBytes(path);
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
        if (end - start < 10 || !bytes.AsSpan((int)start, RecordPrefix.Length).SequenceEqual(RecordPrefix))
        {
            return null;
        }

        return Encoding.ASCII.GetString(bytes, (int)start + 6, 4);
    }

    private static void CloseRange(Dictionary<string, List<(long Start, long End)>> ranges, string? icao, long start, long end)
    {
        if (icao is null || end <= start)
        {
            return;
        }

        if (!ranges.TryGetValue(icao, out var list))
        {
            list = [];
            ranges[icao] = list;
        }

        list.Add((start, end));
    }
}
