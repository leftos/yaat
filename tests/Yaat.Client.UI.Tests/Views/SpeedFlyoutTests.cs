using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Radar.Flyouts;

namespace Yaat.Client.UI.Tests.Views;

// The radar speed flyout's final-approach-speed item reads as the aircraft menu's own entry.
public class SpeedFlyoutTests
{
    [AvaloniaTheory]
    [InlineData("B738")]
    [InlineData("")]
    public void FasItem_HeaderIsTheMenuCatalogsFinalApproachSpeedHeader(string filedType)
    {
        var aircraft = new AircraftModel { Callsign = "AAL1", FiledAircraftType = filedType };
        var radarVm = new RadarViewModel(new ServerConnection(), new VideoMapService(), (_, _, _) => Task.CompletedTask);
        var host = new RecordingMenuHost("");
        var context = new MenuContext(new MenuClick(aircraft.Callsign, null, null, []), host.Session);

        MenuItem flyoutItem = SpeedFlyout.BuildFasItem(aircraft, radarVm, "AB");
        MenuItem? catalogItem = MenuCatalog.Get(MenuIds.SpeedFinalApproach).Build(aircraft, context, host);

        Assert.NotNull(catalogItem);
        Assert.StartsWith(MenuCatalog.Get(MenuIds.SpeedFinalApproach).Label, flyoutItem.Header as string);
        Assert.Equal(catalogItem.Header as string, flyoutItem.Header as string);
    }
}
