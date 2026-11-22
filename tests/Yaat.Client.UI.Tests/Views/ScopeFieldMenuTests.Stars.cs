using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Views.Crc;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.FieldMenus;
using Yaat.Client.Views.Scope.FieldMenus.Crc;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The "STARS field menus" setting: off (the default, as in CRC's STARS) a click on a STARS field only selects the track, the
/// note field still opening its box; on, the altitude and ground speed open the menu of the "ERAM field menus" style (Side by
/// side: today's catalog menus; Tabbed: a popup with one PILOT tab; Simple: CRC's compact menus in STARS colours, PILOT only),
/// and the squawk and line 3 keep their catalog menus in every style. A Mixed scope's STARS tracks follow the setting.
/// </summary>
public partial class ScopeFieldMenuTests
{
    // The STARS field menus on, in the style.
    private static Action<ScopeViewModel> StarsMenus(ScopeFieldMenuStyle style) =>
        scope =>
        {
            scope.Main.Preferences.SetScopeStarsFieldMenus(true);
            scope.Main.Preferences.SetScopeFieldMenuStyle(style);
        };

    // The STARS field menus on, side by side: today's catalog menus.
    private static void StarsMenusSideBySide(ScopeViewModel scope) => StarsMenus(ScopeFieldMenuStyle.SideBySide)(scope);

    private static void IdentingWithNote(AircraftModel ac)
    {
        Identing(ac);
        ac.Note = "Watch wake";
        ac.Scratchpad1 = "ABC";
    }

    [AvaloniaTheory]
    [InlineData(ScopeFieldSlot.Altitude)]
    [InlineData(ScopeFieldSlot.GroundSpeed)]
    [InlineData(ScopeFieldSlot.Ident)]
    [InlineData(ScopeFieldSlot.StatusLine3)]
    public async Task StarsFieldClick_MenusOff_OnlySelects(ScopeFieldSlot slot)
    {
        (MainViewModel main, _, ScopeView view, Window window, AircraftModel ac) = await ClickFieldAsync(
            ScopeTemplates.StarsId,
            slot,
            IdentingWithNote,
            _ => { }
        );
        try
        {
            Assert.False(main.Preferences.ScopeStarsFieldMenus);
            Assert.Same(ac, main.SelectedAircraft);
            Assert.Null(view.ActiveContextMenu);
            Assert.Null(view.ActiveFieldPopup);
            Assert.Null(view.ActiveEntryPopup);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task StarsNoteClick_MenusOff_StillOpensTheNoteBox()
    {
        (MainViewModel main, _, ScopeView view, Window window, AircraftModel ac) = await ClickFieldAsync(
            ScopeTemplates.StarsId,
            ScopeFieldSlot.InstructorNote,
            IdentingWithNote,
            _ => { }
        );
        Popup? note = null;
        try
        {
            Assert.Same(ac, main.SelectedAircraft);
            OverlayLayer overlay = Assert.IsType<OverlayLayer>(OverlayLayer.GetOverlayLayer(view.Canvas));
            note = overlay.Children.OfType<Popup>().LastOrDefault(p => p.IsOpen);
            Assert.NotNull(note);
        }
        finally
        {
            note?.Close();
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task Mixed_MenusOff_AStarsTrackOnlySelects_AndAnEramTrackOpensItsPopup()
    {
        (MainViewModel main, _, ScopeView starsView, Window starsWindow, AircraftModel starsTrack) = await ClickFieldAsync(
            ScopeTemplates.MixedId,
            ScopeFieldSlot.Altitude,
            ac => ac.OwnerSectorCode = "2S",
            _ => { }
        );
        try
        {
            Assert.Same(starsTrack, main.SelectedAircraft);
            Assert.Null(starsView.ActiveContextMenu);
            Assert.Null(starsView.ActiveFieldPopup);
        }
        finally
        {
            CloseWindow(starsWindow, starsView);
        }

        (_, _, ScopeView eramView, Window eramWindow, _) = await ClickFieldAsync(
            ScopeTemplates.MixedId,
            ScopeFieldSlot.EramAltitude,
            ac =>
            {
                ac.Owner = "OAK_45_CTR";
                ac.OwnerSectorCode = "C45";
            },
            _ => { }
        );
        try
        {
            Assert.IsType<ScopeDataAltitudePopup>(eramView.ActiveFieldPopup);
        }
        finally
        {
            CloseWindow(eramWindow, eramView);
        }
    }

    [AvaloniaFact]
    public async Task Mixed_MenusOn_AStarsTrackOpensItsMenu()
    {
        (_, _, ScopeView view, Window window, _) = await ClickFieldAsync(
            ScopeTemplates.MixedId,
            ScopeFieldSlot.Altitude,
            ac => ac.OwnerSectorCode = "2S",
            StarsMenusSideBySide
        );
        try
        {
            Assert.Equal(MenuCatalog.Get(MenuIds.AltitudeMaintain).Label, OpenEntry(view).Header);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaTheory]
    [InlineData(ScopeFieldMenuStyle.Simple)]
    [InlineData(ScopeFieldMenuStyle.Tabbed)]
    [InlineData(ScopeFieldMenuStyle.SideBySide)]
    public async Task StarsIdentClick_MenusOn_OpensTodaysSquawkMenu_InEveryStyle(ScopeFieldMenuStyle style)
    {
        (_, _, ScopeView view, Window window, _) = await ClickFieldAsync(ScopeTemplates.StarsId, ScopeFieldSlot.Ident, Identing, StarsMenus(style));
        try
        {
            ContextMenu menu = Assert.IsType<ContextMenu>(view.ActiveContextMenu);
            Assert.Equal(MenuCatalog.Get(MenuIds.SquawkCode).Label, Assert.IsType<MenuItem>(Assert.Single(menu.Items)).Header);
            Assert.Null(view.ActiveFieldPopup);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // The Tabbed style: a popup below the block with one PILOT tab, the ERAM tabbed popup's PILOT panel in it.

    private static ScopeStarsPilotPopup OpenStarsPilot(ScopeView view)
    {
        ScopeStarsPilotPopup popup = Assert.IsType<ScopeStarsPilotPopup>(view.ActiveFieldPopup);
        Assert.True(popup.IsOpen);
        return popup;
    }

    [AvaloniaFact]
    public async Task StarsAltitudeClick_Tabbed_OpensOnePilotTab_AndAPickSendsOrStagesTheClimb()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window, _) = await ClickFieldAsync(
            ScopeTemplates.StarsId,
            ScopeFieldSlot.Altitude,
            _ => { },
            StarsMenus(ScopeFieldMenuStyle.Tabbed)
        );
        try
        {
            ScopeStarsPilotPopup popup = OpenStarsPilot(view);
            Settle(window);
            Assert.Equal(ScopeFieldMenuMode.Pilot, popup.ShownTab);
            Assert.Null(popup.TabFor(ScopeFieldMenuMode.Eram));
            Assert.True(popup.TabFor(ScopeFieldMenuMode.Pilot)?.IsChecked);
            MaintainAltitudePanel panel = Assert.IsType<MaintainAltitudePanel>(popup.AltitudePanel);
            Button above = panel.RowFor(panel.Altitudes.Where(a => a > 5000).Min());

            ClickInPopup(above, RawInputModifiers.Shift);

            Assert.Matches(@"^\$ UAL238 CM \d+$", scope.EntryText);
            Assert.Equal(ScopeFieldMenuMode.Eram, main.Preferences.ScopeFieldMenuLastMode);
            Assert.Null(view.ActiveFieldPopup);

            Reopen(view, ScopeFieldAction.OpenAltitudeMenu);
            popup = OpenStarsPilot(view);
            panel = Assert.IsType<MaintainAltitudePanel>(popup.AltitudePanel);
            ClickInPopup(panel.RowFor(panel.Altitudes.Where(a => a > 5000).Min()), RawInputModifiers.None);

            Assert.Equal(NotConnected, main.StatusText);
            Assert.Equal(ScopeFieldMenuMode.Eram, main.Preferences.ScopeFieldMenuLastMode);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task StarsGroundSpeedClick_Tabbed_OpensTheAssignSpeedPanel_AndAShiftPickStagesSpd()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window, _) = await ClickFieldAsync(
            ScopeTemplates.StarsId,
            ScopeFieldSlot.GroundSpeed,
            Speedy,
            StarsMenus(ScopeFieldMenuStyle.Tabbed)
        );
        try
        {
            ScopeStarsPilotPopup popup = OpenStarsPilot(view);
            SpeedFlyoutPanel panel = Assert.IsType<SpeedFlyoutPanel>(popup.SpeedPanel);
            Settle(window);

            ClickInPopup(panel.KnotChip(250), RawInputModifiers.Shift);

            Assert.Equal("$ UAL238 SPD 250", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // The Simple style: CRC's compact menus in STARS colours, with no chip, sending PILOT commands only.

    [AvaloniaFact]
    public async Task StarsAltitudeClick_Simple_OpensTheCompactMenu_InStarsColours_WithNoEramOnlyParts()
    {
        (_, _, ScopeView view, Window window, _) = await ClickFieldAsync(
            ScopeTemplates.StarsId,
            ScopeFieldSlot.Altitude,
            Cruising,
            StarsMenus(ScopeFieldMenuStyle.Simple)
        );
        try
        {
            CrcAltitudeMenu menu = OpenCrc<CrcAltitudeMenu>(view);

            Assert.False(menu.ShowModeChip);
            Assert.Same(CrcPalette.Stars, menu.Palette);
            Assert.False(CrcMenuHarness.HasPickArea(menu, "LOCAL TALT"));
            Assert.False(CrcMenuHarness.HasPickArea(menu, "PROCEDURE"));
            Assert.False(CrcMenuHarness.HasPickArea(menu, "350 T"));
            Assert.True(CrcMenuHarness.HasPickArea(menu, "350"));
            Assert.Contains(menu.DrawOps, op => op is CrcQuadOp { Fill: { } fill } && (fill == CrcPalette.Stars.PickBackground));
            Assert.DoesNotContain(menu.DrawOps, op => op is CrcQuadOp { Fill: { } fill } && (fill == CrcPalette.Eram.PickBackground));
            AssertLevelWithTheField(view, window, menu, ScopeFieldSlot.Altitude);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task StarsAltitudeMenu_Simple_APickSendsTheClimb_AndAShiftPickStagesIt_RememberingNoMode()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window, _) = await ClickFieldAsync(
            ScopeTemplates.StarsId,
            ScopeFieldSlot.Altitude,
            Cruising,
            StarsMenus(ScopeFieldMenuStyle.Simple)
        );
        try
        {
            Settle(window);
            CrcMenuHarness.Click(window, OpenCrc<CrcAltitudeMenu>(view), "340", shift: true);

            Assert.Equal("$ UAL238 CM 34000", scope.EntryText);
            Assert.Null(view.ActiveFieldPopup);

            Reopen(view, ScopeFieldAction.OpenAltitudeMenu);
            Settle(window);
            CrcMenuHarness.Click(window, OpenCrc<CrcAltitudeMenu>(view), "350");

            Assert.Equal(NotConnected, main.StatusText);
            Assert.Null(view.ActiveFieldPopup);
            Assert.Equal(ScopeFieldMenuMode.Eram, main.Preferences.ScopeFieldMenuLastMode);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task StarsGroundSpeedClick_Simple_OpensTheCompactSpeedMenu_AndAPickStagesSpd()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window, _) = await ClickFieldAsync(
            ScopeTemplates.StarsId,
            ScopeFieldSlot.GroundSpeed,
            Speedy,
            StarsMenus(ScopeFieldMenuStyle.Simple)
        );
        try
        {
            CrcSpeedMenu menu = OpenCrc<CrcSpeedMenu>(view);
            Assert.False(menu.ShowModeChip);
            Assert.Same(CrcPalette.Stars, menu.Palette);
            AssertLevelWithTheField(view, window, menu, ScopeFieldSlot.GroundSpeed);

            CrcMenuHarness.Click(window, menu, "280", shift: true);

            Assert.Equal("$ UAL238 SPD 280", scope.EntryText);
            Assert.Null(view.ActiveFieldPopup);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task StarsAltitudeClick_SideBySide_OpensTodaysCatalogMenu()
    {
        (_, _, ScopeView view, Window window, _) = await ClickFieldAsync(
            ScopeTemplates.StarsId,
            ScopeFieldSlot.Altitude,
            _ => { },
            StarsMenusSideBySide
        );
        try
        {
            Assert.Equal(MenuCatalog.Get(MenuIds.AltitudeMaintain).Label, OpenEntry(view).Header);
            Assert.Null(view.ActiveFieldPopup);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // The menu's left edge at the block's right, its list's middle level with the middle of the drawn field in the slot, on
    // whatever line the block puts it.
    private static void AssertLevelWithTheField(ScopeView view, Window window, CrcFdbMenu menu, ScopeFieldSlot slot)
    {
        Settle(window);
        Rect block = Assert.IsType<Rect>(view.Canvas.BlockBoundsOf("UAL238"));
        FieldRect field = Assert.Single(view.Canvas.CurrentFrame.Tracks).Block.Fields.First(f => f.Slot == slot);
        Point topLeft = Assert.IsType<Point>(menu.TranslatePoint(default, view.Canvas));

        Assert.InRange(topLeft.X, block.Right - 1, block.Right + 1);
        Assert.InRange(topLeft.Y + menu.ListMiddleY, field.Rect.MidY - 1, field.Rect.MidY + 1);
    }

    [AvaloniaFact]
    public async Task StarsAltitudeMenu_Simple_APickBelowTheAircraft_SendsTheDescent()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window, _) = await ClickFieldAsync(
            ScopeTemplates.StarsId,
            ScopeFieldSlot.Altitude,
            ac => ac.CruiseAltitude = 3000,
            StarsMenus(ScopeFieldMenuStyle.Simple)
        );
        try
        {
            Settle(window);
            CrcMenuHarness.Click(window, OpenCrc<CrcAltitudeMenu>(view), "030", shift: true);

            Assert.Equal("$ UAL238 DM 3000", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task TurningTheMenusOn_WithTheTrackAlreadyDrawn_MakesTheNextClickOpenAMenu()
    {
        (MainViewModel main, _, ScopeView view, Window window, _) = await ClickFieldAsync(
            ScopeTemplates.StarsId,
            ScopeFieldSlot.Altitude,
            _ => { },
            _ => { }
        );
        try
        {
            Assert.Null(view.ActiveContextMenu);

            main.Preferences.SetScopeStarsFieldMenus(true);
            main.Preferences.SetScopeFieldMenuStyle(ScopeFieldMenuStyle.SideBySide);
            view.Canvas.MarkDirty();
            Settle(window);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            ClickAt(window, view.Canvas, Centre(view.Canvas, ScopeFieldSlot.Altitude));

            Assert.Equal(MenuCatalog.Get(MenuIds.AltitudeMaintain).Label, OpenEntry(view).Header);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }
}
