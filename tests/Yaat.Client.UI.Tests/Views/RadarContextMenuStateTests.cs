using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim.Commands;
using CatalogMenuView = Yaat.Client.ContextMenus.MenuView;

namespace Yaat.Client.UI.Tests.Views;

// Regression for the reported bug: an airborne aircraft showed "Cleared for takeoff" in the
// radar Tower submenu. The Tower/Pattern submenus are now state-aware and return null (and are
// omitted) when nothing applies. Builds run through AircraftCommandApplicability.
public class RadarContextMenuStateTests
{
    /// <summary>
    /// A menu context outside solo training, under the "VFR commands for IFR aircraft" setting
    /// <see cref="VfrCommandsForIfr.None"/>.
    /// </summary>
    private static MenuContext Context(string callsign) => Context(callsign, false, VfrCommandsForIfr.None);

    private static MenuContext Context(string callsign, bool soloTrainingMode, VfrCommandsForIfr vfrCommandsForIfr) =>
        TestMenuContext.Create(callsign, "AB", null, soloTrainingMode, vfrCommandsForIfr, CatalogMenuView.Radar);

    /// <summary>An aircraft on final for 28R under <paramref name="flightRules"/>, which cleared to land applies to.</summary>
    private static AircraftModel OnFinal(string callsign, string flightRules) =>
        new()
        {
            Callsign = callsign,
            IsOnGround = false,
            CurrentPhase = "FinalApproach",
            FlightRules = flightRules,
            AssignedRunway = "28R",
        };

    [AvaloniaTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Tower_ForceLanding_HiddenInSoloTraining(bool soloTrainingMode, bool offered)
    {
        AircraftModel ac = OnFinal("AAL123", "IFR");

        MenuItem? tower = SharedMenuGroups.Tower(ac, Context("AAL123", soloTrainingMode, VfrCommandsForIfr.None), new RecordingMenuHost(""));

        Assert.NotNull(tower);
        List<string> headers = Headers(tower);
        Assert.Contains("Cleared to land 28R", headers);
        Assert.Equal(offered, headers.Contains("Force landing 28R"));
    }

    [AvaloniaTheory]
    [InlineData(VfrCommandsForIfr.None, false)]
    [InlineData(VfrCommandsForIfr.EnterFinalOnly, false)]
    [InlineData(VfrCommandsForIfr.All, true)]
    public void Tower_VfrOptions_HiddenForIfrUnderNone_ShownUnderAll(VfrCommandsForIfr mode, bool offered)
    {
        AircraftModel ac = OnFinal("AAL123", "IFR");

        MenuItem? tower = SharedMenuGroups.Tower(ac, Context("AAL123", false, mode), new RecordingMenuHost(""));

        Assert.NotNull(tower);
        List<string> headers = Headers(tower);
        string[] options = ["Cleared for the option 28R", "Touch and go 28R", "Stop and go 28R", "Low approach 28R"];
        Assert.All(options, option => Assert.Equal(offered, headers.Contains(option)));
    }

    private static List<string> Headers(MenuItem menu) =>
        [.. menu.Items.OfType<MenuItem>().Where(m => m.Header is string).Select(m => (string)m.Header!)];

    [AvaloniaFact]
    public void AirborneIfrOnFinal_TowerHasLanding_NotTakeoff()
    {
        var ac = new AircraftModel
        {
            Callsign = "AAL123",
            IsOnGround = false,
            CurrentPhase = "FinalApproach",
            FlightRules = "IFR",
            AssignedRunway = "28R",
        };

        MenuItem? tower = SharedMenuGroups.Tower(ac, Context("AAL123"), new RecordingMenuHost(""));

        Assert.NotNull(tower);
        List<string> headers = Headers(tower!);
        Assert.Contains("Cleared to land 28R", headers);
        Assert.Contains("Go around 28R", headers);
        Assert.DoesNotContain(headers, h => h.StartsWith("Line up and wait", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Cleared for takeoff", StringComparison.Ordinal));
        // VFR-only options hidden for IFR.
        Assert.DoesNotContain(headers, h => h.StartsWith("Touch and go", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void GroundDeparture_TowerHasTakeoff_NotLanding()
    {
        var ac = new AircraftModel
        {
            Callsign = "SWA1",
            IsOnGround = true,
            CurrentPhase = "LinedUpAndWaiting",
            FlightRules = "IFR",
            AssignedRunway = "30",
        };

        MenuItem? tower = SharedMenuGroups.Tower(ac, Context("SWA1"), new RecordingMenuHost(""));

        Assert.NotNull(tower);
        List<string> headers = Headers(tower!);
        Assert.Contains("Cancel takeoff clearance", headers);
        Assert.Contains(headers, h => h.StartsWith("Cleared for takeoff", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Cleared to land", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void AirborneDeparture_TowerOmitted()
    {
        var ac = new AircraftModel
        {
            Callsign = "UAL9",
            IsOnGround = false,
            CurrentPhase = "InitialClimb",
            FlightRules = "IFR",
            AssignedRunway = "1L",
        };

        MenuItem? tower = SharedMenuGroups.Tower(ac, Context("UAL9"), new RecordingMenuHost(""));

        // Nothing tower-related applies to a climbing departure — the submenu is dropped.
        Assert.Null(tower);
    }

    [AvaloniaFact]
    public void Landing_TowerHasExits()
    {
        var ac = new AircraftModel
        {
            Callsign = "DAL5",
            IsOnGround = true,
            CurrentPhase = "Landing",
            FlightRules = "IFR",
            AssignedRunway = "28R",
        };

        MenuItem? tower = SharedMenuGroups.Tower(ac, Context("DAL5"), new RecordingMenuHost(""));

        Assert.NotNull(tower);
        List<string> headers = Headers(tower!);
        Assert.Contains("Exit left", headers);
        Assert.Contains("Exit right", headers);
        Assert.DoesNotContain(headers, h => h.StartsWith("Cleared for takeoff", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void IfrTakeoffClearance_HidesVfrModifiers()
    {
        var ac = new AircraftModel
        {
            Callsign = "AAL2",
            IsOnGround = true,
            CurrentPhase = "LinedUpAndWaiting",
            FlightRules = "IFR",
            AssignedRunway = "30",
        };

        MenuItem? tower = SharedMenuGroups.Tower(ac, Context("AAL2"), new RecordingMenuHost(""));
        Assert.NotNull(tower);

        MenuItem? cto = tower!.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header is "Cleared for takeoff 30");
        Assert.NotNull(cto);
        List<string> ctoHeaders = Headers(cto!);
        // IFR gets the default (follow-SID) clearance and an explicit runway-heading clearance (issue #221).
        Assert.Contains("Default (SID/on course)", ctoHeaders);
        Assert.Contains("Fly runway heading", ctoHeaders);
        // On-course and pattern modifiers are VFR-only — hidden for IFR.
        Assert.DoesNotContain("Fly on course", ctoHeaders);
        Assert.DoesNotContain("Make left traffic", ctoHeaders);
        Assert.DoesNotContain("360 overhead", ctoHeaders);
    }

    [AvaloniaFact]
    public void VfrTakeoffClearance_ShowsRunwayHeadingAndOnCourseAndModifiers()
    {
        var ac = new AircraftModel
        {
            Callsign = "N123",
            IsOnGround = true,
            CurrentPhase = "LinedUpAndWaiting",
            FlightRules = "VFR",
            AssignedRunway = "30",
        };

        MenuItem? tower = SharedMenuGroups.Tower(ac, Context("N123"), new RecordingMenuHost(""));
        Assert.NotNull(tower);

        MenuItem? cto = tower!.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header is "Cleared for takeoff 30");
        Assert.NotNull(cto);
        Assert.Equal(
            [
                "Default (SID/on course)",
                "Fly runway heading",
                "Fly on course",
                "Make left traffic",
                "Make right traffic",
                "Turn left crosswind",
                "Turn right crosswind",
                "Turn left downwind",
                "Turn right downwind",
                "Left 270",
                "Right 270",
                "360 overhead",
                "Custom...",
            ],
            Headers(cto!)
        );
        List<object?> items = [.. cto!.Items];
        int custom = items.FindIndex(i => i is MenuItem { Header: "Custom..." });
        Assert.IsType<Separator>(items[custom - 1]);
    }

    [AvaloniaFact]
    public void VfrPatternAircraft_PatternSubmenuLegGated()
    {
        var ac = new AircraftModel
        {
            Callsign = "N77",
            IsOnGround = false,
            CurrentPhase = "Upwind",
            FlightRules = "VFR",
            AssignedRunway = "28L",
        };

        MenuItem? pattern = SharedMenuGroups.Pattern(ac, Context("N77"), new RecordingMenuHost(""));

        Assert.NotNull(pattern);
        List<string> headers = Headers(pattern!);
        // From upwind only the crosswind turn is valid.
        Assert.Contains("Turn crosswind", headers);
        Assert.DoesNotContain("Turn downwind", headers);
        Assert.DoesNotContain("Turn base", headers);
    }

    [AvaloniaFact]
    public void IfrAircraft_PatternSubmenuOmitted()
    {
        var ac = new AircraftModel
        {
            Callsign = "AAL3",
            IsOnGround = false,
            CurrentPhase = "ApproachNav",
            FlightRules = "IFR",
            AssignedRunway = "28R",
        };

        MenuItem? pattern = SharedMenuGroups.Pattern(ac, Context("AAL3"), new RecordingMenuHost(""));

        // Pattern ops are VFR-only.
        Assert.Null(pattern);
    }
}
