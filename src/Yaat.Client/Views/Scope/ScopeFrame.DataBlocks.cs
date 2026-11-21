using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;

namespace Yaat.Client.Views.Scope;

/// <summary>What one track's block is placed from: the frame's inputs, the track, its layout at text origin and its symbol.</summary>
/// <param name="Input">The frame's inputs.</param>
/// <param name="Aircraft">The track whose block is placed.</param>
/// <param name="AtOrigin">The block's layout at text origin, before any offset.</param>
/// <param name="Symbol">Screen point of the track's position symbol.</param>
/// <param name="Hidden">Whether the track's block is hidden and not shown by the selection.</param>
/// <param name="TemplateId">The template the track was resolved to.</param>
internal readonly record struct ScopeBlockSeed(
    ScopeFrameInput Input,
    AircraftModel Aircraft,
    ScopeBlockLayout AtOrigin,
    SKPoint Symbol,
    bool Hidden,
    string TemplateId
)
{
    /// <summary>The level the track's block was presented at.</summary>
    public required ScopeBlockLevel Level { get; init; }
}

/// <summary>Where a track's block sits, and whether it lays out anything at all.</summary>
public sealed partial record ScopeFrame
{
    /// <summary>
    /// Whether <paramref name="ac"/>'s block and leader are not drawn: the instructor hid its block and the track is not
    /// selected, which still shows it.
    /// </summary>
    internal static bool IsBlockHidden(ScopeFrameInput input, AircraftModel ac, bool isSelected) =>
        (!isSelected) && (input.HiddenCallsigns?.Contains(ac.Callsign) == true);

    /// <summary>
    /// Places <paramref name="seed"/>'s block and returns the offset it took from the symbol: a manual offset wins; else the
    /// offset deconfliction gave it; else a STARS-owned track follows the student's leader direction while the preference is
    /// on; else an ERAM block's CRC default (<see cref="EramDefaultOffsetOf"/>); else the default offset. A hidden block lays
    /// out nothing at the symbol, so neither the frame's draw nor its hit-test finds a block there, while the symbol keeps its
    /// rings and its own hit.
    /// </summary>
    private static (ScopeBlockLayout Block, SKPoint Offset) PlaceBlock(in ScopeBlockSeed seed)
    {
        AircraftModel ac = seed.Aircraft;
        bool hasManual = seed.Input.ManualOffsets.TryGetValue(ac.Callsign, out SKPoint manual);
        bool syncLeader = seed.Input.SyncLeaderDirection && (seed.TemplateId == ScopeTemplates.StarsId);
        SKPoint? deconflicted = DeconflictedOffset(seed.Input, ac.Callsign);
        SKPoint offset =
            EramDefaultOffsetOf(seed, hasManual, deconflicted)
            ?? DatablockText.ResolveBlockOffset(ac, syncLeader, hasManual, manual, seed.AtOrigin.Bounds, deconflicted);
        return (seed.Hidden ? EmptyBlockAt(seed.Symbol) : seed.AtOrigin.At(seed.Symbol.X + offset.X, seed.Symbol.Y + offset.Y), offset);
    }

    /// <summary>
    /// The ERAM default offset of <paramref name="seed"/>'s block, drawn with the ERAM template and neither dragged nor moved by
    /// deconfliction: a Full or Dwell block's leader placement (<see cref="EramDefaultOffset"/>), a Limited or Partial block's
    /// place beside the glyph (<see cref="EramLdbOffset"/>); null for any other block, which takes the default.
    /// </summary>
    private static SKPoint? EramDefaultOffsetOf(in ScopeBlockSeed seed, bool hasManual, SKPoint? deconflicted)
    {
        if (hasManual || (deconflicted is not null) || (seed.TemplateId != ScopeTemplates.EramId))
        {
            return null;
        }

        return seed.Level switch
        {
            ScopeBlockLevel.Full or ScopeBlockLevel.Dwell => EramDefaultOffset(
                seed.Input.Viewport.PixelWidth,
                seed.AtOrigin.TextBounds,
                seed.Input.MetricsFor(seed.TemplateId).LineHeight
            ),
            ScopeBlockLevel.Limited or ScopeBlockLevel.Partial => EramLdbOffset(seed.Symbol, seed.AtOrigin.TextBounds),
            _ => null,
        };
    }

    /// <summary>
    /// CRC's default ERAM full-block placement (the YAAT-2683 mock's lane A2): the leader starts
    /// <see cref="EramGlyphs.LeaderAnchorOffsetPx"/> out and runs leader length 1 (<see cref="EramGlyphs.LeaderLength"/>) to the
    /// north-east, to its end e; the text box's left sits <see cref="EramGlyphs.BlockGapPx"/> past e and its top
    /// <see cref="EramGlyphs.LeaderRow"/> row pitches (<paramref name="rowPitch"/>) above it, so e meets the block's third row.
    /// <paramref name="textAtOrigin"/> is the block's text bounds at text origin.
    /// </summary>
    internal static SKPoint EramDefaultOffset(float viewportWidth, SKRect textAtOrigin, float rowPitch)
    {
        float leg = (EramGlyphs.LeaderAnchorOffsetPx + EramGlyphs.LeaderLength(viewportWidth)) / MathF.Sqrt(2f);
        return new SKPoint(leg + EramGlyphs.BlockGapPx - textAtOrigin.Left, -leg - (EramGlyphs.LeaderRow * rowPitch) - textAtOrigin.Top);
    }

    /// <summary>
    /// CRC's ERAM limited-block placement (lane A2): no leader, the text box's left <see cref="EramGlyphs.LdbGapPx"/> right of
    /// the glyph cell drawn at <paramref name="symbol"/>, and the first row's baseline (the text origin) on the cell's bottom
    /// edge. <paramref name="textAtOrigin"/> is the block's text bounds at text origin.
    /// </summary>
    internal static SKPoint EramLdbOffset(SKPoint symbol, SKRect textAtOrigin)
    {
        SKPoint cell = EramGlyphs.CellOrigin(symbol);
        float left = cell.X + EramGlyphs.CellWidth + EramGlyphs.LdbGapPx;
        float baseline = cell.Y + EramGlyphs.CellHeight;
        return new SKPoint(left - symbol.X - textAtOrigin.Left, baseline - symbol.Y);
    }

    /// <summary>
    /// The offset deconfliction gave <paramref name="callsign"/>'s block (<see cref="ScopeFrameInput.DeconflictOffsets"/>), or
    /// null.
    /// </summary>
    private static SKPoint? DeconflictedOffset(ScopeFrameInput input, string callsign) =>
        ((input.DeconflictOffsets is { } offsets) && offsets.TryGetValue(callsign, out SKPoint offset)) ? offset : null;

    // A block that draws nothing: no fields, an empty bounds and text box at the symbol, so no block hovers or takes a press
    // where the block would have been.
    private static ScopeBlockLayout EmptyBlockAt(SKPoint symbol)
    {
        var point = new SKRect(symbol.X, symbol.Y, symbol.X, symbol.Y);
        return new ScopeBlockLayout(point, [], point);
    }
}
