using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Sim.Commands;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// After an accurate say-again press the command box holds the raw transcript, which must not go to the solo natural-command
/// path on Enter — that path maps it to an aircraft again. Any edit hands the text back to the normal path.
/// </summary>
public class MainViewModelSayAgainEnterTests
{
    // A transcript the solo natural path maps to "UAL12 TL 270".
    private const string Staged = "united twelve turn left heading two seven zero";

    private const string SayAgain = "Say again: callsign unclear";

    [AvaloniaFact]
    public async Task Enter_OnTheStagedTranscript_SendsNothingAndKeepsTheSayAgainStatus()
    {
        MainViewModel vm = await ConnectedVmAsync();

        DeliverSayAgain(vm, Staged);

        CommandBoxOutcome outcome = await vm.SubmitCommandTextAsync(Staged);

        Assert.Null(outcome.Submission);
        Assert.Null(outcome.Refused);
        Assert.Equal(SayAgain, outcome.StatusText);
    }

    [AvaloniaFact]
    public async Task Enter_OnAnEditedStagedTranscript_TakesTheNormalPath()
    {
        MainViewModel vm = await ConnectedVmAsync();

        DeliverSayAgain(vm, Staged);

        CommandBoxOutcome outcome = await vm.SubmitCommandTextAsync("xnited twelve turn left heading two seven zero");

        Assert.NotEqual(SayAgain, outcome.StatusText);
    }

    private static void DeliverSayAgain(MainViewModel vm, string transcript)
    {
        vm.HandleSpeechServiceCommandReady(new SpeechResult(transcript, null, null, SayAgain));
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<MainViewModel> ConnectedVmAsync()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        await vm.NavDataInitialization;
        await vm.CrcAliasLoad;
        vm.IsConnected = true;
        vm.ActiveRoomId = "room-1";
        vm.OnAircraftUpdated(IfrAircraft("UAL12"));
        vm.SessionSoloTrainingMode = true;
        Dispatcher.UIThread.RunJobs();
        return vm;
    }

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
}
