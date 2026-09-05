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
    private void RecordGeneratedAircraftSpawn(AircraftState state)
    {
        var scenario = Scenario;
        if (scenario is null || !RunProfile.RecordsActions)
        {
            return;
        }

        scenario.ActionLog.Add(new RecordedAircraftSpawn(scenario.ElapsedSeconds, state.ToSnapshot()));
    }

    internal static void ApplyRecordedAircraftSpawnsBeforeTick(
        List<RecordedAction> actions,
        ref int actionCursor,
        int elapsedSeconds,
        Action<RecordedAction> actionApplier,
        HashSet<int> appliedActionIndexes
    )
    {
        while (actionCursor < actions.Count && actions[actionCursor].ElapsedSeconds <= elapsedSeconds)
        {
            if (IsPreTickAction(actions[actionCursor]))
            {
                actionApplier(actions[actionCursor]);
                appliedActionIndexes.Add(actionCursor);
            }

            actionCursor++;
        }
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
        var scenario = Scenario;
        if (scenario is null || !RunProfile.RecordsActions)
        {
            return;
        }

        scenario.ActionLog.Add(action);
    }

    private static void NormalizeSyntheticAircraftSpawn(AircraftState state)
    {
        var baseType = AircraftState.StripTypePrefix(state.AircraftType).Trim().ToUpperInvariant();
        if (!AircraftSiblingMap.TryResolve(baseType, out var sibling))
        {
            return;
        }

        state.AircraftType = sibling;
        if (
            string.IsNullOrWhiteSpace(state.FlightPlan.AircraftType)
            || state.FlightPlan.AircraftType.Equals(baseType, StringComparison.OrdinalIgnoreCase)
        )
        {
            state.FlightPlan.AircraftType = sibling;
        }

        var category = AircraftCategorization.Categorize(sibling);
        var defaultSpeed = AircraftPerformance.DefaultSpeed(sibling, category, state.Altitude, targetAltitude: null);
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
