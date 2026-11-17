using System.Globalization;
using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope;

/// <summary>A compass direction on the screen, screen north up, as CRC's ERAM <c>LeaderDirection</c> names them.</summary>
public enum ScopeCompass
{
    N,
    NE,
    E,
    SE,
    S,
    SW,
    W,
    NW,
}

/// <summary>CRC's ERAM <c>RdbOffset</c>: how far round from the leader the range data block turns, in degrees.</summary>
public enum ScopeRdbOffset
{
    D45,
    D90,
    D135,
    D180,
    D225,
    D270,
    D315,
}

/// <summary>A track's range data block as the frame lays it out: its distance text, its group colour and its text box.</summary>
/// <param name="Text">The distance to the group's point (<see cref="ScopeCrrReadout.DistanceText"/>).</param>
/// <param name="Color">The group's colour at full brightness (<see cref="ScopeCrrReadout.ColorOf"/>).</param>
/// <param name="Rect">The text box in screen points; the text's baseline is its bottom.</param>
public sealed record ScopeCrrReadoutView(string Text, SKColor Color, SKRect Rect);

/// <summary>
/// ERAM's continuous range readout on the Scope (CRC <c>RdbRenderObject</c> and <c>RdbOrientation</c>): a track drawn with an
/// FDB-level ERAM block whose aircraft is a member of an existing CRR group shows its distance to the group's point, in the
/// group's colour, beside its target symbol on the side the ported orientation table gives for its leader.
/// </summary>
public static class ScopeCrrReadout
{
    /// <summary>The farthest distance shown as a number; anything past it reads <see cref="PastMaxText"/>.</summary>
    public const double MaxDistanceNm = 999.9;

    /// <summary>The text of a distance past <see cref="MaxDistanceNm"/>.</summary>
    public const string PastMaxText = ">999.9";

    /// <summary>The leader length the Scope's ERAM blocks count as (CRC <c>EramDisplaySettings.LeaderLength</c>'s default).</summary>
    public const int LeaderLength = 1;

    /// <summary>The RDB offset the Scope uses (CRC <c>EramDisplaySettings.RdbOffset</c>'s default).</summary>
    public const ScopeRdbOffset DefaultOffset = ScopeRdbOffset.D180;

    /// <summary>How far the box sits from the symbol on a straight side (CRC <c>RDB_FULL_OFFSET</c>).</summary>
    public const float FullOffsetPx = 7f;

    /// <summary>How far the box sits from the symbol on each axis on a diagonal side (CRC <c>RDB_DIAGONAL_OFFSET</c>).</summary>
    public const float DiagonalOffsetPx = 4f;

    /// <summary>CRC's ERAM CRR colours (<c>EramColorExtensions.GetRawColor</c>).</summary>
    public static readonly SKColor Green = new(0, 243, 0);

    /// <inheritdoc cref="Green"/>
    public static readonly SKColor Coral = new(243, 50, 207);

    /// <inheritdoc cref="Green"/>
    public static readonly SKColor Yellow = new(200, 200, 0);

    /// <inheritdoc cref="Green"/>
    public static readonly SKColor White = new(243, 243, 243);

    private static readonly Dictionary<(ScopeCompass, ScopeRdbOffset), ScopeCompass> Length1 = Table(
        "SE SE SE S SW W NW",
        "SW SW SW SW W NW SW",
        "SW SW SW W NW NW NW",
        "NE SW W NW N NE NE",
        "SW W NW N NE E SE",
        "NW NW N NE E SE S",
        "N N NE E SE S S",
        "N NE E SE S S S"
    );

    private static readonly Dictionary<(ScopeCompass, ScopeRdbOffset), ScopeCompass> Length23 = Table(
        "NE E SE S SW W NW",
        "E SE S SW W NW E",
        "S S SW W NW NW NW",
        "NE SW W NW N NE NE",
        "S W NW N NE E SE",
        "NW NW N NE E SE S",
        "N N NE NE E SE S",
        "N NE E SE S S W"
    );

    /// <summary>
    /// The range data block's text and colour for <paramref name="ac"/> drawn with <paramref name="templateId"/> (a resolved
    /// template, never Mixed) at <paramref name="level"/>: only an ERAM block at Full or Dwell, whose aircraft's group is in
    /// <paramref name="groups"/>; else null.
    /// </summary>
    public static (string Text, SKColor Color)? Content(
        AircraftModel ac,
        string templateId,
        ScopeBlockLevel level,
        IReadOnlyList<EramCrrGroupHubDto> groups
    )
    {
        if ((templateId != ScopeTemplates.EramId) || (level is not (ScopeBlockLevel.Full or ScopeBlockLevel.Dwell)))
        {
            return null;
        }

        if ((ac.EramCrrGroup is not { } label) || (groups.FirstOrDefault(g => g.Label == label) is not { } group))
        {
            return null;
        }

        double nm = GeoMath.DistanceNm(group.Latitude, group.Longitude, ac.Position.Lat, ac.Position.Lon);
        return (DistanceText(nm), ColorOf(group.Color));
    }

    /// <summary>
    /// Lays <paramref name="content"/> out beside the symbol at <paramref name="symbol"/>, for a block placed
    /// <paramref name="blockOffset"/> from it, measured with <paramref name="metrics"/>.
    /// </summary>
    public static ScopeCrrReadoutView Place((string Text, SKColor Color) content, SKPoint symbol, SKPoint blockOffset, ScopeTextMetrics metrics)
    {
        ScopeCompass side = Orientation(LeaderOf(blockOffset), LeaderLength, DefaultOffset);
        SKRect rect = BoxAt(symbol, side, metrics.Measure(content.Text), metrics.Size);
        return new ScopeCrrReadoutView(content.Text, content.Color, rect);
    }

    /// <summary>
    /// <paramref name="distanceNm"/> to one decimal (invariant culture), or <see cref="PastMaxText"/> past
    /// <see cref="MaxDistanceNm"/> (CRC <c>GetDistanceString</c>).
    /// </summary>
    public static string DistanceText(double distanceNm) =>
        distanceNm > MaxDistanceNm ? PastMaxText : distanceNm.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>The colour of a group whose <c>EramCrrColor</c> is named <paramref name="colorName"/>; any other name is white, as CRC's.</summary>
    public static SKColor ColorOf(string colorName) =>
        colorName.ToUpperInvariant() switch
        {
            "GREEN" => Green,
            "CORAL" => Coral,
            "YELLOW" => Yellow,
            _ => White,
        };

    /// <summary>
    /// The direction a block placed <paramref name="blockOffset"/> from its symbol leads in: the nearest of the eight compass
    /// points (screen Y grows down); a zero offset leads NE, CRC's default.
    /// </summary>
    public static ScopeCompass LeaderOf(SKPoint blockOffset)
    {
        if ((blockOffset.X == 0f) && (blockOffset.Y == 0f))
        {
            return ScopeCompass.NE;
        }

        return LeaderOfBearing(Math.Atan2(blockOffset.X, -blockOffset.Y) * 180.0 / Math.PI);
    }

    /// <summary>
    /// The nearest of the eight compass points to <paramref name="bearingDeg"/> (degrees clockwise from screen north, any
    /// turn); an exact boundary between two points resolves clockwise.
    /// </summary>
    public static ScopeCompass LeaderOfBearing(double bearingDeg)
    {
        double normalized = ((bearingDeg % 360.0) + 360.0) % 360.0;
        int octant = (int)Math.Round(normalized / 45.0, MidpointRounding.AwayFromZero) % 8;
        return (ScopeCompass)octant;
    }

    /// <summary>
    /// The side of the symbol the range data block sits on, for a leader in <paramref name="leader"/> of
    /// <paramref name="leaderLength"/> and the offset <paramref name="offset"/> (CRC <c>RdbOrientation.Get</c>): length 0 puts it
    /// E of a leader to the SW, W or NW and W of any other; length 1 and lengths 2-3 read their tables.
    /// </summary>
    public static ScopeCompass Orientation(ScopeCompass leader, int leaderLength, ScopeRdbOffset offset) =>
        leaderLength switch
        {
            0 => leader is ScopeCompass.SW or ScopeCompass.W or ScopeCompass.NW ? ScopeCompass.E : ScopeCompass.W,
            1 => Length1[(leader, offset)],
            _ => Length23[(leader, offset)],
        };

    /// <summary>
    /// The text box of <paramref name="width"/> by <paramref name="height"/> on <paramref name="side"/> of
    /// <paramref name="symbol"/> (CRC <c>RdbRenderObject.Render</c>): <see cref="FullOffsetPx"/> out on a straight side,
    /// <see cref="DiagonalOffsetPx"/> on each axis on a diagonal one.
    /// </summary>
    public static SKRect BoxAt(SKPoint symbol, ScopeCompass side, float width, float height)
    {
        float dy = side switch
        {
            ScopeCompass.N => -(FullOffsetPx + height),
            ScopeCompass.NE or ScopeCompass.NW => -(DiagonalOffsetPx + height),
            ScopeCompass.S => FullOffsetPx,
            ScopeCompass.SE or ScopeCompass.SW => DiagonalOffsetPx,
            _ => 0f,
        };
        float dx = side switch
        {
            ScopeCompass.NE or ScopeCompass.SE => DiagonalOffsetPx,
            ScopeCompass.E => FullOffsetPx,
            ScopeCompass.SW or ScopeCompass.NW => -(DiagonalOffsetPx + width),
            ScopeCompass.W => -(FullOffsetPx + width),
            _ => 0f,
        };
        float left = symbol.X + dx;
        float top = symbol.Y + dy;
        return new SKRect(left, top, left + width, top + height);
    }

    /// <summary>
    /// Builds one of CRC's orientation tables from eight rows, one per leader direction N to NW, each the seven sides for the
    /// offsets D45 to D315.
    /// </summary>
    private static Dictionary<(ScopeCompass, ScopeRdbOffset), ScopeCompass> Table(params string[] rows)
    {
        var table = new Dictionary<(ScopeCompass, ScopeRdbOffset), ScopeCompass>();
        for (int leader = 0; leader < rows.Length; leader++)
        {
            string[] sides = rows[leader].Split(' ');
            for (int offset = 0; offset < sides.Length; offset++)
            {
                table[((ScopeCompass)leader, (ScopeRdbOffset)offset)] = Enum.Parse<ScopeCompass>(sides[offset]);
            }
        }

        return table;
    }
}
