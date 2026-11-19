using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope.Prefs;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The Ground View's wind and altimeter readout shows the METAR of the airport the view depicts, whichever of
/// weather and layout arrives first. Loading live weather at session start used to beat the async layout fetch
/// and leave the first station in the list showing (issue #449). When the airport a view depicts has no METAR
/// in the loaded weather, the view shows no weather at all plus a "No METAR for X" note — never another
/// station's report. Stations are keyed by the FAA id the layouts and position configs use, so a P-prefixed
/// ICAO ("PHNL") reads as "HNL"; and with no weather profile loaded every view still gets the synthetic
/// fair-weather report for its own airport.
/// </summary>
public class MainViewModelGroundWeatherTests
{
    private const string AunMetar = "METAR KAUN 230235Z AUTO 00000KT 10SM CLR 17/08 A3002";
    private const string SfoMetar = "METAR KSFO 230236Z 29012KT 10SM FEW008 16/12 A2998";
    private const string OakMetar = "METAR KOAK 230236Z 28008KT 10SM FEW010 18/11 A3000";
    private const string HnlMetar = "METAR PHNL 230236Z 07012KT 10SM FEW020 27/19 A3005";

    [AvaloniaFact]
    public void WeatherBeforeLayout_GroundShowsItsOwnAirportOnceLayoutLoads()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplyWeatherChanged(new WeatherChangedDto("Live Weather", null, null, [AunMetar, SfoMetar], null, null, null));
        vm.Ground.SetLayoutForTesting(new GroundLayoutDto("SFO", [], [], null, null, null, IsRunwaysOnly: false));

        Assert.Equal("SFO", vm.Ground.WeatherInfo?.StationId);
    }

    [AvaloniaFact]
    public void LayoutBeforeWeather_GroundShowsItsOwnAirport()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.Ground.SetLayoutForTesting(new GroundLayoutDto("SFO", [], [], null, null, null, IsRunwaysOnly: false));
        vm.ApplyWeatherChanged(new WeatherChangedDto("Live Weather", null, null, [AunMetar, SfoMetar], null, null, null));

        Assert.Equal("SFO", vm.Ground.WeatherInfo?.StationId);
    }

    [AvaloniaFact]
    public void GroundView_NoMetarForItsAirport_ShowsNoWeatherAndANote()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplyWeatherChanged(new WeatherChangedDto("Live Weather", null, null, [OakMetar], null, null, null));
        vm.Ground.SetLayoutForTesting(new GroundLayoutDto("SFO", [], [], null, null, null, IsRunwaysOnly: false));

        Assert.Null(vm.Ground.WeatherInfo);
        Assert.Equal("No METAR for SFO", vm.Ground.WeatherNote);
    }

    [AvaloniaFact]
    public void GroundView_MatchingMetar_HasNoNote()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplyWeatherChanged(new WeatherChangedDto("Live Weather", null, null, [OakMetar, SfoMetar], null, null, null));
        vm.Ground.SetLayoutForTesting(new GroundLayoutDto("SFO", [], [], null, null, null, IsRunwaysOnly: false));

        Assert.Equal("SFO", vm.Ground.WeatherInfo?.StationId);
        Assert.Null(vm.Ground.WeatherNote);
    }

    [AvaloniaFact]
    public void GroundView_PPrefixedMetar_MatchesTheFaaAirportId()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplyWeatherChanged(new WeatherChangedDto("Live Weather", null, null, [HnlMetar], null, null, null));
        vm.Ground.SetLayoutForTesting(new GroundLayoutDto("HNL", [], [], null, null, null, IsRunwaysOnly: false));

        Assert.Equal("HNL", vm.Ground.WeatherInfo?.StationId);
        Assert.Null(vm.Ground.WeatherNote);
    }

    [AvaloniaFact]
    public void GroundView_SwitchingAirport_MovesTheNoteWithTheLayout()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplyWeatherChanged(new WeatherChangedDto("Live Weather", null, null, [SfoMetar], null, null, null));

        vm.Ground.SetLayoutForTesting(new GroundLayoutDto("SFO", [], [], null, null, null, IsRunwaysOnly: false));
        Assert.Equal("SFO", vm.Ground.WeatherInfo?.StationId);
        Assert.Null(vm.Ground.WeatherNote);

        vm.Ground.SetLayoutForTesting(new GroundLayoutDto("OAK", [], [], null, null, null, IsRunwaysOnly: false));
        Assert.Null(vm.Ground.WeatherInfo);
        Assert.Equal("No METAR for OAK", vm.Ground.WeatherNote);

        vm.Ground.SetLayoutForTesting(new GroundLayoutDto("SFO", [], [], null, null, null, IsRunwaysOnly: false));
        Assert.Equal("SFO", vm.Ground.WeatherInfo?.StationId);
        Assert.Null(vm.Ground.WeatherNote);
    }

    [AvaloniaFact]
    public void GroundView_NoAirportKnown_ShowsNothing()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        Assert.Null(vm.Ground.WeatherInfo);
        Assert.Null(vm.Ground.WeatherNote);
    }

    [AvaloniaFact]
    public void GroundView_NoWeatherProfile_UsesTheDefaultForItsAirport()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.Ground.SetLayoutForTesting(new GroundLayoutDto("SFO", [], [], null, null, null, IsRunwaysOnly: false));

        // No profile loaded: the calm/standard default stands in for the airport's report, so there is
        // nothing to be missing and no note.
        Assert.Equal("SFO", vm.Ground.WeatherInfo?.StationId);
        Assert.Equal(29.92, vm.Ground.WeatherInfo?.AltimeterInHg);
        Assert.Equal(0, vm.Ground.WeatherInfo?.WindSpeedKts);
        Assert.Null(vm.Ground.WeatherNote);
    }

    [AvaloniaFact]
    public void GroundView_NoWeatherProfile_ExtraViewGetsItsOwnDefault()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        try
        {
            vm.Ground.SetLayoutForTesting(new GroundLayoutDto("SFO", [], [], null, null, null, IsRunwaysOnly: false));
            GroundViewInstance extra = vm.OpenExtraGroundView("OAK");

            // OAK is neither the scenario's primary airport (there is no scenario) nor a radar weather
            // airport, so its default exists only because every Ground View's airport is covered.
            extra.Vm.SetLayoutForTesting(new GroundLayoutDto("OAK", [], [], null, null, null, IsRunwaysOnly: false));

            Assert.Equal("OAK", extra.Vm.WeatherInfo?.StationId);
            Assert.Equal(29.92, extra.Vm.WeatherInfo?.AltimeterInHg);
            Assert.Null(extra.Vm.WeatherNote);
        }
        finally
        {
            ClearPersistedViews(vm.Preferences);
        }
    }

    [AvaloniaFact]
    public void ExtraGroundView_DoesNotInheritTheDockedViewsWeather()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        try
        {
            vm.Ground.SetLayoutForTesting(new GroundLayoutDto("SFO", [], [], null, null, null, IsRunwaysOnly: false));
            vm.ApplyWeatherChanged(new WeatherChangedDto("Live Weather", null, null, [SfoMetar], null, null, null));
            Assert.Equal("SFO", vm.Ground.WeatherInfo?.StationId);

            GroundViewInstance extra = vm.OpenExtraGroundView("OAK");

            // Its own airport has no report, and it is not the docked view's: nothing shows until its
            // layout lands, then the note does.
            Assert.Null(extra.Vm.WeatherInfo);
            Assert.Null(extra.Vm.WeatherNote);

            extra.Vm.SetLayoutForTesting(new GroundLayoutDto("OAK", [], [], null, null, null, IsRunwaysOnly: false));

            Assert.Null(extra.Vm.WeatherInfo);
            Assert.Equal("No METAR for OAK", extra.Vm.WeatherNote);
        }
        finally
        {
            ClearPersistedViews(vm.Preferences);
        }
    }

    [AvaloniaFact]
    public void ClearScenarioState_DropsAStaleNoMetarNote()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        using var scope = new ScopeViewModel(vm, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        vm.OpenScope = scope;

        vm.ScopeMaps.ApplyPositionDisplayConfig(new PositionDisplayConfigDto([], [], ["SFO"], "SFO", null, 3));
        // A loaded profile, JSON and all: that is what keeps the synthetic defaults (which would otherwise
        // rebuild the readout on unload) out of the picture and leaves the stale note the only thing to clear.
        vm.ApplyWeatherChanged(new WeatherChangedDto("Live Weather", null, null, [AunMetar], "{\"name\":\"Live Weather\"}", null, null));
        Assert.Equal(["No METAR for SFO"], scope.WeatherLines);
        var changed = new List<string?>();
        scope.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Unloading the scenario clears the maps, and with them the position's airport filter — the note
        // it justified must not outlive it, and the open Scope is told to redraw its weather.
        vm.ClearScenarioState();

        Assert.Contains(nameof(ScopeViewModel.WeatherLines), changed);
        Assert.StartsWith("AUN ", Assert.Single(scope.WeatherLines), StringComparison.Ordinal);
    }

    private static void ClearPersistedViews(UserPreferences preferences) => preferences.SetExtraGroundViews([]);
}
