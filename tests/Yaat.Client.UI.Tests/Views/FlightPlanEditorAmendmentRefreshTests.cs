using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

// A flight-plan amendment that lands while the Flight Plan Editor is open (own submit echo or
// another controller's amendment via AircraftUpdated) must show up in the editor without a
// close/reopen. Untouched fields refresh to the live values; a field the instructor is mid-edit
// on keeps their text — only its change-baseline moves to the new live value.
public class FlightPlanEditorAmendmentRefreshTests
{
    private static AircraftModel BuildAircraft() =>
        new()
        {
            Callsign = "N248ZV",
            FiledAircraftType = "C150",
            EquipmentSuffix = "A",
            Departure = "KOAK",
            Destination = "KOAK",
            CruiseSpeed = 90,
            CruiseAltitude = 3500,
            Route = "DIRECT",
            Remarks = "STUDENT PILOT",
        };

    private static FlightPlanEditorWindow Open(AircraftModel ac, Action<string, FlightPlanAmendment>? onAmend = null)
    {
        var window = new FlightPlanEditorWindow(ac, onAmend ?? ((_, _) => { }), _ => Task.CompletedTask);
        window.ShowAndRunLayout();
        return window;
    }

    [AvaloniaFact]
    public void UntouchedFields_RefreshLive_WhenAmendmentArrivesWhileOpen()
    {
        var ac = BuildAircraft();
        var window = Open(ac);

        // Another controller amends the plan while the editor is open.
        ac.FiledAircraftType = "C172";
        ac.EquipmentSuffix = "G";
        ac.Departure = "KHWD";
        ac.Destination = "KSQL";
        ac.CruiseSpeed = 110;
        ac.CruiseAltitude = 4500;
        ac.Route = "VPCOL";
        ac.Remarks = "SOLO XC";

        Assert.Equal("C172", window.FindControl<TextBox>("TypBox")!.Text);
        Assert.Equal("G", window.FindControl<TextBox>("EqBox")!.Text);
        Assert.Equal("KHWD", window.FindControl<TextBox>("DepBox")!.Text);
        Assert.Equal("KSQL", window.FindControl<TextBox>("DestBox")!.Text);
        Assert.Equal("110", window.FindControl<TextBox>("SpdBox")!.Text);
        Assert.Equal("045", window.FindControl<TextBox>("AltBox")!.Text);
        Assert.Equal("VPCOL", window.FindControl<TextBox>("RteBox")!.Text);
        Assert.Equal("SOLO XC", window.FindControl<TextBox>("RmkBox")!.Text);

        // The refreshed values are the new baseline — nothing is pending to submit.
        Assert.False(window.FindControl<Button>("SubmitButton")!.IsEnabled);
    }

    [AvaloniaFact]
    public void EditedField_KeepsInstructorText_WhileOtherFieldsRefresh()
    {
        var ac = BuildAircraft();
        var window = Open(ac);

        // Instructor is mid-edit on the route when an amendment to other fields arrives.
        var rteBox = window.FindControl<TextBox>("RteBox")!;
        rteBox.Text = "VPSUN VPCOL";
        HeadlessWindowExtensions.PumpDispatcher();
        ac.Destination = "KSQL";

        Assert.Equal("VPSUN VPCOL", rteBox.Text);
        Assert.Equal("KSQL", window.FindControl<TextBox>("DestBox")!.Text);
        // The route edit is still pending against the live plan.
        Assert.True(window.FindControl<Button>("SubmitButton")!.IsEnabled);
    }

    [AvaloniaFact]
    public void EditedField_BaselineTracksLivePlan_WhenSameFieldIsAmended()
    {
        var ac = BuildAircraft();
        var window = Open(ac);

        // Instructor types the same value the incoming amendment carries — no longer a change.
        var destBox = window.FindControl<TextBox>("DestBox")!;
        destBox.Text = "KSQL";
        HeadlessWindowExtensions.PumpDispatcher();
        Assert.True(window.FindControl<Button>("SubmitButton")!.IsEnabled);

        ac.Destination = "KSQL";

        Assert.Equal("KSQL", destBox.Text);
        Assert.False(window.FindControl<Button>("SubmitButton")!.IsEnabled);
    }

    [AvaloniaFact]
    public void RemarksRefresh_TracksProtocolPrefix_ForLaterSubmit()
    {
        var ac = BuildAircraft();
        FlightPlanAmendment? submitted = null;
        var window = Open(ac, (_, amendment) => submitted = amendment);

        // Amendment introduces a protocol prefix; the editable part shows without it.
        ac.Remarks = "+/V/PILOT RMK/NEW TEXT";
        var rmkBox = window.FindControl<TextBox>("RmkBox")!;
        Assert.Equal("NEW TEXT", rmkBox.Text);

        // A subsequent edit + submit round-trips the new prefix.
        rmkBox.Text = "EDITED TEXT";
        HeadlessWindowExtensions.PumpDispatcher();
        var submit = window.FindControl<Button>("SubmitButton")!;
        Assert.True(submit.IsEnabled);
        submit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent, submit));

        Assert.NotNull(submitted);
        Assert.Equal("+/V/PILOT RMK/EDITED TEXT", submitted!.Remarks);
    }
}
