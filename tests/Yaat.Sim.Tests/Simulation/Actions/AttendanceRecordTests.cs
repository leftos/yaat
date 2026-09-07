using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Actions;

/// <summary>
/// <see cref="RecordedAttendanceChange"/> as a recorded input: issuing one replaces the engine's
/// <see cref="Attendance"/>, the set survives a snapshot round-trip and the polymorphic recording serializer, and the
/// record lands in the action log so a replay reproduces it. Real NCT hierarchy from the ZOA config: <c>4Q</c>
/// auto-consolidates under <c>4U</c>.
/// </summary>
public class AttendanceRecordTests
{
    private static readonly TrackOwner Student = TrackOwner.CreateStars("NCT_2B", "NCT", 2, "B");

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public AttendanceRecordTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? Engine()
    {
        if (_zoa is null)
        {
            return null;
        }

        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        var scenario = engine.Scenario!;
        scenario.StudentPosition = Student;
        scenario.StudentTcp = TrackResolver.FindTcpByCode(scenario, "2B")!;
        return engine;
    }

    private static Tcp Tcp(SimulationEngine engine, string code) => TrackResolver.FindTcpByCode(engine.Scenario!, code)!;

    [Fact]
    public void AttendingATcp_MakesItAttended_AndControlsItsConsolidatedChild()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AttendanceTestSupport.Attend(engine, "4U");

        Assert.True(engine.Attendance.IsTcpAttended(Tcp(engine, "4U")));
        Assert.False(engine.Attendance.IsTcpAttended(Tcp(engine, "4Q")));
        Assert.True(engine.Attendance.IsTcpControlledByCrc(Tcp(engine, "4Q"), engine.Scenario!, engine.ConsolidationState));
    }

    [Fact]
    public void AnEmptyRecord_ClearsTheAttendedSet()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AttendanceTestSupport.Attend(engine, "4U");
        engine.Actions.IssueDerived(new RecordedAttendanceChange(engine.Scenario!.ElapsedSeconds, []));

        Assert.False(engine.Attendance.IsTcpAttended(Tcp(engine, "4U")));
        Assert.False(engine.Attendance.IsTcpControlledByCrc(Tcp(engine, "4Q"), engine.Scenario!, engine.ConsolidationState));
        Assert.Empty(engine.Attendance.PositionIds);
    }

    /// <summary>
    /// The room's ARTCC config is the authority on what a position is, but an id it cannot place is still attendance:
    /// the entry is kept by id so the set is never silently under-reported, and it matches no TCP.
    /// </summary>
    [Fact]
    public void AnUnresolvablePositionId_IsKeptByIdAlone()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        engine.Actions.IssueDerived(new RecordedAttendanceChange(engine.Scenario!.ElapsedSeconds, ["NOPE"]));

        Assert.Contains("NOPE", engine.Attendance.PositionIds);
        Assert.False(engine.Attendance.IsTcpAttended(Tcp(engine, "4U")));
        Assert.False(engine.Attendance.IsTcpAttended(Tcp(engine, "4Q")));
    }

    [Fact]
    public void ASnapshot_CarriesTheAttendedPositionIds_ToAFreshEngine()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AttendanceTestSupport.Attend(engine, "4U");
        var expected = engine.Attendance.PositionIds;
        Assert.NotEmpty(expected);

        var snapshot = engine.CaptureSnapshot(actionIndex: 0);

        if (Engine() is not { } restored)
        {
            return;
        }

        restored.RestoreFromSnapshot(snapshot);

        Assert.Equal(expected, restored.Attendance.PositionIds);
        Assert.True(restored.Attendance.IsTcpAttended(Tcp(restored, "4U")));
    }

    [Fact]
    public void TheRecord_RoundTripsThePolymorphicRecordingSerializer()
    {
        RecordedAction record = new RecordedAttendanceChange(42, ["01GEAMB98RKCPP9HCNPW5AVDA5", "01GEAS78D6ZW10PQJ90P44Q364"]);

        var json = JsonSerializer.Serialize(record, RecordingJsonOptions.Default);
        var restored = JsonSerializer.Deserialize<RecordedAction>(json, RecordingJsonOptions.Default);

        var attendance = Assert.IsType<RecordedAttendanceChange>(restored);
        Assert.Equal(42, attendance.ElapsedSeconds);
        Assert.Equal(["01GEAMB98RKCPP9HCNPW5AVDA5", "01GEAS78D6ZW10PQJ90P44Q364"], attendance.AttendedPositionIds);
    }

    [Fact]
    public void IssuingTheRecord_AppendsItToTheActionLog()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AttendanceTestSupport.Attend(engine, "4U");

        var recorded = Assert.Single(engine.Scenario!.ActionLog.OfType<RecordedAttendanceChange>());
        Assert.Equal(engine.Attendance.PositionIds, recorded.AttendedPositionIds);
    }
}
