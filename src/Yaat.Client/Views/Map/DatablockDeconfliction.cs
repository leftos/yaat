using SkiaSharp;
using Yaat.Client.Models;

namespace Yaat.Client.Views.Map;

/// <summary>
/// Pure, UI-agnostic datablock deconfliction shared by the radar and ground views. Given each
/// visible aircraft's symbol anchor, block bounds, and preferred placement, it returns an effective
/// text-origin offset per aircraft that keeps overlapping datablocks readable. Two modes:
/// <see cref="DatablockDeconflictMode.CompassSnap"/> snaps each block to one of the eight STARS
/// compass leader directions, extending the leader onto larger rings when the base ring cannot
/// deconflict; <see cref="DatablockDeconflictMode.FreeForm"/> slides blocks freely via damped
/// repulsion toward per-block deconflicted targets (assigned each frame with the same candidate
/// machinery CompassSnap uses), hopping over/under an obstacle rather than being dragged along an anchor's
/// line of travel. Both bias the resulting layout toward the aircraft's lateral order, extend leaders
/// onto outer rings when a cluster is too dense for the base span, and are deterministic and
/// frame-stable when fed the prior frame's result.
/// </summary>
public static class DatablockDeconfliction
{
    private const float SpringStiffness = 0.06f;
    private const float Damping = 0.5f;
    private const float MatchEpsilon = 0.5f;
    private const float SelfCoverPenalty = 1000f;

    /// <summary>
    /// How far a symbol anchor may sit outside the viewport before its datablock is excluded from the
    /// pass. Covers the on-screen portion of a symbol straddling the edge so a block keeps deconflicting
    /// while its symbol is visible, then is dropped once the symbol is panned out of sight.
    /// </summary>
    private const float OffscreenAnchorMargin = 24f;

    // Eight screen-compass unit directions (Y is down): N, NE, E, SE, S, SW, W, NW.
    private static readonly (int X, int Y)[] CompassDirs = [(0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1)];

    /// <summary>
    /// One datablock to place. <see cref="PreferredOffset"/> is the offset the view would use without
    /// deconfliction (leader-direction or default); for pinned blocks it is the current manual offset.
    /// </summary>
    public readonly record struct Item
    {
        /// <summary>Stable identifier (callsign) used as the result-dictionary key.</summary>
        public required string Callsign { get; init; }

        /// <summary>Aircraft symbol position in screen pixels.</summary>
        public required SKPoint Anchor { get; init; }

        /// <summary>Block bounds when its text origin sits at (0, 0).</summary>
        public required SKRect RectAtOrigin { get; init; }

        /// <summary>Offset the view would apply without deconfliction (candidate 0 / pass-through for pinned).</summary>
        public required SKPoint PreferredOffset { get; init; }

        /// <summary>Manually-dragged or otherwise fixed block: never moved, only avoided.</summary>
        public required bool IsPinned { get; init; }

        /// <summary>Selected aircraft: placed first and biased toward its preferred position.</summary>
        public required bool IsPriority { get; init; }
    }

    /// <summary>Tunable weights and screen bounds. Use <see cref="Default"/> for the standard set.</summary>
    public readonly record struct Options
    {
        public required SKRect ScreenBounds { get; init; }
        public required float WOverlap { get; init; }
        public required float WPinned { get; init; }
        public required float WSelf { get; init; }
        public required float WLeaderLen { get; init; }
        public required float WDefault { get; init; }
        public required float WOffscreen { get; init; }
        public required float WOrder { get; init; }
        public required float WOrderForce { get; init; }
        public required float DirTiebreak { get; init; }
        public required float LeaderRingStep { get; init; }
        public required float LeaderRingPenalty { get; init; }
        public required int LeaderExtraRings { get; init; }
        public required float SymbolPad { get; init; }
        public required float SymbolHitCost { get; init; }
        public required float HysteresisMargin { get; init; }
        public required float PriorityBias { get; init; }
        public required float LeaderGap { get; init; }
        public required int FreeFormIterations { get; init; }

        /// <summary>Base preference cost of a free-form slide-escape candidate.</summary>
        public required float WSlide { get; init; }

        /// <summary>Per-pixel cost added to a slide-escape candidate for its distance from the preferred placement.</summary>
        public required float WSlideDist { get; init; }

        /// <summary>
        /// Free-form target-assignment hysteresis. Must stay below <see cref="WDefault"/> so a block
        /// whose preferred placement frees up always reclaims it from a compass-slot incumbent.
        /// </summary>
        public required float FreeFormHysteresisMargin { get; init; }

        /// <summary>
        /// Outermost candidate ring. Rings beyond <see cref="LeaderExtraRings"/> are only chosen when
        /// every inner candidate overlaps — a cluster dense enough that the normal leader span cannot
        /// fit every block extends leaders further out instead of stacking blocks on each other.
        /// </summary>
        public required int MaxExtraRings { get; init; }

        /// <summary>Standard weights validated for typical traffic densities.</summary>
        public static Options Default(SKRect screenBounds) =>
            new()
            {
                ScreenBounds = screenBounds,
                WOverlap = 1f,
                WPinned = 1.5f,
                WSelf = 100000f,
                WLeaderLen = 0.5f,
                WDefault = 40f,
                WOffscreen = 2f,
                WOrder = 5f,
                WOrderForce = 0.02f,
                DirTiebreak = 1f,
                LeaderRingStep = 22f,
                LeaderRingPenalty = 20f,
                LeaderExtraRings = 3,
                SymbolPad = 2f,
                SymbolHitCost = 800f,
                HysteresisMargin = 250f,
                PriorityBias = 200f,
                LeaderGap = 18f,
                FreeFormIterations = 12,
                WSlide = 10f,
                WSlideDist = 0.25f,
                FreeFormHysteresisMargin = 25f,
                MaxExtraRings = 8,
            };
    }

    /// <summary>One candidate placement: its text-origin offset plus a fixed preference cost.</summary>
    private readonly record struct Candidate(SKPoint Offset, float Preference, bool IsPreferred);

    /// <summary>An already-placed block, kept with its anchor so later blocks can avoid it and stay in order.</summary>
    private readonly record struct Placed(SKRect Rect, SKPoint Anchor);

    /// <summary>
    /// One movable block mid free-form step: its item, current offset, the rect at that offset, and the
    /// deconflicted target the forces pull it toward (assigned per frame by <see cref="AssignTargets"/>).
    /// </summary>
    private readonly record struct Block(Item Item, SKPoint Offset, SKPoint Target, SKRect Rect);

    /// <summary>
    /// Resolves an effective text-origin offset for every item. Pinned items are written through with
    /// their <see cref="Item.PreferredOffset"/>. Pass the same <paramref name="previousResolved"/> and
    /// <paramref name="resolvedOffsets"/> dictionaries back each frame for stability; the result is
    /// fully rewritten into <paramref name="resolvedOffsets"/>.
    /// </summary>
    public static void Resolve(
        DatablockDeconflictMode mode,
        IReadOnlyList<Item> items,
        in Options options,
        IReadOnlyDictionary<string, SKPoint> previousResolved,
        Dictionary<string, SKPoint> resolvedOffsets
    )
    {
        resolvedOffsets.Clear();
        if (items.Count == 0)
        {
            return;
        }

        // Deconfliction only repositions datablocks for symbols the user can actually see. An aircraft
        // panned outside the viewport is dropped here (it emits no offset and is not an obstacle) so the
        // view falls back to its default placement, which clips off-screen with the symbol rather than
        // clamping a stranded block to the viewport edge.
        var visible = OnScreenItems(items, options.ScreenBounds);
        if (visible.Count == 0)
        {
            return;
        }

        var pinnedRects = new List<SKRect>();
        var movable = new List<Item>(visible.Count);
        foreach (var item in visible)
        {
            if (item.IsPinned)
            {
                pinnedRects.Add(Translate(item.RectAtOrigin, Add(item.Anchor, item.PreferredOffset)));
                resolvedOffsets[item.Callsign] = item.PreferredOffset;
            }
            else
            {
                movable.Add(item);
            }
        }

        if (movable.Count == 0)
        {
            return;
        }

        switch (mode)
        {
            case DatablockDeconflictMode.CompassSnap:
                ResolveCompassSnap(movable, visible, pinnedRects, options, previousResolved, resolvedOffsets);
                break;
            case DatablockDeconflictMode.FreeForm:
                ResolveFreeForm(movable, visible, pinnedRects, options, previousResolved, resolvedOffsets);
                break;
            default:
                foreach (var item in movable)
                {
                    resolvedOffsets[item.Callsign] = item.PreferredOffset;
                }
                break;
        }
    }

    private static void ResolveCompassSnap(
        List<Item> movable,
        IReadOnlyList<Item> allItems,
        List<SKRect> pinnedRects,
        in Options o,
        IReadOnlyDictionary<string, SKPoint> previousResolved,
        Dictionary<string, SKPoint> resolvedOffsets
    )
    {
        SortMovable(movable);
        var placed = new List<Placed>(movable.Count);
        var candidates = new List<Candidate>(((o.MaxExtraRings + 1) * CompassDirs.Length) + 1);

        foreach (var item in movable)
        {
            BuildCandidates(item, o, candidates);
            int bestIdx = 0;
            float bestCost = float.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                float c = CandidateCost(item, candidates[i], placed, pinnedRects, allItems, o);
                if (c < bestCost)
                {
                    bestCost = c;
                    bestIdx = i;
                }
            }

            bestIdx = ApplyHysteresis(item, candidates, bestIdx, bestCost, placed, pinnedRects, allItems, o, previousResolved);

            var chosen = candidates[bestIdx];
            placed.Add(new Placed(Translate(item.RectAtOrigin, Add(item.Anchor, chosen.Offset)), item.Anchor));
            resolvedOffsets[item.Callsign] = chosen.Offset;
        }
    }

    private static float CandidateCost(
        in Item item,
        in Candidate candidate,
        List<Placed> placed,
        List<SKRect> pinned,
        IReadOnlyList<Item> allItems,
        in Options o
    )
    {
        var rect = Translate(item.RectAtOrigin, Add(item.Anchor, candidate.Offset));
        float c = Cost(rect, item.Anchor, item.Callsign, candidate.Preference, placed, pinned, allItems, o);
        if (item.IsPriority && candidate.IsPreferred)
        {
            c -= o.PriorityBias;
        }
        return c;
    }

    private static int ApplyHysteresis(
        in Item item,
        List<Candidate> candidates,
        int bestIdx,
        float bestCost,
        List<Placed> placed,
        List<SKRect> pinned,
        IReadOnlyList<Item> allItems,
        in Options o,
        IReadOnlyDictionary<string, SKPoint> previousResolved
    )
    {
        if (!previousResolved.TryGetValue(item.Callsign, out var prev))
        {
            return bestIdx;
        }

        int incumbent = MatchCandidate(candidates, prev);
        if (incumbent < 0 || incumbent == bestIdx)
        {
            return bestIdx;
        }

        float incCost = CandidateCost(item, candidates[incumbent], placed, pinned, allItems, o);
        return incCost <= bestCost + o.HysteresisMargin ? incumbent : bestIdx;
    }

    private static void ResolveFreeForm(
        List<Item> movable,
        IReadOnlyList<Item> allItems,
        List<SKRect> pinnedRects,
        in Options o,
        IReadOnlyDictionary<string, SKPoint> previousResolved,
        Dictionary<string, SKPoint> resolvedOffsets
    )
    {
        SortMovable(movable);
        int n = movable.Count;
        var offsets = new SKPoint[n];
        for (int i = 0; i < n; i++)
        {
            offsets[i] = previousResolved.TryGetValue(movable[i].Callsign, out var prev) ? prev : movable[i].PreferredOffset;
        }

        var targets = new SKPoint[n];
        AssignTargets(movable, allItems, pinnedRects, o, previousResolved, targets);

        // An assigned slot on an extended ring sits past the normal leader cap; each block's leader
        // clamp must allow at least its own target's leader so the clamp never drags it off the slot.
        var maxLeaders = new float[n];
        for (int i = 0; i < n; i++)
        {
            var item = movable[i];
            float targetLeader = LeaderLength(item.Anchor, Translate(item.RectAtOrigin, Add(item.Anchor, targets[i])));
            maxLeaders[i] = MathF.Max(MaxLeaderLength(o), targetLeader + 0.5f);
        }

        for (int iter = 0; iter < o.FreeFormIterations; iter++)
        {
            FreeFormStep(movable, allItems, pinnedRects, o, offsets, targets, maxLeaders);
        }

        for (int i = 0; i < n; i++)
        {
            resolvedOffsets[movable[i].Callsign] = offsets[i];
        }
    }

    /// <summary>
    /// Assigns each movable block the deconflicted offset the force pass pulls it toward. A greedy
    /// candidate pass shaped like <see cref="ResolveCompassSnap"/>: preferred + compass-ring slots, plus
    /// minimal slide escapes so a transiently-blocked block rests just past its obstacle instead of
    /// being yanked onto a distant compass slot. With every block previously springing to the one shared
    /// preferred offset, a dense cluster's equilibrium was a single overlapping column that (with mixed
    /// block heights) never even converged (#406); per-block targets make a deconflicted layout the
    /// fixed point. Hysteresis keeps an established compass/preferred slot while it stays competitive
    /// (slides are excluded as incumbents — they move with the geometry), and its margin stays below
    /// <see cref="Options.WDefault"/> so a freed preferred placement is always reclaimed.
    /// </summary>
    private static void AssignTargets(
        List<Item> movable,
        IReadOnlyList<Item> allItems,
        List<SKRect> pinnedRects,
        in Options o,
        IReadOnlyDictionary<string, SKPoint> previousResolved,
        SKPoint[] targets
    )
    {
        var placed = new List<Placed>(movable.Count);
        var candidates = new List<Candidate>(((o.MaxExtraRings + 1) * CompassDirs.Length) + 3);

        for (int i = 0; i < movable.Count; i++)
        {
            var item = movable[i];
            BuildCandidates(item, o, candidates);
            int incumbent = previousResolved.TryGetValue(item.Callsign, out var prev) ? MatchCandidate(candidates, prev) : -1;
            AddSlideCandidates(item, placed, pinnedRects, o, candidates);

            int bestIdx = 0;
            float bestCost = float.MaxValue;
            for (int c = 0; c < candidates.Count; c++)
            {
                float cost = CandidateCost(item, candidates[c], placed, pinnedRects, allItems, o);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestIdx = c;
                }
            }

            if ((incumbent >= 0) && (incumbent != bestIdx))
            {
                float incCost = CandidateCost(item, candidates[incumbent], placed, pinnedRects, allItems, o);
                if (incCost <= bestCost + o.FreeFormHysteresisMargin)
                {
                    bestIdx = incumbent;
                }
            }

            var chosen = candidates[bestIdx];
            placed.Add(new Placed(Translate(item.RectAtOrigin, Add(item.Anchor, chosen.Offset)), item.Anchor));
            targets[i] = chosen.Offset;
        }
    }

    /// <summary>
    /// Adds up to two minimal slide-to-clear escapes from the preferred placement: the smallest shift,
    /// in each direction along one axis, that clears every already-placed and pinned rect. These are the
    /// natural targets for a transient conflict (a block briefly blocked by a passing neighbor hops just
    /// past it), where the nearest clear compass slot can be 60+ px away and would visibly yank the
    /// block. Only the axis perpendicular to the anchor-alignment axis of the deepest obstacle is
    /// offered — a same-row neighbor yields up/down escapes, a same-column one left/right. The anchor
    /// axis (not the rect-overlap axis, which flips with overlap depth) is what stays stable while an
    /// anchor drives past: a slide along the row would clear the neighbor by backing away from it, and
    /// against a moving anchor that candidate is recomputed a little further along every frame — the
    /// #402 drag reborn as a target. Skipped entirely when the preferred placement is already clear
    /// (candidate 0 wins anyway).
    /// </summary>
    private static void AddSlideCandidates(in Item item, List<Placed> placed, List<SKRect> pinned, in Options o, List<Candidate> dest)
    {
        var prefRect = Translate(item.RectAtOrigin, Add(item.Anchor, item.PreferredOffset));
        if (DeepestObstacleRef(prefRect, placed, pinned) is not { } obstacleRef)
        {
            return;
        }

        bool rowNeighbor = MathF.Abs(item.Anchor.X - obstacleRef.X) >= MathF.Abs(item.Anchor.Y - obstacleRef.Y);
        float maxTravel = MaxLeaderLength(o);
        var dirs = rowNeighbor ? VerticalSlideDirs : HorizontalSlideDirs;
        foreach (var (dx, dy) in dirs)
        {
            if (SlideEscape(item, placed, pinned, dx, dy, maxTravel) is { } offset)
            {
                dest.Add(new Candidate(offset, o.WSlide + (o.WSlideDist * Dist(offset, item.PreferredOffset)), false));
            }
        }
    }

    private static readonly (float X, float Y)[] VerticalSlideDirs = [(0f, -1f), (0f, 1f)];
    private static readonly (float X, float Y)[] HorizontalSlideDirs = [(-1f, 0f), (1f, 0f)];

    /// <summary>
    /// Reference point (anchor for placed blocks, rect center for pinned ones) of the obstacle that
    /// overlaps <paramref name="rect"/> the most, or null when the rect is clear.
    /// </summary>
    private static SKPoint? DeepestObstacleRef(SKRect rect, List<Placed> placed, List<SKRect> pinned)
    {
        SKPoint? reference = null;
        float deepest = 0f;
        foreach (var p in placed)
        {
            Consider(p.Rect, p.Anchor);
        }
        foreach (var r in pinned)
        {
            Consider(r, Center(r));
        }
        return reference;

        void Consider(SKRect other, SKPoint refPoint)
        {
            float area = IntersectArea(rect, other);
            if (area > deepest)
            {
                deepest = area;
                reference = refPoint;
            }
        }
    }

    /// <summary>
    /// The preferred offset shifted along one axis by the minimum needed to clear all placed and pinned
    /// rects, or null when the preferred placement is already clear or clearing needs more travel than
    /// <paramref name="maxTravel"/>.
    /// </summary>
    private static SKPoint? SlideEscape(in Item item, List<Placed> placed, List<SKRect> pinned, float dx, float dy, float maxTravel)
    {
        var offset = item.PreferredOffset;
        bool moved = false;
        for (int guard = 0; guard < 8; guard++)
        {
            var rect = Translate(item.RectAtOrigin, Add(item.Anchor, offset));
            float push = 0f;
            foreach (var p in placed)
            {
                push = MathF.Max(push, ClearanceAlong(rect, p.Rect, dx, dy));
            }
            foreach (var r in pinned)
            {
                push = MathF.Max(push, ClearanceAlong(rect, r, dx, dy));
            }

            if (push <= 0f)
            {
                return moved ? offset : null;
            }

            moved = true;
            offset = new SKPoint(offset.X + (dx * (push + 1f)), offset.Y + (dy * (push + 1f)));
            if (Dist(offset, item.PreferredOffset) > maxTravel)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>How far <paramref name="rect"/> must travel along (dx, dy) to stop intersecting <paramref name="other"/> (0 when clear).</summary>
    private static float ClearanceAlong(SKRect rect, SKRect other, float dx, float dy)
    {
        if (IntersectArea(rect, other) <= 0f)
        {
            return 0f;
        }
        if (dy < 0f)
        {
            return rect.Bottom - other.Top;
        }
        if (dy > 0f)
        {
            return other.Bottom - rect.Top;
        }
        return dx < 0f ? rect.Right - other.Left : other.Right - rect.Left;
    }

    private static void FreeFormStep(
        List<Item> movable,
        IReadOnlyList<Item> allItems,
        List<SKRect> pinned,
        in Options o,
        SKPoint[] offsets,
        SKPoint[] targets,
        float[] maxLeaders
    )
    {
        int n = movable.Count;
        var blocks = new Block[n];
        for (int i = 0; i < n; i++)
        {
            blocks[i] = new Block(movable[i], offsets[i], targets[i], Translate(movable[i].RectAtOrigin, Add(movable[i].Anchor, offsets[i])));
        }

        var delta = new SKPoint[n];
        AccumulateBlockForces(blocks, delta, o);
        AccumulatePinnedForces(blocks, pinned, delta, o);
        AccumulateOrderForces(blocks, delta, o);
        ApplyForces(blocks, allItems, delta, o, offsets, maxLeaders);
    }

    private static float MaxLeaderLength(in Options o) => o.LeaderGap + (o.LeaderExtraRings * o.LeaderRingStep);

    private static void AccumulateBlockForces(Block[] blocks, SKPoint[] delta, in Options o)
    {
        for (int i = 0; i < blocks.Length; i++)
        {
            for (int j = i + 1; j < blocks.Length; j++)
            {
                if (PairSeparation(blocks[i], blocks[j], o) is { } v)
                {
                    delta[i] = Add(delta[i], v.A);
                    delta[j] = Add(delta[j], v.B);
                }
            }
        }
    }

    private static void AccumulatePinnedForces(Block[] blocks, List<SKRect> pinned, SKPoint[] delta, in Options o)
    {
        for (int i = 0; i < blocks.Length; i++)
        {
            foreach (var p in pinned)
            {
                if (Separation(blocks[i], p, o) is { } v)
                {
                    delta[i] = Add(delta[i], v);
                }
            }
        }
    }

    /// <summary>
    /// Vector that moves a movable block off a pinned obstacle: the minimum translation, unless the
    /// <see cref="HopVector"/> along the other axis leaves the block closer to its preferred placement.
    /// Pure minimum translation always separates same-row blocks along X, so a block whose anchor is
    /// travelling along that row (a departure rolling past a holding aircraft) was shoved back exactly as
    /// fast as its anchor advanced and dragged hundreds of pixels from its aircraft (#402).
    /// </summary>
    private static SKPoint? Separation(in Block self, SKRect other, in Options o)
    {
        if (MinTranslation(self.Rect, other) is not { } mtv)
        {
            return null;
        }

        var perp = PerpendicularTranslation(self.Rect, other, mtv);
        var hop = HopVector(self.Target, self.Offset, perp);
        float afterMtv = MoveCost(self.Item, self.Target, Add(self.Offset, mtv), o);
        float afterHop = MoveCost(self.Item, self.Target, Add(self.Offset, hop), o);
        return afterHop + 1f < afterMtv ? hop : mtv;
    }

    /// <summary>
    /// Escape candidate on the axis perpendicular to the minimum translation: clear the obstacle along
    /// that axis while returning to the assigned target on the other, so a block shoved along its
    /// anchor's direction of travel hops over/under the obstacle instead of being dragged with it.
    /// </summary>
    private static SKPoint HopVector(SKPoint target, SKPoint offset, SKPoint perp) =>
        perp.X == 0f ? new SKPoint(target.X - offset.X, perp.Y) : new SKPoint(perp.X, target.Y - offset.Y);

    /// <summary>
    /// Per-block vectors that move two movable blocks off each other in opposite directions: half the
    /// minimum translation each, or each block's <see cref="HopVector"/> on the other axis when the pair's
    /// summed <see cref="MoveCost"/> is lower that way. Evaluated for the pair as a whole so the two never
    /// hop the same way.
    /// </summary>
    private static (SKPoint A, SKPoint B)? PairSeparation(in Block a, in Block b, in Options o)
    {
        if (MinTranslation(a.Rect, b.Rect) is not { } mtv)
        {
            return null;
        }

        var perp = PerpendicularTranslation(a.Rect, b.Rect, mtv);
        var hopA = HopVector(a.Target, a.Offset, Scale(perp, 0.5f));
        var hopB = HopVector(b.Target, b.Offset, Scale(perp, -0.5f));
        float costMtv =
            MoveCost(a.Item, a.Target, Add(a.Offset, Scale(mtv, 0.5f)), o) + MoveCost(b.Item, b.Target, Add(b.Offset, Scale(mtv, -0.5f)), o);
        float costHop = MoveCost(a.Item, a.Target, Add(a.Offset, hopA), o) + MoveCost(b.Item, b.Target, Add(b.Offset, hopB), o);
        return costHop + 1f < costMtv ? (hopA, hopB) : (Scale(mtv, 0.5f), Scale(mtv, -0.5f));
    }

    /// <summary>
    /// How undesirable a candidate offset is for a block: distance from its assigned target, plus a
    /// large penalty when the block (with symbol padding) would cover its own aircraft symbol — the
    /// free-form analogue of <see cref="Options.WSelf"/>.
    /// </summary>
    private static float MoveCost(in Item item, SKPoint target, SKPoint candidate, in Options o)
    {
        float cost = Dist(candidate, target);
        var rect = Inflate(Translate(item.RectAtOrigin, Add(item.Anchor, candidate)), o.SymbolPad);
        if (Contains(rect, item.Anchor))
        {
            cost += SelfCoverPenalty;
        }
        return cost;
    }

    private static float Dist(SKPoint a, SKPoint b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static SKPoint PerpendicularTranslation(SKRect a, SKRect b, SKPoint mtv)
    {
        float ix = MathF.Min(a.Right, b.Right) - MathF.Max(a.Left, b.Left);
        float iy = MathF.Min(a.Bottom, b.Bottom) - MathF.Max(a.Top, b.Top);
        float acx = (a.Left + a.Right) / 2f;
        float acy = (a.Top + a.Bottom) / 2f;
        float bcx = (b.Left + b.Right) / 2f;
        float bcy = (b.Top + b.Bottom) / 2f;
        return mtv.Y == 0f ? new SKPoint(0f, (acy >= bcy ? 1f : -1f) * iy) : new SKPoint((acx >= bcx ? 1f : -1f) * ix, 0f);
    }

    /// <summary>
    /// Hard cap on leader length at the outermost compass ring, pulling the block straight back toward
    /// its anchor by the excess. A safety net behind the axis choice above: no force combination may
    /// leave a block stranded far from its aircraft.
    /// </summary>
    private static SKPoint ClampLeader(in Item item, SKPoint offset, float maxLeader)
    {
        var rect = Translate(item.RectAtOrigin, Add(item.Anchor, offset));
        float cx = Math.Clamp(item.Anchor.X, rect.Left, rect.Right);
        float cy = Math.Clamp(item.Anchor.Y, rect.Top, rect.Bottom);
        float dx = item.Anchor.X - cx;
        float dy = item.Anchor.Y - cy;
        float len = MathF.Sqrt((dx * dx) + (dy * dy));
        if (len <= maxLeader)
        {
            return offset;
        }
        float excess = len - maxLeader;
        return new SKPoint(offset.X + (dx / len * excess), offset.Y + (dy / len * excess));
    }

    /// <summary>
    /// Gentle restoring force that nudges any pair of blocks whose centers have crossed back toward the
    /// lateral order of their anchors. Kept small so block/pinned repulsion still dominates separation.
    /// </summary>
    private static void AccumulateOrderForces(Block[] blocks, SKPoint[] delta, in Options o)
    {
        for (int i = 0; i < blocks.Length; i++)
        {
            for (int j = i + 1; j < blocks.Length; j++)
            {
                if (OrderForce(blocks[i].Item.Anchor, Center(blocks[i].Rect), blocks[j].Item.Anchor, Center(blocks[j].Rect), o) is { } v)
                {
                    delta[i] = Add(delta[i], Scale(v, 0.5f));
                    delta[j] = Add(delta[j], Scale(v, -0.5f));
                }
            }
        }
    }

    /// <summary>Integrates one step: forces + spring, then screen and leader clamps, written back to <paramref name="offsets"/>.</summary>
    private static void ApplyForces(
        Block[] blocks,
        IReadOnlyList<Item> allItems,
        SKPoint[] delta,
        in Options o,
        SKPoint[] offsets,
        float[] maxLeaders
    )
    {
        for (int i = 0; i < blocks.Length; i++)
        {
            var item = blocks[i].Item;
            var push = Add(delta[i], SymbolPush(blocks[i].Rect, allItems, o));
            var spring = Scale(Sub(blocks[i].Target, blocks[i].Offset), SpringStiffness);
            push = Add(push, spring);
            var next = Add(blocks[i].Offset, Scale(push, Damping));
            next = ClampOffset(item, next, o.ScreenBounds);
            offsets[i] = ClampLeader(item, next, maxLeaders[i]);
        }
    }

    private static SKPoint SymbolPush(SKRect rect, IReadOnlyList<Item> items, in Options o)
    {
        var inflated = Inflate(rect, o.SymbolPad);
        var push = new SKPoint(0, 0);
        foreach (var it in items)
        {
            if (Contains(inflated, it.Anchor))
            {
                push = Add(push, ExpelPoint(inflated, it.Anchor));
            }
        }
        return push;
    }

    private static float Cost(
        SKRect rect,
        SKPoint anchor,
        string ownCallsign,
        float preference,
        List<Placed> placed,
        List<SKRect> pinned,
        IReadOnlyList<Item> items,
        in Options o
    )
    {
        float cost = preference;
        var center = Center(rect);
        foreach (var p in placed)
        {
            cost += o.WOverlap * IntersectArea(rect, p.Rect);
            cost += o.WOrder * OrderInversion(anchor, center, p.Anchor, Center(p.Rect));
        }
        foreach (var r in pinned)
        {
            cost += o.WPinned * IntersectArea(rect, r);
        }
        if (Contains(rect, anchor))
        {
            cost += o.WSelf;
        }
        cost += ForeignSymbolPenalty(rect, ownCallsign, items, o);
        cost += o.WLeaderLen * LeaderLength(anchor, rect);
        cost += o.WOffscreen * OffscreenArea(rect, o.ScreenBounds);
        return cost;
    }

    private static float ForeignSymbolPenalty(SKRect rect, string ownCallsign, IReadOnlyList<Item> items, in Options o)
    {
        var inflated = Inflate(rect, o.SymbolPad);
        float penalty = 0f;
        foreach (var it in items)
        {
            if (!string.Equals(it.Callsign, ownCallsign, StringComparison.Ordinal) && Contains(inflated, it.Anchor))
            {
                penalty += o.SymbolHitCost;
            }
        }
        return penalty;
    }

    /// <summary>
    /// Builds the candidate offsets for one block: the preferred offset (candidate 0), then the eight
    /// compass directions at the base ring and on up to <see cref="Options.MaxExtraRings"/> larger rings.
    /// The preference cost keeps the preferred offset strongly favored and the base ring tried before
    /// any extension, while leaving direction choice to overlap/leader/order so a longer leader is only
    /// taken when it actually clears a conflict.
    /// </summary>
    private static void BuildCandidates(in Item item, in Options o, List<Candidate> dest)
    {
        dest.Clear();
        dest.Add(new Candidate(item.PreferredOffset, 0f, true));
        for (int ring = 0; ring <= o.MaxExtraRings; ring++)
        {
            float gap = o.LeaderGap + (ring * o.LeaderRingStep);
            float ringPenalty = ring * o.LeaderRingPenalty;
            for (int d = 0; d < CompassDirs.Length; d++)
            {
                var (hx, hy) = CompassDirs[d];
                var offset = CompassOffset(hx, hy, item.RectAtOrigin, gap);
                float preference = o.WDefault + (d * o.DirTiebreak) + ringPenalty;
                dest.Add(new Candidate(offset, preference, false));
            }
        }
    }

    /// <summary>
    /// Text-origin offset placing the block's center one leader-gap + half-extent away from the symbol
    /// in the given screen-compass direction. Mirrors the radar leader-direction geometry.
    /// </summary>
    private static SKPoint CompassOffset(int hx, int hy, SKRect rectAtOrigin, float leaderGap)
    {
        float centerX = (rectAtOrigin.Left + rectAtOrigin.Right) / 2f;
        float centerY = (rectAtOrigin.Top + rectAtOrigin.Bottom) / 2f;
        float desiredCenterX = hx * (leaderGap + (rectAtOrigin.Width / 2f));
        float desiredCenterY = hy * (leaderGap + (rectAtOrigin.Height / 2f));
        return new SKPoint(desiredCenterX - centerX, desiredCenterY - centerY);
    }

    /// <summary>
    /// Sorts movable items into a deterministic greedy-placement order: priority first, then along the
    /// cluster's wider spread axis (so a horizontal row is placed left-to-right and a column
    /// top-to-bottom), then the cross axis, then callsign.
    /// </summary>
    private static void SortMovable(List<Item> movable)
    {
        float minX = float.MaxValue;
        float maxX = float.MinValue;
        float minY = float.MaxValue;
        float maxY = float.MinValue;
        foreach (var it in movable)
        {
            minX = MathF.Min(minX, it.Anchor.X);
            maxX = MathF.Max(maxX, it.Anchor.X);
            minY = MathF.Min(minY, it.Anchor.Y);
            maxY = MathF.Max(maxY, it.Anchor.Y);
        }

        bool xPrimary = (maxX - minX) >= (maxY - minY);
        movable.Sort((a, b) => CompareMovable(a, b, xPrimary));
    }

    private static int CompareMovable(Item a, Item b, bool xPrimary)
    {
        if (a.IsPriority != b.IsPriority)
        {
            return a.IsPriority ? -1 : 1;
        }

        float a1 = xPrimary ? a.Anchor.X : a.Anchor.Y;
        float b1 = xPrimary ? b.Anchor.X : b.Anchor.Y;
        int c1 = a1.CompareTo(b1);
        if (c1 != 0)
        {
            return c1;
        }

        float a2 = xPrimary ? a.Anchor.Y : a.Anchor.X;
        float b2 = xPrimary ? b.Anchor.Y : b.Anchor.X;
        int c2 = a2.CompareTo(b2);
        return c2 != 0 ? c2 : string.CompareOrdinal(a.Callsign, b.Callsign);
    }

    /// <summary>
    /// Penalty (in pixels of crossing) for a candidate block center that sits on the wrong side of an
    /// already-placed neighbor relative to their anchors, measured on whichever axis the anchors are
    /// more separated. Zero when the anchors are co-located on that axis or the order is preserved.
    /// </summary>
    private static float OrderInversion(SKPoint anchorCur, SKPoint centerCur, SKPoint anchorNbr, SKPoint centerNbr)
    {
        float dx = anchorCur.X - anchorNbr.X;
        float dy = anchorCur.Y - anchorNbr.Y;
        if (MathF.Abs(dx) >= MathF.Abs(dy))
        {
            if (MathF.Abs(dx) < 1f)
            {
                return 0f;
            }
            float wrong = (centerCur.X - centerNbr.X) * MathF.Sign(dx);
            return wrong < 0f ? -wrong : 0f;
        }

        if (MathF.Abs(dy) < 1f)
        {
            return 0f;
        }
        float wrongY = (centerCur.Y - centerNbr.Y) * MathF.Sign(dy);
        return wrongY < 0f ? -wrongY : 0f;
    }

    /// <summary>Restoring force toward correct order for a crossed pair (null when order is preserved).</summary>
    private static SKPoint? OrderForce(SKPoint anchorI, SKPoint centerI, SKPoint anchorJ, SKPoint centerJ, in Options o)
    {
        float dx = anchorI.X - anchorJ.X;
        float dy = anchorI.Y - anchorJ.Y;
        if (MathF.Abs(dx) >= MathF.Abs(dy))
        {
            if (MathF.Abs(dx) < 1f)
            {
                return null;
            }
            float sign = MathF.Sign(dx);
            float wrong = (centerI.X - centerJ.X) * sign;
            return wrong < 0f ? new SKPoint(sign * o.WOrderForce * -wrong, 0f) : null;
        }

        if (MathF.Abs(dy) < 1f)
        {
            return null;
        }
        float signY = MathF.Sign(dy);
        float wrongY = (centerI.Y - centerJ.Y) * signY;
        return wrongY < 0f ? new SKPoint(0f, signY * o.WOrderForce * -wrongY) : null;
    }

    private static int MatchCandidate(List<Candidate> candidates, SKPoint target)
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            if (Near(candidates[i].Offset, target))
            {
                return i;
            }
        }
        return -1;
    }

    private static SKPoint? MinTranslation(SKRect a, SKRect b)
    {
        float ix = MathF.Min(a.Right, b.Right) - MathF.Max(a.Left, b.Left);
        float iy = MathF.Min(a.Bottom, b.Bottom) - MathF.Max(a.Top, b.Top);
        if (ix <= 0f || iy <= 0f)
        {
            return null;
        }

        float acx = (a.Left + a.Right) / 2f;
        float acy = (a.Top + a.Bottom) / 2f;
        float bcx = (b.Left + b.Right) / 2f;
        float bcy = (b.Top + b.Bottom) / 2f;
        if (ix < iy)
        {
            return new SKPoint((acx >= bcx ? 1f : -1f) * ix, 0f);
        }
        return new SKPoint(0f, (acy >= bcy ? 1f : -1f) * iy);
    }

    private static SKPoint ExpelPoint(SKRect rect, SKPoint p)
    {
        float toLeft = p.X - rect.Left;
        float toRight = rect.Right - p.X;
        float toTop = p.Y - rect.Top;
        float toBottom = rect.Bottom - p.Y;
        float minH = MathF.Min(toLeft, toRight);
        float minV = MathF.Min(toTop, toBottom);
        if (minH < minV)
        {
            return new SKPoint(toLeft < toRight ? toLeft : -toRight, 0f);
        }
        return new SKPoint(0f, toTop < toBottom ? toTop : -toBottom);
    }

    private static SKPoint ClampOffset(in Item item, SKPoint offset, SKRect bounds)
    {
        var rect = Translate(item.RectAtOrigin, Add(item.Anchor, offset));
        float dx = 0f;
        float dy = 0f;
        if (rect.Left < bounds.Left)
        {
            dx = bounds.Left - rect.Left;
        }
        else if (rect.Right > bounds.Right)
        {
            dx = bounds.Right - rect.Right;
        }

        if (rect.Top < bounds.Top)
        {
            dy = bounds.Top - rect.Top;
        }
        else if (rect.Bottom > bounds.Bottom)
        {
            dy = bounds.Bottom - rect.Bottom;
        }

        return new SKPoint(offset.X + dx, offset.Y + dy);
    }

    /// <summary>
    /// Returns the items whose symbol anchor lies within the viewport (expanded by
    /// <see cref="OffscreenAnchorMargin"/>). Off-screen anchors are dropped so deconfliction never
    /// clamps a stranded datablock into view for an aircraft the user has panned out of sight.
    /// </summary>
    private static List<Item> OnScreenItems(IReadOnlyList<Item> items, SKRect bounds)
    {
        var result = new List<Item>(items.Count);
        foreach (var item in items)
        {
            if (AnchorVisible(item.Anchor, bounds))
            {
                result.Add(item);
            }
        }
        return result;
    }

    private static bool AnchorVisible(SKPoint anchor, SKRect bounds) =>
        (anchor.X >= bounds.Left - OffscreenAnchorMargin)
        && (anchor.X <= bounds.Right + OffscreenAnchorMargin)
        && (anchor.Y >= bounds.Top - OffscreenAnchorMargin)
        && (anchor.Y <= bounds.Bottom + OffscreenAnchorMargin);

    private static float IntersectArea(SKRect a, SKRect b)
    {
        float ix = MathF.Min(a.Right, b.Right) - MathF.Max(a.Left, b.Left);
        float iy = MathF.Min(a.Bottom, b.Bottom) - MathF.Max(a.Top, b.Top);
        return (ix <= 0f || iy <= 0f) ? 0f : ix * iy;
    }

    private static float OffscreenArea(SKRect rect, SKRect bounds)
    {
        float total = rect.Width * rect.Height;
        if (total <= 0f)
        {
            return 0f;
        }

        float ix = MathF.Max(0f, MathF.Min(rect.Right, bounds.Right) - MathF.Max(rect.Left, bounds.Left));
        float iy = MathF.Max(0f, MathF.Min(rect.Bottom, bounds.Bottom) - MathF.Max(rect.Top, bounds.Top));
        return total - (ix * iy);
    }

    private static float LeaderLength(SKPoint anchor, SKRect rect)
    {
        float cx = Math.Clamp(anchor.X, rect.Left, rect.Right);
        float cy = Math.Clamp(anchor.Y, rect.Top, rect.Bottom);
        float dx = anchor.X - cx;
        float dy = anchor.Y - cy;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private static SKPoint Center(SKRect r) => new((r.Left + r.Right) / 2f, (r.Top + r.Bottom) / 2f);

    private static SKRect Translate(SKRect r, SKPoint p) => new(r.Left + p.X, r.Top + p.Y, r.Right + p.X, r.Bottom + p.Y);

    private static SKRect Inflate(SKRect r, float pad) => new(r.Left - pad, r.Top - pad, r.Right + pad, r.Bottom + pad);

    private static bool Contains(SKRect r, SKPoint p) => p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom;

    private static bool Near(SKPoint a, SKPoint b) => MathF.Abs(a.X - b.X) < MatchEpsilon && MathF.Abs(a.Y - b.Y) < MatchEpsilon;

    private static SKPoint Add(SKPoint a, SKPoint b) => new(a.X + b.X, a.Y + b.Y);

    private static SKPoint Sub(SKPoint a, SKPoint b) => new(a.X - b.X, a.Y - b.Y);

    private static SKPoint Scale(SKPoint p, float s) => new(p.X * s, p.Y * s);
}
