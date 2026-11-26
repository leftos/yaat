using Avalonia;
using Xunit;
using Yaat.Client.Views.Scope.Toolbar.Dcb;

namespace Yaat.Client.Tests.Views.Scope.Toolbar;

/// <summary>The STARS DCB flow: a full or weather tile fills a column of its own, and a half stacks under the previous half.</summary>
public class DcbLayoutTests
{
    private static readonly DcbTileShape Full = DcbTileShape.Full;
    private static readonly DcbTileShape Half = DcbTileShape.Half;
    private static readonly DcbTileShape Weather = DcbTileShape.Weather;

    [Fact]
    public void Full_TakesAColumn()
    {
        IReadOnlyList<Rect> rects = DcbLayout.Flow([Full, Full]);

        Assert.Equal(new Rect(0, 0, 43, 45), rects[0]);
        Assert.Equal(new Rect(43, 0, 43, 45), rects[1]);
    }

    [Fact]
    public void TwoHalves_StackInOneColumn()
    {
        IReadOnlyList<Rect> rects = DcbLayout.Flow([Half, Half]);

        // The second half starts one DIP under the first, and the pair fills the bar's 45 DIPs.
        Assert.Equal(new Rect(0, 0, 43, 22), rects[0]);
        Assert.Equal(new Rect(0, 23, 43, 22), rects[1]);
        Assert.Equal(45, rects[1].Bottom);
    }

    [Fact]
    public void ThreeHalves_ThirdStartsANewColumn()
    {
        IReadOnlyList<Rect> rects = DcbLayout.Flow([Half, Half, Half]);

        Assert.Equal(new Rect(0, 0, 43, 22), rects[0]);
        Assert.Equal(new Rect(0, 23, 43, 22), rects[1]);
        Assert.Equal(new Rect(43, 0, 43, 22), rects[2]);
    }

    [Fact]
    public void Weather_IsNarrow()
    {
        IReadOnlyList<Rect> rects = DcbLayout.Flow([Weather]);

        Assert.Equal(28, rects[0].Width);
        Assert.Equal(45, rects[0].Height);
    }

    [Fact]
    public void MainPageOrder_GivesTheMockWidth()
    {
        // CRC's STARS Main page in CRC order: RANGE, PLACE CNTR, OFF CNTR, RR, PLACE RR, RR CNTR, MAPS, MAP 1-6, WX1-6,
        // BRITE, LDR DIR, LDR, CHAR SIZE, MODE, PREF, SITE, SSA FILTER, GI TEXT FILTER, SHIFT.
        DcbTileShape[] main =
        [
            Full,
            Half,
            Half,
            Full,
            Half,
            Half,
            Full,
            Half,
            Half,
            Half,
            Half,
            Half,
            Half,
            Weather,
            Weather,
            Weather,
            Weather,
            Weather,
            Weather,
            Full,
            Half,
            Half,
            Full,
            Full,
            Full,
            Full,
            Half,
            Half,
            Full,
        ];

        IReadOnlyList<Rect> rects = DcbLayout.Flow(main);

        Assert.Equal(29, rects.Count);
        Assert.Equal(856d, rects.Max(rect => rect.Right));
    }
}
