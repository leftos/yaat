using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Sim.Commands;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// What pressing Enter in the main command line does with each kind of typed line: what the status line says, whether the
/// line is cleared or kept for re-editing, what lands in the command history, and whether the line reached the server. The
/// test view model has no server, so a line that reaches the send reports "Not connected." and leaves no submission.
/// </summary>
public class MainViewModelCommandLineSendTests
{
    private static AircraftDto IfrAircraft(string callsign) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            Latitude = 37.72,
            Longitude = -122.22,
            Heading = 280,
            Altitude = 3000,
            GroundSpeed = 180,
            BeaconCode = 4521,
            TransponderMode = "ModeC",
            IsIdenting = false,
            VerticalSpeed = 0,
            AssignedHeading = null,
            AssignedAltitude = null,
            AssignedSpeed = null,
            Departure = "SAN",
            Destination = "OAK",
            Route = "",
            FlightRules = "IFR",
            Status = "Active",
        };

    private static async Task<MainViewModel> NewVmWithAircraftAsync()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        await vm.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        await vm.CrcAliasLoad;
        vm.OnAircraftUpdated(IfrAircraft("UAL123"));
        Dispatcher.UIThread.RunJobs();
        return vm;
    }

    private static async Task SendAsync(MainViewModel vm, string text)
    {
        vm.CommandText = text;
        await vm.SendCommandCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task PilotCommandToTheSelectedAircraft_ReachesTheSend_AndKeepsTheLineWhenTheSendFails()
    {
        MainViewModel vm = await NewVmWithAircraftAsync();
        vm.SelectedAircraft = vm.Aircraft.Single(a => a.Callsign == "UAL123");

        await SendAsync(vm, "FH 270");

        Assert.Equal("Command error: Not connected.", vm.StatusText);
        Assert.Equal("FH 270", vm.CommandText);
        Assert.Null(vm.LastSubmission);
        Assert.Empty(vm.CommandHistory);
        Assert.Equal("UAL123", vm.SelectedAircraft?.Callsign);
    }

    [AvaloniaFact]
    public async Task BareCallsign_SelectsTheAircraft_ClearsTheLine_AndSendsNothing()
    {
        MainViewModel vm = await NewVmWithAircraftAsync();
        Assert.Null(vm.SelectedAircraft);
        string statusBefore = vm.StatusText;

        await SendAsync(vm, "UAL123");

        Assert.Equal("UAL123", vm.SelectedAircraft?.Callsign);
        Assert.Equal("", vm.CommandText);
        Assert.Equal(statusBefore, vm.StatusText);
        Assert.Null(vm.LastSubmission);
        Assert.Empty(vm.CommandHistory);
    }

    [AvaloniaFact]
    public async Task ScopeDotCommand_EchoesInTheStatusLine_ClearsTheLine_AndRecordsHistory()
    {
        MainViewModel vm = await NewVmWithAircraftAsync();

        await SendAsync(vm, ".scope range 25");

        Assert.Equal(".scope range 25", vm.StatusText);
        Assert.Equal(25, vm.PrimaryScope.RangeNm);
        Assert.Equal("", vm.CommandText);
        Assert.Null(vm.LastSubmission);
        CommandHistoryEntry entry = Assert.Single(vm.CommandHistory);
        Assert.Equal("", entry.Callsign);
        Assert.Equal(".SCOPE RANGE 25", entry.Command);
    }

    [AvaloniaFact]
    public async Task UnknownDotCommand_ReportsTheVerb_ClearsTheLine_AndRecordsHistory()
    {
        MainViewModel vm = await NewVmWithAircraftAsync();

        await SendAsync(vm, ".zzqx 1");

        Assert.Equal("Unknown command or alias: .zzqx", vm.StatusText);
        Assert.Equal("", vm.CommandText);
        Assert.Null(vm.LastSubmission);
        Assert.Equal(".ZZQX 1", Assert.Single(vm.CommandHistory).Command);
    }

    [AvaloniaFact]
    public async Task UnknownCommandAfterACallsign_ReportsTheVerb_KeepsTheLine_AndSendsNothing()
    {
        MainViewModel vm = await NewVmWithAircraftAsync();

        await SendAsync(vm, "UAL123 XYZZY");

        Assert.StartsWith("\"XYZZY\" is not a recognized command", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal("UAL123 XYZZY", vm.CommandText);
        Assert.Null(vm.LastSubmission);
        Assert.Empty(vm.CommandHistory);
        Assert.Null(vm.SelectedAircraft);
    }

    [AvaloniaFact]
    public async Task AssignmentOverridePrefix_IsStripped_AndTheCommandReachesTheSend()
    {
        MainViewModel vm = await NewVmWithAircraftAsync();

        await SendAsync(vm, "** UAL123 FH 270");

        Assert.Equal("Command error: Not connected.", vm.StatusText);
        Assert.Equal("** UAL123 FH 270", vm.CommandText);
        Assert.Null(vm.LastSubmission);
        Assert.Equal("UAL123", vm.SelectedAircraft?.Callsign);
    }

    [AvaloniaFact]
    public async Task ChatPrefix_SendsAChatNotACommand_AndClearsTheLineEvenWhenTheSendFails()
    {
        MainViewModel vm = await NewVmWithAircraftAsync();

        await SendAsync(vm, "'hello there");

        Assert.Equal("Chat error: Not connected.", vm.StatusText);
        Assert.Equal("", vm.CommandText);
        Assert.Null(vm.LastSubmission);
        Assert.Empty(vm.CommandHistory);
        Assert.Null(vm.SelectedAircraft);
    }

    [AvaloniaFact]
    public async Task TakeWithNothingSelected_AsksForAnAircraft_AndKeepsTheLine()
    {
        MainViewModel vm = await NewVmWithAircraftAsync();

        await SendAsync(vm, "TAKE");

        Assert.Equal("Select an aircraft first", vm.StatusText);
        Assert.Equal("TAKE", vm.CommandText);
        Assert.Null(vm.LastSubmission);
        Assert.Empty(vm.CommandHistory);
    }

    [AvaloniaFact]
    public async Task PilotCommandWithNothingSelected_AsksForACallsign_AndKeepsTheLine()
    {
        MainViewModel vm = await NewVmWithAircraftAsync();

        await SendAsync(vm, "FH 270");

        Assert.Equal("No aircraft matched — type a callsign (or partial) before the command", vm.StatusText);
        Assert.Equal("FH 270", vm.CommandText);
        Assert.Null(vm.LastSubmission);
        Assert.Empty(vm.CommandHistory);
    }

    [AvaloniaFact]
    public async Task VfrOnlyCommandOnAnIfrAircraft_ShowsTheGateRejection_AndAddsItAsAWarning()
    {
        MainViewModel vm = await NewVmWithAircraftAsync();
        vm.Preferences.SetVfrCommandsForIfr(VfrCommandsForIfr.None);
        long before = TerminalEntry.LastSequence;

        await SendAsync(vm, "UAL123 ELD 28L");

        Assert.EndsWith(
            " requires a VFR aircraft — use CIFR on UAL123 first, or change Settings > Scenarios > VFR commands for IFR aircraft.",
            vm.StatusText,
            StringComparison.Ordinal
        );
        TerminalEntry warning = Assert.Single(vm.TerminalEntries, e => (e.Sequence > before) && (e.Kind == TerminalEntryKind.Warning));
        Assert.Equal(vm.StatusText, warning.Message);
        Assert.Equal("UAL123 ELD 28L", vm.CommandText);
        Assert.Null(vm.LastSubmission);
        Assert.Equal("UAL123", vm.SelectedAircraft?.Callsign);
    }

    [AvaloniaFact]
    public async Task UnknownMacro_ReportsTheMacro_AndKeepsTheLine()
    {
        MainViewModel vm = await NewVmWithAircraftAsync();

        await SendAsync(vm, "UAL123 !NOPE");

        Assert.Equal("Unknown macro \"!NOPE\"", vm.StatusText);
        Assert.Equal("UAL123 !NOPE", vm.CommandText);
        Assert.Null(vm.LastSubmission);
        Assert.Empty(vm.CommandHistory);
    }

    [AvaloniaFact]
    public async Task AmbiguousCallsignPrefix_ListsTheCandidates_AndKeepsTheLine()
    {
        MainViewModel vm = await NewVmWithAircraftAsync();
        vm.OnAircraftUpdated(IfrAircraft("UAL124"));
        Dispatcher.UIThread.RunJobs();

        await SendAsync(vm, "UAL12 FH 270");

        Assert.StartsWith("\"UAL12\" matches multiple aircraft: ", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("UAL123", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("UAL124", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal("UAL12 FH 270", vm.CommandText);
        Assert.Null(vm.LastSubmission);
        Assert.Null(vm.SelectedAircraft);
    }

    [AvaloniaFact]
    public async Task SoloModeNaturalLine_IsMappedToACommand_AndReachesTheSend()
    {
        MainViewModel vm = await NewVmWithAircraftAsync();
        vm.SessionSoloTrainingMode = true;
        Dispatcher.UIThread.RunJobs();

        await SendAsync(vm, "UAL123 turn left heading 270");

        Assert.Equal("Command error: Not connected.", vm.StatusText);
        Assert.Equal("UAL123 turn left heading 270", vm.CommandText);
        Assert.Null(vm.LastSubmission);
        Assert.Empty(vm.CommandHistory);
        Assert.Equal("UAL123", vm.SelectedAircraft?.Callsign);
    }
}
