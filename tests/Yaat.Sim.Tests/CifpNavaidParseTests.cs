using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// CIFP section D navaid records over the committed CIFP: a record with a VOR keeps the VOR position (columns 33-51), and
/// one without a VOR (class column 28 blank: a standalone TACAN, DME or ILS/DME) takes its DME position (columns 56-74).
/// </summary>
public class CifpNavaidParseTests
{
    private static readonly Lazy<IReadOnlyDictionary<string, CifpNavaid>> Navaids = new(() => CifpParser.ParseNavaids(TestVnasData.GetCifpPath()!));

    public CifpNavaidParseTests() => TestVnasData.EnsureInitialized();

    /// <summary>The section D primary record of <paramref name="ident"/>, read straight from the committed CIFP.</summary>
    private static string Record(string ident) =>
        File.ReadLines(TestVnasData.GetCifpPath()!)
            .First(line => (line.Length > 50) && (line[4] == 'D') && (line[5] == ' ') && (line[13..18].Trim() == ident));

    [Fact]
    public void VorWithDme_KeepsTheVorPosition_Osi()
    {
        // SUSAD        OSI   K2011390VDLW N37233300W122165269    N37233365W122165463E0170022701     NARWOODSIDE
        CifpNavaid osi = Navaids.Value["OSI"];

        Assert.Equal("VOR", osi.Type);
        Assert.Equal(37.0 + (23.0 / 60) + (33.00 / 3600), osi.Lat, 9);
        Assert.Equal(-(122.0 + (16.0 / 60) + (52.69 / 3600)), osi.Lon, 9);
    }

    [Fact]
    public void BlankVorWithTacan_IsATacanAtTheDmePosition_Nuq()
    {
        // SUSAD        NUQ   K2011760 TTW                    NUQ N37255669W122032731E0170000040     NARMOFFETT
        CifpNavaid nuq = Navaids.Value["NUQ"];

        Assert.Equal("TACAN", nuq.Type);
        Assert.Equal("MOFFETT", nuq.Name);
        Assert.Equal(37.4324, nuq.Lat, 4);
        Assert.Equal(-122.0576, nuq.Lon, 4);
    }

    [Fact]
    public void BlankVorWithMilitaryTacan_IsATacan_Dys()
    {
        // SUSAD        DYS   K4013360 MLW                    DYS N32250664W099512538E0050017841     NARDYESS
        CifpNavaid dys = Navaids.Value["DYS"];

        Assert.Equal("TACAN", dys.Type);
        Assert.Equal(32.0 + (25.0 / 60) + (6.64 / 3600), dys.Lat, 6);
        Assert.Equal(-(99.0 + (51.0 / 60) + (25.38 / 3600)), dys.Lon, 6);
    }

    [Fact]
    public void BlankVorWithDme_IsADmeAtTheDmePosition_Wvi()
    {
        // SUSAD        WVI   K2011425 DLW                    WVI N36562113W121470760E0126001501     NARWATSONVILLE
        CifpNavaid wvi = Navaids.Value["WVI"];

        Assert.Equal("DME", wvi.Type);
        Assert.Equal("WATSONVILLE", wvi.Name);
        Assert.Equal(36.9392, wvi.Lat, 4);
        Assert.Equal(-121.7854, wvi.Lon, 4);
    }

    [Fact]
    public void BlankVorWithIlsDme_IsAnIlsDme_Iinb()
    {
        // SUSAD KOAKK2 IINB  K2010870 ITW                    IINBN37433308W122145901E0140000230     NAROAKLAND SAN FRANCISCO BA
        CifpNavaid iinb = Navaids.Value["IINB"];

        Assert.Equal("ILS/DME", iinb.Type);
        Assert.Equal(37.0 + (43.0 / 60) + (33.08 / 3600), iinb.Lat, 6);
        Assert.Equal(-(122.0 + (14.0 / 60) + (59.01 / 3600)), iinb.Lon, 6);
    }

    /// <summary>The class field " ITWN" carries an N at column 32, inside the old latitude scan; the DME position still reads.</summary>
    [Fact]
    public void IlsDmeWithClassEndingInN_ParsesFromTheDmePosition_Irne()
    {
        // SUSAD KACKK6 IRNE  K6010910 ITWN                   IRNEN41144636W070043217W0140000330     NARNANTUCKET MEML
        CifpNavaid irne = Navaids.Value["IRNE"];

        Assert.Equal("ILS/DME", irne.Type);
        Assert.Equal(41.0 + (14.0 / 60) + (46.36 / 3600), irne.Lat, 6);
        Assert.Equal(-(70.0 + (4.0 / 60) + (32.17 / 3600)), irne.Lon, 6);
    }

    [Fact]
    public void EveryRecordWithoutAVor_Parses()
    {
        int withoutVor = Navaids.Value.Values.Count(navaid => navaid.Type is "TACAN" or "DME" or "ILS/DME");

        Assert.Equal(1266, withoutVor);
    }

    /// <summary>The NUQ record with its DME position blanked holds no position at either column, so it is skipped.</summary>
    [Fact]
    public void RecordWithNeitherAVorNorADmePosition_IsSkipped()
    {
        string nuq = Record("NUQ");
        string noPosition = string.Concat(nuq.AsSpan(0, 55), new string(' ', 19), nuq.AsSpan(74));

        Assert.NotNull(CifpParser.ParseNavaidRecord(nuq, isNdb: false));
        Assert.Null(CifpParser.ParseNavaidRecord(noPosition, isNdb: false));
    }

    /// <summary>A record with no VOR whose ranging class is none of T, M, D or I is no facility YAAT can name.</summary>
    [Theory]
    [InlineData(" P   ")]
    [InlineData("     ")]
    public void ClassifyNavaid_NoVorWithAnUnknownRangingClass_IsNull(string navClass) =>
        Assert.Null(CifpParser.ClassifyNavaid(isNdb: false, navClass));

    [Theory]
    [InlineData("VD   ", false, "VOR")]
    [InlineData("VT   ", false, "VORTAC")]
    [InlineData(" T   ", false, "TACAN")]
    [InlineData(" M   ", false, "TACAN")]
    [InlineData(" D   ", false, "DME")]
    [InlineData(" I   ", false, "ILS/DME")]
    [InlineData("     ", true, "NDB")]
    public void ClassifyNavaid_KnownClasses(string navClass, bool isNdb, string type) =>
        Assert.Equal(type, CifpParser.ClassifyNavaid(isNdb, navClass));
}
