using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// YAAT-709: an auto-delete (departure distance, on parking) closed the Flight Plan Editor. The server broadcasts every
/// removal, auto-delete included, as <c>AircraftDeleted</c>; the editor must react only to its own aircraft's deletion.
/// </summary>
public class Issue709FlightPlanEditorDeleteTests
{
    private static AircraftDto MakeAircraft(string callsign) =>
        new(
            Callsign: callsign,
            AircraftType: "B738",
            Latitude: 37.72,
            Longitude: -122.22,
            Heading: 280,
            Altitude: 3000,
            GroundSpeed: 220,
            BeaconCode: 4521,
            TransponderMode: "C",
            IsIdenting: false,
            VerticalSpeed: 0,
            AssignedHeading: null,
            AssignedAltitude: null,
            AssignedSpeed: null,
            Departure: "OAK",
            Destination: "LAX",
            Route: "OAK6 SNS",
            FlightRules: "IFR",
            Status: "Active"
        );

    private static MainViewModel HostScenario()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyScenarioBootstrap(
            new ScenarioBootstrap
            {
                ScenarioId = "scenario-709",
                ScenarioName = "FP editor vs auto-delete",
                PrimaryAirportId = "OAK",
                PositionDisplayConfig = null,
                FlightStripsConfig = null,
                Aircraft = [MakeAircraft("UAL1"), MakeAircraft("DAL2")],
                ElapsedSeconds = 0,
            }
        );
        return vm;
    }

    [AvaloniaFact]
    public void DeletingAnotherAircraft_KeepsEditorOpenWithUnsavedEdits()
    {
        MainViewModel vm = HostScenario();
        try
        {
            AircraftModel edited = vm.Aircraft.First(a => a.Callsign == "UAL1");
            FlightPlanEditorWindow editor = FlightPlanEditorManager.Open(edited, vm);
            editor.RteBox.Text = "OAK6 SNS V25 LAX";

            vm.OnAircraftDeleted("DAL2");
            Dispatcher.UIThread.RunJobs();

            Assert.Same(editor, FlightPlanEditorManager.OpenEditor);
            Assert.Equal("UAL1 - Flight Plan", editor.Title);
            Assert.Equal("OAK6 SNS V25 LAX", editor.RteBox.Text);
            Assert.True(editor.SubmitButton.IsEnabled);
        }
        finally
        {
            FlightPlanEditorManager.Close();
        }
    }

    [AvaloniaFact]
    public void DeletingItsOwnAircraft_KeepsTheEditorOpenWithEveryFieldCleared()
    {
        MainViewModel vm = HostScenario();
        try
        {
            AircraftModel edited = vm.Aircraft.First(a => a.Callsign == "UAL1");
            FlightPlanEditorWindow editor = FlightPlanEditorManager.Open(edited, vm);
            editor.RteBox.Text = "OAK6 SNS V25 LAX";
            Dispatcher.UIThread.RunJobs();
            Assert.True(editor.SubmitButton.IsEnabled);

            vm.OnAircraftDeleted("UAL1");
            Dispatcher.UIThread.RunJobs();

            Assert.Same(editor, FlightPlanEditorManager.OpenEditor);
            Assert.Equal("Flight Plan", editor.Title);
            Assert.Equal("", editor.AidText.Text);
            Assert.Equal("", editor.BcnText.Text);
            Assert.Equal("", editor.TypBox.Text);
            Assert.Equal("", editor.EqBox.Text);
            Assert.Equal("", editor.IcaoEqBox.Text);
            Assert.Equal("", editor.DepBox.Text);
            Assert.Equal("", editor.DestBox.Text);
            Assert.Equal("", editor.SpdBox.Text);
            Assert.Equal("", editor.AltBox.Text);
            Assert.Equal("", editor.RteBox.Text);
            Assert.Equal("", editor.RmkBox.Text);
            Assert.False(editor.SubmitButton.IsEnabled);
            Assert.False(editor.RecycleBcnButton.IsEnabled);

            // A later change to the deleted model's plan never reaches the cleared window.
            edited.Route = "OAK6 SNS V25 LAX";
            Assert.Equal("", editor.RteBox.Text);

            // The cleared editor ignores typing too: nothing can be submitted until another aircraft is
            // opened in it, so the Amend button must not re-arm.
            editor.RteBox.Text = "X";
            Dispatcher.UIThread.RunJobs();
            Assert.False(editor.SubmitButton.IsEnabled);
        }
        finally
        {
            FlightPlanEditorManager.Close();
        }
    }

    [AvaloniaFact]
    public void DeletedEditor_ReopenedOnAnotherAircraft_RebindsIt()
    {
        MainViewModel vm = HostScenario();
        try
        {
            AircraftModel edited = vm.Aircraft.First(a => a.Callsign == "UAL1");
            FlightPlanEditorWindow editor = FlightPlanEditorManager.Open(edited, vm);
            editor.RteBox.Text = "OAK6 SNS V25 LAX";

            vm.OnAircraftDeleted("UAL1");
            Dispatcher.UIThread.RunJobs();
            Assert.False(editor.RecycleBcnButton.IsEnabled);

            AircraftModel other = vm.Aircraft.First(a => a.Callsign == "DAL2");
            other.Route = "DAL2 SID";
            FlightPlanEditorWindow reopened = FlightPlanEditorManager.Open(other, vm);

            Assert.Same(editor, reopened);
            Assert.Equal("DAL2 - Flight Plan", reopened.Title);
            Assert.Equal("DAL2 SID", reopened.RteBox.Text);
            Assert.True(reopened.RecycleBcnButton.IsEnabled);

            // The rebind re-subscribes, so a later change to the new aircraft's plan reaches the window.
            other.Route = "DAL2 SID V25 LAX";
            Assert.Equal("DAL2 SID V25 LAX", reopened.RteBox.Text);
        }
        finally
        {
            FlightPlanEditorManager.Close();
        }
    }
}
