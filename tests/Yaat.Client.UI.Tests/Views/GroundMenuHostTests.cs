using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Ground;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The ground menu host's taxi-route and hidden-datablock members: they act only on the right-clicked aircraft, and
/// still act by callsign when the menu was opened on an aircraft the main view model has no model for.
/// </summary>
public class GroundMenuHostTests
{
    private const string Callsign = "UAL100";
    private const string OtherCallsign = "SWA200";

    private static (GroundView View, GroundViewModel Ground) GroundHarness()
    {
        var ground = new GroundViewModel(new ServerConnection(), sendCommand: (_, _, _) => Task.CompletedTask);
        var view = new GroundView { DataContext = ground };
        return (view, ground);
    }

    [AvaloniaFact]
    public void GroundMenuHost_MismatchedCallsign_Throws()
    {
        (GroundView view, GroundViewModel ground) = GroundHarness();
        var host = new GroundMenuHost(view, ground, null, new AircraftModel { Callsign = Callsign });

        Assert.Throws<InvalidOperationException>(() => host.GetTaxiRouteMode(OtherCallsign));
        Assert.Throws<InvalidOperationException>(() => host.SetTaxiRouteMode(OtherCallsign, TaxiRouteDisplayMode.AlwaysShow));
        Assert.Throws<InvalidOperationException>(() => host.IsDataBlockHidden(OtherCallsign));
        Assert.Throws<InvalidOperationException>(() => host.ToggleHiddenDataBlock(OtherCallsign));

        Assert.Equal(TaxiRouteDisplayMode.Follow, ground.GetTaxiRouteMode(OtherCallsign));
        Assert.False(view.Canvas.IsDataBlockHidden(OtherCallsign));
    }

    [AvaloniaFact]
    public void GroundMenuHost_NoAircraftModel_ReadsAndDrivesDisplayByCallsign()
    {
        (GroundView view, GroundViewModel ground) = GroundHarness();
        var host = new GroundMenuHost(view, ground, null, null);

        Assert.Equal(TaxiRouteDisplayMode.Follow, host.GetTaxiRouteMode(Callsign));
        host.SetTaxiRouteMode(Callsign, TaxiRouteDisplayMode.AlwaysHide);
        Assert.Equal(TaxiRouteDisplayMode.AlwaysHide, ground.GetTaxiRouteMode(Callsign));
        Assert.Equal(TaxiRouteDisplayMode.AlwaysHide, host.GetTaxiRouteMode(Callsign));

        Assert.False(host.IsDataBlockHidden(Callsign));
        host.ToggleHiddenDataBlock(Callsign);
        Assert.True(view.Canvas.IsDataBlockHidden(Callsign));
        Assert.True(host.IsDataBlockHidden(Callsign));
    }
}
