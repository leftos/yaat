using System.Runtime.CompilerServices;
using SkiaSharp;
using Yaat.Client.Models;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// An ERAM target glyph: the radar return's shape by its state (CRC <c>EramTargetSymbolType</c>), for the states the Scope has a
/// source for. A weak primary, an MCI target and a reduced-separation target have none, so they are never drawn.
/// </summary>
public enum EramTargetGlyph
{
    /// <summary>A beacon return correlated with a flight plan: a backslash.</summary>
    CorrelatedBeacon,

    /// <summary>A beacon return with no correlated flight plan: a slash.</summary>
    UncorrelatedBeacon,

    /// <summary>A primary-only return correlated with a flight plan: an X.</summary>
    CorrelatedPrimary,

    /// <summary>A strong primary-only return with no correlated flight plan: a plus.</summary>
    UncorrelatedPrimary,

    /// <summary>A beacon return squawking ident (SPI): three bars.</summary>
    Ident,

    /// <summary>A beacon return on code 1200: a V.</summary>
    Vfr,
}

/// <summary>An ERAM track-position symbol, drawn at the target under a full block (CRC <c>TrackSymbol</c>).</summary>
public enum EramTrackSymbol
{
    /// <summary>Correlated with a destination: a hollow diamond.</summary>
    Flat,

    /// <summary>Any other full block: a D, its bar on the right.</summary>
    Free,

    /// <summary>Coasting (no return): a hash.</summary>
    Coast,

    /// <summary>Frozen: a bow-tie.</summary>
    Frozen,
}

/// <summary>
/// What an ERAM-template track draws at its position: its target glyph, null while it coasts (no return), and its track-position
/// symbol, null below a full block.
/// </summary>
/// <param name="Target">The target glyph, or null for none.</param>
/// <param name="Track">The track-position symbol, or null for none.</param>
public readonly record struct EramSymbolState(EramTargetGlyph? Target, EramTrackSymbol? Track)
{
    /// <summary>The VFR beacon code.</summary>
    public const uint VfrCode = 1200;

    /// <summary>What <paramref name="ac"/> draws with its block at <paramref name="level"/>.</summary>
    public static EramSymbolState Of(AircraftModel ac, ScopeBlockLevel level) => new(TargetOf(ac), TrackOf(ac, level));

    /// <summary>Whether <paramref name="ac"/>'s ERAM track coasts, on a coast track or in a coverage gap: it has no return.</summary>
    public static bool IsCoasting(AircraftModel ac) => ac.EramIsCoastTrack || ac.EramIsCoverageCoasting;

    /// <summary>
    /// <paramref name="ac"/>'s target glyph: none while coasting; a primary (X correlated, + not) when its transponder is on
    /// standby or off, as it then replies with no code; else ident's bars, then the 1200 V, then the beacon (\ correlated, / not).
    /// </summary>
    public static EramTargetGlyph? TargetOf(AircraftModel ac)
    {
        if (IsCoasting(ac))
        {
            return null;
        }

        return (ac.IsIdenting && !RepliesAsPrimary(ac)) ? EramTargetGlyph.Ident : SteadyTargetOf(ac);
    }

    /// <summary>
    /// <paramref name="ac"/>'s steady target glyph: the one its correlation and code give, ident and coast ignored: a primary
    /// (X correlated, + not) on standby or off, else the 1200 V, else the beacon (\ correlated, / not). A history sample first
    /// seen with no record of its own takes it (<see cref="EramHistoryGlyphs"/>).
    /// </summary>
    public static EramTargetGlyph SteadyTargetOf(AircraftModel ac)
    {
        if (RepliesAsPrimary(ac))
        {
            return ac.EramIsCorrelated ? EramTargetGlyph.CorrelatedPrimary : EramTargetGlyph.UncorrelatedPrimary;
        }

        if (ac.BeaconCode == VfrCode)
        {
            return EramTargetGlyph.Vfr;
        }

        return ac.EramIsCorrelated ? EramTargetGlyph.CorrelatedBeacon : EramTargetGlyph.UncorrelatedBeacon;
    }

    // A transponder on standby or off replies with no code, so its target draws as a primary.
    private static bool RepliesAsPrimary(AircraftModel ac) => ac.TransponderMode is "Standby" or "Off";

    /// <summary>
    /// <paramref name="ac"/>'s track-position symbol under a Full or Dwell block (none below one, or at Ground), in CRC's
    /// order: frozen, then coast, then flat when correlated with a destination, else free.
    /// </summary>
    public static EramTrackSymbol? TrackOf(AircraftModel ac, ScopeBlockLevel level)
    {
        if (level is not (ScopeBlockLevel.Full or ScopeBlockLevel.Dwell))
        {
            return null;
        }

        if (ac.EramIsFrozen)
        {
            return EramTrackSymbol.Frozen;
        }

        if (IsCoasting(ac))
        {
            return EramTrackSymbol.Coast;
        }

        return (ac.EramIsCorrelated && !string.IsNullOrEmpty(ac.Destination)) ? EramTrackSymbol.Flat : EramTrackSymbol.Free;
    }
}

/// <summary>One ERAM glyph's geometry in its 12 x 15 cell: the 2 px strokes and the filled bars.</summary>
/// <param name="Strokes">The lines and outlines, stroked 2 px wide.</param>
/// <param name="Fills">The bars, filled.</param>
internal sealed record EramGlyphShape(SKPath Strokes, SKPath Fills);

/// <summary>
/// The ERAM look's glyphs (YAAT's own drawings of CRC's shapes, from the YAAT-2683 mock: a 12 x 15 cell, 2 px strokes, crisp),
/// its yellows at CRC's default brightness groups, and its leader geometry.
/// </summary>
internal static class EramGlyphs
{
    /// <summary>The glyph cell's half width.</summary>
    public const float HalfWidth = 6f;

    /// <summary>The glyph cell's half height.</summary>
    public const float HalfHeight = 7.5f;

    /// <summary>The glyph cell's width.</summary>
    public const float CellWidth = 12f;

    /// <summary>The glyph cell's height.</summary>
    public const float CellHeight = 15f;

    /// <summary>
    /// How far the glyph cell's top sits above the target's whole pixel: <see cref="HalfHeight"/> rounded down, so the cell's
    /// centre row lands on the target's pixel.
    /// </summary>
    public const float CellTopAboveTarget = 7f;

    /// <summary>The glyphs' stroke width at 100 percent display scale; it scales with the canvas, as the cell does.</summary>
    public const float StrokeWidth = 2f;

    /// <summary>The gap from the glyph cell's right edge to a limited block's text (the mock's lane A2).</summary>
    public const float LdbGapPx = 3f;

    /// <summary>How many row pitches above the default leader's end a full block's text top sits: the end meets the third row.</summary>
    public const float LeaderRow = 2f;

    /// <summary>How far from the target the leader starts (CRC <c>LEADER_ANCHOR_OFFSET</c>).</summary>
    public const float LeaderAnchorOffsetPx = 6f;

    /// <summary>The gap from the default leader's end to its block.</summary>
    public const float BlockGapPx = 5f;

    /// <summary>The longest leader length 1 draws (CRC: min(display width / 32, 60)).</summary>
    public const float MaxLeaderLengthPx = 60f;

    /// <summary>The target glyph's yellow (CRC's targets at their default 92 percent).</summary>
    public static readonly SKColor TargetColor = new(200, 200, 0);

    /// <summary>The full block's, leader's and track symbol's yellow (CRC's FDB at its default 90 percent).</summary>
    public static readonly SKColor FdbColor = new(196, 196, 0);

    /// <summary>The limited and partial block's yellow (CRC's LDB at its default 60 percent).</summary>
    public static readonly SKColor LdbColor = new(129, 129, 0);

    /// <summary>The history glyphs' yellow (CRC's histories at their default 16 percent).</summary>
    public static readonly SKColor HistoryColor = new(34, 34, 0);

    private static readonly EramGlyphShape CorrelatedBeacon = Shape(lines: [(1, 1, 11, 14)], rects: []);
    private static readonly EramGlyphShape UncorrelatedBeacon = Shape(lines: [(1, 14, 11, 1)], rects: []);
    private static readonly EramGlyphShape CorrelatedPrimary = Shape(lines: [(1, 1, 11, 14), (11, 1, 1, 14)], rects: []);
    private static readonly EramGlyphShape UncorrelatedPrimary = Shape(lines: [], rects: [(5, 1, 2, 13), (2, 7, 8, 2)]);
    private static readonly EramGlyphShape Ident = Shape(lines: [], rects: [(0, 2, 12, 2), (0, 7, 12, 2), (0, 12, 12, 2)]);
    private static readonly EramGlyphShape Vfr = Shape(lines: [(1, 1, 6, 10), (11, 1, 6, 10)], rects: [(5, 10, 2, 4)]);
    private static readonly EramGlyphShape Flat = new(DiamondPath(), new SKPath());
    private static readonly EramGlyphShape Free = new(FreePath(), new SKPath());
    private static readonly EramGlyphShape Coast = Shape(lines: [], rects: [(3, 1, 2, 13), (7, 1, 2, 13), (1, 4, 10, 2), (1, 9, 10, 2)]);
    private static readonly EramGlyphShape Frozen = Shape(lines: [(2, 3, 10, 12), (10, 3, 2, 12)], rects: [(1, 1, 10, 2), (1, 12, 10, 2)]);

    /// <summary>The geometry of <paramref name="glyph"/>.</summary>
    public static EramGlyphShape ShapeOf(EramTargetGlyph glyph) =>
        glyph switch
        {
            EramTargetGlyph.CorrelatedBeacon => CorrelatedBeacon,
            EramTargetGlyph.UncorrelatedBeacon => UncorrelatedBeacon,
            EramTargetGlyph.CorrelatedPrimary => CorrelatedPrimary,
            EramTargetGlyph.UncorrelatedPrimary => UncorrelatedPrimary,
            EramTargetGlyph.Ident => Ident,
            EramTargetGlyph.Vfr => Vfr,
            _ => throw new ArgumentOutOfRangeException(nameof(glyph), glyph, $"no ERAM glyph shape for {glyph}"),
        };

    /// <summary>The geometry of <paramref name="symbol"/>.</summary>
    public static EramGlyphShape ShapeOf(EramTrackSymbol symbol) =>
        symbol switch
        {
            EramTrackSymbol.Flat => Flat,
            EramTrackSymbol.Free => Free,
            EramTrackSymbol.Coast => Coast,
            EramTrackSymbol.Frozen => Frozen,
            _ => throw new ArgumentOutOfRangeException(nameof(symbol), symbol, $"no ERAM track symbol shape for {symbol}"),
        };

    /// <summary>The top-left of the glyph cell drawn at <paramref name="at"/>: the cell's centre on the target's whole pixel.</summary>
    public static SKPoint CellOrigin(SKPoint at) => new(MathF.Round(at.X) - HalfWidth, MathF.Round(at.Y) - CellTopAboveTarget);

    /// <summary>
    /// Draws <paramref name="shape"/> at <paramref name="at"/>: its strokes with <paramref name="stroke"/>, its bars with
    /// <paramref name="fill"/>.
    /// </summary>
    public static void Draw(SKCanvas canvas, EramGlyphShape shape, SKPoint at, SKPaint stroke, SKPaint fill)
    {
        SKPoint origin = CellOrigin(at);
        canvas.Save();
        canvas.Translate(origin.X, origin.Y);
        canvas.DrawPath(shape.Strokes, stroke);
        canvas.DrawPath(shape.Fills, fill);
        canvas.Restore();
    }

    /// <summary>The length of CRC's leader length 1 on a display <paramref name="viewportWidth"/> wide.</summary>
    public static float LeaderLength(float viewportWidth) => Math.Min(viewportWidth / 32f, MaxLeaderLengthPx);

    private static EramGlyphShape Shape((float X1, float Y1, float X2, float Y2)[] lines, (float X, float Y, float W, float H)[] rects)
    {
        var strokes = new SKPath();
        foreach ((float x1, float y1, float x2, float y2) in lines)
        {
            strokes.MoveTo(x1, y1);
            strokes.LineTo(x2, y2);
        }

        var fills = new SKPath();
        foreach ((float x, float y, float w, float h) in rects)
        {
            fills.AddRect(SKRect.Create(x, y, w, h));
        }

        return new EramGlyphShape(strokes, fills);
    }

    // The mock's "M6,1 L11,7.5 L6,14 L1,7.5 Z".
    private static SKPath DiamondPath()
    {
        var path = new SKPath();
        path.MoveTo(6, 1);
        path.LineTo(11, 7.5f);
        path.LineTo(6, 14);
        path.LineTo(1, 7.5f);
        path.Close();
        return path;
    }

    // The mock's "M10,1 L10,14 M10,1 A8,6.5 0 0 0 10,14": the bar, and the half ellipse bulging left from its top to its bottom.
    private static SKPath FreePath()
    {
        var path = new SKPath();
        path.MoveTo(10, 1);
        path.LineTo(10, 14);
        path.AddArc(new SKRect(2, 1, 18, 14), 270, -180);
        return path;
    }
}

/// <summary>
/// The target glyph each ERAM history sample had when it was taken, kept per aircraft by the sample's position (a sample carries
/// no time): a sample first seen takes the glyph its target has then, so a state change shows on the newer samples only, and a
/// sample taken while coasting has none and is not drawn. On a track's first sight the samples it already has take its steady
/// glyph (<see cref="EramSymbolState.SteadyTargetOf"/>), as their state is unknown. Samples gone from the history are forgotten,
/// and with HIST at 0 the whole record is, so raising HIST seeds the trail afresh.
/// </summary>
internal static class EramHistoryGlyphs
{
    private static readonly ConditionalWeakTable<AircraftModel, Dictionary<(double Lat, double Lon), EramTargetGlyph?>> Seen = [];

    /// <summary>
    /// The glyph of each of <paramref name="ac"/>'s newest <paramref name="count"/> history samples, oldest first and one per
    /// <see cref="ScopeTrackLayers.HistoryDots"/> point; new samples are recorded with <paramref name="current"/>, those present
    /// at the track's first sight with its steady glyph.
    /// </summary>
    public static IReadOnlyList<EramTargetGlyph?> For(AircraftModel ac, int count, EramTargetGlyph? current)
    {
        if (count <= 0)
        {
            Seen.Remove(ac);
            return [];
        }

        if (ac.PositionHistory is not { Count: > 0 } samples)
        {
            return [];
        }

        Dictionary<(double Lat, double Lon), EramTargetGlyph?> seen = Seen.GetValue(ac, static _ => []);
        lock (seen)
        {
            Record(seen, samples, (seen.Count == 0) ? EramSymbolState.SteadyTargetOf(ac) : current);
            var glyphs = new List<EramTargetGlyph?>(Math.Min(count, samples.Count));
            for (int i = Math.Max(0, samples.Count - count); i < samples.Count; i++)
            {
                if (samples[i].Length >= 2)
                {
                    glyphs.Add(seen[(samples[i][0], samples[i][1])]);
                }
            }

            return glyphs;
        }
    }

    private static void Record(Dictionary<(double Lat, double Lon), EramTargetGlyph?> seen, IReadOnlyList<double[]> samples, EramTargetGlyph? current)
    {
        foreach (double[] sample in samples)
        {
            if (sample.Length >= 2)
            {
                seen.TryAdd((sample[0], sample[1]), current);
            }
        }

        if (seen.Count <= samples.Count)
        {
            return;
        }

        var live = new HashSet<(double Lat, double Lon)>(samples.Where(s => s.Length >= 2).Select(s => (s[0], s[1])));
        foreach ((double Lat, double Lon) key in seen.Keys.Where(k => !live.Contains(k)).ToList())
        {
            seen.Remove(key);
        }
    }
}
