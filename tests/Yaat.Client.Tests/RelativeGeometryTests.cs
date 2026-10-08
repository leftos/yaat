using Xunit;
using Yaat.Client.ContextMenus;

namespace Yaat.Client.Tests;

/// <summary>
/// Tests for <see cref="RelativeGeometry"/>: the clock position, distance and altitude difference the context menu's For
/// section and traffic list name another aircraft by, and their wording.
/// </summary>
public class RelativeGeometryTests
{
    [Fact]
    public void ClockPosition_AheadIsTwelve()
    {
        Assert.Equal(12, RelativeGeometry.ClockPosition(90, 90));
        Assert.Equal(12, RelativeGeometry.ClockPosition(90, 104));
        Assert.Equal(12, RelativeGeometry.ClockPosition(90, 76));
    }

    [Fact]
    public void ClockPosition_HoursAroundTheDial()
    {
        Assert.Equal(5, RelativeGeometry.ClockPosition(0, 150));
        Assert.Equal(5, RelativeGeometry.ClockPosition(90, 240));
        Assert.Equal(3, RelativeGeometry.ClockPosition(0, 90));
        Assert.Equal(6, RelativeGeometry.ClockPosition(0, 180));
        Assert.Equal(9, RelativeGeometry.ClockPosition(0, 270));
    }

    [Fact]
    public void ClockPosition_WrapsAtNorth()
    {
        Assert.Equal(1, RelativeGeometry.ClockPosition(350, 20));
        Assert.Equal(11, RelativeGeometry.ClockPosition(10, 340));
        Assert.Equal(12, RelativeGeometry.ClockPosition(0, 359));
        Assert.Equal(12, RelativeGeometry.ClockPosition(359, 1));
    }

    [Fact]
    public void AltitudeDelta_BelowAboveAndLevel()
    {
        Assert.Equal(-1500, RelativeGeometry.AltitudeDelta(3000, 1500));
        Assert.Equal(500, RelativeGeometry.AltitudeDelta(1000, 1520));
        Assert.Equal(4000, RelativeGeometry.AltitudeDelta(2000, 5960));
        Assert.Equal(0, RelativeGeometry.AltitudeDelta(5000, 5060));
        Assert.Equal(0, RelativeGeometry.AltitudeDelta(5000, 4901));
        Assert.Equal(-100, RelativeGeometry.AltitudeDelta(5000, 4900));

        Assert.Equal("1,500 ft below", RelativeGeometry.AltitudeDeltaLine(-1500));
        Assert.Equal("500 ft above", RelativeGeometry.AltitudeDeltaLine(500));
        Assert.Equal("same altitude", RelativeGeometry.AltitudeDeltaLine(0));
        Assert.Equal("1,500 below", RelativeGeometry.AltitudeDeltaColumn(-1500));
        Assert.Equal("4,000 above", RelativeGeometry.AltitudeDeltaColumn(4000));
        Assert.Equal("same altitude", RelativeGeometry.AltitudeDeltaColumn(0));
    }

    [Fact]
    public void WholeNm_RoundsHalfAwayFromZero()
    {
        Assert.Equal(4, RelativeGeometry.WholeNm(3.5));
        Assert.Equal(3, RelativeGeometry.WholeNm(3.49));
        Assert.Equal(0, RelativeGeometry.WholeNm(0.4));
    }

    [Fact]
    public void Describe_FormatsTheMockLine()
    {
        Assert.Equal("AAL601 is at its 2 o'clock, 4 nm, 1,500 ft below", RelativeGeometry.Describe("AAL601", 2, 4, -1500));
        Assert.Equal("N1 is at its 12 o'clock, 1 nm, same altitude", RelativeGeometry.Describe("N1", 12, 1, 0));
    }
}
