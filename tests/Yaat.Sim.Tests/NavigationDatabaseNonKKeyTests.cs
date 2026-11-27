using System.IO.Compression;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Proto;

namespace Yaat.Sim.Tests;

/// <summary>
/// YAAT-2909: data keyed by airport while a database builds (ARTCC custom procedures, gate pronunciations) must be keyed
/// by the id its reads use, whatever database is installed at the time, and the CIFP navaids of the other areas
/// (Latin America, Pacific, Canada) must not displace a USA fix or airport sharing their ident.
/// </summary>
[Collection("NavDbMutator")]
public class NavigationDatabaseNonKKeyTests
{
    private const string RenamedSid = "ZZHNL1";

    public NavigationDatabaseNonKKeyTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void ArtccData_AtAPacificAirport_IsFoundByFaaAndIcaoId()
    {
        string navDataPath = Path.Combine(AppContext.BaseDirectory, "TestData", "NavData.dat");
        string bundledGz = Path.Combine(AppContext.BaseDirectory, "TestData", "FAACIFP18.gz");
        string? cifpPath = TestVnasData.GetCifpPath();
        if (!File.Exists(navDataPath) || !File.Exists(bundledGz) || (cifpPath is null))
        {
            return;
        }

        string dir = Directory.CreateTempSubdirectory("yaat-nonk-keys-").FullName;
        try
        {
            string zhn = Path.Combine(dir, "ZHN");
            Directory.CreateDirectory(Path.Combine(zhn, "Procedures"));
            File.WriteAllLines(Path.Combine(zhn, "Procedures", "phnl.cifp"), RenamedHnl2Records(bundledGz));
            Directory.CreateDirectory(Path.Combine(zhn, "GatePronunciations"));
            Directory.CreateDirectory(Path.Combine(zhn, "Airports"));
            File.WriteAllText(Path.Combine(zhn, "Airports", "phnl.json"), """{ "airportId": "PHNL", "avoidTaxiways": [ { "name": "S" } ] }""");
            File.WriteAllText(
                Path.Combine(zhn, "GatePronunciations", "phnl.json"),
                """{ "airportId": "PHNL", "gates": [ { "name": "A1", "pronunciations": ["alpha one"] } ] }"""
            );
            NavDataSet navData = NavDataSet.Parser.ParseFrom(File.ReadAllBytes(navDataPath));

            // Built while no ICAO/FAA pairs are installed, as at startup before the first database is in place.
            NavigationDatabase db;
            using (NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting()))
            {
                db = new NavigationDatabase(navData, cifpPath, dir, supplementaryCifpFilePaths: []);
            }

            Assert.Contains(db.GetArtccFragmentSids("HNL"), sid => sid.ProcedureId == RenamedSid);
            Assert.Contains(db.GetArtccFragmentSids("PHNL"), sid => sid.ProcedureId == RenamedSid);
            Assert.Equal(["alpha one"], db.GetGatePronunciations("HNL", "A1"));
            Assert.Equal(["alpha one"], db.GetGatePronunciations("PHNL", "A1"));
            Assert.Contains("S", db.AirportSidecars.GetAvoidedTaxiways("HNL"));
            Assert.Contains("S", db.AirportSidecars.GetAvoidedTaxiways("PHNL"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("TRI", 36.475, -82.407)]
    [InlineData("PNA", 42.795, -109.807)]
    public void CifpNavaidOfAnotherArea_DoesNotDisplaceTheUsaFix(string ident, double lat, double lon)
    {
        // The Latin American area holds NDB TRI (Piarco) and VOR PNA (Punta Cana); in the USA they are Tri-Cities and Pinedale.
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        (double Lat, double Lon)? position = navDb.GetFixPosition(ident);

        Assert.NotNull(position);
        Assert.InRange(position.Value.Lat, lat - 0.3, lat + 0.3);
        Assert.InRange(position.Value.Lon, lon - 0.3, lon + 0.3);
        Assert.Null(navDb.GetNavaidName(ident));
    }

    [Fact]
    public void CifpNavaidOfAUsRegionInAnotherArea_PlacesAsAUsaNavaid()
    {
        // The Honolulu VORTAC is filed under the Pacific area (SPACD) but region PH, a US region: it names HNL as a USA navaid would.
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        Assert.Equal("HONOLULU", navDb.GetNavaidName("HNL"));
        Assert.True(navDb.IsRadialNavaid("HNL"));
    }

    [Fact]
    public void ParseNavaids_IdentInTwoAreas_KeepsTheUsaRecord()
    {
        // NDB ST is PESTE in the Latin American area and HUSSK in the USA.
        string? cifpPath = TestVnasData.GetCifpPath();
        if (cifpPath is null)
        {
            return;
        }

        Assert.Equal("HUSSK", CifpParser.ParseNavaids(cifpPath)["ST"].Name);
    }

    private static List<string> RenamedHnl2Records(string bundledGz)
    {
        using var gz = new GZipStream(File.OpenRead(bundledGz), CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        var records = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("SPACP PHNLPHDHNL2 ", StringComparison.Ordinal))
            {
                records.Add(line[..13] + RenamedSid.PadRight(6) + line[19..]);
            }
        }

        Assert.NotEmpty(records);
        return records;
    }
}
