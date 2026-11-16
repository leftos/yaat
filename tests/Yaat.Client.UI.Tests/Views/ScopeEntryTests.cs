using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Dock;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Typing on the Scope: with the canvas focused, the entry shows in the Scope's own area under its kind label, Enter runs it
/// through the send path and shows the response there, Esc clears it before it disarms the Rings tool, and the main command
/// line's text and status stay as they were. STARS draws one area under the SSA; ERAM and Mixed draw the MCA and the Response
/// Area at the bottom left. F3, F4, F5, F7 and F11 on STARS each start their function's entry.
/// </summary>
public class ScopeEntryTests
{
    private const string Untouched = "UNTOUCHED LINE";

    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window)> ShowScopeAsync(ScopeProfileKind profile)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        scope.ProfileKind = profile;
        main.CommandText = Untouched;
        var view = new ScopeView { DataContext = scope };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };
        window.Closed += (_, _) => scope.Dispose();
        window.Show();
        Settle(window);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (main, scope, view, window);
    }

    private static void Settle(Window window)
    {
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static void FocusCanvas(Window window, ScopeView view)
    {
        Point? inWindow = view.Canvas.TranslatePoint(new Point(view.Canvas.Bounds.Width / 2, view.Canvas.Bounds.Height / 2), window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, MouseButton.Left);
        window.MouseUp(inWindow.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Type(Window window, string text)
    {
        window.KeyTextInput(text);
        Settle(window);
    }

    private static async Task EnterAsync(Window window, ScopeViewModel scope)
    {
        window.DispatchKey(Key.Enter);
        await scope.LastEntryRun;
        Settle(window);
    }

    private static List<string> Lines(Control area) => [.. area.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")];

    private static RawInputModifiers ActionModifier => PlatformHelper.IsMacOS ? RawInputModifiers.Meta : RawInputModifiers.Control;

    // A colour of the entry areas as the overlay draws it: scaled by the Scope's FDB brightness.
    private static Color Drawn(ScopeViewModel scope, string hex)
    {
        SKColor scaled = ScopeBrightness.Apply(SKColor.Parse(hex), scope.Brightness.Fdb);
        return Color.FromArgb(scaled.Alpha, scaled.Red, scaled.Green, scaled.Blue);
    }

    private static List<Color> Colours(Control area) =>
        [.. area.GetVisualDescendants().OfType<TextBlock>().Select(t => Assert.IsType<SolidColorBrush>(t.Foreground).Color)];

    private static async Task RunEntryAsync(ScopeViewModel scope, string entry)
    {
        Assert.True(scope.TypeEntryText(entry));
        Assert.True(scope.ExecuteEntry());
        await scope.LastEntryRun;
    }

    // The point of the control's top-left (or bottom-left, at its height) in the view.
    private static Point Corner(Control control, ScopeView view, bool bottom)
    {
        Point? point = control.TranslatePoint(new Point(0, bottom ? control.Bounds.Height : 0), view);
        Assert.NotNull(point);
        return point.Value;
    }

    [AvaloniaFact]
    public async Task TypingWithTheCanvasFocused_ShowsThePreview_AndLeavesTheCommandLineAlone()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            FocusCanvas(window, view);
            Type(window, "UAL238 FH 270");

            Assert.Equal("UAL238 FH 270", scope.EntryText);
            Assert.True(view.EntryLayer.StarsArea.IsVisible);
            List<string> lines = Lines(view.EntryLayer.StarsArea);
            Assert.Equal(2, lines.Count);
            Assert.StartsWith("PILOT FH", lines[0], StringComparison.Ordinal);
            Assert.Equal("UAL238 FH 270_", lines[1]);
            Assert.Equal(Untouched, main.CommandText);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A staged entry on STARS takes the place of the response in its one area, as a typed one does.</summary>
    [AvaloniaFact]
    public async Task StageEntry_OnStars_ClearsTheShownResponse()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        scope.ProfileKind = ScopeProfileKind.Stars;
        Add(main, "UAL238");
        await RunEntryAsync(scope, "UAL238 FH 270");
        Assert.NotNull(scope.EntryResponse);

        scope.StageEntry("UAL238", "SPD 250");

        Assert.Equal("PILOT UAL238 SPD 250", scope.EntryText);
        Assert.Null(scope.EntryResponse);
    }

    /// <summary>A stage leaves the history walk, so Down has no recalled line to step from.</summary>
    [AvaloniaFact]
    public async Task StageEntry_AfterARecall_ResetsTheHistoryIndex()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        Add(main, "UAL238");
        await RunEntryAsync(scope, "UAL238 FH 270");
        Assert.True(scope.WalkEntryHistory(older: true));

        scope.StageEntry("UAL238", "SPD 250");

        Assert.False(scope.WalkEntryHistory(older: false));
    }

    [AvaloniaFact]
    public async Task Enter_RunsTheEntry_AndShowsTheResponse()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            AddAircraft(main, "UAL238", 5000);
            string statusBefore = main.StatusText;
            FocusCanvas(window, view);
            Type(window, "UAL238 FH 270");

            await EnterAsync(window, scope);

            Assert.Equal("", scope.EntryText);
            Assert.Equal("UAL238 FH 270", scope.LastSentEntryText);
            Assert.Equal(new ScopeEntryResponse(ScopeResponseKind.Error, "Command error: Not connected.", "UAL238 FH 270"), scope.EntryResponse);
            Assert.Equal(["Command error: Not connected.", "UAL238 FH 270"], Lines(view.EntryLayer.StarsArea));
            Assert.Contains(main.CommandHistory, h => h.Command == "UAL238 FH 270");
            Assert.Equal(statusBefore, main.StatusText);
            Assert.Equal(Untouched, main.CommandText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AStarsEntry_SendsItsYaatCommand_AndLeavesTheCommandLineAlone()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            main.SelectedAircraft = AddAircraft(main, "UAL238", 5000);
            FocusCanvas(window, view);
            Type(window, "+100");
            Assert.StartsWith("STARS +100 temporary altitude", Lines(view.EntryLayer.StarsArea)[0], StringComparison.Ordinal);

            await EnterAsync(window, scope);

            Assert.Equal("TEMPALT 100", scope.LastSentEntryText);
            Assert.Equal(Untouched, main.CommandText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AStarsKeyWordEntry_SendsItsYaatCommand_ForTheTypedCallsign()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Stars;
            AddAircraft(main, "UAL238", 5000);

            await RunEntryAsync(scope, "INIT UAL238");

            Assert.Equal("UAL238 TRACK", scope.LastSentEntryText);
        }
    }

    [AvaloniaTheory]
    [InlineData("MF N")]
    [InlineData("/3 UAL238")]
    public async Task AStarsEntryWithNoYaatCommand_IsRefused_NotSupported_AndSendsNothing(string entry)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Stars;
            main.SelectedAircraft = AddAircraft(main, "UAL238", 5000);

            await RunEntryAsync(scope, entry);

            Assert.Equal(new ScopeEntryResponse(ScopeResponseKind.Error, ScopeViewModel.NotSupportedMessage, entry), scope.EntryResponse);
            Assert.Null(scope.LastSentEntryText);
        }
    }

    [AvaloniaFact]
    public async Task AStarsJRingEntry_PlacesTheScopesJRing_OnTheSelectedTrack()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Stars;
            main.SelectedAircraft = AddAircraft(main, "UAL238", 5000);

            await RunEntryAsync(scope, "*J5");

            Assert.Equal(".scope rings jring 5 UAL238", scope.LastSentEntryText);
            Assert.Equal(5.0, scope.RingsOf("UAL238").JRingNm);

            await RunEntryAsync(scope, "*J");

            Assert.Equal(".scope rings jring 5 UAL238", scope.LastSentEntryText);
            Assert.Null(scope.RingsOf("UAL238").JRingNm);
        }
    }

    [AvaloniaFact]
    public async Task BareHnd_OnATrackWithNoHandoff_AnswersThePilotPathsRefusal()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Stars;
            main.SelectedAircraft = AddAircraft(main, "UAL238", 5000);

            await RunEntryAsync(scope, "HND");

            Assert.Equal("ACCEPT", scope.LastSentEntryText);
            Assert.Equal(ScopeResponseKind.Error, scope.EntryResponse?.Kind);
        }
    }

    private static readonly ActivePositionDto Oakland = new("OAK_APP", "Oakland Approach", null, "135.100", "NCT", "NorCal", "Tracon", "2B", "NCT");

    // A STARS Scope acting as OAK_APP (2B), with UAL238 selected, its handoff running from owner to peer.
    private static async Task<(MainViewModel Main, ScopeViewModel Scope)> HandoffScopeAsync(string owner, string peer)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        main.SetActivePositionFromServer(new PositionDisplayConfigDto([], [], [], "2B", Oakland, 3));
        scope.ProfileKind = ScopeProfileKind.Stars;
        AircraftModel ac = AddAircraft(main, "UAL238", 5000);
        ac.Owner = owner;
        ac.OwnerFacilityId = "NCT";
        ac.HandoffPeer = peer;
        ac.HandoffPeerFacilityId = "NCT";
        main.SelectedAircraft = ac;
        return (main, scope);
    }

    [AvaloniaTheory]
    [InlineData("HND", "CANCEL")]
    [InlineData("HND UAL2", "UAL238 CANCEL")]
    [InlineData("HND UAL238", "UAL238 CANCEL")]
    public async Task BareHnd_OnAHandoffFromTheActingPosition_Cancels(string entry, string sent)
    {
        (_, ScopeViewModel scope) = await HandoffScopeAsync("OAK_APP", "SFO_APP");
        using (scope)
        {
            await RunEntryAsync(scope, entry);

            Assert.Equal(sent, scope.LastSentEntryText);
        }
    }

    [AvaloniaTheory]
    [InlineData("HND", "ACCEPT")]
    [InlineData("HND UAL2", "UAL238 ACCEPT")]
    public async Task BareHnd_OnAHandoffToTheActingPosition_Accepts(string entry, string sent)
    {
        (_, ScopeViewModel scope) = await HandoffScopeAsync("SFO_APP", "OAK_APP");
        using (scope)
        {
            await RunEntryAsync(scope, entry);

            Assert.Equal(sent, scope.LastSentEntryText);
        }
    }

    [AvaloniaTheory]
    [InlineData("*J5")]
    [InlineData("*P")]
    public async Task ATpaEntry_WithNoAircraftSelected_AnswersTheNoAircraftText(string entry)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Stars;
            main.SelectedAircraft = null;

            await RunEntryAsync(scope, entry);

            Assert.Equal(new ScopeEntryResponse(ScopeResponseKind.Error, MainViewModel.NoAircraftMatchedMessage, entry), scope.EntryResponse);
            Assert.Null(scope.LastSentEntryText);
        }
    }

    [AvaloniaFact]
    public async Task BareJ_OnATrackWithNoJRing_SaysSo()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Stars;
            main.SelectedAircraft = AddAircraft(main, "UAL238", 5000);

            await RunEntryAsync(scope, "*J");

            Assert.Equal(new ScopeEntryResponse(ScopeResponseKind.Error, "No J-ring on UAL238", "*J"), scope.EntryResponse);
            Assert.Null(scope.LastSentEntryText);
        }
    }

    [AvaloniaFact]
    public async Task BareP_RemovesTheSelectedTracksCone()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Stars;
            main.SelectedAircraft = AddAircraft(main, "UAL238", 5000);
            await RunEntryAsync(scope, "*P5");
            Assert.Equal(5.0, scope.RingsOf("UAL238").ConeNm);

            await RunEntryAsync(scope, "*P");

            Assert.Equal(".scope rings cone 5 UAL238", scope.LastSentEntryText);
            Assert.Null(scope.RingsOf("UAL238").ConeNm);
        }
    }

    [AvaloniaFact]
    public async Task ADotCommand_EchoesIntoTheFeedback_NotTheStatusLine()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            string statusBefore = main.StatusText;
            FocusCanvas(window, view);
            Type(window, ".scope range 60");

            await EnterAsync(window, scope);

            Assert.Equal(60, scope.RangeNm);
            ScopeEntryResponse response = Assert.IsType<ScopeEntryResponse>(scope.EntryResponse);
            Assert.Equal(ScopeResponseKind.Text, response.Kind);
            Assert.NotEqual("", response.Message);
            Assert.StartsWith(Lines(view.EntryLayer.StarsArea)[0], response.Message, StringComparison.Ordinal);
            Assert.Equal(statusBefore, main.StatusText);
            Assert.Equal(Untouched, main.CommandText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AnEramEntry_IsRefused_NotSupported()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Eram);
        try
        {
            string statusBefore = main.StatusText;
            FocusCanvas(window, view);
            Type(window, "QQ 250 UAL238");
            Assert.StartsWith("ERAM QQ", Lines(view.EntryLayer.EramPreview)[0], StringComparison.Ordinal);

            await EnterAsync(window, scope);

            Assert.Equal(["NOT SUPPORTED", "QQ 250 UAL238"], Lines(view.EntryLayer.EramFeedback));
            Assert.Equal(["_"], Lines(view.EntryLayer.EramPreview));
            Assert.Null(scope.LastSentEntryText);
            Assert.Equal(statusBefore, main.StatusText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Esc_ClearsThePreview_ThenDisarmsRings()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            FocusCanvas(window, view);
            Type(window, "FH 270");
            scope.Press(ScopeToolbarButtons.Find(ScopeButtonId.Tools)!);
            Assert.True(scope.IsRingToolArmed);

            window.DispatchKey(Key.Escape);

            Assert.Equal("", scope.EntryText);
            Assert.False(view.EntryLayer.StarsArea.IsVisible);
            Assert.True(scope.IsRingToolArmed);

            window.DispatchKey(Key.Escape);

            Assert.False(scope.IsRingToolArmed);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Esc_OnEram_ClearsTheFeedbackToo()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Eram);
        try
        {
            FocusCanvas(window, view);
            Type(window, "QQ 250 UAL238");
            await EnterAsync(window, scope);
            Type(window, "FH");
            Assert.Equal(["NOT SUPPORTED", "QQ 250 UAL238"], Lines(view.EntryLayer.EramFeedback));

            window.DispatchKey(Key.Escape);
            Settle(window);

            Assert.Equal("", scope.EntryText);
            Assert.Null(scope.EntryResponse);
            Assert.Empty(Lines(view.EntryLayer.EramFeedback));
            Assert.Equal(["_"], Lines(view.EntryLayer.EramPreview));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TypingWithoutCanvasFocus_DoesNothing()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            Type(window, "FH 270");

            Assert.Equal("", scope.EntryText);
            Assert.False(view.EntryLayer.StarsArea.IsVisible);
            Assert.Equal(Untouched, main.CommandText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CtrlShiftT_StillTogglesTdm()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            FocusCanvas(window, view);
            Type(window, "FH");
            RawInputModifiers action = (PlatformHelper.IsMacOS ? RawInputModifiers.Meta : RawInputModifiers.Control) | RawInputModifiers.Shift;

            window.DispatchKey(Key.T, action);

            Assert.True(scope.IsTdm);
            Assert.Equal("FH", scope.EntryText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task EnterOnABarePilotWord_DoesNothing()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            FocusCanvas(window, view);
            Type(window, "PILOT");

            await EnterAsync(window, scope);

            Assert.Equal("PILOT", scope.EntryText);
            Assert.Null(scope.EntryResponse);
            Assert.Null(scope.LastSentEntryText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Backspace_RemovesTheLastCharacter()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            FocusCanvas(window, view);
            Type(window, "FH 27");

            window.DispatchKey(Key.Back);
            Settle(window);

            Assert.Equal("FH 2", scope.EntryText);
            Assert.Equal("FH 2_", Lines(view.EntryLayer.StarsArea)[^1]);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task UpAndDown_WalkTheScopesOwnHistory_NotTheCommandLines()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Eram);
        try
        {
            main.AddHistory("", "SQALL");
            FocusCanvas(window, view);
            Type(window, "QQ 1");
            await EnterAsync(window, scope);
            Type(window, "QQ 2");
            await EnterAsync(window, scope);

            var seen = new List<string>();
            foreach (Key key in new[] { Key.Up, Key.Up, Key.Up, Key.Down, Key.Down })
            {
                window.DispatchKey(key);
                seen.Add(scope.EntryText);
            }

            Assert.Equal(["QQ 2", "QQ 1", "QQ 1", "QQ 2", ""], seen);
            Assert.Equal(Untouched, main.CommandText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Up_SavesAHalfTypedEntry_AndDownPastTheNewestRestoresIt()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Eram);
        try
        {
            FocusCanvas(window, view);
            Type(window, "QQ 1");
            await EnterAsync(window, scope);
            Type(window, "FH");

            window.DispatchKey(Key.Up);
            Assert.Equal("QQ 1", scope.EntryText);
            window.DispatchKey(Key.Down);

            Assert.Equal("FH", scope.EntryText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheEntryHistory_KeepsTheLast50()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Eram;
            for (int i = 0; i <= 50; i++)
            {
                await RunEntryAsync(scope, $"QQ {i}");
            }

            for (int i = 0; i < 60; i++)
            {
                scope.WalkEntryHistory(older: true);
            }

            Assert.Equal("QQ 1", scope.EntryText);
        }
    }

    [AvaloniaFact]
    public async Task CtrlR_RecallsTheLastEntry_OnEram()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Eram);
        try
        {
            FocusCanvas(window, view);
            Type(window, "QQ 1");
            await EnterAsync(window, scope);

            window.DispatchKey(Key.R, ActionModifier);

            Assert.Equal("QQ 1", scope.EntryText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CtrlR_DoesNothing_OnStars()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            FocusCanvas(window, view);
            Type(window, ".scope range 60");
            await EnterAsync(window, scope);

            window.DispatchKey(Key.R, ActionModifier);

            Assert.Equal("", scope.EntryText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TabAndFunctionKeysAndCtrlChords_PassThrough()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            FocusCanvas(window, view);
            Type(window, "FH");
            var handled = new List<bool>();
            view.AddHandler(InputElement.KeyDownEvent, (_, e) => handled.Add(e.Handled), RoutingStrategies.Bubble, handledEventsToo: true);

            // Tab last: it may move the focus off the canvas. F8 is a function key the Scope leaves alone on STARS.
            window.DispatchKey(Key.F8);
            window.DispatchKey(Key.K, ActionModifier);
            window.DispatchKey(Key.Tab);

            Assert.Equal("FH", scope.EntryText);
            Assert.Equal([false, false, false], handled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(Key.F3, "INIT ")]
    [InlineData(Key.F4, "TERM ")]
    [InlineData(Key.F5, "HND ")]
    [InlineData(Key.F7, "MF ")]
    [InlineData(Key.F11, "CA ")]
    public async Task AStarsFunctionKey_StartsItsFunctionEntry(Key key, string word)
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            FocusCanvas(window, view);

            window.DispatchKey(key);

            Assert.Equal(word, scope.EntryText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task F3_ReplacesTheEntryItFinds()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            FocusCanvas(window, view);
            Type(window, "+1");

            window.DispatchKey(Key.F3);

            Assert.Equal("INIT ", scope.EntryText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task F3ThenTheCallsignAndEnter_SendsTheTrackCommand()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            FocusCanvas(window, view);
            window.DispatchKey(Key.F3);
            Type(window, "UAL238");

            await EnterAsync(window, scope);

            Assert.Equal("UAL238 TRACK", scope.LastSentEntryText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task F3_OnEram_IsNotHandled()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Eram);
        try
        {
            FocusCanvas(window, view);
            var handled = new List<bool>();
            view.AddHandler(InputElement.KeyDownEvent, (_, e) => handled.Add(e.Handled), RoutingStrategies.Bubble, handledEventsToo: true);

            window.DispatchKey(Key.F3);

            Assert.Equal("", scope.EntryText);
            Assert.Equal([false], handled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ShiftF3_OnStars_LeavesTheEntryAlone()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            FocusCanvas(window, view);
            Type(window, "FH");
            var handled = new List<bool>();
            view.AddHandler(InputElement.KeyDownEvent, (_, e) => handled.Add(e.Handled), RoutingStrategies.Bubble, handledEventsToo: true);

            window.DispatchKey(Key.F3, RawInputModifiers.Shift);

            Assert.Equal("FH", scope.EntryText);
            Assert.Equal([false], handled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheStarsResponse_ClearsOnTheNextEntrysFirstKey()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            FocusCanvas(window, view);
            Type(window, ".scope range 60");
            await EnterAsync(window, scope);
            Assert.NotNull(scope.EntryResponse);

            Type(window, "F");

            Assert.Null(scope.EntryResponse);
            Assert.Equal("F_", Lines(view.EntryLayer.StarsArea)[^1]);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task UpThenBackspaceThenDown_KeepsTheEdit()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Eram);
        try
        {
            FocusCanvas(window, view);
            Type(window, "QQ 1");
            await EnterAsync(window, scope);

            window.DispatchKey(Key.Up);
            window.DispatchKey(Key.Back);
            window.DispatchKey(Key.Down);

            Assert.Equal("QQ ", scope.EntryText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Esc_GivesWayToATearoffMove()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            main.RunScopeCommand(".scope tearoff range tl 10 10");
            Settle(window);
            FocusCanvas(window, view);
            Type(window, "FH");
            ScopeButtonControl copy = view
                .TearoffLayer.GetVisualDescendants()
                .OfType<ScopeButtonControl>()
                .Single(c => c.IsTearoff && c.IsEffectivelyVisible && (c.Key == new ScopeButtonKey(ScopeButtonId.Range, null)));
            Border strip = Assert.IsType<Border>(copy.Strip);
            Point grab = strip.TranslatePoint(new Point(strip.Bounds.Width / 2, strip.Bounds.Height / 2), window)!.Value;
            window.MouseMove(grab);
            window.MouseDown(grab, MouseButton.Left);
            window.MouseUp(grab, MouseButton.Left);
            Settle(window);
            Assert.True(view.TearoffLayer.IsMoving);

            window.DispatchKey(Key.Escape);
            Settle(window);

            Assert.False(view.TearoffLayer.IsMoving);
            Assert.Equal("FH", scope.EntryText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task LongEntries_WrapAt30_AndCapAt3PreviewLines()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.TypeEntryText(new string('A', 100));

            Assert.Equal([new string('A', 30), new string('A', 30), new string('A', 10) + "_"], scope.EntryPreviewLines);
        }
    }

    [AvaloniaFact]
    public async Task Feedback_CapsAt5Lines()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Eram;
            await RunEntryAsync(scope, "QQ " + new string('1', 200));

            IReadOnlyList<ScopeResponseLine> lines = scope.EntryResponseLines;
            Assert.Equal(5, lines.Count);
            Assert.Equal(new ScopeResponseLine("NOT SUPPORTED", true), lines[0]);
            Assert.All(lines.Skip(1), line => Assert.False(line.IsMessage));
        }
    }

    [AvaloniaFact]
    public async Task TheStarsResponse_IsGreenOnAccept_AndRedOnError()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            scope.EntryResponse = new ScopeEntryResponse(ScopeResponseKind.Accept, ScopeViewModel.AcceptMessage, "FH 270");
            Settle(window);
            List<Color> accept = Colours(view.EntryLayer.StarsArea);

            scope.EntryResponse = new ScopeEntryResponse(ScopeResponseKind.Error, "Bad entry", "FH 270");
            Settle(window);
            List<Color> error = Colours(view.EntryLayer.StarsArea);

            Assert.Equal([Drawn(scope, "#3fd06a"), Drawn(scope, "#3fd06a")], accept);
            Assert.Equal([Drawn(scope, "#ff4040"), Drawn(scope, "#2fe02f")], error);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheMixedProfile_UsesTheEramBoxes()
    {
        (_, _, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Mixed);
        try
        {
            FocusCanvas(window, view);
            Type(window, "QQ 250");

            Assert.True(view.EntryLayer.EramBoxes.IsVisible);
            Assert.False(view.EntryLayer.StarsArea.IsVisible);
            Assert.StartsWith("ERAM QQ", Lines(view.EntryLayer.EramPreview)[0], StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheStarsArea_SitsBelowTheDefaultSsaCard()
    {
        (_, _, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            FocusCanvas(window, view);
            Type(window, "FH 270");
            ScopeViewCard ssa = Assert.Single(view.TearoffLayer.GetVisualDescendants().OfType<ScopeViewCard>());

            Point areaTop = Corner(view.EntryLayer.StarsArea, view, bottom: false);
            Point ssaBottom = Corner(ssa, view, bottom: true);

            Assert.Equal(new Point(16, 86), areaTop);
            Assert.True(areaTop.Y >= ssaBottom.Y, $"The SSA card's bottom is at {ssaBottom.Y}, below the area's top at {areaTop.Y}.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheEramBoxes_SitAtTheBottomLeft()
    {
        (_, _, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Eram);
        try
        {
            Control mca = view.EntryLayer.Mca;
            Control ra = view.EntryLayer.ResponseArea;

            Point mcaBottom = Corner(mca, view, bottom: true);
            Point mcaTop = Corner(mca, view, bottom: false);
            Point raBottom = Corner(ra, view, bottom: true);

            Assert.True(view.EntryLayer.EramBoxes.IsVisible);
            Assert.Equal(16, mcaBottom.X);
            Assert.Equal(view.Bounds.Height - 16, mcaBottom.Y);
            Assert.Equal(374, mca.Bounds.Width);
            Assert.True(mca.Bounds.Height >= 105);
            Assert.Equal(new Size(372, 70), ra.Bounds.Size);
            Assert.Equal(mcaTop.Y - 8, raBottom.Y);
        }
        finally
        {
            window.Close();
        }
    }
}
