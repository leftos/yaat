using System.Globalization;
using Yaat.LayoutInspector.Tick;
using Yaat.Sim;

namespace Yaat.LayoutInspector;

/// <summary>
/// Fixed-width text output for <see cref="TickDataRow"/>s — the former
/// <c>Yaat.TickInspector</c> table and per-segment summary renderers. Not an
/// <see cref="IFormatter"/> implementation because the call signature takes a
/// whole list of rows plus reference state, not one query result at a time.
/// </summary>
public static class TickTableFormatter
{
    private const double FeetPerNm = GeoMath.FeetPerNm;

    /// <summary>
    /// Writes a per-tick compact table. Columns: tick, hdg, gs, nav, distFt,
    /// brg, angd, tgtSpd, brake, onArc. When <paramref name="refLine"/> is
    /// supplied, adds xteFt/hdgErr columns. When <paramref name="exitRefs"/>
    /// are supplied, adds one along/dist pair per named hold-short.
    /// </summary>
    public static void PrintTable(List<TickDataRow> rows, RunwayReference? refLine, List<ExitRef> exitRefs)
    {
        string header = $"{"tick", 4} {"hdg", 7} {"gs", 6} {"nav", 5} {"distFt", 7} {"brg", 7} {"angd", 6} {"tgtSpd", 7} {"brake", 7} {"onArc", 5}";
        if (refLine is not null)
        {
            header += $" {"xteFt", 8} {"hdgErr", 7}";
        }

        foreach (var er in exitRefs)
        {
            // along_<twy> = signed along-track in ft, positive = hold-short ahead of aircraft
            // dist_<twy>  = straight-line distance in ft
            header += $" {"along_" + er.Taxiway, 11} {"dist_" + er.Taxiway, 11}";
        }

        Console.WriteLine(header);
        Console.WriteLine(new string('-', header.Length));

        int? prevNav = null;
        foreach (var row in rows)
        {
            string distFt = row.NavDist is { } d ? $"{d * FeetPerNm, 7:F1}" : "-".PadLeft(7);
            string nav = (row.NavTarget?.ToString(CultureInfo.InvariantCulture) ?? "-").PadLeft(5);
            string onArc = ((row.NavOnArc ?? false) ? "arc" : "str").PadLeft(5);
            string line =
                $"{row.Time, 4} {Fmt(row.Hdg, 7)} {Fmt(row.Gs, 6)} {nav} {distFt} {Fmt(row.NavBrg, 7)} {Fmt(row.NavAngleDiff, 6)} {Fmt(row.NavTargetSpd, 7)} {Fmt(row.NavBrakeLimit, 7)} {onArc}";

            if (refLine is { } r)
            {
                double xte = r.CrossTrackFt(row.Lat, row.Lon);
                double herr = r.HeadingErrorDeg(row.Hdg);
                line += $" {xte, 8:F2} {FmtSigned(herr, 7)}";
            }

            foreach (var er in exitRefs)
            {
                if (refLine is not { } r2)
                {
                    continue;
                }

                (double alongFt, double distFtExit) = HoldShortResolver.NearestDistances(row.Lat, row.Lon, er, r2);
                line += $" {alongFt, 11:F0} {distFtExit, 11:F0}";
            }

            string marker = prevNav is not null && row.NavTarget != prevNav ? "  <-- nav changed" : "";
            Console.WriteLine(line + marker);
            prevNav = row.NavTarget;
        }
    }

    /// <summary>
    /// Writes a per-segment summary — one row per run of identical
    /// <see cref="TickDataRow.NavTarget"/> values. Each row shows tick range,
    /// heading/gs/distance at segment start and end, and (when a reference
    /// runway is set) cross-track and heading error at both ends. Rows with a
    /// null nav target (no active nav segment) are skipped, matching the
    /// original TickInspector behavior.
    /// </summary>
    public static void PrintSummary(List<TickDataRow> rows, RunwayReference? refLine)
    {
        string header = $"{"range", 11}  {"nav", 5}  {"kind", 4}  {"ticks", 5}  {"hdg s->e", 15}  {"gs s->e", 15}  {"dhdg", 6}  {"dist s->e", 16}";
        if (refLine is not null)
        {
            header += $"  {"xte s->e (ft)", 15}  {"hdgErr s->e", 14}";
        }

        Console.WriteLine(header);
        Console.WriteLine(new string('-', header.Length));

        int? curNav = null;
        TickDataRow? segStart = null;
        TickDataRow? prev = null;
        foreach (var row in rows)
        {
            if (row.NavTarget != curNav)
            {
                if (curNav is not null && segStart is not null && prev is not null)
                {
                    PrintSegmentSummary(curNav.Value, segStart, prev, refLine);
                }

                curNav = row.NavTarget;
                segStart = row;
            }

            prev = row;
        }

        if (curNav is not null && segStart is not null && prev is not null)
        {
            PrintSegmentSummary(curNav.Value, segStart, prev, refLine);
        }
    }

    private static void PrintSegmentSummary(int nav, TickDataRow first, TickDataRow last, RunwayReference? refLine)
    {
        int ticks = last.Time - first.Time + 1;
        double dh = (((last.Hdg - first.Hdg) + 540.0) % 360.0) - 180.0;
        string kind = (first.NavOnArc ?? false) ? "arc" : "str";
        double d0 = (first.NavDist ?? 0) * FeetPerNm;
        double d1 = (last.NavDist ?? 0) * FeetPerNm;

        string navStr = nav.ToString(CultureInfo.InvariantCulture).PadLeft(5);
        string hdgStr = $"{first.Hdg, 6:F1}->{last.Hdg, 6:F1}";
        string gsStr = $"{first.Gs, 6:F1}->{last.Gs, 6:F1}";
        string distStr = $"{d0, 6:F1}->{d1, 6:F1}ft";

        string line = $"{first.Time, 4}->{last.Time, 4}  {navStr}  {kind, 4}  {ticks, 5}  {hdgStr, 15}  {gsStr, 15}  {dh, +6:F1}  {distStr, 16}";

        if (refLine is { } r)
        {
            double xte0 = r.CrossTrackFt(first.Lat, first.Lon);
            double xte1 = r.CrossTrackFt(last.Lat, last.Lon);
            double he0 = r.HeadingErrorDeg(first.Hdg);
            double he1 = r.HeadingErrorDeg(last.Hdg);
            string xteStr = $"{xte0, +6:F2}->{xte1, +6:F2}";
            string heStr = $"{he0, +5:F2}->{he1, +5:F2}";
            line += $"  {xteStr, 15}  {heStr, 14}";
        }

        Console.WriteLine(line);
    }

    /// <summary>
    /// Formats a nullable double in a fixed-width field. Sentinel infinities
    /// (<c>double.MaxValue</c>) and NaN render as "-" so the column stays
    /// readable when a nav field is not applicable.
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
}
