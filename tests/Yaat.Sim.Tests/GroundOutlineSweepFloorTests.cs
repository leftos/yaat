using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// The outline floor a tug move is held to against one neighbour, by the pair's design groups: AC 150/5300-13B Table
/// 5-1's parking position clearance read by the more demanding of the two groups, 10 ft for ADG I and II and 25 ft for
/// ADG III to VI, less the 0.5 ft slack.
/// </summary>
public class GroundOutlineSweepFloorTests
{
    /// <summary>A point on the KSFO field the cases stand at.</summary>
    private static readonly LatLon Field = new(37.6155, -122.3830);

    /// <summary>How far east of the mover the neighbour stands, feet: far enough that the outlines start more than 25 ft apart.</summary>
    private const double AbeamFt = 150.0;

    public GroundOutlineSweepFloorTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Theory]
    [InlineData(AirplaneDesignGroup.I, AirplaneDesignGroup.I, 10.0)]
    [InlineData(AirplaneDesignGroup.I, AirplaneDesignGroup.II, 10.0)]
    [InlineData(AirplaneDesignGroup.II, AirplaneDesignGroup.II, 10.0)]
    [InlineData(AirplaneDesignGroup.II, AirplaneDesignGroup.III, 25.0)]
    [InlineData(AirplaneDesignGroup.III, AirplaneDesignGroup.III, 25.0)]
    [InlineData(AirplaneDesignGroup.VI, AirplaneDesignGroup.I, 25.0)]
    public void PairWingtipBufferFt_IsTenFeetForAnAdgIOrIIPair_ElseTwentyFive(AirplaneDesignGroup a, AirplaneDesignGroup b, double expectedFt) =>
        Assert.Equal(expectedFt, GroundOutlineSweep.PairWingtipBufferFt(a, b));

    [Theory]
    [InlineData(30.0, 10.0, 9.5)]
    [InlineData(5.0, 10.0, 4.5)]
    [InlineData(0.2, 10.0, 0.5)]
    [InlineData(30.0, 25.0, 24.5)]
    [InlineData(5.0, 25.0, 4.5)]
    public void FloorFt_IsTheLeastOfBufferAndAnchorLessTheSlack_NeverUnderTheSlack(double anchorFt, double bufferFt, double expectedFt) =>
        Assert.Equal(expectedFt, GroundOutlineSweep.FloorFt(anchorFt, bufferFt), 9);

    [Theory]
    [InlineData("C172", AirplaneDesignGroup.I)]
    [InlineData("CRJ7", AirplaneDesignGroup.II)]
    [InlineData("B738", AirplaneDesignGroup.III)]
    public void Of_CarriesTheTypesDesignGroup(string type, AirplaneDesignGroup expected) =>
        Assert.Equal(expected, GroundOutlineSize.Of(AircraftFootprint.FromType(type), towedNoseFirst: false).DesignGroup);

    [Fact]
    public void Sweep_SmallPair_FloorIs9Point5_LargePair_24Point5()
    {
        GroundOutlineSweepResult small = SweepAbeam("C172", "CRJ7");
        GroundOutlineSweepResult large = SweepAbeam("B738", "CRJ7");

        Assert.True(small.StartClearanceFt > 25.0, $"test setup: the C172 starts {small.StartClearanceFt:F1} ft from the CRJ7");
        Assert.True(large.StartClearanceFt > 25.0, $"test setup: the B738 starts {large.StartClearanceFt:F1} ft from the CRJ7");
        Assert.Equal(9.5, small.FloorFt, 9);
        Assert.Equal(24.5, large.FloorFt, 9);
    }

    [Fact]
    public void TowStartFloorFt_ForAC172Pair_Is9Point5()
    {
        var startPose = new PushbackPose(Field, 0.0);
        var neighbourPose = new PushbackPose(Abeam(), 0.0);
        var c172 = AircraftFootprint.FromType("C172");

        double floorFt = GroundOutlineSweep.TowStartFloorFt(new TugRowAnchor(startPose, PushbackLegKind.Push), c172, neighbourPose, c172);

        Assert.Equal(9.5, floorFt, 9);
    }

    [Fact]
    public void Sweep_SmallMoverBesideALargeNeighbour_FloorIs24Point5()
    {
        GroundOutlineSweepResult result = SweepAbeam("C172", "B738");

        Assert.True(result.StartClearanceFt > 25.0, $"test setup: the C172 starts {result.StartClearanceFt:F1} ft from the B738");
        Assert.Equal(24.5, result.FloorFt, 9);
    }

    [Fact]
    public void TowStartFloorFt_SmallMoverBesideALargeNeighbour_Is24Point5()
    {
        var startPose = new PushbackPose(Field, 0.0);
        var neighbourPose = new PushbackPose(Abeam(), 0.0);
        double startFt = SweepAbeam("C172", "B738").StartClearanceFt;

        double floorFt = GroundOutlineSweep.TowStartFloorFt(
            new TugRowAnchor(startPose, PushbackLegKind.Push),
            AircraftFootprint.FromType("C172"),
            neighbourPose,
            AircraftFootprint.FromType("B738")
        );

        Assert.True(startFt > 25.0, $"test setup: the C172 starts {startFt:F1} ft from the B738");
        Assert.Equal(24.5, floorFt, 9);
    }

    private static LatLon Abeam() => GeoMath.ProjectPoint(Field, new TrueHeading(90.0), AbeamFt / GeoMath.FeetPerNm);

    private static GroundOutlineSweepResult SweepAbeam(string moverType, string neighbourType)
    {
        var startPose = new PushbackPose(Field, 0.0);
        var frame = new GroundOutlineFrame(Field);
        var moverSize = GroundOutlineSize.Of(AircraftFootprint.FromType(moverType), towedNoseFirst: false);
        var neighbourSize = GroundOutlineSize.Of(AircraftFootprint.FromType(neighbourType), towedNoseFirst: false);
        return GroundOutlineSweep.Sweep([(startPose, 0.0)], startPose, null, frame, moverSize, Abeam(), 0.0, neighbourSize);
    }
}
