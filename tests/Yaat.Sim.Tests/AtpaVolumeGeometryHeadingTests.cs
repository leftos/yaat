using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// Covers the true-heading resolution for ATPA volume geometry (GitHub issue #189). Aircraft tracks
/// and great-circle bearings are true, but vNAS stores the volume's centerline as a MAGNETIC heading
/// rounded to the runway designator (88 for runway 8R). <see cref="AtpaVolumeGeometry"/> resolves the
/// actual runway true heading from the configured threshold so the volume aligns with the final the
/// aircraft fly — important on closely-spaced parallels, where a rotated centerline would pull the
/// neighboring runway's traffic into the volume.
/// </summary>
public class AtpaVolumeGeometryHeadingTests
{
    public AtpaVolumeGeometryHeadingTests() => TestVnasData.EnsureInitialized();

    private static AtpaVolumeConfig IahVolume() =>
        new()
        {
            Id = "IAH8R",
            AirportId = "KIAH",
            // IAH 8R threshold + magnetic heading from the ZHU ARTCC config.
            RunwayThreshold = new TowerLocationConfig { Lat = 29.9934, Lon = -95.3550 },
            MagneticHeading = 88,
            MaximumHeadingDeviation = 90,
            Floor = 0,
            Ceiling = 200,
            Length = 30,
            // Narrow band so the heading source measurably shifts a far-out target's cross-track.
            WidthLeft = 1000,
            WidthRight = 1000,
        };

    [Fact]
    public void VolumeTrueHeading_ResolvesRunwayTrueHeading()
    {
        if (TestVnasData.NavigationDb?.GetRunways("KIAH").Count is null or 0)
        {
            return; // runway data unavailable -> skip
        }

        // IAH 8R true heading is 89.95 deg; the resolver must return that, not the rounded magnetic 88.
        var heading = AtpaVolumeGeometry.VolumeTrueHeadingDeg(IahVolume());
        Assert.Equal(90.0, heading, precision: 0);
        Assert.NotEqual(88.0, heading, precision: 1);
    }

    [Fact]
    public void IsInside_TrueCenterlineTargetFarOut_UsesRunwayHeading()
    {
        if (TestVnasData.NavigationDb?.GetRunways("KIAH").Count is null or 0)
        {
            return;
        }

        var volume = IahVolume();
        var approachCourse = new TrueHeading(AtpaVolumeGeometry.VolumeTrueHeadingDeg(volume));
        var outbound = new TrueHeading((approachCourse.Degrees + 180.0) % 360.0);
        var threshold = new LatLon(volume.RunwayThreshold.Lat, volume.RunwayThreshold.Lon);

        // 18 nm out along the resolved true final, outbound from the threshold (where on-final arrivals
        // sit), tracking inbound. Cross-track is ~0, so it is inside the narrow 1000 ft band.
        var ac = new AircraftState
        {
            Callsign = "AAL1",
            AircraftType = "B738",
            Position = GeoMath.ProjectPoint(threshold, outbound, 18.0),
            TrueHeading = approachCourse,
            TrueTrack = approachCourse,
            Altitude = 6000,
            IndicatedAirspeed = 180,
            VerticalSpeed = 0,
            IsOnGround = false,
        };

        Assert.True(AtpaVolumeGeometry.IsInside(volume, ac));
        Assert.True(AtpaVolumeGeometry.IsEstablishedOnApproach(volume, ac));
    }
}
