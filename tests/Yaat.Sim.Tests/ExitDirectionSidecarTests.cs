using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

/// <summary>
/// The <c>exitDirections</c> section of the per-airport sidecar overrides the default exit
/// (turn-off) side per landing runway end — needed where the GeoJSON <c>turnoff</c> flip for the
/// reciprocal end is wrong (issue #405, KMIA 26R). Loader validation is warn-don't-throw, matching
/// every other section.
/// </summary>
public class ExitDirectionSidecarTests
{
    private static AirportSidecarLoadResult LoadSidecar(string json)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "exitdir-sidecar-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "Airports");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(Path.Combine(categoryDir, "test.json"), json);
            return AirportSidecarLoader.LoadAll(tempDir);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_ReadsExitDirectionsSection()
    {
        AirportSidecarLoadResult result = LoadSidecar(
            """
            {
              "airportId": "KMIA",
              "exitDirections": [
                { "runway": "26R", "side": "left", "notes": "facility request" }
              ]
            }
            """
        );

        Assert.Empty(result.Warnings);
        AirportSidecar airport = Assert.Single(result.Airports);
        ExitDirectionOverride entry = Assert.Single(airport.ExitDirections);
        Assert.Equal("26R", entry.Runway);
        Assert.Equal(ExitSide.Left, entry.Side);
        Assert.Equal("facility request", entry.Notes);
    }

    [Fact]
    public void LoadAll_NormalizesDesignatorAndSideCase()
    {
        AirportSidecarLoadResult result = LoadSidecar(
            """
            {
              "airportId": "KTST",
              "exitDirections": [ { "runway": " 8l ", "side": "RIGHT" } ]
            }
            """
        );

        Assert.Empty(result.Warnings);
        ExitDirectionOverride entry = Assert.Single(Assert.Single(result.Airports).ExitDirections);
        Assert.Equal("08L", entry.Runway);
        Assert.Equal(ExitSide.Right, entry.Side);
    }

    [Theory]
    [InlineData("""{ "side": "left" }""", "missing runway")]
    [InlineData("""{ "runway": "26R", "side": "up" }""", "must be 'left' or 'right'")]
    [InlineData("""{ "runway": "26R" }""", "must be 'left' or 'right'")]
    public void LoadAll_InvalidEntry_WarnsAndSkips(string entry, string expectedWarningFragment)
    {
        AirportSidecarLoadResult result = LoadSidecar(
            $$"""
            { "airportId": "KTST", "exitDirections": [ {{entry}} ] }
            """
        );

        Assert.Empty(Assert.Single(result.Airports).ExitDirections);
        Assert.Contains(result.Warnings, w => w.Contains(expectedWarningFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LoadAll_DuplicateRunwayInOneFile_WarnsAndLastWins()
    {
        AirportSidecarLoadResult result = LoadSidecar(
            """
            {
              "airportId": "KTST",
              "exitDirections": [
                { "runway": "26R", "side": "left" },
                { "runway": "26r", "side": "right" }
              ]
            }
            """
        );

        Assert.Contains(result.Warnings, w => w.Contains("duplicate runway", StringComparison.OrdinalIgnoreCase));
        ExitDirectionOverride entry = Assert.Single(Assert.Single(result.Airports).ExitDirections);
        Assert.Equal(ExitSide.Right, entry.Side);
    }

    [Fact]
    public void Catalog_GetExitDirection_NormalizesAirportAndDesignatorForms()
    {
        var catalog = new AirportSidecarCatalog([
            new AirportSidecar("KMIA") { ExitDirections = [new ExitDirectionOverride("26R", null, ExitSide.Left, null)] },
        ]);

        Assert.Equal(ExitSide.Left, catalog.GetExitDirection("KMIA", "26R"));
        Assert.Equal(ExitSide.Left, catalog.GetExitDirection("MIA", "26R"));
        Assert.Null(catalog.GetExitDirection("KMIA", "8L"));
        Assert.Null(catalog.GetExitDirection("KOAK", "26R"));
        Assert.Null(catalog.GetExitDirection("", "26R"));
        Assert.Null(catalog.GetExitDirection("KMIA", ""));
    }

    [Fact]
    public void Catalog_GetExitDirection_NormalizesSingleDigitDesignators()
    {
        var catalog = new AirportSidecarCatalog([
            new AirportSidecar("KTST") { ExitDirections = [new ExitDirectionOverride("09", null, ExitSide.Left, null)] },
        ]);

        Assert.Equal(ExitSide.Left, catalog.GetExitDirection("KTST", "9"));
        Assert.Equal(ExitSide.Left, catalog.GetExitDirection("KTST", "09"));
    }

    [Theory]
    [InlineData(""" "exitDirections": [ { "runway": "28R", "side": "right", "arrivalClass": "bizjet" } ] """, "arrivalClass must be 'ga' or 'cargo'")]
    [InlineData(""" "cargoOperators": ["FX"] """, "three-letter ICAO operator code")]
    [InlineData(""" "cargoOperators": ["FDX1"] """, "three-letter ICAO operator code")]
    [InlineData(""" "parkingAreas": [ { "area": " ", "stands": ["SIG*"] } ] """, "missing area")]
    [InlineData(""" "parkingAreas": [ { "area": "Signature", "stands": [] } ] """, "needs at least one stand")]
    [InlineData(""" "parkingAreas": [ { "area": "Signature", "stands": [" "] } ] """, "no blank stand")]
    [InlineData(""" "parkingAreas": [ { "area": "Signature", "stands": ["S*G"] } ] """, "may only end a stand pattern")]
    [InlineData(""" "parkingAreas": [ { "area": "Signature", "stands": ["{n}-*"] } ] """, "uses {n} but the area does not")]
    public void LoadAll_InvalidArrivalSection_WarnsAndSkips(string section, string expectedWarningFragment)
    {
        AirportSidecarLoadResult result = LoadSidecar(
            $$"""
            { "airportId": "KTST", {{section}} }
            """
        );

        AirportSidecar airport = Assert.Single(result.Airports);
        Assert.Empty(airport.ExitDirections);
        Assert.Empty(airport.CargoOperators);
        Assert.Empty(airport.ParkingAreas);
        Assert.Contains(result.Warnings, w => w.Contains(expectedWarningFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LoadAll_ReadsTheArrivalSections_Normalized()
    {
        AirportSidecarLoadResult result = LoadSidecar(
            """
            {
              "airportId": "KTST",
              "exitDirections": [ { "runway": "28R", "side": "right", "arrivalClass": "Cargo" } ],
              "cargoOperators": [" atn ", "ATN"],
              "parkingAreas": [ { "area": "Plot {n}", "stands": ["{n}-*"] } ]
            }
            """
        );

        Assert.Empty(result.Warnings);
        AirportSidecar airport = Assert.Single(result.Airports);
        Assert.Equal(new ExitDirectionOverride("28R", ArrivalClass.Cargo, ExitSide.Right, null), Assert.Single(airport.ExitDirections));
        Assert.Equal(["ATN"], airport.CargoOperators);
        ParkingAreaAlias area = Assert.Single(airport.ParkingAreas);
        Assert.Equal(("PLOT{N}", "{N}-*"), (area.Area, Assert.Single(area.Stands)));
    }

    /// <summary>A parking area's <c>maxAdg</c> parses to its design group, and is null on an area without one.</summary>
    [Fact]
    public void LoadAll_ParkingAreaMaxAdg_ParsesAndIsNullWhenAbsent()
    {
        AirportSidecarLoadResult result = LoadSidecar(
            """
            {
              "airportId": "KTST",
              "parkingAreas": [
                { "area": "Signature", "stands": ["SIG*"], "maxAdg": "III" },
                { "area": "Plot {n}", "stands": ["{n}-*"] }
              ]
            }
            """
        );

        Assert.Empty(result.Warnings);
        IReadOnlyList<ParkingAreaAlias> areas = Assert.Single(result.Airports).ParkingAreas;
        Assert.Equal(2, areas.Count);
        Assert.Equal(AirplaneDesignGroup.III, areas[0].MaxAdg);
        Assert.Null(areas[1].MaxAdg);
    }

    /// <summary>A <c>maxAdg</c> that is not I to VI is warned about, naming the entry, and the area loads without it.</summary>
    [Theory]
    [InlineData("VII")]
    [InlineData("3")]
    [InlineData("")]
    public void LoadAll_InvalidParkingAreaMaxAdg_WarnsAndLoadsTheAreaWithoutIt(string maxAdg)
    {
        AirportSidecarLoadResult result = LoadSidecar(
            $$"""
            { "airportId": "KTST", "parkingAreas": [ { "area": "Signature", "stands": ["SIG*"], "maxAdg": "{{maxAdg}}" } ] }
            """
        );

        string warning = Assert.Single(result.Warnings);
        Assert.Contains("parkingAreas[0] (Signature): maxAdg", warning, StringComparison.Ordinal);
        ParkingAreaAlias area = Assert.Single(Assert.Single(result.Airports).ParkingAreas);
        Assert.Equal("SIGNATURE", area.Area);
        Assert.Null(area.MaxAdg);
    }

    /// <summary>The <c>gaOperators</c> section mirrors <c>cargoOperators</c>: trimmed, upper-cased, de-duplicated.</summary>
    [Fact]
    public void Loader_ReadsGaOperators()
    {
        AirportSidecarLoadResult result = LoadSidecar(
            """
            {
              "airportId": "KTST",
              "gaOperators": [" eja ", "EJA", "XOJ"]
            }
            """
        );

        Assert.Empty(result.Warnings);
        Assert.Equal(["EJA", "XOJ"], Assert.Single(result.Airports).GaOperators);
    }

    /// <summary>A code that is not three upper-case letters is warned and skipped, naming its own section.</summary>
    [Theory]
    [InlineData("FX")]
    [InlineData("EJA1")]
    [InlineData("TOOLS")]
    public void Loader_InvalidGaOperator_WarnsAndSkips(string code)
    {
        AirportSidecarLoadResult result = LoadSidecar(
            $$"""
            { "airportId": "KTST", "gaOperators": ["{{code}}"] }
            """
        );

        Assert.Empty(Assert.Single(result.Airports).GaOperators);
        Assert.Contains(result.Warnings, w => w.Contains("gaOperators[0]", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("three-letter ICAO operator code", StringComparison.Ordinal));
    }

    /// <summary>
    /// A GA arrival for exit-side purposes is a registration written without its hyphen (a US N-number, or four to six
    /// upper-case letters) or an airline-style callsign whose operator the sidecar lists in <c>gaOperators</c>; cargo wins
    /// first, and an airline on neither list, or any other shape, is null.
    /// </summary>
    [Theory]
    [InlineData("N377L", ArrivalClass.GeneralAviation)]
    [InlineData("N12345", ArrivalClass.GeneralAviation)]
    [InlineData("CFABC", ArrivalClass.GeneralAviation)]
    [InlineData("GABCD", ArrivalClass.GeneralAviation)]
    [InlineData("DEABC", ArrivalClass.GeneralAviation)]
    [InlineData("EJA791", ArrivalClass.GeneralAviation)]
    [InlineData("XOJ712", ArrivalClass.GeneralAviation)]
    [InlineData("OPT55", ArrivalClass.GeneralAviation)]
    [InlineData("ATN3303", ArrivalClass.Cargo)]
    [InlineData("UAL2164", null)]
    [InlineData("QQQ123", null)]
    [InlineData("RCH123", null)]
    [InlineData("C2134", null)]
    [InlineData("N0ABC", null)]
    [InlineData("NABCDEFG", null)]
    [InlineData("N123456", null)]
    [InlineData("GABCDEFG", null)]
    public void Classify_GaIsARegistrationOrAListedOperator(string callsign, ArrivalClass? expected)
    {
        var cargo = new HashSet<string>(StringComparer.Ordinal) { "ATN" };
        var ga = new HashSet<string>(StringComparer.Ordinal) { "EJA", "XOJ", "OPT" };

        Assert.Equal(expected, ArrivalExitSide.Classify(callsign, cargo, ga));
    }

    [Fact]
    public void Catalog_ClassEntries_CrossFileMerge_LastWins_AndStayOutOfTheClassLessLookup()
    {
        var catalog = new AirportSidecarCatalog([
            new AirportSidecar("KSFO") { ExitDirections = [new ExitDirectionOverride("28R", ArrivalClass.Cargo, ExitSide.Left, null)] },
            new AirportSidecar("SFO") { ExitDirections = [new ExitDirectionOverride("28R", ArrivalClass.Cargo, ExitSide.Right, null)] },
        ]);

        Assert.Equal(ExitSide.Right, catalog.GetExitDirection("KSFO", "28R", ArrivalClass.Cargo));
        Assert.Null(catalog.GetExitDirection("KSFO", "28R", ArrivalClass.GeneralAviation));
        Assert.Null(catalog.GetExitDirection("KSFO", "28R"));
    }

    [Fact]
    public void Catalog_GetExitDirection_CrossFileMerge_LastWins()
    {
        var catalog = new AirportSidecarCatalog([
            new AirportSidecar("KMIA") { ExitDirections = [new ExitDirectionOverride("26R", null, ExitSide.Right, null)] },
            new AirportSidecar("MIA") { ExitDirections = [new ExitDirectionOverride("26R", null, ExitSide.Left, null)] },
        ]);

        Assert.Equal(ExitSide.Left, catalog.GetExitDirection("KMIA", "26R"));
    }
}
