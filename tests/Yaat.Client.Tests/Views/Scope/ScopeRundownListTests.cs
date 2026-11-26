using SkiaSharp;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The Radar view's RUNDOWN overlay: a title then one line per row at the canvas's top-left, each row in its colour with a
/// blinking row's off phase at a quarter brightness, and a hit test that finds only a row line's text box.
/// </summary>
public class ScopeRundownListTests
{
    private static readonly ScopeTextMetrics Metrics = new(s => s.Length * 7f, 12f, 14f);

    private static IReadOnlyList<RundownRow> Rows(params CoordinationItemHubDto[] items) => RundownRows.Build([List("PFAT", items)], 100);

    private static IReadOnlyList<RundownRow> TwoRows() => Rows(Item("Unsent", null, "POAK-1"), Item("Unacknowledged", null, "POAK-2"));

    private static SKColor RowColor(string status, double? expiresAt, bool flashOn) =>
        ScopeRundownList.Layout(Rows(Item(status, expiresAt)), Metrics, flashOn, ScopeRundownList.DefaultOrigin).Lines[1].Color;

    [Fact]
    public void Layout_NoRows_DrawsNothing()
    {
        var list = ScopeRundownList.Layout([], Metrics, flashOn: true, ScopeRundownList.DefaultOrigin);

        Assert.Empty(list.Lines);
        Assert.Null(list.RowAt(10, 12));
    }

    [Fact]
    public void Layout_OneRow_TitleThenRow()
    {
        IReadOnlyList<RundownRow> rows = Rows(Item("Unsent", null));

        var list = ScopeRundownList.Layout(rows, Metrics, flashOn: true, ScopeRundownList.DefaultOrigin);

        Assert.Equal(2, list.Lines.Count);
        Assert.Equal("RUNDOWN", list.Lines[0].Text);
        Assert.Null(list.Lines[0].Row);
        Assert.Equal(SKColors.LimeGreen, list.Lines[0].Color);
        Assert.Same(rows[0], list.Lines[1].Row);
        Assert.Equal(rows[0].Text, list.Lines[1].Text);
    }

    [Fact]
    public void Layout_PlacesLinesTopLeft()
    {
        var list = ScopeRundownList.Layout(TwoRows(), Metrics, flashOn: true, ScopeRundownList.DefaultOrigin);

        Assert.Equal(new SKRect(8, 190, 8 + (7 * 7), 190 + 14), list.Lines[0].Rect);
        Assert.Equal(202f, list.Lines[0].Baseline);
        Assert.Equal(216f, list.Lines[1].Baseline);
        Assert.Equal(204f, list.Lines[1].Rect.Top);
        Assert.Equal(230f, list.Lines[2].Baseline);
    }

    [Fact]
    public void Layout_AtACustomOrigin_ShiftsEveryLine()
    {
        var list = ScopeRundownList.Layout(TwoRows(), Metrics, flashOn: true, new SKPoint(300, 40));

        Assert.Equal(300f, list.Lines[0].Rect.Left);
        Assert.Equal(40f, list.Lines[0].Rect.Top);
        Assert.Equal(54f, list.Lines[1].Rect.Top);
    }

    [Fact]
    public void Contains_TheTitle_IsInside()
    {
        var list = ScopeRundownList.Layout(TwoRows(), Metrics, flashOn: true, ScopeRundownList.DefaultOrigin);
        SKRect rect = list.Lines[0].Rect;

        Assert.True(list.Contains(rect.MidX, rect.MidY));
    }

    [Fact]
    public void Contains_PastTheWidestLine_IsOutside()
    {
        var list = ScopeRundownList.Layout(TwoRows(), Metrics, flashOn: true, ScopeRundownList.DefaultOrigin);
        float widest = list.Lines.Max(line => line.Rect.Right);

        Assert.False(list.Contains(widest + 1, list.Lines[1].Rect.MidY));
    }

    [Fact]
    public void Contains_WithNoRows_IsFalse()
    {
        var list = ScopeRundownList.Layout([], Metrics, flashOn: true, ScopeRundownList.DefaultOrigin);
        SKPoint origin = ScopeRundownList.DefaultOrigin;

        Assert.False(list.Contains(origin.X + 1, origin.Y + 1));
    }

    [Fact]
    public void Layout_CautionRow_IsYellow() => Assert.Equal(SKColors.Yellow, RowColor("Acknowledged", 225, flashOn: false));

    [Fact]
    public void Layout_BlinkingRow_OnPhase_FullColor() => Assert.Equal(SKColors.LimeGreen, RowColor("Unacknowledged", null, flashOn: true));

    [Fact]
    public void Layout_BlinkingRow_OffPhase_QuarterBrightness() =>
        Assert.Equal(ScopeBrightness.Apply(SKColors.LimeGreen, 25), RowColor("Unacknowledged", null, flashOn: false));

    [Fact]
    public void Layout_BlinkingCaution_OffPhase_QuarterBrightness() =>
        Assert.Equal(ScopeBrightness.Apply(SKColors.Yellow, 25), RowColor("DepartureExpirationWarning", 225, flashOn: false));

    [Fact]
    public void Layout_SteadyRow_OffPhase_Unchanged() => Assert.Equal(SKColors.LimeGreen, RowColor("Unsent", null, flashOn: false));

    [Fact]
    public void RowAt_InsideARowLine_GivesThatRow()
    {
        IReadOnlyList<RundownRow> rows = TwoRows();
        var list = ScopeRundownList.Layout(rows, Metrics, flashOn: true, ScopeRundownList.DefaultOrigin);
        SKRect rect = list.Lines[2].Rect;

        Assert.Same(rows[1], list.RowAt(rect.MidX, rect.MidY));
    }

    [Fact]
    public void RowAt_OnTheTitle_GivesNull()
    {
        var list = ScopeRundownList.Layout(TwoRows(), Metrics, flashOn: true, ScopeRundownList.DefaultOrigin);
        SKRect rect = list.Lines[0].Rect;

        Assert.Null(list.RowAt(rect.MidX, rect.MidY));
    }

    [Fact]
    public void RowAt_PastTheTextWidth_GivesNull()
    {
        var list = ScopeRundownList.Layout(TwoRows(), Metrics, flashOn: true, ScopeRundownList.DefaultOrigin);
        SKRect rect = list.Lines[1].Rect;

        Assert.Null(list.RowAt(rect.Right + 1, rect.MidY));
    }

    [Fact]
    public void FrameBuild_CarriesTheRundown()
    {
        ScopeFrame frame = BuildFrame(origin: null);

        Assert.Equal(2, frame.Rundown.Lines.Count);
    }

    [Fact]
    public void FrameBuild_UsesTheInputsOrigin()
    {
        ScopeFrame frame = BuildFrame(new SKPoint(100, 100));

        Assert.Equal(100f, frame.Rundown.Lines[0].Rect.Top);
    }

    private static ScopeFrame BuildFrame(SKPoint? origin)
    {
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        var input = new ScopeFrameInput(
            [],
            null,
            new MapViewport
            {
                PixelWidth = 1000,
                PixelHeight = 800,
                CenterLat = 37.0,
                CenterLon = -122.0,
            },
            ScopeTemplates.StarsId,
            ScopeTextMetrics.FromStyle(new TextStyle(font, paint)),
            ScopeTestAircraft.AllAlerts,
            ScopeTestAircraft.AllDisplay,
            new Dictionary<string, SKPoint>(),
            new HashSet<string>(),
            MvaFacilityId: null,
            ScopeProfile.OthersFull,
            VectorMinutes: 0,
            VectorOwnership: ScopeVectorOwnership.All,
            HistoryCount: 0,
            LocalRings: new Dictionary<string, ScopeTrackRings>(),
            IntentPaths: new ScopeIntentPathCache(),
            RundownRows: Rows(Item("Unsent", null)),
            RundownOrigin: origin
        );

        return ScopeFrame.Build(input);
    }

    private static CoordinationItemHubDto Item(string status, double? expiresAt, string id = "POAK-1") =>
        new(
            Id: id,
            Callsign: "SWA123",
            Status: status,
            LineNumber: 7,
            AircraftType: "B738",
            Beacon: "0425",
            Category: "F",
            ExitFix: "SJC",
            Message: "RWY 30",
            OriginTcp: "3O",
            ExpireElapsedSeconds: expiresAt
        );

    private static CoordinationListHubDto List(string listId, params CoordinationItemHubDto[] items) =>
        new(ListId: listId, Title: "Oakland departure releases", AutoAcknowledge: false, SenderTcps: ["3O"], ReceiverTcps: ["4O"], Items: [.. items]);
}
