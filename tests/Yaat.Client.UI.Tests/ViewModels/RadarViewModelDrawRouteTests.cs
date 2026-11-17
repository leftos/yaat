using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.ViewModels;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>The Radar's draw-route members forward to its <see cref="DrawRouteState"/> and keep its bound properties in step.</summary>
public class RadarViewModelDrawRouteTests
{
    [AvaloniaFact]
    public async Task RadarForwarders_KeepTheRadarsBoundState_InStep()
    {
        MainViewModel main = await MainAsync();
        RadarViewModel radar = main.Radar;

        radar.EnterDrawRoute("UAL238");
        radar.PlaceRouteWaypoint(37.0, -122.0);
        radar.SetWaypointCondition(0, "050", null);
        Assert.NotNull(radar.WaypointConditionsSnapshot);
        radar.UndoRouteWaypoint();

        Assert.True(radar.IsDrawingRoute);
        Assert.Null(radar.DrawnWaypoints);
        Assert.Null(radar.WaypointConditionsSnapshot);
    }
}
