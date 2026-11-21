using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Views.Scope.FieldMenus.Crc;
using static Yaat.Client.UI.Tests.Views.Crc.CrcMenuHarness;

namespace Yaat.Client.UI.Tests.Views.Crc;

/// <summary>
/// The CRC-style ERAM altitude menu (ViewAltitudeMenu): 14 characters wide, the 590..010 list seven lines at a time
/// centred on the selected altitude, and one Picked record per pick area.
/// </summary>
public class CrcAltitudeMenuTests
{
    private static readonly CrcAltitudeValues FlightPlan350 = new(350, null, null, null);

    private static (CrcAltitudeMenu Menu, Window Window, List<CrcAltitudePick> Picks) Open(CrcAltitudeValues values) => Open(values, chip: false);

    private static (CrcAltitudeMenu Menu, Window Window, List<CrcAltitudePick> Picks) Open(CrcAltitudeValues values, bool chip)
    {
        var menu = new CrcAltitudeMenu("UAL123", values, CrcMenuMode.Eram, chip);
        var picks = new List<CrcAltitudePick>();
        menu.Picked += picks.Add;
        return (menu, Show(menu), picks);
    }

    [AvaloniaFact]
    public void OuterSize_IsCrcSize()
    {
        (CrcAltitudeMenu menu, _, _) = Open(FlightPlan350);

        Assert.Equal(new Size(169, 191), menu.Bounds.Size);
    }

    [AvaloniaFact]
    public void OuterSize_WithChip_AddsTheChipRow()
    {
        (CrcAltitudeMenu menu, _, _) = Open(FlightPlan350, chip: true);

        Assert.Equal(new Size(169, 191 + ChipRowHeight), menu.Bounds.Size);
    }

    [AvaloniaFact]
    public void List_Runs590To010_By20Above410()
    {
        (CrcAltitudeMenu menu, _, _) = Open(FlightPlan350);

        int[] expected = [590, 570, 550, 530, 510, 490, 470, 450, 430, 410, .. Enumerable.Range(1, 40).Select(i => 410 - (i * 10))];
        Assert.Equal(expected, menu.Altitudes);
    }

    [AvaloniaFact]
    public void List_OpensCentredOnTheSelectedAltitude_SevenLines()
    {
        (CrcAltitudeMenu menu, _, _) = Open(FlightPlan350);

        Assert.Equal([380, 370, 360, 350, 340, 330, 320], menu.VisibleAltitudes);
    }

    [AvaloniaFact]
    public void List_InsertsAnOffListSelectionInOrder_AndCentresOnIt()
    {
        (CrcAltitudeMenu menu, _, _) = Open(new CrcAltitudeValues(350, 355, null, null));

        Assert.Equal(51, menu.Altitudes.Count);
        Assert.Equal([380, 370, 360, 355, 350, 340, 330], menu.VisibleAltitudes);
    }

    [AvaloniaFact]
    public void Arrows_ScrollAPageAtATime()
    {
        (CrcAltitudeMenu menu, Window window, _) = Open(FlightPlan350);

        Click(window, menu, "DOWN");
        Assert.Equal([310, 300, 290, 280, 270, 260, 250], menu.VisibleAltitudes);

        Click(window, menu, "UP");
        Assert.Equal(380, menu.VisibleAltitudes[0]);
        Click(window, menu, "UP");
        Assert.Equal(490, menu.VisibleAltitudes[0]);
        Click(window, menu, "UP");
        Assert.Equal(590, menu.VisibleAltitudes[0]);
    }

    [AvaloniaFact]
    public void RowPick_RaisesAltitude_AndShiftSetsShift()
    {
        (CrcAltitudeMenu menu, Window window, List<CrcAltitudePick> picks) = Open(FlightPlan350);

        Click(window, menu, "350");
        Click(window, menu, "340", shift: true);

        Assert.Equal(
            [new CrcAltitudePick(350, CrcAltitudePickArea.Altitude, false), new CrcAltitudePick(340, CrcAltitudePickArea.Altitude, true)],
            picks
        );
    }

    [AvaloniaFact]
    public void TColumnPick_RaisesT()
    {
        (CrcAltitudeMenu menu, Window window, List<CrcAltitudePick> picks) = Open(FlightPlan350);

        Click(window, menu, "340 T");

        Assert.Equal([new CrcAltitudePick(340, CrcAltitudePickArea.T, false)], picks);
    }

    [AvaloniaFact]
    public void LocalTalt_TogglesWithoutAPick_ThenARowPickIsLocalTalt()
    {
        (CrcAltitudeMenu menu, Window window, List<CrcAltitudePick> picks) = Open(FlightPlan350);

        Click(window, menu, "LOCAL TALT");
        Assert.Empty(picks);
        Assert.False(HasPickArea(menu, "330 T"));

        Click(window, menu, "330");
        Assert.Equal([new CrcAltitudePick(330, CrcAltitudePickArea.LocalTalt, false)], picks);
    }

    [AvaloniaFact]
    public void Procedure_TogglesWithoutAPick_ThenARowPickIsProcedure()
    {
        (CrcAltitudeMenu menu, Window window, List<CrcAltitudePick> picks) = Open(FlightPlan350);

        Click(window, menu, "PROCEDURE");
        Click(window, menu, "330", shift: true);

        Assert.Equal([new CrcAltitudePick(330, CrcAltitudePickArea.Procedure, true)], picks);
    }

    [AvaloniaFact]
    public void FpPickArea_ShowsWithAnInterim_AndRaisesFp()
    {
        (CrcAltitudeMenu menu, Window window, List<CrcAltitudePick> picks) = Open(new CrcAltitudeValues(350, 240, null, null));

        Click(window, menu, "FP 350");

        Assert.Equal([new CrcAltitudePick(350, CrcAltitudePickArea.Fp, false)], picks);
    }

    [AvaloniaFact]
    public void InterimTPickArea_ShowsWithInterimAndLocal_AndRaisesInterimT()
    {
        (CrcAltitudeMenu menu, Window window, List<CrcAltitudePick> picks) = Open(new CrcAltitudeValues(350, 240, 200, null));

        Click(window, menu, "240 T");

        Assert.Equal([new CrcAltitudePick(240, CrcAltitudePickArea.InterimT, false)], picks);
    }

    [AvaloniaFact]
    public void NoTemporaryAltitude_HidesFpAndInterimT()
    {
        (CrcAltitudeMenu menu, _, _) = Open(FlightPlan350);

        string[] buttons = [.. menu.PickTargets.Select(t => t.Key).Where(k => !char.IsDigit(k[0]))];
        Assert.Equal(["X", "LOCAL TALT", "PROCEDURE", "UP", "DOWN"], buttons);
    }
}
