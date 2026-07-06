using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

// Regression for the N263FY bug: creating a flight plan via the built-in Flight Plan Editor
// didn't show the squawk in the FPE's BCN box, even though a squawk was generated and showed
// on the flight strip. Two defects: (1) the training wire only carried the reported code
// (Transponder.Code), never the controller-assigned code (Transponder.AssignedCode) the strip
// shows; (2) the FPE set BcnText once at open and never refreshed. The BCN box must show the
// assigned code and update live when it changes while the editor is open.
public class FlightPlanEditorBeaconRefreshTests
{
    private static FlightPlanEditorWindow Open(AircraftModel ac)
    {
        var window = new FlightPlanEditorWindow(ac, (_, _) => { }, _ => Task.CompletedTask);
        window.ShowAndRunLayout();
        return window;
    }

    [AvaloniaFact]
    public void Bcn_ShowsAssignedCode_NotReportedCode()
    {
        // Assigned 0302 (what ATC filed / the strip shows), pilot still squawking 0000.
        var ac = new AircraftModel
        {
            Callsign = "N263FY",
            AssignedBeaconCode = 302,
            BeaconCode = 0,
        };
        var window = Open(ac);

        var bcn = window.FindControl<TextBlock>("BcnText");
        Assert.NotNull(bcn);
        Assert.Equal("0302", bcn!.Text);
    }

    [AvaloniaFact]
    public void Bcn_RefreshesLive_WhenAssignedCodeChangesWhileOpen()
    {
        var ac = new AircraftModel
        {
            Callsign = "N263FY",
            AssignedBeaconCode = 0,
            BeaconCode = 0,
        };
        var window = Open(ac);

        var bcn = window.FindControl<TextBlock>("BcnText");
        Assert.NotNull(bcn);
        Assert.Equal("0000", bcn!.Text);

        // Server assigns / recycle returns a new code while the editor stays open.
        ac.AssignedBeaconCode = 456;

        Assert.Equal("0456", bcn.Text);
    }

    [AvaloniaFact]
    public void SubmitButton_FlipsCreateToAmend_WhenFlightPlanFiledWhileOpen()
    {
        // No route/departure/destination => HasFlightPlan is false => "Create".
        var ac = new AircraftModel { Callsign = "N263FY" };
        var window = Open(ac);

        var submit = window.FindControl<Button>("SubmitButton");
        Assert.NotNull(submit);
        Assert.Equal("Create", submit!.Content as string);

        // Filing a plan flips HasFlightPlan true while the editor stays open.
        ac.Destination = "KMOD";

        Assert.Equal("Amend", submit.Content as string);
    }
}
