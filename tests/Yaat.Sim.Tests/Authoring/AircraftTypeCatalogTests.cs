using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// <see cref="AircraftSpecEntry"/> reads the vNAS AircraftSpecs keys, and <c>AircraftTypeCatalog</c> keeps that list for the
/// Scenario Editor's type autocomplete.
/// </summary>
public class AircraftTypeCatalogTests
{
    private static readonly string SpecsPath = Path.Combine(AppContext.BaseDirectory, "TestData", "AircraftSpecs.json");

    private static List<AircraftSpecEntry> ReadSpecs() =>
        JsonSerializer.Deserialize<List<AircraftSpecEntry>>(File.ReadAllText(SpecsPath)) ?? throw new InvalidOperationException("No specs");

    [Fact]
    public void SpecEntry_ReadsManufacturerModelAndDescription_FromTheRealData()
    {
        AircraftSpecEntry b738 = ReadSpecs().First(s => s.Designator == "B738");

        Assert.Equal("BOEING", b738.Manufacturer);
        Assert.Equal("737-800", b738.Model);
        Assert.Equal("L2J", b738.TypeDescription);
        Assert.Equal("M", b738.Wtc);
    }

    [Fact]
    public void IsKnown_MatchesARealDesignator_InAnyCase()
    {
        TestVnasData.EnsureInitialized();

        Assert.True(AircraftTypeCatalog.IsKnown("B738"));
        Assert.True(AircraftTypeCatalog.IsKnown("b738"));
        Assert.True(AircraftTypeCatalog.IsKnown(" B738 "));
        Assert.False(AircraftTypeCatalog.IsKnown("ZZZZ"));
    }

    [Fact]
    public void Search_ReturnsAtMostLimit_PrefixMatches_InOrdinalOrder()
    {
        TestVnasData.EnsureInitialized();

        IReadOnlyList<AircraftTypeEntry> found = AircraftTypeCatalog.Search("B73", 5);

        Assert.NotEmpty(found);
        Assert.True(found.Count <= 5);
        Assert.All(found, e => Assert.StartsWith("B73", e.Designator, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(found.Select(e => e.Designator).Order(StringComparer.Ordinal), found.Select(e => e.Designator));
    }

    [Fact]
    public void Search_KeepsTheFirstRowOfADesignator()
    {
        TestVnasData.EnsureInitialized();

        AircraftTypeEntry b738 = Assert.Single(AircraftTypeCatalog.Search("B738", 5), e => e.Designator == "B738");

        Assert.Equal(new AircraftTypeEntry("B738", "BOEING", "737-800", "M"), b738);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Search_BlankPrefix_ReturnsNone(string prefix)
    {
        TestVnasData.EnsureInitialized();

        Assert.Empty(AircraftTypeCatalog.Search(prefix, 5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Search_NoRoomInTheLimit_ReturnsNone(int limit)
    {
        TestVnasData.EnsureInitialized();

        Assert.Empty(AircraftTypeCatalog.Search("B73", limit));
    }

    [Fact]
    public void EmptyCatalog_SearchesNothing_AndKnowsNothing()
    {
        TestVnasData.EnsureInitialized();

        using (AircraftTypeCatalog.ScopedOverride([]))
        {
            Assert.Empty(AircraftTypeCatalog.Search("B73", 5));
            Assert.False(AircraftTypeCatalog.IsKnown("B738"));
        }

        Assert.True(AircraftTypeCatalog.IsKnown("B738"));
    }
}
