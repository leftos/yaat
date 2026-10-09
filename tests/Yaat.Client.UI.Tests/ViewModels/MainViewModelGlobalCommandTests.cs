using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Typed verbs addressed to the room (HFR, HFROFF, REL, ARWY, ASDXALERTS) and GHOST are sent with an empty callsign
/// whether or not an aircraft is selected, instead of answering "No aircraft matched" or taking the selected aircraft's
/// callsign. The test view model has no server, so the send itself throws "Not connected."; the global send path is the
/// one that reports that as "{VERB} error: …", where the aircraft path reports "Command error: …".
/// </summary>
public class MainViewModelGlobalCommandTests
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
            IsIdenting: false,
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

    private static async Task SendAsync(MainViewModel vm, string text)
    {
        vm.CommandText = text;
        await vm.SendCommandCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData("HFR OAK", "HFR")]
    [InlineData("HFROFF OAK", "HFROFF")]
    [InlineData("REL OAK", "REL")]
    [InlineData("ARWY OAK 28L", "ARWY")]
    [InlineData("GHOST N12345 28R", "GHOST")]
    [InlineData("ASDXALERTS", "ASDXALERTS")]
    public async Task RoomVerb_NothingSelected_IsSentOnTheGlobalPath(string text, string verb)
    {
        MainViewModel vm = NewVm();
        Assert.Null(vm.SelectedAircraft);

        await SendAsync(vm, text);

        Assert.DoesNotContain("No aircraft matched", vm.StatusText);
        Assert.Equal($"{verb} error: Not connected.", vm.StatusText);
        Assert.Equal("", vm.CommandText);
    }

    [AvaloniaFact]
    public async Task RoomVerb_AircraftSelected_IsStillSentOnTheGlobalPath()
    {
        MainViewModel vm = NewVm();
        vm.OnAircraftUpdated(MakeAircraft("N172TB"));
        Dispatcher.UIThread.RunJobs();
        vm.SelectedAircraft = Assert.Single(vm.Aircraft, a => a.Callsign == "N172TB");

        await SendAsync(vm, "HFR OAK");

        Assert.Equal("HFR error: Not connected.", vm.StatusText);
        Assert.Equal("N172TB", vm.SelectedAircraft?.Callsign);
    }

    /// <summary>CFR releases the selected departure, so with nothing selected it still asks for an aircraft.</summary>
    [AvaloniaFact]
    public async Task Cfr_NothingSelected_StillAsksForAnAircraft()
    {
        MainViewModel vm = NewVm();

        await SendAsync(vm, "CFR 1830");

        Assert.Contains("No aircraft matched", vm.StatusText);
    }

    /// <summary>
    /// Every verb routed to the global path has a handler and nothing else does: a verb listed without one would be
    /// swallowed unsent, as GHOST was.
    /// </summary>
    [AvaloniaFact]
    public void GlobalHandlers_MatchTheVerbsSentWithoutSelection()
    {
        MainViewModel vm = NewVm();

        foreach (CanonicalCommandType type in Enum.GetValues<CanonicalCommandType>())
        {
            bool sendsWithoutSelection = CommandScopes.SendsWithoutSelection(type);
            bool hasHandler = vm.GlobalCommandHandlerFor(type) is not null;
            Assert.True(
                sendsWithoutSelection == hasHandler,
                $"{type}: sent without selection = {sendsWithoutSelection}, has a global handler = {hasHandler}"
            );
        }
    }
}
