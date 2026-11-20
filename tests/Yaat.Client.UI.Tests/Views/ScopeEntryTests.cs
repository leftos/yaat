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
using Yaat.Sim.Commands;
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
        // No server here: an ERAM entry is answered ACCEPT, so the tests that only walk the history stay hermetic.
        scope.EramEntrySender = _ => Task.FromResult(new ScopeEramEntryResultDto(true, ["ACCEPT"], null));
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

    /// <summary>A STARS Scope's preview takes printable ASCII only, upper-cased as typed, as CRC's does; ERAM keeps the text as typed.</summary>
    [AvaloniaFact]
    public async Task TypingOnAStarsScope_UpperCasesThePreview_AndDropsNonAsciiText()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Stars;

            Assert.True(scope.TypeEntryText("*t1é\t"));
            Assert.False(scope.TypeEntryText("é"));
            Assert.Equal("*T1", scope.EntryText);

            // A $ entry is a YAAT command: it keeps its text as typed.
            scope.ClearEntry();
            Assert.True(scope.TypeEntryText("$"));
            Assert.True(scope.TypeEntryText(" ual1 fh 270é"));
            Assert.Equal("$ ual1 fh 270é", scope.EntryText);

            scope.ClearEntry();
            scope.ProfileKind = ScopeProfileKind.Eram;
            Assert.True(scope.TypeEntryText("qf ual1"));
            Assert.Equal("qf ual1", scope.EntryText);
        }
    }

    [AvaloniaFact]
    public async Task TypingWithTheCanvasFocused_ShowsThePreview_AndLeavesTheCommandLineAlone()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            FocusCanvas(window, view);
            Type(window, "$UAL238 FH 270");

            Assert.Equal("$UAL238 FH 270", scope.EntryText);
            Assert.True(view.EntryLayer.StarsArea.IsVisible);
            List<string> lines = Lines(view.EntryLayer.StarsArea);
            Assert.Equal(2, lines.Count);
            Assert.StartsWith("$ FH", lines[0], StringComparison.Ordinal);
            Assert.Equal("$UAL238 FH 270_", lines[1]);
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
        AddAircraft(main, "UAL238", 5000);
        await RunEntryAsync(scope, "$UAL238 FH 270");
        Assert.NotNull(scope.EntryResponse);

        scope.StageEntry("UAL238", "SPD 250");

        Assert.Equal("$ UAL238 SPD 250", scope.EntryText);
        Assert.Null(scope.EntryResponse);
    }

    /// <summary>A stage leaves the history walk, so Down has no recalled line to step from.</summary>
    [AvaloniaFact]
    public async Task StageEntry_AfterARecall_ResetsTheHistoryIndex()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        AddAircraft(main, "UAL238", 5000);
        await RunEntryAsync(scope, "$UAL238 FH 270");
        Assert.True(scope.WalkEntryHistory(older: true));

        scope.StageEntry("UAL238", "SPD 250");

        Assert.False(scope.WalkEntryHistory(older: false));
    }

    // A bare ERAM entry staged from a field click (a Shift+click on a scope-data row, the HSF indicator or the VCI cell).

    /// <summary>The bare entry replaces the preview, never appends, even to a <c>$</c> entry for the same aircraft.</summary>
    [AvaloniaFact]
    public async Task StageEramText_ReplacesAPilotEntryForTheSameAircraft_WithExactlyTheEntry()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        scope.ProfileKind = ScopeProfileKind.Eram;
        AddAircraft(main, "UAL238", 5000);
        Assert.True(scope.TypeEntryText("$ UAL238 CM 80"));

        scope.StageEramText("UAL238", "QQ 110 UAL238");

        Assert.Equal("QQ 110 UAL238", scope.EntryText);
    }

    [AvaloniaFact]
    public async Task StageEramText_ReplacesATypedEntry_WithExactlyTheEntry()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        scope.ProfileKind = ScopeProfileKind.Eram;
        AddAircraft(main, "UAL238", 5000);
        Assert.True(scope.TypeEntryText("QU KSFO"));

        scope.StageEramText("UAL238", "QS UAL238");

        Assert.Equal("QS UAL238", scope.EntryText);
    }

    [AvaloniaFact]
    public async Task StageEramText_AfterARecall_ResetsTheHistoryIndex()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        scope.ProfileKind = ScopeProfileKind.Eram;
        scope.EramEntrySender = _ => Task.FromResult(new ScopeEramEntryResultDto(true, ["ACCEPT"], null));
        AddAircraft(main, "UAL238", 5000);
        await RunEntryAsync(scope, "QQ 250 UAL238");
        Assert.True(scope.WalkEntryHistory(older: true));

        scope.StageEramText("UAL238", "QQ 110 UAL238");

        Assert.False(scope.WalkEntryHistory(older: false));
    }

    [AvaloniaFact]
    public async Task StageEramText_ClearsTheShownResponse()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        scope.ProfileKind = ScopeProfileKind.Eram;
        scope.EramEntrySender = _ => Task.FromResult(new ScopeEramEntryResultDto(true, ["ACCEPT"], null));
        AddAircraft(main, "UAL238", 5000);
        await RunEntryAsync(scope, "QQ 250 UAL238");
        Assert.NotNull(scope.EntryResponse);

        scope.StageEramText("UAL238", "QQ 110 UAL238");

        Assert.Null(scope.EntryResponse);
    }

    [AvaloniaFact]
    public async Task StageEramText_ThenEnter_SendsTheEntryWithTheTrackAsThePick()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        scope.ProfileKind = ScopeProfileKind.Eram;
        var sent = new List<ScopeEramEntryDto>();
        scope.EramEntrySender = dto =>
        {
            sent.Add(dto);
            return Task.FromResult(new ScopeEramEntryResultDto(true, ["ACCEPT"], null));
        };
        AddAircraft(main, "UAL238", 5000);

        scope.StageEramText("UAL238", "QQ 110 UAL238");
        Assert.True(scope.ExecuteEntry());
        await scope.LastEntryRun;

        ScopeEramEntryDto dto = Assert.Single(sent);
        Assert.Equal(["QQ", "110", "UAL238"], dto.Tokens);
        Assert.Equal([new ScopeEramPickDto(2, "UAL238")], dto.Picks);
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
            Type(window, "$UAL238 FH 270");

            await EnterAsync(window, scope);

            Assert.Equal("", scope.EntryText);
            Assert.Equal("UAL238 FH 270", scope.LastSentEntryText);
            Assert.Equal(new ScopeEntryResponse(ScopeResponseKind.Error, "Command error: Not connected.", "$UAL238 FH 270", []), scope.EntryResponse);
            Assert.Equal(["Command error: Not connected.", "$UAL238 FH 270"], Lines(view.EntryLayer.StarsArea));
            Assert.Contains(main.CommandHistory, h => h.Command == "$UAL238 FH 270");
            Assert.Equal(statusBefore, main.StatusText);
            Assert.Equal(Untouched, main.CommandText);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A pilot command typed without <c>$</c> is no Scope entry: nothing is sent, and the answer names the prefix.</summary>
    [AvaloniaFact]
    public async Task APilotCommandWithoutDollar_WithATrackSelected_SendsNothing_AndAnswersFormat()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            AircraftModel ac = AddAircraft(main, "UAL238", 5000);
            FocusCanvas(window, view);
            main.SelectedAircraft = ac;
            Type(window, "FH 270");

            await EnterAsync(window, scope);

            Assert.Null(scope.LastSentEntryText);
            Assert.Equal(new ScopeEntryResponse(ScopeResponseKind.Error, "FORMAT - $ FOR A YAAT COMMAND", "FH 270", []), scope.EntryResponse);
            Assert.Equal("FORMAT - $ FOR A YAAT COMMAND", Lines(view.EntryLayer.StarsArea)[0]);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A slow entry's answer, landing after a later entry was answered, leaves the later answer shown.</summary>
    [AvaloniaFact]
    public async Task ASlowEntrysLateAnswer_DoesNotOverwriteALaterEntrysAnswer()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Eram;
            AddAircraft(main, "UAL238", 5000);
            var slow = new TaskCompletionSource<ScopeEramEntryResultDto>();
            scope.EramEntrySender = _ => slow.Task;
            Assert.True(scope.TypeEntryText("QQ 250 UAL238"));
            Assert.True(scope.ExecuteEntry());
            Task slowRun = scope.LastEntryRun;

            await RunEntryAsync(scope, ".scope range 60");
            ScopeEntryResponse? later = scope.EntryResponse;
            Assert.NotNull(later);
            Assert.Equal(".scope range 60", later.Echo);

            slow.SetResult(new ScopeEramEntryResultDto(true, ["LATE"], null));
            await slowRun;

            Assert.Same(later, scope.EntryResponse);
        }
    }

    /// <summary>
    /// A track clicked into an entry that was then erased names no later <c>$</c> entry: that one is sent as it reads, for the
    /// selected track.
    /// </summary>
    [AvaloniaFact]
    public async Task ADollarEntry_AfterAClickedTrackWasErased_SendsAsItReads_ForTheSelectedTrack()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Stars;
            AddAircraft(main, "UAL1", 5000);
            AircraftModel aal2 = AddAircraft(main, "AAL2", 5000);
            Assert.True(scope.TypeEntryText("$FH 270"));
            scope.FillTrack("UAL1");
            Assert.Equal("$UAL1 FH 270", scope.EntryText);
            while (scope.BackspaceEntry()) { }

            main.SelectedAircraft = aal2;
            await RunEntryAsync(scope, "$DM 110");

            Assert.Equal("DM 110", scope.LastSentEntryText);
            // With no hub, SendTextAsync answers "Command error: Not connected." only once it has resolved a target aircraft (with
            // none it answers NoAircraftMatchedMessage), and it writes the resolved target back to SelectedAircraft.
            Assert.Equal("Command error: Not connected.", Assert.IsType<ScopeEntryResponse>(scope.EntryResponse).Message);
            Assert.Same(aal2, main.SelectedAircraft);
        }
    }

    /// <summary>
    /// A <c>$</c> entry with no callsign typed, no track clicked and nothing selected is sent as it reads, and SendTextAsync
    /// refuses it for naming no aircraft.
    /// </summary>
    [AvaloniaFact]
    public async Task ADollarEntry_WithNoCallsignNoClickAndNothingSelected_IsSentAsIs_AndRefusedBySendTextAsync()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Stars;
            AddAircraft(main, "UAL1", 5000);
            main.SelectedAircraft = null;

            await RunEntryAsync(scope, "$FH 270");

            Assert.Equal("FH 270", scope.LastSentEntryText);
            Assert.Equal(new ScopeEntryResponse(ScopeResponseKind.Error, MainViewModel.NoAircraftMatchedMessage, "$FH 270", []), scope.EntryResponse);
        }
    }

    [AvaloniaFact]
    public async Task DollarUn_SendsUn_WithNoCallsignAdded()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Stars;
            AddAircraft(main, "UAL1", 5000);
            Assert.True(scope.TypeEntryText("$"));
            scope.FillTrack("UAL1");
            while (scope.BackspaceEntry()) { }

            await RunEntryAsync(scope, "$UN");

            Assert.Equal("UN", scope.LastSentEntryText);
        }
    }

    /// <summary>A slow entry's answer, landing after the measure tool's prompt was shown, leaves the prompt shown.</summary>
    [AvaloniaFact]
    public async Task ASlowEntrysLateAnswer_DoesNotOverwriteAMeasurePrompt()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Eram;
            AddAircraft(main, "UAL238", 5000);
            var slow = new TaskCompletionSource<ScopeEramEntryResultDto>();
            scope.EramEntrySender = _ => slow.Task;
            Assert.True(scope.TypeEntryText("QQ 250 UAL238"));
            Assert.True(scope.ExecuteEntry());
            Task slowRun = scope.LastEntryRun;

            scope.ArmMeasure();
            ScopeEntryResponse? prompt = scope.EntryResponse;
            Assert.NotNull(prompt);

            slow.SetResult(new ScopeEramEntryResultDto(true, ["LATE"], null));
            await slowRun;

            Assert.Same(prompt, scope.EntryResponse);
        }
    }

    /// <summary>A slow <c>$</c> command's answer, landing after a later entry was answered, leaves the later answer shown.</summary>
    [AvaloniaFact]
    public async Task ASlowDollarCommandsLateAnswer_DoesNotOverwriteALaterEntrysAnswer()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Eram;
            var slow = new TaskCompletionSource();
            scope.YaatEntrySender = (text, sink, scheme) => text.StartsWith('.') ? main.SendTextAsync(text, sink, scheme) : slow.Task;
            Assert.True(scope.TypeEntryText("$UAL238 FH 270"));
            Assert.True(scope.ExecuteEntry());
            Task slowRun = scope.LastEntryRun;

            await RunEntryAsync(scope, ".scope range 60");
            ScopeEntryResponse? later = scope.EntryResponse;
            Assert.NotNull(later);

            slow.SetResult();
            await slowRun;

            Assert.Same(later, scope.EntryResponse);
        }
    }

    /// <summary>Esc clears the shown answer only: a slow entry's answer landing after it, with no later entry, still shows.</summary>
    [AvaloniaFact]
    public async Task ASlowEntrysAnswer_AfterEsc_StillShows()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Eram;
            AddAircraft(main, "UAL238", 5000);
            var slow = new TaskCompletionSource<ScopeEramEntryResultDto>();
            scope.EramEntrySender = _ => slow.Task;
            Assert.True(scope.TypeEntryText("QQ 250 UAL238"));
            Assert.True(scope.ExecuteEntry());
            Task slowRun = scope.LastEntryRun;
            Assert.True(scope.TypeEntryText("QQ"));
            Assert.True(scope.ClearEntry());

            slow.SetResult(new ScopeEramEntryResultDto(true, ["ACCEPT"], null));
            await slowRun;

            Assert.Equal("QQ 250 UAL238", Assert.IsType<ScopeEntryResponse>(scope.EntryResponse).Echo);
        }
    }

    /// <summary>
    /// On STARS, a slow entry's answer that lands after the next entry's first key is dropped: not shown while that entry is
    /// typed, nor once it is erased again.
    /// </summary>
    [AvaloniaFact]
    public async Task AStarsLateAnswer_IsDroppedOnceTheNextEntryStarts_AndNeverShownWhileItIsTyped()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Stars;
            var slow = new TaskCompletionSource();
            scope.YaatEntrySender = (_, _, _) => slow.Task;
            Assert.True(scope.TypeEntryText("$UAL238 FH 270"));
            Assert.True(scope.ExecuteEntry());
            Task slowRun = scope.LastEntryRun;
            Assert.True(scope.TypeEntryText("F"));

            slow.SetResult();
            await slowRun;

            Assert.Null(scope.EntryResponse);
            while (scope.BackspaceEntry()) { }
            Assert.Null(scope.EntryResponse);
        }
    }

    /// <summary>On STARS, a slow entry's answer that lands before any key of a next entry still shows.</summary>
    [AvaloniaFact]
    public async Task AStarsLateAnswer_WithNoNewKey_StillShows()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Stars;
            var slow = new TaskCompletionSource();
            scope.YaatEntrySender = (_, _, _) => slow.Task;
            Assert.True(scope.TypeEntryText("$UAL238 FH 270"));
            Assert.True(scope.ExecuteEntry());
            Task slowRun = scope.LastEntryRun;

            slow.SetResult();
            await slowRun;

            Assert.Equal("$UAL238 FH 270", Assert.IsType<ScopeEntryResponse>(scope.EntryResponse).Echo);
        }
    }

    /// <summary>On STARS, the next entry's first key drops a slow answer even when that entry is then cleared with Esc.</summary>
    [AvaloniaFact]
    public async Task AStarsLateAnswer_AfterEscWithTheEntryTyped_IsDropped()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Stars;
            var slow = new TaskCompletionSource();
            scope.YaatEntrySender = (_, _, _) => slow.Task;
            Assert.True(scope.TypeEntryText("$UAL238 FH 270"));
            Assert.True(scope.ExecuteEntry());
            Task slowRun = scope.LastEntryRun;
            Assert.True(scope.TypeEntryText("F"));
            Assert.True(scope.ClearEntry());

            slow.SetResult();
            await slowRun;

            Assert.Null(scope.EntryResponse);
        }
    }

    /// <summary>On STARS, a staged entry starts the next entry as a typed key does: a slow answer landing after it is dropped.</summary>
    [AvaloniaFact]
    public async Task AStarsLateAnswer_IsDropped_WhenTheNextEntryIsStaged()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Stars;
            var slow = new TaskCompletionSource();
            scope.YaatEntrySender = (_, _, _) => slow.Task;
            Assert.True(scope.TypeEntryText("$UAL238 FH 270"));
            Assert.True(scope.ExecuteEntry());
            Task slowRun = scope.LastEntryRun;
            scope.StageEntry("UAL238", "SPD 250");
            Assert.Equal("$ UAL238 SPD 250", scope.EntryText);

            slow.SetResult();
            await slowRun;

            Assert.Null(scope.EntryResponse);
        }
    }

    /// <summary>On STARS, an Up that recalls the last entry starts the next entry: a slow answer landing after it is dropped.</summary>
    [AvaloniaFact]
    public async Task AStarsLateAnswer_IsDropped_WhenTheNextEntryIsRecalledFromTheHistory()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Stars;
            var slow = new TaskCompletionSource();
            scope.YaatEntrySender = (_, _, _) => slow.Task;
            Assert.True(scope.TypeEntryText("$UAL238 FH 270"));
            Assert.True(scope.ExecuteEntry());
            Task slowRun = scope.LastEntryRun;
            Assert.True(scope.WalkEntryHistory(older: true));
            Assert.Equal("$UAL238 FH 270", scope.EntryText);

            slow.SetResult();
            await slowRun;

            Assert.Null(scope.EntryResponse);
        }
    }

    /// <summary>On STARS, a function key starts the next entry: a slow answer landing after it is dropped.</summary>
    [AvaloniaFact]
    public async Task AStarsLateAnswer_IsDropped_WhenAFunctionKeyStartsTheNextEntry()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Stars;
            var slow = new TaskCompletionSource();
            scope.YaatEntrySender = (_, _, _) => slow.Task;
            Assert.True(scope.TypeEntryText("$UAL238 FH 270"));
            Assert.True(scope.ExecuteEntry());
            Task slowRun = scope.LastEntryRun;
            Assert.True(scope.StartStarsFunctionEntry(Key.F3));
            Assert.Equal("INIT ", scope.EntryText);

            slow.SetResult();
            await slowRun;

            Assert.Null(scope.EntryResponse);
        }
    }

    /// <summary>On ERAM and Mixed, a slow entry's answer still shows when it lands while the next entry is typed.</summary>
    [AvaloniaTheory]
    [InlineData(ScopeProfileKind.Eram)]
    [InlineData(ScopeProfileKind.Mixed)]
    public async Task OnEramAndMixed_ALateAnswer_StillShowsWhileTheNextEntryIsTyped(ScopeProfileKind profile)
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = profile;
            var slow = new TaskCompletionSource();
            scope.YaatEntrySender = (_, _, _) => slow.Task;
            Assert.True(scope.TypeEntryText("$UAL238 FH 270"));
            Assert.True(scope.ExecuteEntry());
            Task slowRun = scope.LastEntryRun;
            Assert.True(scope.TypeEntryText("Q"));

            slow.SetResult();
            await slowRun;

            Assert.Equal("$UAL238 FH 270", Assert.IsType<ScopeEntryResponse>(scope.EntryResponse).Echo);
        }
    }

    [AvaloniaFact]
    public async Task OnMixed_TheSelectedTracksOwner_DecidesTheDialect_AndTheLabelFollowsTheSelection()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Mixed);
        try
        {
            var eramSends = new List<ScopeEramEntryDto>();
            scope.EramEntrySender = dto =>
            {
                eramSends.Add(dto);
                return Task.FromResult(new ScopeEramEntryResultDto(true, ["ACCEPT"], null));
            };
            AircraftModel stars = AddAircraft(main, "UAL238", 5000);
            stars.OwnerSectorCode = "1A";
            AircraftModel eram = AddAircraft(main, "AAL1", 5000);
            eram.OwnerSectorCode = "C36";
            main.SelectedAircraft = stars;
            FocusCanvas(window, view);
            Assert.Null(main.SelectedAircraft);
            main.SelectedAircraft = stars;
            // QQ is no STARS entry: for a STARS-owned track it reads as nothing and has no label.
            Type(window, "QQ 250");
            Assert.Equal("QQ 250_", Lines(view.EntryLayer.EramPreview)[0]);

            main.SelectedAircraft = eram;
            Settle(window);
            Assert.StartsWith("ERAM QQ", Lines(view.EntryLayer.EramPreview)[0], StringComparison.Ordinal);

            main.SelectedAircraft = stars;
            Settle(window);
            Assert.Equal("QQ 250_", Lines(view.EntryLayer.EramPreview)[0]);
            await EnterAsync(window, scope);

            Assert.Null(scope.LastSentEntryText);
            Assert.Equal(ScopeViewModel.FormatMessage, scope.EntryResponse?.Message);
            Assert.Empty(eramSends);
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

    // A user scheme with TRACK renamed to TRK, set as the user's own.
    private static CommandScheme UseTrkScheme(MainViewModel main)
    {
        var scheme = CommandScheme.Default();
        scheme.Patterns[CanonicalCommandType.TrackAircraft].Aliases = ["TRK"];
        main.Preferences.SetCommandScheme(scheme);
        return scheme;
    }

    // Runs entry on a STARS Scope with UAL238 known; returns the text and scheme it reached the sender with.
    private static async Task<(string Text, CommandScheme Scheme)> SentWithAsync(MainViewModel main, ScopeViewModel scope, string entry)
    {
        scope.ProfileKind = ScopeProfileKind.Stars;
        AddAircraft(main, "UAL238", 5000);
        var sent = new List<(string, CommandScheme)>();
        scope.YaatEntrySender = (text, _, scheme) =>
        {
            sent.Add((text, scheme));
            return Task.CompletedTask;
        };

        await RunEntryAsync(scope, entry);
        return Assert.Single(sent);
    }

    /// <summary>A STARS entry's translation is written in the default verbs, so it is read in them whatever the user renamed.</summary>
    [AvaloniaFact]
    public async Task AStarsEntry_UnderARenamedVerb_ReachesTheSenderWithASchemeThatReadsItsTranslation()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            UseTrkScheme(main);

            (string text, CommandScheme scheme) = await SentWithAsync(main, scope, "INIT UAL238");

            Assert.Equal("UAL238 TRACK", text);
            ParsedInput? parsed = CommandSchemeParser.Parse(text["UAL238 ".Length..], scheme);
            Assert.NotNull(parsed);
            Assert.Equal(CanonicalCommandType.TrackAircraft, parsed.Type);
        }
    }

    /// <summary>A <c>$</c> entry is typed in the user's own verbs and reaches the sender with the user's scheme.</summary>
    [AvaloniaFact]
    public async Task ADollarEntry_InTheUsersOwnVerb_ReachesTheSenderWithTheUsersScheme()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            CommandScheme user = UseTrkScheme(main);

            (string text, CommandScheme scheme) = await SentWithAsync(main, scope, "$ TRK UAL238");

            Assert.Equal("TRK UAL238", text);
            Assert.Same(user, scheme);
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

            Assert.Equal(new ScopeEntryResponse(ScopeResponseKind.Error, ScopeViewModel.NotSupportedMessage, entry, []), scope.EntryResponse);
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

            Assert.Equal(new ScopeEntryResponse(ScopeResponseKind.Error, MainViewModel.NoAircraftMatchedMessage, entry, []), scope.EntryResponse);
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

            Assert.Equal(new ScopeEntryResponse(ScopeResponseKind.Error, "No J-ring on UAL238", "*J", []), scope.EntryResponse);
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
    public async Task AnEramEntry_IsSentToTheServer_AndTheFeedbackShowsItsLines()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Eram);
        try
        {
            var sent = new List<ScopeEramEntryDto>();
            scope.EramEntrySender = dto =>
            {
                sent.Add(dto);
                return Task.FromResult(new ScopeEramEntryResultDto(true, ["ACCEPT", "INTERIM ALT", "UAL238"], null));
            };
            string statusBefore = main.StatusText;
            FocusCanvas(window, view);
            Type(window, "QQ 250 UAL238");
            Assert.StartsWith("ERAM QQ", Lines(view.EntryLayer.EramPreview)[0], StringComparison.Ordinal);

            await EnterAsync(window, scope);

            Assert.Equal(["QQ", "250", "UAL238"], Assert.Single(sent).Tokens);
            Assert.Equal(["✓", " ACCEPT", "INTERIM ALT", "UAL238", "QQ 250 UAL238"], Lines(view.EntryLayer.EramFeedback));
            Assert.Equal(["_"], Lines(view.EntryLayer.EramPreview));
            Assert.Null(scope.LastSentEntryText);
            Assert.Equal(statusBefore, main.StatusText);
            Assert.Equal("QQ 250 UAL238", main.CommandHistory[^1].Command);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// CRC's ERAM keyboard maps backtick to its clear-weather character as the ERAM entry is sent, which is where the server
    /// reads a message's text from; the entry itself stays ASCII and the preview draws the key's mark.
    /// </summary>
    [AvaloniaFact]
    public async Task TheClearWeatherKey_OnEram_TypesItsCharacter_WhenSent()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Eram);
        try
        {
            var sent = new List<ScopeEramEntryDto>();
            scope.EramEntrySender = dto =>
            {
                sent.Add(dto);
                return Task.FromResult(new ScopeEramEntryResultDto(true, ["ACCEPT"], null));
            };
            FocusCanvas(window, view);
            Type(window, "SM 26 ");
            Type(window, "`");
            Type(window, "HELLO");

            Assert.Equal("SM 26 `HELLO", scope.EntryText);
            Assert.Equal("SM 26 ○HELLO_", Lines(view.EntryLayer.EramPreview)[^1]);

            await EnterAsync(window, scope);

            Assert.Contains("\u0087HELLO", Assert.Single(sent).Tokens);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A <c>$</c> command typed on an ERAM Scope sends as typed: only an ERAM entry takes ERAM's characters, so the push free
    /// point's tilde reaches the command parser as a tilde.
    /// </summary>
    [AvaloniaFact]
    public async Task APilotPushFreePoint_OnEram_SendsAsTyped()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Eram);
        try
        {
            FocusCanvas(window, view);
            Type(window, "$UAL238 PUSH ~37.61523/-122.38604/090");

            Assert.Contains("~", string.Concat(scope.EntryPreviewLines));

            await EnterAsync(window, scope);

            Assert.Contains("~37.61523", Assert.IsType<string>(scope.LastSentEntryText));
            Assert.DoesNotContain('\u0088', scope.LastSentEntryText);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A response's echo shows ERAM's characters as the marks CRC draws, where a server echo carries them.</summary>
    [AvaloniaFact]
    public async Task TheResponseEcho_ShowsEramsCharactersAsTheirMarks()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ProfileKind = ScopeProfileKind.Eram;
            scope.EntryResponse = new ScopeEntryResponse(ScopeResponseKind.Text, "ACCEPT", "SM 26 \u0087HELLO", []);

            Assert.Equal("SM 26 ○HELLO", scope.EntryResponseLines[^1].Text);
        }
    }

    /// <summary>STARS has no such mapping: the key types as itself.</summary>
    [AvaloniaFact]
    public async Task TheWeatherSymbolKeys_OnStars_TypeThemselves()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            FocusCanvas(window, view);
            Type(window, "`");

            Assert.Equal("`", scope.EntryText);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A control character still types nothing, on either layout.</summary>
    [AvaloniaTheory]
    [InlineData(ScopeProfileKind.Stars)]
    [InlineData(ScopeProfileKind.Eram)]
    public async Task ABell_StillTypesNothing(ScopeProfileKind profile)
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(profile);
        try
        {
            FocusCanvas(window, view);
            Type(window, "\u0007");

            Assert.Equal("", scope.EntryText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AnEramRefusal_ShowsTheErrorLine()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Eram);
        try
        {
            scope.EramEntrySender = _ => Task.FromResult(new ScopeEramEntryResultDto(false, ["NOT YOUR CONTROL"], null));
            FocusCanvas(window, view);
            Type(window, "QQ 250 UAL238");

            await EnterAsync(window, scope);

            Assert.Equal(["NOT YOUR CONTROL", "QQ 250 UAL238"], Lines(view.EntryLayer.EramFeedback));
            Assert.Equal(ScopeResponseKind.Error, scope.EntryResponse?.Kind);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AnEramSendFailure_ShowsTheException_AndKeepsTheHistory()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Eram);
        try
        {
            scope.EramEntrySender = _ => throw new InvalidOperationException("Not connected.");
            FocusCanvas(window, view);
            Type(window, "QT UAL238");

            await EnterAsync(window, scope);

            Assert.Equal(["Not connected.", "QT UAL238"], Lines(view.EntryLayer.EramFeedback));
            Assert.Equal("QT UAL238", main.CommandHistory[^1].Command);
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
            Assert.Equal(["✓", " ACCEPT", "QQ 250 UAL238"], Lines(view.EntryLayer.EramFeedback));

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
    public async Task EnterOnABareDollar_DoesNothing()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            FocusCanvas(window, view);
            Type(window, "$");

            await EnterAsync(window, scope);

            Assert.Equal("$", scope.EntryText);
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
            scope.EramEntrySender = _ => Task.FromResult(new ScopeEramEntryResultDto(false, ["FORMAT"], null));
            await RunEntryAsync(scope, "QQ " + new string('1', 200));

            IReadOnlyList<ScopeResponseLine> lines = scope.EntryResponseLines;
            Assert.Equal(5, lines.Count);
            Assert.Equal(new ScopeResponseLine("FORMAT", true), lines[0]);
            Assert.All(lines.Skip(1), line => Assert.False(line.IsMessage));
        }
    }

    [AvaloniaFact]
    public async Task TheStarsResponse_IsGreenOnAccept_AndRedOnError()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync(ScopeProfileKind.Stars);
        try
        {
            scope.EntryResponse = new ScopeEntryResponse(ScopeResponseKind.Accept, ScopeViewModel.AcceptMessage, "FH 270", []);
            Settle(window);
            List<Color> accept = Colours(view.EntryLayer.StarsArea);

            scope.EntryResponse = new ScopeEntryResponse(ScopeResponseKind.Error, "Bad entry", "FH 270", []);
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
