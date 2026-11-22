using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.FieldMenus;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// A middle click on a Scope block runs a typed ERAM entry for the track, or toggles a STARS block's highlight whatever the
/// preview holds; a left click with text in the preview fills the track into it, opening no menu and toggling no lock; with
/// an empty preview a left click still opens the field's menu, and on an ERAM block's HSF indicator or VCI cell runs its toggle.
/// </summary>
public class ScopeMiddleClickTests
{
    private static async Task<(ScopeViewModel Scope, ScopeView View, Window Window, AircraftModel Aircraft)> ShowAsync(
        string templateId,
        string entry
    )
    {
        MainViewModel main = await ScopeTestFixture.MainAsync();
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot) { TemplateId = templateId };
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

        AircraftModel ac = AddAt(main, view.Canvas, "UAL238", 300f, 300f);
        main.SelectedAircraft = null;
        scope.TypeEntryText(entry);
        Dispatcher.UIThread.RunJobs();
        return (scope, view, window, ac);
    }

    // A B738 at 5000 ft drawn at (x, y) on the canvas.
    private static AircraftModel AddAt(MainViewModel main, ScopeCanvas canvas, string callsign, float x, float y)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon(x, y);
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
        view.ActiveFieldPopup?.Close();
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

    /// <summary>A <c>$</c> command middle-clicked onto an ERAM block goes to the command pipeline for that track, never to ERAM.</summary>
    [AvaloniaFact]
    public async Task EramBlockMiddleClick_WithADollarCommand_SendsItForTheTrack_NotToTheEramDispatch()
    {
        (ScopeViewModel scope, ScopeView view, Window window, _) = await ShowAsync(ScopeTemplates.EramId, "$FH 270");
        try
        {
            var eramSends = new List<ScopeEramEntryDto>();
            scope.EramEntrySender = dto =>
            {
                eramSends.Add(dto);
                return Task.FromResult(new ScopeEramEntryResultDto(true, ["ACCEPT"], null));
            };

            ClickField(window, view.Canvas, ScopeFieldSlot.EramAltitude, MouseButton.Middle, "UAL238");
            await scope.LastEntryRun;

            Assert.Equal("UAL238 FH 270", scope.LastSentEntryText);
            Assert.Empty(eramSends);
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
            eram.Owner = "OAK_36_CTR";
            eram.OwnerSectorCode = "C36";
            AircraftModel stars = AddAt(scope.Main, view.Canvas, "AAL1", 600f, 300f);
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
            eram.Owner = "OAK_36_CTR";
            eram.OwnerSectorCode = "C36";
            AircraftModel stars = AddAt(scope.Main, view.Canvas, "AAL1", 600f, 300f);
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
    public async Task OnMixed_AMiddleClickOnAnUnownedTrackAt40Nm_TogglesItsHighlight_AndSendsNothing()
    {
        (ScopeViewModel scope, ScopeView view, Window window, _) = await ShowAsync(ScopeTemplates.MixedId, "QQ 250");
        try
        {
            var eramSends = new List<ScopeEramEntryDto>();
            scope.EramEntrySender = dto =>
            {
                eramSends.Add(dto);
                return Task.FromResult(new ScopeEramEntryResultDto(true, ["ACCEPT"], null));
            };
            // Up to 120 NM Mixed draws an unowned track as STARS.
            scope.RangeNm = 40;
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

            scope.TypeEntryText("$FH 270");
            ClickField(window, view.Canvas, ScopeFieldSlot.Callsign, MouseButton.Middle, "UAL238");

            Assert.DoesNotContain("UAL238", scope.DataBlockState.HighlightedCallsigns);
            Assert.Equal("$FH 270", scope.EntryText);
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
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel ac) = await ShowAsync(ScopeTemplates.EramId, "$FH 270");
        try
        {
            ClickField(window, view.Canvas, ScopeFieldSlot.EramAltitude, MouseButton.Left, "UAL238");

            Assert.Equal("$UAL238 FH 270", scope.EntryText);
            Assert.Null(view.ActiveContextMenu);
            Assert.Same(ac, scope.Main.SelectedAircraft);

            // Field A of another track: the fill names it in place of UAL238, and its dwell lock is not toggled.
            AircraftModel other = AddAt(scope.Main, view.Canvas, "DAL1", 550f, 300f);
            ClickField(window, view.Canvas, ScopeFieldSlot.Callsign, MouseButton.Left, "DAL1");

            Assert.Equal("$DAL1 FH 270", scope.EntryText);
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

    // Gives UAL238 an HSF speed alone, so line 4 draws the speed token, and lets the canvas build its next frame.
    private static void SetHsfSpeed(ScopeView view, AircraftModel ac)
    {
        ac.EramAssignedSpeed = "S250";
        view.Canvas.MarkDirty();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task EramMiddleClickOnTheSpeedToken_WithAnEmptyPreview_OpensTheSpeedPopup()
    {
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel ac) = await ShowAsync(ScopeTemplates.EramId, "");
        try
        {
            SetHsfSpeed(view, ac);

            ClickField(window, view.Canvas, ScopeFieldSlot.Line4Speed, MouseButton.Middle, "UAL238");

            ScopeDataHsfPopup popup = Assert.IsType<ScopeDataHsfPopup>(view.ActiveFieldPopup);
            Assert.Equal(HsfPopupKind.Speed, popup.Kind);
            Assert.True(popup.IsOpen);
            Assert.Equal("", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramMiddleClickOnTheSpeedToken_WithQq250InThePreview_FillsTheTrackAndExecutes()
    {
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel ac) = await ShowAsync(ScopeTemplates.EramId, "QQ 250");
        try
        {
            SetHsfSpeed(view, ac);

            ClickField(window, view.Canvas, ScopeFieldSlot.Line4Speed, MouseButton.Middle, "UAL238");
            await scope.LastEntryRun;

            Assert.Equal("QQ 250 UAL238", Assert.IsType<ScopeEntryResponse>(scope.EntryResponse).Echo);
            Assert.Null(view.ActiveFieldPopup);
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

            Assert.True(Assert.IsType<ScopeDataAltitudePopup>(view.ActiveFieldPopup).IsOpen);
            Assert.Equal("", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    /// <summary>Ruling (A): the <c>$</c> entry already names UAL238 first, so a click on it acts as with an empty preview.</summary>
    [AvaloniaFact]
    public async Task EramLeftClick_OnTheTrackThePilotEntryNames_ActsAsWithAnEmptyPreview()
    {
        (ScopeViewModel scope, ScopeView view, Window window, _) = await ShowAsync(ScopeTemplates.EramId, "$ UAL238 FH 270");
        try
        {
            ClickField(window, view.Canvas, ScopeFieldSlot.Callsign, MouseButton.Left, "UAL238");

            Assert.Contains("UAL238", scope.DataBlockState.DwellLockedCallsigns);
            Assert.Equal("$ UAL238 FH 270", scope.EntryText);

            ClickField(window, view.Canvas, ScopeFieldSlot.EramAltitude, MouseButton.Left, "UAL238");

            Assert.True(Assert.IsType<ScopeDataAltitudePopup>(view.ActiveFieldPopup).IsOpen);
            Assert.Equal("$ UAL238 FH 270", scope.EntryText);
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

    // A left click on an ERAM FDB's HSF indicator or VCI cell runs its ERAM toggle straight through, as CRC's datablock does.

    private static readonly ActivePositionDto Sector44 = new(
        Callsign: "OAK_44_CTR",
        Name: "Sector 44",
        RadioName: null,
        Frequency: "135.100",
        FacilityId: "ZOA",
        FacilityName: "Oakland Center",
        PositionType: "Artcc",
        HandoffCode: "C44",
        EramSectorId: "44"
    );

    // Sector 44 views UAL238 as a full block with its VCI lit (on 44's frequency) and an HSF heading, so both marks are drawn.
    private static List<ScopeEramEntryDto> LightHsfAndVci(ScopeViewModel scope, AircraftModel ac, ScopeEramEntryResultDto answer, bool onFrequency)
    {
        var sends = new List<ScopeEramEntryDto>();
        scope.EramEntrySender = dto =>
        {
            sends.Add(dto);
            return Task.FromResult(answer);
        };
        scope.Main.SetActivePositionFromServer(new PositionDisplayConfigDto([], [], [], "C44", Sector44, 3, NoStarsDisplay: true));
        scope.PinOthersLevel(ScopeProfile.OthersFull);
        ac.EramOnFrequencySectorIds = onFrequency ? ["44"] : [];
        ac.EramAssignedHeading = "H270";
        Dispatcher.UIThread.RunJobs();
        return sends;
    }

    [AvaloniaFact]
    public async Task EramBlockLeftClick_OnTheHsfIndicator_RunsQsWithThePick_AndShowsTheAnswer()
    {
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel ac) = await ShowAsync(ScopeTemplates.EramId, "");
        try
        {
            List<ScopeEramEntryDto> sends = LightHsfAndVci(scope, ac, new ScopeEramEntryResultDto(true, ["ACCEPT"], null), onFrequency: true);

            ClickField(window, view.Canvas, ScopeFieldSlot.HsfIndicator, MouseButton.Left, "UAL238");
            await scope.LastEntryRun;

            ScopeEramEntryDto sent = Assert.Single(sends);
            Assert.Equal(["QS", "UAL238"], sent.Tokens);
            Assert.Equal([new ScopeEramPickDto(1, "UAL238")], sent.Picks);
            Assert.Equal("", scope.EntryText);
            ScopeEntryResponse response = Assert.IsType<ScopeEntryResponse>(scope.EntryResponse);
            Assert.Equal(ScopeResponseKind.Accept, response.Kind);
            Assert.Equal("ACCEPT", response.Message);
            Assert.Equal("QS UAL238", response.Echo);
            Assert.Null(view.ActiveContextMenu);
            // As CRC's, the click adds nothing to the entry history, so Up has nothing to recall.
            Assert.False(scope.WalkEntryHistory(older: true));
            Assert.Equal("", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // CRC picks the on-frequency indicator's bounds whether or not the VCI is lit, so a click on an unlit cell (previewed by
    // the hover that precedes it, which places no click target in the block) still sends the toggle.
    [AvaloniaFact]
    public async Task EramBlockLeftClick_OnAnUnlitVciCell_RunsQnSlashSlashWithThePick()
    {
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel ac) = await ShowAsync(ScopeTemplates.EramId, "");
        try
        {
            List<ScopeEramEntryDto> sends = LightHsfAndVci(scope, ac, new ScopeEramEntryResultDto(true, ["ACCEPT"], null), onFrequency: false);
            ScopeTrack track = Assert.Single(view.Canvas.CurrentFrame.Tracks);
            Assert.DoesNotContain(track.Block.Fields, f => f.Slot == ScopeFieldSlot.Vci);
            SKRect cell = Assert.NotNull(track.Block.VciCell);
            Point? onCell = view.Canvas.TranslatePoint(new Point(cell.Left - 4f, cell.Top - 2f), window);
            Assert.NotNull(onCell);
            window.MouseMove(onCell.Value);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("UAL238", view.Canvas.HoveredVciCallsign);

            window.MouseDown(onCell.Value, MouseButton.Left);
            window.MouseUp(onCell.Value, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            await scope.LastEntryRun;

            ScopeEramEntryDto sent = Assert.Single(sends);
            Assert.Equal(["QN", "//", "UAL238"], sent.Tokens);
            Assert.Equal([new ScopeEramPickDto(2, "UAL238")], sent.Picks);
            Assert.Equal("ACCEPT", Assert.IsType<ScopeEntryResponse>(scope.EntryResponse).Message);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // CRC ranks picks in two tiers, a dwell-locked block above a plain one (a hover alone ranks nothing: CRC's move walk resets
    // the dwell first), and within one tier the HSF indicator, then the VCI indicator, beat a block's fields; the instructor's
    // selection is no tier. DAL238's Field A is placed over UAL238's unlit VCI box: only a dwell-locked DAL238 over a plain
    // UAL238 keeps the press (its Field A click toggles its lock off) and sends nothing; a plain, hovered or selected DAL238, or
    // one over a dwell-locked UAL238, loses it to the VCI toggle.
    [AvaloniaTheory]
    [InlineData("plain")]
    [InlineData("locked")]
    [InlineData("hovered")]
    [InlineData("selected")]
    [InlineData("ownerLocked")]
    public async Task EramLeftClick_OnAFieldAOverAnotherBlocksVciBox_GoesByDwellLockTier(string tier)
    {
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel vciOwner) = await ShowAsync(ScopeTemplates.EramId, "");
        try
        {
            List<ScopeEramEntryDto> sends = LightHsfAndVci(scope, vciOwner, new ScopeEramEntryResultDto(true, ["ACCEPT"], null), onFrequency: false);
            if (tier == "ownerLocked")
            {
                scope.ToggleDwellLock("UAL238");
                Dispatcher.UIThread.RunJobs();
            }

            // DAL238's symbol is placed so its callsign's centre lands inside UAL238's VCI hover box.
            AircraftModel fieldOwner = AddOverVciBox(scope, view.Canvas, ScopeFieldSlot.Callsign);
            ApplyTier(scope, window, view.Canvas, tier, fieldOwner);
            ScopeFrame frame = view.Canvas.CurrentFrame;
            FieldRect fieldA = Assert
                .Single(frame.Tracks, t => t.Aircraft.Callsign == "DAL238")
                .Block.Fields.First(f => f.Slot == ScopeFieldSlot.Callsign);
            bool blockWins = tier == "locked";
            // The hover previews the VCI exactly where the press toggles it.
            Assert.Same(blockWins ? null : vciOwner, frame.HoverAt(fieldA.Rect.MidX, fieldA.Rect.MidY).VciPreviewed);

            ClickField(window, view.Canvas, ScopeFieldSlot.Callsign, MouseButton.Left, "DAL238");
            await scope.LastEntryRun;

            if (blockWins)
            {
                Assert.Empty(sends);
                Assert.Same(fieldOwner, scope.Main.SelectedAircraft);
                // The press went to DAL238's Field A, which toggles its dwell lock off.
                Assert.Empty(scope.DataBlockState.DwellLockedCallsigns);
            }
            else
            {
                AssertVciToggled(sends, "UAL238");
                string[] locks = tier == "ownerLocked" ? ["UAL238"] : [];
                Assert.Equal(locks, scope.DataBlockState.DwellLockedCallsigns);
            }
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // Puts DAL238 in the row's tier: the instructor's local dwell lock, the pointer on its symbol (dwelled, not locked), or the
    // instructor's selection; plain and ownerLocked leave it plain.
    private static void ApplyTier(ScopeViewModel scope, Window window, ScopeCanvas canvas, string tier, AircraftModel fieldOwner)
    {
        switch (tier)
        {
            case "locked":
                scope.ToggleDwellLock("DAL238");
                break;
            case "hovered":
                ScopeTrack track = Assert.Single(canvas.CurrentFrame.Tracks, t => t.Aircraft.Callsign == "DAL238");
                Point? onSymbol = canvas.TranslatePoint(new Point(track.Symbol.X, track.Symbol.Y), window);
                Assert.NotNull(onSymbol);
                window.MouseMove(onSymbol.Value);
                break;
            case "selected":
                scope.Main.SelectedAircraft = fieldOwner;
                break;
        }

        Dispatcher.UIThread.RunJobs();
        if (tier == "hovered")
        {
            Assert.Equal("DAL238", canvas.HoveredCallsign);
            Assert.Empty(scope.DataBlockState.DwellLockedCallsigns);
        }
    }

    // DAL238, placed so the centre of its field in `slot` lands inside UAL238's unlit VCI hover box, at UAL238's VciTarget.
    private static AircraftModel AddOverVciBox(ScopeViewModel scope, ScopeCanvas canvas, ScopeFieldSlot slot)
    {
        ScopeTrack b = Assert.Single(canvas.CurrentFrame.Tracks);
        SKRect field = b.Block.Fields.First(f => f.Slot == slot).Rect;
        return AddWithPointAt(scope, canvas, b, new SKPoint(field.MidX, field.MidY));
    }

    // DAL238, placed so the point `onBlock` of a block laid out as `b`'s lands on b's VCI target.
    private static AircraftModel AddWithPointAt(ScopeViewModel scope, ScopeCanvas canvas, ScopeTrack b, SKPoint onBlock)
    {
        SKPoint symbol = VciTarget(b) - (onBlock - b.Symbol);
        return AddAt(scope.Main, canvas, "DAL238", symbol.X, symbol.Y);
    }

    // A point inside the track's VCI hover box, off its cell: 4 px left of it and 2 px above it.
    private static SKPoint VciTarget(ScopeTrack track)
    {
        SKRect cell = Assert.NotNull(track.Block.VciCell);
        return new SKPoint(cell.Left - 4f, cell.Top - 2f);
    }

    private static void AssertVciToggled(List<ScopeEramEntryDto> sends, string callsign)
    {
        ScopeEramEntryDto sent = Assert.Single(sends);
        Assert.Equal(["QN", "//", callsign], sent.Tokens);
        Assert.Equal([new ScopeEramPickDto(2, callsign)], sent.Picks);
    }

    // The emphasised area of a dwell-locked block is its dwell box, the fields without column 0: a press in the locked block's
    // column 0, outside that box, over a plain block's VCI box toggles that VCI.
    [AvaloniaFact]
    public async Task EramLeftClick_InALockedBlocksColumn0OutsideItsDwellBox_OverAPlainVciBox_TogglesTheVci()
    {
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel vciOwner) = await ShowAsync(ScopeTemplates.EramId, "");
        try
        {
            List<ScopeEramEntryDto> sends = LightHsfAndVci(scope, vciOwner, new ScopeEramEntryResultDto(true, ["ACCEPT"], null), onFrequency: false);
            ScopeTrack b = Assert.Single(view.Canvas.CurrentFrame.Tracks);
            SKRect bCallsign = b.Block.Fields.First(f => f.Slot == ScopeFieldSlot.Callsign).Rect;
            // Beside Field A in column 0 (widened by the R): inside the block bounds, left of the dwell box, above the VCI cell.
            var inColumn0 = new SKPoint((b.Block.Bounds.Left + b.DwellBox.Left) / 2f, bCallsign.MidY);
            SKPoint target = VciTarget(b);
            AddWithPointAt(scope, view.Canvas, b, inColumn0);
            // Both blocks sit at one offset on purpose, so the column-0 point measured on b's block lands on DAL238's.
            scope.DataBlockState.SetManualOffset("DAL238", b.BlockOffset);
            scope.ToggleDwellLock("DAL238");
            Dispatcher.UIThread.RunJobs();

            ScopeFrame frame = view.Canvas.CurrentFrame;
            ScopeTrack locked = Assert.Single(frame.Tracks, t => t.Aircraft.Callsign == "DAL238");
            Assert.True(locked.Block.Bounds.Contains(target.X, target.Y));
            Assert.False(locked.DwellBox.Contains(target.X, target.Y));
            Assert.Same(vciOwner, frame.HoverAt(target.X, target.Y).VciPreviewed);

            ClickCanvas(window, view.Canvas, new Point(target.X, target.Y), MouseButton.Left);
            await scope.LastEntryRun;

            AssertVciToggled(sends, "UAL238");
            Assert.Equal(["DAL238"], scope.DataBlockState.DwellLockedCallsigns);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // Within one tier CRC picks the HSF indicator before the on-frequency indicator: DAL238's HSF indicator over plain UAL238's
    // VCI box takes the press, QS with DAL238 as the pick.
    [AvaloniaFact]
    public async Task EramLeftClick_OnAnHsfIndicatorOverAnotherBlocksVciBox_SameTier_RunsQsWithThePick()
    {
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel vciOwner) = await ShowAsync(ScopeTemplates.EramId, "");
        try
        {
            List<ScopeEramEntryDto> sends = LightHsfAndVci(scope, vciOwner, new ScopeEramEntryResultDto(true, ["ACCEPT"], null), onFrequency: false);
            SKPoint target = VciTarget(Assert.Single(view.Canvas.CurrentFrame.Tracks));
            AircraftModel hsfOwner = AddOverVciBox(scope, view.Canvas, ScopeFieldSlot.HsfIndicator);
            hsfOwner.EramAssignedHeading = "H270";
            Dispatcher.UIThread.RunJobs();

            ScopeFrame frame = view.Canvas.CurrentFrame;
            FieldRect hsf = Assert
                .Single(frame.Tracks, t => t.Aircraft.Callsign == "DAL238")
                .Block.Fields.First(f => f.Slot == ScopeFieldSlot.HsfIndicator);
            Assert.True(hsf.Rect.Contains(target.X, target.Y));
            // The HSF indicator takes the point from the VCI box under it: the hover previews no VCI where the press runs the HSF.
            Assert.Null(frame.HoverAt(target.X, target.Y).VciPreviewed);

            ClickCanvas(window, view.Canvas, new Point(target.X, target.Y), MouseButton.Left);
            await scope.LastEntryRun;

            ScopeEramEntryDto sent = Assert.Single(sends);
            Assert.Equal(["QS", "DAL238"], sent.Tokens);
            Assert.Equal([new ScopeEramPickDto(1, "DAL238")], sent.Picks);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // Both blocks dwell-locked, one tier: DAL238's HSF indicator over UAL238's VCI box takes the hover and the press from it.
    [AvaloniaFact]
    public async Task EramLeftClick_OnALockedHsfIndicatorOverALockedBlocksVciBox_RunsQsWithThePick()
    {
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel vciOwner) = await ShowAsync(ScopeTemplates.EramId, "");
        try
        {
            List<ScopeEramEntryDto> sends = LightHsfAndVci(scope, vciOwner, new ScopeEramEntryResultDto(true, ["ACCEPT"], null), onFrequency: false);
            scope.ToggleDwellLock("UAL238");
            Dispatcher.UIThread.RunJobs();
            // Both blocks are laid out at Dwell, so the locked UAL238's layout places DAL238's HSF indicator.
            SKPoint target = VciTarget(Assert.Single(view.Canvas.CurrentFrame.Tracks));
            AircraftModel hsfOwner = AddOverVciBox(scope, view.Canvas, ScopeFieldSlot.HsfIndicator);
            hsfOwner.EramAssignedHeading = "H270";
            scope.ToggleDwellLock("DAL238");
            Dispatcher.UIThread.RunJobs();

            ScopeFrame frame = view.Canvas.CurrentFrame;
            FieldRect hsf = Assert
                .Single(frame.Tracks, t => t.Aircraft.Callsign == "DAL238")
                .Block.Fields.First(f => f.Slot == ScopeFieldSlot.HsfIndicator);
            Assert.True(hsf.Rect.Contains(target.X, target.Y));
            Assert.Null(frame.HoverAt(target.X, target.Y).VciPreviewed);

            ClickCanvas(window, view.Canvas, new Point(target.X, target.Y), MouseButton.Left);
            await scope.LastEntryRun;

            ScopeEramEntryDto sent = Assert.Single(sends);
            Assert.Equal(["QS", "DAL238"], sent.Tokens);
            Assert.Equal([new ScopeEramPickDto(1, "DAL238")], sent.Picks);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // The HSF indicator wins only in the VCI owner's tier or above: a plain DAL238's HSF indicator over a dwell-locked UAL238's
    // VCI box loses the press, and the hover, to the VCI.
    [AvaloniaFact]
    public async Task EramLeftClick_OnAPlainHsfIndicatorOverALockedBlocksVciBox_TogglesTheVci()
    {
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel vciOwner) = await ShowAsync(ScopeTemplates.EramId, "");
        try
        {
            List<ScopeEramEntryDto> sends = LightHsfAndVci(scope, vciOwner, new ScopeEramEntryResultDto(true, ["ACCEPT"], null), onFrequency: false);
            // The HSF indicator's place in a plain block, taken before the lock relays UAL238's block out at Dwell.
            SKRect plainHsf = Assert.Single(view.Canvas.CurrentFrame.Tracks).Block.Fields.First(f => f.Slot == ScopeFieldSlot.HsfIndicator).Rect;
            scope.ToggleDwellLock("UAL238");
            Dispatcher.UIThread.RunJobs();
            ScopeTrack locked = Assert.Single(view.Canvas.CurrentFrame.Tracks);
            SKPoint target = VciTarget(locked);
            AircraftModel hsfOwner = AddWithPointAt(scope, view.Canvas, locked, new SKPoint(plainHsf.MidX, plainHsf.MidY));
            hsfOwner.EramAssignedHeading = "H270";
            Dispatcher.UIThread.RunJobs();

            ScopeFrame frame = view.Canvas.CurrentFrame;
            FieldRect hsf = Assert
                .Single(frame.Tracks, t => t.Aircraft.Callsign == "DAL238")
                .Block.Fields.First(f => f.Slot == ScopeFieldSlot.HsfIndicator);
            Assert.True(hsf.Rect.Contains(target.X, target.Y));
            Assert.Same(vciOwner, frame.HoverAt(target.X, target.Y).VciPreviewed);

            ClickCanvas(window, view.Canvas, new Point(target.X, target.Y), MouseButton.Left);
            await scope.LastEntryRun;

            AssertVciToggled(sends, "UAL238");
            Assert.Equal(["UAL238"], scope.DataBlockState.DwellLockedCallsigns);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // As CRC's on-frequency indicator pick: QN // with the clicked track as a pick.
    [AvaloniaFact]
    public async Task EramBlockLeftClick_OnTheVciCell_RunsQnSlashSlashWithThePick_AndShowsTheRefusal()
    {
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel ac) = await ShowAsync(ScopeTemplates.EramId, "");
        try
        {
            List<ScopeEramEntryDto> sends = LightHsfAndVci(
                scope,
                ac,
                new ScopeEramEntryResultDto(false, ["NOT YOUR CONTROL"], null),
                onFrequency: true
            );

            ClickField(window, view.Canvas, ScopeFieldSlot.Vci, MouseButton.Left, "UAL238");
            await scope.LastEntryRun;

            ScopeEramEntryDto sent = Assert.Single(sends);
            Assert.Equal(["QN", "//", "UAL238"], sent.Tokens);
            Assert.Equal([new ScopeEramPickDto(2, "UAL238")], sent.Picks);
            Assert.Equal("", scope.EntryText);
            ScopeEntryResponse response = Assert.IsType<ScopeEntryResponse>(scope.EntryResponse);
            Assert.Equal(ScopeResponseKind.Error, response.Kind);
            Assert.Equal("NOT YOUR CONTROL", response.Message);
            Assert.Equal("QN // UAL238", response.Echo);
            Assert.False(scope.WalkEntryHistory(older: true));
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // A middle click on an ERAM FDB with an empty preview does what a plain left click does on the HSF or VCI cell.

    [AvaloniaFact]
    public async Task EramBlockMiddleClick_OnTheHsfIndicator_WithAnEmptyPreview_RunsQsWithThePick()
    {
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel ac) = await ShowAsync(ScopeTemplates.EramId, "");
        try
        {
            List<ScopeEramEntryDto> sends = LightHsfAndVci(scope, ac, new ScopeEramEntryResultDto(true, ["ACCEPT"], null), onFrequency: true);

            ClickField(window, view.Canvas, ScopeFieldSlot.HsfIndicator, MouseButton.Middle, "UAL238");
            await scope.LastEntryRun;

            ScopeEramEntryDto sent = Assert.Single(sends);
            Assert.Equal(["QS", "UAL238"], sent.Tokens);
            Assert.Equal([new ScopeEramPickDto(1, "UAL238")], sent.Picks);
            Assert.Equal("", scope.EntryText);
            Assert.Equal("QS UAL238", Assert.IsType<ScopeEntryResponse>(scope.EntryResponse).Echo);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // A middle click ignores Shift: on the HSF cell it runs the toggle, staging nothing.
    [AvaloniaFact]
    public async Task EramBlockMiddleClick_WithShiftHeld_OnTheHsfIndicator_RunsQsWithThePick_AndStagesNothing()
    {
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel ac) = await ShowAsync(ScopeTemplates.EramId, "");
        try
        {
            List<ScopeEramEntryDto> sends = LightHsfAndVci(scope, ac, new ScopeEramEntryResultDto(true, ["ACCEPT"], null), onFrequency: true);
            FieldRect field = Assert.Single(view.Canvas.CurrentFrame.Tracks).Block.Fields.First(f => f.Slot == ScopeFieldSlot.HsfIndicator);
            Point? inWindow = view.Canvas.TranslatePoint(new Point(field.Rect.MidX, field.Rect.MidY), window);
            Assert.NotNull(inWindow);

            window.MouseDown(inWindow.Value, MouseButton.Middle, RawInputModifiers.Shift);
            window.MouseUp(inWindow.Value, MouseButton.Middle, RawInputModifiers.Shift);
            Dispatcher.UIThread.RunJobs();
            await scope.LastEntryRun;

            ScopeEramEntryDto sent = Assert.Single(sends);
            Assert.Equal(["QS", "UAL238"], sent.Tokens);
            Assert.Equal([new ScopeEramPickDto(1, "UAL238")], sent.Picks);
            Assert.Equal("", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramBlockMiddleClick_OnTheVciCell_WithAnEmptyPreview_RunsQnSlashSlashWithThePick()
    {
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel ac) = await ShowAsync(ScopeTemplates.EramId, "");
        try
        {
            List<ScopeEramEntryDto> sends = LightHsfAndVci(scope, ac, new ScopeEramEntryResultDto(true, ["ACCEPT"], null), onFrequency: true);

            ClickField(window, view.Canvas, ScopeFieldSlot.Vci, MouseButton.Middle, "UAL238");
            await scope.LastEntryRun;

            ScopeEramEntryDto sent = Assert.Single(sends);
            Assert.Equal(["QN", "//", "UAL238"], sent.Tokens);
            Assert.Equal([new ScopeEramPickDto(2, "UAL238")], sent.Picks);
            Assert.Equal("", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramBlockMiddleClick_OnTheHsfIndicator_WithTextInThePreview_FillsTheTrackAndExecutes()
    {
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel ac) = await ShowAsync(ScopeTemplates.EramId, "QQ 250");
        try
        {
            List<ScopeEramEntryDto> sends = LightHsfAndVci(scope, ac, new ScopeEramEntryResultDto(true, ["ACCEPT"], null), onFrequency: true);

            ClickField(window, view.Canvas, ScopeFieldSlot.HsfIndicator, MouseButton.Middle, "UAL238");
            await scope.LastEntryRun;

            ScopeEramEntryDto sent = Assert.Single(sends);
            Assert.Equal(["QQ", "250", "UAL238"], sent.Tokens);
            Assert.Equal([new ScopeEramPickDto(2, "UAL238")], sent.Picks);
            Assert.Equal("", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramBlockMiddleClick_OnTheAltitude_WithAnEmptyPreview_OpensTheAltitudePopup()
    {
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel ac) = await ShowAsync(ScopeTemplates.EramId, "");
        try
        {
            ClickField(window, view.Canvas, ScopeFieldSlot.EramAltitude, MouseButton.Middle, "UAL238");

            Assert.True(Assert.IsType<ScopeDataAltitudePopup>(view.ActiveFieldPopup).IsOpen);
            Assert.Null(view.ActiveContextMenu);
            Assert.Same(ac, scope.Main.SelectedAircraft);
            Assert.Equal("", scope.EntryText);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    // A Shift+left click on the HSF or VCI cell stages its bare toggle in the preview instead of sending it, the track kept as
    // the pick, so Enter sends it as the plain click would.

    private static void ShiftClickField(Window window, ScopeCanvas canvas, ScopeFieldSlot slot, string callsign)
    {
        FieldRect field = Assert.Single(canvas.CurrentFrame.Tracks, t => t.Aircraft.Callsign == callsign).Block.Fields.First(f => f.Slot == slot);
        Point? inWindow = canvas.TranslatePoint(new Point(field.Rect.MidX, field.Rect.MidY), window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, MouseButton.Left, RawInputModifiers.Shift);
        window.MouseUp(inWindow.Value, MouseButton.Left, RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task EramBlockShiftClick_OnTheHsfIndicator_StagesTheBareToggle_AndEnterSendsItWithThePick()
    {
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel ac) = await ShowAsync(ScopeTemplates.EramId, "");
        try
        {
            List<ScopeEramEntryDto> sends = LightHsfAndVci(scope, ac, new ScopeEramEntryResultDto(true, ["ACCEPT"], null), onFrequency: true);

            ShiftClickField(window, view.Canvas, ScopeFieldSlot.HsfIndicator, "UAL238");

            Assert.Equal("QS UAL238", scope.EntryText);
            Assert.Empty(sends);

            await EnterAsync(window, scope);

            ScopeEramEntryDto sent = Assert.Single(sends);
            Assert.Equal(["QS", "UAL238"], sent.Tokens);
            Assert.Equal([new ScopeEramPickDto(1, "UAL238")], sent.Picks);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }

    [AvaloniaFact]
    public async Task EramBlockShiftClick_OnTheVciCell_StagesTheBareToggle_AndEnterSendsItWithThePick()
    {
        (ScopeViewModel scope, ScopeView view, Window window, AircraftModel ac) = await ShowAsync(ScopeTemplates.EramId, "");
        try
        {
            List<ScopeEramEntryDto> sends = LightHsfAndVci(scope, ac, new ScopeEramEntryResultDto(true, ["ACCEPT"], null), onFrequency: true);

            ShiftClickField(window, view.Canvas, ScopeFieldSlot.Vci, "UAL238");

            Assert.Equal("QN // UAL238", scope.EntryText);
            Assert.Empty(sends);

            await EnterAsync(window, scope);

            ScopeEramEntryDto sent = Assert.Single(sends);
            Assert.Equal(["QN", "//", "UAL238"], sent.Tokens);
            Assert.Equal([new ScopeEramPickDto(2, "UAL238")], sent.Picks);
        }
        finally
        {
            CloseWindow(window, view);
        }
    }
}
