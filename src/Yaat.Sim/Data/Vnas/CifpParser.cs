using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// Single-pass ARINC 424 CIFP parser that extracts FAF fix
/// names per (airport, runway) and terminal waypoint coordinates.
/// </summary>
public static partial class CifpParser
{
    // Approach type priority: ILS > LOC > RNAV > everything else.
    // Lower value = higher priority.
    private static readonly Dictionary<char, int> ApproachTypePriority =
        new()
        {
            ['I'] = 0, // ILS
            ['L'] = 1, // LOC
            ['H'] = 2, // RNAV (GPS)
            ['R'] = 3, // RNAV
            ['P'] = 4, // GPS
        };

    private const int DefaultPriority = 10;

    public static CifpParseResult Parse(
        string cifpFilePath, ILogger? logger = null)
    {
        var fafByApproach = new Dictionary<string, FafCandidate>();
        var terminalWaypoints = new Dictionary<string, (double Lat, double Lon)>(
            StringComparer.OrdinalIgnoreCase);

        int approachRecords = 0;
        int waypointRecords = 0;

        foreach (var line in File.ReadLines(cifpFilePath))
        {
            if (line.Length < 50)
            {
                continue;
            }

            if (!line.StartsWith("SUSAP", StringComparison.Ordinal))
            {
                continue;
            }

            char subsection = line[12];

            if (subsection == 'F')
            {
                ProcessApproachRecord(line, fafByApproach);
                approachRecords++;
            }
            else if (subsection == 'C')
            {
                ProcessTerminalWaypoint(line, terminalWaypoints);
                waypointRecords++;
            }
        }

        // Convert per-approach FAF map to per-(airport, runway) map,
        // preferring higher-priority approach types
        var fafFixes = new Dictionary<(string Airport, string Runway), string>();

        foreach (var (_, candidate) in fafByApproach)
        {
            var key = (candidate.Airport, candidate.Runway);

            if (fafFixes.ContainsKey(key))
            {
                // Only replace if this approach has higher priority
                var existingKey = fafByApproach.Values
                    .FirstOrDefault(c =>
                        c.Airport == candidate.Airport
                        && c.Runway == candidate.Runway
                        && c.FafFix == fafFixes[key]);
                if (existingKey is not null
                    && candidate.Priority < existingKey.Priority)
                {
                    fafFixes[key] = candidate.FafFix;
                }
            }
            else
            {
                fafFixes[key] = candidate.FafFix;
            }
        }

        logger?.LogInformation(
            "CIFP parsed: {Approaches} approach records, "
            + "{Waypoints} waypoint records, "
            + "{FafCount} FAF fixes, "
            + "{WpCount} terminal waypoints",
            approachRecords, waypointRecords,
            fafFixes.Count, terminalWaypoints.Count);

        return new CifpParseResult(fafFixes, terminalWaypoints);
    }

    private static void ProcessApproachRecord(
        string line,
        Dictionary<string, FafCandidate> fafByApproach)
    {
        // Waypoint description code at position 43 (0-indexed: 42)
        char waypointDesc = line[42];
        if (waypointDesc is not ('D' or 'F'))
        {
            return; // Not a FAF
        }

        // Airport ICAO at positions 7-10 (0-indexed: 6-9)
        string icao = line[6..10].Trim();
        string airport = icao.StartsWith('K') ? icao[1..] : icao;

        // Approach ID at positions 14-19 (0-indexed: 13-18)
        string approachId = line[13..19].Trim();
        if (approachId.Length == 0)
        {
            return;
        }

        // Extract runway from approach ID
        string? runway = ParseRunwayFromApproachId(approachId);
        if (runway is null)
        {
            return;
        }

        // Fix identifier at positions 30-34 (0-indexed: 29-33)
        string fixId = line[29..34].Trim();
        if (fixId.Length == 0)
        {
            return;
        }

        // Approach type priority from first char of approach ID
        char typeCode = approachId[0];
        int priority = ApproachTypePriority.GetValueOrDefault(
            typeCode, DefaultPriority);

        string key = $"{airport}:{approachId}";

        // Keep the last FAF in each approach (highest sequence wins)
        if (!fafByApproach.TryGetValue(key, out var existing)
            || existing.Priority > priority)
        {
            fafByApproach[key] = new FafCandidate(
                airport, runway, fixId, priority);
        }
    }

    private static void ProcessTerminalWaypoint(
        string line,
        Dictionary<string, (double Lat, double Lon)> waypoints)
    {
        // Waypoint identifier at positions 14-18 (0-indexed: 13-17)
        string ident = line[13..18].Trim();
        if (ident.Length == 0 || waypoints.ContainsKey(ident))
        {
            return;
        }

        // Scan for N/S latitude marker starting from position 28
        int latStart = -1;
        int scanEnd = Math.Min(45, line.Length);
        for (int i = 28; i < scanEnd; i++)
        {
            if (line[i] is 'N' or 'S')
            {
                latStart = i;
                break;
            }
        }

        if (latStart < 0 || line.Length < latStart + 19)
        {
            return;
        }

        var lat = ParseArinc424Latitude(
            line.AsSpan(latStart, 9));
        var lon = ParseArinc424Longitude(
            line.AsSpan(latStart + 9, 10));

        if (lat is not null && lon is not null)
        {
            waypoints[ident] = (lat.Value, lon.Value);
        }
    }

    internal static double? ParseArinc424Latitude(ReadOnlySpan<char> s)
    {
        if (s.Length < 9)
        {
            return null;
        }

        char hemisphere = s[0];
        if (hemisphere is not ('N' or 'S'))
        {
            return null;
        }

        if (!int.TryParse(s[1..3], out int deg)
            || !int.TryParse(s[3..5], out int min)
            || !int.TryParse(s[5..7], out int sec)
            || !int.TryParse(s[7..9], out int hundredths))
        {
            return null;
        }

        double result = deg + min / 60.0
            + (sec + hundredths / 100.0) / 3600.0;
        return hemisphere == 'S' ? -result : result;
    }

    internal static double? ParseArinc424Longitude(ReadOnlySpan<char> s)
    {
        if (s.Length < 10)
        {
            return null;
        }

        char hemisphere = s[0];
        if (hemisphere is not ('E' or 'W'))
        {
            return null;
        }

        if (!int.TryParse(s[1..4], out int deg)
            || !int.TryParse(s[4..6], out int min)
            || !int.TryParse(s[6..8], out int sec)
            || !int.TryParse(s[8..10], out int hundredths))
        {
            return null;
        }

        double result = deg + min / 60.0
            + (sec + hundredths / 100.0) / 3600.0;
        return hemisphere == 'W' ? -result : result;
    }

    private static string? ParseRunwayFromApproachId(string approachId)
    {
        if (approachId.Length < 2)
        {
            return null;
        }

        // Skip first character (approach type code)
        string rest = approachId[1..];

        var match = RunwayPattern().Match(rest);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^(\d{1,2}[LRC]?)")]
    private static partial Regex RunwayPattern();

    private sealed record FafCandidate(
        string Airport, string Runway, string FafFix, int Priority);
}

/// <summary>
/// Result of parsing a CIFP file: FAF fix names per runway
/// and terminal waypoint coordinates.
/// </summary>
public sealed class CifpParseResult
{
    /// <summary>
    /// (airport FAA ID, runway ID) → FAF fix identifier.
    /// </summary>
    public Dictionary<(string Airport, string Runway), string> FafFixes { get; }

    /// <summary>
    /// Fix identifier → (lat, lon) for terminal waypoints.
    /// </summary>
    public Dictionary<string, (double Lat, double Lon)> TerminalWaypoints { get; }

    public CifpParseResult(
        Dictionary<(string Airport, string Runway), string> fafFixes,
        Dictionary<string, (double Lat, double Lon)> terminalWaypoints)
    {
        FafFixes = fafFixes;
        TerminalWaypoints = terminalWaypoints;
    }
}
