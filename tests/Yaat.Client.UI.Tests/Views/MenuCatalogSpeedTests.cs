using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Pins the Assign speed picker's rows. Every aircraft leads with <c>Resume normal speed</c> (<c>RNS</c>) and
/// <c>Final approach speed</c> (<c>RFAS</c>, labelled with the filed type's approach speed rounded to the knot). An aircraft
/// with a filed type then gets every tenth knot from its approach speed rounded up through its knots ceiling — the highest
/// of its cruise, climb and descent speeds at the aircraft's altitude and the profile's FL150/FL240 climb and FL100 descent
/// knots, plus 20 kt (10 for a piston), floored to ten and capped at 250 below 10,000 ft unless the type waives the limit —
/// then, at or above FL240, the Mach rows its profile spans, each listed <c>M.78</c> and sent as <c>MACH .78</c>. Without a
/// filed type the knots stay 150-350.
/// </summary>
public class MenuCatalogSpeedTests
{
    private const string Callsign = "N1AA";

    /// <summary>How many rows every list leads with, before the knots rows.</summary>
    private const int LeadingRows = 2;

    /// <summary>Real aircraft data, so each type's profile, FAA record and category resolve as they do in the app.</summary>
    public MenuCatalogSpeedTests() => TestVnasData.EnsureInitialized();

    private static MenuContext Context() => TestMenuContext.Create(Callsign, "AB", null, false, VfrCommandsForIfr.None);

    private static AircraftModel Aircraft(string type, double altitude) =>
        new()
        {
            Callsign = Callsign,
            FiledAircraftType = type,
            Altitude = altitude,
        };

    /// <summary>The popup the Assign speed picker opens for <paramref name="aircraft"/>: its row texts and the highlighted row.</summary>
    private static (IReadOnlyList<string> Items, object? Selected) SpeedPopup(AircraftModel aircraft)
    {
        var host = new RecordingMenuHost("");
        MenuItem? item = MenuCatalog.Get(MenuIds.SpeedAssign).Build(aircraft, Context(), host);
        Assert.NotNull(item);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return Assert.Single(host.ListPopups);
    }

    private static IReadOnlyList<string> SpeedItems(AircraftModel aircraft) => SpeedPopup(aircraft).Items;

    private static IReadOnlyList<string> SpeedItems(string type, double altitude) => SpeedItems(Aircraft(type, altitude));

    /// <summary>The two rows every Assign speed list leads with.</summary>
    private static string[] Leading(string finalApproachSpeedLabel) => ["Resume normal speed", finalApproachSpeedLabel];

    /// <summary>Every tenth knot from <paramref name="first"/> through <paramref name="last"/>, as the popup lists them.</summary>
    private static string[] Tens(int first, int last) => [.. Enumerable.Range(first / 10, ((last - first) / 10) + 1).Select(tens => $"{tens * 10}")];

    /// <summary>B738: ACD Vref 144 floors the knots at 150; 290 (its FL150/FL240 climb and FL100 descent knots) + 20 is 310,
    /// which the top Mach row M.81 (IAS 275.7) floors down to 270; the spanned Mach values are 0.78 (final climb and initial
    /// descent) and the cruise TAS 460 at FL348 as M.797, so the rows run M.74 (0.78 - 0.04) through M.81 (0.797 + 0.01, on
    /// the hundredth). The two leading rows come first, then the knots, then the Mach rows.</summary>
    [AvaloniaFact]
    public void B738_AtFl350_ListsMachRowsFromM74ToM81() =>
        Assert.Equal(
            [.. Leading("Final approach speed (144)"), .. Tens(150, 270), "M.74", "M.75", "M.76", "M.77", "M.78", "M.79", "M.80", "M.81"],
            SpeedItems("B738", 35_000)
        );

    /// <summary>B738 at 5,000 ft: below 10,000 ft the 250 kt limit caps the ceiling, and no Mach rows are listed below FL240.</summary>
    [AvaloniaFact]
    public void B738_At5000Ft_KnotsFromRoundedUpApproachTo250_NoMach() =>
        Assert.Equal([.. Leading("Final approach speed (144)"), .. Tens(150, 250)], SpeedItems("B738", 5_000));

    /// <summary>The knots stop at 270, the top Mach row's own IAS floored to ten, four rows short of the 310 the schedule alone gives.</summary>
    [AvaloniaFact]
    public void B738_AtFl350_KnotsCappedAtTheTopMachRowsIas()
    {
        IReadOnlyList<string> items = SpeedItems("B738", 35_000);
        string[] knots = [.. items.Skip(LeadingRows).TakeWhile(text => !text.StartsWith('M'))];

        Assert.Equal(Tens(150, 270), knots);
        Assert.Equal(275.7, WindInterpolator.MachToIas(0.81, 35_000), 1);
    }

    /// <summary>C172: ACD Vref 62 floors the knots at 70; the highest value in play is its own FL100 descent knot 120, and
    /// the piston margin makes that 130. No Mach rows.</summary>
    [AvaloniaFact]
    public void C172_At3000Ft_FloorAndPistonMargin() =>
        Assert.Equal([.. Leading("Final approach speed (62)"), .. Tens(70, 130)], SpeedItems("C172", 3_000));

    /// <summary>DH8D resolves to its DH8C sibling's profile, which carries no Mach value, so FL250 gives knots only: ACD
    /// Vref 125 floors them at 130, and the highest value in play is its FL100 descent knot 250 + 20.</summary>
    [AvaloniaFact]
    public void Turboprop_AtFl250_ListsKnotsOnly() =>
        Assert.Equal([.. Leading("Final approach speed (125)"), .. Tens(130, 270)], SpeedItems("DH8D", 25_000));

    /// <summary>A388 has no profile and no sibling, so a jet's Mach rows come from the jet category baseline: 0.74 for the
    /// final climb and initial descent speeds and the baseline cruise TAS 440 at FL350 as M.763, spanning M.70 through M.77.
    /// The knots fall to the jet category default speed + 20, and end at 290 because the top Mach row M.77 itself is 291.4
    /// KIAS at FL300, which floors to ten below the 300 the schedule gives.</summary>
    [AvaloniaFact]
    public void UnprofiledJet_AtFl300_ListsTheJetBaselineMachRows() =>
        Assert.Equal(
            [.. Leading("Final approach speed (138)"), .. Tens(140, 290), "M.70", "M.71", "M.72", "M.73", "M.74", "M.75", "M.76", "M.77"],
            SpeedItems("A388", 30_000)
        );

    /// <summary>C680 spans M.78 (0.82 - 0.04) through M.90 (its TAS cruise 510 at FL552 as M.889, + 0.01). At FL800 the top
    /// row's own IAS is 107.8 kt, so the Mach cap draws the knots ceiling below the 110 kt floor and no knots row is left:
    /// the list is the two leading rows and the Mach rows, with nothing highlighted when no speed is assigned.</summary>
    [AvaloniaFact]
    public void CapsLeavingNoKnotsRows_KeepTheLeadingRowsAndMachRows_WithNoSeed()
    {
        (IReadOnlyList<string> items, object? selected) = SpeedPopup(Aircraft("C680", 80_000));

        Assert.Equal(
            [
                .. Leading("Final approach speed (108)"),
                "M.78",
                "M.79",
                "M.80",
                "M.81",
                "M.82",
                "M.83",
                "M.84",
                "M.85",
                "M.86",
                "M.87",
                "M.88",
                "M.89",
                "M.90",
            ],
            items
        );
        Assert.Null(selected);
    }

    [AvaloniaFact]
    public void MachRow_SendsMachCommand_KnotsRowSendsSpd()
    {
        var machHost = new RecordingMenuHost("M.78");
        MenuItem? machItem = MenuCatalog.Get(MenuIds.SpeedAssign).Build(Aircraft("B738", 35_000), Context(), machHost);
        Assert.NotNull(machItem);
        machItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal([(Callsign, "MACH .78", "AB")], machHost.Sent);

        var knotsHost = new RecordingMenuHost("250");
        MenuItem? knotsItem = MenuCatalog.Get(MenuIds.SpeedAssign).Build(Aircraft("B738", 35_000), Context(), knotsHost);
        Assert.NotNull(knotsItem);
        knotsItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal([(Callsign, "SPD 250", "AB")], knotsHost.Sent);
    }

    [AvaloniaFact]
    public void PickingResumeNormalSpeed_SendsRns()
    {
        var host = new RecordingMenuHost("Resume normal speed");
        MenuItem? item = MenuCatalog.Get(MenuIds.SpeedAssign).Build(Aircraft("B738", 35_000), Context(), host);
        Assert.NotNull(item);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal([(Callsign, "RNS", "AB")], host.Sent);
    }

    [AvaloniaFact]
    public void PickingFinalApproachSpeed_SendsRfas()
    {
        var host = new RecordingMenuHost("Final approach speed (144)");
        MenuItem? item = MenuCatalog.Get(MenuIds.SpeedAssign).Build(Aircraft("B738", 35_000), Context(), host);
        Assert.NotNull(item);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal([(Callsign, "RFAS", "AB")], host.Sent);
    }

    /// <summary>F16 waives 14 CFR 91.117, so 5,000 ft does not cap its list at 250: ACD Vref 160 floors the knots, and its
    /// FL100 descent knot 350 (its own cruise TAS 495 resolving to ~249 KIAS at FL425, and 300 kt climb knots, are lower)
    /// plus the 20 kt margin leaves the ceiling at 370.</summary>
    [AvaloniaFact]
    public void WaivedType_Below10000Ft_KeepsItsCeilingAbove250() =>
        Assert.Equal([.. Leading("Final approach speed (160)"), .. Tens(160, 370)], SpeedItems("F16", 5_000));

    [AvaloniaFact]
    public void NoFiledType_ListsTheLeadingRowsThen150To350() =>
        Assert.Equal([.. Leading("Final approach speed"), .. Tens(150, 350)], SpeedItems(new AircraftModel { Callsign = Callsign }));
}
