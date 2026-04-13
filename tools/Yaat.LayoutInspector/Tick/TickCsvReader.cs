using System.Globalization;

namespace Yaat.LayoutInspector.Tick;

/// <summary>
/// Reads <c>TickRecorder</c> CSV files into <see cref="TickDataRow"/>s. All
/// numeric columns are parsed with <see cref="CultureInfo.InvariantCulture"/>
/// because the recorder always writes in invariant format.
/// </summary>
public static class TickCsvReader
{
    /// <summary>
    /// Parses the CSV at <paramref name="path"/>. An empty file or a file with
    /// only a header line returns an empty list (not an error).
    /// </summary>
    public static List<TickDataRow> Read(string path)
    {
        var rows = new List<TickDataRow>();
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2)
        {
            return rows;
        }

        var headers = lines[0].Split(',');
        int Col(string name) => Array.IndexOf(headers, name);

        int iT = Col("t"),
            iLat = Col("lat"),
            iLon = Col("lon"),
            iHdg = Col("hdg"),
            iGs = Col("gs");
        int iPhase = Col("phase"),
            iTwy = Col("twy");
        int iNavTarget = Col("navTarget"),
            iNavDist = Col("navDist"),
            iNavBrg = Col("navBrg");
        int iNavTargetSpd = Col("navTargetSpd"),
            iNavBrakeLimit = Col("navBrakeLimit");
        int iNavArcLimit = Col("navArcLimit"),
            iNavOnArc = Col("navOnArc"),
            iNavNodeReqSpd = Col("navNodeReqSpd");

        for (int i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split(',');
            if (parts.Length < 7)
            {
                continue;
            }

            rows.Add(
                new TickDataRow(
                    Time: int.Parse(parts[iT]),
                    Lat: double.Parse(parts[iLat], CultureInfo.InvariantCulture),
                    Lon: double.Parse(parts[iLon], CultureInfo.InvariantCulture),
                    Hdg: double.Parse(parts[iHdg], CultureInfo.InvariantCulture),
                    Gs: double.Parse(parts[iGs], CultureInfo.InvariantCulture),
                    Phase: parts[iPhase],
                    Twy: parts[iTwy],
                    NavTarget: TryParseInt(parts, iNavTarget),
                    NavDist: TryParseDouble(parts, iNavDist),
                    NavBrg: TryParseDouble(parts, iNavBrg),
                    NavTargetSpd: TryParseDouble(parts, iNavTargetSpd),
                    NavBrakeLimit: TryParseDouble(parts, iNavBrakeLimit),
                    NavArcLimit: TryParseDouble(parts, iNavArcLimit),
                    NavOnArc: TryParseInt(parts, iNavOnArc) == 1,
                    NavNodeReqSpd: TryParseDouble(parts, iNavNodeReqSpd)
                )
            );
        }

        return rows;
    }

    private static int? TryParseInt(string[] parts, int idx)
    {
        if ((idx < 0) || (idx >= parts.Length) || string.IsNullOrEmpty(parts[idx]))
        {
            return null;
        }

        return int.TryParse(parts[idx], out int v) ? v : null;
    }

    private static double? TryParseDouble(string[] parts, int idx)
    {
        if ((idx < 0) || (idx >= parts.Length) || string.IsNullOrEmpty(parts[idx]))
        {
            return null;
        }

        return double.TryParse(parts[idx], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
    }
}
