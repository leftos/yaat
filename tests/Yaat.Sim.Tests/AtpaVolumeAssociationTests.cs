using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// Covers single-volume best-fit association support: resolving a volume's runway designator from its
/// configured threshold, and the lossy scratchpad-runway tie-break that disambiguates overlapping parallel
/// volumes. Uses the real KOAK 28/30 ATPA volume thresholds from the ZOA adaptation.
/// </summary>
public class AtpaVolumeAssociationTests
{
    private readonly ITestOutputHelper output;

    public AtpaVolumeAssociationTests(ITestOutputHelper output)
    {
        this.output = output;
        TestVnasData.EnsureInitialized();
    }

    // KOAK 28 and 30 volume thresholds (ZOA/NCT adaptation).
    private static AtpaVolumeConfig Oak28() =>
        new()
        {
            Id = "O28",
            AirportId = "OAK",
            RunwayThreshold = new TowerLocationConfig { Lat = 37.7236111111111, Lon = -122.205555555556 },
            MagneticHeading = 276,
            MaximumHeadingDeviation = 45,
            Floor = 8,
            Ceiling = 4000,
            Length = 16,
            WidthLeft = 3646,
            WidthRight = 18228,
        };

    private static AtpaVolumeConfig Oak30() =>
        new()
        {
            Id = "O30",
            AirportId = "OAK",
            RunwayThreshold = new TowerLocationConfig { Lat = 37.7013888888889, Lon = -122.214444444444 },
            MagneticHeading = 295,
            MaximumHeadingDeviation = 90,
            Floor = 8,
            Ceiling = 5000,
            Length = 16,
            WidthLeft = 18228,
            WidthRight = 6684,
        };

    [Fact]
    public void VolumeRunwayDesignator_ResolvesOakRunways()
    {
        if (TestVnasData.NavigationDb?.GetRunways("OAK").Count is null or 0)
        {
            return; // runway data unavailable -> skip
        }

        var d28 = AtpaVolumeGeometry.VolumeRunwayDesignator(Oak28());
        var d30 = AtpaVolumeGeometry.VolumeRunwayDesignator(Oak30());
        output.WriteLine($"OAK 28 designator={d28 ?? "(null)"}  OAK 30 designator={d30 ?? "(null)"}");

        Assert.Equal("28R", d28);
        Assert.Equal("30", d30);
    }

    [Theory]
    [InlineData("I30", "30", true)] // exact
    [InlineData("I8R", "28R", true)] // lossy: "8R" suffix-matches canonical "28R"
    [InlineData("I28R", "28R", true)] // full form also matches
    [InlineData("I30", "28R", false)] // 30 must not match the 28 volume
    [InlineData("I8R", "30", false)] // 28R must not match the 30 volume
    public void ScratchpadMatchesVolumeRunway_TolerantSuffixMatch(string scratchpad, string expectVolumeRunway, bool expectMatch)
    {
        if (TestVnasData.NavigationDb?.GetRunways("OAK").Count is null or 0)
        {
            return;
        }

        var volume = expectVolumeRunway == "30" ? Oak30() : Oak28();
        var ac = new AircraftState
        {
            Callsign = "TST1",
            AircraftType = "C152",
            Stars = new AircraftStarsState { Scratchpad1 = scratchpad },
        };

        Assert.Equal(expectMatch, AtpaProcessor.ScratchpadMatchesVolumeRunway(ac, volume));
    }

    // SFO 28 IN-TRAIL (active, airportId SFO) and SFO 28 SIDE-BY (disabled by flipping airportId to OVE).
    // Identical threshold/heading/width — the only functional difference is the airportId. Both are handed
    // to Process. An SFO 28 arrival must still be associated to one volume and paired in-trail — exclusive
    // association must not split the pair across the active and disabled twins or drop it entirely.
    private static AtpaVolumeConfig Sfo28(string id, string airportId) =>
        new()
        {
            Id = id,
            VolumeId = id,
            AirportId = airportId,
            RunwayThreshold = new TowerLocationConfig { Lat = 37.6125, Lon = -122.357777777778 },
            MagneticHeading = 282,
            MaximumHeadingDeviation = 45,
            Floor = 12,
            Ceiling = 7000,
            Length = 25,
            WidthLeft = 12152,
            WidthRight = 12152,
        };

    private static AircraftState Arrival(string callsign, string type, LatLon position, TrueHeading heading) =>
        new()
        {
            Callsign = callsign,
            AircraftType = type,
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = 3000,
            IndicatedAirspeed = 160,
            VerticalSpeed = 0,
            IsOnGround = false,
        };

    [Fact]
    public void OverlappingActiveAndDisabledTwin_PairsInTrailNotLost()
    {
        if (TestVnasData.NavigationDb?.GetRunways("SFO").Count is null or 0)
        {
            return;
        }

        var inTrail = Sfo28("S28IT", "SFO");
        var disabledSideBy = Sfo28("S28SB", "OVE");
        var volumes = new List<AtpaVolumeConfig> { inTrail, disabledSideBy };

        var trueCourse = AtpaVolumeGeometry.VolumeTrueHeadingDeg(inTrail);
        var heading = new TrueHeading(trueCourse);
        var outbound = new TrueHeading((trueCourse + 180.0) % 360.0);
        var threshold = new LatLon(inTrail.RunwayThreshold.Lat, inTrail.RunwayThreshold.Lon);

        var lead = Arrival("SWA1", "B738", GeoMath.ProjectPoint(threshold, outbound, 6.0), heading);
        var trail = Arrival("SWA2", "B738", GeoMath.ProjectPoint(threshold, outbound, 9.0), heading);

        var results = new AtpaProcessor().Process([lead, trail], volumes, new StarsConfig());
        output.WriteLine($"results: {string.Join(", ", results.Select(r => $"{r.Key}->{r.Value.TargetTrackId}"))}");

        Assert.True(results.ContainsKey("SWA2"), "Trailing SFO 28 arrival must get an in-trail cone despite the overlapping disabled twin");
        Assert.Equal("CALLSIGNSWA1", results["SWA2"].TargetTrackId);
        Assert.False(results.ContainsKey("SWA1"), "Lead has no aircraft ahead — no cone");
    }

    [Fact]
    public void DisabledVolumePointedAtOve_ProducesNoCones()
    {
        if (TestVnasData.NavigationDb?.GetRunways("SFO").Count is null or 0)
        {
            return;
        }

        // Only the disabled (OVE) twin is present. Its airportId resolves no runway at the SFO threshold, so
        // it is inactive and must produce no cones even with two aircraft geometrically on the final.
        var disabled = Sfo28("S28SB", "OVE");
        Assert.False(AtpaVolumeGeometry.IsActiveVolume(disabled));

        var trueCourse = AtpaVolumeGeometry.VolumeTrueHeadingDeg(disabled);
        var heading = new TrueHeading(trueCourse);
        var outbound = new TrueHeading((trueCourse + 180.0) % 360.0);
        var threshold = new LatLon(disabled.RunwayThreshold.Lat, disabled.RunwayThreshold.Lon);

        var lead = Arrival("SWA1", "B738", GeoMath.ProjectPoint(threshold, outbound, 6.0), heading);
        var trail = Arrival("SWA2", "B738", GeoMath.ProjectPoint(threshold, outbound, 9.0), heading);

        var results = new AtpaProcessor().Process([lead, trail], [disabled], new StarsConfig());
        Assert.Empty(results);
    }

    [Fact]
    public void ReducedSeparation_AppliesOnlyWithinConfiguredDistance()
    {
        if (TestVnasData.NavigationDb?.GetRunways("OAK").Count is null or 0)
        {
            return;
        }

        // OAK 30 with reduced 2.5 NM separation enabled within 10 nm of the threshold. Same-type light pair
        // (no wake), so the floor is the binding constraint: 2.5 NM inside 10 nm, reverting to 3.0 outside.
        var vol = Oak30();
        vol.TwoPointFiveApproachEnabled = true;
        vol.TwoPointFiveApproachDistance = 10;

        var trueCourse = AtpaVolumeGeometry.VolumeTrueHeadingDeg(vol);
        var heading = new TrueHeading(trueCourse);
        var outbound = new TrueHeading((trueCourse + 180.0) % 360.0);
        var threshold = new LatLon(vol.RunwayThreshold.Lat, vol.RunwayThreshold.Lon);

        var leadIn = Arrival("N1", "C172", GeoMath.ProjectPoint(threshold, outbound, 5.0), heading);
        var trailIn = Arrival("N2", "C172", GeoMath.ProjectPoint(threshold, outbound, 8.0), heading);
        var inside = new AtpaProcessor().Process([leadIn, trailIn], [vol], new StarsConfig());
        output.WriteLine($"within 10nm: allowed={inside["N2"].AllowedSeparation}");
        Assert.Equal(2.5, inside["N2"].AllowedSeparation, 1);

        var leadOut = Arrival("N3", "C172", GeoMath.ProjectPoint(threshold, outbound, 12.0), heading);
        var trailOut = Arrival("N4", "C172", GeoMath.ProjectPoint(threshold, outbound, 15.0), heading);
        var outside = new AtpaProcessor().Process([leadOut, trailOut], [vol], new StarsConfig());
        output.WriteLine($"beyond 10nm: allowed={outside["N4"].AllowedSeparation}");
        Assert.Equal(3.0, outside["N4"].AllowedSeparation, 1);
    }
}
