using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;

namespace Yaat.Client.Tests.Views;

/// <summary>
/// Pure-function tests for <see cref="DatablockDeconfliction"/> — the shared radar/ground datablock
/// deconfliction helper. Covers both modes, pinning, priority, determinism, stability, and bounds.
/// </summary>
public class DatablockDeconflictionTests
{
    private static readonly SKRect Screen = new(0, 0, 1000, 1000);

    private static DatablockDeconfliction.Item Item(
        string callsign,
        float ax,
        float ay,
        SKPoint preferred,
        bool pinned = false,
        bool priority = false,
        float w = 40,
        float h = 30
    ) =>
        new()
        {
            Callsign = callsign,
            Anchor = new SKPoint(ax, ay),
            RectAtOrigin = new SKRect(0, 0, w, h),
            PreferredOffset = preferred,
            IsPinned = pinned,
            IsPriority = priority,
        };

    private static SKRect ResolvedRect(DatablockDeconfliction.Item item, IReadOnlyDictionary<string, SKPoint> resolved)
    {
        var o = resolved[item.Callsign];
        var r = item.RectAtOrigin;
        return new SKRect(r.Left + item.Anchor.X + o.X, r.Top + item.Anchor.Y + o.Y, r.Right + item.Anchor.X + o.X, r.Bottom + item.Anchor.Y + o.Y);
    }

    private static float Overlap(SKRect a, SKRect b)
    {
        float ix = MathF.Min(a.Right, b.Right) - MathF.Max(a.Left, b.Left);
        float iy = MathF.Min(a.Bottom, b.Bottom) - MathF.Max(a.Top, b.Top);
        return (ix <= 0f || iy <= 0f) ? 0f : ix * iy;
    }

    [Fact]
    public void Resolve_EmptyList_DoesNotThrow()
    {
        var resolved = new Dictionary<string, SKPoint>();
        DatablockDeconfliction.Resolve(
            DatablockDeconflictMode.CompassSnap,
            [],
            DatablockDeconfliction.Options.Default(Screen),
            new Dictionary<string, SKPoint>(),
            resolved
        );
        Assert.Empty(resolved);
    }

    [Fact]
    public void CompassSnap_SingleAircraft_KeepsPreferred()
    {
        var pref = new SKPoint(28, -28);
        var items = new[] { Item("AAL1", 500, 500, pref) };
        var resolved = new Dictionary<string, SKPoint>();

        DatablockDeconfliction.Resolve(
            DatablockDeconflictMode.CompassSnap,
            items,
            DatablockDeconfliction.Options.Default(Screen),
            new Dictionary<string, SKPoint>(),
            resolved
        );

        Assert.Equal(pref, resolved["AAL1"]);
    }

    [Fact]
    public void CompassSnap_TwoOverlapping_Separates()
    {
        var pref = new SKPoint(28, -28);
        var items = new[] { Item("AAL1", 500, 500, pref), Item("UAL2", 500, 500, pref) };
        var resolved = new Dictionary<string, SKPoint>();

        DatablockDeconfliction.Resolve(
            DatablockDeconflictMode.CompassSnap,
            items,
            DatablockDeconfliction.Options.Default(Screen),
            new Dictionary<string, SKPoint>(),
            resolved
        );

        Assert.Equal(0f, Overlap(ResolvedRect(items[0], resolved), ResolvedRect(items[1], resolved)));
    }

    [Fact]
    public void CompassSnap_PinnedNeverMoves_OthersAvoidIt()
    {
        var pref = new SKPoint(28, -28);
        var pinned = Item("AAL1", 500, 500, pref, pinned: true);
        var movable = Item("UAL2", 500, 500, pref);
        var resolved = new Dictionary<string, SKPoint>();

        DatablockDeconfliction.Resolve(
            DatablockDeconflictMode.CompassSnap,
            [pinned, movable],
            DatablockDeconfliction.Options.Default(Screen),
            new Dictionary<string, SKPoint>(),
            resolved
        );

        Assert.Equal(pref, resolved["AAL1"]);
        Assert.Equal(0f, Overlap(ResolvedRect(pinned, resolved), ResolvedRect(movable, resolved)));
    }

    [Fact]
    public void CompassSnap_Deterministic()
    {
        var pref = new SKPoint(28, -28);
        var items = new[] { Item("AAL1", 500, 500, pref), Item("UAL2", 510, 505, pref), Item("DAL3", 495, 498, pref) };

        var first = new Dictionary<string, SKPoint>();
        DatablockDeconfliction.Resolve(
            DatablockDeconflictMode.CompassSnap,
            items,
            DatablockDeconfliction.Options.Default(Screen),
            new Dictionary<string, SKPoint>(),
            first
        );
        var second = new Dictionary<string, SKPoint>();
        DatablockDeconfliction.Resolve(
            DatablockDeconflictMode.CompassSnap,
            items,
            DatablockDeconfliction.Options.Default(Screen),
            new Dictionary<string, SKPoint>(),
            second
        );

        foreach (var key in first.Keys)
        {
            Assert.Equal(first[key], second[key]);
        }
    }

    [Fact]
    public void CompassSnap_StableUnderSmallAnchorPerturbation()
    {
        var pref = new SKPoint(28, -28);
        var options = DatablockDeconfliction.Options.Default(Screen);

        var items = new[] { Item("AAL1", 500, 500, pref), Item("UAL2", 500, 500, pref) };
        var prev = new Dictionary<string, SKPoint>();
        DatablockDeconfliction.Resolve(DatablockDeconflictMode.CompassSnap, items, options, new Dictionary<string, SKPoint>(), prev);

        // Nudge anchors by a fraction of a pixel and feed the prior result back as the stability seed.
        var nudged = new[] { Item("AAL1", 500.3f, 500.2f, pref), Item("UAL2", 500.2f, 500.3f, pref) };
        var next = new Dictionary<string, SKPoint>();
        DatablockDeconfliction.Resolve(DatablockDeconflictMode.CompassSnap, nudged, options, prev, next);

        Assert.Equal(prev["AAL1"], next["AAL1"]);
        Assert.Equal(prev["UAL2"], next["UAL2"]);
    }

    [Fact]
    public void CompassSnap_PriorityAircraftKeepsPreferred()
    {
        var pref = new SKPoint(28, -28);
        var priority = Item("AAL1", 500, 500, pref, priority: true);
        var other = Item("UAL2", 500, 500, pref);
        var resolved = new Dictionary<string, SKPoint>();

        DatablockDeconfliction.Resolve(
            DatablockDeconflictMode.CompassSnap,
            [other, priority],
            DatablockDeconfliction.Options.Default(Screen),
            new Dictionary<string, SKPoint>(),
            resolved
        );

        Assert.Equal(pref, resolved["AAL1"]);
        Assert.Equal(0f, Overlap(ResolvedRect(priority, resolved), ResolvedRect(other, resolved)));
    }

    [Fact]
    public void FreeForm_TwoOverlapping_ReducesOverlap()
    {
        var pref = new SKPoint(28, -28);
        var items = new[] { Item("AAL1", 500, 500, pref), Item("UAL2", 500, 500, pref) };
        var initialOverlap = Overlap(new SKRect(528, 472, 568, 502), new SKRect(528, 472, 568, 502));

        var resolved = new Dictionary<string, SKPoint>();
        DatablockDeconfliction.Resolve(
            DatablockDeconflictMode.FreeForm,
            items,
            DatablockDeconfliction.Options.Default(Screen),
            new Dictionary<string, SKPoint>(),
            resolved
        );

        Assert.True(Overlap(ResolvedRect(items[0], resolved), ResolvedRect(items[1], resolved)) < initialOverlap);
    }

    [Fact]
    public void FreeForm_StaysWithinBounds()
    {
        var smallScreen = new SKRect(0, 0, 120, 120);
        var pref = new SKPoint(28, -28);
        // Anchors clustered near the top-right corner so repulsion would push blocks off-screen if unclamped.
        var items = new[] { Item("AAL1", 110, 15, pref), Item("UAL2", 110, 15, pref), Item("DAL3", 108, 18, pref) };
        var resolved = new Dictionary<string, SKPoint>();

        DatablockDeconfliction.Resolve(
            DatablockDeconflictMode.FreeForm,
            items,
            DatablockDeconfliction.Options.Default(smallScreen),
            new Dictionary<string, SKPoint>(),
            resolved
        );

        foreach (var item in items)
        {
            var rect = ResolvedRect(item, resolved);
            Assert.True(rect.Left >= smallScreen.Left - 0.5f);
            Assert.True(rect.Top >= smallScreen.Top - 0.5f);
            Assert.True(rect.Right <= smallScreen.Right + 0.5f);
            Assert.True(rect.Bottom <= smallScreen.Bottom + 0.5f);
        }
    }

    [Fact]
    public void Off_WritesThroughPreferred()
    {
        var prefA = new SKPoint(28, -28);
        var prefB = new SKPoint(10, 10);
        var items = new[] { Item("AAL1", 500, 500, prefA), Item("UAL2", 500, 500, prefB) };
        var resolved = new Dictionary<string, SKPoint>();

        DatablockDeconfliction.Resolve(
            DatablockDeconflictMode.Off,
            items,
            DatablockDeconfliction.Options.Default(Screen),
            new Dictionary<string, SKPoint>(),
            resolved
        );

        Assert.Equal(prefA, resolved["AAL1"]);
        Assert.Equal(prefB, resolved["UAL2"]);
    }
}
