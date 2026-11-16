using System.Globalization;
using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Sim;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.Views.Scope;

/// <summary>The kind the Rings tool places on a click: none (disarmed), a J-ring, a cone, or a 5 NM or 3 NM DRI.</summary>
public enum ScopeRingTool
{
    /// <summary>The tool is not armed: a click selects as usual.</summary>
    None,

    /// <summary>A J-ring: a circle centred on the target.</summary>
    JRing,

    /// <summary>A cone along the target's ground track.</summary>
    Cone,

    /// <summary>The standard 5 NM DRI halo.</summary>
    Dri5,

    /// <summary>The reduced-separation 3 NM DRI halo, drawn with four gaps.</summary>
    Dri3,
}

/// <summary>The kind words of <see cref="ScopeRingTool"/>, as <c>.scope rings</c> takes them.</summary>
public static class ScopeRingTools
{
    /// <summary>The kind words, in TOOLS tray order.</summary>
    private static readonly (ScopeRingTool Tool, string Name)[] Names =
    [
        (ScopeRingTool.JRing, "jring"),
        (ScopeRingTool.Cone, "cone"),
        (ScopeRingTool.Dri5, "dri5"),
        (ScopeRingTool.Dri3, "dri3"),
    ];

    /// <summary>The lowercase kind word of <paramref name="tool"/>; empty for <see cref="ScopeRingTool.None"/>.</summary>
    public static string NameOf(ScopeRingTool tool) => Names.FirstOrDefault(n => n.Tool == tool).Name ?? "";

    /// <summary>Parses a kind word, case-insensitive.</summary>
    /// <returns>False when <paramref name="word"/> names no ring kind.</returns>
    public static bool TryParse(string word, out ScopeRingTool tool)
    {
        tool = Names.FirstOrDefault(n => string.Equals(n.Name, word, StringComparison.OrdinalIgnoreCase)).Tool;
        return tool != ScopeRingTool.None;
    }
}

/// <summary>The DRI halo a track carries: the standard 5 NM circle or the reduced 3 NM one with four gaps.</summary>
public enum ScopeDriKind
{
    /// <summary>The standard 5 NM circle.</summary>
    Standard,

    /// <summary>The reduced-separation 3 NM halo: four arcs with gaps on north, east, south and west.</summary>
    Reduced,
}

/// <summary>
/// The rings the instructor placed on one track on this Scope: at most one J-ring, one cone and one DRI. Display state only,
/// never saved and never sent to the server.
/// </summary>
/// <param name="JRingNm">The J-ring's radius in NM, or null for none.</param>
/// <param name="ConeNm">The cone's length in NM, or null for none.</param>
/// <param name="Dri">The DRI halo, or null for none.</param>
public sealed record ScopeTrackRings(double? JRingNm, double? ConeNm, ScopeDriKind? Dri)
{
    /// <summary>No rings.</summary>
    public static ScopeTrackRings None { get; } = new(null, null, null);

    /// <summary>Whether the track carries no ring at all.</summary>
    public bool IsEmpty => (JRingNm is null) && (ConeNm is null) && (Dri is null);
}

/// <summary>A drawn circle in screen points, closed (its last point is its first), with its size label.</summary>
/// <param name="Points">The circle's screen points every 5 degrees, from north clockwise, 0 and 360 both included.</param>
/// <param name="LabelAt">The centre of the size label.</param>
/// <param name="Label">The size label text.</param>
public sealed record ScopeRingCircle(IReadOnlyList<SKPoint> Points, SKPoint LabelAt, string Label);

/// <summary>
/// A drawn cone in screen points: the apex on the target, the far end two straight segments from the left point through the
/// axis tip to the right point, and its length label at mid-axis.
/// </summary>
/// <param name="Apex">The apex, on the target.</param>
/// <param name="Left">The far point at the axis minus the half-angle.</param>
/// <param name="Tip">The far point on the axis.</param>
/// <param name="Right">The far point at the axis plus the half-angle.</param>
/// <param name="LabelAt">The centre of the length label, halfway along the axis.</param>
/// <param name="Label">The length label text.</param>
public sealed record ScopeWedge(SKPoint Apex, SKPoint Left, SKPoint Tip, SKPoint Right, SKPoint LabelAt, string Label);

/// <summary>Everything the ring layer draws for one track, laid out on the UI thread.</summary>
/// <param name="JRing">The J-ring, or null.</param>
/// <param name="Cone">The manual cone, or null (also null while an ATPA cone is drawn on the track).</param>
/// <param name="DriArcs">The DRI halo's polylines: one closed circle for 5 NM, four arcs for 3 NM; empty for none.</param>
/// <param name="AtpaCone">The ATPA cone toward the lead, or null.</param>
/// <param name="AtpaState">The ATPA cone's state, which picks its colour.</param>
public sealed record ScopeRingShapes(
    ScopeRingCircle? JRing,
    ScopeWedge? Cone,
    IReadOnlyList<IReadOnlyList<SKPoint>> DriArcs,
    ScopeWedge? AtpaCone,
    AtpaConeState AtpaState
)
{
    /// <summary>Nothing to draw.</summary>
    public static ScopeRingShapes None { get; } = new(null, null, [], null, AtpaConeState.Monitor);

    /// <summary>Whether there is nothing to draw.</summary>
    public bool IsEmpty => (JRing is null) && (Cone is null) && (DriArcs.Count == 0) && (AtpaCone is null);
}

/// <summary>
/// Pure: the Rings tool's size grid and the screen geometry of J-rings, cones, DRI halos and ATPA cones, all anchored to the
/// map (sizes in NM, projected through the viewport), as CRC draws them.
/// </summary>
public static class ScopeRingGeometry
{
    /// <summary>The cone half-angle, in degrees, of the manual and the ATPA cones (CRC).</summary>
    public const double ConeHalfAngleDeg = 2.0;

    /// <summary>The size SIZE starts at, in NM.</summary>
    public const double DefaultSizeNm = 3.0;

    /// <summary>The standard DRI's radius, in NM.</summary>
    public const double StandardDriNm = 5.0;

    /// <summary>The reduced DRI's radius, in NM.</summary>
    public const double ReducedDriNm = 3.0;

    /// <summary>The highest altitude, in feet, a track may be at to take a reduced (3 NM) DRI: FL230.</summary>
    public const int ReducedDriCeilingFt = 23000;

    /// <summary>The first bearing of the first reduced-DRI arc; each arc spans 75 degrees, the four 90 degrees apart.</summary>
    public const double ReducedDriArcStartDeg = 7.5;

    /// <summary>The last bearing of the first reduced-DRI arc.</summary>
    public const double ReducedDriArcEndDeg = 82.5;

    private const int CircleStepDeg = 5;

    private const double ArcStepDeg = 2.5;

    private const double Epsilon = 1e-6;

    /// <summary>The J-ring and cone sizes, ascending: 1.0 to 9.9 NM in tenths, then 10 to 30 NM whole.</summary>
    public static IReadOnlyList<double> SizeGrid { get; } =
    [.. Enumerable.Range(10, 90).Select(t => t / 10.0), .. Enumerable.Range(10, 21).Select(n => (double)n)];

    /// <summary>Whether <paramref name="nm"/> is on <see cref="SizeGrid"/>.</summary>
    public static bool IsRingSize(double nm) => SizeGrid.Any(s => Math.Abs(s - nm) < Epsilon);

    /// <summary>
    /// The size <paramref name="steps"/> wheel steps from <paramref name="current"/> along <see cref="SizeGrid"/>: the first step
    /// goes to the next grid value in the wheel's direction, and a step past either end stops there.
    /// </summary>
    public static double StepSize(double current, int steps)
    {
        if (steps == 0)
        {
            return current;
        }

        int first = (steps > 0) ? SizeGrid.Count(s => s <= (current + Epsilon)) : (SizeGrid.Count(s => s < (current - Epsilon)) - 1);
        if ((first < 0) || (first >= SizeGrid.Count))
        {
            return current;
        }

        int index = first + ((steps > 0) ? (steps - 1) : (steps + 1));
        return SizeGrid[Math.Clamp(index, 0, SizeGrid.Count - 1)];
    }

    /// <summary>A size as the grid writes it: tenths below 10 NM (<c>3.0</c>), whole from 10 NM (<c>12</c>).</summary>
    public static string FormatSize(double nm) => nm.ToString((nm < 10) ? "0.0" : "0", CultureInfo.InvariantCulture);

    /// <summary>A size as a drawn label shows it: whole when whole, else to tenths (<c>3</c>, <c>2.5</c>).</summary>
    public static string LabelText(double nm) => nm.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Whether a track at <paramref name="altitudeFt"/> may take a reduced (3 NM) DRI: at or below FL230.</summary>
    public static bool AllowsReducedDri(double altitudeFt) => altitudeFt <= ReducedDriCeilingFt;

    /// <summary>The screen point <paramref name="distanceNm"/> from <paramref name="from"/> on true bearing <paramref name="bearingDeg"/>.</summary>
    public static SKPoint Project(LatLon from, double bearingDeg, double distanceNm, MapViewport vp)
    {
        LatLon p = GeoMath.ProjectPoint(from, new TrueHeading(bearingDeg), distanceNm);
        (float x, float y) = vp.LatLonToScreen(p.Lat, p.Lon);
        return new SKPoint(x, y);
    }

    /// <summary>
    /// The J-ring of <paramref name="radiusNm"/> around <paramref name="ac"/>, its label on the ring on the side opposite the
    /// leader (<paramref name="leaderOffset"/>, the block's offset from the symbol; straight down when it is zero).
    /// </summary>
    public static ScopeRingCircle JRing(AircraftModel ac, double radiusNm, SKPoint leaderOffset, MapViewport vp)
    {
        IReadOnlyList<SKPoint> points = Circle(ac.Position, radiusNm, vp);
        (float sx, float sy) = vp.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);
        return new ScopeRingCircle(points, OppositeOnRing(new SKPoint(sx, sy), points, leaderOffset), LabelText(radiusNm));
    }

    /// <summary>The closed circle of <paramref name="radiusNm"/> around <paramref name="center"/>, a point every 5 degrees.</summary>
    public static IReadOnlyList<SKPoint> Circle(LatLon center, double radiusNm, MapViewport vp) =>
        [.. Enumerable.Range(0, (360 / CircleStepDeg) + 1).Select(i => Project(center, i * CircleStepDeg, radiusNm, vp))];

    /// <summary>
    /// The point of <paramref name="ring"/> farthest from <paramref name="symbol"/> in the direction opposite
    /// <paramref name="leaderOffset"/>; straight down the screen when the offset is zero.
    /// </summary>
    public static SKPoint OppositeOnRing(SKPoint symbol, IReadOnlyList<SKPoint> ring, SKPoint leaderOffset)
    {
        SKPoint away = (leaderOffset.Length < Epsilon) ? new SKPoint(0, 1) : new SKPoint(-leaderOffset.X, -leaderOffset.Y);
        return ring.MaxBy(p => ((p.X - symbol.X) * away.X) + ((p.Y - symbol.Y) * away.Y));
    }

    /// <summary>The manual cone of <paramref name="lengthNm"/> from <paramref name="ac"/>, along its ground track (never its heading).</summary>
    public static ScopeWedge Cone(AircraftModel ac, double lengthNm, MapViewport vp) => Wedge(ac.Position, ac.TrueTrack, lengthNm, vp);

    /// <summary>
    /// The ATPA cone of <paramref name="trailing"/>: from it along the bearing to <paramref name="lead"/>, as long as its allowed
    /// separation; null when that is not positive.
    /// </summary>
    public static ScopeWedge? AtpaCone(AircraftModel trailing, AircraftModel lead, MapViewport vp)
    {
        double lengthNm = trailing.AtpaAllowedSeparationNm;
        return (lengthNm <= 0) ? null : Wedge(trailing.Position, GeoMath.BearingTo(trailing.Position, lead.Position), lengthNm, vp);
    }

    /// <summary>A cone from <paramref name="apex"/> along true <paramref name="axisDeg"/>, <paramref name="lengthNm"/> long, ±2 degrees.</summary>
    public static ScopeWedge Wedge(LatLon apex, double axisDeg, double lengthNm, MapViewport vp)
    {
        (float ax, float ay) = vp.LatLonToScreen(apex.Lat, apex.Lon);
        return new ScopeWedge(
            new SKPoint(ax, ay),
            Project(apex, axisDeg - ConeHalfAngleDeg, lengthNm, vp),
            Project(apex, axisDeg, lengthNm, vp),
            Project(apex, axisDeg + ConeHalfAngleDeg, lengthNm, vp),
            Project(apex, axisDeg, lengthNm / 2.0, vp),
            LabelText(lengthNm)
        );
    }

    /// <summary>
    /// The DRI halo of <paramref name="kind"/> around <paramref name="center"/>: the closed 5 NM circle, or the 3 NM halo as four
    /// arcs from 7.5 to 82.5 degrees turned 0, 90, 180 and 270 (gaps centred on north, east, south and west). True bearings, so
    /// the halo turns with the map.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<SKPoint>> DriArcs(LatLon center, ScopeDriKind kind, MapViewport vp)
    {
        if (kind == ScopeDriKind.Standard)
        {
            return [Circle(center, StandardDriNm, vp)];
        }

        int points = (int)Math.Round((ReducedDriArcEndDeg - ReducedDriArcStartDeg) / ArcStepDeg) + 1;
        return
        [
            .. Enumerable
                .Range(0, 4)
                .Select(q =>
                    (IReadOnlyList<SKPoint>)
                        [
                            .. Enumerable
                                .Range(0, points)
                                .Select(i => Project(center, ReducedDriArcStartDeg + (q * 90) + (i * ArcStepDeg), ReducedDriNm, vp)),
                        ]
                ),
        ];
    }

    /// <summary>
    /// What the ring layer draws for <paramref name="ac"/>: its placed <paramref name="rings"/>, and the ATPA cone toward
    /// <paramref name="atpaLead"/> (null when ATPA is off or the lead is not drawn). An ATPA cone hides the manual cone; the
    /// J-ring and the DRI draw regardless.
    /// </summary>
    public static ScopeRingShapes Shapes(AircraftModel ac, ScopeTrackRings rings, AircraftModel? atpaLead, SKPoint leaderOffset, MapViewport vp)
    {
        ScopeWedge? atpa = (atpaLead is null) ? null : AtpaCone(ac, atpaLead, vp);
        if (rings.IsEmpty && (atpa is null))
        {
            return ScopeRingShapes.None;
        }

        return new ScopeRingShapes(
            (rings.JRingNm is { } radius) ? JRing(ac, radius, leaderOffset, vp) : null,
            ((atpa is null) && (rings.ConeNm is { } length)) ? Cone(ac, length, vp) : null,
            (rings.Dri is { } dri) ? DriArcs(ac.Position, dri, vp) : [],
            atpa,
            ac.AtpaConeState
        );
    }
}
