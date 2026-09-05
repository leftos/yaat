namespace Yaat.Sim.Simulation.Spine;

/// <summary>
/// The step view of a host: the spine bodies the host supplies. Every member is invoked by the runner at its
/// position in <see cref="SpineOrder"/>, on every run kind; a host that has nothing to do there implements the member
/// empty, and that empty body is the statement "this run does nothing here", not an omission. There are no default
/// implementations, so adding a member fails the build in every host until each has answered (tick-path phase-1
/// criterion 2).
///
/// <para>
/// <b>Step-4 debt.</b> Members whose live body mutates snapshot state let the host decide whether a
/// simulation-affecting step runs — the residue ADR 0001 forbids. They are, today: <see cref="DelayedHandoffs"/>,
/// <see cref="LiveTrafficSync"/>, <see cref="AutoAccept"/>, <see cref="PointoutAutoAck"/>,
/// <see cref="FlightPlanCreatorAutoTrack"/>, <see cref="DeferredAutoTrack"/>, <see cref="CoordinationTimers"/>,
/// <see cref="TowerLists"/>, <see cref="TdlsExpiry"/>, <see cref="SoloTrainingEvaluation"/>,
/// <see cref="AutoDelete"/>, <see cref="SamplePositionHistory"/> and <see cref="AdvanceWeather"/>. ADR 0003 moves
/// each into the engine, deleting the member here and turning its spine entry into a sim step; the interface shrinks
/// as that work lands. The remaining members are broadcast and wire projection, which is the server's.
/// </para>
/// </summary>
public interface IHostSteps
{
    // --- OpenSecond ---

    /// <summary>
    /// Recorded actions that land before the physics of their second (aircraft spawns, live-traffic samples —
    /// <see cref="SimulationEngine.IsPreTickAction"/>). Replay, reconstruction and tape playback apply them here;
    /// a bare or live run has none.
    /// </summary>
    void ApplyPreTickRecordedActions(int second);

    // --- PrePhysics ---

    /// <summary>Fires the delayed handoffs whose time has come (live: <c>TickProcessor.ProcessDelayedHandoffs</c>).</summary>
    void DelayedHandoffs();

    /// <summary>Syncs live-traffic shadows from the feed (live: <c>ShadowTrafficSync.Sync</c>).</summary>
    void LiveTrafficSync();

    // --- PostPhysics ---

    void AutoAccept();
    void PointoutAutoAck();
    void FlightPlanCreatorAutoTrack();
    void DeferredAutoTrack();
    void CoordinationTimers();
    void TowerLists();
    void AsdexAlerts();

    /// <summary>Live runs <see cref="SimulationEngine.TickSoloTrainingEvaluation"/> and broadcasts the events; other hosts skip it.</summary>
    void SoloTrainingEvaluation();

    void AutoArrivalStrips();
    void AutoApproachDepartureStrips();
    void AutoTdlsQueue();
    void TdlsAutoWilco();
    void TdlsExpiry();
    void TdlsTrackRemoval();

    /// <summary>Live runs <see cref="SimulationEngine.TickAutoDelete"/> and fans out the delete broadcasts; other hosts skip it.</summary>
    void AutoDelete();

    void SurfaceCoastExpiry();
    void RundownBroadcast();
    void LiveTrafficStatusBroadcast();
    void TimersBroadcast();

    // --- EndOfSecond ---

    /// <summary>Samples <see cref="AircraftState.PositionHistory"/> (live only today).</summary>
    void SamplePositionHistory();

    /// <summary>
    /// Advances the weather timeline for the completed second. Three semantics coexist today (live gated on
    /// <c>WeatherTimeline.HasMeaningfulChange</c>, replay and reconstruction ungated, a bare engine frozen) — the
    /// oracle's only physics divergence, retired by tick step 5.
    /// </summary>
    void AdvanceWeather();

    /// <summary>Routine and SPECI METAR issuance (live only).</summary>
    void IssueMetars();

    /// <summary>Recorded actions at or before the completed second that were not applied pre-tick.</summary>
    void ApplyRecordedActions();
}
