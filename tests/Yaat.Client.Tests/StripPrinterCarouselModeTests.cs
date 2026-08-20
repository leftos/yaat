using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>
/// The printer modal's carousel split follows the facility's vNAS strips config
/// (<c>enableArrivalStrips</c> + <c>enableSeparateArrDepPrinters</c>): separate printers
/// demux arrival strips into their own carousel; a unified facility routes everything —
/// arrival strips included — into the single departure carousel, and the badge shows one
/// total instead of the "dep/arr" split. The title prefix "(N) " reflects total pending.
/// </summary>
public class StripPrinterCarouselModeTests
{
    private static StripItemViewModel Item(string id, StripItemType type) => new(new StripItemDto(id, "N248ZV", false, type, false, [], "OAK", ""));

    private static Dictionary<string, StripItemViewModel> Lookup(params StripItemViewModel[] items) => items.ToDictionary(i => i.Id, i => i);

    [Fact]
    public void ReplaceAll_SeparateCarousels_SplitsArrivalsOut()
    {
        var printer = new StripPrinterViewModel { SeparateArrivalCarousel = true };
        var dep = Item("STRIP_N248ZV", StripItemType.DepartureStrip);
        var arr = Item("ARRIVAL_N248ZV", StripItemType.ArrivalStrip);

        printer.ReplaceAll([dep.Id, arr.Id], Lookup(dep, arr));

        Assert.Equal([dep], printer.DepartureQueue);
        Assert.Equal([arr], printer.ArrivalQueue);
        Assert.Equal("1/1", printer.BadgeText);
        Assert.Equal("Departure Printer:", printer.DepartureSectionLabel);
    }

    [Fact]
    public void ReplaceAll_UnifiedCarousel_RoutesArrivalsIntoDepartureQueue()
    {
        var printer = new StripPrinterViewModel { SeparateArrivalCarousel = false };
        var dep = Item("STRIP_N248ZV", StripItemType.DepartureStrip);
        var arr = Item("ARRIVAL_N248ZV", StripItemType.ArrivalStrip);

        printer.ReplaceAll([dep.Id, arr.Id], Lookup(dep, arr));

        Assert.Equal([dep, arr], printer.DepartureQueue);
        Assert.Empty(printer.ArrivalQueue);
        Assert.Equal("2", printer.BadgeText);
        Assert.Equal("Printer:", printer.DepartureSectionLabel);
    }

    [Fact]
    public void PendingCount_TracksBothQueues()
    {
        var printer = new StripPrinterViewModel { SeparateArrivalCarousel = true };
        var dep = Item("STRIP_N248ZV", StripItemType.DepartureStrip);
        var arr = Item("ARRIVAL_N248ZV", StripItemType.ArrivalStrip);

        Assert.Equal(0, printer.PendingCount);
        printer.ReplaceAll([dep.Id, arr.Id], Lookup(dep, arr));
        Assert.Equal(2, printer.PendingCount);
        printer.Clear();
        Assert.Equal(0, printer.PendingCount);
    }

    [Theory]
    [InlineData(0, "OAK", "vStrips", true, "OAK - vStrips (YAAT)")]
    [InlineData(3, "OAK", "vStrips", true, "(3) OAK - vStrips (YAAT)")]
    [InlineData(3, "OAK", "vStrips", false, "(3) OAK - vStrips")]
    [InlineData(0, null, "vStrips", true, "vStrips (YAAT)")]
    [InlineData(2, null, "vTDLS", false, "(2) vTDLS")]
    [InlineData(1, "NCT", "vTDLS", true, "(1) NCT - vTDLS (YAAT)")]
    public void ClientProductTitle_BuildsFacilityAndPendingCountVariants(
        int pending,
        string? facilityId,
        string product,
        bool includeYaatSuffix,
        string expected
    )
    {
        Assert.Equal(expected, ClientProductTitle.Build(pending, facilityId, product, includeYaatSuffix));
    }
}
