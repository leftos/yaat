using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Projection of the room's <c>AtpaResultsChanged</c> pair list onto the per-aircraft ATPA fields the
/// radar renderer reads. Mirrors the conflict-alert projection: only the trailing aircraft of a pairing
/// carries it, every aircraft is visited so a cleared pairing blanks its fields, and an aircraft that
/// arrives after the broadcast is seeded from the current set.
/// </summary>
public class MainViewModelAtpaProjectionTests
{
    private static MainViewModel NewVm() => new(new FakeFilePickerService());

    private static AircraftDto MakeAircraft(string callsign) =>
        new(
            Callsign: callsign,
            AircraftType: "B738",
            Latitude: 37.62,
            Longitude: -122.22,
            Heading: 90,
            Altitude: 3_000,
            GroundSpeed: 180,
            BeaconCode: 1200,
            TransponderMode: "ModeC",
            IsIdenting: false,
            VerticalSpeed: 0,
            AssignedHeading: null,
            AssignedAltitude: null,
            AssignedSpeed: null,
            Departure: "SFO",
            Destination: "OAK",
            Route: "",
            FlightRules: "IFR",
            Status: "Active"
        );

    private static MainViewModel VmWith(params string[] callsigns)
    {
        var vm = NewVm();
        vm.ApplyScenarioBootstrap(new ScenarioBootstrap("scenario-atpa", "ATPA Test", "OAK", null, null, [.. callsigns.Select(MakeAircraft)]));
        return vm;
    }

    private static AircraftModel Find(MainViewModel vm, string callsign) => vm.Aircraft.Single(a => a.Callsign == callsign);

    [AvaloniaFact]
    public void ApplyAtpaResults_SetsFieldsOnTrailingAircraftOnly()
    {
        var vm = VmWith("LEAD1", "TRAIL1", "OTHER1");

        vm.ApplyAtpaResults([new AtpaPairDto("TRAIL1", "LEAD1", 5.0, AtpaConeState.Warning)]);

        var trail = Find(vm, "TRAIL1");
        Assert.Equal("LEAD1", trail.AtpaLeadCallsign);
        Assert.Equal(5.0, trail.AtpaAllowedSeparationNm);
        Assert.Equal(AtpaConeState.Warning, trail.AtpaConeState);

        // The leader carries no pairing of its own, and neither does an unrelated aircraft.
        foreach (var ac in new[] { Find(vm, "LEAD1"), Find(vm, "OTHER1") })
        {
            Assert.Null(ac.AtpaLeadCallsign);
            Assert.Equal(0, ac.AtpaAllowedSeparationNm);
            Assert.Equal(AtpaConeState.Monitor, ac.AtpaConeState);
        }
    }

    [AvaloniaFact]
    public void ApplyAtpaResults_EmptyList_ClearsPreviousPair()
    {
        var vm = VmWith("LEAD1", "TRAIL1");

        vm.ApplyAtpaResults([new AtpaPairDto("TRAIL1", "LEAD1", 4.0, AtpaConeState.Alert)]);
        vm.ApplyAtpaResults([]);

        var trail = Find(vm, "TRAIL1");
        Assert.Null(trail.AtpaLeadCallsign);
        Assert.Equal(0, trail.AtpaAllowedSeparationNm);
        Assert.Equal(AtpaConeState.Monitor, trail.AtpaConeState);
    }

    [AvaloniaFact]
    public void LateAddedAircraft_IsSeededFromCurrentPairs()
    {
        var vm = VmWith("LEAD1");

        // The pairing arrives before the trailing aircraft's first per-aircraft update.
        vm.ApplyAtpaResults([new AtpaPairDto("TRAIL1", "LEAD1", 6.0, AtpaConeState.Monitor)]);

        vm.OnAircraftUpdated(MakeAircraft("TRAIL1"));
        Dispatcher.UIThread.RunJobs();

        var trail = Find(vm, "TRAIL1");
        Assert.Equal("LEAD1", trail.AtpaLeadCallsign);
        Assert.Equal(6.0, trail.AtpaAllowedSeparationNm);
        Assert.Equal(AtpaConeState.Monitor, trail.AtpaConeState);
    }
}
