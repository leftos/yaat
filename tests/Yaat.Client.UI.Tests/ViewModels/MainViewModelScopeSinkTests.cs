using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

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
        await vm.SendTextAsync(text, sink);
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

        Assert.Equal(["No YAAT Scope is open"], sink.Messages);
        Assert.Equal([("", ".scope range 40")], sink.History);
        Assert.Equal(1, sink.Clears);
        AssertCommandLineUntouched(vm, statusBefore);
    }

    [AvaloniaFact]
    public async Task ViewDotCommandWithAnUnknownView_ReportsToTheSink()
    {
        MainViewModel vm = await NewVmAsync();
        using var scope = new ScopeViewModel(vm, prefStore: null, profileStore: null);
        vm.OpenScope = scope;
        string statusBefore = vm.StatusText;

        RecordingSink sink = await SendAsync(vm, ".view zzz on");

        string message = Assert.Single(sink.Messages);
        Assert.StartsWith("Usage: .view <ssa|ca|rpoq>", message, StringComparison.Ordinal);
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
