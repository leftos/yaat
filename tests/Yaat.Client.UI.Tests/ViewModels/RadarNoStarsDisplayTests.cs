using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// A display config marked <see cref="PositionDisplayConfigDto.NoStarsDisplay"/> (an ERAM position's) names the active position
/// only: neither the scenario bootstrap nor a Radar View opened after it applies its empty map group or airports to a Radar.
/// </summary>
public class RadarNoStarsDisplayTests
{
    private static readonly PositionDisplayConfigDto Stars = new([1, 2, 3], ["4U"], ["SFO"], "4U", null, 3);

    private static readonly PositionDisplayConfigDto Eram = new([], [], [], "C44", null, 3, NoStarsDisplay: true);

    [AvaloniaFact]
    public void ScenarioBootstrap_WithANoStarsConfig_KeepsTheRadarsMapsAndAirports()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        main.Radar.ApplyPositionDisplayConfig(Stars);
        List<int> before = [.. main.Radar.MapShortcuts.Select(s => s.StarsId)];
        Assert.NotEmpty(before);

        main.Radar.ApplyScenarioBootstrap(Bootstrap(Eram), artccId: null);

        Assert.Equal(before, main.Radar.MapShortcuts.Select(s => s.StarsId));
        Assert.Equal(["SFO"], main.Radar.WeatherAirports);
    }

    /// <summary>
    /// The extra Radar View is not handed the ERAM config, so once its maps load it builds its shortcuts from the facility's
    /// first map group, not from the ERAM config's empty one.
    /// </summary>
    [AvaloniaFact]
    public void ExtraRadarView_OpenedAfterANoStarsConfig_DoesNotTakeItsEmptyGroup()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        main.OnPositionDisplayChanged(Eram);
        Dispatcher.UIThread.RunJobs();

        RadarViewModel extra = main.OpenExtraRadarView("OAK").Vm;
        extra.ApplyVideoMapsDtoForTesting(new FacilityVideoMapsDto("ZOA", "NCT", [], [], [new MapGroupDto([7, 8], ["4U"])]));

        Assert.Equal([7, 8], extra.MapShortcuts.Select(s => s.StarsId).Order());
    }

    private static ScenarioBootstrap Bootstrap(PositionDisplayConfigDto config) =>
        new()
        {
            ScenarioId = "scenario-eram",
            ScenarioName = "ERAM",
            PrimaryAirportId = "OAK",
            PositionDisplayConfig = config,
            FlightStripsConfig = null,
            Aircraft = [],
            ElapsedSeconds = 0,
            SessionStartUtc = null,
        };
}
