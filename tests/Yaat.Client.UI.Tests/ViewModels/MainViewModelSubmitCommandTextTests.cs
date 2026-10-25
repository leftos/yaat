using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Sim.Commands;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// <see cref="MainViewModel.SubmitCommandTextAsync"/> reports what the command box did with a typed line: a command the client
/// itself rejects never reaches the server, and its rejection comes back among the warnings.
/// </summary>
public class MainViewModelSubmitCommandTextTests
{
    private static AircraftDto IfrAircraft(string callsign) =>
        new(
            Callsign: callsign,
            AircraftType: "B738",
            Latitude: 37.72,
            Longitude: -122.22,
            Heading: 280,
            Altitude: 3000,
            GroundSpeed: 180,
            BeaconCode: 4521,
            TransponderMode: "ModeC",
            IsIdenting: false,
            VerticalSpeed: 0,
            AssignedHeading: null,
            AssignedAltitude: null,
            AssignedSpeed: null,
            Departure: "SAN",
            Destination: "OAK",
            Route: "",
            FlightRules: "IFR",
            Status: "Active"
        );

    [AvaloniaFact]
    public async Task VfrGatedCommandOnAnIfrAircraft_ReturnsTheGateRejectionAsAWarning_AndSendsNothing()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        await vm.NavDataInitialization;
        await vm.CrcAliasLoad;
        vm.Preferences.SetVfrCommandsForIfr(VfrCommandsForIfr.None);
        vm.IsConnected = true;
        vm.ActiveRoomId = "room-1";
        vm.OnAircraftUpdated(IfrAircraft("UAL123"));
        Dispatcher.UIThread.RunJobs();

        CommandBoxOutcome outcome = await vm.SubmitCommandTextAsync("UAL123 ELD 28L");

        Assert.Null(outcome.Refused);
        Assert.Null(outcome.Submission);
        Assert.Contains(outcome.Warnings, warning => warning.Contains("ELD", StringComparison.Ordinal));
    }
}
