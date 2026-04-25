using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

public class PatternAuthoredOverrideTests
{
    private static GroundRunway MakeAuthored(double? aglFt, double? sizeNm) =>
        new()
        {
            Name = "28L - 10R",
            Coordinates = [],
            WidthFt = 150,
            PatternAltitudeAglFt = aglFt,
            PatternSizeNm = sizeNm,
        };

    [Fact]
    public void ResolveOverrides_NoCommandNoData_ReturnsNullPair()
    {
        var rwy = TestRunwayFactory.Make(elevationFt: 9);
        var (size, alt) = PatternGeometry.ResolveAuthoredOverrides(rwy, authoredRunway: null, commandSizeNm: null, commandAltitudeMslFt: null);
        Assert.Null(size);
        Assert.Null(alt);
    }

    [Fact]
    public void ResolveOverrides_AuthoredOnly_TranslatesAglToMsl()
    {
        // OAK 28L: 600ft AGL above field elev 9ft → 609ft MSL.
        var rwy = TestRunwayFactory.Make(elevationFt: 9);
        var authored = MakeAuthored(aglFt: 600, sizeNm: 0.5);

        var (size, alt) = PatternGeometry.ResolveAuthoredOverrides(rwy, authored, commandSizeNm: null, commandAltitudeMslFt: null);

        Assert.Equal(0.5, size);
        Assert.Equal(609, alt);
    }

    [Fact]
    public void ResolveOverrides_CommandWinsOverAuthored()
    {
        var rwy = TestRunwayFactory.Make(elevationFt: 9);
        var authored = MakeAuthored(aglFt: 600, sizeNm: 0.5);

        var (size, alt) = PatternGeometry.ResolveAuthoredOverrides(rwy, authored, commandSizeNm: 1.0, commandAltitudeMslFt: 1500);

        Assert.Equal(1.0, size);
        Assert.Equal(1500, alt);
    }

    [Fact]
    public void ResolveOverrides_CommandPartial_FillsRemainderFromAuthored()
    {
        var rwy = TestRunwayFactory.Make(elevationFt: 9);
        var authored = MakeAuthored(aglFt: 600, sizeNm: 0.5);

        // command altitude only — size falls through to authored
        var (size1, alt1) = PatternGeometry.ResolveAuthoredOverrides(rwy, authored, commandSizeNm: null, commandAltitudeMslFt: 1500);
        Assert.Equal(0.5, size1);
        Assert.Equal(1500, alt1);

        // command size only — altitude falls through to authored
        var (size2, alt2) = PatternGeometry.ResolveAuthoredOverrides(rwy, authored, commandSizeNm: 1.0, commandAltitudeMslFt: null);
        Assert.Equal(1.0, size2);
        Assert.Equal(609, alt2);
    }

    [Fact]
    public void Compute_WithAuthoredAlt_RaisesPatternAltitudeAboveCategoryDefault()
    {
        // Field elev 9ft. Piston default = 1000 AGL → 1009 MSL.
        // Authored 600 AGL → 609 MSL. Resolution should produce 609.
        var rwy = TestRunwayFactory.Make(elevationFt: 9);
        var authored = MakeAuthored(aglFt: 600, sizeNm: null);

        var (_, alt) = PatternGeometry.ResolveAuthoredOverrides(rwy, authored, commandSizeNm: null, commandAltitudeMslFt: null);
        var wp = PatternGeometry.Compute(
            rwy,
            AircraftCategory.Piston,
            PatternDirection.Left,
            sizeOverrideNm: null,
            altitudeOverrideFt: alt,
            airportRunways: null
        );

        Assert.Equal(609, wp.PatternAltitude);
    }
}
