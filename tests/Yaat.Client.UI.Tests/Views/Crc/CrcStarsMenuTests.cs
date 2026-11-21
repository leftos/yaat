using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;
using Yaat.Client.Views.Scope.FieldMenus.Crc;
using static Yaat.Client.UI.Tests.Views.Crc.CrcMenuHarness;

namespace Yaat.Client.UI.Tests.Views.Crc;

/// <summary>
/// The CRC-style menus as the STARS field menus open them: PILOT only, with no ERAM | PILOT chip, the altitude menu without
/// the ERAM-only LOCAL TALT and PROCEDURE pick areas and T column, drawn in CRC's STARS DCB colours (<see cref="CrcPalette.Stars"/>);
/// the ERAM menus keep CRC's ERAM colours.
/// </summary>
public class CrcStarsMenuTests
{
    [AvaloniaFact]
    public void StarsAltitudeMenu_HasNoChip_NoEramOnlyParts_AndStarsColours()
    {
        var menu = new CrcAltitudeMenu("UAL123", new CrcAltitudeValues(350, null, null, null), CrcPalette.Stars);
        var picks = new List<CrcAltitudePick>();
        menu.Picked += picks.Add;
        Window window = Show(menu);

        Assert.False(menu.ShowModeChip);
        Assert.Equal(CrcMenuMode.Pilot, menu.Mode);
        Assert.False(HasPickArea(menu, "LOCAL TALT"));
        Assert.False(HasPickArea(menu, "PROCEDURE"));
        Assert.False(HasPickArea(menu, "350 T"));
        Assert.Contains(menu.DrawOps, op => op is CrcQuadOp { Fill: { } fill } && (fill == CrcPalette.Stars.PickBackground));
        Assert.Contains(menu.DrawOps, op => op is CrcQuadOp { Fill: { } fill } && (fill == CrcPalette.Stars.Emphasis));
        Assert.Contains(menu.DrawOps, op => op is CrcTextOp { Text: "350" } text && (text.Color == CrcPalette.Stars.Text));
        Assert.DoesNotContain(menu.DrawOps, op => op is CrcQuadOp { Fill: { } fill } && (fill == CrcPalette.Eram.PickBackground));

        Click(window, menu, "340");

        Assert.Equal(new CrcAltitudePick(340, CrcAltitudePickArea.Altitude, false), Assert.Single(picks));
    }

    /// <summary>
    /// With interim and local interim altitudes set, the ERAM menu shows FP and <c>nnn T</c> above the list; the PILOT-only
    /// menu shows neither, since neither sends a pilot command.
    /// </summary>
    [AvaloniaFact]
    public void StarsAltitudeMenu_WithInterimAndLocalInterim_ShowsNoFpOrInterimTArea()
    {
        var values = new CrcAltitudeValues(350, 240, 200, null);
        var eram = new CrcAltitudeMenu("UAL123", values, CrcMenuMode.Eram, showModeChip: false);
        var stars = new CrcAltitudeMenu("UAL123", values, CrcPalette.Stars);
        Show(eram);
        Show(stars);

        Assert.True(HasPickArea(eram, "FP 350"));
        Assert.True(HasPickArea(eram, "240 T"));
        Assert.False(HasPickArea(stars, "FP 350"));
        Assert.False(HasPickArea(stars, "240 T"));
        Assert.DoesNotContain(stars.PickTargets, t => !t.IsText && t.Key is not ("X" or "UP" or "DOWN"));
    }

    [AvaloniaFact]
    public void StarsSpeedMenu_HasNoChip_AndStarsColours()
    {
        var menu = new CrcSpeedMenu("UAL123", new CrcSpeedValues(null, null, 100), CrcPalette.Stars);
        Show(menu);

        Assert.False(menu.ShowModeChip);
        Assert.Equal(CrcMenuMode.Pilot, menu.Mode);
        Assert.Contains(menu.DrawOps, op => op is CrcQuadOp { Fill: { } fill } && (fill == CrcPalette.Stars.PickBackground));
        Assert.DoesNotContain(menu.DrawOps, op => op is CrcQuadOp { Fill: { } fill } && (fill == CrcPalette.Eram.PickBackground));
    }

    [AvaloniaFact]
    public void EramMenus_KeepTheEramPalette()
    {
        Assert.Same(
            CrcPalette.Eram,
            new CrcAltitudeMenu("UAL123", new CrcAltitudeValues(350, null, null, null), CrcMenuMode.Eram, showModeChip: true).Palette
        );
        Assert.Same(CrcPalette.Eram, new CrcSpeedMenu("UAL123", new CrcSpeedValues(null, null, 100), CrcMenuMode.Eram, showModeChip: true).Palette);
        Assert.Same(CrcPalette.Eram, new CrcHeadingMenu("UAL123", new CrcHeadingValues(270, null), CrcMenuMode.Eram, showModeChip: true).Palette);
        Assert.Equal(Color.FromRgb(153, 153, 153), CrcPalette.Eram.PickBackground);
    }
}
