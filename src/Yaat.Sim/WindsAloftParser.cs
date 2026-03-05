namespace Yaat.Sim;

/// <summary>Wind data at a specific altitude from an FD report.</summary>
public readonly record struct WindAtLevel(int AltitudeFt, int DirectionTrue, int SpeedKts, bool IsLightVariable);

/// <summary>All wind data for a single FD station.</summary>
public readonly record struct StationWinds(string StationId, IReadOnlyList<WindAtLevel> Winds);

/// <summary>
/// Parses FAA Winds and Temperatures Aloft (FD) fixed-width text format.
/// Data source: aviationweather.gov /api/data/windtemp
/// </summary>
public static class WindsAloftParser
{
    private static readonly int[] StandardLevels = [3000, 6000, 9000, 12_000, 18_000, 24_000, 30_000, 34_000, 39_000];

    /// <summary>
    /// Parses FD text into a list of station wind reports.
    /// </summary>
    public static List<StationWinds> Parse(string fdText)
    {
        var results = new List<StationWinds>();
        if (string.IsNullOrWhiteSpace(fdText))
        {
            return results;
        }

        var lines = fdText.Split('\n');

        // Find the FT header line to determine column positions
        int headerLineIndex = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("FT", StringComparison.Ordinal) || trimmed.StartsWith("  FT", StringComparison.Ordinal))
            {
                // Verify it has altitude numbers
                if (trimmed.Contains("3000") || trimmed.Contains("6000"))
                {
                    headerLineIndex = i;
                    break;
                }
            }
        }

        if (headerLineIndex < 0)
        {
            return results;
        }

        var headerLine = lines[headerLineIndex];
        var columns = ParseHeaderColumns(headerLine);
        if (columns.Count == 0)
        {
            return results;
        }

        // Parse data lines after header
        for (int i = headerLineIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Station ID is the first non-space token (3-letter identifier)
            var trimmed = line.TrimStart();
            if (trimmed.Length < 3)
            {
                continue;
            }

            int spaceIdx = trimmed.IndexOf(' ');
            if (spaceIdx < 2)
            {
                continue;
            }

            string stationId = trimmed[..spaceIdx].Trim();

            // Skip lines that look like headers or metadata
            if (stationId is "FT" or "VALID" or "DATA" or "TEMPS" or "BASED")
            {
                continue;
            }

            var winds = new List<WindAtLevel>();
            foreach (var (altitude, startCol, endCol) in columns)
            {
                if (startCol >= line.Length)
                {
                    continue;
                }

                int actualEnd = Math.Min(endCol, line.Length);
                var cell = line[startCol..actualEnd].Trim();
                if (string.IsNullOrEmpty(cell))
                {
                    continue;
                }

                var wind = DecodeWind(altitude, cell);
                if (wind is not null)
                {
                    winds.Add(wind.Value);
                }
            }

            if (winds.Count > 0)
            {
                results.Add(new StationWinds(stationId, winds));
            }
        }

        return results;
    }

    private static List<(int Altitude, int StartCol, int EndCol)> ParseHeaderColumns(string headerLine)
    {
        var columns = new List<(int Altitude, int StartCol, int EndCol)>();

        foreach (int level in StandardLevels)
        {
            string levelStr = level.ToString();
            int idx = headerLine.IndexOf(levelStr, StringComparison.Ordinal);
            if (idx < 0)
            {
                continue;
            }

            // Column center is at the altitude label position; data is 4 chars (DDSS) + optional temp
            // Estimate column start as center of the altitude label minus ~2 chars for the 4-char wind code
            int colCenter = idx + levelStr.Length / 2;
            int startCol = Math.Max(0, colCenter - 3);
            int endCol = colCenter + 5;

            columns.Add((level, startCol, endCol));
        }

        // Refine columns: each column ends where the next starts
        for (int i = 0; i < columns.Count - 1; i++)
        {
            var current = columns[i];
            var next = columns[i + 1];
            int midpoint = (current.EndCol + next.StartCol) / 2;
            columns[i] = (current.Altitude, current.StartCol, midpoint);
            columns[i + 1] = (next.Altitude, midpoint, next.EndCol);
        }

        return columns;
    }

    /// <summary>
    /// Decodes a 4-character FD wind code: DDSS where DD*10 = direction, SS = speed.
    /// If DD >= 50: direction = (DD-50)*10, speed = SS+100.
    /// 9900 = light and variable.
    /// </summary>
    internal static WindAtLevel? DecodeWind(int altitudeFt, string code)
    {
        // Strip temperature suffix (e.g., "2714-08" → "2714", "2321+03" → "2321")
        int signIdx = code.IndexOfAny(['+', '-']);
        if (signIdx >= 4)
        {
            code = code[..signIdx];
        }

        // Must be exactly 4 digits
        if (code.Length != 4 || !int.TryParse(code, out int value))
        {
            return null;
        }

        // Light and variable
        if (value == 9900)
        {
            return new WindAtLevel(altitudeFt, 0, 0, true);
        }

        int dd = value / 100;
        int ss = value % 100;

        int direction;
        int speed;

        if (dd >= 50)
        {
            direction = (dd - 50) * 10;
            speed = ss + 100;
        }
        else
        {
            direction = dd * 10;
            speed = ss;
        }

        // Normalize direction
        if (direction == 0 && speed > 0)
        {
            direction = 360;
        }

        if (direction > 360)
        {
            direction %= 360;
        }

        return new WindAtLevel(altitudeFt, direction, speed, false);
    }
}
