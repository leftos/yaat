using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Tests;

/// <summary>
/// Regression coverage for TAXI command building from the ground view's right-click menu.
/// Verifies that taxi-spot destinations emit `$` and parking destinations emit `@`.
/// </summary>
public class GroundViewModelTaxiVariantsTests
{
    private static GroundViewModel MakeViewModel()
    {
        var connection = new ServerConnection();
        return new GroundViewModel(connection, sendCommand: (_, _, _) => Task.CompletedTask);
    }

    private static TaxiRoute EmptyRoute() => new() { Segments = [], HoldShortPoints = [] };

    [Fact]
    public void BuildTaxiCrossingVariants_TaxiSpot_EmitsDollarPrefix()
    {
        GroundViewModel vm = MakeViewModel();
        TaxiRoute route = EmptyRoute();
        var spot = new TaxiSpotDestination("I8L", IsTaxiSpot: true);

        List<(string Label, string Command, TaxiRoute Preview)> variants = vm.BuildTaxiCrossingVariants(route, spot, pathOverride: null);

        (string Label, string Command, TaxiRoute Preview) single = Assert.Single(variants);
        Assert.Equal("TAXI $I8L", single.Command);
    }

    [Fact]
    public void BuildTaxiCrossingVariants_Parking_EmitsAtPrefix()
    {
        GroundViewModel vm = MakeViewModel();
        TaxiRoute route = EmptyRoute();
        var spot = new TaxiSpotDestination("A12", IsTaxiSpot: false);

        List<(string Label, string Command, TaxiRoute Preview)> variants = vm.BuildTaxiCrossingVariants(route, spot, pathOverride: null);

        (string Label, string Command, TaxiRoute Preview) single = Assert.Single(variants);
        Assert.Equal("TAXI @A12", single.Command);
    }

    [Fact]
    public void BuildTaxiCrossingVariants_NoSpot_ReturnsEmpty()
    {
        GroundViewModel vm = MakeViewModel();
        TaxiRoute route = EmptyRoute();

        List<(string Label, string Command, TaxiRoute Preview)> variants = vm.BuildTaxiCrossingVariants(route, spot: null, pathOverride: null);

        Assert.Empty(variants);
    }
}
