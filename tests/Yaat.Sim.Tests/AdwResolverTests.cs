using System.IO;
using System.Linq;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="AdwResolver"/> turns facility-published Arrival/Departure Window ranges into drawable
/// marks. The sign convention is the thing worth pinning: a positive range runs outbound along the
/// final approach course, a negative one past the threshold and down the runway. Checked against the
/// KMIA windows in ZMA D11 Miami ATCT SOP 3-9.
/// </summary>
public class AdwResolverTests
{
    private static AirportGroundLayout MiaLayout()
    {
        TestVnasData.EnsureInitialized();
        string path = Path.Combine("TestData", "mia.geojson");
        return GeoJsonParser.Parse("MIA", File.ReadAllText(path), "MIA");
    }

    private static LatLon MidPoint(AdwMark mark) => new((mark.A.Lat + mark.B.Lat) / 2.0, (mark.A.Lon + mark.B.Lon) / 2.0);

    private static double LengthFt(AdwMark mark) => GeoMath.DistanceNm(mark.A.Lat, mark.A.Lon, mark.B.Lat, mark.B.Lon) * GeoMath.FeetPerNm;

    [Fact]
    public void Resolve_NoWindows_ReturnsEmpty()
    {
        Assert.Empty(AdwResolver.Resolve(MiaLayout(), []));
    }

    [Fact]
    public void Resolve_OuterMark_SitsOutOnFinalNotDownTheRunway()
    {
        var layout = MiaLayout();
        var window = new AdwWindow("26R", "30", 2.7, -0.1, null);

        var outer = Assert.Single(AdwResolver.Resolve(layout, [window]), m => m.Kind == AdwMarkKind.Outer);

        var runway = layout.FindRunway("26R")!;
        var threshold = runway.LandingThresholdForEnd("26R")!.Value.Threshold;
        var center = MidPoint(outer);

        // 26R lands westbound (~260 true), so its final approach course lies EAST of the threshold.
        double bearingToMark = GeoMath.BearingTo(threshold.Lat, threshold.Lon, center.Lat, center.Lon);
        Assert.InRange(bearingToMark, 60, 100);
        Assert.InRange(GeoMath.DistanceNm(threshold.Lat, threshold.Lon, center.Lat, center.Lon), 2.69, 2.71);
    }

    [Fact]
    public void Resolve_NegativeInnerRange_SitsDownTheRunwayFromTheThreshold()
    {
        var layout = MiaLayout();
        var window = new AdwWindow("26R", "30", 2.7, -0.1, null);

        var inner = Assert.Single(AdwResolver.Resolve(layout, [window]), m => m.Kind == AdwMarkKind.Inner);

        var runway = layout.FindRunway("26R")!;
        var threshold = runway.LandingThresholdForEnd("26R")!.Value.Threshold;
        var center = MidPoint(inner);

        // Negative inner range → 0.1 nm past the threshold, i.e. WEST of it, in the landing direction.
        double bearingToMark = GeoMath.BearingTo(threshold.Lat, threshold.Lon, center.Lat, center.Lon);
        Assert.InRange(bearingToMark, 240, 280);
        Assert.InRange(GeoMath.DistanceNm(threshold.Lat, threshold.Lon, center.Lat, center.Lon), 0.09, 0.11);
    }

    [Fact]
    public void Resolve_PositiveInnerRange_SitsShortOfTheThreshold()
    {
        var layout = MiaLayout();
        // SOP 3-9.D: arriving 30, departing 26L — inner range is +0.1 nm, i.e. short of the threshold.
        var window = new AdwWindow("30", "26L", 2.7, 0.1, null);

        var inner = Assert.Single(AdwResolver.Resolve(layout, [window]), m => m.Kind == AdwMarkKind.Inner);

        var runway = layout.FindRunway("30")!;
        var threshold = runway.LandingThresholdForEnd("30")!.Value.Threshold;
        var center = MidPoint(inner);

        // RWY 30 lands northwest, so "short of the threshold" is southeast of it.
        double bearingToMark = GeoMath.BearingTo(threshold.Lat, threshold.Lon, center.Lat, center.Lon);
        Assert.InRange(bearingToMark, 110, 130);
    }

    [Fact]
    public void Resolve_Mia30Pair_MarksStraddleTheThresholdPointFourNmApart()
    {
        var layout = MiaLayout();
        var windows = new[] { new AdwWindow("30", "26L", 2.7, 0.1, "SOP 3-9.D"), new AdwWindow("30", "26R", 2.9, -0.3, "SOP 3-9.E") };

        var inner26L = Assert.Single(AdwResolver.Resolve(layout, windows), m => (m.Kind == AdwMarkKind.Inner) && (m.DepartureRunway == "26L"));
        var inner26R = Assert.Single(AdwResolver.Resolve(layout, windows), m => (m.Kind == AdwMarkKind.Inner) && (m.DepartureRunway == "26R"));

        var a = MidPoint(inner26L);
        var b = MidPoint(inner26R);
        Assert.InRange(GeoMath.DistanceNm(a.Lat, a.Lon, b.Lat, b.Lon), 0.39, 0.41);
    }

    [Fact]
    public void Resolve_Mia30_MeasuresFromTheDisplacedThresholdNotThePavementEnd()
    {
        var layout = MiaLayout();
        var runway = layout.FindRunway("30")!;

        // KMIA 12/30 is authored "threshold": "0 - 957" — RWY 30's landing threshold is 957 ft downfield.
        Assert.Equal(957, runway.ThresholdDisplacementForEnd("30"));

        var inner = Assert.Single(AdwResolver.Resolve(layout, [new AdwWindow("30", "26R", 2.9, -0.3, null)]), m => m.Kind == AdwMarkKind.Inner);
        var center = MidPoint(inner);
        var pavementEnd = runway.Coordinates[^1];

        // Measured from the pavement end the mark would be 0.3 nm out; from the displaced threshold it
        // is 957 ft further up the runway, so the pavement-end distance must be 0.3 nm + 957 ft.
        double fromPavementFt = GeoMath.DistanceNm(pavementEnd.Lat, pavementEnd.Lon, center.Lat, center.Lon) * GeoMath.FeetPerNm;
        Assert.InRange(fromPavementFt, (0.3 * GeoMath.FeetPerNm) + 957 - 10, (0.3 * GeoMath.FeetPerNm) + 957 + 10);
    }

    /// <summary>
    /// The datum test with a sign flip in it. SOP 3-9.D's +0.1 nm inner range is *short* of the landing
    /// threshold, and RWY 30's threshold is displaced 957 ft — so the mark still lands 349 ft **on** the
    /// pavement. Measured from the pavement end instead it would sit 608 ft **off** the end, out in the
    /// approach lights. The reporter's ASDE-X screenshot puts it 377 ft inside the pavement, which is the
    /// displaced-threshold reading and excludes the other one outright.
    /// </summary>
    [Fact]
    public void Resolve_Mia30ArrDep26L_InnerMarkLandsOnPavementNotOffTheEnd()
    {
        var layout = MiaLayout();
        var runway = layout.FindRunway("30")!;
        var pavementEnd = runway.Coordinates[^1];

        var inner = Assert.Single(AdwResolver.Resolve(layout, [new AdwWindow("30", "26L", 2.7, 0.1, null)]), m => m.Kind == AdwMarkKind.Inner);
        var center = MidPoint(inner);

        double fromPavementFt = GeoMath.DistanceNm(pavementEnd.Lat, pavementEnd.Lon, center.Lat, center.Lon) * GeoMath.FeetPerNm;
        Assert.InRange(fromPavementFt, 349 - 10, 349 + 10);

        // On the pavement means toward the NW (the landing direction), not off the SE end.
        double bearingFromPavement = GeoMath.BearingTo(pavementEnd.Lat, pavementEnd.Lon, center.Lat, center.Lon);
        Assert.InRange(bearingFromPavement, 290, 310);
    }

    [Fact]
    public void Resolve_InnerMarkSpansTheRunwayWidth_OuterMarkUsesTheFixedTick()
    {
        var layout = MiaLayout();
        var runway = layout.FindRunway("26R")!;

        var marks = AdwResolver.Resolve(layout, [new AdwWindow("26R", "30", 2.7, -0.1, null)]);
        var inner = Assert.Single(marks, m => m.Kind == AdwMarkKind.Inner);
        var outer = Assert.Single(marks, m => m.Kind == AdwMarkKind.Outer);

        Assert.InRange(LengthFt(inner), runway.WidthFt - 2, runway.WidthFt + 2);
        Assert.InRange(LengthFt(outer), 545, 549);
    }

    [Fact]
    public void Resolve_MarksAreLaidPerpendicularToTheRunway()
    {
        var layout = MiaLayout();
        var runway = layout.FindRunway("26R")!;
        double landingCourse = runway.LandingThresholdForEnd("26R")!.Value.LandingCourseDeg;

        foreach (var mark in AdwResolver.Resolve(layout, [new AdwWindow("26R", "30", 2.7, -0.1, null)]))
        {
            double markBearing = GeoMath.BearingTo(mark.A.Lat, mark.A.Lon, mark.B.Lat, mark.B.Lon);
            double offset = Math.Abs(((markBearing - landingCourse + 540.0) % 360.0) - 180.0);
            Assert.InRange(offset, 89, 91);
        }
    }

    [Fact]
    public void Resolve_UnknownArrivalRunway_IsSkippedNotThrown()
    {
        var layout = MiaLayout();
        Assert.Empty(AdwResolver.Resolve(layout, [new AdwWindow("17L", "30", 2.7, -0.1, null)]));
    }

    [Fact]
    public void Resolve_RunwayWithoutGeometry_IsSkippedNotThrown()
    {
        var layout = new AirportGroundLayout
        {
            AirportId = "TST",
            Runways =
            {
                new GroundRunway
                {
                    Name = "30 - 12",
                    Coordinates = [],
                    WidthFt = 150,
                },
            },
        };

        Assert.Empty(AdwResolver.Resolve(layout, [new AdwWindow("30", "26R", 2.7, -0.1, null)]));
    }

    [Fact]
    public void GetMarks_UsesTheShippedMiamiSidecar()
    {
        var layout = MiaLayout();

        var marks = AdwResolver.GetMarks(layout);

        // Four published windows → an outer and an inner mark each.
        Assert.Equal(8, marks.Count);
        Assert.Equal(4, marks.Count(m => m.Kind == AdwMarkKind.Inner));
        Assert.Same(marks, AdwResolver.GetMarks(layout));
    }
}
