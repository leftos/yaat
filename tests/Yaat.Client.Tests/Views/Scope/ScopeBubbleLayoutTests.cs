using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// Pins where <see cref="ScopeBubbles"/> places a speech bubble: its wrap, the anchor under the block it belongs to (the symbol
/// of a hidden block, the moved block of a dragged one) and the two switches that draw none.
/// </summary>
[Collection(MapBlinkClockCollection.Name)]
public class ScopeBubbleLayoutTests
{
    private const float FontSize = 12f;

    private static readonly ScopeBubbleOptions Shown = new(Show: true, AlwaysShowGround: false);

    private static MapViewport Viewport =>
        new()
        {
            PixelWidth = 1000,
            PixelHeight = 800,
            CenterLat = 37.0,
            CenterLon = -122.0,
        };

    private static AircraftSpeechBubble Bubble(string text) => new(text, DateTime.UtcNow.AddMinutes(1), SpeechBubbleSeverity.Speech);

    private static ScopeFrame Frame(SKPoint? offset = null, bool hidden = false)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.SpeechBubble = Bubble("CLEARED FOR TAKEOFF RUNWAY 30");
        using SKFont font = PlatformHelper.MonospaceFontBold(FontSize);
        using var paint = new SKPaint();
        var metrics = ScopeTextMetrics.FromStyle(new TextStyle(font, paint));
        var offsets = new Dictionary<string, SKPoint>();
        if (offset is { } moved)
        {
            offsets[ac.Callsign] = moved;
        }

        var input = new ScopeFrameInput(
            [ac],
            null,
            Viewport,
            ScopeTemplates.StarsId,
            metrics,
            ScopeTestAircraft.AllAlerts,
            ScopeTestAircraft.AllDisplay,
            offsets,
            new HashSet<string>(),
            MvaFacilityId: null,
            OthersLevel: ScopeProfile.OthersFull,
            VectorMinutes: 0,
            VectorOwnership: ScopeVectorOwnership.Own,
            HistoryCount: 0,
            LocalRings: new Dictionary<string, ScopeTrackRings>(),
            IntentPaths: new ScopeIntentPathCache(),
            HiddenCallsigns: hidden ? new HashSet<string>(StringComparer.Ordinal) { ac.Callsign } : null
        );
        return ScopeFrame.Build(input);
    }

    private static SKFont MeasureFont() => PlatformHelper.MonospaceFontBold(FontSize);

    private static string Repeat(char value, int count) => new(value, count);

    private static ScopeBubble PlaceOne(ScopeFrame frame)
    {
        using SKFont font = MeasureFont();
        return Assert.Single(ScopeBubbles.Place(frame.Tracks, Shown, DateTime.UtcNow, font));
    }

    [Fact]
    public void Wrap_BreaksAtThirtySixChars_AndMarksTheOverflowWithAnEllipsis()
    {
        string text = string.Join(' ', Enumerable.Repeat("REQUEST", 16));

        IReadOnlyList<string> lines = ScopeBubbles.Wrap(text);

        Assert.Equal(3, lines.Count);
        Assert.All(lines.Take(2), line => Assert.True(line.Length <= ScopeBubbles.MaxLineChars));
        Assert.EndsWith("…", lines[2], StringComparison.Ordinal);
    }

    /// <summary>The wrap's rows: the text a bubble carries, then the exact lines it shows for it.</summary>
    public static TheoryData<string, string[]> WrapCases =>
        new()
        {
            { $"{Repeat('A', 36)} {Repeat('B', 36)} C {Repeat('D', 36)}", [Repeat('A', 36), Repeat('B', 36), "C…"] },
            { Repeat('A', 40), [Repeat('A', 36), Repeat('A', 4)] },
            { $"{Repeat('A', 36)} {Repeat('B', 36)} C {Repeat('D', 40)}", [Repeat('A', 36), Repeat('B', 36), "C…"] },
            { Repeat('A', 36), [Repeat('A', 36)] },
            { Repeat('A', 108), [Repeat('A', 36), Repeat('A', 36), Repeat('A', 36)] },
            { Repeat('A', 109), [Repeat('A', 36), Repeat('A', 36), Repeat('A', 35) + "…"] },
            { "hello world", ["hello world"] },
            { "", [] },
        };

    [Theory]
    [MemberData(nameof(WrapCases))]
    public void Wrap_BreaksWordsAndLinesAtTheBubbleLimits(string text, string[] expected) => Assert.Equal(expected, ScopeBubbles.Wrap(text));

    [Fact]
    public void Bubble_AnchorsBelowTheBlock()
    {
        ScopeFrame frame = Frame();
        ScopeTrack track = Assert.Single(frame.Tracks);

        ScopeBubble bubble = PlaceOne(frame);

        Assert.Equal(track.Aircraft.Callsign, bubble.Callsign);
        Assert.Equal(track.Block.Bounds.Left, bubble.Rect.Left, 3);
        Assert.Equal(track.Block.Bounds.Bottom + ScopeBubbles.Gap, bubble.Rect.Top, 3);
        Assert.True(bubble.Rect.Bottom > bubble.Rect.Top);
    }

    [Fact]
    public void Bubble_HiddenBlock_AnchorsAtTheSymbol()
    {
        ScopeFrame frame = Frame(hidden: true);
        ScopeTrack track = Assert.Single(frame.Tracks);
        Assert.Empty(track.Block.Fields);

        ScopeBubble bubble = PlaceOne(frame);

        Assert.Equal(track.Symbol.X - ScopeRenderer.SymbolRadius, bubble.Rect.Left, 3);
        Assert.Equal(track.Symbol.Y + ScopeRenderer.SymbolRadius + ScopeBubbles.Gap, bubble.Rect.Top, 3);
    }

    [Fact]
    public void Bubble_DraggedBlock_FollowsTheBlock()
    {
        ScopeFrame frame = Frame(offset: new SKPoint(60, 40));
        ScopeTrack track = Assert.Single(frame.Tracks);
        Assert.NotEqual(track.Symbol.X, track.Block.Bounds.Left, 3);

        ScopeBubble bubble = PlaceOne(frame);

        Assert.Equal(track.Block.Bounds.Left, bubble.Rect.Left, 3);
        Assert.Equal(track.Block.Bounds.Bottom + ScopeBubbles.Gap, bubble.Rect.Top, 3);
    }

    [Fact]
    public void Bubble_Expired_YieldsNone()
    {
        ScopeFrame frame = Frame();
        using SKFont font = MeasureFont();

        Assert.Empty(ScopeBubbles.Place(frame.Tracks, Shown, DateTime.UtcNow.AddMinutes(5), font));
    }

    [Fact]
    public void Bubble_SpeechBubblesOff_YieldsNone()
    {
        ScopeFrame frame = Frame();
        using SKFont font = MeasureFont();

        Assert.Empty(ScopeBubbles.Place(frame.Tracks, new ScopeBubbleOptions(Show: false, AlwaysShowGround: false), DateTime.UtcNow, font));
    }
}
