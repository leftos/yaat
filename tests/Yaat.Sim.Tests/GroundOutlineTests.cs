using Xunit;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// The outline clearance the ground conflict detector measures tug moves with: three-segment crosses (fuselage, wing,
/// tailplane) in flat feet. Every case uses a round 100 ft × 100 ft aircraft so the expected distances can be read off
/// the geometry: the tail sits 50 ft behind the reference point and the tailplane spans 20 ft either side of it.
/// </summary>
public class GroundOutlineTests
{
    private const double Tolerance = 1e-6;

    private static readonly GroundOutlineSize Square = new(100.0, 100.0, 0.0);

    public GroundOutlineTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static GroundOutline At(double eastFt, double northFt, double noseDeg, GroundOutlineSize size) =>
        GroundOutline.At(new OutlinePoint(eastFt, northFt), noseDeg, size);

    [Fact]
    public void ParallelCrossesSideBySide_ClearanceIsTheGapBetweenTheWingtips()
    {
        GroundOutline left = At(0.0, 0.0, 0.0, Square);
        GroundOutline right = At(130.0, 0.0, 0.0, Square);

        Assert.Equal(30.0, GroundOutline.Clearance(left, right), Tolerance);
        Assert.Equal(30.0, GroundOutline.Clearance(right, left), Tolerance);
    }

    [Fact]
    public void ParallelCrossesInTrail_ClearanceIsTheGapFromNoseToTail()
    {
        GroundOutline lead = At(0.0, 125.0, 0.0, Square);
        GroundOutline trailer = At(0.0, 0.0, 0.0, Square);

        Assert.Equal(25.0, GroundOutline.Clearance(lead, trailer), Tolerance);
    }

    [Fact]
    public void CrossingFuselages_ClearanceIsZero()
    {
        GroundOutline northbound = At(0.0, 0.0, 0.0, Square);
        GroundOutline eastbound = At(0.0, 10.0, 90.0, Square);

        Assert.Equal(0.0, GroundOutline.Clearance(northbound, eastbound));
    }

    /// <summary>
    /// An eastbound aircraft whose tail sits 1 ft beyond the tip of a northbound aircraft's tailplane: the tailplane is
    /// the only part that close (the wings and fuselages are all 21 ft or more apart), so the clearance is 1 ft, and
    /// 0 once the tail reaches the tip.
    /// </summary>
    [Fact]
    public void TailplaneOnlyContact_IsMeasuredFromTheTailplaneTip()
    {
        GroundOutline northbound = At(0.0, 0.0, 0.0, Square);
        GroundOutline justClear = At(71.0, -50.0, 90.0, Square);
        GroundOutline touching = At(70.0, -50.0, 90.0, Square);

        Assert.Equal(1.0, GroundOutline.Clearance(northbound, justClear), Tolerance);
        Assert.Equal(0.0, GroundOutline.Clearance(northbound, touching), Tolerance);
    }

    /// <summary>
    /// Two northbound aircraft in trail 20 ft nose-to-tail: pulled, the trailer's tug reaches 30 ft past its nose and
    /// into the aircraft ahead.
    /// </summary>
    [Fact]
    public void PullExtendsTheFuselageForTheTug()
    {
        GroundOutline ahead = At(0.0, 120.0, 0.0, Square);
        GroundOutline pushed = At(0.0, 0.0, 0.0, Square);
        GroundOutline pulled = At(0.0, 0.0, 0.0, Square with { NoseLeadFt = GroundOutline.TugLeadFt });

        Assert.Equal(20.0, GroundOutline.Clearance(pushed, ahead), Tolerance);
        Assert.Equal(0.0, GroundOutline.Clearance(pulled, ahead), Tolerance);
        Assert.Equal(80.0, pulled.Fuselage.B.NorthFt, Tolerance);
    }

    [Fact]
    public void SizeOf_AddsTheTugLeadOnlyOnAPull_AndReachCoversIt()
    {
        var parked = GroundOutlineSize.Of("B738", towedNoseFirst: false);
        var pulled = GroundOutlineSize.Of("B738", towedNoseFirst: true);

        Assert.Equal(0.0, parked.NoseLeadFt);
        Assert.Equal(GroundOutline.TugLeadFt, pulled.NoseLeadFt);
        Assert.True(parked.LengthFt > 100.0, $"a B738 is {parked.LengthFt:F1} ft long");
        Assert.Equal((parked.LengthFt / 2.0) + GroundOutline.TugLeadFt, pulled.ReachFt, Tolerance);
    }

    [Fact]
    public void Frame_MapsFeetEastAndNorthOfItsOrigin()
    {
        var origin = new LatLon(37.6189, -122.3810);
        var frame = new GroundOutlineFrame(origin);
        OutlinePoint east = frame.ToLocal(GeoMath.ProjectPoint(origin, new TrueHeading(90.0), 100.0 / GeoMath.FeetPerNm));
        OutlinePoint north = frame.ToLocal(GeoMath.ProjectPoint(origin, new TrueHeading(0.0), 100.0 / GeoMath.FeetPerNm));

        Assert.Equal(100.0, east.EastFt, 0.1);
        Assert.Equal(0.0, east.NorthFt, 0.1);
        Assert.Equal(0.0, north.EastFt, 0.1);
        Assert.Equal(100.0, north.NorthFt, 0.1);
    }
}
