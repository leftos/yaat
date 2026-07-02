using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Regression tests for the single-token command-input shortcut that used to select an aircraft by
/// substring callsign match without checking whether the token was itself a complete command. Typing
/// a bare command such as "TB" (turn base) while an aircraft whose callsign contains "TB" was on
/// frequency silently selected that aircraft and swallowed the command. Complete commands must win
/// over partial (substring) callsign matches.
/// </summary>
public class MainViewModelCommandSelectTests
{
    private static MainViewModel NewVm() => new(new FakeFilePickerService());

    private static AircraftDto MakeAircraft(string callsign) =>
        new(
            Callsign: callsign,
            AircraftType: "C172",
            Latitude: 37.62,
            Longitude: -122.22,
            Heading: 90,
            Altitude: 1500,
            GroundSpeed: 90,
            BeaconCode: 1200,
            TransponderMode: "ModeC",
            VerticalSpeed: 0,
            AssignedHeading: null,
            AssignedAltitude: null,
            AssignedSpeed: null,
            Departure: "OAK",
            Destination: "SQL",
            Route: "",
            FlightRules: "VFR",
            Status: "Active"
        );

    [AvaloniaFact]
    public async Task BareCommand_DoesNotSelectAircraftBySubstringCallsign()
    {
        var vm = NewVm();
        vm.OnAircraftUpdated(MakeAircraft("N172TB"));
        Dispatcher.UIThread.RunJobs();

        // Precondition: the substring-callsign aircraft is actually on frequency, so a hijack is
        // possible. Without this guard the test would false-pass on an empty aircraft list.
        Assert.Contains(vm.Aircraft, a => a.Callsign == "N172TB");
        Assert.Null(vm.SelectedAircraft);

        vm.CommandText = "TB";
        await vm.SendCommandCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        // "TB" is the turn-base command, not a partial callsign — it must not hijack-select N172TB.
        Assert.Null(vm.SelectedAircraft);
        // With nothing selected, the command falls through and reports it has no target.
        Assert.Contains("No aircraft matched", vm.StatusText);
    }
}
