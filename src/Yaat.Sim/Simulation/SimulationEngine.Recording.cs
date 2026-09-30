using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation.Replay;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Training;

namespace Yaat.Sim.Simulation;

// Recording actions and applying recorded ones back onto the world.
public sealed partial class SimulationEngine
{
    /// <summary>Records a generator-spawned aircraft for replay; a no-op on a run kind that records nothing.</summary>
    public void RecordGeneratedAircraftSpawn(AircraftState state)
    {
        SimScenarioState? scenario = Scenario;
        if (scenario is null || !RunProfile.RecordsActions)
        {
            return;
        }

        scenario.ActionLog.Add(new RecordedAircraftSpawn(scenario.ElapsedSeconds, state.ToSnapshot()));
    }

    /// <summary>
    /// Actions that must land before the physics of their second: aircraft spawns and live-traffic
    /// samples (both happen in pre-physics live). Everything else applies after the second.
    /// </summary>
    public static bool IsPreTickAction(RecordedAction action) => action is RecordedAircraftSpawn or RecordedLiveTrafficSample;

    /// <summary>Puts a recorded spawn's aircraft into the world as it was captured; the pre-tick half of the router's <c>ApplyRecorded</c>.</summary>
    internal void ApplyRecordedAircraftSpawn(RecordedAircraftSpawn spawn)
    {
        var state = AircraftState.FromSnapshot(spawn.Aircraft, ResolveSpawnLayout(spawn.Aircraft));
        if (spawn.IsSynthetic)
        {
            NormalizeSyntheticAircraftSpawn(state);
        }

        World.AddAircraft(state);
    }

    /// <summary>The ground layout a recorded aircraft taxis on: the one it was captured with, else the scenario's primary airport's.</summary>
    private AirportGroundLayout? ResolveSpawnLayout(AircraftSnapshotDto recorded)
    {
        if (recorded.Ground.LayoutAirportId is { } layoutAirportId)
        {
            return _groundData.GetLayout(layoutAirportId);
        }

        return Scenario?.PrimaryAirportId is { } primaryAirportId ? _groundData.GetLayout(primaryAirportId) : null;
    }

    /// <summary>
    /// Appends an engine-originated action (a live-traffic sample or removal, an AI-controller command) to the recording
    /// unless the <see cref="RunProfile"/> says the log is this run's input rather than its output. Public for the
    /// server's diagnostic actions (<see cref="RecordedLiveTrafficStatus"/>), which have no sim-side twin.
    /// </summary>
    public void RecordAction(RecordedAction action)
    {
        SimScenarioState? scenario = Scenario;
        if (scenario is null || !RunProfile.RecordsActions)
        {
            return;
        }

        scenario.ActionLog.Add(action);
    }

    /// <summary>
    /// Stores the synthetic spawn's filed type bare: a blank filed type or one naming the substituted actual type
    /// becomes the sibling, any other keeps its parsed type; a suffix or element a in the filed string lands in its own
    /// field, and a part the string does not give leaves the stored value alone.
    /// </summary>
    public static void NormalizeSyntheticFiledType(AircraftFlightPlan plan, string baseType, string sibling)
    {
        FiledAircraftType filed = FlightPlanNormalization.SplitTypeAndSuffix(plan.AircraftType);
        bool filedIsActual = (string.IsNullOrWhiteSpace(filed.Type)) || (filed.Type.Equals(baseType, StringComparison.OrdinalIgnoreCase));
        plan.AircraftType = filedIsActual ? sibling : filed.Type;
        plan.ApplyFiledAircraftData(filed);
    }

    private static void NormalizeSyntheticAircraftSpawn(AircraftState state)
    {
        string baseType = AircraftState.StripTypePrefix(state.AircraftType).Trim().ToUpperInvariant();
        if (!AircraftSiblingMap.TryResolve(baseType, out string? sibling))
        {
            return;
        }

        state.AircraftType = sibling;
        NormalizeSyntheticFiledType(state.FlightPlan, baseType, sibling);

        AircraftCategory category = AircraftCategorization.Categorize(sibling);
        double defaultSpeed = AircraftPerformance.DefaultSpeed(sibling, category, state.Altitude, targetAltitude: null);
        if (!state.IsOnGround && state.IndicatedAirspeed > defaultSpeed)
        {
            state.IndicatedAirspeed = defaultSpeed;
        }

        if (state.Targets.TargetSpeed is { } targetSpeed && targetSpeed > defaultSpeed)
        {
            state.Targets.TargetSpeed = defaultSpeed;
        }
    }
}
