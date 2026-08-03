using System.IO;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="LandingThreshold"/> converts a <see cref="RunwayInfo"/>'s pavement-end threshold into the
/// end's *landing* threshold. The nav database stores pavement ends — probed against the vNAS airport
/// maps, every <c>RunwayInfo.ThresholdLatitude</c> sits within ~5 ft of <c>Coordinates[0]</c>/<c>[^1]</c>,
/// so a displaced end (KSJC 30L, 2,537 ft) has no landing datum without the ground layout.
///
/// AIM 2-3-3.b.8.2: pavement before a displaced threshold is available for takeoff in either direction
/// and for rollout from the opposite end, but not for landing in that direction.
/// </summary>
public class LandingThresholdTests
{
    public LandingThresholdTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AirportGroundLayout Layout(string geojson, string airportId) =>
        GeoJsonParser.Parse(airportId, File.ReadAllText(Path.Combine("TestData", geojson)), airportId);

    private static RunwayInfo Runway(string airportId, string designator)
    {
        var rwy = NavigationDatabase.Instance.GetRunway(airportId, designator);
        Assert.NotNull(rwy);
        return rwy;
    }

    private static double FeetApart(LatLon a, LatLon b) => GeoMath.DistanceNm(a.Lat, a.Lon, b.Lat, b.Lon) * GeoMath.FeetPerNm;

    [Fact]
    public void Resolve_NoLayout_FallsBackToThePavementThreshold()
    {
        var rwy = Runway("KSJC", "30L");

        var threshold = LandingThreshold.Resolve(rwy, layout: null);

        Assert.Equal(rwy.ThresholdLatitude, threshold.Lat);
        Assert.Equal(rwy.ThresholdLongitude, threshold.Lon);
        Assert.Equal(0, LandingThreshold.DisplacementFt(rwy, layout: null));
    }

    [Fact]
    public void Resolve_UndisplacedEnd_ReturnsThePavementThreshold()
    {
        var rwy = Runway("KOAK", "28R");

        var threshold = LandingThreshold.Resolve(rwy, Layout("oak.geojson", "OAK"));

        Assert.Equal(0, LandingThreshold.DisplacementFt(rwy, Layout("oak.geojson", "OAK")));
        Assert.InRange(FeetApart(threshold, new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude)), 0, 1);
    }

    /// <summary>
    /// KSJC 12R/30L is authored <c>"threshold": "1297 - 2537"</c>. Landing 30L starts 2,537 ft downfield
    /// of the pavement end — the departures-only stretch issue #324 is about.
    /// </summary>
    [Theory]
    [InlineData("30L", 2537.0)]
    [InlineData("12R", 1297.0)]
    public void Resolve_DisplacedEnd_MovesDownfieldByThePublishedDisplacement(string designator, double expectedFt)
    {
        var layout = Layout("sjc.geojson", "SJC");
        var rwy = Runway("KSJC", designator);
        var pavement = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);

        var threshold = LandingThreshold.Resolve(rwy, layout);

        Assert.Equal(expectedFt, LandingThreshold.DisplacementFt(rwy, layout));
        Assert.InRange(FeetApart(threshold, pavement), expectedFt - 5, expectedFt + 5);

        // Downfield means toward the far end, i.e. along the landing direction.
        double bearing = GeoMath.BearingTo(pavement.Lat, pavement.Lon, threshold.Lat, threshold.Lon);
        Assert.InRange(Math.Abs(((bearing - rwy.TrueHeading.Degrees + 540.0) % 360.0) - 180.0), 0, 1);
    }

    /// <summary>
    /// The threshold has to stay on the <see cref="RunwayInfo"/> centerline every other calculation uses.
    /// Projecting along the layout's own course instead would introduce a cross-track offset wherever the
    /// two disagree, and the landing centerline steering would chase it.
    /// </summary>
    [Fact]
    public void Resolve_StaysOnTheRunwayInfoCenterline()
    {
        var rwy = Runway("KSJC", "30L");

        var threshold = LandingThreshold.Resolve(rwy, Layout("sjc.geojson", "SJC"));

        double xteFt =
            Math.Abs(GeoMath.SignedCrossTrackDistanceNm(threshold.Lat, threshold.Lon, rwy.ThresholdLatitude, rwy.ThresholdLongitude, rwy.TrueHeading))
            * GeoMath.FeetPerNm;
        Assert.InRange(xteFt, 0, 1);
    }

    /// <summary>
    /// Runway designators repeat across airports ("28R" is at both KOAK and KSFO), so a layout for the
    /// wrong airport must not contribute a displacement.
    /// </summary>
    [Fact]
    public void Resolve_LayoutForADifferentAirport_IsIgnored()
    {
        var rwy = Runway("KSJC", "30L");

        Assert.Equal(0, LandingThreshold.DisplacementFt(rwy, Layout("oak.geojson", "OAK")));
    }

    [Fact]
    public void Resolve_LayoutWithoutThatRunway_FallsBackToThePavementThreshold()
    {
        var rwy = Runway("KSJC", "30L");
        var layout = new AirportGroundLayout { AirportId = "SJC" };

        var threshold = LandingThreshold.Resolve(rwy, layout);

        Assert.Equal(rwy.ThresholdLatitude, threshold.Lat);
        Assert.Equal(rwy.ThresholdLongitude, threshold.Lon);
    }
}
