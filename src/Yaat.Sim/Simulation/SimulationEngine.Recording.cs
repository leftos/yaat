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
        if (scenario is null || IsReplayingRecordedActions || scenario.IsPlaybackMode)
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

    internal void ApplyRecordedAction(RecordedAction action)
    {
        switch (action)
        {
            case RecordedAircraftSpawn spawn:
                ApplyRecordedAircraftSpawn(spawn);
                break;
            case RecordedLiveTrafficSample sample:
                ApplyRecordedLiveTrafficSample(sample);
                break;
            case RecordedLiveTrafficRemoval removal:
                ApplyRecordedLiveTrafficRemoval(removal);
                break;
            case RecordedCommand cmd:
                ReplayCommand(cmd);
                break;
            case RecordedAmendFlightPlan amend:
                AmendFlightPlan(amend.Callsign, amend.Amendment);
                break;
            case RecordedRequestNewBeaconCode recycle:
                RequestNewBeaconCode(recycle.Callsign, recycle.AssignedByFacilityId, recycle.AssignedBySectorId);
                break;
            case RecordedWeatherChange weather:
                if (weather.WeatherJson is not null)
                {
                    ApplyWeatherJson(weather.WeatherJson);
                    if (Scenario is not null)
                    {
                        Scenario.MetarReissuanceEnabled = weather.ReconstructMetars;
                    }
                }
                else
                {
                    World.Weather = null;
                    if (Scenario is not null)
                    {
                        Scenario.WeatherTimeline = null;
                        Scenario.WeatherSourceJson = null;
                        Scenario.MetarReissuanceEnabled = false;
                    }
                }
                break;
            case RecordedSettingChange setting:
                ApplySettingChange(setting);
                break;
            case RecordedArrivalGeneratorsChange generators:
                ApplyGeneratorsJson(generators.GeneratorsJson);
                break;
        }
    }

    private void ApplyRecordedAircraftSpawn(RecordedAircraftSpawn spawn)
    {
        AirportGroundLayout? groundLayout = null;
        if (spawn.Aircraft.Ground.LayoutAirportId is { } layoutAirportId)
        {
            groundLayout = _groundData.GetLayout(layoutAirportId);
        }
        else if (Scenario?.PrimaryAirportId is { } primaryAirportId)
        {
            groundLayout = _groundData.GetLayout(primaryAirportId);
        }

        var state = AircraftState.FromSnapshot(spawn.Aircraft, groundLayout);
        if (spawn.IsSynthetic)
        {
            NormalizeSyntheticAircraftSpawn(state);
        }

        World.AddAircraft(state);
    }

    /// <summary>
    /// Appends an engine-originated action (a live-traffic sample or removal, an AI-controller command) to the recording
    /// unless the room is replaying or playing a tape. Public for the server's diagnostic actions
    /// (<see cref="RecordedLiveTrafficStatus"/>), which have no sim-side twin.
    /// </summary>
    public void RecordAction(RecordedAction action)
    {
        var scenario = Scenario;
        if (scenario is null || IsReplayingRecordedActions || scenario.IsPlaybackMode)
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
