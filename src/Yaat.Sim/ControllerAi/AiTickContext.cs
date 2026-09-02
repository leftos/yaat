using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.ControllerAi;

/// <summary>What the host hands <see cref="AiControllerService.Tick"/> each second; the service turns it into an <see cref="AiTickContext"/>.</summary>
public sealed record AiTickInputs(
    SimScenarioState Scenario,
    SimulationWorld World,
    IReadOnlyList<AircraftState> Aircraft,
    Func<AircraftState, AirportGroundLayout?> LayoutFor,
    Func<string?, IReadOnlyList<RunwayInfo>> RunwaysFor,
    IReadOnlyList<ActiveConflict> ActiveConflicts,
    IReadOnlyList<EramActiveConflict> EramConflicts
);

/// <summary>Everything a brain may read or act on during one AI tick. Read-only views of the sim plus the AI's own state.</summary>
public sealed class AiTickContext
{
    public required IReadOnlyList<AircraftState> Snapshot { get; init; }

    public required AiWorldView View { get; init; }

    public required SimScenarioState Scenario { get; init; }

    public required SimulationWorld World { get; init; }

    public required IAiCommandSink Sink { get; init; }

    public required IAiStaffing Staffing { get; init; }

    public required SerializableRandom AiRng { get; init; }

    public required double ElapsedSeconds { get; init; }

    public required AiAnomalyLog Anomalies { get; init; }

    /// <summary>Outcomes of the commands issued on earlier ticks that the host has since dispatched.</summary>
    public required IReadOnlyList<AiCommandOutcome> Outcomes { get; init; }

    public required WeatherProfile? Weather { get; init; }

    public required IReadOnlyList<ActiveConflict> ActiveConflicts { get; init; }

    public required IReadOnlyList<EramActiveConflict> EramConflicts { get; init; }

    public required double AutoAcceptDelaySeconds { get; init; }

    /// <summary>The ground layout an aircraft is operating on; null when its airport has none loaded.</summary>
    public required Func<AircraftState, AirportGroundLayout?> LayoutFor { get; init; }

    /// <summary>The runways (pavements) of an airport given as FAA or ICAO id; empty when unknown.</summary>
    public required Func<string?, IReadOnlyList<RunwayInfo>> RunwaysFor { get; init; }

    /// <summary>The session's runway-in-use decisions, shared by every brain so Ground and Local agree.</summary>
    public required RunwayInUseState RunwayInUse { get; init; }
}

/// <summary>A per-position controller brain. Brains tick in (role rank, position id) order; each acts only on its own jurisdiction.</summary>
public interface IPositionBrain
{
    AiPositionConfig Position { get; }

    void Tick(AiTickContext context);

    /// <summary>Forgets every per-aircraft memo — after a scenario reload or a snapshot restore.</summary>
    void Reset();
}
