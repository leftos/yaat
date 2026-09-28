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
    /// Terminal NDBs (section PN) share the NDB record layout. In the committed TestData CIFP, FN (COLLN, KFNL) exists
    /// only there: E0090. Pinned to that file, since the current cycle the suite resolves may not carry it.
    /// </summary>
    [Fact]
    public void CifpNavaid_ParsesTerminalNdb_FnColln()
    {
        var bundled = new CifpResolveOptions(BundledGzPath: Path.Combine(AppContext.BaseDirectory, "TestData", "FAACIFP18.gz"), AllowDownload: false);
        string bundledPath = Assert.IsType<string>(CifpPathResolver.ResolveSupplementaryBundledPath(bundled));
        IReadOnlyDictionary<string, CifpNavaid> navaids = CifpParser.ParseNavaids(bundledPath);

        CifpNavaid fn = navaids["FN"];
        Assert.Equal("COLLN", fn.Name);
        Assert.Equal("NDB", fn.Type);
        Assert.Equal(9.0, fn.StationDeclination);
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
