using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Views.Scope.FieldMenus.Crc;
using static Yaat.Client.UI.Tests.Views.Crc.CrcMenuHarness;

namespace Yaat.Client.UI.Tests.Views.Crc;

/// <summary>The CRC-style ERAM heading menu (ViewHeadingMenu): 10 characters wide, LT / PH / RT, the compass list and DELETE.</summary>
public class CrcHeadingMenuTests
{
    private static (CrcHeadingMenu Menu, Window Window, List<CrcHeadingPick> Picks) Open(string? assigned) => Open(assigned, chip: false);

    private static (CrcHeadingMenu Menu, Window Window, List<CrcHeadingPick> Picks) Open(string? assigned, bool chip)
    {
        var menu = new CrcHeadingMenu("UAL123", new CrcHeadingValues(265, assigned), CrcMenuMode.Eram, chip);
        var picks = new List<CrcHeadingPick>();
        menu.Picked += picks.Add;
        return (menu, Show(menu), picks);
    }

    [AvaloniaFact]
    public void OuterSize_IsCrcSize()
    {
        (CrcHeadingMenu menu, _, _) = Open("H270");

        Assert.Equal(new Size(121, 191), menu.Bounds.Size);
    }

    [AvaloniaFact]
    public void OuterSize_WithChip_AddsTheChipRow()
    {
        (CrcHeadingMenu menu, _, _) = Open("H270", chip: true);

        Assert.Equal(new Size(121, 191 + ChipRowHeight), menu.Bounds.Size);
    }

    [AvaloniaFact]
    public void CompassPick_RaisesHeading_AndShiftSetsShift()
    {
        (CrcHeadingMenu menu, Window window, List<CrcHeadingPick> picks) = Open("H270");

        Click(window, menu, "H270");
        Click(window, menu, "H265", shift: true);

        Assert.Equal([new CrcHeadingPick(270, CrcHeadingPickArea.Heading, false), new CrcHeadingPick(265, CrcHeadingPickArea.Heading, true)], picks);
    }

    [AvaloniaFact]
    public void Lt_SwapsToTheLeftTurnList_ThenAPickIsLt()
    {
        (CrcHeadingMenu menu, Window window, List<CrcHeadingPick> picks) = Open("H270");

        Click(window, menu, "LT");
        Assert.Empty(picks);
        Assert.False(HasPickArea(menu, "0L"));

        Click(window, menu, "20L");
        Assert.Equal([new CrcHeadingPick(20, CrcHeadingPickArea.Lt, false)], picks);
    }

    [AvaloniaFact]
    public void Rt_SwapsToTheRightTurnList_ThenAPickIsRt()
    {
        (CrcHeadingMenu menu, Window window, List<CrcHeadingPick> picks) = Open("H270");

        Click(window, menu, "RT");
        Click(window, menu, "35R");

        Assert.Equal([new CrcHeadingPick(35, CrcHeadingPickArea.Rt, false)], picks);
    }

    [AvaloniaFact]
    public void AssignedTurn_OpensOnItsTurnList()
    {
        (CrcHeadingMenu menu, Window window, List<CrcHeadingPick> picks) = Open("20L");

        Click(window, menu, "15L");

        Assert.Equal([new CrcHeadingPick(15, CrcHeadingPickArea.Lt, false)], picks);
    }

    [AvaloniaFact]
    public void Ph_RaisesPh()
    {
        (CrcHeadingMenu menu, Window window, List<CrcHeadingPick> picks) = Open("H270");

        Click(window, menu, "PH");

        Assert.Equal([new CrcHeadingPick(0, CrcHeadingPickArea.Ph, false)], picks);
    }

    [AvaloniaFact]
    public void Delete_RaisesDelete_AndShowsOnlyWithAnAssignedHeading()
    {
        (CrcHeadingMenu menu, Window window, List<CrcHeadingPick> picks) = Open("H270");
        (CrcHeadingMenu unassigned, _, _) = Open(null);

        Click(window, menu, "DELETE");

        Assert.Equal([new CrcHeadingPick(0, CrcHeadingPickArea.Delete, false)], picks);
        Assert.False(HasPickArea(unassigned, "DELETE"));
    }
}
