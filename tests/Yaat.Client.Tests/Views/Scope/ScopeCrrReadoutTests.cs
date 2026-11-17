using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Scope;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// ERAM's range data block (CRC <c>RdbRenderObject</c>, <c>RdbOrientation</c>): the distance text to one decimal and
/// <c>&gt;999.9</c> past it, the group's colour, shown only for an FDB-level ERAM block whose group exists, and its side of the
/// symbol from the ported orientation table.
/// </summary>
public class ScopeCrrReadoutTests
{
    private static readonly EramCrrGroupHubDto GroupA = new("A", "Coral", 37.0, -122.0);

    private static AircraftModel Member(string? group)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Position = new LatLon(37.0 + (12.0 / 60.0), -122.0);
        ac.EramCrrGroup = group;
        return ac;
    }

    [Theory]
    [InlineData(0.44, "0.4")]
    [InlineData(0.0, "0.0")]
    [InlineData(12.36, "12.4")]
    [InlineData(999.9, "999.9")]
    [InlineData(999.94, ">999.9")]
    [InlineData(1500.0, ">999.9")]
    public void DistanceText_IsF1_AndPastMaxReadsOver999(double nm, string text) => Assert.Equal(text, ScopeCrrReadout.DistanceText(nm));

    [Fact]
    public void DistanceText_IgnoresTheCurrentCulture()
    {
        System.Globalization.CultureInfo before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("12.5", ScopeCrrReadout.DistanceText(12.5));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData("Green", 0, 243, 0)]
    [InlineData("Coral", 243, 50, 207)]
    [InlineData("Yellow", 200, 200, 0)]
    [InlineData("White", 243, 243, 243)]
    [InlineData("Mauve", 243, 243, 243)]
    public void ColorOf_IsCrcsCrrColour(string name, byte r, byte g, byte b) => Assert.Equal(new SKColor(r, g, b), ScopeCrrReadout.ColorOf(name));

    [Theory]
    [InlineData(ScopeBlockLevel.Full)]
    [InlineData(ScopeBlockLevel.Dwell)]
    public void Content_AnFdbLevelEramBlockInAnExistingGroup_ShowsItsDistanceInTheGroupColour(ScopeBlockLevel level)
    {
        (string Text, SKColor Color)? content = ScopeCrrReadout.Content(Member("A"), ScopeTemplates.EramId, level, [GroupA]);

        Assert.Equal(("12.0", ScopeCrrReadout.Coral), content);
    }

    [Theory]
    [InlineData(ScopeBlockLevel.Partial)]
    [InlineData(ScopeBlockLevel.Limited)]
    [InlineData(ScopeBlockLevel.Ground)]
    public void Content_AnEramBlockBelowFdbLevel_ShowsNothing(ScopeBlockLevel level) =>
        Assert.Null(ScopeCrrReadout.Content(Member("A"), ScopeTemplates.EramId, level, [GroupA]));

    [Theory]
    [InlineData(ScopeTemplates.StarsId)]
    [InlineData(ScopeTemplates.EuroScopeId)]
    public void Content_ANonEramBlock_ShowsNothing(string templateId) =>
        Assert.Null(ScopeCrrReadout.Content(Member("A"), templateId, ScopeBlockLevel.Full, [GroupA]));

    [Theory]
    [InlineData("B")]
    [InlineData(null)]
    public void Content_NoSuchGroup_ShowsNothing(string? group) =>
        Assert.Null(ScopeCrrReadout.Content(Member(group), ScopeTemplates.EramId, ScopeBlockLevel.Full, [GroupA]));

    [Theory]
    [InlineData(ScopeCompass.N, ScopeRdbOffset.D45, ScopeCompass.SE)]
    [InlineData(ScopeCompass.N, ScopeRdbOffset.D180, ScopeCompass.S)]
    [InlineData(ScopeCompass.NE, ScopeRdbOffset.D180, ScopeCompass.SW)]
    [InlineData(ScopeCompass.NE, ScopeRdbOffset.D315, ScopeCompass.SW)]
    [InlineData(ScopeCompass.E, ScopeRdbOffset.D225, ScopeCompass.NW)]
    [InlineData(ScopeCompass.SE, ScopeRdbOffset.D45, ScopeCompass.NE)]
    [InlineData(ScopeCompass.S, ScopeRdbOffset.D45, ScopeCompass.SW)]
    [InlineData(ScopeCompass.SW, ScopeRdbOffset.D315, ScopeCompass.S)]
    [InlineData(ScopeCompass.W, ScopeRdbOffset.D180, ScopeCompass.E)]
    [InlineData(ScopeCompass.NW, ScopeRdbOffset.D315, ScopeCompass.S)]
    public void Orientation_LeaderLength1_ReadsCrcsTable(ScopeCompass leader, ScopeRdbOffset offset, ScopeCompass side) =>
        Assert.Equal(side, ScopeCrrReadout.Orientation(leader, 1, offset));

    [Theory]
    [InlineData(ScopeCompass.N, ScopeRdbOffset.D45, ScopeCompass.NE)]
    [InlineData(ScopeCompass.NE, ScopeRdbOffset.D315, ScopeCompass.E)]
    [InlineData(ScopeCompass.E, ScopeRdbOffset.D45, ScopeCompass.S)]
    [InlineData(ScopeCompass.S, ScopeRdbOffset.D45, ScopeCompass.S)]
    [InlineData(ScopeCompass.W, ScopeRdbOffset.D180, ScopeCompass.NE)]
    [InlineData(ScopeCompass.NW, ScopeRdbOffset.D315, ScopeCompass.W)]
    public void Orientation_LeaderLength2And3_ReadCrcsSecondTable(ScopeCompass leader, ScopeRdbOffset offset, ScopeCompass side)
    {
        Assert.Equal(side, ScopeCrrReadout.Orientation(leader, 2, offset));
        Assert.Equal(side, ScopeCrrReadout.Orientation(leader, 3, offset));
    }

    [Theory]
    [InlineData(ScopeCompass.SW, ScopeCompass.E)]
    [InlineData(ScopeCompass.W, ScopeCompass.E)]
    [InlineData(ScopeCompass.NW, ScopeCompass.E)]
    [InlineData(ScopeCompass.N, ScopeCompass.W)]
    [InlineData(ScopeCompass.NE, ScopeCompass.W)]
    [InlineData(ScopeCompass.SE, ScopeCompass.W)]
    public void Orientation_LeaderLength0_IsEastOfAWestwardLeader_ElseWest(ScopeCompass leader, ScopeCompass side) =>
        Assert.Equal(side, ScopeCrrReadout.Orientation(leader, 0, ScopeRdbOffset.D90));

    [Theory]
    [InlineData(28f, -28f, ScopeCompass.NE)]
    [InlineData(0f, -30f, ScopeCompass.N)]
    [InlineData(30f, 2f, ScopeCompass.E)]
    [InlineData(20f, 20f, ScopeCompass.SE)]
    [InlineData(-1f, 40f, ScopeCompass.S)]
    [InlineData(-20f, 20f, ScopeCompass.SW)]
    [InlineData(-40f, 0f, ScopeCompass.W)]
    [InlineData(-20f, -20f, ScopeCompass.NW)]
    [InlineData(0f, 0f, ScopeCompass.NE)]
    public void LeaderOf_IsTheNearestCompassPointOfTheBlockOffset(float dx, float dy, ScopeCompass leader) =>
        Assert.Equal(leader, ScopeCrrReadout.LeaderOf(new SKPoint(dx, dy)));

    [Theory]
    [InlineData(22.5, ScopeCompass.NE)]
    [InlineData(67.5, ScopeCompass.E)]
    [InlineData(202.5, ScopeCompass.SW)]
    [InlineData(337.5, ScopeCompass.N)]
    public void LeaderOfBearing_AnExactBoundary_ResolvesClockwise(double bearingDeg, ScopeCompass leader) =>
        Assert.Equal(leader, ScopeCrrReadout.LeaderOfBearing(bearingDeg));

    [Theory]
    [InlineData(ScopeCompass.N, 100f, 72f)]
    [InlineData(ScopeCompass.NE, 104f, 75f)]
    [InlineData(ScopeCompass.E, 107f, 100f)]
    [InlineData(ScopeCompass.SE, 104f, 104f)]
    [InlineData(ScopeCompass.S, 100f, 107f)]
    [InlineData(ScopeCompass.SW, 66f, 104f)]
    [InlineData(ScopeCompass.W, 63f, 100f)]
    [InlineData(ScopeCompass.NW, 66f, 75f)]
    public void BoxAt_SitsOnItsSideOfTheSymbol_AsCrcOffsetsIt(ScopeCompass side, float left, float top)
    {
        // A 30 x 21 box at a symbol at (100, 100): 7 px out on a straight side, 4 px on each axis on a diagonal one.
        SKRect rect = ScopeCrrReadout.BoxAt(new SKPoint(100f, 100f), side, 30f, 21f);

        Assert.Equal(left, rect.Left, 0.01f);
        Assert.Equal(top, rect.Top, 0.01f);
        Assert.Equal(30f, rect.Width, 0.01f);
        Assert.Equal(21f, rect.Height, 0.01f);
    }
}
