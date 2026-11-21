using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Views.Scope.FieldMenus.Crc;
using static Yaat.Client.UI.Tests.Views.Crc.CrcMenuHarness;

namespace Yaat.Client.UI.Tests.Views.Crc;

/// <summary>The CRC-style ERAM speed menu (ViewSpeedMenu): 14 characters wide, KT / + / −, the speed list and DEL.</summary>
public class CrcSpeedMenuTests
{
    private static (CrcSpeedMenu Menu, Window Window, List<CrcSpeedPick> Picks) Open(string? assigned) => Open(assigned, chip: false);

    private static (CrcSpeedMenu Menu, Window Window, List<CrcSpeedPick> Picks) Open(string? assigned, bool chip)
    {
        var menu = new CrcSpeedMenu("UAL123", new CrcSpeedValues(assigned, null, 350), CrcMenuMode.Pilot, chip);
        var picks = new List<CrcSpeedPick>();
        menu.Picked += picks.Add;
        return (menu, Show(menu), picks);
    }

    [AvaloniaFact]
    public void OuterSize_IsCrcSize()
    {
        (CrcSpeedMenu menu, _, _) = Open("280");

        Assert.Equal(new Size(169, 191), menu.Bounds.Size);
    }

    [AvaloniaFact]
    public void OuterSize_WithChip_AddsTheChipRow()
    {
        (CrcSpeedMenu menu, _, _) = Open("280", chip: true);

        Assert.Equal(new Size(169, 191 + ChipRowHeight), menu.Bounds.Size);
    }

    [AvaloniaFact]
    public void KnotsList_OpensCentredOnTheAssignedSpeed()
    {
        (CrcSpeedMenu menu, _, _) = Open("280");

        Assert.Equal(["310", "300", "290", "280", "270", "260", "250"], menu.VisibleSpeeds);
    }

    [AvaloniaFact]
    public void SpeedPick_RaisesKt_AndShiftSetsShift()
    {
        (CrcSpeedMenu menu, Window window, List<CrcSpeedPick> picks) = Open("280");

        Click(window, menu, "250");
        Click(window, menu, "260", shift: true);

        Assert.Equal([new CrcSpeedPick(250, false, CrcSpeedPickArea.Kt, false), new CrcSpeedPick(260, false, CrcSpeedPickArea.Kt, true)], picks);
    }

    [AvaloniaFact]
    public void Plus_TogglesWithoutAPick_ThenASpeedPickIsPlus()
    {
        (CrcSpeedMenu menu, Window window, List<CrcSpeedPick> picks) = Open("280");

        Click(window, menu, "+");
        Assert.Empty(picks);
        Click(window, menu, "250");

        Assert.Equal([new CrcSpeedPick(250, false, CrcSpeedPickArea.Plus, false)], picks);
    }

    [AvaloniaFact]
    public void Minus_TogglesWithoutAPick_ThenASpeedPickIsMinus()
    {
        (CrcSpeedMenu menu, Window window, List<CrcSpeedPick> picks) = Open("280");

        Click(window, menu, "-");
        Click(window, menu, "250");

        Assert.Equal([new CrcSpeedPick(250, false, CrcSpeedPickArea.Minus, false)], picks);
    }

    [AvaloniaFact]
    public void Kt_TogglesToMach_ThenASpeedPickIsMach()
    {
        (CrcSpeedMenu menu, Window window, List<CrcSpeedPick> picks) = Open("280");

        Click(window, menu, "KT");
        Assert.True(HasPickArea(menu, "M"));
        Click(window, menu, ".79");

        Assert.Equal([new CrcSpeedPick(79, true, CrcSpeedPickArea.Kt, false)], picks);
    }

    [AvaloniaFact]
    public void Del_RaisesDel_AndShowsOnlyWithAnAssignedSpeed()
    {
        (CrcSpeedMenu menu, Window window, List<CrcSpeedPick> picks) = Open("280");
        (CrcSpeedMenu unassigned, _, _) = Open(null);

        Click(window, menu, "DEL 280");

        Assert.Equal([new CrcSpeedPick(0, false, CrcSpeedPickArea.Del, false)], picks);
        Assert.DoesNotContain(unassigned.PickTargets, t => t.Key.StartsWith("DEL", StringComparison.Ordinal));
    }
}
