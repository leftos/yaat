using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The Ground View's wind and altimeter readout shows the METAR of the airport the view depicts, whichever of
/// weather and layout arrives first. Loading live weather at session start used to beat the async layout fetch
/// and leave the first station in the list showing (issue #449).
/// </summary>
public class MainViewModelGroundWeatherTests
{
    private const string AunMetar = "METAR KAUN 230235Z AUTO 00000KT 10SM CLR 17/08 A3002";
    private const string SfoMetar = "METAR KSFO 230236Z 29012KT 10SM FEW008 16/12 A2998";

    [AvaloniaFact]
    public void WeatherBeforeLayout_GroundShowsItsOwnAirportOnceLayoutLoads()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplyWeatherChanged(new WeatherChangedDto("Live Weather", null, null, [AunMetar, SfoMetar], null));
        vm.Ground.SetLayoutForTesting(new GroundLayoutDto("SFO", [], [], null, null, null));

        Assert.Equal("SFO", vm.Ground.WeatherInfo?.StationId);
    }

    [AvaloniaFact]
    public void LayoutBeforeWeather_GroundShowsItsOwnAirport()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.Ground.SetLayoutForTesting(new GroundLayoutDto("SFO", [], [], null, null, null));
        vm.ApplyWeatherChanged(new WeatherChangedDto("Live Weather", null, null, [AunMetar, SfoMetar], null));

        Assert.Equal("SFO", vm.Ground.WeatherInfo?.StationId);
    }
}
