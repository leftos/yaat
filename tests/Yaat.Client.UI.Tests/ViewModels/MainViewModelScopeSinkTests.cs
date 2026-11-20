using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// A line sent through <c>SendTextAsync</c> with a sink other than the command line's reports only to that sink: the global
/// verbs, the dot commands and the CRC aliases write their outcome to it, and the main command line's status and text stay
/// as they were.
/// </summary>
public class MainViewModelScopeSinkTests
{
    private const string Untouched = "UNTOUCHED LINE";

    private static AircraftDto IfrAircraft(string callsign) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            Latitude = 37.72,
            Longitude = -122.22,
            Heading = 280,
            Altitude = 3000,
            GroundSpeed = 180,
            BeaconCode = 4521,
            TransponderMode = "ModeC",
            IsIdenting = false,
            VerticalSpeed = 0,
            AssignedHeading = null,
            AssignedAltitude = null,
            AssignedSpeed = null,
            Departure = "SAN",
            Destination = "OAK",
            Route = "",
            FlightRules = "IFR",
            Status = "Active",
        };

    private static async Task<MainViewModel> NewVmAsync()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        await vm.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        await vm.CrcAliasLoad;
        vm.OnAircraftUpdated(IfrAircraft("UAL123"));
        vm.OnAircraftUpdated(IfrAircraft("UAL124"));
        Dispatcher.UIThread.RunJobs();
        vm.CommandText = Untouched;
        return vm;
    }

    private static async Task<RecordingSink> SendAsync(MainViewModel vm, string text)
    {
        var sink = new RecordingSink();
        await vm.SendTextAsync(text, sink, vm.Preferences.CommandScheme);
        Dispatcher.UIThread.RunJobs();
        return sink;
    }

    private static void AssertCommandLineUntouched(MainViewModel vm, string statusBefore)
    {
        Assert.Equal(statusBefore, vm.StatusText);
        Assert.Equal(Untouched, vm.CommandText);
    }

    [AvaloniaFact]
    public async Task BookmarkVerbForAMissingBookmark_ReportsItToTheSink()
    {
        MainViewModel vm = await NewVmAsync();
        string statusBefore = vm.StatusText;

        RecordingSink sink = await SendAsync(vm, "BM GO 9");

        Assert.Equal(["No bookmark bm-9"], sink.Errors);
        Assert.Equal(1, sink.Clears);
        AssertCommandLineUntouched(vm, statusBefore);
    }

    [AvaloniaFact]
    public async Task GlobalVerbThatFailsToSend_ReportsTheErrorAndClearsTheSinksLine()
    {
        MainViewModel vm = await NewVmAsync();
        string statusBefore = vm.StatusText;

        RecordingSink sink = await SendAsync(vm, "SQALL");

        Assert.Equal(["SQALL error: Not connected."], sink.Errors);
        Assert.Equal(1, sink.Clears);
        AssertCommandLineUntouched(vm, statusBefore);
    }

    [AvaloniaFact]
    public async Task ScopeDotCommand_ReportsTheEchoToTheSink()
    {
        MainViewModel vm = await NewVmAsync();
        string statusBefore = vm.StatusText;

        RecordingSink sink = await SendAsync(vm, ".scope range 40");

        Assert.Equal([".scope range 40"], sink.Messages);
        Assert.Equal([("", ".scope range 40")], sink.History);
        Assert.Equal(1, sink.Clears);
        AssertCommandLineUntouched(vm, statusBefore);
    }

    [AvaloniaFact]
    public async Task ViewDotCommandWithAnUnknownView_ReportsToTheSink()
    {
        MainViewModel vm = await NewVmAsync();
        using var scope = new ScopeViewModel(vm, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        vm.OpenScope = scope;
        string statusBefore = vm.StatusText;

        RecordingSink sink = await SendAsync(vm, ".view zzz on");

        string message = Assert.Single(sink.Messages);
        Assert.StartsWith("Usage: .view <ssa|ca|rpoq|crr>", message, StringComparison.Ordinal);
        AssertCommandLineUntouched(vm, statusBefore);
    }

    [AvaloniaFact]
    public async Task CrcAliasCommand_ReportsToTheSink()
    {
        MainViewModel vm = await NewVmAsync();
        string statusBefore = vm.StatusText;

        RecordingSink sink = await SendAsync(vm, ".reloadaliases");
        await vm.CrcAliasLoad;

        Assert.Equal(["Reloading CRC aliases"], sink.Messages);
        AssertCommandLineUntouched(vm, statusBefore);
    }

    [AvaloniaFact]
    public async Task AmbiguousCallsign_ReportsTheCandidatesToTheSink()
    {
        MainViewModel vm = await NewVmAsync();
        string statusBefore = vm.StatusText;

        RecordingSink sink = await SendAsync(vm, "UAL12 FH 270");

        string message = Assert.Single(sink.Errors);
        Assert.StartsWith("\"UAL12\" matches multiple aircraft: ", message, StringComparison.Ordinal);
        Assert.Equal(0, sink.Clears);
        AssertCommandLineUntouched(vm, statusBefore);
    }

    [AvaloniaFact]
    public async Task MeasureCommands_ReportToTheSink()
    {
        MainViewModel vm = await NewVmAsync();
        string statusBefore = vm.StatusText;

        RecordingSink arm = await SendAsync(vm, ".rbl");
        RecordingSink missing = await SendAsync(vm, ".rbl 5");
        RecordingSink clear = await SendAsync(vm, ".norbl");

        Assert.Contains(arm.Messages, m => m.StartsWith("Measure: click the first point", StringComparison.Ordinal));
        Assert.Empty(arm.Errors);
        Assert.Equal(["No measurement 5"], missing.Errors);
        Assert.Contains("No measurements to clear", clear.Messages);
        Assert.Empty(clear.Errors);
        AssertCommandLineUntouched(vm, statusBefore);
    }

    /// <summary>
    /// The main line reads <c>*T</c> by CRC's grammar, the Scope's one parse: <c>*T1</c> removes line 1, a spaced form is FORMAT,
    /// <c>*T</c> clears all.
    /// </summary>
    [AvaloniaFact]
    public async Task StarTOnTheMainLine_RemovesLineN_RefusesASpacedForm_AndClearsAll()
    {
        MainViewModel vm = await NewVmAsync();
        await SendAsync(vm, ".rbl UAL123 UAL124");
        await SendAsync(vm, ".rbl UAL124 UAL123");

        RecordingSink remove = await SendAsync(vm, "*t1");
        Assert.Equal([2], vm.Measure.Lines.Select(l => l.Slot));

        RecordingSink spaced = await SendAsync(vm, "*T 2");
        RecordingSink missing = await SendAsync(vm, "*T5");
        Assert.Single(vm.Measure.Lines);

        RecordingSink clear = await SendAsync(vm, "*T");

        Assert.Empty(vm.Measure.Lines);
        Assert.Empty(remove.Errors);
        Assert.Equal(["FORMAT"], spaced.Errors);
        Assert.Equal(["RBL ID"], missing.Errors);
        Assert.Empty(clear.Errors);
    }

    /// <summary>The main line has no slew: <c>*T</c> and an ACID anchors a line, and the same line's next entry completes it.</summary>
    [AvaloniaFact]
    public async Task StarTAcidOnTheMainLine_Anchors_AndTheNextTypedEntryCompletesTheLine()
    {
        MainViewModel vm = await NewVmAsync();
        var sink = new RecordingSink();

        await vm.SendTextAsync("*TUAL123", sink, vm.Preferences.CommandScheme);
        Assert.Equal("UAL123", vm.Measure.Anchor?.Endpoint.Callsign);

        await vm.SendTextAsync("UAL124", sink, vm.Preferences.CommandScheme);
        Dispatcher.UIThread.RunJobs();

        Client.Views.Map.RangeBearingLine line = Assert.Single(vm.Measure.Lines);
        Assert.Equal(("UAL123", "UAL124"), (line.A.Callsign, line.B.Callsign));
        Assert.Null(vm.Measure.Anchor);
        Assert.Empty(sink.Errors);
    }

    /// <summary>
    /// A pending <c>*T</c> anchor does not lock the main line: an entry that is not a completion form cancels the anchor and
    /// runs as usual.
    /// </summary>
    [AvaloniaFact]
    public async Task AMainLineEntryThatIsNoCompletion_CancelsThePendingStarTAnchor_AndRunsAsUsual()
    {
        MainViewModel vm = await NewVmAsync();
        var sink = new RecordingSink();
        await vm.SendTextAsync("*TUAL123", sink, vm.Preferences.CommandScheme);
        Assert.NotNull(vm.Measure.Anchor);

        await vm.SendTextAsync("UAL124 FH 270", sink, vm.Preferences.CommandScheme);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.Measure.Anchor);
        Assert.Empty(vm.Measure.Lines);
        Assert.DoesNotContain(sink.Errors, e => e is "FORMAT" or "NO FLIGHT");
    }

    /// <summary>
    /// The review's scenario: a main-line <c>*T</c> line completed by a click on the Scope, then a Ctrl+M anchor at the same
    /// track; the main line's next entry is not read as a completion, and the Scope's anchor stays.
    /// </summary>
    [AvaloniaFact]
    public async Task AfterAMainLineStarTLineIsCompletedByAClick_ACtrlMAnchorAtTheSameTrack_DoesNotCaptureTheMainLine()
    {
        MainViewModel vm = await NewVmAsync();
        ScopeViewModel scope = Assert.IsType<ScopeViewModel>(vm.ActiveMeasureScope);
        var sink = new RecordingSink();
        await vm.SendTextAsync("*TUAL123", sink, vm.Preferences.CommandScheme);
        scope.PickMeasurePoint(RblEndpoint.OnAircraft("UAL124"));
        Assert.Single(vm.Measure.Lines);
        scope.ArmMeasure();
        scope.PickMeasurePoint(RblEndpoint.OnAircraft("UAL123"));
        RblPendingAnchor? anchor = vm.Measure.Anchor;
        Assert.NotNull(anchor);

        await vm.SendTextAsync("UAL124 DM 50", sink, vm.Preferences.CommandScheme);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(anchor, vm.Measure.Anchor);
        Assert.Single(vm.Measure.Lines);
        Assert.DoesNotContain(sink.Errors, e => e is "FORMAT" or "NO FLIGHT");
    }

    [AvaloniaFact]
    public async Task AMainLineCompletion_WithEveryLineInUse_IsCapacity_AndTheAnchorStaysPending()
    {
        MainViewModel vm = await NewVmAsync();
        var sink = new RecordingSink();
        await vm.SendTextAsync("*TUAL123", sink, vm.Preferences.CommandScheme);
        RblPendingAnchor? anchor = vm.Measure.Anchor;
        Assert.NotNull(anchor);
        MeasureTarget ground = vm.MeasureSurface(RblView.Ground, null);
        for (int i = 0; i < RangeBearingLineStore.MaxLines; i++)
        {
            vm.Measure.Place(RblEndpoint.AtPoint(new LatLon(37.6, -122.4), ""), RblEndpoint.AtPoint(new LatLon(37.8, -122.0), ""), ground);
        }

        await vm.SendTextAsync("UAL124", sink, vm.Preferences.CommandScheme);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["CAPACITY"], sink.Errors);
        Assert.Equal(anchor, vm.Measure.Anchor);

        vm.Measure.Remove(1);
        await vm.SendTextAsync("UAL124", sink, vm.Preferences.CommandScheme);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.Measure.Anchor);
        Assert.Equal(RangeBearingLineStore.MaxLines, vm.Measure.Lines.Count);
    }

    /// <summary>
    /// A one-word line that resolves to no track cancels the pending main-line anchor and runs as usual: a global verb goes to
    /// the global path, whose pause send throws "Not connected." on this view model with no server.
    /// </summary>
    [AvaloniaFact]
    public async Task AGlobalVerb_AfterAMainLineStarTAnchor_CancelsTheAnchor_AndRuns()
    {
        MainViewModel vm = await NewVmAsync();
        var sink = new RecordingSink();
        await vm.SendTextAsync("*TOAK", sink, vm.Preferences.CommandScheme);
        Assert.NotNull(vm.Measure.Anchor);

        InvalidOperationException sent = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            vm.SendTextAsync("PAUSE", sink, vm.Preferences.CommandScheme)
        );
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Not connected.", sent.Message);
        Assert.Null(vm.Measure.Anchor);
        Assert.Empty(vm.Measure.Lines);
        Assert.Empty(sink.Errors);
    }

    /// <summary>A partial callsign after a main-line <c>*T</c> anchor cancels it and selects the aircraft, as a bare select does.</summary>
    [AvaloniaFact]
    public async Task APartialCallsign_AfterAMainLineStarTAnchor_CancelsTheAnchor_AndSelectsTheAircraft()
    {
        MainViewModel vm = await NewVmAsync();
        var sink = new RecordingSink();
        await vm.SendTextAsync("*TOAK", sink, vm.Preferences.CommandScheme);

        await vm.SendTextAsync("123", sink, vm.Preferences.CommandScheme);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.Measure.Anchor);
        Assert.Empty(vm.Measure.Lines);
        Assert.Equal("UAL123", vm.SelectedAircraft?.Callsign);
    }

    /// <summary>
    /// The main-line lines that never reach the completion (chat, a <c>CRC </c>-prefixed line, the staged say-again transcript)
    /// cancel a pending main-line <c>*T</c> anchor, so the next line runs as usual: an ACID selects its aircraft, no line drawn.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("chat")]
    [InlineData("crc")]
    [InlineData("say again")]
    public async Task ALineThatSkipsTheCompletion_CancelsTheMainLineStarTAnchor(string kind)
    {
        MainViewModel vm = await NewVmAsync();
        var sink = new RecordingSink();
        await vm.SendTextAsync("*TUAL123", sink, vm.Preferences.CommandScheme);
        Assert.NotNull(vm.Measure.Anchor);

        string line = kind switch
        {
            "chat" => "'checking",
            "crc" => "CRC .reloadaliases",
            _ => StageSayAgain(vm),
        };
        await vm.SendTextAsync(line, sink, vm.Preferences.CommandScheme);
        await vm.CrcAliasLoad;
        await vm.SendTextAsync("UAL124", sink, vm.Preferences.CommandScheme);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.Measure.Anchor);
        Assert.Empty(vm.Measure.Lines);
        Assert.Equal("UAL124", vm.SelectedAircraft?.Callsign);
    }

    // Stages a say-again transcript, as an accurate say-again press does; returns it.
    private static string StageSayAgain(MainViewModel vm)
    {
        const string Transcript = "united one two four turn left";
        vm.HandleSpeechServiceCommandReady(new SpeechResult(Transcript, null, null, "Say again: callsign unclear"));
        Dispatcher.UIThread.RunJobs();
        return Transcript;
    }

    /// <summary>The main-line anchor goes when the Scope it sits on changes profile away from STARS.</summary>
    [AvaloniaFact]
    public async Task AScopeLeavingStars_DropsTheMainLineStarTAnchorOnIt()
    {
        MainViewModel vm = await NewVmAsync();
        ScopeViewModel scope = Assert.IsType<ScopeViewModel>(vm.ActiveMeasureScope);
        scope.ProfileKind = Client.Views.Scope.Profiles.ScopeProfileKind.Stars;
        var sink = new RecordingSink();
        await vm.SendTextAsync("*TOAK", sink, vm.Preferences.CommandScheme);
        Assert.Same(scope, vm.Measure.Anchor?.Surface);

        scope.ProfileKind = Client.Views.Scope.Profiles.ScopeProfileKind.Eram;

        Assert.Null(vm.Measure.Anchor);
    }

    [AvaloniaFact]
    public async Task BookmarkGo_ReportsTheRewindToTheSink()
    {
        MainViewModel vm = await NewVmAsync();
        vm.ApplyBookmarks([new TimelineBookmarkDto("bm-1", 30, "one", "TS")]);
        string statusBefore = vm.StatusText;

        RecordingSink sink = await SendAsync(vm, "BM GO 1");

        string error = Assert.Single(sink.Errors);
        Assert.StartsWith("Rewind", error, StringComparison.Ordinal);
        AssertCommandLineUntouched(vm, statusBefore);
    }

    [AvaloniaFact]
    public async Task ASupersededHeldRewind_StillReportsAnOutcomeToItsOwnSink()
    {
        MainViewModel vm = await NewVmAsync();
        vm.RewindOverlay.Delay = _ => Task.CompletedTask;
        var inFlight = new TaskCompletionSource<RewindOutcome>();
        Task<RewindOutcome> running = vm.RewindOverlay.RequestAsync(5, "TS", _ => inFlight.Task);
        var superseded = new RecordingSink();
        var superseding = new RecordingSink();

        Task<RewindOutcome> held = vm.RewindToSeconds(10, superseded);
        Task<RewindOutcome> replacing = vm.RewindToSeconds(20, superseding);
        inFlight.SetResult(new RewindOutcome(true, "Rewound to 00:05"));
        await running;
        await held;
        await replacing;
        Dispatcher.UIThread.RunJobs();

        string error = Assert.Single(superseded.Messages);
        Assert.Equal([error], superseded.Errors);
        Assert.Equal(superseding.Errors[^1], error);
    }

    [AvaloniaFact]
    public async Task ASupersededCommandLineRewind_DoesNotOverwriteALaterJumpsStatus()
    {
        MainViewModel vm = await NewVmAsync();
        vm.RewindOverlay.Delay = _ => Task.CompletedTask;
        var sends = new Dictionary<double, TaskCompletionSource<RewindResultDto?>>();
        vm.RewindSender = seconds =>
        {
            var send = new TaskCompletionSource<RewindResultDto?>();
            sends[seconds] = send;
            return send.Task;
        };

        Task<RewindOutcome> a = vm.RewindToSeconds(5);
        Task<RewindOutcome> b = vm.RewindToSeconds(10);
        Task<RewindOutcome> c = vm.RewindToSeconds(20);
        sends[5].SetResult(null);
        await a;
        Task<RewindOutcome> d = vm.RewindToSeconds(30);
        sends[20].SetResult(null);
        await c;
        await b;
        Dispatcher.UIThread.RunJobs();

        try
        {
            Assert.False(sends.ContainsKey(10));
            Assert.True(sends.ContainsKey(30));
            Assert.StartsWith("Rewinding to ", vm.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            sends[30].TrySetResult(null);
            await d;
        }
    }

    /// <summary>Records what a send reports, in order; statuses and errors both land in <see cref="Messages"/>.</summary>
    private sealed class RecordingSink : ICommandTextSink
    {
        public List<string> Messages { get; } = [];

        public List<string> Errors { get; } = [];

        public List<(string Callsign, string Command)> History { get; } = [];

        public int Clears { get; private set; }

        public void ReportStatus(string status) => Messages.Add(status);

        public void ReportError(string message)
        {
            Messages.Add(message);
            Errors.Add(message);
        }

        public void ClearInput() => Clears++;

        public void AddHistory(string callsign, string command) => History.Add((callsign, command));

        public void AddWarning(string message) { }
    }
}
