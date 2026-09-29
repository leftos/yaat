using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Eram;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Eram;

/// <summary>
/// The ERAM conflict-alert settings (the <c>CA</c> entry) on the bare engine: a <see cref="RecordedEramRoomEntry"/>
/// writes one facility's function or sector-display switch absolutely through
/// <see cref="SimulationEngine.ApplyEramRoomEntry"/>, the drain hands the host one payload-less notification per applied
/// record, <see cref="EramRoomSettings.ShowsConflict"/> answers what a sector is shown, the snapshot carries the settings
/// and a replay from zero rebuilds them.
/// </summary>
public class EramRoomSettingsStepTests
{
    private const string Facility = "ZOA";

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public EramRoomSettingsStepTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? Engine() => _zoa is null ? null : AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);

    private static RecordedEramRoomEntry Entry(double elapsed, string entry) => new(elapsed, Facility, entry);

    /// <summary>A recording of everything the engine has run so far, carrying the ARTCC the replay re-initialises from.</summary>
    private SessionRecording Recording(SimulationEngine engine)
    {
        SimScenarioState scenario = engine.Scenario!;
        return new SessionRecording
        {
            ScenarioJson = AiTestFixture.ParkedAtOak,
            RngSeed = 7,
            Actions = [.. scenario.ActionLog],
            TotalElapsedSeconds = scenario.ElapsedSeconds,
            ArtccConfigJson = JsonSerializer.Serialize(_zoa, RecordingJsonOptions.Default),
            SessionStartUtc = MagneticDeclination.EvaluationDateUtc,
            StudentPositionState = new ReplayStudentPosition(scenario.StudentPosition, scenario.StudentTcp, scenario.StudentPositionType, false),
        };
    }

    private static EramRoomSettings Settings(params string[] entries)
    {
        var settings = new EramRoomSettings();
        foreach (string entry in entries)
        {
            Assert.True(settings.TryApply(Facility, entry), entry);
        }

        return settings;
    }

    [Fact]
    public void ACaOffRecord_SetsTheFunction_AndTellsTheHostOnce()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        CommandResult applied = engine.Actions.ApplyRecorded(Entry(0, "CA CA FUNCTION OFF"), host);

        Assert.True(applied.Success, applied.Message);
        EramFacilityConflictSettings settings = Assert.Single(engine.EramRoomSettings.Facilities.Values);
        Assert.Equal(Facility, settings.FacilityId);
        Assert.False(settings.CaFunctionOn);
        Assert.True(settings.MciFunctionOn);
        Assert.Equal(1, host.EramConflictSettingsChanges);
    }

    /// <summary>An entry says what the switch is, never "flip it": OFF twice is still off, and ON restores the default.</summary>
    [Fact]
    public void ASectorDisplayRecord_OffThenOn_IsAbsoluteNeverAToggle()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        Assert.True(engine.Actions.ApplyRecorded(Entry(0, "CA CA DISPLAY 44 OFF"), host).Success);
        Assert.True(engine.Actions.ApplyRecorded(Entry(1, "CA CA DISPLAY 44 OFF"), host).Success);

        Assert.False(engine.EramRoomSettings.ShowsConflict(Facility, "44", isMciPair: false));
        Assert.Equal(["44"], engine.EramRoomSettings.Facilities[Facility].CaDisplayOffSectors);

        Assert.True(engine.Actions.ApplyRecorded(Entry(2, "CA CA DISPLAY 44 ON"), host).Success);

        Assert.True(engine.EramRoomSettings.ShowsConflict(Facility, "44", isMciPair: false));
        Assert.Empty(engine.EramRoomSettings.Facilities);
        Assert.Equal(3, host.EramConflictSettingsChanges);
    }

    [Theory]
    [InlineData("")]
    [InlineData("CA")]
    [InlineData("CA CA ON")]
    [InlineData("CA INT FUNCTION OFF")]
    [InlineData("CA CA FUNCTION MAYBE")]
    [InlineData("CA CA FUNCTION 44 OFF")]
    [InlineData("CA MCI DISPLAY OFF")]
    [InlineData("CA CA DISPLAY ALL  OFF")]
    [InlineData("CA MCI DISPLAY ALL OFF")]
    [InlineData("CA CA DISPLAY 44 ALL ON")]
    [InlineData("CA CA TOGGLE 44 OFF")]
    [InlineData("RK CA FUNCTION OFF")]
    [InlineData("ca ca function off")]
    public void AMalformedEntry_ChangesNothing(string entry)
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        Assert.True(engine.Actions.ApplyRecorded(Entry(0, "CA MCI DISPLAY 45 OFF"), host).Success);

        CommandResult applied = engine.Actions.ApplyRecorded(Entry(1, entry), host);

        Assert.False(applied.Success);
        EramFacilityConflictSettings settings = Assert.Single(engine.EramRoomSettings.Facilities.Values);
        Assert.True(settings.CaFunctionOn);
        Assert.True(settings.MciFunctionOn);
        Assert.Empty(settings.CaDisplayOffSectors);
        Assert.Equal(["45"], settings.MciDisplayOffSectors);
        Assert.Equal(1, host.EramConflictSettingsChanges);
    }

    [Fact]
    public void ShowsConflict_CaFunctionOff_HidesEveryAlertInTheFacility()
    {
        EramRoomSettings settings = Settings("CA CA FUNCTION OFF");

        Assert.False(settings.ShowsConflict(Facility, "44", isMciPair: false));
        Assert.False(settings.ShowsConflict(Facility, "44", isMciPair: true));
        Assert.False(settings.ShowsConflict(Facility, null, isMciPair: false));
        Assert.True(settings.ShowsConflict("ZLA", "44", isMciPair: false));
    }

    [Fact]
    public void ShowsConflict_MciFunctionOff_HidesOnlyMciPairs()
    {
        EramRoomSettings settings = Settings("CA MCI FUNCTION OFF");

        Assert.False(settings.ShowsConflict(Facility, "44", isMciPair: true));
        Assert.False(settings.ShowsConflict(Facility, null, isMciPair: true));
        Assert.True(settings.ShowsConflict(Facility, "44", isMciPair: false));
    }

    [Fact]
    public void ShowsConflict_SectorDisplayOff_HidesOnlyThatSector()
    {
        EramRoomSettings settings = Settings("CA CA DISPLAY 44 46 OFF");

        Assert.False(settings.ShowsConflict(Facility, "44", isMciPair: false));
        Assert.False(settings.ShowsConflict(Facility, "44", isMciPair: true));
        Assert.False(settings.ShowsConflict(Facility, "46", isMciPair: false));
        Assert.True(settings.ShowsConflict(Facility, "45", isMciPair: false));
        Assert.True(settings.ShowsConflict(Facility, null, isMciPair: false));
    }

    [Fact]
    public void ShowsConflict_MciDisplayOff_HidesOnlyMciPairsForThatSector()
    {
        EramRoomSettings settings = Settings("CA MCI DISPLAY 44 OFF");

        Assert.False(settings.ShowsConflict(Facility, "44", isMciPair: true));
        Assert.True(settings.ShowsConflict(Facility, "44", isMciPair: false));
        Assert.True(settings.ShowsConflict(Facility, "45", isMciPair: true));
    }

    [Fact]
    public void ShowsConflict_AnUnknownFacility_Shows()
    {
        var settings = new EramRoomSettings();

        Assert.True(settings.ShowsConflict("ZNY", "44", isMciPair: true));
        Assert.True(settings.ShowsConflict("ZNY", null, isMciPair: false));
    }

    /// <summary>
    /// <see cref="SimulationEngine.CaptureSnapshot"/> carries the settings, so an engine that never applied the record —
    /// a bundle reconstruction from a snapshot, a session restore — holds the same switches as the run that made them.
    /// </summary>
    [Fact]
    public void TheSnapshot_RoundTripsTheSettings()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        Assert.True(engine.Actions.ApplyRecorded(Entry(0, "CA MCI FUNCTION OFF"), host).Success);
        Assert.True(engine.Actions.ApplyRecorded(Entry(0, "CA CA DISPLAY 46 44 OFF"), host).Success);
        Assert.True(engine.Actions.ApplyRecorded(new RecordedEramRoomEntry(0, "ZLA", "CA MCI DISPLAY 12 OFF"), host).Success);

        StateSnapshotDto snapshot = engine.CaptureSnapshot();

        List<EramConflictSettingsSnapshotDto> captured = Assert.IsType<List<EramConflictSettingsSnapshotDto>>(snapshot.Server!.EramConflictSettings);
        Assert.Equal(["ZLA", "ZOA"], captured.Select(f => f.FacilityId));
        Assert.Equal(["44", "46"], captured[1].CaDisplayOffSectors);

        SimulationEngine restored = Engine()!;
        restored.RestoreFromSnapshot(snapshot);

        Assert.Equal(2, restored.EramRoomSettings.Facilities.Count);
        EramFacilityConflictSettings zoa = restored.EramRoomSettings.Facilities[Facility];
        Assert.True(zoa.CaFunctionOn);
        Assert.False(zoa.MciFunctionOn);
        Assert.Equal(["44", "46"], zoa.CaDisplayOffSectors.Order(StringComparer.Ordinal));
        Assert.Empty(zoa.MciDisplayOffSectors);
        Assert.Equal(["12"], restored.EramRoomSettings.Facilities["ZLA"].MciDisplayOffSectors);
    }

    /// <summary>
    /// A restore replaces the settings whole: a snapshot taken while every facility was at the defaults carries no
    /// section, and restoring it undoes whatever was switched off since.
    /// </summary>
    [Fact]
    public void ARestoreWithNoSection_ResetsToDefaults()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        StateSnapshotDto snapshot = engine.CaptureSnapshot();
        Assert.Null(snapshot.Server!.EramConflictSettings);
        Assert.True(engine.Actions.ApplyRecorded(Entry(0, "CA CA FUNCTION OFF"), new AttendanceActionHost()).Success);

        engine.RestoreFromSnapshot(snapshot);

        Assert.Empty(engine.EramRoomSettings.Facilities);
        Assert.True(engine.EramRoomSettings.ShowsConflict(Facility, "44", isMciPair: false));
    }

    /// <summary>
    /// The Sim replay pin: a recording whose log carries the entry a live run wrote rebuilds the settings, and a replay
    /// from zero starts from the defaults — a setting the replaying engine held before is gone.
    /// </summary>
    [Fact]
    public void AReplayFromZero_RebuildsTheSettings()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        for (int i = 0; i < 5; i++)
        {
            engine.TickOneSecond();
        }

        Assert.True(engine.Actions.IssueDerived(Entry(engine.Scenario!.ElapsedSeconds, "CA CA DISPLAY 44 OFF"), host).Success);
        SessionRecording recording = Recording(engine);

        var replayed = new SimulationEngine(new TestAirportGroundData());
        Assert.True(replayed.EramRoomSettings.TryApply(Facility, "CA MCI FUNCTION OFF"));
        replayed.Replay(recording, engine.Scenario!.ElapsedSeconds + 5);

        EramFacilityConflictSettings settings = Assert.Single(replayed.EramRoomSettings.Facilities.Values);
        Assert.True(settings.CaFunctionOn);
        Assert.True(settings.MciFunctionOn);
        Assert.Equal(["44"], settings.CaDisplayOffSectors);
        Assert.Empty(settings.MciDisplayOffSectors);
    }

    /// <summary>
    /// The settings are written under the room gate but read outside it (the per-tick CRC broadcast, the fan-out payload
    /// factories), so a reader racing the writer must never throw: four readers enumerate and query the settings while
    /// this thread applies entries that add, change and drop facilities.
    /// </summary>
    [Fact]
    public async Task ConcurrentReads_WhileAnotherThreadApplies_NeverThrow()
    {
        var settings = new EramRoomSettings();
        string[] entries =
        [
            "CA CA DISPLAY 44 46 OFF",
            "CA MCI FUNCTION OFF",
            "CA CA DISPLAY 44 ON",
            "CA MCI DISPLAY 12 OFF",
            "CA MCI FUNCTION ON",
            "CA CA DISPLAY 46 ON",
            "CA MCI DISPLAY 12 ON",
        ];
        const int readerCount = 4;
        using var started = new CountdownEvent(readerCount);
        using var stop = new CancellationTokenSource();
        Task[] readers = [.. Enumerable.Range(0, readerCount).Select(_ => Task.Run(() => ReadUntilStopped(settings, started, stop.Token)))];
        started.Wait(TestContext.Current.CancellationToken);

        for (int i = 0; i < 600; i++)
        {
            string facility = (i % 3) switch
            {
                0 => "ZOA",
                1 => "ZLA",
                _ => "ZSE",
            };
            Assert.True(settings.TryApply(facility, entries[i % entries.Length]));
        }

        await stop.CancelAsync();
        await Task.WhenAll(readers);
    }

    private static void ReadUntilStopped(EramRoomSettings settings, CountdownEvent started, CancellationToken stop)
    {
        started.Signal();
        while (!stop.IsCancellationRequested)
        {
            foreach (EramFacilityConflictSettings facility in settings.Facilities.Values)
            {
                int offSectors = facility.CaDisplayOffSectors.Count(id => id.Length > 0) + facility.MciDisplayOffSectors.Count(id => id.Length > 0);
                Assert.True(offSectors >= 0);
                Assert.True(settings.ShowsConflict(facility.FacilityId, "99", isMciPair: false) || !facility.CaFunctionOn);
            }
        }
    }
}
