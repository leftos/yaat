using System.Globalization;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Testing;

namespace Yaat.TickInspector;

/// <summary>
/// Compact reader for per-tick aircraft state CSVs written by
/// <c>Yaat.Sim.Tests.Helpers.TickRecorder</c>.
///
/// Raw tick CSVs are painful to read by hand: long lat/lon columns,
/// <c>double.MaxValue</c> spam in the <c>navArcLimit</c> column, no visibility
/// into segment transitions, and no cross-track / heading-error signal unless
/// you hand-compute it. This tool pulls out the interesting columns, formats
/// them fixed-width, hides sentinel infinities, flags segment changes, and
/// (with <c>--runway</c>) adds signed cross-track and signed heading error
/// against the runway centerline — using the same <c>GeoMath</c> functions as
/// Yaat.Sim itself so there is no floating-point drift from a re-implementation.
///
/// Usage:
///   dotnet run --project tools/Yaat.TickInspector -- &lt;csv&gt; [--summary]
///              [--range START-END] [--runway ICAO/RWY] [--navdata DIR]
///
/// Examples:
///   # Full compact table, no reference line:
///   dotnet run --project tools/Yaat.TickInspector -- .tmp/sfo-lineup-28r-e.csv
///
///   # Per-segment summary with SFO 28R cross-track and heading error:
///   dotnet run --project tools/Yaat.TickInspector -- .tmp/sfo-lineup-28r-e.csv \
///       --summary --runway SFO/28R
///
///   # Ticks 38-60 around a segment transition:
///   dotnet run --project tools/Yaat.TickInspector -- .tmp/sfo-lineup-28r-e.csv \
///       --range 38-60 --runway SFO/28R
/// </summary>
public static class Program
{
    private const double FeetPerNm = GeoMath.FeetPerNm;

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        string? csvPath = null;
        string? runwayArg = null;
        string? navdataDir = null;
        bool summary = false;
        (int Lo, int Hi)? tickRange = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--summary":
                    summary = true;
                    break;
                case "--range" when i + 1 < args.Length:
                    tickRange = ParseRange(args[++i]);
                    break;
                case "--runway" when i + 1 < args.Length:
                    runwayArg = args[++i];
                    break;
                case "--navdata" when i + 1 < args.Length:
                    navdataDir = args[++i];
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return 0;
                default:
                    if (csvPath is null && !a.StartsWith('-'))
                    {
                        csvPath = a;
                    }
                    else
                    {
                        Console.Error.WriteLine($"Unknown argument: {a}");
                        PrintUsage();
                        return 2;
                    }
                    break;
            }
        }

        if (csvPath is null)
        {
            Console.Error.WriteLine("error: csv path required");
            PrintUsage();
            return 2;
        }

        if (!File.Exists(csvPath))
        {
            Console.Error.WriteLine($"error: {csvPath} not found");
            return 1;
        }

        RefLine? refLine = null;
        if (runwayArg is not null)
        {
            refLine = LoadRunwayReference(runwayArg, navdataDir);
            if (refLine is null)
            {
                return 1;
            }
        }

        var rows = LoadRows(csvPath);
        if (tickRange is { } r)
        {
            rows = rows.Where(x => x.T >= r.Lo && x.T <= r.Hi).ToList();
        }

        if (rows.Count == 0)
        {
            Console.Error.WriteLine("error: no matching rows in csv");
            return 1;
        }

        if (summary)
        {
            PrintSummary(rows, refLine);
        }
        else
        {
            PrintTable(rows, refLine);
        }

        return 0;
    }

    private static (int Lo, int Hi) ParseRange(string arg)
    {
        var parts = arg.Split('-');
        if (parts.Length != 2)
        {
            throw new ArgumentException($"--range expects 'START-END', got {arg}");
        }

        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private static RefLine? LoadRunwayReference(string arg, string? navdataDir)
    {
        var parts = arg.Split('/');
        if (parts.Length != 2)
        {
            Console.Error.WriteLine($"error: --runway expects 'ICAO/RWY' (e.g. SFO/28R), got {arg}");
            return null;
        }

        string airport = parts[0].ToUpperInvariant();
        string rwy = parts[1].ToUpperInvariant();

        navdataDir ??= FindDefaultNavDataDir();
        if (navdataDir is null)
        {
            Console.Error.WriteLine("error: could not locate NavData directory; pass --navdata DIR");
            return null;
        }

        try
        {
            TestVnasData.SetTestDataDir(navdataDir);
            TestVnasData.EnsureInitialized();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: failed to load NavData from {navdataDir}: {ex.Message}");
            return null;
        }

        var runway = NavigationDatabase.Instance.GetRunway(airport, rwy);
        if (runway is null)
        {
            Console.Error.WriteLine($"error: runway {airport}/{rwy} not found in NavData");
            return null;
        }

        Console.Error.WriteLine(
            $"# ref {airport}/{rwy}: threshold=({runway.ThresholdLatitude:F6},{runway.ThresholdLongitude:F6}) hdg={runway.TrueHeading.Degrees:F3}°"
        );
        return new RefLine(runway.ThresholdLatitude, runway.ThresholdLongitude, runway.TrueHeading);
    }

    private static string? FindDefaultNavDataDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "yaat.slnx")))
            {
                var testData = Path.Combine(dir.FullName, "tests", "Yaat.Sim.Tests", "TestData");
                return Directory.Exists(testData) ? testData : null;
            }
            dir = dir.Parent;
        }
        return null;
    }

    // ---------- CSV parsing ----------

    private sealed class TickRow
    {
        public required int T { get; init; }
        public required double Lat { get; init; }
        public required double Lon { get; init; }
        public required double Hdg { get; init; }
        public required double Gs { get; init; }
        public string? NavTarget { get; init; }
        public double? NavDistNm { get; init; }
        public double? NavBrgDeg { get; init; }
        public double? NavAngleDiffDeg { get; init; }
        public double? NavTargetSpdKts { get; init; }
        public double? NavBrakeLimitKts { get; init; }
        public double? NavArcLimitKts { get; init; }
        public bool NavOnArc { get; init; }
    }

    private readonly record struct RefLine(double Lat, double Lon, TrueHeading Heading);

    private static List<TickRow> LoadRows(string path)
    {
        var rows = new List<TickRow>();
        using var reader = new StreamReader(path);
        string? header = reader.ReadLine();
        if (header is null)
        {
            return rows;
        }

        string[] cols = header.Split(',');
        int ixT = Array.IndexOf(cols, "t");
        int ixLat = Array.IndexOf(cols, "lat");
        int ixLon = Array.IndexOf(cols, "lon");
        int ixHdg = Array.IndexOf(cols, "hdg");
        int ixGs = Array.IndexOf(cols, "gs");
        int ixNavTarget = Array.IndexOf(cols, "navTarget");
        int ixNavDist = Array.IndexOf(cols, "navDist");
        int ixNavBrg = Array.IndexOf(cols, "navBrg");
        int ixNavAngle = Array.IndexOf(cols, "navAngleDiff");
        int ixNavTgtSpd = Array.IndexOf(cols, "navTargetSpd");
        int ixNavBrake = Array.IndexOf(cols, "navBrakeLimit");
        int ixNavArcLim = Array.IndexOf(cols, "navArcLimit");
        int ixNavOnArc = Array.IndexOf(cols, "navOnArc");

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = line.Split(',');
            if (fields.Length < 5)
            {
                continue;
            }

            int t = int.Parse(fields[ixT], CultureInfo.InvariantCulture);
            double lat = ParseOrZero(fields, ixLat);
            double lon = ParseOrZero(fields, ixLon);
            double hdg = ParseOrZero(fields, ixHdg);
            double gs = ParseOrZero(fields, ixGs);

            string? navTarget = ixNavTarget >= 0 && ixNavTarget < fields.Length ? fields[ixNavTarget] : null;
            if (string.IsNullOrEmpty(navTarget))
            {
                navTarget = null;
            }

            rows.Add(
                new TickRow
                {
                    T = t,
                    Lat = lat,
                    Lon = lon,
                    Hdg = hdg,
                    Gs = gs,
                    NavTarget = navTarget,
                    NavDistNm = ParseOrNull(fields, ixNavDist),
                    NavBrgDeg = ParseOrNull(fields, ixNavBrg),
                    NavAngleDiffDeg = ParseOrNull(fields, ixNavAngle),
                    NavTargetSpdKts = ParseOrNull(fields, ixNavTgtSpd),
                    NavBrakeLimitKts = ParseOrNull(fields, ixNavBrake),
                    NavArcLimitKts = ParseOrNull(fields, ixNavArcLim),
                    NavOnArc = ixNavOnArc >= 0 && ixNavOnArc < fields.Length && fields[ixNavOnArc] == "1",
                }
            );
        }

        return rows;
    }

    private static double ParseOrZero(string[] fields, int index)
    {
        if (index < 0 || index >= fields.Length)
        {
            return 0;
        }
        return double.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static double? ParseOrNull(string[] fields, int index)
    {
        if (index < 0 || index >= fields.Length || string.IsNullOrEmpty(fields[index]))
        {
            return null;
        }
        return double.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    // ---------- Formatting helpers ----------

    /// <summary>
    /// Format a double in a fixed-width field, rendering sentinel infinities
    /// (e.g. <c>double.MaxValue</c>) and NaN as "-" so the column stays readable.
    /// </summary>
    private static string Fmt(double? val, int width, int precision = 1)
    {
        if (val is null || !double.IsFinite(val.Value) || Math.Abs(val.Value) > 1e10)
        {
            return "-".PadLeft(width);
        }
        return val.Value.ToString($"F{precision}", CultureInfo.InvariantCulture).PadLeft(width);
    }

    private static string FmtSigned(double? val, int width, int precision = 2)
    {
        if (val is null || !double.IsFinite(val.Value))
        {
            return "-".PadLeft(width);
        }
        string s = val.Value.ToString($"+0.{new string('0', precision)};-0.{new string('0', precision)}", CultureInfo.InvariantCulture);
        return s.PadLeft(width);
    }

    private static double CrossTrackFt(double lat, double lon, RefLine r) =>
        GeoMath.SignedCrossTrackDistanceNm(lat, lon, r.Lat, r.Lon, r.Heading) * FeetPerNm;

    private static double HeadingErrDeg(double hdg, TrueHeading refHdg) => refHdg.SignedAngleTo(new TrueHeading(hdg));

    // ---------- Table rendering ----------

    private static void PrintTable(List<TickRow> rows, RefLine? refLine)
    {
        string header = $"{"tick", 4} {"hdg", 7} {"gs", 6} {"nav", 5} {"distFt", 7} {"brg", 7} {"angd", 6} {"tgtSpd", 7} {"brake", 7} {"onArc", 5}";
        if (refLine is not null)
        {
            header += $" {"xteFt", 8} {"hdgErr", 7}";
        }
        Console.WriteLine(header);
        Console.WriteLine(new string('-', header.Length));

        string? prevNav = null;
        foreach (var row in rows)
        {
            string distFt = row.NavDistNm is { } d ? $"{d * FeetPerNm, 7:F1}" : "-".PadLeft(7);
            string nav = (row.NavTarget ?? "-").PadLeft(5);
            string onArc = (row.NavOnArc ? "arc" : "str").PadLeft(5);
            string line =
                $"{row.T, 4} {Fmt(row.Hdg, 7)} {Fmt(row.Gs, 6)} {nav} {distFt} {Fmt(row.NavBrgDeg, 7)} {Fmt(row.NavAngleDiffDeg, 6)} {Fmt(row.NavTargetSpdKts, 7)} {Fmt(row.NavBrakeLimitKts, 7)} {onArc}";

            if (refLine is { } r)
            {
                double xte = CrossTrackFt(row.Lat, row.Lon, r);
                double herr = HeadingErrDeg(row.Hdg, r.Heading);
                line += $" {xte, 8:F2} {FmtSigned(herr, 7)}";
            }

            string marker = prevNav is not null && row.NavTarget != prevNav ? "  <-- nav changed" : "";
            Console.WriteLine(line + marker);
            prevNav = row.NavTarget;
        }
    }

    // ---------- Summary rendering ----------

    private static void PrintSummary(List<TickRow> rows, RefLine? refLine)
    {
        string header = $"{"range", 11}  {"nav", 5}  {"kind", 4}  {"ticks", 5}  {"hdg s->e", 15}  {"gs s->e", 15}  {"dhdg", 6}  {"dist s->e", 16}";
        if (refLine is not null)
        {
            header += $"  {"xte s->e (ft)", 15}  {"hdgErr s->e", 14}";
        }
        Console.WriteLine(header);
        Console.WriteLine(new string('-', header.Length));

        string? curNav = null;
        TickRow? segStart = null;
        TickRow? prev = null;
        foreach (var row in rows)
        {
            if (row.NavTarget != curNav)
            {
                if (curNav is not null && segStart is not null && prev is not null)
                {
                    PrintSegmentSummary(curNav, segStart, prev, refLine);
                }
                curNav = row.NavTarget;
                segStart = row;
            }
            prev = row;
        }
        if (curNav is not null && segStart is not null && prev is not null)
        {
            PrintSegmentSummary(curNav, segStart, prev, refLine);
        }
    }

    private static void PrintSegmentSummary(string nav, TickRow first, TickRow last, RefLine? refLine)
    {
        int ticks = last.T - first.T + 1;
        double dh = (((last.Hdg - first.Hdg) + 540.0) % 360.0) - 180.0;
        string kind = first.NavOnArc ? "arc" : "str";
        double d0 = (first.NavDistNm ?? 0) * FeetPerNm;
        double d1 = (last.NavDistNm ?? 0) * FeetPerNm;

        string hdgStr = $"{first.Hdg, 6:F1}->{last.Hdg, 6:F1}";
        string gsStr = $"{first.Gs, 6:F1}->{last.Gs, 6:F1}";
        string distStr = $"{d0, 6:F1}->{d1, 6:F1}ft";

        string line = $"{first.T, 4}->{last.T, 4}  {nav, 5}  {kind, 4}  {ticks, 5}  {hdgStr, 15}  {gsStr, 15}  {dh, +6:F1}  {distStr, 16}";

        if (refLine is { } r)
        {
            double xte0 = CrossTrackFt(first.Lat, first.Lon, r);
            double xte1 = CrossTrackFt(last.Lat, last.Lon, r);
            double he0 = HeadingErrDeg(first.Hdg, r.Heading);
            double he1 = HeadingErrDeg(last.Hdg, r.Heading);
            string xteStr = $"{xte0, +6:F2}->{xte1, +6:F2}";
            string heStr = $"{he0, +5:F2}->{he1, +5:F2}";
            line += $"  {xteStr, 15}  {heStr, 14}";
        }

        Console.WriteLine(line);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: Yaat.TickInspector <csv-path> [flags]");
        Console.WriteLine();
        Console.WriteLine("Flags:");
        Console.WriteLine("  --summary            Per-segment summary (one row per nav target change)");
        Console.WriteLine("  --range START-END    Filter to a tick range (inclusive)");
        Console.WriteLine("  --runway ICAO/RWY    Reference runway for cross-track (xteFt) and heading-error (hdgErr)");
        Console.WriteLine("                       columns. Loads NavData via TestVnasData.");
        Console.WriteLine("  --navdata DIR        Override NavData directory (default: tests/Yaat.Sim.Tests/TestData)");
        Console.WriteLine();
        Console.WriteLine("Output conventions:");
        Console.WriteLine("  xteFt   = signed cross-track, positive = right of runway centerline");
        Console.WriteLine("  hdgErr  = signed heading deviation, positive = clockwise of runway heading");
        Console.WriteLine("  -       = sentinel infinity (e.g. navArcLimit when not on an arc)");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  Yaat.TickInspector .tmp/sfo-lineup-28r-e.csv --summary --runway SFO/28R");
        Console.WriteLine("  Yaat.TickInspector .tmp/sfo-lineup-28r-e.csv --range 38-60 --runway SFO/28R");
    }
}
