using Avalonia;
using Avalonia.Layout;
using Xunit;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The corner placement a dropped floating view card anchors to, and a toolbar button's text name.
/// </summary>
public class ScopeTearoffTests
{
    private static readonly Size Area = new(1000, 800);

    [Fact]
    public void Nearest_TopLeftQuadrant_AnchorsTopLeft() =>
        Assert.Equal(new ScopePlacement(ScopeCorner.TopLeft, 10, 15), ScopePlacement.Nearest(new Rect(10, 15, 30, 20), Area));

    [Fact]
    public void Nearest_TopRightQuadrant_AnchorsTopRight() =>
        Assert.Equal(new ScopePlacement(ScopeCorner.TopRight, 70, 15), ScopePlacement.Nearest(new Rect(900, 15, 30, 20), Area));

    [Fact]
    public void Nearest_BottomLeftQuadrant_AnchorsBottomLeft() =>
        Assert.Equal(new ScopePlacement(ScopeCorner.BottomLeft, 10, 80), ScopePlacement.Nearest(new Rect(10, 700, 30, 20), Area));

    [Fact]
    public void Nearest_BottomRightQuadrant_AnchorsBottomRight() =>
        Assert.Equal(new ScopePlacement(ScopeCorner.BottomRight, 70, 80), ScopePlacement.Nearest(new Rect(900, 700, 30, 20), Area));

    [Fact]
    public void Nearest_CentreOnTheVerticalMidline_GoesLeft() =>
        Assert.Equal(new ScopePlacement(ScopeCorner.TopLeft, 450, 15), ScopePlacement.Nearest(new Rect(450, 15, 100, 20), Area));

    [Fact]
    public void Nearest_CentreOnTheHorizontalMidline_GoesTop() =>
        Assert.Equal(new ScopePlacement(ScopeCorner.TopLeft, 10, 390), ScopePlacement.Nearest(new Rect(10, 390, 30, 20), Area));

    [Fact]
    public void Nearest_RectPartlyOutsideTheArea_ClampsTheDistancesToZero()
    {
        Assert.Equal(new ScopePlacement(ScopeCorner.TopLeft, 0, 0), ScopePlacement.Nearest(new Rect(-5, -7, 30, 20), Area));
        Assert.Equal(new ScopePlacement(ScopeCorner.BottomRight, 0, 0), ScopePlacement.Nearest(new Rect(980, 790, 30, 20), Area));
    }

    [Fact]
    public void Nearest_RoundsTheDistancesToWholePixels() =>
        Assert.Equal(new ScopePlacement(ScopeCorner.TopLeft, 10, 16), ScopePlacement.Nearest(new Rect(10.4, 15.6, 30, 20), Area));

    public static TheoryData<ScopeCorner, HorizontalAlignment, VerticalAlignment, Thickness> CornerSides =>
        new()
        {
            { ScopeCorner.TopLeft, HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(7, 9, 0, 0) },
            { ScopeCorner.TopRight, HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, 9, 7, 0) },
            { ScopeCorner.BottomLeft, HorizontalAlignment.Left, VerticalAlignment.Bottom, new Thickness(7, 0, 0, 9) },
            { ScopeCorner.BottomRight, HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0, 0, 7, 9) },
        };

    [Theory]
    [MemberData(nameof(CornerSides))]
    public void ToMarginAndTheAlignments_PutTheDistancesOnTheCornersSides(
        ScopeCorner corner,
        HorizontalAlignment horizontal,
        VerticalAlignment vertical,
        Thickness margin
    )
    {
        var place = new ScopePlacement(corner, 7, 9);

        Assert.Equal(margin, place.ToMargin());
        Assert.Equal(horizontal, place.HorizontalAlignment);
        Assert.Equal(vertical, place.VerticalAlignment);
    }

    [Theory]
    [InlineData(ScopeButtonId.Range, "range")]
    [InlineData(ScopeButtonId.Brite, "brite")]
    [InlineData(ScopeButtonId.BriteMapA, "britemapa")]
    [InlineData(ScopeButtonId.Db4thLine, "db4thline")]
    [InlineData(ScopeButtonId.TemplateStars, "templatestars")]
    public void NameOf_IsTheIdLowercased(ScopeButtonId id, string expected) =>
        Assert.Equal(expected, ScopeToolbarButtons.NameOf(ScopeToolbarButtons.Find(id)!));
}
