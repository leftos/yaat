using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// SAAB SAID facility-config parsing. Unlike the flat <c>asdexConfiguration</c>, the SAID schema
/// nests the vendor-specific block under <c>saabConfiguration</c> and carries a string
/// <c>vendor</c> enum. <see cref="ArtccConfigResolver.GetAllSaidAirports"/> emits only the Saab
/// vendor, defaults visibility to ASDE-X's 15 nm / 1500 ft (SAID config carries neither), and
/// takes coordinates from <c>saabConfiguration.towerLocation</c>.
/// </summary>
public sealed class SaidConfigParseTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public SaidConfigParseTests()
    {
        // GetAllSaidAirports passes NavigationDatabase.Instance to the collector; pin it so the
        // singleton getter doesn't throw when another test class hasn't initialized it yet.
        TestVnasData.EnsureInitialized();
    }

    private const string SaidFacilityJson = """
        {
          "id": "ZZZ",
          "facility": {
            "id": "ZZZ-ROOT",
            "type": "Artcc",
            "name": "Test ARTCC",
            "childFacilities": [
              {
                "id": "OAK",
                "type": "AirportTracon",
                "name": "OAK SAID",
                "saidConfiguration": {
                  "vendor": "Saab",
                  "saabConfiguration": {
                    "videoMapId": "01ABC",
                    "defaultRotation": 28,
                    "defaultZoomRange": 100,
                    "fixRules": [ { "id": "r1", "searchPattern": "SEGUL#", "fixId": "SEGUL" } ],
                    "useDestinationIdAsFix": true,
                    "towerLocation": { "lat": 37.72, "lon": -122.22 }
                  }
                }
              }
            ]
          }
        }
        """;

    private static ArtccConfigRoot Load(string json) => JsonSerializer.Deserialize<ArtccConfigRoot>(json, JsonOptions)!;

    private static FacilityConfig Oak(ArtccConfigRoot root) => root.Facility.ChildFacilities.First(f => f.Id == "OAK");

    [Fact]
    public void Deserialize_NestedVendorShape_PopulatesSaabConfiguration()
    {
        var said = Oak(Load(SaidFacilityJson)).SaidConfiguration!;

        Assert.Equal(SaidVendor.Saab, said.Vendor);
        Assert.NotNull(said.SaabConfiguration);
        Assert.Equal("01ABC", said.SaabConfiguration!.VideoMapId);
        Assert.Equal(28, said.SaabConfiguration.DefaultRotation);
        Assert.Equal(100, said.SaabConfiguration.DefaultZoomRange);
        Assert.True(said.SaabConfiguration.UseDestinationIdAsFix);
        Assert.Equal(37.72, said.SaabConfiguration.TowerLocation!.Lat);
        Assert.Equal(-122.22, said.SaabConfiguration.TowerLocation.Lon);

        var rule = Assert.Single(said.SaabConfiguration.FixRules);
        Assert.Equal("SEGUL#", rule.SearchPattern);
        Assert.Equal("SEGUL", rule.FixId);
    }

    [Fact]
    public void GetAllSaidAirports_EmitsSaabAirport_WithDefaultRangeCeilingAndTowerCoords()
    {
        var airport = Assert.Single(Load(SaidFacilityJson).GetAllSaidAirports());

        Assert.Equal("OAK", airport.AirportId);
        Assert.Equal(37.72, airport.Lat);
        Assert.Equal(-122.22, airport.Lon);
        Assert.Equal(15, airport.Range); // ASDE-X default — SAID config carries no range
        Assert.Equal(1500, airport.Ceiling); // ASDE-X default — SAID config carries no ceiling
    }

    [Fact]
    public void GetAllSaidAirports_NonSaabVendor_EmitsNothing()
    {
        var json = SaidFacilityJson.Replace("\"vendor\": \"Saab\"", "\"vendor\": \"UAvionix\"", StringComparison.Ordinal);
        Assert.Empty(Load(json).GetAllSaidAirports());
    }

    [Fact]
    public void FacilityWithoutSaidConfiguration_HasNullConfig()
    {
        var json = SaidFacilityJson.Replace("\"saidConfiguration\"", "\"unusedConfiguration\"", StringComparison.Ordinal);
        Assert.Null(Oak(Load(json)).SaidConfiguration);
        Assert.Empty(Load(json).GetAllSaidAirports());
    }
}
