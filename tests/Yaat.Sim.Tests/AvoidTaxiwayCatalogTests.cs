using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for <see cref="AvoidTaxiwayCatalog"/> — airport-keyed lookup of avoided taxiway names,
/// accepting both ICAO ("KOAK") and FAA ("OAK") forms.
/// </summary>
public class AvoidTaxiwayCatalogTests
{
    private static AvoidTaxiwayCatalog Sample() =>
        new([
            new AvoidTaxiwayAirport("KOAK", [new AvoidTaxiwayEntry { Name = "S" }]),
            new AvoidTaxiwayAirport("OAK", [new AvoidTaxiwayEntry { Name = "Q3" }]),
        ]);

    [Fact]
    public void GetAvoidedTaxiways_AcceptsBothIcaoAndFaa()
    {
        var catalog = new AvoidTaxiwayCatalog([new AvoidTaxiwayAirport("KOAK", [new AvoidTaxiwayEntry { Name = "S" }])]);

        Assert.Contains("S", catalog.GetAvoidedTaxiways("KOAK"));
        Assert.Contains("S", catalog.GetAvoidedTaxiways("OAK"));
        Assert.Contains("s", catalog.GetAvoidedTaxiways("oak")); // case-insensitive membership
    }

    [Fact]
    public void Ctor_CombinesEntriesForSameAirportAcrossFiles()
    {
        var catalog = Sample();

        var avoided = catalog.GetAvoidedTaxiways("KOAK");
        Assert.Contains("S", avoided);
        Assert.Contains("Q3", avoided);
        Assert.Equal(2, avoided.Count);
    }

    [Fact]
    public void GetAvoidedTaxiways_UnknownAirport_ReturnsEmptyNeverNull()
    {
        var catalog = Sample();

        var avoided = catalog.GetAvoidedTaxiways("KSFO");
        Assert.NotNull(avoided);
        Assert.Empty(avoided);
    }

    [Fact]
    public void Empty_ReturnsEmptyForAnyAirport()
    {
        Assert.Empty(AvoidTaxiwayCatalog.Empty.GetAvoidedTaxiways("KOAK"));
        Assert.Empty(AvoidTaxiwayCatalog.Empty.GetAvoidedTaxiways(""));
    }
}
