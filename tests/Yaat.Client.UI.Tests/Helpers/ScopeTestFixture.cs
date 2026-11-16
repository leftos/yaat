using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Helpers;

/// <summary>The setup the Scope test classes share: a main view model with its nav data loaded, and a Scope opened over it.</summary>
internal static class ScopeTestFixture
{
    /// <summary>A main view model with a fake file picker, once its nav data has loaded.</summary>
    public static async Task<MainViewModel> MainAsync()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        return main;
    }

    /// <summary>An open Scope with no viewer and no store (the Mixed profile at 40 NM), and the main view model it was opened on.</summary>
    public static async Task<(MainViewModel Main, ScopeViewModel Scope)> OpenScopeAsync()
    {
        MainViewModel main = await MainAsync();
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null);
        main.OpenScope = scope;
        return (main, scope);
    }

    /// <summary>An airborne B738 at <paramref name="altitudeFt"/> over the Bay Area, added to the traffic the scope draws.</summary>
    public static AircraftModel AddAircraft(MainViewModel main, string callsign, double altitudeFt)
    {
        var ac = new AircraftModel
        {
            Callsign = callsign,
            AircraftType = "B738",
            FiledAircraftType = "B738",
            FlightRules = "IFR",
            TransponderMode = "C",
            Altitude = altitudeFt,
            GroundSpeed = 250,
            Position = new LatLon(37.7, -122.2),
        };
        main.Aircraft.Add(ac);
        return ac;
    }
}
