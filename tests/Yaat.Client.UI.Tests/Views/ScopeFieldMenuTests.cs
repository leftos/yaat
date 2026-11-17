using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.FieldMenus;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// A left click on a Scope field: a field with an action selects the track and opens that field's pick menu at the pointer,
/// its one catalog entry's submenu already open; a field without one only selects; ERAM Field A keeps its dwell-lock click;
/// the armed Rings tool wins; the EuroScope owner takes control. A row of a field menu sends for the clicked aircraft.
/// </summary>
public class ScopeFieldMenuTests
{
    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window)> ShowScope(string templateId)
    {
        MainViewModel main = await MainAsync();
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null) { TemplateId = templateId };
        main.OpenScope = scope;
        var view = new ScopeView { DataContext = scope };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };
        window.Closed += (_, _) => scope.Dispose();
        window.Show();
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (main, scope, view, window);
    }

    private static AircraftModel AddAt(MainViewModel main, ScopeCanvas canvas, Action<AircraftModel> setUp)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon(300f, 300f);
        var ac = new AircraftModel
        {
            Callsign = "UAL238",
            AircraftType = "B738",
            FiledAircraftType = "B738",
            FlightRules = "IFR",
            TransponderMode = "C",
            Altitude = 5000,
            GroundSpeed = 250,
            Position = new LatLon(lat, lon),
        };
        setUp(ac);
        main.Aircraft.Add(ac);
        Dispatcher.UIThread.RunJobs();
        return ac;
    }

    private static Point Centre(ScopeCanvas canvas, ScopeFieldSlot slot)
    {
        FieldRect field = Assert.Single(canvas.CurrentFrame.Tracks).Block.Fields.First(f => f.Slot == slot);
        return new Point(field.Rect.MidX, field.Rect.MidY);
    }

    private static void ClickAt(Window window, ScopeCanvas canvas, Point canvasPoint)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, MouseButton.Left);
        window.MouseUp(inWindow.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Shows the scope, runs <paramref name="beforeClick"/>, adds UAL238 at (300, 300) as <paramref name="setUp"/> leaves it
    /// with nothing selected, and clicks the centre of its <paramref name="slot"/> field.
    /// </summary>
    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window, AircraftModel Aircraft)> ClickFieldAsync(
        string templateId,
        ScopeFieldSlot slot,
        Action<AircraftModel> setUp,
        Action<ScopeViewModel> beforeClick
    )
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScope(templateId);
        await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        beforeClick(scope);
        Dispatcher.UIThread.RunJobs();
        AircraftModel ac = AddAt(main, view.Canvas, setUp);
        main.SelectedAircraft = null;
        Dispatcher.UIThread.RunJobs();
        ClickAt(window, view.Canvas, Centre(view.Canvas, slot));
        return (main, scope, view, window, ac);
    }

    private static void CloseWindow(Window window)
    {
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // The field menu closes before its window, which a headless window cannot close with a popup still open.
    private static void CloseWindow(Window window, ScopeView view)
    {
        view.ActiveContextMenu?.Close();
        Dispatcher.UIThread.RunJobs();
        CloseWindow(window);
    }

    private static MenuItem OpenEntry(ScopeView view)
    {
        ContextMenu menu = Assert.IsType<ContextMenu>(view.ActiveContextMenu);
        Assert.True(menu.IsOpen);
        MenuItem entry = Assert.IsType<MenuItem>(Assert.Single(menu.Items));
        Assert.True(entry.IsSubMenuOpen, "the entry's submenu is not open");
        return entry;
    }

    [AvaloniaFact]
    public async Task StarsAltitudeClick_SelectsTheTrack_AndOpensTheAltitudeEntryWithItsSubmenuOpen()
    {
        (MainViewModel main, _, ScopeView view, Window window, AircraftModel ac) = await ClickFieldAsync(
            ScopeTemplates.StarsId,
            ScopeFieldSlot.Altitude,
            _ => { },
            _ => { }
        );
        try
        {
            Assert.Same(ac, main.SelectedAircraft);
            Assert.Equal(MenuCatalog.Get(MenuIds.AltitudeMaintain).Label, OpenEntry(view).Header);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramFieldAClick_TogglesTheDwellLock_AndOpensNoMenu()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window, AircraftModel ac) = await ClickFieldAsync(
            ScopeTemplates.EramId,
            ScopeFieldSlot.Callsign,
            _ => { },
            _ => { }
        );
        try
        {
            Assert.Contains("UAL238", scope.DataBlockState.DwellLockedCallsigns);
            Assert.Null(view.ActiveContextMenu);
            Assert.Same(ac, main.SelectedAircraft);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramAltitudeClick_OpensTheAltitudeEntry_AndTogglesNoDwellLock()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window, AircraftModel ac) = await ClickFieldAsync(
            ScopeTemplates.EramId,
            ScopeFieldSlot.EramAltitude,
            _ => { },
            _ => { }
        );
        try
        {
            Assert.Empty(scope.DataBlockState.DwellLockedCallsigns);
            Assert.Same(ac, main.SelectedAircraft);
            Assert.Equal(MenuCatalog.Get(MenuIds.AltitudeMaintain).Label, OpenEntry(view).Header);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task StarsCallsignClick_OnlySelects()
    {
        (MainViewModel main, _, ScopeView view, Window window, AircraftModel ac) = await ClickFieldAsync(
            ScopeTemplates.StarsId,
            ScopeFieldSlot.Callsign,
            _ => { },
            _ => { }
        );
        try
        {
            Assert.Same(ac, main.SelectedAircraft);
            Assert.Null(view.ActiveContextMenu);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task ArmedRings_FieldClick_PlacesTheRing_AndOpensNoMenu()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window, _) = await ClickFieldAsync(
            ScopeTemplates.StarsId,
            ScopeFieldSlot.Altitude,
            _ => { },
            s =>
            {
                s.Pins = s.Pins with { Rings = ScopeProfile.RingsJRing };
                s.Press(ScopeToolbarButtons.Find(ScopeButtonId.Tools)!);
                Assert.Equal(ScopeRingTool.JRing, s.ArmedRingTool);
            }
        );
        try
        {
            Assert.Equal(new ScopeTrackRings(3.0, null, null), scope.LocalRings["UAL238"]);
            Assert.Null(main.SelectedAircraft);
            Assert.Null(view.ActiveContextMenu);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EuroScopeAssignedHeadingClick_OpensTheHeadingRose()
    {
        (MainViewModel main, _, ScopeView view, Window window, AircraftModel ac) = await ClickFieldAsync(
            ScopeTemplates.EuroScopeId,
            ScopeFieldSlot.AssignedHeading,
            _ => { },
            _ => { }
        );
        try
        {
            Assert.Same(ac, main.SelectedAircraft);
            Assert.Equal(MenuCatalog.Get(MenuIds.HeadingFly).Label, OpenEntry(view).Header);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    /// <summary>Offline there is no room to take control in, so the take reports why on the status line, as the Radar's does.</summary>
    [AvaloniaFact]
    public async Task EuroScopeOwnerClick_TakesControl_AndOpensNoMenu()
    {
        (MainViewModel main, _, ScopeView view, Window window, AircraftModel ac) = await ClickFieldAsync(
            ScopeTemplates.EuroScopeId,
            ScopeFieldSlot.Owner,
            _ => { },
            _ => { }
        );
        try
        {
            Assert.Equal("Cannot take control — not in a multi-user room", main.StatusText);
            Assert.Same(ac, main.SelectedAircraft);
            Assert.Null(view.ActiveContextMenu);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // The acting position is Oakland Center sector 45; its callsign is what the handoff checks match a track's ends on.
    private const string Acting = "OAK_45_CTR";

    private static readonly ActivePositionDto Sector45 = new(
        Callsign: Acting,
        Name: "Sector 45",
        RadioName: null,
        Frequency: "135.100",
        FacilityId: "ZOA",
        FacilityName: "Oakland Center",
        PositionType: "Artcc",
        HandoffCode: "C45",
        EramSectorId: "45"
    );

    // Acts as sector 45, as ScopeViewModelViewerTests.ServerActivePosition_SetsTheViewer_AndRaisesItsChange sets it, and draws
    // others' tracks full, so line 3 shows on every block whoever owns it.
    private static void ActAsSector45(ScopeViewModel scope)
    {
        scope.Main.SetActivePositionFromServer(new PositionDisplayConfigDto([], [], [], "C45", Sector45, 3, NoStarsDisplay: true));
        scope.Pins = scope.Pins with { OthersLevel = ScopeProfile.OthersFull };
    }

    // ERAM line 3 shows FRZN for a frozen track with no handoff, and the handoff for one in progress, so the field is drawn.
    private static Action<AircraftModel> Line3(string? owner, string? handoffPeer) =>
        ac =>
        {
            ac.EramIsFrozen = true;
            ac.Owner = owner;
            ac.OwnerSectorCode = owner is null ? null : "C" + owner[4..6];
            ac.HandoffPeer = handoffPeer;
            ac.HandoffPeerSectorCode = handoffPeer is null ? null : "C" + handoffPeer[4..6];
        };

    private static async Task<(MainViewModel Main, ScopeView View, Window Window, AircraftModel Aircraft)> ClickEramLine3Async(
        string? owner,
        string? handoffPeer
    )
    {
        (MainViewModel main, _, ScopeView view, Window window, AircraftModel ac) = await ClickFieldAsync(
            ScopeTemplates.EramId,
            ScopeFieldSlot.StatusLine3,
            Line3(owner, handoffPeer),
            ActAsSector45
        );
        return (main, view, window, ac);
    }

    private static async Task AssertLine3OpensAsync(string? owner, string? handoffPeer, string expectedId)
    {
        (MainViewModel main, ScopeView view, Window window, AircraftModel ac) = await ClickEramLine3Async(owner, handoffPeer);
        try
        {
            Assert.Same(ac, main.SelectedAircraft);
            ContextMenu menu = Assert.IsType<ContextMenu>(view.ActiveContextMenu);
            MenuItem entry = Assert.IsType<MenuItem>(Assert.Single(menu.Items));
            Assert.Equal(MenuCatalog.Get(expectedId).Label, entry.Header);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    private static async Task AssertLine3OnlySelectsAsync(string? owner, string? handoffPeer)
    {
        (MainViewModel main, ScopeView view, Window window, AircraftModel ac) = await ClickEramLine3Async(owner, handoffPeer);
        try
        {
            Assert.Same(ac, main.SelectedAircraft);
            Assert.Null(view.ActiveContextMenu);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    /// <summary>Handoff field, row (a): owned by another sector, handing off to the acting position.</summary>
    [AvaloniaFact]
    public Task EramLine3Click_InboundHandoff_OpensAcceptHandoff() => AssertLine3OpensAsync("OAK_40_CTR", Acting, MenuIds.TrackAcceptHandoff);

    /// <summary>Handoff field, row (b): owned by the acting position, handing off to another sector.</summary>
    [AvaloniaFact]
    public Task EramLine3Click_OutboundHandoff_OpensCancelHandoff() => AssertLine3OpensAsync(Acting, "OAK_40_CTR", MenuIds.TrackCancelHandoff);

    /// <summary>Handoff field, row (c): owned, with no handoff.</summary>
    [AvaloniaFact]
    public Task EramLine3Click_OwnedWithNoHandoff_OpensInitiateHandoff() => AssertLine3OpensAsync(Acting, null, MenuIds.TrackInitiateHandoff);

    /// <summary>Handoff field, row (d): no owner and no handoff, so no handoff entry applies and the click only selects.</summary>
    [AvaloniaFact]
    public Task EramLine3Click_UnownedWithNoHandoff_OnlySelects() => AssertLine3OnlySelectsAsync(null, null);

    /// <summary>Handoff field, row (e): a handoff between two other sectors (C40 to C30), neither end the acting C45.</summary>
    [AvaloniaFact]
    public Task EramLine3Click_HandoffBetweenTwoOthers_OnlySelects() => AssertLine3OnlySelectsAsync("OAK_40_CTR", "OAK_30_CTR");

    private static AircraftModel Jet() =>
        new()
        {
            Callsign = "UAL238",
            AircraftType = "B738",
            FiledAircraftType = "B738",
            FlightRules = "IFR",
            Altitude = 11000,
            IndicatedAirspeed = 280,
            GroundSpeed = 300,
            Destination = "KSFO",
            Position = new LatLon(37.7, -122.2),
        };

    [AvaloniaFact]
    public void SpeedMenu_HoldsTheSpeedEntry_AndAChipSendsForTheAircraft()
    {
        var host = new RecordingMenuHost("");
        ContextMenu menu = Assert.IsType<ContextMenu>(ScopeFieldMenus.Build(ScopeFieldAction.OpenSpeedMenu, Jet(), host));
        MenuItem entry = Assert.IsType<MenuItem>(Assert.Single(menu.Items));
        Assert.Equal(MenuCatalog.Get(MenuIds.SpeedAssign).Label, entry.Header);
        var window = new Window { Width = 900, Height = 900 };
        try
        {
            window.Show();
            menu.Open(window);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(entry.IsSubMenuOpen, "the speed entry's submenu is not open");
            SpeedFlyoutPanel panel = Assert.IsType<SpeedFlyoutPanel>(
                Assert.IsType<SpeedFlyoutHost>(Assert.Single(entry.Items.OfType<MenuItem>()).Header).Child
            );

            panel.KnotChip(250).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            (string callsign, string command, _) = Assert.Single(host.Sent);
            Assert.Equal(("UAL238", "SPD 250"), (callsign, command));
        }
        finally
        {
            menu.Close();
            CloseWindow(window);
        }
    }

    // What an offline send leaves on the status line, so a send shows there and a stage does not.
    private const string NotConnected = "Command error: Not connected.";

    // A jet's speed, so the speed flyout offers the 250-knot chip.
    private static void Speedy(AircraftModel ac) => ac.IndicatedAirspeed = 280;

    private static Button SpeedChip(ScopeView view, int knots)
    {
        MenuItem entry = OpenEntry(view);
        Assert.Equal(MenuCatalog.Get(MenuIds.SpeedAssign).Label, entry.Header);
        SpeedFlyoutHost flyout = Assert.IsType<SpeedFlyoutHost>(Assert.Single(entry.Items.OfType<MenuItem>()).Header);
        return Assert.IsType<SpeedFlyoutPanel>(flyout.Child).KnotChip(knots);
    }

    // A click at the centre of a control inside a popup, through the popup's own top level, as a mouse gives it.
    private static void ClickInPopup(Control control, RawInputModifiers modifiers)
    {
        TopLevel top = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(control));
        Point? at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top);
        Assert.NotNull(at);
        top.MouseDown(at.Value, MouseButton.Left, modifiers);
        top.MouseUp(at.Value, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static Task<(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window, AircraftModel Aircraft)> OpenSpeedMenuAsync(
        Action<ScopeViewModel> beforeClick
    ) => ClickFieldAsync(ScopeTemplates.StarsId, ScopeFieldSlot.GroundSpeed, Speedy, beforeClick);

    /// <summary>Shift held on a row inside the speed flyout, a submenu popup: the row stages its command and sends nothing.</summary>
    [AvaloniaFact]
    public async Task ShiftClickOnASpeedChip_StagesThePilotEntry_AndSendsNothing()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window, _) = await OpenSpeedMenuAsync(_ => { });
        try
        {
            string statusBefore = main.StatusText;

            ClickInPopup(SpeedChip(view, 250), RawInputModifiers.Shift);

            Assert.Equal("PILOT UAL238 SPD 250", scope.EntryText);
            Assert.Equal(statusBefore, main.StatusText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task PlainClickOnASpeedChip_Sends_AndStagesNothing()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window, _) = await OpenSpeedMenuAsync(_ => { });
        try
        {
            ClickInPopup(SpeedChip(view, 250), RawInputModifiers.None);

            Assert.Equal(NotConnected, main.StatusText);
            Assert.Equal("", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task ShiftEnterOnAFocusedSpeedChip_StagesThePilotEntry()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window, _) = await OpenSpeedMenuAsync(_ => { });
        try
        {
            string statusBefore = main.StatusText;
            Button chip = SpeedChip(view, 250);
            Assert.True(chip.Focus());
            TopLevel top = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(chip));

            top.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Shift);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("PILOT UAL238 SPD 250", scope.EntryText);
            Assert.Equal(statusBefore, main.StatusText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    /// <summary>After a stage the menu is closed and the canvas has the focus, so Enter runs the entry without its PILOT word.</summary>
    [AvaloniaFact]
    public async Task AfterAStage_TheCanvasHasTheFocus_AndEnterRunsTheStagedEntry()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window, _) = await OpenSpeedMenuAsync(_ => { });
        try
        {
            ClickInPopup(SpeedChip(view, 250), RawInputModifiers.Shift);

            Assert.Null(view.ActiveContextMenu);
            Assert.True(view.Canvas.IsFocused, "the canvas does not have the focus");
            window.DispatchKey(Key.Enter);
            await scope.LastEntryRun;
            Assert.Equal("UAL238 SPD 250", scope.LastSentEntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task ShiftClick_IntoAPilotEntryForTheSameAircraft_Appends()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window, _) = await OpenSpeedMenuAsync(s => s.TypeEntryText("PILOT UAL238 CM 80"));
        try
        {
            ClickInPopup(SpeedChip(view, 250), RawInputModifiers.Shift);

            Assert.Equal("PILOT UAL238 CM 80, SPD 250", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    /// <summary>A menu-item row (not a flyout button) picked with Shift: the EuroScope runway field's 28L row stages.</summary>
    [AvaloniaFact]
    public async Task EuroScopeRunwayRow_ShiftClick_StagesTheRunway_AndSendsNothing()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window, _) = await ClickFieldAsync(
            ScopeTemplates.EuroScopeId,
            ScopeFieldSlot.AssignedRunway,
            ac =>
            {
                ac.Destination = "KSFO";
                ac.AssignedRunway = "28R";
            },
            _ => { }
        );
        try
        {
            string statusBefore = main.StatusText;
            MenuItem row = Assert.Single(OpenEntry(view).Items.OfType<MenuItem>(), i => (i.Header as string) == "28L");

            ClickInPopup(row, RawInputModifiers.Shift);

            Assert.Equal("PILOT UAL238 RWY 28L", scope.EntryText);
            Assert.Equal(statusBefore, main.StatusText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    /// <summary>A top-menu leaf picked with Shift: the handoff field's Accept handoff entry on an inbound handoff stages.</summary>
    [AvaloniaFact]
    public async Task HandoffAcceptEntry_ShiftClick_StagesTheAccept()
    {
        (MainViewModel main, ScopeView view, Window window, _) = await ClickEramLine3Async("OAK_40_CTR", Acting);
        try
        {
            string statusBefore = main.StatusText;
            ContextMenu menu = Assert.IsType<ContextMenu>(view.ActiveContextMenu);
            MenuItem entry = Assert.IsType<MenuItem>(Assert.Single(menu.Items));

            ClickInPopup(entry, RawInputModifiers.Shift);

            Assert.Equal("PILOT UAL238 ACCEPT", Assert.IsType<ScopeViewModel>(view.DataContext).EntryText);
            Assert.Equal(statusBefore, main.StatusText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    /// <summary>Shift pressed and released in the open menu with no row hit does not stick: the next plain click sends.</summary>
    [AvaloniaFact]
    public async Task ShiftPressedAndReleasedWithNoRowHit_ThenAPlainClick_Sends()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window, _) = await OpenSpeedMenuAsync(_ => { });
        try
        {
            Button chip = SpeedChip(view, 250);
            TopLevel top = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(chip));
            top.KeyPressQwerty(PhysicalKey.ShiftLeft, RawInputModifiers.Shift);
            top.KeyReleaseQwerty(PhysicalKey.ShiftLeft, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            ClickInPopup(chip, RawInputModifiers.None);

            Assert.Equal(NotConnected, main.StatusText);
            Assert.Equal("", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    /// <summary>A stage does not carry over to the next field menu: reopened, a plain click sends.</summary>
    [AvaloniaFact]
    public async Task AfterAStage_TheReopenedMenu_SendsOnAPlainClick()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window, _) = await OpenSpeedMenuAsync(_ => { });
        try
        {
            ClickInPopup(SpeedChip(view, 250), RawInputModifiers.Shift);
            Assert.Equal("PILOT UAL238 SPD 250", scope.EntryText);
            Assert.Null(view.ActiveContextMenu);

            ClickAt(window, view.Canvas, Centre(view.Canvas, ScopeFieldSlot.GroundSpeed));
            ClickInPopup(SpeedChip(view, 250), RawInputModifiers.None);

            Assert.Equal(NotConnected, main.StatusText);
            Assert.Equal("PILOT UAL238 SPD 250", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // The submenus down to the first enabled row that sends a command (other than a delete), that row last; null when none.
    private static List<MenuItem>? PathToSendingRow(ItemCollection items)
    {
        foreach (MenuItem item in items.OfType<MenuItem>().Where(i => i.IsEnabled))
        {
            if ((item.ItemCount == 0) && (MenuCommandText.GetCommand(item) is { } command) && !command.StartsWith("DEL", StringComparison.Ordinal))
            {
                return [item];
            }

            if ((item.ItemCount > 0) && (PathToSendingRow(item.Items) is { } path))
            {
                path.Insert(0, item);
                return path;
            }
        }

        return null;
    }

    /// <summary>The right-click aircraft menu's host stages nothing, so a row picked with Shift held still sends.</summary>
    [AvaloniaFact]
    public async Task RightClickMenu_ShiftClickOnARow_StillSends()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScope(ScopeTemplates.StarsId);
        try
        {
            await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            AddAt(main, view.Canvas, Speedy);
            Point? target = view.Canvas.TranslatePoint(new Point(300, 300), window);
            Assert.NotNull(target);
            window.MouseDown(target.Value, MouseButton.Right);
            window.MouseUp(target.Value, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            ContextMenu menu = Assert.IsType<ContextMenu>(view.ActiveContextMenu);
            List<MenuItem> path = Assert.IsType<List<MenuItem>>(PathToSendingRow(menu.Items));
            foreach (MenuItem parent in path[..^1])
            {
                parent.IsSubMenuOpen = true;
                Dispatcher.UIThread.RunJobs();
            }

            ClickInPopup(path[^1], RawInputModifiers.Shift);

            Assert.Equal(NotConnected, main.StatusText);
            Assert.Equal("", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public void RunwayMenu_ListsTheDestinationsRunways_AndARowSendsForTheAircraft()
    {
        var host = new RecordingMenuHost("");
        ContextMenu menu = Assert.IsType<ContextMenu>(ScopeFieldMenus.Build(ScopeFieldAction.OpenRunwayMenu, Jet(), host));
        MenuItem entry = Assert.IsType<MenuItem>(Assert.Single(menu.Items));
        MenuItem row = Assert.Single(entry.Items.OfType<MenuItem>(), i => (i.Header as string) == "28R");

        row.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        (string callsign, string command, _) = Assert.Single(host.Sent);
        Assert.Equal(("UAL238", "RWY 28R"), (callsign, command));
    }
}
