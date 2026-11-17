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
);

/// <summary>Where a track's block sits, and whether it lays out anything at all.</summary>
public sealed partial record ScopeFrame
{
    /// <summary>
    /// Whether <paramref name="ac"/>'s block and leader are not drawn: the instructor hid its block and the track is not
    /// selected, which still shows it.
    /// </summary>
    private static bool IsBlockHidden(ScopeFrameInput input, AircraftModel ac, bool isSelected) =>
        (!isSelected) && (input.HiddenCallsigns?.Contains(ac.Callsign) == true);

    /// <summary>
    /// Places <paramref name="seed"/>'s block and returns the offset it took from the symbol: a manual offset wins; else a
    /// STARS-owned track follows the student's leader direction while the preference is on; else the default offset. A hidden
    /// block lays out nothing at the symbol, so neither the frame's draw nor its hit-test finds a block there, while the
    /// symbol keeps its rings and its own hit.
    /// </summary>
    private static (ScopeBlockLayout Block, SKPoint Offset) PlaceBlock(in ScopeBlockSeed seed)
    {
        AircraftModel ac = seed.Aircraft;
        bool hasManual = seed.Input.ManualOffsets.TryGetValue(ac.Callsign, out SKPoint manual);
        bool syncLeader = seed.Input.SyncLeaderDirection && (seed.TemplateId == ScopeTemplates.StarsId);
        SKPoint offset = DatablockText.ResolveBlockOffset(ac, syncLeader, hasManual, manual, seed.AtOrigin.Bounds, null);
        return (seed.Hidden ? EmptyBlockAt(seed.Symbol) : seed.AtOrigin.At(seed.Symbol.X + offset.X, seed.Symbol.Y + offset.Y), offset);
    }

    // A block that draws nothing: no fields, an empty bounds and text box at the symbol, so no block hovers or takes a press
    // where the block would have been.
    private static ScopeBlockLayout EmptyBlockAt(SKPoint symbol)
    {
        var point = new SKRect(symbol.X, symbol.Y, symbol.X, symbol.Y);
        return new ScopeBlockLayout(point, [], point);
    }
}
