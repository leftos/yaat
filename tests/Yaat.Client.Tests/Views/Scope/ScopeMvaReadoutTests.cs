using Xunit;
using Yaat.Client.Views.Scope;
using Yaat.Sim;
using Yaat.Sim.Data.Mva;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>The Ctrl-hover MVA readout's text: the floor and sector under the cursor, or nothing where no chart covers it.</summary>
public class ScopeMvaReadoutTests
{
    // KOAK, inside NCT's MVA chart.
    private static readonly LatLon Koak = new(37.7213, -122.2208);

    // Mid-Pacific, under no MVA chart.
    private static readonly LatLon OpenOcean = new(30.0, -140.0);

    [Fact]
    public void OverAnMvaSector_NamesItsFloorAndSector()
    {
        MvaSector sector = MvaDatabase.Default.FindSector(Koak, preferredFacility: null)!;

        Assert.Equal($"MVA {sector.FloorFtMsl} ({sector.Sector})", ScopeMvaReadout.TextAt(MvaDatabase.Default, Koak, null));
    }

    [Fact]
    public void OutsideEverySector_GivesNoText()
    {
        Assert.Null(MvaDatabase.Default.FindSector(OpenOcean, preferredFacility: null));

        Assert.Null(ScopeMvaReadout.TextAt(MvaDatabase.Default, OpenOcean, null));
    }

    [Fact]
    public void NoMvaDataForTheFacility_GivesNoText()
    {
        var empty = new MvaDatabase([]);

        Assert.Null(ScopeMvaReadout.TextAt(empty, Koak, "NCT"));
    }
}
