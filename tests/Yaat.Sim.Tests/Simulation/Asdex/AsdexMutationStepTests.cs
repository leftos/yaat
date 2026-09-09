using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Asdex;

/// <summary>
/// The CRC-sourced ASDE-X and SAAB SAID mutations on the bare engine: a <see cref="RecordedAsdexMutation"/> or
/// <see cref="RecordedSaidMutation"/> tags, terminates, suspends, unsuspends, inhibits alerts on or edits the DB
/// fields of one aircraft through <see cref="SimulationEngine.ApplyAsdexMutation"/> /
/// <see cref="SimulationEngine.ApplySaidMutation"/>, and <c>ASDXALERTS</c> sweeps every aircraft's alert inhibit
/// through <see cref="SimulationEngine.EnableAllAsdexAlerts"/>. The state is <c>AircraftStarsState</c>'s and is
/// snapshotted, so every run kind carries it — a bare engine, a replay and a reconstruction all end where the live
/// run did. The one thing that leaves the simulation is the terminate consumer, which the live room turns into its
/// one-shot delete marker.
/// </summary>
public class AsdexMutationStepTests
{
    private const string Parked = AiTestFixture.Callsign;
    private const string OnFinal = "N456TP";

    /// <summary>Two aircraft, because <c>ASDXALERTS</c> is a sweep and one aircraft cannot show that it swept.</summary>
    private const string TwoAtOak = """
        {
          "id": "asdex-two",
          "name": "ASDE-X two at OAK",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N152SP",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Parking", "parking": "SIG1" },
              "flightplan": {
                "rules": "VFR",
                "departure": "KOAK",
                "destination": "KOAK",
                "cruiseAltitude": 1500,
                "cruiseSpeed": 100,
                "route": "",
                "remarks": "",
                "aircraftType": "C172"
              }
            },
            {
              "id": "a2",
              "aircraftId": "N456TP",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "OnFinal", "runway": "28R", "distanceFromRunway": 5 },
              "flightplan": {
                "rules": "VFR",
                "departure": "KOAK",
                "destination": "KOAK",
                "cruiseAltitude": 1500,
                "cruiseSpeed": 100,
                "route": "",
                "remarks": "",
                "aircraftType": "C172"
              }
            }
          ]
        }
        """;

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public AsdexMutationStepTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? Engine() => _zoa is null ? null : AiTestFixture.Load(TwoAtOak, _zoa, 7, []);

    private static RecordedAsdexMutation Asdex(string kind, string? aircraftId) => new(0, kind, aircraftId, null, null, null, null, null, null, null);

    private static RecordedSaidMutation Said(string kind, string? aircraftId) => new(0, kind, aircraftId, null, null, null, null, null, null, null);

    /// <summary>An <c>EditDbFields</c> record carrying <paramref name="value"/> in one field's slot and nulls elsewhere.</summary>
    private static RecordedAsdexMutation AsdexEdit(AsdexEditField field, string value)
    {
        string? Slot(AsdexEditField slot) => slot == field ? value : null;
        return new RecordedAsdexMutation(
            0,
            "EditDbFields",
            Parked,
            Slot(AsdexEditField.Callsign),
            Slot(AsdexEditField.BeaconCode),
            Slot(AsdexEditField.Category),
            Slot(AsdexEditField.AircraftType),
            Slot(AsdexEditField.Fix),
            Slot(AsdexEditField.Scratchpad1),
            Slot(AsdexEditField.Scratchpad2)
        );
    }

    private static RecordedSaidMutation SaidEdit(SaidEditField field, string value)
    {
        string? Slot(SaidEditField slot) => slot == field ? value : null;
        return new RecordedSaidMutation(
            0,
            "EditDbFields",
            Parked,
            Slot(SaidEditField.Callsign),
            Slot(SaidEditField.BeaconCode),
            Slot(SaidEditField.Category),
            Slot(SaidEditField.AircraftType),
            Slot(SaidEditField.Fix),
            Slot(SaidEditField.Scratchpad1),
            Slot(SaidEditField.Scratchpad2)
        );
    }

    private static string? AsdexValue(AircraftStarsState stars, AsdexEditField field) =>
        field switch
        {
            AsdexEditField.Scratchpad1 => stars.AsdexScratchpad1,
            AsdexEditField.Scratchpad2 => stars.AsdexScratchpad2,
            AsdexEditField.Callsign => stars.AsdexCallsignOverride,
            AsdexEditField.BeaconCode => stars.AsdexBeaconCodeOverride,
            AsdexEditField.Category => stars.AsdexCategoryOverride,
            AsdexEditField.AircraftType => stars.AsdexAircraftTypeOverride,
            AsdexEditField.Fix => stars.AsdexFixOverride,
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown ASDE-X display field"),
        };

    private static string? SaidValue(AircraftStarsState stars, SaidEditField field) =>
        field switch
        {
            SaidEditField.Scratchpad1 => stars.SaidScratchpad1,
            SaidEditField.Scratchpad2 => stars.SaidScratchpad2,
            SaidEditField.Callsign => stars.SaidCallsignOverride,
            SaidEditField.BeaconCode => stars.SaidBeaconCodeOverride,
            SaidEditField.Category => stars.SaidCategoryOverride,
            SaidEditField.AircraftType => stars.SaidAircraftTypeOverride,
            SaidEditField.Fix => stars.SaidFixOverride,
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown SAID display field"),
        };

    /// <summary>A recording of everything the engine has run so far, carrying the ARTCC the replay re-initialises from.</summary>
    private SessionRecording Recording(SimulationEngine engine)
    {
        var scenario = engine.Scenario!;
        return new SessionRecording
        {
            ScenarioJson = TwoAtOak,
            RngSeed = 7,
            Actions = [.. scenario.ActionLog],
            TotalElapsedSeconds = scenario.ElapsedSeconds,
            ArtccConfigJson = JsonSerializer.Serialize(_zoa, RecordingJsonOptions.Default),
            SessionStartUtc = MagneticDeclination.EvaluationDateUtc,
            StudentPositionState = new ReplayStudentPosition(scenario.StudentPosition, scenario.StudentTcp, scenario.StudentPositionType, false),
        };
    }

    // --- ASDE-X per-aircraft verbs ---

    /// <summary>CRC's untermination: the tag clears the terminated bit so the next tick re-emits the track.</summary>
    [Fact]
    public void ATagRecord_ClearsTheTerminatedBit()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        engine.FindAircraft(Parked)!.Stars.AsdexTerminated = true;

        Assert.True(engine.Actions.ApplyRecorded(Asdex("Tag", Parked), host).Success);

        Assert.False(engine.FindAircraft(Parked)!.Stars.AsdexTerminated);
        Assert.Empty(host.AsdexTerminations);
    }

    /// <summary>
    /// Terminate is the one mutation with a consumer: the bit is engine state every run kind carries, and the live
    /// room turns the notification into the one-shot delete marker its broadcaster drains.
    /// </summary>
    [Fact]
    public void ATerminateRecord_SetsTheBit_AndTellsTheHostOnce()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        Assert.True(engine.Actions.ApplyRecorded(Asdex("Terminate", Parked), host).Success);

        Assert.True(engine.FindAircraft(Parked)!.Stars.AsdexTerminated);
        Assert.Equal(Parked, Assert.Single(host.AsdexTerminations));
        Assert.Empty(host.SaidTerminations);
    }

    [Fact]
    public void ASuspendRecord_SetsTheBit_AndUnsuspendClearsIt()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        Assert.True(engine.Actions.ApplyRecorded(Asdex("Suspend", Parked), host).Success);
        Assert.True(engine.FindAircraft(Parked)!.Stars.AsdexSuspended);

        Assert.True(engine.Actions.ApplyRecorded(Asdex("Unsuspend", Parked), host).Success);
        Assert.False(engine.FindAircraft(Parked)!.Stars.AsdexSuspended);
    }

    [Fact]
    public void AnInhibitAlertsRecord_SetsTheInhibitOnThatAircraftAlone()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        Assert.True(engine.Actions.ApplyRecorded(Asdex("InhibitAlerts", Parked), host).Success);

        Assert.True(engine.FindAircraft(Parked)!.Stars.AsdexAlertsInhibited);
        Assert.False(engine.FindAircraft(OnFinal)!.Stars.AsdexAlertsInhibited);
    }

    /// <summary>An <c>EditDbFields</c> writes every field it carries and leaves a null one alone.</summary>
    [Fact]
    public void AnEditRecord_WritesTheFieldsItCarries_AndLeavesANullFieldAlone()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var edit = new RecordedAsdexMutation(0, "EditDbFields", Parked, "OVRCS", "1200", "OVR", "OVRTYPE", "OVRFIX", "SP1", null);

        Assert.True(engine.Actions.ApplyRecorded(edit, host).Success);

        var stars = engine.FindAircraft(Parked)!.Stars;
        Assert.Equal("OVRCS", stars.AsdexCallsignOverride);
        Assert.Equal("1200", stars.AsdexBeaconCodeOverride);
        Assert.Equal("OVR", stars.AsdexCategoryOverride);
        Assert.Equal("OVRTYPE", stars.AsdexAircraftTypeOverride);
        Assert.Equal("OVRFIX", stars.AsdexFixOverride);
        Assert.Equal("SP1", stars.AsdexScratchpad1);
        Assert.Null(stars.AsdexScratchpad2);
    }

    public static TheoryData<AsdexEditField> AsdexFields => [.. Enum.GetValues<AsdexEditField>()];

    public static TheoryData<SaidEditField> SaidFields => [.. Enum.GetValues<SaidEditField>()];

    /// <summary>
    /// CRC's editor echoes the whole displayed row back (its <c>ConstructDbFieldsDto</c> copies the track's current
    /// fields), so an unset one arrives as an empty string rather than as nil — and an empty string is a value the
    /// record carries, not the "clear this override" the typed <c>ASDXSP1</c> form means by it. Every field, because
    /// the four that fall back to a derived value on the wire (callsign, category, type, fix) would show the derived
    /// value instead of the blank the controller is looking at.
    /// </summary>
    [Theory]
    [MemberData(nameof(AsdexFields))]
    public void AnEditRecordCarryingAnEmptyField_StoresItAsWritten(AsdexEditField field)
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var aircraft = engine.FindAircraft(Parked)!;
        TrackEngine.SetAsdexField(aircraft, field, "AB");
        Assert.Equal("AB", AsdexValue(aircraft.Stars, field));

        Assert.True(engine.Actions.ApplyRecorded(AsdexEdit(field, ""), host).Success);

        Assert.Equal("", AsdexValue(aircraft.Stars, field));
    }

    /// <summary>The SAID twin: its editor is the same CRC form on the <c>Said*</c> slots.</summary>
    [Theory]
    [MemberData(nameof(SaidFields))]
    public void ASaidEditRecordCarryingAnEmptyField_StoresItAsWritten(SaidEditField field)
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var aircraft = engine.FindAircraft(Parked)!;
        TrackEngine.SetSaidField(aircraft, field, "AB");
        Assert.Equal("AB", SaidValue(aircraft.Stars, field));

        Assert.True(engine.Actions.ApplyRecorded(SaidEdit(field, ""), host).Success);

        Assert.Equal("", SaidValue(aircraft.Stars, field));
    }

    /// <summary>An aircraft that has left the world takes the mutation silently, the way the room's applier did.</summary>
    [Fact]
    public void AMutationForAnUnknownAircraft_IsANoOp()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        Assert.True(engine.Actions.ApplyRecorded(Asdex("Terminate", "NOPE999"), host).Success);
        Assert.True(engine.Actions.ApplyRecorded(Said("Terminate", "NOPE999"), host).Success);

        Assert.Empty(host.AsdexTerminations);
        Assert.Empty(host.SaidTerminations);
    }

    /// <summary>
    /// The lookup is by callsign alone, not by the room's FLID resolver (callsign, then CID, then assigned beacon): a
    /// surface mutation names a surface track, whose id is <c>CALLSIGN{callsign}</c>. A record carrying the aircraft's
    /// CID therefore matches nothing — which is what the wire can never send.
    /// </summary>
    [Fact]
    public void AMutationNamingTheAircraftsCid_AppliesNothing()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var aircraft = engine.FindAircraft(Parked)!;
        aircraft.Cid = "123";

        Assert.True(engine.Actions.ApplyRecorded(Asdex("Terminate", aircraft.Cid), host).Success);
        Assert.True(engine.Actions.ApplyRecorded(Said("Terminate", aircraft.Cid), host).Success);

        Assert.False(aircraft.Stars.AsdexTerminated);
        Assert.False(aircraft.Stars.SaidTerminated);
        Assert.Empty(host.AsdexTerminations);
        Assert.Empty(host.SaidTerminations);
    }

    // --- SAID: the same shape on the Said* fields, with no alerts ---

    [Fact]
    public void ASaidTagRecord_ClearsTheTerminatedBit()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        engine.FindAircraft(Parked)!.Stars.SaidTerminated = true;

        Assert.True(engine.Actions.ApplyRecorded(Said("Tag", Parked), host).Success);

        Assert.False(engine.FindAircraft(Parked)!.Stars.SaidTerminated);
    }

    [Fact]
    public void ASaidTerminateRecord_SetsTheBit_AndTellsTheHostOnce()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        Assert.True(engine.Actions.ApplyRecorded(Said("Terminate", Parked), host).Success);

        Assert.True(engine.FindAircraft(Parked)!.Stars.SaidTerminated);
        Assert.Equal(Parked, Assert.Single(host.SaidTerminations));
        Assert.Empty(host.AsdexTerminations);
    }

    [Fact]
    public void ASaidSuspendRecord_SetsTheBit_AndUnsuspendClearsIt()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        Assert.True(engine.Actions.ApplyRecorded(Said("Suspend", Parked), host).Success);
        Assert.True(engine.FindAircraft(Parked)!.Stars.SaidSuspended);

        Assert.True(engine.Actions.ApplyRecorded(Said("Unsuspend", Parked), host).Success);
        Assert.False(engine.FindAircraft(Parked)!.Stars.SaidSuspended);
    }

    /// <summary>SAID keeps its own override fields; an ASDE-X row on the same aircraft is untouched by a SAID edit.</summary>
    [Fact]
    public void ASaidEditRecord_WritesTheSaidFieldsOnly()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var edit = new RecordedSaidMutation(0, "EditDbFields", Parked, "OVRCS", "1200", "OVR", "OVRTYPE", "OVRFIX", "SP1", "SP2");

        Assert.True(engine.Actions.ApplyRecorded(edit, host).Success);

        var stars = engine.FindAircraft(Parked)!.Stars;
        Assert.Equal("OVRCS", stars.SaidCallsignOverride);
        Assert.Equal("1200", stars.SaidBeaconCodeOverride);
        Assert.Equal("OVR", stars.SaidCategoryOverride);
        Assert.Equal("OVRTYPE", stars.SaidAircraftTypeOverride);
        Assert.Equal("OVRFIX", stars.SaidFixOverride);
        Assert.Equal("SP1", stars.SaidScratchpad1);
        Assert.Equal("SP2", stars.SaidScratchpad2);
        Assert.Null(stars.AsdexCallsignOverride);
        Assert.Null(stars.AsdexScratchpad1);
    }

    // --- The global sweep ---

    /// <summary>
    /// <c>ASDXALERTS</c> is a per-aircraft sweep, not room state: every aircraft's inhibit is cleared, on every run
    /// kind.
    /// </summary>
    [Fact]
    public void AsdexEnableAllAlerts_ClearsTheInhibitOnEveryAircraft()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        Assert.True(engine.Actions.ApplyRecorded(Asdex("InhibitAlerts", Parked), host).Success);
        Assert.True(engine.Actions.ApplyRecorded(Asdex("InhibitAlerts", OnFinal), host).Success);

        var result = engine.Actions.Issue(new ActionInput("", "ASDXALERTS", "conn-1", "XX", Baked: null), host).Result;

        Assert.True(result.Success, result.Message);
        Assert.False(engine.FindAircraft(Parked)!.Stars.AsdexAlertsInhibited);
        Assert.False(engine.FindAircraft(OnFinal)!.Stars.AsdexAlertsInhibited);
    }

    /// <summary>The recorded <c>EnableAllAlerts</c> mutation a CRC client's menu sends is the same sweep.</summary>
    [Fact]
    public void AnEnableAllAlertsRecord_ClearsTheInhibitOnEveryAircraft()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        Assert.True(engine.Actions.ApplyRecorded(Asdex("InhibitAlerts", Parked), host).Success);
        Assert.True(engine.Actions.ApplyRecorded(Asdex("InhibitAlerts", OnFinal), host).Success);

        Assert.True(engine.Actions.ApplyRecorded(Asdex("EnableAllAlerts", null), host).Success);

        Assert.False(engine.FindAircraft(Parked)!.Stars.AsdexAlertsInhibited);
        Assert.False(engine.FindAircraft(OnFinal)!.Stars.AsdexAlertsInhibited);
    }

    // --- The replay pin ---

    /// <summary>
    /// A recording whose log carries the mutations a live CRC session wrote rebuilds the same ASDE-X and SAID state on
    /// a fresh engine — the pin that a Sim-side replay of a CRC session ends on the surface display the room had.
    /// </summary>
    [Fact]
    public void Replay_RebuildsTheAsdexStateFromTheRecordedMutations()
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

        var elapsed = engine.Scenario!.ElapsedSeconds;
        var edit = new RecordedAsdexMutation(elapsed, "EditDbFields", Parked, null, null, null, null, null, "AB", null);
        var suspend = new RecordedAsdexMutation(elapsed, "Suspend", Parked, null, null, null, null, null, null, null);
        var saidTerminate = new RecordedSaidMutation(elapsed, "Terminate", Parked, null, null, null, null, null, null, null);
        Assert.True(engine.Actions.IssueDerived(edit, host).Success);
        Assert.True(engine.Actions.IssueDerived(suspend, host).Success);
        Assert.True(engine.Actions.IssueDerived(saidTerminate, host).Success);

        var recording = Recording(engine);

        var replayed = new SimulationEngine(new TestAirportGroundData());
        replayed.Replay(recording, elapsed + 5);

        var stars = replayed.FindAircraft(Parked)!.Stars;
        Assert.Equal("AB", stars.AsdexScratchpad1);
        Assert.True(stars.AsdexSuspended);
        Assert.True(stars.SaidTerminated);
    }
}
