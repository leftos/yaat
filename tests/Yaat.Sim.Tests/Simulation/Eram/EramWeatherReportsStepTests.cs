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
/// The entered ERAM weather reports (the <c>WX</c> entry) on the bare engine: a <see cref="RecordedEramRoomEntry"/> of the
/// shape <c>WX {station} {hhmm} {text}</c> stores one station's report stamped with the session-clock instant of the
/// entry, keyed by the station's METAR id with the last entry winning; the report expires at the first :53 after its
/// entry, by sim time; the snapshot carries the reports and a replay from zero rebuilds them.
/// </summary>
public class EramWeatherReportsStepTests
{
    private const string Facility = "ZOA";

    /// <summary>A session clock ten seconds short of a routine observation instant (12:53Z).</summary>
    private static readonly DateTime TenSecondsBeforeRoutine = new(2026, 6, 1, 12, 52, 50, DateTimeKind.Utc);

    private static readonly DateTime Routine = new(2026, 6, 1, 12, 53, 0, DateTimeKind.Utc);

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public EramWeatherReportsStepTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? Engine() => _zoa is null ? null : AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);

    private static RecordedEramRoomEntry Entry(double elapsed, string entry) => new(elapsed, Facility, entry);

    /// <summary>Anchors the engine's session clock so its current second is <paramref name="nowUtc"/>.</summary>
    private static void SetClock(SimulationEngine engine, DateTime nowUtc)
    {
        SimScenarioState scenario = engine.Scenario!;
        scenario.SessionStartUtc = nowUtc.AddSeconds(-scenario.ElapsedSeconds);
    }

    /// <summary>A recording of everything the engine has run so far, carrying the ARTCC and the session clock it ran on.</summary>
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
            SessionStartUtc = scenario.SessionStartUtc,
            StudentPositionState = new ReplayStudentPosition(scenario.StudentPosition, scenario.StudentTcp, scenario.StudentPositionType, false),
        };
    }

    private static EramWeatherReport? Report(SimulationEngine engine, string station) =>
        engine.EramWeatherReports.Reports.SingleOrDefault(r => r.StationId == station);

    [Fact]
    public void WxEntry_StoresPerStation_StampedWithTheSessionClock()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        DateTime sessionStart = engine.Scenario!.SessionStartUtc;

        Assert.True(engine.Actions.ApplyRecorded(Entry(0, "WX KOAK 1250 27012KT 10SM CLR A2992"), host).Success);
        Assert.True(engine.Actions.ApplyRecorded(Entry(30, "WX SFO 1245 FOG BANK WEST"), host).Success);

        Assert.Equal(new EramWeatherReport("KOAK", "1250", "27012KT 10SM CLR A2992", sessionStart), Report(engine, "KOAK"));
        Assert.Equal(new EramWeatherReport("KSFO", "1245", "FOG BANK WEST", sessionStart.AddSeconds(30)), Report(engine, "KSFO"));
        Assert.Equal(0, host.EramConflictSettingsChanges);
        Assert.Empty(engine.EramSectorMessages.Messages);
    }

    [Fact]
    public void WxEntry_LastEntryWinsPerStation()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        Assert.True(engine.Actions.ApplyRecorded(Entry(0, "WX KOAK 1250 FIRST"), host).Success);
        Assert.True(engine.Actions.ApplyRecorded(Entry(1, "WX koak 1251 SECOND REPORT"), host).Success);

        EramWeatherReport report = Assert.Single(engine.EramWeatherReports.Reports);
        Assert.Equal("KOAK", report.StationId);
        Assert.Equal("1251", report.ObservationTime);
        Assert.Equal("SECOND REPORT", report.Text);

        // A three-letter FAA id names the same station as its METAR id: one report, the later entry.
        Assert.True(engine.Actions.ApplyRecorded(Entry(2, "WX OAK 1252 THIRD"), host).Success);

        report = Assert.Single(engine.EramWeatherReports.Reports);
        Assert.Equal(new EramWeatherReport("KOAK", "1252", "THIRD", engine.Scenario!.SessionStartUtc.AddSeconds(2)), report);
    }

    [Theory]
    [InlineData("WX")]
    [InlineData("WX ")]
    [InlineData("WX KOAK")]
    [InlineData("WX KOAK 1250")]
    [InlineData("WX KOAK 1250 ")]
    [InlineData("WX KOAK 1250  TEXT")]
    [InlineData("WX KOAK 1250 TEXT ")]
    [InlineData("WX K 1250 TEXT")]
    [InlineData("WX KOAKXX 1250 TEXT")]
    [InlineData("WX K0AK 1250 TEXT")]
    [InlineData("WX KOAK 125 TEXT")]
    [InlineData("WX KOAK 12A0 TEXT")]
    [InlineData("WX KOAK 2400 TEXT")]
    [InlineData("WX KOAK 1260 TEXT")]
    [InlineData("wx KOAK 1250 TEXT")]
    [InlineData("WXX KOAK 1250 TEXT")]
    public void MalformedWxEntry_Fails(string entry)
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        Assert.True(engine.Actions.ApplyRecorded(Entry(0, "WX KSFO 1250 KEEP"), host).Success);

        CommandResult applied = engine.Actions.ApplyRecorded(Entry(1, entry), host);

        Assert.False(applied.Success);
        EramWeatherReport report = Assert.Single(engine.EramWeatherReports.Reports);
        Assert.Equal("KEEP", report.Text);
    }

    /// <summary>
    /// <see cref="SimulationEngine.CaptureSnapshot"/> carries the reports in station order, and a restore replaces them
    /// whole: a snapshot taken while no station held one carries no section and restores none.
    /// </summary>
    [Fact]
    public void WeatherReports_SurviveSnapshotRoundTrip()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        StateSnapshotDto empty = engine.CaptureSnapshot();
        Assert.Null(empty.Server!.EramWeatherReports);

        var host = new AttendanceActionHost();
        Assert.True(engine.Actions.ApplyRecorded(Entry(0, "WX KSFO 1250 SECOND"), host).Success);
        Assert.True(engine.Actions.ApplyRecorded(Entry(5, "WX KOAK 1245 FIRST"), host).Success);

        StateSnapshotDto snapshot = engine.CaptureSnapshot();

        List<EramWeatherReportSnapshotDto> captured = Assert.IsType<List<EramWeatherReportSnapshotDto>>(snapshot.Server!.EramWeatherReports);
        Assert.Equal(["KOAK/1245/FIRST", "KSFO/1250/SECOND"], captured.Select(r => $"{r.StationId}/{r.ObservationTime}/{r.Text}"));

        SimulationEngine restored = Engine()!;
        restored.RestoreFromSnapshot(snapshot);

        Assert.Equal(engine.EramWeatherReports.Reports.OrderBy(r => r.StationId), restored.EramWeatherReports.Reports.OrderBy(r => r.StationId));

        restored.RestoreFromSnapshot(empty);

        Assert.Empty(restored.EramWeatherReports.Reports);
    }

    /// <summary>
    /// The Sim replay pin: a recording whose log carries the entries a live run wrote rebuilds the same reports, entry
    /// instants included, and a replay from zero starts from none — a report the replaying engine held before is gone.
    /// </summary>
    [Fact]
    public void ReplayFromZero_ReproducesWeatherReports()
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

        double now = engine.Scenario!.ElapsedSeconds;
        Assert.True(engine.Actions.IssueDerived(Entry(now, "WX KOAK 1250 FIRST"), host).Success);
        Assert.True(engine.Actions.IssueDerived(Entry(now, "WX KSFO 1250 SECOND"), host).Success);
        Assert.True(engine.Actions.IssueDerived(Entry(now, "WX KSFO 1251 THIRD"), host).Success);
        SessionRecording recording = Recording(engine);

        var replayed = new SimulationEngine(new TestAirportGroundData());
        Assert.True(replayed.EramWeatherReports.TryApply("WX KSJC 1200 STALE", TenSecondsBeforeRoutine));
        replayed.Replay(recording, now + 5);

        Assert.Equal(engine.EramWeatherReports.Reports.OrderBy(r => r.StationId), replayed.EramWeatherReports.Reports.OrderBy(r => r.StationId));
        Assert.Null(Report(replayed, "KSJC"));
    }

    [Fact]
    public void Clear_RemovesEveryReport()
    {
        var reports = new EramWeatherReports();
        Assert.True(reports.TryApply("WX KOAK 1250 ONE", TenSecondsBeforeRoutine));
        Assert.True(reports.TryApply("WX KSFO 1250 TWO", TenSecondsBeforeRoutine));

        reports.Clear();

        Assert.Empty(reports.Reports);
    }

    /// <summary>
    /// A report stands until the next :53 routine instant after its entry, by the session clock: the second that reaches
    /// 12:53:00 removes it from the store, with no issuer running. A report entered at 12:53:00 itself stands until 13:53.
    /// </summary>
    [Fact]
    public void Report_ExpiresAtTheNextRoutineInstant_BySimTime()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SetClock(engine, TenSecondsBeforeRoutine);
        var actionHost = new AttendanceActionHost();
        Assert.True(engine.Actions.IssueDerived(Entry(engine.Scenario!.ElapsedSeconds, "WX KOAK 1250 ENTERED"), actionHost).Success);
        Assert.Equal(Routine, Report(engine, "KOAK")!.ExpiresAtUtc);

        for (int i = 0; i < 9; i++)
        {
            engine.TickOneSecond();
            Assert.Single(engine.EramWeatherReports.Reports);
            Assert.NotNull(Report(engine, "KOAK"));
        }

        engine.TickOneSecond();

        Assert.Equal(Routine, engine.Scenario.SimTimeUtc);
        Assert.Empty(engine.EramWeatherReports.Reports);

        Assert.True(engine.EramWeatherReports.TryApply("WX KOAK 1253 AT THE ROUTINE", Routine));
        Assert.Equal(Routine.AddHours(1), Report(engine, "KOAK")!.ExpiresAtUtc);
    }

    /// <summary>
    /// A replay reproduces show-then-expire: replayed to a second before the :53 instant the report stands, replayed past
    /// it the report is gone, with no issuer in either run.
    /// </summary>
    [Fact]
    public void Replay_ReproducesShowThenExpire()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SetClock(engine, TenSecondsBeforeRoutine);
        double entered = engine.Scenario!.ElapsedSeconds;
        Assert.True(engine.Actions.IssueDerived(Entry(entered, "WX KOAK 1250 ENTERED"), new AttendanceActionHost()).Success);
        for (int i = 0; i < 15; i++)
        {
            engine.TickOneSecond();
        }

        SessionRecording recording = Recording(engine);

        var before = new SimulationEngine(new TestAirportGroundData());
        before.Replay(recording, entered + 9);
        Assert.Equal("ENTERED", Assert.Single(before.EramWeatherReports.Reports).Text);

        var after = new SimulationEngine(new TestAirportGroundData());
        after.Replay(recording, entered + 11);
        Assert.Empty(after.EramWeatherReports.Reports);
    }
}
