using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.EuroScopeFlyouts;
using Yaat.Client.Views.Scope.FieldMenus;
using Yaat.Client.Views.Scope.Prefs;
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
    private static Task<(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window)> ShowScope(string templateId) =>
        ShowScope(templateId, 800, 600);

    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window)> ShowScope(
        string templateId,
        double width,
        double height
    )
    {
        MainViewModel main = await MainAsync();
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot) { TemplateId = templateId };
        main.OpenScope = scope;
        var view = new ScopeView { DataContext = scope };
        var window = new Window
        {
            Width = width,
            Height = height,
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

    private static AircraftModel AddAt(MainViewModel main, ScopeCanvas canvas, Action<AircraftModel> setUp) =>
        AddAt(main, canvas, setUp, new Point(300, 300));

    private static AircraftModel AddAt(MainViewModel main, ScopeCanvas canvas, Action<AircraftModel> setUp, Point target)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)target.X, (float)target.Y);
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
        view.ActiveFieldPopup?.Close();
        view.ActiveEntryPopup?.Close();
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
            List<string> texts = PopupTexts(OpenPopup(view));
            Assert.Contains(ScopeDataAltitudePopup.ScopeDataHead, texts);
            Assert.Contains(ScopeDataAltitudePopup.PilotHead, texts);
            Assert.Null(view.ActiveContextMenu);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // The ERAM altitude popup: SCOPE DATA rows run their entry, or with Shift stage it; PILOT rows send, or with Shift stage.

    private static ScopeDataAltitudePopup OpenPopup(ScopeView view)
    {
        ScopeDataAltitudePopup popup = Assert.IsType<ScopeDataAltitudePopup>(view.ActiveFieldPopup);
        Assert.True(popup.IsOpen);
        return popup;
    }

    private static List<string> PopupTexts(ScopeDataAltitudePopup popup) =>
        [.. Assert.IsAssignableFrom<Control>(popup.Popup.Child).GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")];

    private static List<ScopeEramEntryDto> CaptureEram(ScopeViewModel scope)
    {
        var sends = new List<ScopeEramEntryDto>();
        scope.EramEntrySender = dto =>
        {
            sends.Add(dto);
            return Task.FromResult(new ScopeEramEntryResultDto(true, ["ACCEPT"], null));
        };
        return sends;
    }

    private static Task<(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window, AircraftModel Aircraft)> OpenEramAltitudeAsync(
        Action<ScopeViewModel> beforeClick
    ) => ClickFieldAsync(ScopeTemplates.EramId, ScopeFieldSlot.EramAltitude, _ => { }, beforeClick);

    private static readonly string[] AltitudeRows = ["FP altitude", "Interim", "Local interim", "Procedure", "Interim + CERA", "CERA only"];

    private static readonly string[] ClearRows = ["Clear interim / procedure", "Clear local", "Clear CERA"];

    [AvaloniaFact]
    public async Task EramAltitudePopup_TypedAltitude_PlainClickOnInterim_RunsTheEntry_AndClosesThePopup()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window, _) = await OpenEramAltitudeAsync(_ => { });
        try
        {
            List<ScopeEramEntryDto> sends = CaptureEram(scope);
            ScopeDataAltitudePopup popup = OpenPopup(view);
            popup.AltitudeBox.Text = "110";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("QQ 110 UAL238", popup.EntryShownFor("Interim"));

            ClickInPopup(popup.RowFor("Interim"), RawInputModifiers.None);
            await scope.LastEntryRun;

            ScopeEramEntryDto sent = Assert.Single(sends);
            Assert.Equal(["QQ", "110", "UAL238"], sent.Tokens);
            Assert.Equal([new ScopeEramPickDto(2, "UAL238")], sent.Picks);
            ScopeEntryResponse response = Assert.IsType<ScopeEntryResponse>(scope.EntryResponse);
            Assert.Equal("ACCEPT", response.Message);
            Assert.Equal("QQ 110 UAL238", response.Echo);
            Assert.Equal("", scope.EntryText);
            Assert.False(popup.IsOpen);
            Assert.Null(view.ActiveFieldPopup);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramAltitudePopup_TypedAltitude_ShiftClickOnInterim_StagesTheBareEntry_AndSendsNothing()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window, _) = await OpenEramAltitudeAsync(_ => { });
        try
        {
            List<ScopeEramEntryDto> sends = CaptureEram(scope);
            ScopeDataAltitudePopup popup = OpenPopup(view);
            popup.AltitudeBox.Text = "110";
            Dispatcher.UIThread.RunJobs();

            ClickInPopup(popup.RowFor("Interim"), RawInputModifiers.Shift);

            Assert.Equal("QQ 110 UAL238", scope.EntryText);
            Assert.Empty(sends);
            Assert.False(popup.IsOpen);
            Assert.Null(view.ActiveFieldPopup);
            Assert.True(view.Canvas.IsFocused, "the canvas does not have the focus");
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramAltitudePopup_EmptyBox_DisablesTheAltitudeRows_AndClearCeraRuns()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window, _) = await OpenEramAltitudeAsync(_ => { });
        try
        {
            List<ScopeEramEntryDto> sends = CaptureEram(scope);
            ScopeDataAltitudePopup popup = OpenPopup(view);

            Assert.All(AltitudeRows, label => Assert.False(popup.RowFor(label).IsEnabled, label));
            Assert.All(ClearRows, label => Assert.True(popup.RowFor(label).IsEnabled, label));
            Assert.Equal("QQ ··· UAL238", popup.EntryShownFor("Interim"));
            Assert.Equal("QQ L··· UAL238", popup.EntryShownFor("Local interim"));

            ClickInPopup(popup.RowFor("Clear CERA"), RawInputModifiers.None);
            await scope.LastEntryRun;

            Assert.Equal(["QR", "000", "UAL238"], Assert.Single(sends).Tokens);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // The PILOT row next above the aircraft's 5000 ft, in view as the list opens centred on 5000 ft.
    private static Button PilotRowAbove(ScopeDataAltitudePopup popup)
    {
        MaintainAltitudePanel panel = Assert.IsType<MaintainAltitudePanel>(popup.PilotPanel);
        return panel.RowFor(panel.Altitudes.Where(a => a > 5000).Min());
    }

    [AvaloniaFact]
    public async Task EramAltitudePopup_PlainClickOnAPilotRow_Sends_AndStagesNothing()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window, _) = await OpenEramAltitudeAsync(_ => { });
        try
        {
            ScopeDataAltitudePopup popup = OpenPopup(view);

            ClickInPopup(PilotRowAbove(popup), RawInputModifiers.None);

            Assert.Equal(NotConnected, main.StatusText);
            Assert.Equal("", scope.EntryText);
            Assert.Null(view.ActiveFieldPopup);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramAltitudePopup_ShiftClickOnAPilotRow_StagesThePilotEntry_AndSendsNothing()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window, _) = await OpenEramAltitudeAsync(_ => { });
        try
        {
            string statusBefore = main.StatusText;
            ScopeDataAltitudePopup popup = OpenPopup(view);

            ClickInPopup(PilotRowAbove(popup), RawInputModifiers.Shift);

            Assert.Matches(@"^\$ UAL238 CM \d+$", scope.EntryText);
            Assert.Equal(statusBefore, main.StatusText);
            Assert.Null(view.ActiveFieldPopup);
            Assert.True(view.Canvas.IsFocused, "the canvas does not have the focus");
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramAltitudeClick_WithRingsArmed_PlacesTheRing_AndOpensNoPopup()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window, _) = await OpenEramAltitudeAsync(s =>
        {
            s.Pins = s.Pins with { Rings = ScopeProfile.RingsJRing };
            s.Press(ScopeToolbarButtons.Find(ScopeButtonId.Tools)!);
            Assert.Equal(ScopeRingTool.JRing, s.ArmedRingTool);
        });
        try
        {
            Assert.True(scope.LocalRings.ContainsKey("UAL238"));
            Assert.Null(main.SelectedAircraft);
            Assert.Null(view.ActiveFieldPopup);
            Assert.Null(view.ActiveContextMenu);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramAltitudePopup_OpeningAContextMenu_ClosesIt()
    {
        (_, _, ScopeView view, Window window, _) = await ClickFieldAsync(ScopeTemplates.EramId, ScopeFieldSlot.EramAltitude, Speedy, _ => { });
        try
        {
            ScopeDataAltitudePopup popup = OpenPopup(view);

            // No pointer press, so no light dismiss: only the one-surface rule closes the popup.
            view.OnFieldClicked("UAL238", ScopeFieldAction.OpenSpeedMenu, default, KeyModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.False(popup.IsOpen);
            Assert.Null(view.ActiveFieldPopup);
            Assert.NotNull(view.ActiveContextMenu);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task OpeningTheEramAltitudePopup_ClosesAnOpenContextMenu()
    {
        (_, _, ScopeView view, Window window, _) = await ClickFieldAsync(ScopeTemplates.EramId, ScopeFieldSlot.Ident, Identing, _ => { });
        try
        {
            ContextMenu menu = Assert.IsType<ContextMenu>(view.ActiveContextMenu);

            view.OnFieldClicked("UAL238", ScopeFieldAction.OpenEramAltitudeMenu, default, KeyModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.False(menu.IsOpen);
            Assert.Null(view.ActiveContextMenu);
            Assert.True(OpenPopup(view).IsOpen);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EscInsideTheEramAltitudePopup_WithTextInThePreview_ClosesIt_AndKeepsTheText()
    {
        // The pilot entry names UAL238 first, so the altitude click opens the popup rather than filling the track.
        (_, ScopeViewModel scope, ScopeView view, Window window, _) = await OpenEramAltitudeAsync(s => s.TypeEntryText("$ UAL238 FH 270"));
        try
        {
            ScopeDataAltitudePopup popup = OpenPopup(view);
            Assert.True(popup.AltitudeBox.Focus());
            TopLevel top = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(popup.AltitudeBox));

            top.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.False(popup.IsOpen);
            Assert.Null(view.ActiveFieldPopup);
            Assert.Equal("$ UAL238 FH 270", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    /// <summary>As the catalog's flyout opens, the PILOT list opens scrolled to the aircraft's altitude, near its foot here.</summary>
    [AvaloniaFact]
    public async Task EramAltitudePopup_OpensWithThePilotListCentredOnTheAircraftsAltitude()
    {
        (_, _, ScopeView view, Window window, _) = await OpenEramAltitudeAsync(_ => { });
        try
        {
            MaintainAltitudePanel panel = Assert.IsType<MaintainAltitudePanel>(OpenPopup(view).PilotPanel);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.True(panel.Scroll.Offset.Y > 0, $"the list is not scrolled to 5000 ft (offset {panel.Scroll.Offset.Y})");
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EscInsideTheEramAltitudePopup_ClosesIt()
    {
        (_, _, ScopeView view, Window window, _) = await OpenEramAltitudeAsync(_ => { });
        try
        {
            ScopeDataAltitudePopup popup = OpenPopup(view);
            Assert.True(popup.AltitudeBox.Focus());
            TopLevel top = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(popup.AltitudeBox));

            top.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.False(popup.IsOpen);
            Assert.Null(view.ActiveFieldPopup);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // Shows the ERAM scope with UAL238 at (300, 300), runs beforeClick, and Shift+left-clicks the centre of its altitude.
    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window)> ShiftClickEramAltitudeAsync(
        Action<ScopeViewModel> beforeClick
    )
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScope(ScopeTemplates.EramId);
        await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        beforeClick(scope);
        Dispatcher.UIThread.RunJobs();
        AddAt(main, view.Canvas, _ => { });
        main.SelectedAircraft = null;
        Dispatcher.UIThread.RunJobs();
        Point? inWindow = view.Canvas.TranslatePoint(Centre(view.Canvas, ScopeFieldSlot.EramAltitude), window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, MouseButton.Left, RawInputModifiers.Shift);
        window.MouseUp(inWindow.Value, MouseButton.Left, RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        return (main, scope, view, window);
    }

    [AvaloniaFact]
    public async Task EramAltitudeShiftClick_OpensThePopup_AsAPlainClick()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShiftClickEramAltitudeAsync(_ => { });
        try
        {
            Assert.True(OpenPopup(view).IsOpen);
            Assert.Null(view.ActiveContextMenu);
            Assert.Equal("UAL238", main.SelectedAircraft?.Callsign);
            Assert.Equal("", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramAltitudeShiftClick_WithRingsArmed_PlacesTheRing_AndOpensNothing()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShiftClickEramAltitudeAsync(s =>
        {
            s.Pins = s.Pins with { Rings = ScopeProfile.RingsJRing };
            s.Press(ScopeToolbarButtons.Find(ScopeButtonId.Tools)!);
            Assert.Equal(ScopeRingTool.JRing, s.ArmedRingTool);
        });
        try
        {
            Assert.True(scope.LocalRings.ContainsKey("UAL238"));
            Assert.Null(main.SelectedAircraft);
            Assert.Null(view.ActiveFieldPopup);
            Assert.Null(view.ActiveContextMenu);
            Assert.Equal("", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public void EramAltitudePopup_APilotRow_SendsItsCommandForTheAircraft()
    {
        var host = new RecordingMenuHost("");
        var popup = new ScopeDataAltitudePopup(Jet(), host, _ => { }, _ => { });
        MaintainAltitudePanel panel = Assert.IsType<MaintainAltitudePanel>(popup.PilotPanel);
        int altitude = panel.Altitudes.Where(a => a > 11000).Min();

        panel.RowFor(altitude).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        (string callsign, string command, _) = Assert.Single(host.Sent);
        Assert.Equal(("UAL238", $"CM {altitude}"), (callsign, command));
    }

    // The ERAM fourth-line popups: a SCOPE DATA section per HSF value beside the PILOT panel that matches it.

    private static void WithHsf(AircraftModel ac)
    {
        ac.EramAssignedHeading = "H270";
        ac.EramAssignedSpeed = "S250";
    }

    private static void WithFreeText(AircraftModel ac) => ac.EramFreeText = "LOWALT";

    // An identing jet, so the ERAM ident field draws and its click opens the squawk menu.
    private static void Identing(AircraftModel ac)
    {
        Speedy(ac);
        ac.IsIdenting = true;
    }

    // A canvas left click on a fourth-line token, or on Field E, opens the popup its action names.
    [AvaloniaTheory]
    [InlineData(ScopeFieldSlot.Line4Heading, nameof(HsfPopupKind.Heading))]
    [InlineData(ScopeFieldSlot.Line4Speed, nameof(HsfPopupKind.Speed))]
    [InlineData(ScopeFieldSlot.GroundSpeed, nameof(HsfPopupKind.Speed))]
    public async Task EramTokenClick_OpensItsPopup(ScopeFieldSlot slot, string kindName)
    {
        HsfPopupKind kind = Enum.Parse<HsfPopupKind>(kindName);
        (_, _, ScopeView view, Window window, _) = await ClickFieldAsync(ScopeTemplates.EramId, slot, WithHsf, _ => { });
        try
        {
            ScopeDataHsfPopup popup = OpenHsf(view);
            Assert.Equal(kind, popup.Kind);
            Assert.Null(view.ActiveContextMenu);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramFreeTextClick_OpensTheFreeTextPopup()
    {
        (_, _, ScopeView view, Window window, _) = await ClickFieldAsync(ScopeTemplates.EramId, ScopeFieldSlot.Line4FreeText, WithFreeText, _ => { });
        try
        {
            Assert.Equal(HsfPopupKind.FreeText, OpenHsf(view).Kind);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramLine4FallbackClick_OpensTheCombinedPopup()
    {
        (_, _, ScopeView view, Window window, _) = await ClickFieldAsync(ScopeTemplates.EramId, ScopeFieldSlot.Line4Notes, _ => { }, _ => { });
        try
        {
            ScopeDataHsfPopup popup = OpenHsf(view);
            Assert.Equal(HsfPopupKind.Combined, popup.Kind);
            Assert.Equal("", popup.HeadingBox?.Text ?? "");
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task StarsGroundSpeedClick_StillOpensTheSpeedMenu()
    {
        (_, _, ScopeView view, Window window, _) = await OpenSpeedMenuAsync(_ => { });
        try
        {
            Assert.Equal(MenuCatalog.Get(MenuIds.SpeedAssign).Label, OpenEntry(view).Header);
            Assert.Null(view.ActiveFieldPopup);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public void EramHsfPopup_ADisabledClearRow_ReadsNoneSet_AndAnEnabledOneKeepsItsNote()
    {
        ScopeDataHsfPopup empty = NewHsf(HsfPopupKind.Combined, _ => { });
        Assert.Equal(ScopeDataPopupBase.NoneSet, empty.NoteShownFor("Clear heading"));
        Assert.Equal(ScopeDataPopupBase.NoneSet, empty.NoteShownFor("Clear speed"));
        Assert.Equal("", empty.NoteShownFor("Set heading"));

        Assert.Equal("", NewHsf(HsfPopupKind.Combined, WithHsf).NoteShownFor("Clear heading"));
        Assert.Equal("heading, speed, text", NewHsf(HsfPopupKind.FreeText, WithFreeText).NoteShownFor("Clear all HSF"));
        Assert.Equal(ScopeDataPopupBase.NoneSet, NewHsf(HsfPopupKind.FreeText, _ => { }).NoteShownFor("Clear all HSF"));
    }

    // Opens the popup over a bare window on the host, as ScopeView would over its canvas.
    private static Window OpenInWindow(ScopeDataPopupBase popup) => OpenInWindow(popup, 600);

    private static Window OpenInWindow(ScopeDataPopupBase popup, double height)
    {
        var anchor = new Border();
        var window = new Window
        {
            Width = 800,
            Height = height,
            Content = anchor,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(popup.Open(anchor, null));
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Tick(RecordingMenuHost host)
    {
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void EramFreeTextPopup_ClearAllHsf_FollowsTheStoredText_OnTheClock_AndCloseStopsTheTick()
    {
        var host = new RecordingMenuHost("");
        AircraftModel ac = Jet();
        var popup = new ScopeDataHsfPopup(ac, host, _ => { }, _ => { }, HsfPopupKind.FreeText);
        Window window = OpenInWindow(popup);
        try
        {
            Assert.False(popup.RowFor("Clear all HSF").IsEnabled);

            ac.EramFreeText = "LOWALT";
            Tick(host);

            Assert.True(popup.RowFor("Clear all HSF").IsEnabled);
            Assert.Equal(1, host.Clock.LiveTimerCount);
            popup.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, host.Clock.LiveTimerCount);
        }
        finally
        {
            popup.Close();
            CloseWindow(window);
        }
    }

    [AvaloniaFact]
    public void EramHeadingPopup_AHeadingStoredWhileOpen_ShowsInTheCaption_AndEnablesClearHeading_OnTheClock()
    {
        var host = new RecordingMenuHost("");
        AircraftModel ac = Jet();
        var popup = new ScopeDataHsfPopup(ac, host, _ => { }, _ => { }, HsfPopupKind.Heading);
        Window window = OpenInWindow(popup);
        try
        {
            Assert.Contains("none", HsfTexts(popup));

            ac.EramAssignedHeading = "H270";
            Tick(host);

            Assert.Contains("now H270", HsfTexts(popup));
            Assert.True(popup.RowFor("Clear heading").IsEnabled);
        }
        finally
        {
            popup.Close();
            CloseWindow(window);
        }
    }

    [AvaloniaFact]
    public void EramSpeedPopup_APilotAssignSpeedChoice_SendsItsCommandForTheAircraft_AndClosesThePopup()
    {
        var host = new RecordingMenuHost("");
        var popup = new ScopeDataHsfPopup(Jet(), host, _ => { }, _ => { }, HsfPopupKind.Speed);
        Window window = OpenInWindow(popup);
        try
        {
            SpeedFlyoutPanel panel = Assert.IsType<SpeedFlyoutPanel>(popup.SpeedPanel);

            panel.KnotChip(250).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            (string callsign, string command, _) = Assert.Single(host.Sent);
            Assert.Equal(("UAL238", "SPD 250"), (callsign, command));
            Assert.False(popup.IsOpen);
        }
        finally
        {
            popup.Close();
            CloseWindow(window);
        }
    }

    // The popup's first box opens focused, its stored value selected so typing replaces it.
    private static async Task AssertOpensFocusedAndSelectedAsync(ScopeFieldAction action, Func<ScopeDataHsfPopup, TextBox?> boxOf, string stored)
    {
        (_, ScopeView view, Window window) = await OpenHsfAsync(
            action,
            ac =>
            {
                WithHsf(ac);
                WithFreeText(ac);
            }
        );
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            TextBox box = Assert.IsType<TextBox>(boxOf(OpenHsf(view)));

            Assert.True(box.IsFocused, "the box does not have the focus");
            Assert.Equal(stored, box.SelectedText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public Task EramHeadingPopup_OpensWithTheHeadingBoxFocused_AndItsValueSelected() =>
        AssertOpensFocusedAndSelectedAsync(ScopeFieldAction.OpenEramHeadingMenu, p => p.HeadingBox, "H270");

    [AvaloniaFact]
    public Task EramSpeedPopup_OpensWithTheSpeedBoxFocused_AndItsValueSelected() =>
        AssertOpensFocusedAndSelectedAsync(ScopeFieldAction.OpenEramSpeedMenu, p => p.SpeedBox, "S250");

    [AvaloniaFact]
    public Task EramFreeTextPopup_OpensWithTheFreeTextBoxFocused_AndItsValueSelected() =>
        AssertOpensFocusedAndSelectedAsync(ScopeFieldAction.OpenEramFreeTextMenu, p => p.FreeTextBox, "LOWALT");

    [AvaloniaFact]
    public async Task EramSpeedPopup_ASpeedTypedWithItsSlash_RunsOneSlash()
    {
        (ScopeViewModel scope, ScopeView view, Window window) = await OpenHsfAsync(ScopeFieldAction.OpenEramSpeedMenu, WithHsf);
        try
        {
            List<ScopeEramEntryDto> sends = CaptureEram(scope);
            ScopeDataHsfPopup popup = OpenHsf(view);
            Assert.IsType<TextBox>(popup.SpeedBox).Text = "/250-";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("QS /250- UAL238", popup.EntryShownFor("Set speed"));

            ClickInPopup(popup.RowFor("Set speed"), RawInputModifiers.None);
            await scope.LastEntryRun;

            Assert.Equal("QS /250- UAL238", string.Join(' ', Assert.Single(sends).Tokens));
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public void EramHsfPopup_ClearHeading_FollowsTheStoredHeading_OnRefresh()
    {
        AircraftModel ac = Jet();
        var popup = new ScopeDataHsfPopup(ac, new RecordingMenuHost(""), _ => { }, _ => { }, HsfPopupKind.Combined);
        Assert.False(popup.RowFor("Clear heading").IsEnabled);

        ac.EramAssignedHeading = "H270";
        popup.Refresh();

        Assert.True(popup.RowFor("Clear heading").IsEnabled);
        ac.EramAssignedHeading = null;
        popup.Refresh();
        Assert.False(popup.RowFor("Clear heading").IsEnabled);
    }

    // Shows the ERAM scope with UAL238 at (300, 300) as setUp leaves it, and clicks a field of the action with no pointer press.
    private static async Task<(ScopeViewModel Scope, ScopeView View, Window Window)> OpenHsfAsync(
        ScopeFieldAction action,
        Action<AircraftModel> setUp
    )
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScope(ScopeTemplates.EramId);
        await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        AddAt(main, view.Canvas, setUp);
        view.OnFieldClicked("UAL238", action, default, KeyModifiers.None);
        Dispatcher.UIThread.RunJobs();
        return (scope, view, window);
    }

    private static ScopeDataHsfPopup OpenHsf(ScopeView view)
    {
        ScopeDataHsfPopup popup = Assert.IsType<ScopeDataHsfPopup>(view.ActiveFieldPopup);
        Assert.True(popup.IsOpen);
        return popup;
    }

    private static List<string> HsfTexts(ScopeDataHsfPopup popup) =>
        [.. Assert.IsAssignableFrom<Control>(popup.Popup.Child).GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")];

    private static ScopeDataHsfPopup NewHsf(HsfPopupKind kind, Action<AircraftModel> setUp)
    {
        AircraftModel ac = Jet();
        setUp(ac);
        return new ScopeDataHsfPopup(ac, new RecordingMenuHost(""), _ => { }, _ => { }, kind);
    }

    // The popups against their mocks: the rows hint, the captions, a disabled row's colours, the notes' column, the altitude
    // title note, the PILOT column's width, the Combined popup's height, the entries' ERAM marks, and where the popup opens.

    private static readonly Color NormalLabel = Color.Parse("#E8E8E8");

    private static readonly Color Faded = Color.Parse("#5D636B");

    private static Color ColorOf(TextBlock text) => Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color;

    private static void AssertRowsHintBeside(TextBox? box)
    {
        Panel row = Assert.IsAssignableFrom<Panel>(Assert.IsType<TextBox>(box).Parent);
        TextBlock hint = Assert.Single(row.Children.OfType<TextBlock>(), t => t.Text == ScopeDataPopupBase.RowsHint);
        Assert.Equal(11, hint.FontSize);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, hint.HorizontalAlignment);
        Assert.Equal(Faded, ColorOf(hint));
    }

    [AvaloniaFact]
    public void EramPopups_EveryBoxRow_HoldsTheFadedRowsHint()
    {
        ScopeDataHsfPopup combined = NewHsf(HsfPopupKind.Combined, _ => { });
        AssertRowsHintBeside(combined.HeadingBox);
        AssertRowsHintBeside(combined.SpeedBox);
        AssertRowsHintBeside(NewHsf(HsfPopupKind.FreeText, _ => { }).FreeTextBox);
        AssertRowsHintBeside(new ScopeDataAltitudePopup(Jet(), new RecordingMenuHost(""), _ => { }, _ => { }).AltitudeBox);
    }

    private static void AssertBoldCaption(ScopeDataHsfPopup popup, string caption)
    {
        Control content = Assert.IsAssignableFrom<Control>(popup.Popup.Child);
        TextBlock text = Assert.Single(content.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == caption);
        Assert.Equal(FontWeight.Bold, text.FontWeight);
    }

    [AvaloniaFact]
    public void EramHsfPopup_SectionCaptions_ReadInBoldCapitals()
    {
        ScopeDataHsfPopup combined = NewHsf(HsfPopupKind.Combined, _ => { });
        AssertBoldCaption(combined, "HEADING");
        AssertBoldCaption(combined, "SPEED");
        AssertBoldCaption(NewHsf(HsfPopupKind.FreeText, _ => { }), "FREE TEXT");
    }

    [AvaloniaFact]
    public void EramHsfPopup_ADisabledSetRow_KeepsItsLabel_AndFadesItsEntryAndNote()
    {
        ScopeDataHsfPopup popup = NewHsf(HsfPopupKind.Combined, _ => { });
        Button row = popup.RowFor("Set heading");
        Assert.False(row.IsEnabled);

        (TextBlock label, TextBlock entry, TextBlock note) = popup.RowPartsFor("Set heading");

        Assert.Equal(1, row.Opacity);
        Assert.Equal(NormalLabel, ColorOf(label));
        Assert.Equal(Faded, ColorOf(entry));
        Assert.Equal(Faded, ColorOf(note));
    }

    // Mock B4: a disabled clear row fades whole, its label, note and entry in their own colours at 40% opacity.
    [AvaloniaFact]
    public void EramHsfPopup_ADisabledClearRow_FadesWhole()
    {
        ScopeDataHsfPopup popup = NewHsf(HsfPopupKind.Combined, _ => { });
        Button row = popup.RowFor("Clear heading");
        Assert.False(row.IsEnabled);

        (TextBlock label, TextBlock entry, TextBlock note) = popup.RowPartsFor("Clear heading");

        Assert.Equal(0.4, row.Opacity, 3);
        Assert.Equal(NormalLabel, ColorOf(label));
        Assert.Equal(Color.Parse("#9AA3AD"), ColorOf(entry));
        Assert.Equal(FlyoutPanelParts.TitleBrush.Color, ColorOf(note));
        Assert.Equal(1, NewHsf(HsfPopupKind.Combined, WithHsf).RowFor("Clear heading").Opacity);
    }

    // The dashed outline over a row's content, which only the hovered enabled row shows.
    private static Rectangle OutlineOf(Button row) => Assert.Single(row.GetLogicalDescendants().OfType<Rectangle>());

    // Moves the pointer onto the centre of a control in an open popup.
    private static void HoverOver(Control control)
    {
        TopLevel top = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(control));
        Point? at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top);
        Assert.NotNull(at);
        top.MouseMove(at.Value);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void EramPopup_AHoveredDisabledRow_ShowsNoOutline()
    {
        ScopeDataHsfPopup popup = NewHsf(HsfPopupKind.Combined, _ => { });
        Window window = OpenInWindow(popup);
        try
        {
            Button row = popup.RowFor("Clear heading");
            Assert.False(row.IsEnabled);

            HoverOver(row);

            Assert.False(OutlineOf(row).IsVisible, "the hovered disabled row shows the outline");
        }
        finally
        {
            popup.Close();
            Dispatcher.UIThread.RunJobs();
            CloseWindow(window);
        }
    }

    [AvaloniaFact]
    public void EramPopup_AHoveredEnabledRow_ShowsADashedOutline()
    {
        ScopeDataHsfPopup popup = NewHsf(HsfPopupKind.Combined, WithHsf);
        Window window = OpenInWindow(popup);
        try
        {
            Button row = popup.RowFor("Clear heading");
            Assert.True(row.IsEnabled);
            Rectangle outline = OutlineOf(row);
            Assert.False(outline.IsVisible, "the outline shows before the pointer reaches the row");

            HoverOver(row);

            Assert.True(row.IsPointerOver, "the row is not under the pointer");
            Assert.True(outline.IsVisible, "the hovered row shows no outline");
            Assert.Equal(Color.Parse("#8FC1FF"), Assert.IsAssignableFrom<ISolidColorBrush>(outline.Stroke).Color);
            Assert.Equal(1, outline.StrokeThickness);
            Assert.NotEmpty(Assert.IsAssignableFrom<IEnumerable<double>>(outline.StrokeDashArray));
        }
        finally
        {
            popup.Close();
            Dispatcher.UIThread.RunJobs();
            CloseWindow(window);
        }
    }

    private static Border BorderOf(TextBox box) => box.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");

    private static Color BrushColor(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    [AvaloniaFact]
    public void EramPopup_TheBox_HasTheMockBorder_AndABlueBorderWhenFocused()
    {
        ScopeDataHsfPopup popup = NewHsf(HsfPopupKind.Combined, WithHsf);
        Window window = OpenInWindow(popup);
        try
        {
            TextBox focused = Assert.IsType<TextBox>(popup.HeadingBox);
            TextBox other = Assert.IsType<TextBox>(popup.SpeedBox);
            Assert.True(focused.IsFocused, "the heading box has no focus");
            Assert.False(other.IsFocused);

            Border plain = BorderOf(other);
            Assert.Equal(Color.Parse("#5A5A5A"), BrushColor(plain.BorderBrush));
            Assert.Equal(new Thickness(1), plain.BorderThickness);
            Assert.Equal(new CornerRadius(4), plain.CornerRadius);
            Assert.Equal(Color.Parse("#1E1E1E"), BrushColor(plain.Background));

            HoverOver(other);
            Assert.True(other.IsPointerOver, "the speed box is not under the pointer");
            Border hovered = BorderOf(other);
            Assert.Equal(Color.Parse("#5A5A5A"), BrushColor(hovered.BorderBrush));
            Assert.Equal(new Thickness(1), hovered.BorderThickness);
            Assert.Equal(Color.Parse("#1E1E1E"), BrushColor(hovered.Background));

            Border lit = BorderOf(focused);
            Assert.Equal(Color.Parse("#4A9EFF"), BrushColor(lit.BorderBrush));
            Assert.Equal(new Thickness(2), lit.BorderThickness);
            Assert.Equal(new CornerRadius(4), lit.CornerRadius);
            Assert.Equal(Color.Parse("#1E1E1E"), BrushColor(lit.Background));
        }
        finally
        {
            popup.Close();
            Dispatcher.UIThread.RunJobs();
            CloseWindow(window);
        }
    }

    [AvaloniaFact]
    public void EramHsfPopup_TheChip_TheSectionRule_AndTheCaptions_MatchTheMock()
    {
        ScopeDataHsfPopup popup = NewHsf(HsfPopupKind.Combined, _ => { });
        Control content = Assert.IsAssignableFrom<Control>(popup.Popup.Child);
        List<Border> borders = [.. content.GetLogicalDescendants().OfType<Border>()];

        Border chip = borders.First(b => b.Child is TextBlock { Text: "ERAM" });
        Assert.Equal(40, chip.Width);
        Assert.Equal(18, chip.Height);
        Assert.Equal(default, chip.CornerRadius);

        Border rule = Assert.Single(
            borders,
            b => (b.Height == 1) && (b.Background is ISolidColorBrush { Color: var c }) && (c == Color.Parse("#4A4A4A"))
        );
        Assert.Equal(new Thickness(2, 8, 2, 2), rule.Margin);

        foreach (string caption in new[] { "HEADING", "SPEED" })
        {
            TextBlock text = Assert.Single(content.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == caption);
            Assert.Equal(Color.Parse("#CFD5DC"), ColorOf(text));
        }

        Control freeText = Assert.IsAssignableFrom<Control>(NewHsf(HsfPopupKind.FreeText, _ => { }).Popup.Child);
        TextBlock freeTextCaption = Assert.Single(freeText.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "FREE TEXT");
        Assert.Equal(Color.Parse("#CFD5DC"), ColorOf(freeTextCaption));
    }

    // Where a control's left and right edges fall in the row's coordinates.
    private static (double Left, double Right) Span(Control control, Control row)
    {
        Point? left = control.TranslatePoint(new Point(0, 0), row);
        Assert.NotNull(left);
        return (left.Value.X, left.Value.X + control.Bounds.Width);
    }

    [AvaloniaFact]
    public void EramAltitudePopup_ANote_SitsRightAlignedBesideTheEntry()
    {
        AircraftModel ac = Jet();
        ac.CruiseAltitude = 25000;
        ScopeDataAltitudePopup popup = AltitudePopupFor(ac);
        Window window = OpenInWindow(popup);
        try
        {
            Button row = popup.RowFor("FP altitude");
            (TextBlock label, TextBlock entry, TextBlock note) = popup.RowPartsFor("FP altitude");
            (_, double labelRight) = Span(label, row);
            (double noteLeft, double noteRight) = Span(note, row);
            (double entryLeft, _) = Span(entry, row);

            Assert.InRange(entryLeft - noteRight, 0, 16);
            // The headless font is too wide to leave slack in the row, so the right alignment is read from the grid: a star
            // column between the label and the note takes whatever room is left.
            Grid grid = Assert.IsType<Grid>(label.Parent);
            int noteColumn = Grid.GetColumn(note);
            Assert.Equal(Grid.GetColumn(entry) - 1, noteColumn);
            Assert.True(grid.ColumnDefinitions[noteColumn - 1].Width.IsStar, "no star column before the note");
            Assert.True(noteLeft >= labelRight, $"the note at {noteLeft} overlaps the label ending at {labelRight}");
        }
        finally
        {
            popup.Close();
            CloseWindow(window);
        }
    }

    private static ScopeDataAltitudePopup AltitudePopupFor(AircraftModel ac) => new(ac, new RecordingMenuHost(""), _ => { }, _ => { });

    // EramInterimAltitude is in hundreds of feet, CruiseAltitude in feet.
    [AvaloniaFact]
    public void EramAltitudePopup_TitleNote_ReadsTheFpAltitudeAndTheInterim_AndFpAltitudeReadsNow()
    {
        AircraftModel ac = Jet();
        ac.CruiseAltitude = 25000;
        ac.EramInterimAltitude = null;
        ScopeDataAltitudePopup popup = AltitudePopupFor(ac);

        Assert.Equal("FP 250 · no interim", popup.CaptionNoteFor("Scope data"));
        Assert.Equal("now 250", popup.NoteShownFor("FP altitude"));

        ac.EramInterimAltitude = 180;
        popup.Refresh();

        Assert.Equal("FP 250 · interim 180", popup.CaptionNoteFor("Scope data"));
    }

    [AvaloniaFact]
    public void EramAltitudePopup_WithNoCruiseAltitude_TitleNoteElidesIt_AndFpAltitudeHasNoNote()
    {
        AircraftModel ac = Jet();
        ac.CruiseAltitude = 0;
        ac.EramInterimAltitude = null;
        ScopeDataAltitudePopup popup = AltitudePopupFor(ac);

        Assert.Equal("FP ··· · no interim", popup.CaptionNoteFor("Scope data"));
        Assert.Equal("", popup.NoteShownFor("FP altitude"));
    }

    [AvaloniaFact]
    public void EramSpeedPopup_ThePilotColumn_HoldsAssignSpeed_WithEightPixelsToSpare()
    {
        ScopeDataHsfPopup popup = NewHsf(HsfPopupKind.Speed, WithHsf);
        Window window = OpenInWindow(popup);
        try
        {
            Control column = Assert.IsAssignableFrom<Control>(popup.PilotColumnHost);
            SpeedFlyoutPanel panel = Assert.IsType<SpeedFlyoutPanel>(popup.SpeedPanel);
            Assert.True(column.Bounds.Width >= 319, $"the PILOT column is {column.Bounds.Width} wide");
            Assert.True(column.Bounds.Width >= panel.DesiredSize.Width + 8, $"{column.Bounds.Width} < {panel.DesiredSize.Width} + 8");
            (_, double right) = Span(panel, column);
            Assert.True(right <= column.Bounds.Width - 8, $"Assign speed ends at {right} in a column {column.Bounds.Width} wide");
        }
        finally
        {
            popup.Close();
            CloseWindow(window);
        }
    }

    [AvaloniaFact]
    public void EramCombinedPopup_CapsItsHeightAt600_AndScrollsItsPilotColumn()
    {
        ScopeDataHsfPopup popup = NewHsf(HsfPopupKind.Combined, WithHsf);
        // A window taller than the cap, so only the cap can keep the popup at 600.
        Window window = OpenInWindow(popup, 1000);
        try
        {
            ScrollViewer scroll = Assert.IsType<ScrollViewer>(popup.PilotScroll);
            Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
            List<Control> inside = [.. scroll.GetLogicalDescendants().OfType<Control>()];
            Assert.Contains(Assert.IsType<HeadingFlyoutPanel>(popup.HeadingPanel), inside);
            Assert.Contains(Assert.IsType<SpeedFlyoutPanel>(popup.SpeedPanel), inside);
            Assert.True(
                scroll.Extent.Height > scroll.Viewport.Height,
                $"the PILOT column ({scroll.Extent.Height}) fits its {scroll.Viewport.Height}"
            );

            Control content = Assert.IsAssignableFrom<Control>(popup.Popup.Child);
            content.Measure(Size.Infinity);
            Assert.True(content.DesiredSize.Height <= 600, $"the Combined popup wants {content.DesiredSize.Height}");
        }
        finally
        {
            popup.Close();
            CloseWindow(window);
        }
    }

    [AvaloniaFact]
    public void EramFreeTextPopup_SetFreeText_ShowsEramMarks_AndPicksTheBacktickEntry()
    {
        AircraftModel ac = Jet();
        WithFreeText(ac);
        var picks = new List<string>();
        var popup = new ScopeDataHsfPopup(ac, new RecordingMenuHost(""), _ => { }, picks.Add, HsfPopupKind.FreeText);

        Assert.Equal("QS ○LOWALT UAL238", popup.EntryShownFor("Set free text"));

        popup.RowFor("Set free text").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(["QS `LOWALT UAL238"], picks);
    }

    // The run site: RunEramText gets the backtick entry and sends ERAM's own clear-weather character in its place.
    [AvaloniaFact]
    public async Task EramFreeTextPopup_PlainClickOnSetFreeText_RunsTheBacktickEntry()
    {
        (ScopeViewModel scope, ScopeView view, Window window) = await OpenHsfAsync(ScopeFieldAction.OpenEramFreeTextMenu, WithFreeText);
        try
        {
            List<ScopeEramEntryDto> sends = CaptureEram(scope);
            ClickInPopup(OpenHsf(view).RowFor("Set free text"), RawInputModifiers.None);
            await scope.LastEntryRun;

            Assert.Equal(["QS", "\u0087LOWALT", "UAL238"], Assert.Single(sends).Tokens);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // The stage site: StageEramText gets the backtick entry as is.
    [AvaloniaFact]
    public async Task EramFreeTextPopup_ShiftClickOnSetFreeText_StagesTheBacktickEntry()
    {
        (ScopeViewModel scope, ScopeView view, Window window) = await OpenHsfAsync(ScopeFieldAction.OpenEramFreeTextMenu, WithFreeText);
        try
        {
            ClickInPopup(OpenHsf(view).RowFor("Set free text"), RawInputModifiers.Shift);

            Assert.Equal("QS `LOWALT UAL238", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // Shows the ERAM scope in a 1400 x 1000 window, adds UAL238 with its target where `target` puts it on the canvas, clicks its
    // altitude, and answers the popup's frame, the block and the window, all in the canvas's coordinates.
    private static async Task<(ScopeView View, Window Window, Rect Popup, Rect Block, Rect Bounds)> ClickAltitudePlacedAsync(Func<Rect, Point> target)
    {
        (MainViewModel main, _, ScopeView view, Window window) = await ShowScope(ScopeTemplates.EramId, 1400, 1000);
        await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        AddAt(main, view.Canvas, _ => { }, target(view.Canvas.Bounds));
        main.SelectedAircraft = null;
        Dispatcher.UIThread.RunJobs();
        ClickAt(window, view.Canvas, Centre(view.Canvas, ScopeFieldSlot.EramAltitude));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        return Framed(view, window, OpenPopup(view));
    }

    // The popup's frame, the block and the window, all in the canvas's coordinates.
    private static (ScopeView View, Window Window, Rect Popup, Rect Block, Rect Bounds) Framed(
        ScopeView view,
        Window window,
        ScopeDataPopupBase popup
    )
    {
        SKRect block = Assert.Single(view.Canvas.CurrentFrame.Tracks).Block.Bounds;
        Control content = Assert.IsAssignableFrom<Control>(popup.Popup.Child);
        PixelPoint canvasAt = view.Canvas.PointToScreen(new Point(0, 0));
        PixelPoint contentAt = content.PointToScreen(new Point(0, 0));
        PixelPoint windowAt = window.PointToScreen(new Point(0, 0));
        return (
            view,
            window,
            new Rect(contentAt.X - canvasAt.X, contentAt.Y - canvasAt.Y, content.Bounds.Width, content.Bounds.Height),
            new Rect(block.Left, block.Top, block.Width, block.Height),
            new Rect(windowAt.X - canvasAt.X, windowAt.Y - canvasAt.Y, window.ClientSize.Width, window.ClientSize.Height)
        );
    }

    [AvaloniaFact]
    public async Task EramAltitudeClick_WithRoomBelow_OpensThePopupUnderTheBlock_LeftAligned()
    {
        (ScopeView view, Window window, Rect popup, Rect block, _) = await ClickAltitudePlacedAsync(_ => new Point(300, 300));
        try
        {
            Assert.True(popup.Top >= block.Bottom, $"the popup's top {popup.Top} is above the block's bottom {block.Bottom}");
            Assert.InRange(popup.Top - block.Bottom, 3, 5);
            Assert.InRange(popup.Left - block.Left, -1, 1);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramAltitudeClick_NearTheBottomEdge_OpensThePopupAboveTheBlock_InsideTheWindow()
    {
        (ScopeView view, Window window, Rect popup, Rect block, Rect bounds) = await ClickAltitudePlacedAsync(c => new Point(300, c.Height - 60));
        try
        {
            Assert.True(bounds.Contains(popup), $"the popup {popup} leaves the window {bounds}");
            Assert.True(popup.Bottom <= block.Top, $"the popup's bottom {popup.Bottom} covers the block from {block.Top}");
            Assert.InRange(popup.Left - block.Left, -1, 1);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramAltitudeClick_NearTheRightEdge_ShiftsThePopupLeft_InsideTheWindow()
    {
        (ScopeView view, Window window, Rect popup, Rect block, Rect bounds) = await ClickAltitudePlacedAsync(c => new Point(c.Width - 150, 300));
        try
        {
            Assert.True(bounds.Contains(popup), $"the popup {popup} leaves the window {bounds}");
            Assert.True(popup.Top >= block.Bottom, $"the popup's top {popup.Top} is above the block's bottom {block.Bottom}");
            Assert.True(popup.Left < block.Left, $"the popup at {popup.Left} is not shifted left of the block at {block.Left}");
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // Shows the ERAM scope in a 1400 x 800 window, adds UAL238 with its target where `target` puts it on the canvas, opens its
    // line-4 popup (Heading and Speed, 600 high) at its block, and answers the popup's frame, the block and the window.
    private static Task<(ScopeView View, Window Window, Rect Popup, Rect Block, Rect Bounds)> OpenCombinedPlacedAsync(Func<Rect, Point> target) =>
        OpenCombinedPlacedAsync(target, 1400);

    // As the other overload, in a window `width` wide.
    private static async Task<(ScopeView View, Window Window, Rect Popup, Rect Block, Rect Bounds)> OpenCombinedPlacedAsync(
        Func<Rect, Point> target,
        double width
    )
    {
        (MainViewModel main, _, ScopeView view, Window window) = await ShowScope(ScopeTemplates.EramId, width, 800);
        await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        AddAt(main, view.Canvas, WithHsf, target(view.Canvas.Bounds));
        view.OnFieldClicked("UAL238", ScopeFieldAction.OpenEramHsfMenu, default, KeyModifiers.None);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return Framed(view, window, OpenHsf(view));
    }

    private static void AssertBesideInside(Rect popup, Rect block, Rect bounds)
    {
        Assert.False(popup.Intersects(block), $"the popup {popup} covers the block {block}");
        Assert.True(popup.Top >= bounds.Top, $"the popup's top {popup.Top} is above the window's {bounds.Top}");
        Assert.True(popup.Bottom <= bounds.Bottom, $"the popup's bottom {popup.Bottom} is below the window's {bounds.Bottom}");
    }

    [AvaloniaFact]
    public async Task EramLine4Click_NoRoomBelowOrAbove_OpensThePopupRightOfTheBlock()
    {
        (ScopeView view, Window window, Rect popup, Rect block, Rect bounds) = await OpenCombinedPlacedAsync(c => new Point(300, c.Height / 2));
        try
        {
            Assert.True(popup.Height > bounds.Height - block.Bottom, $"the popup {popup} would fit below the block {block}");
            Assert.True(popup.Height > block.Top - bounds.Top, $"the popup {popup} would fit above the block {block}");
            AssertBesideInside(popup, block, bounds);
            Assert.InRange(popup.Left - block.Right, 3, 5);
            // Level with the block where the window has room below it, else clamped to the window's bottom.
            Assert.InRange(popup.Top - Math.Min(block.Top, bounds.Bottom - popup.Height), -1, 1);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramLine4Click_NoRoomBelowAboveOrRight_OpensThePopupLeftOfTheBlock()
    {
        (ScopeView view, Window window, Rect popup, Rect block, Rect bounds) = await OpenCombinedPlacedAsync(c => new Point(
            c.Width - 150,
            c.Height / 2
        ));
        try
        {
            AssertBesideInside(popup, block, bounds);
            Assert.InRange(block.Left - popup.Right, 3, 5);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // A window too narrow for the popup on either side of the block: it opens at the window's left edge, top inside the window.
    [AvaloniaFact]
    public async Task EramLine4Click_NoRoomOnAnySide_OpensThePopupAtTheWindowsLeftEdge()
    {
        (ScopeView view, Window window, Rect popup, Rect block, Rect bounds) = await OpenCombinedPlacedAsync(
            c => new Point(c.Width / 2, c.Height / 2),
            1000
        );
        try
        {
            Assert.True(popup.Width > bounds.Right - block.Right - 4, $"the popup {popup} would fit right of the block {block}");
            Assert.True(popup.Width > block.Left - bounds.Left - 4, $"the popup {popup} would fit left of the block {block}");
            Assert.InRange(popup.Left - bounds.Left, -1, 1);
            Assert.True(popup.Top >= bounds.Top, $"the popup's top {popup.Top} is above the window's {bounds.Top}");
            Assert.True(popup.Bottom <= bounds.Bottom, $"the popup's bottom {popup.Bottom} is below the window's {bounds.Bottom}");
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramHeadingToken_OpensTheHeadingSection_BesideFlyHeading()
    {
        (_, ScopeView view, Window window) = await OpenHsfAsync(ScopeFieldAction.OpenEramHeadingMenu, WithHsf);
        try
        {
            ScopeDataHsfPopup popup = OpenHsf(view);
            Assert.Equal(HsfPopupKind.Heading, popup.Kind);
            Assert.NotNull(popup.HeadingBox);
            Assert.Null(popup.SpeedBox);
            Assert.Null(popup.FreeTextBox);
            Assert.NotNull(popup.HeadingPanel);
            Assert.Null(popup.SpeedPanel);
            List<string> texts = HsfTexts(popup);
            Assert.Contains(ScopeDataPopupBase.ScopeDataHead, texts);
            Assert.Contains(ScopeDataPopupBase.PilotHead, texts);
            Assert.Contains(ScopeDataHsfPopup.HeadingCaption, texts);
            Assert.DoesNotContain(ScopeDataHsfPopup.SpeedCaption, texts);
            Assert.Contains("now H270", texts);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramSpeedToken_OpensTheSpeedSection_BesideAssignSpeed()
    {
        (_, ScopeView view, Window window) = await OpenHsfAsync(ScopeFieldAction.OpenEramSpeedMenu, WithHsf);
        try
        {
            ScopeDataHsfPopup popup = OpenHsf(view);
            Assert.Equal(HsfPopupKind.Speed, popup.Kind);
            Assert.Null(popup.HeadingBox);
            Assert.NotNull(popup.SpeedBox);
            Assert.Null(popup.FreeTextBox);
            Assert.Null(popup.HeadingPanel);
            Assert.NotNull(popup.SpeedPanel);
            List<string> texts = HsfTexts(popup);
            Assert.Contains(ScopeDataPopupBase.PilotHead, texts);
            Assert.Contains(ScopeDataHsfPopup.SpeedCaption, texts);
            Assert.DoesNotContain(ScopeDataHsfPopup.HeadingCaption, texts);
            Assert.Contains("now S250", texts);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramFreeText_OpensTheFreeTextSectionAlone_WithNoPilotColumn()
    {
        (_, ScopeView view, Window window) = await OpenHsfAsync(ScopeFieldAction.OpenEramFreeTextMenu, WithFreeText);
        try
        {
            ScopeDataHsfPopup popup = OpenHsf(view);
            Assert.Equal(HsfPopupKind.FreeText, popup.Kind);
            Assert.Null(popup.HeadingBox);
            Assert.Null(popup.SpeedBox);
            Assert.NotNull(popup.FreeTextBox);
            Assert.Null(popup.HeadingPanel);
            Assert.Null(popup.SpeedPanel);
            List<string> texts = HsfTexts(popup);
            Assert.Contains(ScopeDataPopupBase.ScopeDataHead, texts);
            Assert.DoesNotContain(ScopeDataPopupBase.PilotHead, texts);
            Assert.Contains(ScopeDataHsfPopup.FreeTextCaption, texts);
            Assert.Equal("LOWALT", popup.FreeTextBox.Text);
            Assert.Equal("QS \u25CBLOWALT UAL238", popup.EntryShownFor("Set free text"));
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramLine4Fallback_OpensHeadingAndSpeed_BesideFlyHeadingAboveAssignSpeed()
    {
        (_, ScopeView view, Window window) = await OpenHsfAsync(ScopeFieldAction.OpenEramHsfMenu, WithHsf);
        try
        {
            ScopeDataHsfPopup popup = OpenHsf(view);
            Assert.Equal(HsfPopupKind.Combined, popup.Kind);
            Assert.Null(popup.FreeTextBox);
            HeadingFlyoutPanel heading = Assert.IsType<HeadingFlyoutPanel>(popup.HeadingPanel);
            SpeedFlyoutPanel speed = Assert.IsType<SpeedFlyoutPanel>(popup.SpeedPanel);
            Panel column = Assert.IsAssignableFrom<Panel>(heading.Parent);
            Assert.Same(column, speed.Parent);
            Assert.True(column.Children.IndexOf(heading) < column.Children.IndexOf(speed), "Fly heading is not above Assign speed");
            Assert.Equal("H270", popup.HeadingBox?.Text);
            Assert.Equal("S250", popup.SpeedBox?.Text);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public void EramHsfPopup_NothingStored_OpensWithEmptyBoxes_AndTheClearRowsDisabled()
    {
        ScopeDataHsfPopup popup = NewHsf(HsfPopupKind.Combined, _ => { });

        Assert.Equal("", popup.HeadingBox?.Text ?? "");
        Assert.Equal("", popup.SpeedBox?.Text ?? "");
        Assert.False(popup.RowFor("Clear heading").IsEnabled);
        Assert.False(popup.RowFor("Clear speed").IsEnabled);
        Assert.False(popup.RowFor("Set heading").IsEnabled);
        Assert.Equal("QS ··· UAL238", popup.EntryShownFor("Set heading"));
        Assert.Equal("QS /··· UAL238", popup.EntryShownFor("Set speed"));
    }

    [AvaloniaFact]
    public void EramHsfPopup_SetHeading_IsEnabledOnlyForAHeadingTheServerTakes()
    {
        ScopeDataHsfPopup popup = NewHsf(HsfPopupKind.Heading, _ => { });
        TextBox box = Assert.IsType<TextBox>(popup.HeadingBox);

        box.Text = "H300";
        Assert.True(popup.RowFor("Set heading").IsEnabled);
        Assert.Equal("QS H300 UAL238", popup.EntryShownFor("Set heading"));
        box.Text = "20L";
        Assert.True(popup.RowFor("Set heading").IsEnabled);
        box.Text = "H999";
        Assert.False(popup.RowFor("Set heading").IsEnabled);
        Assert.Equal("QS ··· UAL238", popup.EntryShownFor("Set heading"));
    }

    [AvaloniaFact]
    public void EramHsfPopup_SetSpeed_IsEnabledOnlyForASpeedTheServerTakes()
    {
        ScopeDataHsfPopup popup = NewHsf(HsfPopupKind.Speed, _ => { });
        TextBox box = Assert.IsType<TextBox>(popup.SpeedBox);

        box.Text = "250-";
        Assert.True(popup.RowFor("Set speed").IsEnabled);
        Assert.Equal("QS /250- UAL238", popup.EntryShownFor("Set speed"));
        box.Text = "S0";
        Assert.False(popup.RowFor("Set speed").IsEnabled);
        box.Text = "FAST";
        Assert.False(popup.RowFor("Set speed").IsEnabled);
    }

    [AvaloniaFact]
    public void EramHsfPopup_SetFreeText_IsEnabledForOneToEightAcceptedCharacters()
    {
        ScopeDataHsfPopup popup = NewHsf(HsfPopupKind.FreeText, _ => { });
        TextBox box = Assert.IsType<TextBox>(popup.FreeTextBox);

        Assert.False(popup.RowFor("Set free text").IsEnabled);
        box.Text = "low alt";
        Assert.Equal("LOWALT", box.Text);
        Assert.True(popup.RowFor("Set free text").IsEnabled);
        box.Text = "ABCDEFGHIJ";
        Assert.Equal("ABCDEFGH", box.Text);
        box.Text = "a#b/1";
        Assert.Equal("AB/1", box.Text);
    }

    [AvaloniaFact]
    public void EramHsfPopup_ClearHeading_IsEnabledOnlyWhileAHeadingIsStored()
    {
        Assert.True(NewHsf(HsfPopupKind.Combined, ac => ac.EramAssignedHeading = "H270").RowFor("Clear heading").IsEnabled);
        Assert.False(NewHsf(HsfPopupKind.Combined, ac => ac.EramAssignedSpeed = "S250").RowFor("Clear heading").IsEnabled);
    }

    [AvaloniaFact]
    public void EramHsfPopup_ClearSpeed_IsEnabledOnlyWhileASpeedIsStored()
    {
        Assert.True(NewHsf(HsfPopupKind.Combined, ac => ac.EramAssignedSpeed = "S250").RowFor("Clear speed").IsEnabled);
        Assert.False(NewHsf(HsfPopupKind.Combined, ac => ac.EramAssignedHeading = "H270").RowFor("Clear speed").IsEnabled);
    }

    [AvaloniaFact]
    public void EramHsfPopup_ClearAllHsf_IsEnabledWhileAnyHsfValueIsStored()
    {
        Assert.True(NewHsf(HsfPopupKind.FreeText, ac => ac.EramAssignedHeading = "H270").RowFor("Clear all HSF").IsEnabled);
        Assert.True(NewHsf(HsfPopupKind.FreeText, ac => ac.EramAssignedSpeed = "S250").RowFor("Clear all HSF").IsEnabled);
        Assert.True(NewHsf(HsfPopupKind.FreeText, WithFreeText).RowFor("Clear all HSF").IsEnabled);
        Assert.False(NewHsf(HsfPopupKind.FreeText, _ => { }).RowFor("Clear all HSF").IsEnabled);
        Assert.Equal("QS * UAL238", NewHsf(HsfPopupKind.FreeText, WithFreeText).EntryShownFor("Clear all HSF"));
    }

    [AvaloniaFact]
    public async Task EramHeadingPopup_TypedH300_PlainClickOnSetHeading_RunsTheEntry_AndClosesThePopup()
    {
        (ScopeViewModel scope, ScopeView view, Window window) = await OpenHsfAsync(ScopeFieldAction.OpenEramHeadingMenu, WithHsf);
        try
        {
            List<ScopeEramEntryDto> sends = CaptureEram(scope);
            ScopeDataHsfPopup popup = OpenHsf(view);
            Assert.Equal("H270", popup.HeadingBox?.Text);
            Assert.IsType<TextBox>(popup.HeadingBox).Text = "H300";
            Dispatcher.UIThread.RunJobs();

            ClickInPopup(popup.RowFor("Set heading"), RawInputModifiers.None);
            await scope.LastEntryRun;

            Assert.Equal("QS H300 UAL238", string.Join(' ', Assert.Single(sends).Tokens));
            Assert.False(popup.IsOpen);
            Assert.Null(view.ActiveFieldPopup);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramHeadingPopup_TypedH300_ShiftClickOnSetHeading_StagesTheEntry_AndSendsNothing()
    {
        (ScopeViewModel scope, ScopeView view, Window window) = await OpenHsfAsync(ScopeFieldAction.OpenEramHeadingMenu, WithHsf);
        try
        {
            List<ScopeEramEntryDto> sends = CaptureEram(scope);
            ScopeDataHsfPopup popup = OpenHsf(view);
            Assert.IsType<TextBox>(popup.HeadingBox).Text = "H300";
            Dispatcher.UIThread.RunJobs();

            ClickInPopup(popup.RowFor("Set heading"), RawInputModifiers.Shift);

            Assert.Equal("QS H300 UAL238", scope.EntryText);
            Assert.Empty(sends);
            Assert.Null(view.ActiveFieldPopup);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramSpeedPopup_TypedSpeed_RunsSetSpeed()
    {
        (ScopeViewModel scope, ScopeView view, Window window) = await OpenHsfAsync(ScopeFieldAction.OpenEramSpeedMenu, WithHsf);
        try
        {
            List<ScopeEramEntryDto> sends = CaptureEram(scope);
            ScopeDataHsfPopup popup = OpenHsf(view);
            Assert.Equal("S250", popup.SpeedBox?.Text);
            Assert.IsType<TextBox>(popup.SpeedBox).Text = "250-";
            Dispatcher.UIThread.RunJobs();

            ClickInPopup(popup.RowFor("Set speed"), RawInputModifiers.None);
            await scope.LastEntryRun;

            Assert.Equal("QS /250- UAL238", string.Join(' ', Assert.Single(sends).Tokens));
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramSpeedPopup_ClearSpeed_RunsTheClear()
    {
        (ScopeViewModel scope, ScopeView view, Window window) = await OpenHsfAsync(ScopeFieldAction.OpenEramSpeedMenu, WithHsf);
        try
        {
            List<ScopeEramEntryDto> sends = CaptureEram(scope);
            ScopeDataHsfPopup popup = OpenHsf(view);

            ClickInPopup(popup.RowFor("Clear speed"), RawInputModifiers.None);
            await scope.LastEntryRun;

            Assert.Equal("QS /* UAL238", string.Join(' ', Assert.Single(sends).Tokens));
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public void EramHeadingPopup_APilotFlyHeadingChoice_SendsItsCommandForTheAircraft()
    {
        var host = new RecordingMenuHost("");
        var popup = new ScopeDataHsfPopup(Jet(), host, _ => { }, _ => { }, HsfPopupKind.Heading);
        HeadingFlyoutPanel panel = Assert.IsType<HeadingFlyoutPanel>(popup.HeadingPanel);

        panel.PresentRow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        (string callsign, string command, _) = Assert.Single(host.Sent);
        Assert.Equal(("UAL238", "FPH"), (callsign, command));
    }

    [AvaloniaFact]
    public async Task OpeningAnEramHsfPopup_ClosesTheAltitudePopup()
    {
        (_, _, ScopeView view, Window window, _) = await OpenEramAltitudeAsync(_ => { });
        try
        {
            ScopeDataAltitudePopup altitude = OpenPopup(view);

            view.OnFieldClicked("UAL238", ScopeFieldAction.OpenEramHeadingMenu, default, KeyModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.False(altitude.IsOpen);
            Assert.Equal(HsfPopupKind.Heading, OpenHsf(view).Kind);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task OpeningAnEramHsfPopup_ClosesAnOpenContextMenu()
    {
        (_, ScopeView view, Window window) = await OpenHsfAsync(ScopeFieldAction.OpenSpeedMenu, Speedy);
        try
        {
            ContextMenu menu = Assert.IsType<ContextMenu>(view.ActiveContextMenu);

            view.OnFieldClicked("UAL238", ScopeFieldAction.OpenEramSpeedMenu, default, KeyModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.False(menu.IsOpen);
            Assert.Null(view.ActiveContextMenu);
            Assert.Equal(HsfPopupKind.Speed, OpenHsf(view).Kind);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EscInsideAnEramHsfPopup_ClosesIt()
    {
        (_, ScopeView view, Window window) = await OpenHsfAsync(ScopeFieldAction.OpenEramFreeTextMenu, WithFreeText);
        try
        {
            ScopeDataHsfPopup popup = OpenHsf(view);
            TextBox box = Assert.IsType<TextBox>(popup.FreeTextBox);
            Assert.True(box.Focus());
            TopLevel top = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(box));

            top.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.False(popup.IsOpen);
            Assert.Null(view.ActiveFieldPopup);
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

    /// <summary>Offline there is no room to take control in, so the take reports why on the status line.</summary>
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

            Assert.Equal("$ UAL238 SPD 250", scope.EntryText);
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

            Assert.Equal("$ UAL238 SPD 250", scope.EntryText);
            Assert.Equal(statusBefore, main.StatusText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    /// <summary>After a stage the menu is closed and the canvas has the focus, so Enter runs the entry without its $.</summary>
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
        (_, ScopeViewModel scope, ScopeView view, Window window, _) = await OpenSpeedMenuAsync(s => s.TypeEntryText("$ UAL238 CM 80"));
        try
        {
            ClickInPopup(SpeedChip(view, 250), RawInputModifiers.Shift);

            Assert.Equal("$ UAL238 CM 80, SPD 250", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    /// <summary>A menu-item row picked with Shift: the EuroScope runway flyout's 28L row stages.</summary>
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
            MenuItem row = EuroScopeRow(OpenEuroScopeMenu(view, "Runway — UAL238"), "28L");

            ClickInPopup(row, RawInputModifiers.Shift);

            Assert.Equal("$ UAL238 RWY 28L", scope.EntryText);
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

            Assert.Equal("$ UAL238 ACCEPT", Assert.IsType<ScopeViewModel>(view.DataContext).EntryText);
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
            Assert.Equal("$ UAL238 SPD 250", scope.EntryText);
            Assert.Null(view.ActiveContextMenu);

            ClickAt(window, view.Canvas, Centre(view.Canvas, ScopeFieldSlot.GroundSpeed));
            ClickInPopup(SpeedChip(view, 250), RawInputModifiers.None);

            Assert.Equal(NotConnected, main.StatusText);
            Assert.Equal("$ UAL238 SPD 250", scope.EntryText);
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

    // ---- The EuroScope tag's own flyouts (restored from the Radar view) ----

    // The EuroScope flyout the click opened: an open context menu whose first row is its bold header.
    private static ContextMenu OpenEuroScopeMenu(ScopeView view, string header)
    {
        ContextMenu menu = Assert.IsType<ContextMenu>(view.ActiveContextMenu);
        Assert.True(menu.IsOpen);
        Assert.Equal(header, Assert.IsType<MenuItem>(menu.Items[0]).Header);
        return menu;
    }

    private static List<string> EuroScopeHeaders(ContextMenu menu) => [.. menu.Items.OfType<MenuItem>().Select(i => i.Header as string ?? "")];

    private static MenuItem EuroScopeRow(ContextMenu menu, string header) =>
        Assert.Single(menu.Items.OfType<MenuItem>(), i => (i.Header as string) == header);

    private static void Pick(MenuItem row)
    {
        row.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static List<string> SentCommands(RecordingMenuHost host) => [.. host.Sent.Select(s => $"{s.Callsign} {s.Command}")];

    // Shows a EuroScope scope with UAL238 as setUp leaves it and runs the action as a field click does.
    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window)> OpenEuroScopeAsync(
        ScopeFieldAction action,
        Action<AircraftModel> setUp
    )
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScope(ScopeTemplates.EuroScopeId);
        await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        AddAt(main, view.Canvas, setUp);
        view.OnFieldClicked("UAL238", action, default, KeyModifiers.None);
        Dispatcher.UIThread.RunJobs();
        return (main, scope, view, window);
    }

    private static List<string> ButtonLabels(Popup popup) =>
        [.. Assert.IsAssignableFrom<Control>(popup.Child).GetLogicalDescendants().OfType<Button>().Select(b => b.Content as string ?? "")];

    private static Button PopupButton(Popup popup, string label) =>
        Assert.Single(Assert.IsAssignableFrom<Control>(popup.Child).GetLogicalDescendants().OfType<Button>(), b => (b.Content as string) == label);

    [AvaloniaFact]
    public async Task EuroScopeAltitudeClick_OpensTheEuroScopeAltitudeFlyout_FL400ToFL010_CappedAt280()
    {
        (_, _, ScopeView view, Window window, _) = await ClickFieldAsync(ScopeTemplates.EuroScopeId, ScopeFieldSlot.Altitude, _ => { }, _ => { });
        try
        {
            ContextMenu menu = OpenEuroScopeMenu(view, "Altitude — UAL238");
            List<string> rows = [.. EuroScopeHeaders(menu).Skip(1)];
            Assert.Equal([.. Enumerable.Range(1, 40).Reverse().Select(fl => $"FL{fl * 10:D3}")], rows);
            ScrollViewer scroller = menu.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.Equal(280.0, scroller.MaxHeight);
            Assert.Null(view.ActiveFieldPopup);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public void EuroScopeAltitudeFlyout_FL120AboveTheAircraft_SendsCm120_AndBelowItDm()
    {
        var host = new RecordingMenuHost("");
        var ac = new AircraftModel { Callsign = "UAL238", Altitude = 5000 };
        ContextMenu menu = EuroScopeAltitudeFlyout.Build(ac, host);

        Pick(EuroScopeRow(menu, "FL120"));
        Pick(EuroScopeRow(menu, "FL030"));

        Assert.Equal(["UAL238 CM 120", "UAL238 DM 30"], SentCommands(host));
    }

    [AvaloniaFact]
    public async Task EuroScopeAltitudeRow_ShiftClick_StagesTheClimb_AndSendsNothing()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window, _) = await ClickFieldAsync(
            ScopeTemplates.EuroScopeId,
            ScopeFieldSlot.Altitude,
            _ => { },
            _ => { }
        );
        try
        {
            string statusBefore = main.StatusText;
            MenuItem row = EuroScopeRow(OpenEuroScopeMenu(view, "Altitude — UAL238"), "FL120");
            row.BringIntoView();
            Dispatcher.UIThread.RunJobs();

            ClickInPopup(row, RawInputModifiers.Shift);

            Assert.Equal("$ UAL238 CM 120", scope.EntryText);
            Assert.Equal(statusBefore, main.StatusText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EuroScopeSpeedClick_OpensTheEuroScopeSpeedFlyout_AndARowSendsSpd()
    {
        (_, _, ScopeView view, Window window) = await OpenEuroScopeAsync(ScopeFieldAction.OpenSpeedMenu, _ => { });
        try
        {
            List<string> headers = EuroScopeHeaders(OpenEuroScopeMenu(view, "Speed — UAL238"));
            Assert.Equal("Resume Normal Speed (RNS)", headers[1]);
            Assert.Equal([.. Enumerable.Range(8, 28).Reverse().Select(s => $"{s * 10:D3} kt")], headers.Skip(2).Take(28));
        }
        finally
        {
            CloseWindow(window, view);
        }

        var host = new RecordingMenuHost("");
        ContextMenu menu = EuroScopeSpeedFlyout.Build(new AircraftModel { Callsign = "UAL238", GroundSpeed = 250 }, host);
        Pick(EuroScopeRow(menu, "250 kt"));
        Pick(EuroScopeRow(menu, "Resume Normal Speed (RNS)"));
        Assert.Equal(["UAL238 SPD 250", "UAL238 RNS"], SentCommands(host));
    }

    [AvaloniaFact]
    public async Task EuroScopeSquawkClick_OpensTheEuroScopeSquawkFlyout_AndItsFiveRowsSend()
    {
        (_, _, ScopeView view, Window window) = await OpenEuroScopeAsync(ScopeFieldAction.OpenSquawkMenu, _ => { });
        try
        {
            OpenEuroScopeMenu(view, "Squawk — UAL238");
        }
        finally
        {
            CloseWindow(window, view);
        }

        var host = new RecordingMenuHost("");
        ContextMenu menu = EuroScopeSquawkFlyout.Build(new AircraftModel { Callsign = "UAL238" }, host);
        foreach (MenuItem row in menu.Items.OfType<MenuItem>().Where(i => i.IsEnabled))
        {
            Pick(row);
        }

        Assert.Equal(["UAL238 SN", "UAL238 SS", "UAL238 SQVFR", "UAL238 IDENT", "UAL238 RANDSQ"], SentCommands(host));
    }

    [AvaloniaFact]
    public async Task EuroScopeRunwayClick_OpensTheEuroScopeRunwayFlyout_AndClearSendsRwy()
    {
        (_, _, ScopeView view, Window window, _) = await ClickFieldAsync(
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
            List<string> headers = EuroScopeHeaders(OpenEuroScopeMenu(view, "Runway — UAL238"));
            Assert.Contains("Currently assigned: 28R", headers);
            Assert.Contains("▶ 28R", headers);
        }
        finally
        {
            CloseWindow(window, view);
        }

        var host = new RecordingMenuHost("");
        ContextMenu menu = EuroScopeRunwayFlyout.Build(new AircraftModel { Callsign = "UAL238" }, host);
        Pick(EuroScopeRow(menu, "Clear assigned runway"));
        Assert.Equal(["UAL238 RWY"], SentCommands(host));
    }

    [AvaloniaFact]
    public async Task EuroScopeScratchpadClick_OpensTheScratchpadPopup_WithItsPresets_AndASubmitSendsSp1()
    {
        (_, _, ScopeView view, Window window) = await OpenEuroScopeAsync(ScopeFieldAction.OpenScratchpadMenu, _ => { });
        try
        {
            Popup popup = Assert.IsType<Popup>(view.ActiveEntryPopup);
            Assert.True(popup.IsOpen);
            Assert.Null(view.ActiveContextMenu);
            List<string> labels = ButtonLabels(popup);
            Assert.Equal(["CLEA", "NOTC", "ST-UP", "PUSH", "TAXI", "DEPA"], labels.Skip(2));
        }
        finally
        {
            CloseWindow(window, view);
        }

        var host = new RecordingMenuHost("");
        var anchor = new Border();
        Popup built = EuroScopeScratchpadFlyout.Build(anchor, new AircraftModel { Callsign = "UAL238" }, host, 1);
        PopupButton(built, "PUSH").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["UAL238 SP1 PUSH"], SentCommands(host));
    }

    [AvaloniaFact]
    public async Task EuroScopeHandoffClick_ShowsAccept_OnlyWithAPendingHandoff_AndAcceptSends()
    {
        (_, _, ScopeView view, Window window) = await OpenEuroScopeAsync(ScopeFieldAction.OpenHandoffMenu, ac => ac.HandoffPeerSectorCode = "1C");
        try
        {
            Popup popup = Assert.IsType<Popup>(view.ActiveEntryPopup);
            Assert.True(popup.IsOpen);
            Assert.Contains("Accept handoff", ButtonLabels(popup));
        }
        finally
        {
            CloseWindow(window, view);
        }

        Popup none = EuroScopeHandoffFlyout.Build(new Border(), new AircraftModel { Callsign = "UAL238" }, new RecordingMenuHost(""));
        Assert.DoesNotContain("Accept handoff", ButtonLabels(none));

        var host = new RecordingMenuHost("");
        Popup pending = EuroScopeHandoffFlyout.Build(new Border(), new AircraftModel { Callsign = "UAL238", HandoffPeerSectorCode = "1C" }, host);
        PopupButton(pending, "Accept handoff").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["UAL238 ACCEPT"], SentCommands(host));
    }

    private static TextBox EntryBox(Popup popup) =>
        Assert.Single(Assert.IsAssignableFrom<Control>(popup.Child).GetLogicalDescendants().OfType<TextBox>());

    // Types the text in the open popup's box and presses Enter there, with the modifiers held.
    private static void SubmitTyped(Popup popup, string text, RawInputModifiers modifiers)
    {
        TextBox box = EntryBox(popup);
        box.Text = text;
        Assert.True(box.Focus());
        TopLevel top = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(box));
        top.KeyPressQwerty(PhysicalKey.Enter, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    // Types the text in a built, unopened popup's box and raises Enter on it.
    private static void SubmitUnopened(Popup popup, string text)
    {
        TextBox box = EntryBox(popup);
        box.Text = text;
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Dispatcher.UIThread.RunJobs();
    }

    private static string PopupTitle(Popup popup) =>
        Assert.IsAssignableFrom<Control>(popup.Child).GetLogicalDescendants().OfType<TextBlock>().First().Text ?? "";

    /// <summary>Esc in the open scratchpad box closes the popup and leaves the Scope's entry; Ctrl+M there arms nothing.</summary>
    [AvaloniaFact]
    public async Task EuroScopeScratchpadPopup_EscClosesIt_KeepingTheEntry_AndCtrlMInItsBoxArmsNothing()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await OpenEuroScopeAsync(ScopeFieldAction.OpenScratchpadMenu, _ => { });
        try
        {
            scope.TypeEntryText("$ UAL238 FH 270");
            Popup popup = Assert.IsType<Popup>(view.ActiveEntryPopup);
            TextBox box = EntryBox(popup);
            Assert.True(box.Focus());
            TopLevel top = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(box));
            ScopeEntryResponse? responseBefore = scope.EntryResponse;

            top.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();

            Assert.Same(responseBefore, scope.EntryResponse);
            Assert.True(popup.IsOpen);

            top.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.False(popup.IsOpen);
            Assert.Null(view.ActiveEntryPopup);
            Assert.Equal("$ UAL238 FH 270", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EuroScopeScratchpadPreset_ShiftClick_StagesSp1_AndSendsNothing()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await OpenEuroScopeAsync(
            ScopeFieldAction.OpenScratchpadMenu,
            _ => { }
        );
        try
        {
            string statusBefore = main.StatusText;
            Popup popup = Assert.IsType<Popup>(view.ActiveEntryPopup);

            ClickInPopup(PopupButton(popup, "PUSH"), RawInputModifiers.Shift);

            Assert.Equal("$ UAL238 SP1 PUSH", scope.EntryText);
            Assert.Equal(statusBefore, main.StatusText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EuroScopeScratchpad2Click_OpensScratchpad2_AndATypedSubmitSendsSp2()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window, _) = await ClickFieldAsync(
            ScopeTemplates.EuroScopeId,
            ScopeFieldSlot.Scratchpad2,
            ac => ac.Scratchpad2 = "XY",
            _ => { }
        );
        try
        {
            Popup popup = Assert.IsType<Popup>(view.ActiveEntryPopup);
            Assert.Equal("Scratchpad 2 — UAL238", PopupTitle(popup));

            SubmitTyped(popup, "ABC", RawInputModifiers.None);

            Assert.Equal(NotConnected, main.StatusText);
            Assert.Equal("", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }

        var host = new RecordingMenuHost("");
        SubmitUnopened(EuroScopeScratchpadFlyout.Build(new Border(), new AircraftModel { Callsign = "UAL238" }, host, 2), "ABC");
        Assert.Equal(["UAL238 SP2 ABC"], SentCommands(host));
    }

    [AvaloniaFact]
    public async Task EuroScopeHandoffPopup_ATypedSubmitSendsHo()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await OpenEuroScopeAsync(
            ScopeFieldAction.OpenHandoffMenu,
            _ => { }
        );
        try
        {
            Popup popup = Assert.IsType<Popup>(view.ActiveEntryPopup);
            Assert.Equal("Handoff — UAL238", PopupTitle(popup));

            SubmitTyped(popup, "1C", RawInputModifiers.None);

            Assert.Equal(NotConnected, main.StatusText);
            Assert.Equal("", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }

        var host = new RecordingMenuHost("");
        SubmitUnopened(EuroScopeHandoffFlyout.Build(new Border(), new AircraftModel { Callsign = "UAL238" }, host), "1C");
        Assert.Equal(["UAL238 HO 1C"], SentCommands(host));
    }

    /// <summary>Mixed resolves each track to STARS or ERAM only, so its altitude click keeps today's surface.</summary>
    [AvaloniaFact]
    public async Task MixedAltitudeClick_OnAnUnownedTrack_OpensTheEramPopup_NotTheEuroScopeFlyout()
    {
        (_, _, ScopeView view, Window window, _) = await ClickFieldAsync(ScopeTemplates.MixedId, ScopeFieldSlot.EramAltitude, _ => { }, _ => { });
        try
        {
            Assert.IsType<ScopeDataAltitudePopup>(view.ActiveFieldPopup);
            Assert.Null(view.ActiveContextMenu);
            Assert.Null(view.ActiveEntryPopup);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramAndStarsAltitudeClicks_StillOpenTodaysSurfaces()
    {
        (_, _, ScopeView eramView, Window eramWindow, _) = await ClickFieldAsync(
            ScopeTemplates.EramId,
            ScopeFieldSlot.EramAltitude,
            _ => { },
            _ => { }
        );
        try
        {
            Assert.IsType<ScopeDataAltitudePopup>(eramView.ActiveFieldPopup);
            Assert.Null(eramView.ActiveEntryPopup);
        }
        finally
        {
            CloseWindow(eramWindow, eramView);
        }

        (_, _, ScopeView starsView, Window starsWindow, _) = await ClickFieldAsync(
            ScopeTemplates.StarsId,
            ScopeFieldSlot.Altitude,
            _ => { },
            _ => { }
        );
        try
        {
            Assert.Equal(MenuCatalog.Get(MenuIds.AltitudeMaintain).Label, OpenEntry(starsView).Header);
        }
        finally
        {
            CloseWindow(starsWindow, starsView);
        }
    }
}
