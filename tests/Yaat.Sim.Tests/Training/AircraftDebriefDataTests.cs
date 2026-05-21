using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Training;

namespace Yaat.Sim.Tests.Training;

/// <summary>
/// Step 1 of M12.4: per-aircraft lifecycle instrumentation. Covers spawn-time stamping,
/// completion stamping (landing, handoff, drop), and the <see cref="SimulationWorld"/>
/// completed-aircraft registry that survives <see cref="SimulationWorld.RemoveAircraft"/>.
/// Direct phase-tick coverage for the landing path is left to the existing OAK lifecycle
/// suite (any landing regression would surface there); this class focuses on the new
/// state mutations themselves and the registry.
/// </summary>
public class AircraftDebriefDataTests
{
    private static AircraftState MakeAircraft(string callsign = "N123AB") => new() { Callsign = callsign, AircraftType = "C172" };

    private static DispatchContext MakeCtx(double scenarioElapsedSeconds = 0) =>
        TestDispatch.Context(new Random(0), soloTrainingMode: true, scenarioElapsedSeconds: scenarioElapsedSeconds);

    [Fact]
    public void AircraftState_DefaultCompletionState_IsActive()
    {
        var ac = MakeAircraft();

        Assert.Equal(0.0, ac.SpawnedAtSeconds);
        Assert.Null(ac.CompletedAtSeconds);
        Assert.Equal(CompletionReason.Active, ac.CompletionReason);
        Assert.Null(ac.CompletionDetail);
    }

    [Fact]
    public void FrequencyChangeApproved_StampsHandedOffCompletion()
    {
        var ac = MakeAircraft();
        var ctx = MakeCtx(scenarioElapsedSeconds: 182.5);

        var result = ContactCommandHandler.HandleFrequencyChangeApproved(ac, ctx);

        Assert.True(result.Success);
        Assert.Equal(182.5, ac.CompletedAtSeconds);
        Assert.Equal(CompletionReason.HandedOff, ac.CompletionReason);
        Assert.Null(ac.CompletionDetail); // FCA has no target detail
    }

    [Fact]
    public void HandleContact_ReissuedAfterFirst_DoesNotOverwriteOriginalStamp()
    {
        // CT/FCA can be re-issued legitimately (controller corrects a typo, bounces a handoff).
        // The first one owns the completion stamp; later ones don't move it.
        var ac = MakeAircraft();
        var firstCtx = MakeCtx(scenarioElapsedSeconds: 100);
        ContactCommandHandler.HandleFrequencyChangeApproved(ac, firstCtx);

        var secondCtx = MakeCtx(scenarioElapsedSeconds: 150);
        var result = ContactCommandHandler.HandleFrequencyChangeApproved(ac, secondCtx);

        Assert.True(result.Success);
        Assert.Equal(100.0, ac.CompletedAtSeconds);
        Assert.Equal(CompletionReason.HandedOff, ac.CompletionReason);
    }

    [Fact]
    public void HandleContact_AfterLanding_DoesNotOverwriteLanded()
    {
        // Landing → CT (controller hands off to ground after rollout). The landed stamp
        // is the canonical completion; the CT must not relabel it as HandedOff.
        var ac = MakeAircraft();
        ac.SpawnedAtSeconds = 10;
        ac.CompletedAtSeconds = 200;
        ac.CompletionReason = CompletionReason.Landed;
        ac.CompletionDetail = "28R";

        ContactCommandHandler.HandleFrequencyChangeApproved(ac, MakeCtx(scenarioElapsedSeconds: 250));

        Assert.Equal(200.0, ac.CompletedAtSeconds);
        Assert.Equal(CompletionReason.Landed, ac.CompletionReason);
        Assert.Equal("28R", ac.CompletionDetail);
    }

    [Fact]
    public void RemoveAircraft_AfterCompletion_PreservesRecordInRegistry()
    {
        var world = new SimulationWorld();
        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Cid = "500",
            SpawnedAtSeconds = 30,
            CompletedAtSeconds = 245,
            CompletionReason = CompletionReason.HandedOff,
            CompletionDetail = "NCT_F_APP",
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "RNO" },
        };
        world.AddAircraft(ac);

        world.RemoveAircraft("N123AB");

        Assert.Empty(world.GetSnapshot());
        var record = Assert.Single(world.GetCompletedAircraft());
        Assert.Equal("N123AB", record.Callsign);
        Assert.Equal("C172", record.AircraftType);
        Assert.Equal("500", record.Cid);
        Assert.Equal("OAK", record.FiledDeparture);
        Assert.Equal("RNO", record.FiledDestination);
        Assert.Equal(30.0, record.SpawnedAtSeconds);
        Assert.Equal(245.0, record.CompletedAtSeconds);
        Assert.Equal(CompletionReason.HandedOff, record.Reason);
        Assert.Equal("NCT_F_APP", record.Detail);
    }

    [Fact]
    public void RemoveAircraft_OfActiveAircraft_DoesNotAddRecord()
    {
        // Aircraft that disappear without a completion stamp (scenario unload, manual
        // delete on an active aircraft) get no debrief block.
        var world = new SimulationWorld();
        var ac = MakeAircraft();
        world.AddAircraft(ac);

        world.RemoveAircraft("N123AB");

        Assert.Empty(world.GetCompletedAircraft());
    }

    [Fact]
    public void Clear_ResetsCompletedRegistry()
    {
        var world = new SimulationWorld();
        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            CompletedAtSeconds = 100,
            CompletionReason = CompletionReason.Landed,
        };
        world.AddAircraft(ac);
        world.RemoveAircraft("N123AB");
        Assert.Single(world.GetCompletedAircraft());

        world.Clear();

        Assert.Empty(world.GetCompletedAircraft());
        Assert.Empty(world.GetSnapshot());
    }

    [Fact]
    public void AircraftState_Snapshot_RoundTripsLifecycleFields()
    {
        var ac = new AircraftState
        {
            Callsign = "AAL100",
            AircraftType = "B738",
            Cid = "100",
            SpawnedAtSeconds = 42.5,
            CompletedAtSeconds = 487.25,
            CompletionReason = CompletionReason.Landed,
            CompletionDetail = "10R",
        };

        var dto = ac.ToSnapshot();
        var restored = AircraftState.FromSnapshot(dto, groundLayout: null);

        Assert.Equal(42.5, restored.SpawnedAtSeconds);
        Assert.Equal(487.25, restored.CompletedAtSeconds);
        Assert.Equal(CompletionReason.Landed, restored.CompletionReason);
        Assert.Equal("10R", restored.CompletionDetail);
    }

    [Fact]
    public void AircraftState_Snapshot_DefaultsRoundTripAsActive()
    {
        // Pre-feature aircraft (no completion stamped) must round-trip as Active so the
        // M12.4 debrief tab keeps showing them in-service.
        var ac = MakeAircraft();
        ac.SpawnedAtSeconds = 0;

        var dto = ac.ToSnapshot();
        var restored = AircraftState.FromSnapshot(dto, groundLayout: null);

        Assert.Equal(0.0, restored.SpawnedAtSeconds);
        Assert.Null(restored.CompletedAtSeconds);
        Assert.Equal(CompletionReason.Active, restored.CompletionReason);
        Assert.Null(restored.CompletionDetail);
    }

    [Fact]
    public void RemoveAircraft_OmitsRegistryEntry_WhenFlightPlanEmpty()
    {
        // FiledDeparture / FiledDestination must be null (not empty strings) when the
        // aircraft had no flight plan — the debrief operation-kind classifier uses null
        // checks to fall through to Transit.
        var world = new SimulationWorld();
        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            CompletedAtSeconds = 100,
            CompletionReason = CompletionReason.Landed,
            CompletionDetail = "28R",
        };
        world.AddAircraft(ac);

        world.RemoveAircraft("N123AB");

        var record = Assert.Single(world.GetCompletedAircraft());
        Assert.Null(record.FiledDeparture);
        Assert.Null(record.FiledDestination);
    }

    [Fact]
    public void AddAircraft_PurgesStaleCompletedRecordForSameCallsign()
    {
        // Respawn after a removal: the previous completed record would otherwise still
        // be in the registry, and once the new run later completes the Aircraft tab
        // would surface both runs once the new entry is also removed.
        var world = new SimulationWorld();
        var first = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            CompletedAtSeconds = 100,
            CompletionReason = CompletionReason.Landed,
            CompletionDetail = "28R",
        };
        world.AddAircraft(first);
        world.RemoveAircraft("N123AB");
        Assert.Single(world.GetCompletedAircraft());

        var second = new AircraftState { Callsign = "N123AB", AircraftType = "C172" };
        world.AddAircraft(second);

        Assert.Empty(world.GetCompletedAircraft());
    }

    [Fact]
    public void RemoveAircraft_BeyondCapacity_EvictsOldestFirst()
    {
        // Long sessions must not grow the registry unbounded; oldest entries drop off
        // as new completed records arrive past the cap.
        var world = new SimulationWorld();
        for (int i = 0; i < SimulationWorld.CompletedAircraftCapacity + 50; i++)
        {
            string callsign = $"AC{i:D4}";
            var ac = new AircraftState
            {
                Callsign = callsign,
                AircraftType = "C172",
                CompletedAtSeconds = i,
                CompletionReason = CompletionReason.Landed,
            };
            world.AddAircraft(ac);
            world.RemoveAircraft(callsign);
        }

        var records = world.GetCompletedAircraft();
        Assert.Equal(SimulationWorld.CompletedAircraftCapacity, records.Count);
        // The first 50 (AC0000..AC0049) should have been evicted; AC0050 is now the oldest.
        Assert.Equal("AC0050", records[0].Callsign);
        Assert.Equal($"AC{SimulationWorld.CompletedAircraftCapacity + 49:D4}", records[^1].Callsign);
    }
}
