using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// A middle click on a Scope block runs a typed ERAM entry for the track, or toggles a STARS block's highlight whatever the
/// preview holds; a left click with text in the preview fills the track into it, opening no menu and toggling no lock; with
/// an empty preview a left click still opens the field's menu.
/// </summary>
public class ScopeMiddleClickTests
{
    private static async Task<(ScopeViewModel Scope, ScopeView View, Window Window, AircraftModel Aircraft)> ShowAsync(
        string templateId,
        string entry
    )
    {
        MainViewModel main = await ScopeTestFixture.MainAsync();
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

        AircraftModel ac = AddAt(main, view.Canvas, "UAL238", 300f);
        main.SelectedAircraft = null;
        scope.TypeEntryText(entry);
        Dispatcher.UIThread.RunJobs();
        return (scope, view, window, ac);
    }

    // A B738 at 5000 ft drawn at (x, 300) on the canvas.
    private static AircraftModel AddAt(MainViewModel main, ScopeCanvas canvas, string callsign, float x)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon(x, 300f);
        var ac = new AircraftModel
        {
            Callsign = callsign,
            AircraftType = "B738",
            FiledAircraftType = "B738",
            FlightRules = "IFR",
            TransponderMode = "C",
            Altitude = 5000,
            GroundSpeed = 250,
            Position = new LatLon(lat, lon),
        };
        main.Aircraft.Add(ac);
        Dispatcher.UIThread.RunJobs();
        return ac;
    }

    private static void ClickField(Window window, ScopeCanvas canvas, ScopeFieldSlot slot, MouseButton button, string callsign)
    {
        FieldRect field = Assert.Single(canvas.CurrentFrame.Tracks, t => t.Aircraft.Callsign == callsign).Block.Fields.First(f => f.Slot == slot);
        ClickCanvas(window, canvas, new Point(field.Rect.MidX, field.Rect.MidY), button);
    }

    // Empty map: far from UAL238's target at (300, 300) and from its block.
    private static readonly Point EmptyMap = new(100, 500);

    private static void ClickCanvas(Window window, ScopeCanvas canvas, Point canvasPoint, MouseButton button)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, button);
        window.MouseUp(inWindow.Value, button);
        Dispatcher.UIThread.RunJobs();
    }

    private static void CloseWindow(Window window, ScopeView view)
    {
        view.ActiveContextMenu?.Close();
        Dispatcher.UIThread.RunJobs();
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task EramBlockMiddleClick_WithQq250InThePreview_FillsTheTrackAndExecutes()
    {
        (ScopeViewModel scope, ScopeView view, Window window, _) = await ShowAsync(ScopeTemplates.EramId, "QQ 250");
        try
        {
            ClickField(window, view.Canvas, ScopeFieldSlot.EramAltitude, MouseButton.Middle, "UAL238");
            await scope.LastEntryRun;

            Assert.Equal("", scope.EntryText);
            ScopeEntryResponse response = Assert.IsType<ScopeEntryResponse>(scope.EntryResponse);
            Assert.Equal("QQ 250 UAL238", response.Echo);
            Assert.Null(view.ActiveContextMenu);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // On Mixed a middle click runs the entry in the clicked track's dialect, whatever is selected.

    [AvaloniaFact]
    public async Task OnMixed_AMiddleClickOnAnEramOwnedTrack_RunsTheEramEntry_WithAStarsOwnedTrackSelected()
    {
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel eram) = await ShowAsync(ScopeTemplates.MixedId, "QQ 250");
        try
        {
            var eramSends = new List<ScopeEramEntryDto>();
            scope.EramEntrySender = dto =>
            {
                eramSends.Add(dto);
                return Task.FromResult(new ScopeEramEntryResultDto(true, ["ACCEPT"], null));
            };
            eram.OwnerSectorCode = "C36";
            AircraftModel stars = AddAt(scope.Main, view.Canvas, "AAL1", 600f);
            stars.OwnerSectorCode = "1A";
            scope.Main.SelectedAircraft = stars;
            Dispatcher.UIThread.RunJobs();

            ClickField(window, view.Canvas, ScopeFieldSlot.EramAltitude, MouseButton.Middle, "UAL238");
            await scope.LastEntryRun;

            ScopeEramEntryDto sent = Assert.Single(eramSends);
            Assert.Equal(["QQ", "250", "UAL238"], sent.Tokens);
            Assert.Equal([new ScopeEramPickDto(2, "UAL238")], sent.Picks);
            Assert.Null(scope.LastSentEntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task OnMixed_AMiddleClickOnAnEramOwnedFieldA_WithAnEmptyPreview_RunsQuickFlight_WithAStarsOwnedTrackSelected()
    {
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel eram) = await ShowAsync(ScopeTemplates.MixedId, "");
        try
        {
            var eramSends = new List<ScopeEramEntryDto>();
            scope.EramEntrySender = dto =>
            {
                eramSends.Add(dto);
                return Task.FromResult(new ScopeEramEntryResultDto(true, ["ACCEPT"], null));
            };
            eram.OwnerSectorCode = "C36";
            AircraftModel stars = AddAt(scope.Main, view.Canvas, "AAL1", 600f);
            stars.OwnerSectorCode = "1A";
            scope.Main.SelectedAircraft = stars;
            Dispatcher.UIThread.RunJobs();

            ClickField(window, view.Canvas, ScopeFieldSlot.Callsign, MouseButton.Middle, "UAL238");
            await scope.LastEntryRun;

            ScopeEramEntryDto sent = Assert.Single(eramSends);
            Assert.Equal(["QF", "UAL238"], sent.Tokens);
            Assert.Equal([new ScopeEramPickDto(1, "UAL238")], sent.Picks);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task OnMixed_AMiddleClickOnAStarsOwnedTrack_TogglesItsHighlight_AndSendsNothing()
    {
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel stars) = await ShowAsync(ScopeTemplates.MixedId, "QQ 250");
        try
        {
            var eramSends = new List<ScopeEramEntryDto>();
            scope.EramEntrySender = dto =>
            {
                eramSends.Add(dto);
                return Task.FromResult(new ScopeEramEntryResultDto(true, ["ACCEPT"], null));
            };
            stars.OwnerSectorCode = "1A";
            Dispatcher.UIThread.RunJobs();

            ClickField(window, view.Canvas, ScopeFieldSlot.Callsign, MouseButton.Middle, "UAL238");

            Assert.Contains("UAL238", scope.DataBlockState.HighlightedCallsigns);
            Assert.Equal("QQ 250", scope.EntryText);
            Assert.Empty(eramSends);
            Assert.Null(scope.LastSentEntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task StarsBlockMiddleClick_TogglesTheHighlight_WithAndWithoutText()
    {
        (ScopeViewModel scope, ScopeView view, Window window, _) = await ShowAsync(ScopeTemplates.StarsId, "");
        try
        {
            ClickField(window, view.Canvas, ScopeFieldSlot.Callsign, MouseButton.Middle, "UAL238");
            Assert.Contains("UAL238", scope.DataBlockState.HighlightedCallsigns);

            scope.TypeEntryText("FH 270");
            ClickField(window, view.Canvas, ScopeFieldSlot.Callsign, MouseButton.Middle, "UAL238");

            Assert.DoesNotContain("UAL238", scope.DataBlockState.HighlightedCallsigns);
            Assert.Equal("FH 270", scope.EntryText);
            Assert.Null(scope.EntryResponse);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramLeftClick_WithText_FillsTheCallsign_OpensNoMenu_AndTogglesNoLock()
    {
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel ac) = await ShowAsync(ScopeTemplates.EramId, "FH 270");
        try
        {
            ClickField(window, view.Canvas, ScopeFieldSlot.EramAltitude, MouseButton.Left, "UAL238");

            Assert.Equal("UAL238 FH 270", scope.EntryText);
            Assert.Null(view.ActiveContextMenu);
            Assert.Same(ac, scope.Main.SelectedAircraft);

            // Field A of another track: the fill names it in place of UAL238, and its dwell lock is not toggled.
            AircraftModel other = AddAt(scope.Main, view.Canvas, "DAL1", 550f);
            ClickField(window, view.Canvas, ScopeFieldSlot.Callsign, MouseButton.Left, "DAL1");

            Assert.Equal("DAL1 FH 270", scope.EntryText);
            Assert.Empty(scope.DataBlockState.DwellLockedCallsigns);
            Assert.Null(view.ActiveContextMenu);
            Assert.Same(other, scope.Main.SelectedAircraft);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // Keeps each ERAM entry the Scope sends, each answered ACCEPT.
    private static List<ScopeEramEntryDto> CaptureEramSends(ScopeViewModel scope)
    {
        var sent = new List<ScopeEramEntryDto>();
        scope.EramEntrySender = dto =>
        {
            sent.Add(dto);
            return Task.FromResult(new ScopeEramEntryResultDto(true, ["ACCEPT"], null));
        };
        return sent;
    }

    private static RawInputModifiers ActionModifier => PlatformHelper.IsMacOS ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.DispatchKey(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task EnterAsync(Window window, ScopeViewModel scope)
    {
        Press(window, Key.Enter);
        await scope.LastEntryRun;
    }

    private static void TypeKeys(Window window, string text)
    {
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    // A track a left click fills into an ERAM entry goes to the server as a pick of that aircraft, its text kept, until the
    // entry leaves the preview: an edit of its callsign, Esc, a sent entry or a recalled line drop the pick, and the Up/Down
    // round trip back to the half-typed entry keeps it.

    [AvaloniaFact]
    public async Task ALeftClickedTrack_InAnEramEntry_IsSentAsAPick()
    {
        (ScopeViewModel scope, ScopeView view, Window window, _) = await ShowAsync(ScopeTemplates.EramId, "QQ 250");
        try
        {
            List<ScopeEramEntryDto> sent = CaptureEramSends(scope);
            ClickField(window, view.Canvas, ScopeFieldSlot.Callsign, MouseButton.Left, "UAL238");
            Assert.Equal("QQ 250 UAL238", scope.EntryText);

            await EnterAsync(window, scope);

            ScopeEramEntryDto dto = Assert.Single(sent);
            Assert.Equal(["QQ", "250", "UAL238"], dto.Tokens);
            Assert.Equal([new ScopeEramPickDto(2, "UAL238")], dto.Picks);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task ALeftClickedTrack_EditedBeforeEnter_IsSentTyped_WithNoPick()
    {
        (ScopeViewModel scope, ScopeView view, Window window, _) = await ShowAsync(ScopeTemplates.EramId, "QQ 250");
        try
        {
            List<ScopeEramEntryDto> sent = CaptureEramSends(scope);
            ClickField(window, view.Canvas, ScopeFieldSlot.Callsign, MouseButton.Left, "UAL238");
            Press(window, Key.Back);
            Assert.Equal("QQ 250 UAL23", scope.EntryText);

            await EnterAsync(window, scope);

            ScopeEramEntryDto dto = Assert.Single(sent);
            Assert.Equal(["QQ", "250", "UAL23"], dto.Tokens);
            Assert.Null(dto.Picks);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task ALeftClickedTrack_IsNotPickedAgain_WhenTheNextEntryTypesItsCallsign()
    {
        (ScopeViewModel scope, ScopeView view, Window window, _) = await ShowAsync(ScopeTemplates.EramId, "QQ 250");
        try
        {
            List<ScopeEramEntryDto> sent = CaptureEramSends(scope);
            ClickField(window, view.Canvas, ScopeFieldSlot.Callsign, MouseButton.Left, "UAL238");
            await EnterAsync(window, scope);
            TypeKeys(window, "QQ 250 UAL238");

            await EnterAsync(window, scope);

            Assert.Equal(2, sent.Count);
            Assert.Equal(["QQ", "250", "UAL238"], sent[1].Tokens);
            Assert.Null(sent[1].Picks);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task ALeftClickedTrack_ClearedByEsc_IsNotPicked_WhenRetyped()
    {
        (ScopeViewModel scope, ScopeView view, Window window, _) = await ShowAsync(ScopeTemplates.EramId, "QQ 250");
        try
        {
            List<ScopeEramEntryDto> sent = CaptureEramSends(scope);
            ClickField(window, view.Canvas, ScopeFieldSlot.Callsign, MouseButton.Left, "UAL238");
            Press(window, Key.Escape);
            Assert.Equal("", scope.EntryText);
            TypeKeys(window, "QQ 250 UAL238");

            await EnterAsync(window, scope);

            Assert.Null(Assert.Single(sent).Picks);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task ALeftClickedTrack_KeepsItsPick_ThroughUpAndBackDown()
    {
        (ScopeViewModel scope, ScopeView view, Window window, _) = await ShowAsync(ScopeTemplates.EramId, "QT UAL238");
        try
        {
            List<ScopeEramEntryDto> sent = CaptureEramSends(scope);
            Assert.True(scope.ExecuteEntry());
            await scope.LastEntryRun;
            scope.TypeEntryText("QQ 250");
            ClickField(window, view.Canvas, ScopeFieldSlot.Callsign, MouseButton.Left, "UAL238");
            Press(window, Key.Up);
            Assert.Equal("QT UAL238", scope.EntryText);
            Press(window, Key.Down);
            Assert.Equal("QQ 250 UAL238", scope.EntryText);

            await EnterAsync(window, scope);

            Assert.Equal(2, sent.Count);
            Assert.Equal([new ScopeEramPickDto(2, "UAL238")], sent[1].Picks);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task ALeftClickedTrack_IsNotPicked_InAnOlderLineUpRecalls()
    {
        (ScopeViewModel scope, ScopeView view, Window window, _) = await ShowAsync(ScopeTemplates.EramId, "QT UAL238");
        try
        {
            List<ScopeEramEntryDto> sent = CaptureEramSends(scope);
            Assert.True(scope.ExecuteEntry());
            await scope.LastEntryRun;
            scope.TypeEntryText("QQ 250");
            ClickField(window, view.Canvas, ScopeFieldSlot.Callsign, MouseButton.Left, "UAL238");
            Press(window, Key.Up);
            Assert.Equal("QT UAL238", scope.EntryText);

            await EnterAsync(window, scope);

            Assert.Equal(2, sent.Count);
            Assert.Equal(["QT", "UAL238"], sent[1].Tokens);
            Assert.Null(sent[1].Picks);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task ALeftClickedTrack_IsNotPicked_InALineCtrlRRecalls()
    {
        (ScopeViewModel scope, ScopeView view, Window window, _) = await ShowAsync(ScopeTemplates.EramId, "QT UAL238");
        try
        {
            List<ScopeEramEntryDto> sent = CaptureEramSends(scope);
            Assert.True(scope.ExecuteEntry());
            await scope.LastEntryRun;
            scope.TypeEntryText("QQ 250");
            ClickField(window, view.Canvas, ScopeFieldSlot.Callsign, MouseButton.Left, "UAL238");
            Press(window, Key.R, ActionModifier);
            Assert.Equal("QT UAL238", scope.EntryText);

            await EnterAsync(window, scope);

            Assert.Equal(2, sent.Count);
            Assert.Null(sent[1].Picks);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramLeftClick_WithAnEmptyPreview_StillOpensTheFieldMenu()
    {
        (ScopeViewModel scope, ScopeView view, Window window, _) = await ShowAsync(ScopeTemplates.EramId, "");
        try
        {
            ClickField(window, view.Canvas, ScopeFieldSlot.EramAltitude, MouseButton.Left, "UAL238");

            ContextMenu menu = Assert.IsType<ContextMenu>(view.ActiveContextMenu);
            MenuItem entry = Assert.IsType<MenuItem>(Assert.Single(menu.Items));
            Assert.Equal(MenuCatalog.Get(MenuIds.AltitudeMaintain).Label, entry.Header);
            Assert.Equal("", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    /// <summary>Ruling (A): the pilot entry already names UAL238 first, so a click on it acts as with an empty preview.</summary>
    [AvaloniaFact]
    public async Task EramLeftClick_OnTheTrackThePilotEntryNames_ActsAsWithAnEmptyPreview()
    {
        (ScopeViewModel scope, ScopeView view, Window window, _) = await ShowAsync(ScopeTemplates.EramId, "PILOT UAL238 FH 270");
        try
        {
            ClickField(window, view.Canvas, ScopeFieldSlot.Callsign, MouseButton.Left, "UAL238");

            Assert.Contains("UAL238", scope.DataBlockState.DwellLockedCallsigns);
            Assert.Equal("PILOT UAL238 FH 270", scope.EntryText);

            ClickField(window, view.Canvas, ScopeFieldSlot.EramAltitude, MouseButton.Left, "UAL238");

            ContextMenu menu = Assert.IsType<ContextMenu>(view.ActiveContextMenu);
            MenuItem entry = Assert.IsType<MenuItem>(Assert.Single(menu.Items));
            Assert.Equal(MenuCatalog.Get(MenuIds.AltitudeMaintain).Label, entry.Header);
            Assert.Equal("PILOT UAL238 FH 270", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramLeftClickOnTheMap_WithText_FillsThePoint()
    {
        (ScopeViewModel scope, ScopeView view, Window window, _) = await ShowAsync(ScopeTemplates.EramId, "QU");
        try
        {
            ClickCanvas(window, view.Canvas, EmptyMap, MouseButton.Left);

            Assert.StartsWith("QU ", scope.EntryText, StringComparison.Ordinal);
            // An FRD (a fix name, with its radial and distance when off it) or the ERAM lat/long.
            Assert.Matches(@"^QU (//\d{4}[NS]/\d{5}[EW]|[A-Z0-9]+)$", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task StarsMiddleClickOnTheMap_WithText_LeavesTheEntry()
    {
        (ScopeViewModel scope, ScopeView view, Window window, _) = await ShowAsync(ScopeTemplates.StarsId, "");
        try
        {
            scope.ProfileKind = ScopeProfileKind.Stars;
            scope.TypeEntryText("HND");
            Dispatcher.UIThread.RunJobs();

            ClickCanvas(window, view.Canvas, EmptyMap, MouseButton.Middle);

            Assert.Equal("HND", scope.EntryText);
            Assert.Null(scope.EntryResponse);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }
}
