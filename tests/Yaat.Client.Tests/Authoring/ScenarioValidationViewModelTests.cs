using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The Scenario Editor's live validation through the editor over a real store: findings land on the row and beside the field of
/// the aircraft they belong to, an edit or its undo runs the checks again, and a result that arrives after the session closed
/// is dropped. The post queues, so each test runs the UI work when it chooses; navdata is an empty test database.
/// </summary>
public sealed class ScenarioValidationViewModelTests : IDisposable
{
    // Every await on a run's hang guard: it fires only on a hang, never as a time assertion.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    private readonly ScenarioEditorFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task IssueLandsOnTheRightAircraftAndField()
    {
        using IDisposable navData = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
        ScenarioEditorViewModel editor = await OpenAsync(TwoAircraft("UAL2", parking: null));
        ScenarioAircraftViewModel aircraft = editor.Aircraft!;

        Assert.Null(aircraft.Rows[0].Issue);
        Assert.Equal(ScenarioEditIssueSeverity.Error, aircraft.Rows[1].Issue?.Severity);
        aircraft.Selected = aircraft.Rows[1];
        Assert.Equal(ScenarioEditIssueSeverity.Error, aircraft.ParkingIssue?.Severity);
        Assert.Null(aircraft.CallsignIssue);
        aircraft.Selected = aircraft.Rows[0];
        Assert.Null(aircraft.ParkingIssue);
    }

    [Fact]
    public async Task EditRecomputes()
    {
        using IDisposable navData = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
        ScenarioEditorViewModel editor = await OpenAsync(TwoAircraft("UAL2", "GA7"));
        ScenarioAircraftViewModel aircraft = editor.Aircraft!;
        Assert.All(aircraft.Rows, r => Assert.Null(r.Issue));

        CommitCallsign(aircraft, "ual2");
        await SettleAsync(editor);

        Assert.All(aircraft.Rows, r => Assert.Equal(ScenarioEditIssueSeverity.Error, r.Issue?.Severity));
        Assert.Equal(ScenarioEditIssueSeverity.Error, aircraft.CallsignIssue?.Severity);
    }

    [Fact]
    public async Task UndoClearsTheIssue()
    {
        using IDisposable navData = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
        ScenarioEditorViewModel editor = await OpenAsync(TwoAircraft("UAL2", "GA7"));
        ScenarioAircraftViewModel aircraft = editor.Aircraft!;
        CommitCallsign(aircraft, "ual2");
        await SettleAsync(editor);
        Assert.NotNull(aircraft.CallsignIssue);

        editor.UndoCommand.Execute(null);
        await SettleAsync(editor);

        Assert.Equal("AAL1", editor.Aircraft!.Rows[0].Callsign);
        Assert.All(editor.Aircraft.Rows, r => Assert.Null(r.Issue));
        Assert.Null(editor.Aircraft.CallsignIssue);
    }

    [Fact]
    public async Task StaleResultForAClosedSessionIsDropped()
    {
        using IDisposable navData = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
        ScenarioEditorViewModel editor = await OpenAsync(TwoAircraft("UAL2", "GA7"));
        ScenarioValidationViewModel validation = editor.Validation!;
        Assert.Empty(validation.Issues);

        CommitCallsign(editor.Aircraft!, "ual2");
        await _fx.Ui.WaitForPending(1).WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        Assert.Equal(1, _fx.Ui.Count);
        Assert.False(validation.Recomputed.IsCompleted);
        editor.Dispose();
        _fx.Ui.Drain();
        await validation.Recomputed.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Null(editor.Validation);
        Assert.Empty(validation.Issues);
    }

    [Fact]
    public async Task AnOlderRunFinishingAfterANewerOne_IsNotApplied()
    {
        using IDisposable navData = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
        _fx.AddCopy("L1", "Lima", TaxiPair("aal1", "AAA"));
        var session = new ScenarioEditSession(_fx.Store, ScenarioEditorFixture.Zoa, "L1");
        var gate = new TaskCompletionSource<AirportGroundLayout?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var validation = new ScenarioValidationViewModel(
            session,
            _fx.Ui.Post,
            () => true,
            (airportId, _) => (airportId == "AAA") ? gate.Task : Task.FromResult<AirportGroundLayout?>(null),
            Task.CompletedTask
        );

        validation.Recompute();
        Task older = validation.Recomputed;
        session.Edit(
            "Fix the second aircraft",
            s =>
            {
                s.Aircraft[1].AircraftId = "UAL2";
                s.Aircraft[1].AirportId = "BBB";
            }
        );
        validation.Recompute();
        await _fx.Ui.Run(validation.Recomputed.WaitAsync(HangGuard, TestContext.Current.CancellationToken));
        Assert.False(older.IsCompleted);
        Assert.DoesNotContain(validation.Issues, i => i.Field == ScenarioEditFields.Callsign);

        gate.SetResult(null);
        await _fx.Ui.Run(older.WaitAsync(HangGuard, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(validation.Issues, i => i.Field == ScenarioEditFields.Callsign);
    }

    [Fact]
    public async Task NavDataFinishingLoading_RunsTheChecksAgainOnItsOwn()
    {
        using IDisposable navData = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
        _fx.AddCopy("L1", "Lima", TwoAircraft("UAL2", parking: null));
        var session = new ScenarioEditSession(_fx.Store, ScenarioEditorFixture.Zoa, "L1");
        bool loaded = false;
        var ready = new TaskCompletionSource();
        using var validation = new ScenarioValidationViewModel(
            session,
            _fx.Ui.Post,
            () => Volatile.Read(ref loaded),
            ScenarioEditorFixture.NoLayouts,
            ready.Task
        );
        validation.Recompute();
        await _fx.Ui.Run(validation.Recomputed.WaitAsync(HangGuard, TestContext.Current.CancellationToken));
        Assert.Equal(ScenarioEditChecks.NavdataNotLoadedText, validation.ScenarioIssue);

        Volatile.Write(ref loaded, true);
        ready.SetResult();
        await _fx.Ui.WaitForPending(1).WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        _fx.Ui.Drain();
        await _fx.Ui.Run(validation.Recomputed.WaitAsync(HangGuard, TestContext.Current.CancellationToken));

        Assert.Null(validation.ScenarioIssue);
        Assert.Contains(validation.Issues, i => (i.AircraftId == "01KSECOND") && (i.Severity == ScenarioEditIssueSeverity.Error));
    }

    [Fact]
    public async Task NavDataFinishingLoadingAfterTheSessionClosed_RunsNothing()
    {
        using IDisposable navData = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
        _fx.AddCopy("L1", "Lima", TwoAircraft("UAL2", parking: null));
        var session = new ScenarioEditSession(_fx.Store, ScenarioEditorFixture.Zoa, "L1");
        var ready = new TaskCompletionSource();
        var validation = new ScenarioValidationViewModel(session, _fx.Ui.Post, () => false, ScenarioEditorFixture.NoLayouts, ready.Task);
        validation.Recompute();
        await _fx.Ui.Run(validation.Recomputed.WaitAsync(HangGuard, TestContext.Current.CancellationToken));
        Task lastRun = validation.Recomputed;

        validation.Dispose();
        ready.SetResult();

        Assert.Equal(0, _fx.Ui.Count);
        _fx.Ui.Drain();
        Assert.Same(lastRun, validation.Recomputed);
        Assert.Equal(ScenarioEditChecks.NavdataNotLoadedText, validation.ScenarioIssue);
    }

    [Fact]
    public async Task StaleButReadConfig_StillWarnsOnAPositionNoConfigLists()
    {
        using IDisposable navData = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
        Directory.CreateDirectory(_fx.ArtccCacheDir);
        string saved = Path.Combine(_fx.ArtccCacheDir, ScenarioEditorFixture.Zoa + ".json");
        File.WriteAllText(saved, ScenarioEditorFixture.ZoaConfig());
        File.SetLastWriteTimeUtc(saved, DateTime.UtcNow.AddDays(-2));
        _fx.ConfigBody = null;

        ScenarioEditorViewModel editor = await OpenAsync("\"studentPositionId\": \"01NOTAPOSITION000000000000\"");

        Assert.NotNull(editor.General!.ConfigProblem);
        Assert.False(editor.General.IsStudentPositionFreeText);
        Assert.Equal(ScenarioEditIssueSeverity.Warning, editor.General.StudentPositionIssue?.Severity);
    }

    [Fact]
    public async Task DuplicateCallsignMarksBothRows()
    {
        using IDisposable navData = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
        ScenarioEditorViewModel editor = await OpenAsync(TwoAircraft("aal1", "GA7"));
        ScenarioAircraftViewModel aircraft = editor.Aircraft!;

        Assert.All(aircraft.Rows, r => Assert.Equal(ScenarioEditIssueSeverity.Error, r.Issue?.Severity));
        foreach (ScenarioAircraftRowViewModel row in aircraft.Rows)
        {
            aircraft.Selected = row;
            Assert.Equal(ScenarioEditIssueSeverity.Error, aircraft.CallsignIssue?.Severity);
        }
    }

    // Two aircraft: AAL1 airborne at a fix, and a Parking start at OAK with the given callsign and stand (none when null).
    private static string TwoAircraft(string secondCallsign, string? parking)
    {
        string stand = (parking is null) ? "" : $", \"parking\": \"{parking}\"";
        return "\"aircraft\": ["
            + "{\"id\": \"01KAAL1\", \"aircraftId\": \"AAL1\", \"aircraftType\": \"B738\", "
            + "\"startingConditions\": {\"type\": \"FixOrFrd\", \"fix\": \"OAK\", \"altitude\": 5000}, \"presetCommands\": []}, "
            + $"{{\"id\": \"01KSECOND\", \"aircraftId\": \"{secondCallsign}\", \"aircraftType\": \"B738\", \"airportId\": \"OAK\", "
            + $"\"startingConditions\": {{\"type\": \"Parking\"{stand}}}, \"presetCommands\": []}}"
            + "]";
    }

    // AAL1 airborne at a fix, and a Parking start at the airport with the given callsign and a TAXI preset.
    private static string TaxiPair(string secondCallsign, string airportId) =>
        "\"aircraft\": ["
        + "{\"id\": \"01KAAL1\", \"aircraftId\": \"AAL1\", \"aircraftType\": \"B738\", "
        + "\"startingConditions\": {\"type\": \"FixOrFrd\", \"fix\": \"OAK\", \"altitude\": 5000}, \"presetCommands\": []}, "
        + $"{{\"id\": \"01KSECOND\", \"aircraftId\": \"{secondCallsign}\", \"aircraftType\": \"B738\", \"airportId\": \"{airportId}\", "
        + "\"startingConditions\": {\"type\": \"Parking\", \"parking\": \"GA7\"}, "
        + "\"presetCommands\": [{\"id\": \"01KTAXI\", \"command\": \"TAXI B\", \"timeOffset\": 0}]}"
        + "]";

    // The editor on a copy of the aircraft, its first runs settled.
    private async Task<ScenarioEditorViewModel> OpenAsync(string aircraft)
    {
        _fx.AddCopy("L1", "Lima", aircraft);
        (_, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("L1");
        await SettleAsync(editor);
        return editor;
    }

    private async Task SettleAsync(ScenarioEditorViewModel editor) =>
        await _fx.Ui.Run(editor.Validation!.Recomputed.WaitAsync(HangGuard, TestContext.Current.CancellationToken));

    private static void CommitCallsign(ScenarioAircraftViewModel aircraft, string callsign)
    {
        aircraft.Selected = aircraft.Rows[0];
        aircraft.Callsign = callsign;
        aircraft.CommitCallsignCommand.Execute(null);
    }
}
