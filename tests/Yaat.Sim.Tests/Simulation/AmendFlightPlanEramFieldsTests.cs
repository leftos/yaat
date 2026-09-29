using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The ERAM-only flight-plan fields ERAM <c>AM</c> writes: requested altitude (RAL, field 09), the special aircraft
/// indicator (SAI, field 22) and the number of aircraft (NUM, field 21). They survive a snapshot round trip and a
/// recorded amendment, and a beacon-code deletion never draws a fresh discrete code.
/// </summary>
public class AmendFlightPlanEramFieldsTests(ITestOutputHelper output)
{
    private const string Callsign = "AAL123";

    private SimulationEngine BuildEngine()
    {
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    private static AircraftState Aircraft(bool hasFlightPlan, uint assignedCode) =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Transponder = new AircraftTransponder
            {
                Code = 4571,
                AssignedCode = assignedCode,
                Mode = "C",
            },
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "IFR",
                HasFlightPlan = hasFlightPlan,
                AircraftType = "B738",
                EquipmentSuffix = "L",
            },
        };

    [Fact]
    public void Snapshot_RoundTripsRequestedAltitudeSaiAndNum()
    {
        AircraftState ac = Aircraft(hasFlightPlan: true, assignedCode: 4571);
        ac.FlightPlan.RequestedAltitude = PlannedAltitude.Block(33000, 37000);
        ac.FlightPlan.HasSpecialAircraftIndicator = true;
        ac.FlightPlan.NumberOfAircraft = 2;

        string json = JsonSerializer.Serialize(ac.ToSnapshot(), RecordingJsonOptions.Default);
        AircraftSnapshotDto? dto = JsonSerializer.Deserialize<AircraftSnapshotDto>(json, RecordingJsonOptions.Default);
        Assert.NotNull(dto);
        var restored = AircraftState.FromSnapshot(dto, null);

        Assert.Equal(PlannedAltitude.Block(33000, 37000), restored.FlightPlan.RequestedAltitude);
        Assert.True(restored.FlightPlan.HasSpecialAircraftIndicator);
        Assert.Equal(2, restored.FlightPlan.NumberOfAircraft);
    }

    [Fact]
    public void Snapshot_WithoutTheFields_RestoresTheirDefaults()
    {
        // A snapshot written before the fields existed: serialize with them set, then take them out of the JSON.
        AircraftState ac = Aircraft(hasFlightPlan: true, assignedCode: 4571);
        ac.FlightPlan.RequestedAltitude = PlannedAltitude.Ifr(37000);
        ac.FlightPlan.HasSpecialAircraftIndicator = true;
        ac.FlightPlan.NumberOfAircraft = 2;
        JsonObject snapshot = JsonSerializer.SerializeToNode(ac.ToSnapshot(), RecordingJsonOptions.Default)!.AsObject();
        JsonObject flightPlan = snapshot[nameof(AircraftSnapshotDto.FlightPlan)]!.AsObject();
        Assert.True(flightPlan.Remove(nameof(AircraftFlightPlanDto.RequestedAltitude)));
        Assert.True(flightPlan.Remove(nameof(AircraftFlightPlanDto.HasSpecialAircraftIndicator)));
        Assert.True(flightPlan.Remove(nameof(AircraftFlightPlanDto.NumberOfAircraft)));

        AircraftSnapshotDto? dto = snapshot.Deserialize<AircraftSnapshotDto>(RecordingJsonOptions.Default);
        Assert.NotNull(dto);
        var restored = AircraftState.FromSnapshot(dto, null);

        Assert.Null(restored.FlightPlan.RequestedAltitude);
        Assert.False(restored.FlightPlan.HasSpecialAircraftIndicator);
        Assert.Null(restored.FlightPlan.NumberOfAircraft);
    }

    [Fact]
    public void RecordedAmendment_WithoutClearBeaconCode_DeserializesAsNotClearing()
    {
        // A recording written before the flag existed: serialize with it set, then take it out of the JSON.
        var recorded = new RecordedAmendFlightPlan(12.0, Callsign, new FlightPlanAmendment(ClearBeaconCode: true, Remarks: "NEW"), null);
        JsonObject action = JsonSerializer.SerializeToNode<RecordedAction>(recorded, RecordingJsonOptions.Default)!.AsObject();
        JsonObject amendment = action[nameof(RecordedAmendFlightPlan.Amendment)]!.AsObject();
        Assert.True(amendment.Remove(nameof(FlightPlanAmendment.ClearBeaconCode)));

        RecordedAction? read = action.Deserialize<RecordedAction>(RecordingJsonOptions.Default);

        RecordedAmendFlightPlan amend = Assert.IsType<RecordedAmendFlightPlan>(read);
        Assert.False(amend.Amendment.ClearBeaconCode);
        Assert.Equal("NEW", amend.Amendment.Remarks);
    }

    [Fact]
    public void RecordedAmendment_ReplayedFromJson_RestoresRequestedAltitudeSaiAndNum()
    {
        SimulationEngine engine = BuildEngine();
        engine.World.AddAircraft(Aircraft(hasFlightPlan: true, assignedCode: 4571));
        var recorded = new RecordedAmendFlightPlan(
            12.0,
            Callsign,
            new FlightPlanAmendment(
                ClearBeaconCode: false,
                RequestedAltitude: PlannedAltitude.Ifr(35000),
                SpecialAircraftIndicator: true,
                NumberOfAircraft: 3
            ),
            null
        );

        string json = JsonSerializer.Serialize<RecordedAction>(recorded, RecordingJsonOptions.Default);
        RecordedAction? read = JsonSerializer.Deserialize<RecordedAction>(json, RecordingJsonOptions.Default);
        Assert.NotNull(read);
        CommandResult result = engine.Actions.ApplyRecorded(read);

        Assert.True(result.Success, result.Message);
        AircraftFlightPlan plan = engine.FindAircraft(Callsign)!.FlightPlan;
        Assert.Equal(PlannedAltitude.Ifr(35000), plan.RequestedAltitude);
        Assert.True(plan.HasSpecialAircraftIndicator);
        Assert.Equal(3, plan.NumberOfAircraft);
    }

    [Fact]
    public void Amendment_DeletingSaiAndNum_ClearsThem()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = Aircraft(hasFlightPlan: true, assignedCode: 4571);
        ac.FlightPlan.HasSpecialAircraftIndicator = true;
        ac.FlightPlan.NumberOfAircraft = 2;
        engine.World.AddAircraft(ac);

        engine.AmendFlightPlan(Callsign, new FlightPlanAmendment(ClearBeaconCode: false, SpecialAircraftIndicator: false, NumberOfAircraft: 0));

        Assert.False(ac.FlightPlan.HasSpecialAircraftIndicator);
        Assert.Null(ac.FlightPlan.NumberOfAircraft);
    }

    [Fact]
    public void BeaconDeletion_OnATrackWithNoPlan_FilesWithoutDrawingACode()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = Aircraft(hasFlightPlan: false, assignedCode: 0);
        engine.World.AddAircraft(ac);

        engine.AmendFlightPlan(Callsign, new FlightPlanAmendment(ClearBeaconCode: true));

        Assert.True(ac.FlightPlan.HasFlightPlan);
        Assert.Equal(0u, ac.Transponder.AssignedCode);
    }

    [Fact]
    public void BeaconDeletion_OnAFiledPlan_ClearsTheCodeAndWhoAssignedIt()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = Aircraft(hasFlightPlan: true, assignedCode: 0);
        ac.Transponder.AssignCode(4571, "ZOA", "40");
        engine.World.AddAircraft(ac);

        engine.AmendFlightPlan(Callsign, new FlightPlanAmendment(ClearBeaconCode: true));

        Assert.Equal(0u, ac.Transponder.AssignedCode);
        Assert.Null(ac.Transponder.AssignedByFacilityId);
        Assert.Null(ac.Transponder.AssignedBySectorId);
        Assert.Equal(4571u, ac.Transponder.Code); // the pilot keeps squawking until told
    }

    [Fact]
    public void BeaconCodeZero_WithoutTheDeletion_OnATrackWithNoPlan_StillDrawsACode()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = Aircraft(hasFlightPlan: false, assignedCode: 0);
        engine.World.AddAircraft(ac);

        // A zero code is no code: filing the plan draws a discrete one, as the CRC flight-plan editor path relies on.
        engine.AmendFlightPlan(Callsign, new FlightPlanAmendment(ClearBeaconCode: false, BeaconCode: 0));

        Assert.True(ac.FlightPlan.HasFlightPlan);
        Assert.NotEqual(0u, ac.Transponder.AssignedCode);
    }

    [Fact]
    public void Snapshot_RoundTripsTheDepartureMessage()
    {
        AircraftState ac = Aircraft(hasFlightPlan: true, assignedCode: 4571);
        ac.FlightPlan.DepartureMessage = new DepartureMessage("OAK", new TimeOnly(12, 30));

        string json = JsonSerializer.Serialize(ac.ToSnapshot(), RecordingJsonOptions.Default);
        AircraftSnapshotDto? dto = JsonSerializer.Deserialize<AircraftSnapshotDto>(json, RecordingJsonOptions.Default);
        Assert.NotNull(dto);
        var restored = AircraftState.FromSnapshot(dto, null);

        Assert.Equal(new DepartureMessage("OAK", new TimeOnly(12, 30)), restored.FlightPlan.DepartureMessage);
    }

    [Fact]
    public void Snapshot_WithoutTheDepartureMessage_RestoresNone()
    {
        AircraftState ac = Aircraft(hasFlightPlan: true, assignedCode: 4571);
        ac.FlightPlan.DepartureMessage = new DepartureMessage("OAK", new TimeOnly(12, 30));

        JsonObject snapshot = JsonSerializer.SerializeToNode(ac.ToSnapshot(), RecordingJsonOptions.Default)!.AsObject();
        JsonObject flightPlan = snapshot[nameof(AircraftSnapshotDto.FlightPlan)]!.AsObject();
        Assert.True(flightPlan.Remove(nameof(AircraftFlightPlanDto.DepartureMessage)));

        AircraftSnapshotDto? dto = snapshot.Deserialize<AircraftSnapshotDto>(RecordingJsonOptions.Default);
        Assert.NotNull(dto);
        var restored = AircraftState.FromSnapshot(dto, null);

        Assert.Null(restored.FlightPlan.DepartureMessage);
    }

    [Fact]
    public void FilingANewPlan_ClearsTheDepartureMessage()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = Aircraft(hasFlightPlan: false, assignedCode: 0);
        ac.FlightPlan.DepartureMessage = new DepartureMessage("KSFO", new TimeOnly(12, 30));
        engine.World.AddAircraft(ac);

        engine.AmendFlightPlan(Callsign, new FlightPlanAmendment(ClearBeaconCode: false, Altitude: PlannedAltitude.Ifr(24000)));

        Assert.True(ac.FlightPlan.HasFlightPlan);
        Assert.Null(ac.FlightPlan.DepartureMessage);
    }

    [Fact]
    public void AmendingAFiledPlan_KeepsTheDepartureMessage()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = Aircraft(hasFlightPlan: true, assignedCode: 4571);
        ac.FlightPlan.DepartureMessage = new DepartureMessage("KSFO", new TimeOnly(12, 30));
        engine.World.AddAircraft(ac);

        engine.AmendFlightPlan(Callsign, new FlightPlanAmendment(ClearBeaconCode: false, Altitude: PlannedAltitude.Ifr(24000)));

        Assert.Equal(new DepartureMessage("KSFO", new TimeOnly(12, 30)), ac.FlightPlan.DepartureMessage);
    }
}
