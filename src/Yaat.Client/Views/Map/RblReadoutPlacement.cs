using SkiaSharp;

namespace Yaat.Client.Views.Map;

/// <summary>A spot a range/bearing readout can take around its anchor, in preference order: East is CRC's spot.</summary>
public enum RblReadoutSpot
{
    East,
    NorthEast,
    SouthEast,
    North,
    South,
    NorthWest,
    SouthWest,
    West,
}

/// <summary>
/// A datablock a readout may nudge: one at its default or auto-deconflicted placement.
/// <paramref name="Rect"/> is its screen rect without any nudge; <paramref name="Anchor"/> is its aircraft symbol.
/// </summary>
public readonly record struct RblMovableBlock(string AircraftId, SKPoint Anchor, SKRect Rect);

/// <summary>
/// One readout to place this frame. <paramref name="Anchor"/> is the point the ring is built around (the far end, or
/// where the line leaves the screen); <paramref name="Size"/> is the text box, its bottom on the baseline.
/// <paramref name="OwnSymbol"/> is the screen point of the aircraft the far end is latched to, whose symbol the readout
/// is not asked to avoid; <paramref name="AllowNudge"/> is false for the half-placed line.
/// </summary>
public readonly record struct RblReadout(int Slot, SKPoint Anchor, SKSize Size, SKPoint? OwnSymbol, bool AllowNudge);

/// <summary>What readouts avoid this frame, and the limits a nudge works within.</summary>
public sealed record RblObstacles
{
    /// <summary>Datablocks no nudge moves: manual drag offsets and EuroScope tags.</summary>
    public required IReadOnlyList<SKRect> FixedBlocks { get; init; }

    /// <summary>
    /// What earlier readouts took this frame: their rects and the blocks they nudged, at the nudged rects. A readout
    /// avoids these as it avoids <see cref="FixedBlocks"/>, and never nudges a block onto one. Empty for the first readout.
    /// </summary>
    public required IReadOnlyList<SKRect> PlacedThisFrame { get; init; }

    /// <summary>Datablocks a readout may nudge.</summary>
    public required IReadOnlyList<RblMovableBlock> MovableBlocks { get; init; }

    /// <summary>Aircraft symbols, avoided as squares of half-side <see cref="SymbolHalfSide"/>.</summary>
    public required IReadOnlyList<SKPoint> SymbolCentres { get; init; }

    /// <summary>Half the side of the square a symbol occupies.</summary>
    public required float SymbolHalfSide { get; init; }

    /// <summary>How far a nudge may pull a block from its symbol.</summary>
    public required float MaxLeaderLength { get; init; }

    /// <summary>The viewport a spot is clamped into before it is scored.</summary>
    public required SKSize ViewSize { get; init; }
}

/// <summary>Where one readout went, and the nudge (aircraft id → offset delta) it gives the blocks it would cover.</summary>
public readonly record struct RblPlacement(RblReadoutSpot Spot, SKRect Rect, IReadOnlyDictionary<string, SKPoint> Nudges);

/// <summary>A datablock's nudge: the readout (by slot) that made it, and the offset delta.</summary>
public readonly record struct RblNudge(int Slot, SKPoint Delta);

/// <summary>
/// Places range/bearing readouts clear of datablocks, aircraft symbols and each other: the cheapest of eight spots
/// around the far end, kept unless another is clearly cheaper, and, when every spot is covered, a small nudge of the
/// auto-placed datablocks under the spot the fixed obstacles leave freest.
/// </summary>
public static class RblReadoutPlacement
{
    /// <summary>Gap between the anchor and the readout's nearest edge (or corner, for a diagonal spot), CRC's 9 px.</summary>
    public const float SpotGapPx = 9f;

    /// <summary>Weight of one pixel² of overlap against the spot preference cost (0 for East up to 7 for West).</summary>
    public const float OverlapWeight = 1000f;

    /// <summary>Stops a readout trading spots over sub-pixel overlap changes: half a 1000-weighted pixel².</summary>
    public const float IncumbentMarginCost = 500f;

    /// <summary>
    /// Judgement call: a nudged block returns only once it clears the readout by this much, so one crossing the edge
    /// does not flicker.
    /// </summary>
    public const float NudgeReturnMarginPx = 6f;

    /// <summary>The East spot's baseline sits this far below the anchor, as CRC draws it.</summary>
    private const float EastBaselineBelowPx = 4f;

    private static readonly IReadOnlyDictionary<string, SKPoint> NoNudges = new Dictionary<string, SKPoint>();

    private static readonly RblReadoutSpot[] Spots = Enum.GetValues<RblReadoutSpot>();

    /// <summary>
    /// Places every readout in order. Each placed readout, and each block it nudged (at its nudged rect), joins
    /// <see cref="RblObstacles.PlacedThisFrame"/> for the readouts after it. Rewrites <paramref name="incumbents"/> (slot → spot) and
    /// <paramref name="nudges"/> (aircraft id → the nudging readout's slot and delta, read first as last frame's nudges).
    /// </summary>
    /// <returns>Each placed readout's rect by slot, already clamped into the view.</returns>
    public static Dictionary<int, SKRect> PlaceAll(
        IReadOnlyList<RblReadout> readouts,
        RblObstacles obstacles,
        Dictionary<int, RblReadoutSpot> incumbents,
        Dictionary<string, RblNudge> nudges
    )
    {
        Dictionary<int, Dictionary<string, SKPoint>> previousBySlot = NudgesBySlot(nudges);
        nudges.Clear();
        var placedRects = new List<SKRect>(obstacles.PlacedThisFrame);
        var movable = new List<RblMovableBlock>(obstacles.MovableBlocks);
        var rects = new Dictionary<int, SKRect>(readouts.Count);
        var spots = new Dictionary<int, RblReadoutSpot>(readouts.Count);
        foreach (RblReadout readout in readouts)
        {
            RblReadoutSpot? incumbent = incumbents.TryGetValue(readout.Slot, out RblReadoutSpot spot) ? spot : null;
            RblObstacles frame = obstacles with { PlacedThisFrame = [.. placedRects], MovableBlocks = [.. movable] };
            IReadOnlyDictionary<string, SKPoint> ownPrevious = previousBySlot.TryGetValue(readout.Slot, out Dictionary<string, SKPoint>? own)
                ? own
                : NoNudges;
            RblPlacement placement = Place(readout, frame, incumbent, ownPrevious);
            rects[readout.Slot] = placement.Rect;
            spots[readout.Slot] = placement.Spot;
            placedRects.Add(placement.Rect);
            TakeNudgedBlocks(readout.Slot, placement.Nudges, movable, placedRects, nudges);
        }

        incumbents.Clear();
        foreach (KeyValuePair<int, RblReadoutSpot> spot in spots)
        {
            incumbents[spot.Key] = spot.Value;
        }

        return rects;
    }

    /// <summary>Last frame's nudges grouped by the readout that made them, so each readout's hysteresis sees only its own.</summary>
    private static Dictionary<int, Dictionary<string, SKPoint>> NudgesBySlot(IReadOnlyDictionary<string, RblNudge> nudges)
    {
        var bySlot = new Dictionary<int, Dictionary<string, SKPoint>>();
        foreach (KeyValuePair<string, RblNudge> nudge in nudges)
        {
            if (!bySlot.TryGetValue(nudge.Value.Slot, out Dictionary<string, SKPoint>? slotNudges))
            {
                slotNudges = [];
                bySlot[nudge.Value.Slot] = slotNudges;
            }

            slotNudges[nudge.Key] = nudge.Value.Delta;
        }

        return bySlot;
    }

    /// <summary>
    /// Places one readout:the cheapest spot by overlap with every obstacle (hysteresis on <paramref name="incumbent"/>);
    /// when that spot still overlaps something and nudging is allowed, the spot cheapest by the obstacles a nudge cannot
    /// clear (fixed ones, and movable blocks no nudge clears within the leader cap without landing on a fixed rect), with
    /// the movable blocks under it nudged clear. <paramref name="previousNudges"/> is this readout's own nudges from last
    /// frame. Every spot is scored, and returned, as clamped into the view.
    /// </summary>
    public static RblPlacement Place(
        in RblReadout readout,
        RblObstacles obstacles,
        RblReadoutSpot? incumbent,
        IReadOnlyDictionary<string, SKPoint> previousNudges
    )
    {
        List<SKRect> fixedRects = FixedRects(readout, obstacles);
        IReadOnlyList<RblMovableBlock> movable = obstacles.MovableBlocks;
        RblReadoutSpot spot = Choose(readout, obstacles.ViewSize, rect => OverlapArea(rect, fixedRects) + OverlapArea(rect, movable), incumbent);
        SKRect placed = CandidateRect(spot, readout, obstacles.ViewSize);
        if (!readout.AllowNudge)
        {
            return new RblPlacement(spot, placed, NoNudges);
        }

        bool nudgeMode = (OverlapArea(placed, fixedRects) + OverlapArea(placed, movable)) > 0f;
        if (nudgeMode)
        {
            spot = Choose(readout, obstacles.ViewSize, rect => OverlapArea(rect, fixedRects) + UnclearableArea(rect, obstacles), incumbent);
            placed = CandidateRect(spot, readout, obstacles.ViewSize);
        }

        return new RblPlacement(spot, placed, Nudges(placed, obstacles, previousNudges, nudgeMode));
    }

    /// <summary>The readout rect a spot gives around <paramref name="anchor"/>, before any clamp into the view.</summary>
    public static SKRect SpotRect(RblReadoutSpot spot, SKPoint anchor, SKSize size)
    {
        float diagonal = SpotGapPx / MathF.Sqrt(2f);
        float eastBottom = anchor.Y + EastBaselineBelowPx;
        float centredLeft = anchor.X - (size.Width / 2f);
        (float left, float bottom) = spot switch
        {
            RblReadoutSpot.East => (anchor.X + SpotGapPx, eastBottom),
            RblReadoutSpot.NorthEast => (anchor.X + diagonal, anchor.Y - diagonal),
            RblReadoutSpot.SouthEast => (anchor.X + diagonal, anchor.Y + diagonal + size.Height),
            RblReadoutSpot.North => (centredLeft, anchor.Y - SpotGapPx),
            RblReadoutSpot.South => (centredLeft, anchor.Y + SpotGapPx + size.Height),
            RblReadoutSpot.NorthWest => (anchor.X - diagonal - size.Width, anchor.Y - diagonal),
            RblReadoutSpot.SouthWest => (anchor.X - diagonal - size.Width, anchor.Y + diagonal + size.Height),
            _ => (anchor.X - SpotGapPx - size.Width, eastBottom),
        };
        return new SKRect(left, bottom - size.Height, left + size.Width, bottom);
    }

    private static SKRect CandidateRect(RblReadoutSpot spot, in RblReadout readout, SKSize viewSize) =>
        RblLabelPlacement.ClampIntoView(SpotRect(spot, readout.Anchor, readout.Size), viewSize.Width, viewSize.Height);

    /// <summary>Moves the blocks a readout nudged out of the movable list and into the placed rects, at their nudged rects.</summary>
    private static void TakeNudgedBlocks(
        int slot,
        IReadOnlyDictionary<string, SKPoint> placementNudges,
        List<RblMovableBlock> movable,
        List<SKRect> placedRects,
        Dictionary<string, RblNudge> nudges
    )
    {
        for (int i = movable.Count - 1; i >= 0; i--)
        {
            RblMovableBlock block = movable[i];
            if (placementNudges.TryGetValue(block.AircraftId, out SKPoint nudge))
            {
                nudges[block.AircraftId] = new RblNudge(slot, nudge);
                placedRects.Add(Translate(block.Rect, nudge));
                movable.RemoveAt(i);
            }
        }
    }

    private static List<SKRect> FixedRects(in RblReadout readout, RblObstacles obstacles)
    {
        var rects = new List<SKRect>(obstacles.FixedBlocks.Count + obstacles.PlacedThisFrame.Count + obstacles.SymbolCentres.Count);
        rects.AddRange(obstacles.FixedBlocks);
        rects.AddRange(obstacles.PlacedThisFrame);
        float half = obstacles.SymbolHalfSide;
        foreach (SKPoint centre in obstacles.SymbolCentres)
        {
            if (readout.OwnSymbol is { } own && (MathF.Abs(own.X - centre.X) < 0.5f) && (MathF.Abs(own.Y - centre.Y) < 0.5f))
            {
                continue;
            }

            rects.Add(new SKRect(centre.X - half, centre.Y - half, centre.X + half, centre.Y + half));
        }

        return rects;
    }

    private static RblReadoutSpot Choose(in RblReadout readout, SKSize viewSize, Func<SKRect, float> overlapArea, RblReadoutSpot? incumbent)
    {
        RblReadoutSpot best = RblReadoutSpot.East;
        float bestCost = float.MaxValue;
        float incumbentCost = float.MaxValue;
        foreach (RblReadoutSpot spot in Spots)
        {
            float cost = (OverlapWeight * overlapArea(CandidateRect(spot, readout, viewSize))) + (int)spot;
            if (cost < bestCost)
            {
                best = spot;
                bestCost = cost;
            }

            if (spot == incumbent)
            {
                incumbentCost = cost;
            }
        }

        return (incumbent is { } kept) && (bestCost >= (incumbentCost - IncumbentMarginCost)) ? kept : best;
    }

    private static Dictionary<string, SKPoint> Nudges(
        SKRect readout,
        RblObstacles obstacles,
        IReadOnlyDictionary<string, SKPoint> previousNudges,
        bool nudgeMode
    )
    {
        var nudges = new Dictionary<string, SKPoint>();
        foreach (RblMovableBlock block in obstacles.MovableBlocks)
        {
            if (previousNudges.TryGetValue(block.AircraftId, out SKPoint previous) && KeepsNudge(block, previous, readout, obstacles))
            {
                nudges[block.AircraftId] = previous;
            }
            else if (
                nudgeMode
                && (DatablockDeconfliction.IntersectArea(block.Rect, readout) > 0f)
                && (ClearingNudge(block, readout, obstacles) is { } nudge)
            )
            {
                nudges[block.AircraftId] = nudge;
            }
        }

        return nudges;
    }

    /// <summary>
    /// Last frame's nudge stays while the un-nudged block is still within the return margin of the readout, the nudge
    /// still clears the readout, and the nudged block still fits (<see cref="NudgeFits"/>).
    /// </summary>
    private static bool KeepsNudge(in RblMovableBlock block, SKPoint nudge, SKRect readout, RblObstacles obstacles)
    {
        SKRect returnZone = new(
            readout.Left - NudgeReturnMarginPx,
            readout.Top - NudgeReturnMarginPx,
            readout.Right + NudgeReturnMarginPx,
            readout.Bottom + NudgeReturnMarginPx
        );
        SKRect nudged = Translate(block.Rect, nudge);
        return (DatablockDeconfliction.IntersectArea(block.Rect, returnZone) > 0f)
            && (DatablockDeconfliction.IntersectArea(nudged, readout) <= 0f)
            && NudgeFits(block, nudged, obstacles);
    }

    /// <summary>The shortest left/right/up/down move that takes the block clear of the readout to a rect that still fits (<see cref="NudgeFits"/>).</summary>
    private static SKPoint? ClearingNudge(in RblMovableBlock block, SKRect readout, RblObstacles obstacles)
    {
        SKPoint[] moves =
        [
            new(readout.Left - block.Rect.Right, 0f),
            new(readout.Right - block.Rect.Left, 0f),
            new(0f, readout.Top - block.Rect.Bottom),
            new(0f, readout.Bottom - block.Rect.Top),
        ];
        SKPoint? best = null;
        float bestLength = float.MaxValue;
        foreach (SKPoint move in moves)
        {
            float length = MathF.Abs(move.X) + MathF.Abs(move.Y);
            if ((length < bestLength) && NudgeFits(block, Translate(block.Rect, move), obstacles))
            {
                best = move;
                bestLength = length;
            }
        }

        return best;
    }

    /// <summary>
    /// A nudged block fits while its leader stays within the cap and it lands on nothing an earlier readout took this
    /// frame (<see cref="RblObstacles.PlacedThisFrame"/>), so a later readout never pushes a block onto an earlier one.
    /// </summary>
    private static bool NudgeFits(in RblMovableBlock block, SKRect nudged, RblObstacles obstacles) =>
        (DatablockDeconfliction.LeaderLength(block.Anchor, nudged) <= obstacles.MaxLeaderLength)
        && (OverlapArea(nudged, obstacles.PlacedThisFrame) <= 0f);

    private static float OverlapArea(SKRect rect, IReadOnlyList<SKRect> obstacles)
    {
        float total = 0f;
        foreach (SKRect obstacle in obstacles)
        {
            total += DatablockDeconfliction.IntersectArea(rect, obstacle);
        }

        return total;
    }

    private static float OverlapArea(SKRect rect, IReadOnlyList<RblMovableBlock> blocks)
    {
        float total = 0f;
        foreach (RblMovableBlock block in blocks)
        {
            total += DatablockDeconfliction.IntersectArea(rect, block.Rect);
        }

        return total;
    }

    /// <summary>
    /// Overlap with the movable blocks no fitting nudge clears from <paramref name="rect"/>: they stay, so they count as
    /// fixed.
    /// </summary>
    private static float UnclearableArea(SKRect rect, RblObstacles obstacles)
    {
        float total = 0f;
        foreach (RblMovableBlock block in obstacles.MovableBlocks)
        {
            float area = DatablockDeconfliction.IntersectArea(rect, block.Rect);
            if ((area > 0f) && (ClearingNudge(block, rect, obstacles) is null))
            {
                total += area;
            }
        }

        return total;
    }

    private static SKRect Translate(SKRect rect, SKPoint by) => new(rect.Left + by.X, rect.Top + by.Y, rect.Right + by.X, rect.Bottom + by.Y);
}
