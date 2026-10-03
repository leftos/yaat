using SkiaSharp;
using Xunit;
using Yaat.Client.Views.Map;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views;

public class RblReadoutPlacementTests
{
    private const float SymbolHalfSide = 28f;
    private static readonly SKPoint B = new(400f, 300f);
    private static readonly SKSize ReadoutSize = new(100f, 12f);
    private static readonly SKSize ViewSize = new(800f, 600f);
    private static readonly float MaxLeader = DatablockDeconfliction.MaxLeaderLength(
        DatablockDeconfliction.Options.Default(new SKRect(0, 0, ViewSize.Width, ViewSize.Height))
    );
    private static readonly IReadOnlyDictionary<string, SKPoint> NoPreviousNudges = new Dictionary<string, SKPoint>();

    private static RblReadout Readout(SKSize size, bool allowNudge) => new(1, B, size, null, allowNudge);

    private static RblObstacles Obstacles(IReadOnlyList<SKRect> fixedBlocks, IReadOnlyList<RblMovableBlock> movable) =>
        ObstaclesCappedAt(fixedBlocks, movable, MaxLeader);

    private static RblObstacles ObstaclesCappedAt(IReadOnlyList<SKRect> fixedBlocks, IReadOnlyList<RblMovableBlock> movable, float maxLeader) =>
        new()
        {
            FixedBlocks = fixedBlocks,
            PlacedThisFrame = [],
            MovableBlocks = movable,
            SymbolCentres = [],
            SymbolHalfSide = SymbolHalfSide,
            MaxLeaderLength = maxLeader,
            ViewSize = ViewSize,
        };

    private static SKRect At(RblReadoutSpot spot, SKSize size) => RblReadoutPlacement.SpotRect(spot, B, size);

    private static SKRect Square(SKPoint centre, float side) =>
        new(centre.X - (side / 2f), centre.Y - (side / 2f), centre.X + (side / 2f), centre.Y + (side / 2f));

    private static SKRect Translate(SKRect rect, SKPoint by) => new(rect.Left + by.X, rect.Top + by.Y, rect.Right + by.X, rect.Bottom + by.Y);

    /// <summary>
    /// A fixed square at the centre of every spot but East around B, so every spot except East is covered by something
    /// fixed: 4 px on a side, or 2 px on <paramref name="lightest"/>.
    /// </summary>
    private static List<SKRect> CoverAllButEast(SKSize size, RblReadoutSpot? lightest) => CoverAllButEastAround(B, size, lightest);

    private static List<SKRect> CoverAllButEastAround(SKPoint anchor, SKSize size, RblReadoutSpot? lightest)
    {
        var rects = new List<SKRect>();
        foreach (RblReadoutSpot spot in Enum.GetValues<RblReadoutSpot>())
        {
            if (spot != RblReadoutSpot.East)
            {
                SKRect rect = RblReadoutPlacement.SpotRect(spot, anchor, size);
                rects.Add(Square(new SKPoint(rect.MidX, rect.MidY), spot == lightest ? 2f : 4f));
            }
        }

        return rects;
    }

    [Fact]
    public void CrcSpot_PreferredWhenClear()
    {
        RblPlacement placement = RblReadoutPlacement.Place(Readout(ReadoutSize, allowNudge: true), Obstacles([], []), null, NoPreviousNudges);

        Assert.Equal(RblReadoutSpot.East, placement.Spot);
        Assert.Equal(new SKRect(B.X + 9f, B.Y + 4f - ReadoutSize.Height, B.X + 9f + ReadoutSize.Width, B.Y + 4f), placement.Rect);
        Assert.Empty(placement.Nudges);
    }

    [Fact]
    public void RingSpot_ChosenClearOfBlock()
    {
        // A block over East also clips NorthEast (the two overlap by under 2 px), so SouthEast is the first clear spot.
        var block = new RblMovableBlock("AAL1", new SKPoint(450f, 340f), At(RblReadoutSpot.East, ReadoutSize));

        RblPlacement placement = RblReadoutPlacement.Place(Readout(ReadoutSize, allowNudge: true), Obstacles([], [block]), null, NoPreviousNudges);

        Assert.Equal(RblReadoutSpot.SouthEast, placement.Spot);
        Assert.Empty(placement.Nudges);
    }

    [Fact]
    public void Hysteresis_KeepsIncumbentWithinMargin()
    {
        // 0.4 px² over NorthEast's top-right corner: NorthEast costs 401, East 0 — cheaper, but by less than the margin.
        SKRect ne = At(RblReadoutSpot.NorthEast, ReadoutSize);
        var sliver = new SKRect(ne.Right - 0.4f, ne.Top - 5f, ne.Right + 5f, ne.Top + 1f);

        RblPlacement placement = RblReadoutPlacement.Place(
            Readout(ReadoutSize, allowNudge: false),
            Obstacles([sliver], []),
            RblReadoutSpot.NorthEast,
            NoPreviousNudges
        );

        Assert.Equal(RblReadoutSpot.NorthEast, placement.Spot);
    }

    [Fact]
    public void Hysteresis_MovesWhenCheaperByMoreThanMargin()
    {
        // 1 px² over NorthEast: NorthEast costs 1001, East 0, which clears the 500 margin.
        SKRect ne = At(RblReadoutSpot.NorthEast, ReadoutSize);
        var sliver = new SKRect(ne.Right - 1f, ne.Top - 5f, ne.Right + 5f, ne.Top + 1f);

        RblPlacement placement = RblReadoutPlacement.Place(
            Readout(ReadoutSize, allowNudge: false),
            Obstacles([sliver], []),
            RblReadoutSpot.NorthEast,
            NoPreviousNudges
        );

        Assert.Equal(RblReadoutSpot.East, placement.Spot);
    }

    [Fact]
    public void Nudge_MovesAutoPlacedBlock()
    {
        // Every spot is covered; East is the only one the fixed obstacles leave free, so the block over it moves up 4 px.
        SKRect east = At(RblReadoutSpot.East, ReadoutSize);
        var block = new RblMovableBlock("AAL1", new SKPoint(450f, 330f), new SKRect(420f, east.Top - 26f, 480f, east.Top + 4f));

        RblPlacement placement = RblReadoutPlacement.Place(
            Readout(ReadoutSize, allowNudge: true),
            Obstacles(CoverAllButEast(ReadoutSize, lightest: null), [block]),
            null,
            NoPreviousNudges
        );

        Assert.Equal(RblReadoutSpot.East, placement.Spot);
        Assert.Equal(new SKPoint(0f, -4f), Assert.Contains("AAL1", placement.Nudges));
    }

    [Theory]
    [InlineData(5f, true)]
    [InlineData(7f, false)]
    public void Nudge_ReturnsOnceClearByTheMargin(float clearancePx, bool stillNudged)
    {
        SKRect east = At(RblReadoutSpot.East, ReadoutSize);
        float bottom = east.Top - clearancePx;
        var block = new RblMovableBlock("AAL1", new SKPoint(450f, 330f), new SKRect(420f, bottom - 30f, 480f, bottom));
        var previous = new Dictionary<string, SKPoint> { ["AAL1"] = new SKPoint(0f, -4f) };

        RblPlacement placement = RblReadoutPlacement.Place(
            Readout(ReadoutSize, allowNudge: true),
            Obstacles([], [block]),
            RblReadoutSpot.East,
            previous
        );

        Assert.Equal(RblReadoutSpot.East, placement.Spot);
        Assert.Equal(stillNudged, placement.Nudges.ContainsKey("AAL1"));
    }

    [Fact]
    public void Nudge_CappedAtTheLeaderClamp_ReadoutTakesTheLeastCoveredSpot()
    {
        // A block inside East, anchored at its own centre, under a 2 px leader cap: its shortest clearing move (9 px down)
        // leaves a 5 px leader, so no nudge clears it. It stays and counts as fixed, and the readout leaves East for the
        // least-covered spot: South, whose fixed square is the smallest.
        var block = new RblMovableBlock("AAL1", new SKPoint(450f, 299f), new SKRect(430f, 295f, 470f, 303f));
        List<SKRect> fixedRects = CoverAllButEast(ReadoutSize, lightest: RblReadoutSpot.South);

        RblPlacement placement = RblReadoutPlacement.Place(
            Readout(ReadoutSize, allowNudge: true),
            ObstaclesCappedAt(fixedRects, [block], 2f),
            null,
            NoPreviousNudges
        );

        Assert.Equal(RblReadoutSpot.South, placement.Spot);
        Assert.Empty(placement.Nudges);
    }

    [Fact]
    public void Nudge_FallsBackToTheNextShortestMoveWithinTheCap()
    {
        // Up 18 px is the shortest move but leaves an 88 px leader, past the cap; down 24 px leaves 46 px.
        SKRect east = At(RblReadoutSpot.East, ReadoutSize);
        var block = new RblMovableBlock("AAL1", new SKPoint(450f, east.Bottom + 76f), new SKRect(420f, east.Top - 12f, 480f, east.Bottom + 6f));
        Assert.True(MaxLeader < 88f);

        RblPlacement placement = RblReadoutPlacement.Place(
            Readout(ReadoutSize, allowNudge: true),
            Obstacles(CoverAllButEast(ReadoutSize, lightest: null), [block]),
            null,
            NoPreviousNudges
        );

        Assert.Equal(RblReadoutSpot.East, placement.Spot);
        Assert.Equal(new SKPoint(0f, 24f), Assert.Contains("AAL1", placement.Nudges));
    }

    [Fact]
    public void PinnedBlock_NeverNudged()
    {
        // A manual-offset block and a EuroScope tag reach the helper as fixed rects; both sit over East, the cheapest spot.
        SKRect east = At(RblReadoutSpot.East, ReadoutSize);
        var manualBlock = new SKRect(east.Left + 10f, east.Top - 20f, east.Left + 50f, east.Top + 2f);
        var euroScopeTag = new SKRect(east.Left + 60f, east.Top - 20f, east.Left + 90f, east.Top + 2f);
        List<SKRect> fixedRects = [.. CoverAllButEast(ReadoutSize, lightest: null), manualBlock, euroScopeTag];

        RblPlacement placement = RblReadoutPlacement.Place(Readout(ReadoutSize, allowNudge: true), Obstacles(fixedRects, []), null, NoPreviousNudges);

        Assert.Empty(placement.Nudges);
        Assert.NotEqual(RblReadoutSpot.East, placement.Spot);
    }

    [Fact]
    public void OnlyFixedCover_PicksLeastCoveredSpot()
    {
        // 100 px² over every spot but South, which carries 4 px².
        var rects = new List<SKRect>();
        foreach (RblReadoutSpot spot in Enum.GetValues<RblReadoutSpot>())
        {
            SKRect rect = At(spot, ReadoutSize);
            rects.Add(Square(new SKPoint(rect.MidX, rect.MidY), spot == RblReadoutSpot.South ? 2f : 10f));
        }

        RblPlacement placement = RblReadoutPlacement.Place(Readout(ReadoutSize, allowNudge: true), Obstacles(rects, []), null, NoPreviousNudges);

        Assert.Equal(RblReadoutSpot.South, placement.Spot);
        Assert.Empty(placement.Nudges);
    }

    [Fact]
    public void PendingLine_NeverNudges()
    {
        // The Nudge_MovesAutoPlacedBlock layout, for the half-placed line: it takes the cheapest spot and moves nothing.
        SKRect east = At(RblReadoutSpot.East, ReadoutSize);
        var block = new RblMovableBlock("AAL1", new SKPoint(450f, 330f), new SKRect(420f, east.Top - 26f, 480f, east.Top + 4f));

        RblPlacement placement = RblReadoutPlacement.Place(
            Readout(ReadoutSize, allowNudge: false),
            Obstacles(CoverAllButEast(ReadoutSize, lightest: null), [block]),
            null,
            NoPreviousNudges
        );

        Assert.Empty(placement.Nudges);
        Assert.NotEqual(RblReadoutSpot.East, placement.Spot);
    }

    [Fact]
    public void TwoReadouts_SecondSeesTheFirstsNudge()
    {
        // The first readout pushes the block up 4 px, into the second readout's East spot, which the un-nudged block clears.
        SKRect east = At(RblReadoutSpot.East, ReadoutSize);
        var block = new RblMovableBlock("AAL1", new SKPoint(450f, 330f), new SKRect(420f, east.Top - 26f, 480f, east.Top + 4f));
        var second = new RblReadout(2, new SKPoint(B.X, east.Top - 32f), ReadoutSize, null, true);
        var incumbents = new Dictionary<int, RblReadoutSpot>();
        var nudges = new Dictionary<string, RblNudge>();

        Dictionary<int, SKRect> rects = RblReadoutPlacement.PlaceAll(
            [Readout(ReadoutSize, allowNudge: true), second],
            Obstacles(CoverAllButEast(ReadoutSize, lightest: null), [block]),
            incumbents,
            nudges
        );

        // The block carries one nudge, the first readout's: the second never moves it again.
        Assert.Equal(new RblNudge(1, new SKPoint(0f, -4f)), Assert.Contains("AAL1", nudges));
        var nudged = new SKRect(block.Rect.Left, block.Rect.Top - 4f, block.Rect.Right, block.Rect.Bottom - 4f);
        Assert.NotEqual(RblReadoutSpot.East, incumbents[2]);
        Assert.Equal(0f, DatablockDeconfliction.IntersectArea(rects[2], nudged));
    }

    [Fact]
    public void TwoReadouts_SecondNeverPushesABlockOntoTheFirst()
    {
        // The second readout sits 30 px above the first, its ring covered but for East, which overlaps the block's top
        // 4 px. Down 4 px is the shortest clearing move, but it lands the block on the first readout; up 28 px does not.
        var secondAnchor = new SKPoint(B.X, B.Y - 30f);
        var block = new RblMovableBlock("AAL1", new SKPoint(500f, 285f), new SKRect(470f, 270f, 530f, 290f));
        var second = new RblReadout(2, secondAnchor, ReadoutSize, null, true);
        var incumbents = new Dictionary<int, RblReadoutSpot>();
        var nudges = new Dictionary<string, RblNudge>();

        Dictionary<int, SKRect> rects = RblReadoutPlacement.PlaceAll(
            [Readout(ReadoutSize, allowNudge: true), second],
            Obstacles(CoverAllButEastAround(secondAnchor, ReadoutSize, lightest: null), [block]),
            incumbents,
            nudges
        );

        Assert.Equal(RblReadoutSpot.East, incumbents[1]);
        Assert.Equal(RblReadoutSpot.East, incumbents[2]);
        RblNudge nudge = Assert.Contains("AAL1", nudges);
        Assert.Equal(0f, DatablockDeconfliction.IntersectArea(rects[1], Translate(block.Rect, nudge.Delta)));
        Assert.Equal(new RblNudge(2, new SKPoint(0f, -28f)), nudge);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void KeptNudge_BelongsToTheReadoutThatMadeIt(int ownerSlot, bool kept)
    {
        // The Nudge_ReturnsOnceClearByTheMargin layout at 5 px: the first readout keeps last frame's nudge only when it
        // made it. The second readout, far away, does not keep a nudge on a block nowhere near it.
        SKRect east = At(RblReadoutSpot.East, ReadoutSize);
        float bottom = east.Top - 5f;
        var block = new RblMovableBlock("AAL1", new SKPoint(450f, 330f), new SKRect(420f, bottom - 30f, 480f, bottom));
        var second = new RblReadout(2, new SKPoint(100f, 500f), ReadoutSize, null, true);
        var incumbents = new Dictionary<int, RblReadoutSpot> { [1] = RblReadoutSpot.East, [2] = RblReadoutSpot.East };
        var nudges = new Dictionary<string, RblNudge> { ["AAL1"] = new RblNudge(ownerSlot, new SKPoint(0f, -4f)) };

        RblReadoutPlacement.PlaceAll([Readout(ReadoutSize, allowNudge: true), second], Obstacles([], [block]), incumbents, nudges);

        Assert.Equal(RblReadoutSpot.East, incumbents[1]);
        if (kept)
        {
            Assert.Equal(new RblNudge(ownerSlot, new SKPoint(0f, -4f)), Assert.Contains("AAL1", nudges));
        }
        else
        {
            Assert.DoesNotContain("AAL1", nudges);
        }
    }

    [Fact]
    public void OffscreenB_ScoresTheClampedSpot()
    {
        // B has left the screen to the right, so the readout anchors on the right edge and East clamps back inside, onto the block.
        var edge = new SKPoint(800f, 300f);
        SKRect clampedEast = RblLabelPlacement.ClampIntoView(RblReadoutPlacement.SpotRect(RblReadoutSpot.East, edge, ReadoutSize), 800f, 600f);
        var readout = new RblReadout(1, edge, ReadoutSize, null, true);

        RblPlacement placement = RblReadoutPlacement.Place(readout, Obstacles([clampedEast], []), null, NoPreviousNudges);

        Assert.NotEqual(RblReadoutSpot.East, placement.Spot);
        Assert.Equal(0f, DatablockDeconfliction.IntersectArea(placement.Rect, clampedEast));
        Assert.True(placement.Rect.Right <= 800f);
    }

    [Fact]
    public void BuildReadouts_PendingLineNeverNudges()
    {
        var viewport = new MapViewport
        {
            CenterLat = 37.0,
            CenterLon = -122.0,
            Zoom = 1.0,
            PixelWidth = 800f,
            PixelHeight = 600f,
        };
        var a = new LatLon(37.0, -122.0);
        var b = new LatLon(37.0, -121.99);
        using var font = new SKFont(SKTypeface.Default, 12f);

        List<RblReadout> readouts = RangeBearingRenderer.BuildReadouts(
            [new ResolvedRbl(1, a, b, "090/0.5/1", false, false)],
            new ResolvedRbl(0, a, b, "090/0.5", false, false),
            viewport,
            font
        );

        Assert.True(Assert.Single(readouts, r => r.Slot == 1).AllowNudge);
        Assert.False(Assert.Single(readouts, r => r.Slot == 0).AllowNudge);
    }
}
