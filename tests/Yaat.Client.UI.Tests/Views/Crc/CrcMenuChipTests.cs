using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Views.Scope.FieldMenus.Crc;
using static Yaat.Client.UI.Tests.Views.Crc.CrcMenuHarness;

namespace Yaat.Client.UI.Tests.Views.Crc;

/// <summary>The ERAM | PILOT chip row every CRC-style menu carries when asked, and the X cell of its header.</summary>
public class CrcMenuChipTests
{
    private static CrcFdbMenu[] AllMenus(bool chip) =>
        [
            new CrcAltitudeMenu("UAL123", new CrcAltitudeValues(350, null, null, null), CrcMenuMode.Eram, chip),
            new CrcHeadingMenu("UAL123", new CrcHeadingValues(270, "H270"), CrcMenuMode.Eram, chip),
            new CrcSpeedMenu("UAL123", new CrcSpeedValues("280", null, 350), CrcMenuMode.Eram, chip),
        ];

    [AvaloniaFact]
    public void ChipClick_RaisesModeChanged_OnEveryMenu()
    {
        foreach (CrcFdbMenu menu in AllMenus(chip: true))
        {
            Window window = Show(menu);
            var modes = new List<CrcMenuMode>();
            menu.ModeChanged += modes.Add;

            Click(window, menu, "PILOT");
            Click(window, menu, "ERAM");

            Assert.Equal([CrcMenuMode.Pilot, CrcMenuMode.Eram], modes);
            Assert.Equal(CrcMenuMode.Eram, menu.Mode);
        }
    }

    [AvaloniaFact]
    public void LitChipClick_RaisesNothing()
    {
        var menu = new CrcAltitudeMenu("UAL123", new CrcAltitudeValues(350, null, null, null), CrcMenuMode.Pilot, true);
        Window window = Show(menu);
        var modes = new List<CrcMenuMode>();
        menu.ModeChanged += modes.Add;

        Click(window, menu, "PILOT");

        Assert.Empty(modes);
    }

    [AvaloniaFact]
    public void WithoutChip_ThereIsNoChipRow()
    {
        foreach (CrcFdbMenu menu in AllMenus(chip: false))
        {
            Show(menu);

            Assert.False(HasPickArea(menu, "ERAM"));
            Assert.False(HasPickArea(menu, "PILOT"));
            Assert.Equal(191, menu.Bounds.Height);
        }
    }

    [AvaloniaFact]
    public void XCell_RaisesCloseRequested()
    {
        foreach (CrcFdbMenu menu in AllMenus(chip: true))
        {
            Window window = Show(menu);
            int closes = 0;
            menu.CloseRequested += () => closes++;

            Click(window, menu, "X");

            Assert.Equal(1, closes);
        }
    }
}
