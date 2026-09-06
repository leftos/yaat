using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Actions;

/// <summary>
/// The arms the live chain handed to Yaat.Sim when it moved onto the router: the flight-plan verbs (<c>FP</c> / <c>DA</c>
/// / <c>RMK</c>) are one Sim body that files through the engine, records the amendment the state travels in and tags
/// the filing position as the plan's creator; a bare <c>APT</c> is an aviation command whose text is recorded and
/// whose procedure clear runs on every run kind; <c>SHOWAT</c> is a query the host shows the issuing connection and
/// nothing records; a <c>DROP</c> of a pure ghost removes it from the world and tells the host; an <c>INHCA</c> drops
/// the aircraft's active conflicts.
/// </summary>
public class FlightPlanAndQueryArmTests
{
    private static readonly TrackOwner Student = TrackOwner.CreateStars("NCT_2B", "NCT", 2, "B");

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public FlightPlanAndQueryArmTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>The parked-at-OAK fixture under an NCT student, so an <c>AS 4U</c> resolves within the student's facility.</summary>
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

    private static ActionInput Fresh(string callsign, string command) => new(callsign, command, "conn-1", "XX", Baked: null);

    [Fact]
    public void Fp_Issue_FilesThePlan_RecordsTheAmendment_AndTagsTheCreator()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;

        var outcome = engine.Actions.Issue(Fresh(AiTestFixture.Callsign, "AS 4U FP C172/G 050 OAK SFO"), host);

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal(new ActionTrace(RecordedCommandKind.FlightPlan, ActionScope.Callsign, IsHostSlot: false), outcome.Trace);
        Assert.Equal("C172", aircraft.FlightPlan.AircraftType);
        Assert.Equal("G", aircraft.FlightPlan.EquipmentSuffix);
        Assert.Equal("KOAK", aircraft.FlightPlan.Departure);
        Assert.Equal("KSFO", aircraft.FlightPlan.Destination);
        Assert.Equal(5000, aircraft.FlightPlan.Altitude.CruiseFeet);
        Assert.Equal("IFR", aircraft.FlightPlan.FlightRules);
        Assert.NotNull(aircraft.FlightPlan.CreatedByOwner);
        Assert.Equal("U", aircraft.FlightPlan.CreatedByOwner.SectorId);
        Assert.Equal([AiTestFixture.Callsign], host.AmendedCallsigns);
        Assert.Contains($"{AiTestFixture.Callsign} C172/G", outcome.Result.Message);

        // The amendment the state travels in is recorded before the command's text, both by the router's run.
        var log = engine.Scenario!.ActionLog;
        var amendment = Assert.IsType<RecordedAmendFlightPlan>(log[^2]);
        Assert.Equal("KSFO", amendment.Amendment.Destination);
        var command = Assert.IsType<RecordedCommand>(log[^1]);
        Assert.Equal("AS 4U FP C172/G 050 OAK SFO", command.Command);
        Assert.True(command.Accepted);
    }

    [Fact]
    public void Da_IsCreateOnly_AndRefusesAnInvalidCallsign()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var duplicate = engine.Actions.Issue(Fresh(AiTestFixture.Callsign, "DA C172 050"));
        Assert.False(duplicate.Result.Success);
        Assert.Equal("DUP NEW ID", duplicate.Result.Message);
        Assert.Equal("C172", engine.FindAircraft(AiTestFixture.Callsign)!.FlightPlan.AircraftType);

        var invalid = engine.Actions.Issue(Fresh("FOO/BAR", "DA C172 050"));
        Assert.False(invalid.Result.Success);
        Assert.Equal("INVALID CALLSIGN", invalid.Result.Message);

        var unknown = engine.Actions.Issue(Fresh("N99999", "DA C172 050"));
        Assert.False(unknown.Result.Success);
        Assert.Equal("Aircraft 'N99999' not found", unknown.Result.Message);
        Assert.Null(engine.FindAircraft("N99999"));
    }

    [Fact]
    public void Fp_Apply_IsAuditOnly_ButTagsTheCreator()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        var before = aircraft.FlightPlan.Destination;

        var outcome = engine.Actions.Apply(new RecordedCommand(0, AiTestFixture.Callsign, "AS 4U FP B738 100 SFO LAX", "CRC", ""));

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal(before, aircraft.FlightPlan.Destination);
        Assert.Equal("C172", aircraft.FlightPlan.AircraftType);
        Assert.NotNull(aircraft.FlightPlan.CreatedByOwner);
        Assert.Equal("U", aircraft.FlightPlan.CreatedByOwner.SectorId);
        Assert.DoesNotContain(engine.Scenario!.ActionLog, a => a is RecordedAmendFlightPlan);
    }

    [Fact]
    public void Rmk_AmendsTheRemarks()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var outcome = engine.Actions.Issue(Fresh(AiTestFixture.Callsign, "REMARKS /v/ student solo"));

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal("Remarks updated", outcome.Result.Message);
        Assert.Equal("/v/ student solo", engine.FindAircraft(AiTestFixture.Callsign)!.FlightPlan.Remarks);
        Assert.Contains(engine.Scenario!.ActionLog, a => a is RecordedAmendFlightPlan { Amendment.Remarks: "/v/ student solo" });
    }

    [Fact]
    public void Apt_IsAnAviationCommand_RecordedAsText_WithTheProcedureClear()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        aircraft.Approach.Expected = "I28R";

        var outcome = engine.Actions.Issue(Fresh(AiTestFixture.Callsign, "APT SFO"), host);

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal(RecordedCommandKind.Compound, outcome.Trace.Kind);
        Assert.Equal("KSFO", aircraft.FlightPlan.Destination);
        Assert.Null(aircraft.Approach.Expected);
        Assert.Equal([AiTestFixture.Callsign], host.AmendedCallsigns);
        var recorded = Assert.IsType<RecordedCommand>(Assert.Single(engine.Scenario!.ActionLog, a => a is RecordedCommand));
        Assert.Equal("APT SFO", recorded.Command);
        Assert.DoesNotContain(engine.Scenario.ActionLog, a => a is RecordedAmendFlightPlan);
    }

    [Fact]
    public void ShowQueued_GoesToTheHost_AndIsNeverRecorded()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        var outcome = engine.Actions.Issue(Fresh(AiTestFixture.Callsign, "SHOWAT"), host);

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Null(outcome.Result.Message);
        Assert.Equal(new ActionTrace(RecordedCommandKind.ShowQueued, ActionScope.Aircraft, IsHostSlot: false), outcome.Trace);
        var shown = Assert.Single(host.ShownQueues);
        Assert.Equal(("conn-1", AiTestFixture.Callsign), (shown.ConnectionId, shown.Callsign));
        Assert.Equal(["No pending commands"], shown.Lines);
        Assert.Null(outcome.ToRecord);
        Assert.DoesNotContain(engine.Scenario!.ActionLog, a => a is RecordedCommand);
    }

    [Fact]
    public void Drop_OfAPureGhost_RemovesIt_AndTellsTheHost()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var placed = engine.Actions.Issue(Fresh("N77GH", "AS 4U GHOST N77GH 37.72 -122.22"), host);
        Assert.True(placed.Result.Success, placed.Result.Message);
        Assert.Equal(["N77GH"], host.SpawnedCallsigns);
        Assert.True(engine.FindAircraft("N77GH")!.Ghost.IsUnsupported);

        var dropped = engine.Actions.Issue(Fresh("N77GH", "AS 4U DROP"), host);

        Assert.True(dropped.Result.Success, dropped.Result.Message);
        Assert.Null(engine.FindAircraft("N77GH"));
        Assert.Equal(["N77GH"], host.DeletedCallsigns);
    }

    [Fact]
    public void Inhca_DropsTheAircraftsActiveConflicts()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        engine.ConflictAlerts.Conflicts["a"] = new ActiveConflict
        {
            Id = "a",
            CallsignA = AiTestFixture.Callsign,
            CallsignB = "UAL1",
        };
        engine.ConflictAlerts.Conflicts["b"] = new ActiveConflict
        {
            Id = "b",
            CallsignA = "UAL2",
            CallsignB = "UAL3",
        };

        var outcome = engine.Actions.Issue(Fresh(AiTestFixture.Callsign, "AS 4U CAINH"));

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.True(engine.FindAircraft(AiTestFixture.Callsign)!.Stars.IsCaInhibited);
        Assert.Equal(["b"], engine.ConflictAlerts.Conflicts.Keys.Order());
    }
}
