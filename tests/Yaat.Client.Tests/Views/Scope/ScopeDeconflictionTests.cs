using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The Scope's datablock deconfliction: Off leaves every block at its preferred offset; Snap and Free-form spread
/// overlapping blocks apart, Snap onto the compass positions; a dragged block is pinned and avoided; a hidden block takes no
/// part; the preferred side follows the student's leader direction; every template deconflicts.
/// </summary>
[Collection(MapBlinkClockCollection.Name)]
public class ScopeDeconflictionTests
{
    private const int Width = 1000;
    private const int Height = 800;
    private const float FontSize = 12f;

    // Free-form slides blocks a damped step per build; a pair settles well inside this many.
    private const int FreeFormBuilds = 100;

    private static MapViewport Viewport() =>
        new()
        {
            PixelWidth = Width,
            PixelHeight = Height,
            CenterLat = 37.0,
            CenterLon = -122.0,
        };

    /// <summary>Builds the frame of <paramref name="aircraft"/> through <paramref name="build"/>, with the fonts alive meanwhile.</summary>
    private static T Frame<T>(IReadOnlyList<AircraftModel> aircraft, FrameSetup setup, Func<ScopeFrameInput, T> build)
    {
        using SKFont font = PlatformHelper.MonospaceFontBold(FontSize);
        using var paint = new SKPaint();
        var input = new ScopeFrameInput(
            aircraft,
            setup.Selected,
            Viewport(),
            setup.TemplateId ?? ScopeTemplates.StarsId,
            ScopeTextMetrics.FromStyle(new TextStyle(font, paint)),
            ScopeTestAircraft.AllAlerts,
            ScopeTestAircraft.AllDisplay,
            setup.Offsets ?? new Dictionary<string, SKPoint>(),
            new HashSet<string>(),
            MvaFacilityId: null,
            ScopeProfile.OthersFull,
            VectorMinutes: 0,
            VectorOwnership: ScopeVectorOwnership.All,
            HistoryCount: 0,
            LocalRings: new Dictionary<string, ScopeTrackRings>(),
            IntentPaths: new ScopeIntentPathCache(),
            HiddenCallsigns: setup.Hidden,
            SyncLeaderDirection: setup.SyncLeader
        );
        return build(input);
    }

    private static ScopeFrame Deconflicted(DatablockDeconflictMode mode, IReadOnlyList<AircraftModel> aircraft, FrameSetup setup = default) =>
        Frame(aircraft, setup, input => new ScopeDeconfliction().Build(mode, input));

    private static ScopeFrame Plain(IReadOnlyList<AircraftModel> aircraft, FrameSetup setup = default) => Frame(aircraft, setup, ScopeFrame.Build);

    /// <summary>Two tracks a hair apart, so their default blocks all but cover each other.</summary>
    private static AircraftModel[] OverlappingPair() => OverlappingPair(secondLon: -122.0005);

    private static AircraftModel[] OverlappingPair(double secondLon)
    {
        AircraftModel first = ScopeTestAircraft.CreateModel();
        AircraftModel second = ScopeTestAircraft.CreateModel();
        second.Callsign = "SWA1234";
        second.Position = new LatLon(37.0, secondLon);
        return [first, second];
    }

    private static ScopeTrack TrackOf(ScopeFrame frame, string callsign) => frame.Tracks.Single(t => t.Aircraft.Callsign == callsign);

    private static float Overlap(SKRect a, SKRect b)
    {
        float w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        float h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return ((w > 0) && (h > 0)) ? w * h : 0f;
    }

    private static float PairOverlap(ScopeFrame frame) => Overlap(frame.Tracks[0].Block.Bounds, frame.Tracks[1].Block.Bounds);

    /// <summary>
    /// The offsets Snap may give <paramref name="track"/>'s block: its preferred offset, or one of the eight compass positions
    /// on the base leader ring or an outer one.
    /// </summary>
    private static List<SKPoint> SnapPositions(ScopeTrack track, SKPoint preferred)
    {
        var o = DatablockDeconfliction.Options.Default(new SKRect(0, 0, Width, Height));
        SKPoint origin = track.Symbol + track.BlockOffset;
        SKRect b = track.Block.Bounds;
        var atOrigin = new SKRect(b.Left - origin.X, b.Top - origin.Y, b.Right - origin.X, b.Bottom - origin.Y);
        List<SKPoint> positions = [preferred];
        for (int ring = 0; ring <= o.MaxExtraRings; ring++)
        {
            float gap = o.LeaderGap + (ring * o.LeaderRingStep);
            for (int hx = -1; hx <= 1; hx++)
            {
                for (int hy = -1; hy <= 1; hy++)
                {
                    if ((hx != 0) || (hy != 0))
                    {
                        float x = (hx * (gap + (atOrigin.Width / 2f))) - atOrigin.MidX;
                        float y = (hy * (gap + (atOrigin.Height / 2f))) - atOrigin.MidY;
                        positions.Add(new SKPoint(x, y));
                    }
                }
            }
        }

        return positions;
    }

    [Fact]
    public void ModeOff_LeavesEveryBlockAtItsPreferredOffset()
    {
        AircraftModel[] pair = OverlappingPair();

        ScopeFrame frame = Deconflicted(DatablockDeconflictMode.Off, pair);

        Assert.True(PairOverlap(frame) > 0);
        Assert.All(frame.Tracks, t => Assert.Equal(DatablockText.DefaultOffset, t.BlockOffset));
    }

    [Fact]
    public void Snap_TwoOverlappingBlocks_NoLongerOverlap_EachAtASnapPosition()
    {
        AircraftModel[] pair = OverlappingPair();
        Assert.True(PairOverlap(Plain(pair)) > 0);

        ScopeFrame frame = Deconflicted(DatablockDeconflictMode.CompassSnap, pair);

        Assert.Equal(0f, PairOverlap(frame));
        foreach (ScopeTrack track in frame.Tracks)
        {
            Assert.Contains(
                SnapPositions(track, DatablockText.DefaultOffset),
                p => (Math.Abs(p.X - track.BlockOffset.X) < 0.5f) && (Math.Abs(p.Y - track.BlockOffset.Y) < 0.5f)
            );
        }
    }

    [Fact]
    public void FreeForm_TwoOverlappingBlocks_PartOneAboveTheOther()
    {
        // 30 px apart: past the ~28 px (block width less twice its height) under which the shared engine's pair settles
        // crossed with a sliver.
        AircraftModel[] pair = OverlappingPair(secondLon: -122.0075);
        Assert.True(PairOverlap(Plain(pair)) > 0);
        var deconfliction = new ScopeDeconfliction();

        ScopeFrame frame = Frame(
            pair,
            default,
            input =>
            {
                ScopeFrame built = deconfliction.Build(DatablockDeconflictMode.FreeForm, input, step: true);
                for (int i = 1; i < FreeFormBuilds; i++)
                {
                    built = deconfliction.Build(DatablockDeconflictMode.FreeForm, input, step: true);
                }

                return built;
            }
        );

        Assert.Equal(0f, PairOverlap(frame));
        Assert.Equal(DatablockText.DefaultOffset.Y, TrackOf(frame, "SWA1234").BlockOffset.Y, 0.5);
        Assert.True(TrackOf(frame, pair[0].Callsign).BlockOffset.Y > (DatablockText.DefaultOffset.Y + 30));
    }

    [Fact]
    public void FreeForm_BuildsThatDoNotStep_LeaveTheResolvedOffsetsUnchanged()
    {
        AircraftModel[] pair = OverlappingPair();
        var deconfliction = new ScopeDeconfliction();

        (ScopeFrame stepped, List<ScopeFrame> reused, Dictionary<string, SKPoint> after) = Frame(
            pair,
            default,
            input =>
            {
                ScopeFrame first = deconfliction.Build(DatablockDeconflictMode.FreeForm, input, step: true);
                var before = new Dictionary<string, SKPoint>(deconfliction.ResolvedOffsets);
                List<ScopeFrame> frames =
                [
                    .. Enumerable.Range(0, 3).Select(_ => deconfliction.Build(DatablockDeconflictMode.FreeForm, input, step: false)),
                ];
                Assert.Equal(before, deconfliction.ResolvedOffsets);
                return (first, frames, before);
            }
        );

        Assert.NotEmpty(after);
        foreach (ScopeFrame frame in reused)
        {
            Assert.All(frame.Tracks, t => Assert.Equal(TrackOf(stepped, t.Aircraft.Callsign).Block.Bounds, t.Block.Bounds));
        }
    }

    [Fact]
    public void FreeForm_OneRender_StepsOnce_WhateverBuildsCameBetween()
    {
        AircraftModel[] pair = OverlappingPair();
        var withHovers = new ScopeDeconfliction();
        var rendersOnly = new ScopeDeconfliction();

        Frame(
            pair,
            default,
            input =>
            {
                withHovers.Build(DatablockDeconflictMode.FreeForm, input, step: true);
                for (int i = 0; i < 3; i++)
                {
                    withHovers.Build(DatablockDeconflictMode.FreeForm, input, step: false);
                }

                withHovers.Build(DatablockDeconflictMode.FreeForm, input, step: true);
                rendersOnly.Build(DatablockDeconflictMode.FreeForm, input, step: true);
                return rendersOnly.Build(DatablockDeconflictMode.FreeForm, input, step: true);
            }
        );

        Assert.Equal(rendersOnly.ResolvedOffsets, withHovers.ResolvedOffsets);
    }

    [Fact]
    public void ADraggedBlock_StaysPut_AndTheOtherMoves()
    {
        AircraftModel[] pair = OverlappingPair();
        SKPoint manual = DatablockText.DefaultOffset;
        var offsets = new Dictionary<string, SKPoint> { [pair[0].Callsign] = manual };

        ScopeFrame frame = Deconflicted(DatablockDeconflictMode.CompassSnap, pair, new FrameSetup(Offsets: offsets));

        Assert.Equal(manual, TrackOf(frame, pair[0].Callsign).BlockOffset);
        Assert.NotEqual(DatablockText.DefaultOffset, TrackOf(frame, pair[1].Callsign).BlockOffset);
        Assert.Equal(0f, PairOverlap(frame));
    }

    [Fact]
    public void AHiddenBlock_IsNotMoved_AndIsNoObstacle()
    {
        AircraftModel[] pair = OverlappingPair();
        HashSet<string> hidden = [pair[0].Callsign];

        ScopeFrame frame = Deconflicted(DatablockDeconflictMode.CompassSnap, pair, new FrameSetup(Hidden: hidden));

        ScopeTrack hiddenTrack = TrackOf(frame, pair[0].Callsign);
        Assert.Empty(hiddenTrack.Block.Fields);
        Assert.Equal(DatablockText.DefaultOffset, hiddenTrack.BlockOffset);
        Assert.Equal(DatablockText.DefaultOffset, TrackOf(frame, pair[1].Callsign).BlockOffset);
    }

    [Fact]
    public void LeaderSyncOn_ThePreferredSideFollowsTheStudentsLeaderDirection()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.StudentLeaderDirection = 8;

        ScopeFrame frame = Deconflicted(DatablockDeconflictMode.CompassSnap, [ac], new FrameSetup(SyncLeader: true));

        // North: the block centres over the symbol, where the default offset would put it up and to the right.
        ScopeTrack track = Assert.Single(frame.Tracks);
        Assert.Equal(track.Symbol.X, track.Block.Bounds.MidX, 0.5);
        Assert.True(track.Block.Bounds.Bottom < track.Symbol.Y);
    }

    [Theory]
    [InlineData(ScopeTemplates.StarsId)]
    [InlineData(ScopeTemplates.EramId)]
    [InlineData(ScopeTemplates.MixedId)]
    public void EachTemplate_Deconflicts(string templateId)
    {
        AircraftModel[] pair = OverlappingPair();
        var setup = new FrameSetup(TemplateId: templateId);
        Assert.True(PairOverlap(Plain(pair, setup)) > 0);

        ScopeFrame frame = Deconflicted(DatablockDeconflictMode.CompassSnap, pair, setup);

        Assert.Equal(0f, PairOverlap(frame));
    }

    [Fact]
    public void BuildItems_PinsADraggedBlock_PrioritisesTheSelectedOne_AndLeavesAHiddenOneOut()
    {
        AircraftModel[] pair = OverlappingPair();
        AircraftModel third = ScopeTestAircraft.CreateModel();
        third.Callsign = "DAL5";
        var offsets = new Dictionary<string, SKPoint> { [pair[0].Callsign] = new SKPoint(-60, 12) };
        var setup = new FrameSetup(Offsets: offsets, Selected: pair[1], Hidden: new HashSet<string> { third.Callsign });

        List<DatablockDeconfliction.Item> items = Frame(
            [.. pair, third],
            setup,
            input => ScopeDeconfliction.BuildItems(ScopeFrame.Build(input), input)
        );

        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, i => i.Callsign == third.Callsign);
        DatablockDeconfliction.Item dragged = items.Single(i => i.Callsign == pair[0].Callsign);
        Assert.True(dragged.IsPinned);
        Assert.False(dragged.IsPriority);
        Assert.Equal(new SKPoint(-60, 12), dragged.PreferredOffset);
        DatablockDeconfliction.Item selected = items.Single(i => i.Callsign == pair[1].Callsign);
        Assert.False(selected.IsPinned);
        Assert.True(selected.IsPriority);
        Assert.Equal(DatablockText.DefaultOffset, selected.PreferredOffset);
    }

    [Theory]
    [InlineData("off", DatablockDeconflictMode.Off)]
    [InlineData("SNAP", DatablockDeconflictMode.CompassSnap)]
    [InlineData("Free", DatablockDeconflictMode.FreeForm)]
    public void ModeNames_ParseCaseInsensitively_AndRoundTrip(string name, DatablockDeconflictMode mode)
    {
        Assert.Equal(mode, ScopeDeconfliction.ParseMode(name));
        Assert.Equal(name.ToLowerInvariant(), ScopeDeconfliction.NameOf(mode));
        Assert.Null(ScopeDeconfliction.ParseMode("compass"));
    }
}
