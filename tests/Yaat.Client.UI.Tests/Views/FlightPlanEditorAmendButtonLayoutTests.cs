using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

// The Flight Plan Editor's top row packs ten fixed-width columns (612px of fields) before the
// Amend/Create button, so a window narrow enough to squeeze the button's column renders it as a
// sliver the instructor cannot read or hit. The window has to be wide enough for the whole row at
// any width it can be restored to — a saved geometry narrower than that is clamped by MinWidth.
public class FlightPlanEditorAmendButtonLayoutTests
{
    private const double MinimumUsableButtonWidth = 60;

    [AvaloniaFact]
    public void AmendButton_IsFullyVisible_AtTheSavedWidth()
    {
        // 660 is the width users had saved for this window before the ICAO EQ field widened the row.
        var ac = new AircraftModel { Callsign = "N263FY", Destination = "KMOD" };
        var window = new FlightPlanEditorWindow(ac, (_, _) => { }, _ => Task.CompletedTask) { Width = 660 };
        window.ShowAndRunLayout();

        var submit = window.FindControl<Button>("SubmitButton");
        Assert.NotNull(submit);

        Assert.True(
            submit!.Bounds.Width >= submit.DesiredSize.Width,
            $"The Amend button rendered {submit.Bounds.Width:F1}px wide but wants {submit.DesiredSize.Width:F1}px "
                + $"(window {window.Bounds.Width:F1}px) — its content is clipped"
        );
        Assert.True(
            submit.Bounds.Width >= MinimumUsableButtonWidth,
            $"The Amend button rendered {submit.Bounds.Width:F1}px wide (window {window.Bounds.Width:F1}px) "
                + $"— below the {MinimumUsableButtonWidth:F0}px it needs to stay readable and clickable"
        );
    }
}
