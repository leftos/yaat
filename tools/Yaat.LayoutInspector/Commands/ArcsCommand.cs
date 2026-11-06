using System.Globalization;
using System.Text.Json;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;

namespace Yaat.LayoutInspector.Commands;

/// <summary>
/// One fillet arc in the <c>--arcs</c> report. <see cref="EffectiveRadiusFt"/> is
/// <see cref="FollowRoutePlanner.EffectiveArcRadiusFt"/>, null for an arc that does not turn; <see cref="MinBezierRadiusFt"/> is
/// <see cref="GroundArc.MinRadiusOfCurvatureFt"/>; <see cref="HeadingChangeDeg"/> is the arc's total heading change, the one the
/// effective radius spreads its length over. <see cref="FromTaxiway"/> and <see cref="ToTaxiway"/> name the taxiway the arc
/// meets at its first and second node.
/// </summary>
public sealed record ArcReportRow(
    double? EffectiveRadiusFt,
    double MinBezierRadiusFt,
    double HeadingChangeDeg,
    double LengthFt,
    int FromNodeId,
    int ToNodeId,
    string FromTaxiway,
    string ToTaxiway
);

/// <summary>
/// Implements <c>--arcs</c>: lists every fillet arc (<see cref="GroundArc"/>) of the loaded layout, tightest effective radius
/// first, as a text table or, with <c>--json</c>, a JSON array of <see cref="ArcReportRow"/>. <c>--arcs-max-radius &lt;ft&gt;</c>
/// keeps only the arcs whose effective radius is at or below it. Looping it over <c>TestData/*.geojson</c> gives the
/// per-airport corner report.
/// </summary>
public sealed class ArcsCommand : ICommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public int Execute(LayoutAnalyzer analyzer, CliOptions options)
    {
        List<ArcReportRow> rows = BuildRows(analyzer.Layout, options.ArcsMaxRadiusFt);
        if (options.JsonOutput)
        {
            Console.Out.WriteLine(ToJson(rows));
            return 0;
        }

        WriteText(Console.Out, analyzer, rows, options.ArcsMaxRadiusFt);
        return 0;
    }

    /// <summary>
    /// The report rows for every arc of <paramref name="layout"/>, ordered by effective radius (an arc that does not turn last),
    /// then tightest Bezier radius, then node ids; only those at or below <paramref name="maxRadiusFt"/> when it is set.
    /// </summary>
    public static List<ArcReportRow> BuildRows(AirportGroundLayout layout, double? maxRadiusFt) =>
        [
            .. layout
                .Arcs.Select(ToRow)
                .Where(row => (maxRadiusFt is not { } max) || (row.EffectiveRadiusFt <= max))
                .OrderBy(row => row.EffectiveRadiusFt ?? double.PositiveInfinity)
                .ThenBy(row => row.MinBezierRadiusFt)
                .ThenBy(row => row.FromNodeId)
                .ThenBy(row => row.ToNodeId),
        ];

    private static ArcReportRow ToRow(GroundArc arc)
    {
        double effective = FollowRoutePlanner.EffectiveArcRadiusFt(arc);
        double lengthFt = arc.DistanceNm * GeoMath.FeetPerNm;
        bool turns = !double.IsPositiveInfinity(effective);
        double headingChangeDeg = turns ? lengthFt / effective * 180.0 / Math.PI : 0.0;
        (string fromTaxiway, string toTaxiway) = EndTaxiways(arc);
        return new ArcReportRow(
            turns ? effective : null,
            arc.MinRadiusOfCurvatureFt,
            headingChangeDeg,
            lengthFt,
            arc.Nodes[0].Id,
            arc.Nodes[1].Id,
            fromTaxiway,
            toTaxiway
        );
    }

    /// <summary>
    /// The taxiway at each end of <paramref name="arc"/>: the straight edge from the corner it cuts to that end where it has one,
    /// else its first and last <see cref="GroundArc.TaxiwayNames"/>.
    /// </summary>
    private static (string From, string To) EndTaxiways(GroundArc arc) =>
        arc.Corner() is { } corner ? (corner.ToFirst.TaxiwayName, corner.ToSecond.TaxiwayName) : (arc.TaxiwayNames[0], arc.TaxiwayNames[^1]);

    private static void WriteText(TextWriter w, LayoutAnalyzer analyzer, List<ArcReportRow> rows, double? maxRadiusFt)
    {
        string filter = maxRadiusFt is { } max ? string.Create(CultureInfo.InvariantCulture, $", effective radius <= {max:F0} ft") : "";
        w.WriteLine($"Arcs at {analyzer.AirportId}: {rows.Count} of {analyzer.Layout.Arcs.Count}{filter}, tightest effective radius first");
        w.WriteLine($"{"effR_ft", 8} {"minR_ft", 8} {"turn_deg", 8} {"len_ft", 7} {"node1", 6} {"node2", 6}  twy1 - twy2");
        foreach (ArcReportRow r in rows)
        {
            string radii = $"{FormatRadiusFt(r.EffectiveRadiusFt ?? double.PositiveInfinity), 8} {FormatRadiusFt(r.MinBezierRadiusFt), 8}";
            string shape = string.Create(CultureInfo.InvariantCulture, $"{r.HeadingChangeDeg, 8:F1} {r.LengthFt, 7:F0}");
            w.WriteLine($"{radii} {shape} {r.FromNodeId, 6} {r.ToNodeId, 6}  {r.FromTaxiway} - {r.ToTaxiway}");
        }
    }

    /// <summary>The <c>--json</c> output: the rows as an indented camelCase JSON array.</summary>
    public static string ToJson(List<ArcReportRow> rows) => JsonSerializer.Serialize(rows, JsonOptions);

    /// <summary>
    /// A radius column, 0 decimals; <c>inf</c> for an arc that does not turn, and for a straight fillet's tightest radius,
    /// which <see cref="GroundArc.MinRadiusOfCurvatureFt"/> holds as <see cref="double.MaxValue"/>.
    /// </summary>
    public static string FormatRadiusFt(double radiusFt) =>
        radiusFt >= double.MaxValue ? "inf" : radiusFt.ToString("F0", CultureInfo.InvariantCulture);
}
