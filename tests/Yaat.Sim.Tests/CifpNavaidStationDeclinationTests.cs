using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// The CIFP navaid primary record carries the station declination (ARINC 424 field 5.66, columns 75-79) that
/// courses referenced to the VOR are charted against. CCR (CONCORD) is published E017 even though the KCCR airport
/// record's variation of record is E013, which is why a VOR-referenced course must not use the airport value.
/// </summary>
public class CifpNavaidStationDeclinationTests
{
    public CifpNavaidStationDeclinationTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void CifpNavaid_ParsesStationDeclination_CcrIsEast17()
    {
        IReadOnlyDictionary<string, CifpNavaid> navaids = CifpParser.ParseNavaids(TestVnasData.GetCifpPath()!);

        Assert.Equal(17.0, navaids["CCR"].StationDeclination);
        Assert.Equal(17.0, TestVnasData.NavigationDb!.GetStationDeclination("CCR"));
    }

    [Theory]
    [InlineData("OAK", 17.0)] // OAKLAND VOR/DME, E0170
    [InlineData("ACK", -15.0)] // NANTUCKET VOR/DME, W0150: west is negative
    public void CifpNavaid_ParsesStationDeclination_SignFollowsHemisphere(string navaidId, double expected) =>
        Assert.Equal(expected, TestVnasData.NavigationDb!.GetStationDeclination(navaidId));

    [Fact]
    public void GetStationDeclination_UnknownNavaid_IsNull() => Assert.Null(TestVnasData.NavigationDb!.GetStationDeclination("ZZZZZ"));

    /// <summary>
    /// Terminal NDBs (section PN) share the NDB record layout. In the pinned 2611 TestData CIFP, CH (ASHLY, KCHS)
    /// exists only there:
    /// SUSAPNKCHSK7 CH    K7003290HO W N32583477W080055084                       W0070           NARASHLY                         581292108
    /// ident 13-17, position N32583477/W080055084, magnetic variation (W0070) at 75-79. Pinned to that file, since
    /// the current cycle the suite resolves may not carry it.
    /// </summary>
    [Fact]
    public void CifpNavaid_ParsesTerminalNdb_ChAshly()
    {
        string testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        var bundled = new CifpResolveOptions(
            BundledGzPath: Path.Combine(testDataDir, "FAACIFP18.gz"),
            BundledManifestPath: Path.Combine(testDataDir, "cifp-manifest.json"),
            AllowDownload: false
        )
        {
            PinToBundled = true,
        };
        string bundledPath = Assert.IsType<string>(CifpPathResolver.ResolveSupplementaryBundledPath(bundled));
        IReadOnlyDictionary<string, CifpNavaid> navaids = CifpParser.ParseNavaids(bundledPath);

        CifpNavaid ch = navaids["CH"];
        Assert.Equal("ASHLY", ch.Name);
        Assert.Equal("NDB", ch.Type);
        Assert.InRange(ch.Lat, 32.9762, 32.9764);
        Assert.InRange(ch.Lon, -80.0976, -80.0974);
        Assert.Equal(-7.0, ch.StationDeclination);
    }

    [Theory]
    [InlineData("G0000", 0.0)] // grid-oriented station: 0, as cifparse's field 5.66
    [InlineData("T0000", 0.0)]
    [InlineData("W0150", -15.0)]
    [InlineData("X0150", null)]
    public void ParseArinc424MagneticVariation_ReadsHemisphereCodes(string field, double? expected) =>
        Assert.Equal(expected, CifpParser.ParseArinc424MagneticVariation(field));

    /// <summary>
    /// Published-course declination order: station declination, then the airport's variation of record, then the
    /// modelled declination at the navaid, then at the fix. MERIT (a New York waypoint, no station declination,
    /// modelled declination about W013) against KCCR (E013) separates the second and third.
    /// </summary>
    [Fact]
    public void GetPublishedCourseDeclination_FallsBackStationThenAirportThenNavaidThenFix()
    {
        NavigationDatabase navDb = TestVnasData.NavigationDb!;
        (double Lat, double Lon) merit = Assert.IsType<(double, double)>(navDb.GetFixPosition("MERIT"));
        (double Lat, double Lon) ccr = Assert.IsType<(double, double)>(navDb.GetFixPosition("CCR"));
        var ccrPosition = new LatLon(ccr.Lat, ccr.Lon);
        Assert.Null(navDb.GetStationDeclination("MERIT"));
        Assert.Null(navDb.GetAirportMagneticVariation("ZZZZ"));

        Assert.Equal(17.0, navDb.GetPublishedCourseDeclination("CCR", "KMOD", ccrPosition));
        Assert.Equal(13.0, navDb.GetPublishedCourseDeclination("MERIT", "KCCR", ccrPosition));
        Assert.Equal(MagneticDeclination.GetDeclination(merit.Lat, merit.Lon), navDb.GetPublishedCourseDeclination("MERIT", "ZZZZ", ccrPosition));
        Assert.Equal(MagneticDeclination.GetDeclination(ccrPosition), navDb.GetPublishedCourseDeclination(null, "ZZZZ", ccrPosition));
    }
}
