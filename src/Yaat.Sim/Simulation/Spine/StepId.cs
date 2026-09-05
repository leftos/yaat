namespace Yaat.Sim.Simulation.Spine;

/// <summary>
/// One member per spine step, declared in spine order. The id is what the <see cref="StepTrace"/> records and what
/// the engine's timing buckets are keyed by, so a step's name here is its name everywhere.
/// </summary>
public enum StepId
{
    // OpenSecond
    PreTickRecordedActions,

    // PrePhysics
    PrePhysics,
    TerminalEntries,
    DelayedHandoffs,
    LiveTrafficSync,

    // Physics — recorded once per sub-tick with its index
    Physics,

    // PostPhysics, in the live server's order
    LiveTrafficRunwayUse,
    Transponders,
    AutoAccept,
    PointoutAutoAck,
    FlightPlanCreatorAutoTrack,
    DeferredAutoTrack,
    CoordinationTimers,
    TowerLists,
    VisualDetection,
    ConflictAlerts,
    EramConflictAlerts,
    AsdexAlerts,
    SoloTrainingEvaluation,
    PilotProactive,
    Warnings,
    Notifications,
    PilotSpeech,
    PilotReadbacks,
    PilotTransmissions,
    ApproachScores,
    AutoArrivalStrips,
    AutoApproachDepartureStrips,
    AutoTdlsQueue,
    TdlsAutoWilco,
    TdlsExpiry,
    TdlsTrackRemoval,
    StripDispatches,
    AutoDelete,
    SurfaceCoastExpiry,
    RundownBroadcast,
    LiveTrafficStatusBroadcast,
    TimersBroadcast,

    // EndOfSecond
    PositionHistory,
    WeatherAdvance,
    MetarIssuance,
    RecordedActions,
    ControllerAi,
}
