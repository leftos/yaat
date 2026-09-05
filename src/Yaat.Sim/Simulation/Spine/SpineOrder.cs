using System.Collections.Immutable;

namespace Yaat.Sim.Simulation.Spine;

/// <summary>
/// The single ordered definition of the simulation-affecting steps in a sim-second (ADR 0001). Every run kind —
/// live, soak, test, replay, reconstruction — iterates these lists through <see cref="SimulationEngine.RunSecond"/>
/// or its segment entry points; none keeps a list of its own. The order is the live server's, which ADR 0002 makes
/// the adjudication record for ordering disagreements, so this file <em>is</em> that record.
///
/// <para>
/// Three of the five segments are lists. The clock increment (<see cref="SimulationEngine.BeginSecond"/>) and the
/// physics sub-tick (<see cref="SimulationEngine.RunPhysicsSubTick"/>) are fixed code, and the trace-reset plus
/// pre-tick recorded actions form <see cref="SimulationEngine.OpenSecond"/>.
/// </para>
///
/// <para>
/// A host step here is a body the server owns today. While one of them mutates snapshot state the host decides
/// whether a simulation-affecting step runs at all — the residue ADR 0001 forbids; ADR 0003 (tick step 4) moves
/// those bodies into the engine one at a time, and each move turns a <see cref="SpineStep.Host"/> entry into a
/// <see cref="SpineStep.Sim"/> entry without touching the order. The snapshot oracle, not the step trace, is what
/// sees a host slot left empty.
/// </para>
/// </summary>
public static class SpineOrder
{
    public static readonly ImmutableArray<SpineStep> PrePhysics =
    [
        SpineStep.Sim(StepId.PrePhysics, static (engine, host) => host.OnPrePhysics(engine.TickPrePhysics())),
        SpineStep.Sim(StepId.TerminalEntries, static (engine, host) => host.OnTerminalEntries(engine.DrainTerminalEntries())),
        SpineStep.Host(StepId.DelayedHandoffs, static host => host.DelayedHandoffs()),
        // Last in pre-physics so a sample placed at this second is recorded at this second and replays pre-tick;
        // the sync is the pre-physics mutator of the aircraft set.
        SpineStep.Host(StepId.LiveTrafficSync, static host => host.LiveTrafficSync()),
    ];

    public static readonly ImmutableArray<SpineStep> PostPhysics =
    [
        SpineStep.Sim(StepId.LiveTrafficRunwayUse, static (engine, _) => engine.TickLiveTrafficRunwayUse()),
        SpineStep.Sim(StepId.Transponders, static (engine, _) => engine.TickTransponders()),
        SpineStep.Host(StepId.AutoAccept, static host => host.AutoAccept()),
        SpineStep.Host(StepId.PointoutAutoAck, static host => host.PointoutAutoAck()),
        // FP-creator autotrack runs before the airport-based deferred autotrack so a controller who explicitly
        // types VP/DA wins over scenario AutoTrackAirportIds for the aircraft they just created the FP for.
        SpineStep.Host(StepId.FlightPlanCreatorAutoTrack, static host => host.FlightPlanCreatorAutoTrack()),
        SpineStep.Host(StepId.DeferredAutoTrack, static host => host.DeferredAutoTrack()),
        SpineStep.Host(StepId.CoordinationTimers, static host => host.CoordinationTimers()),
        SpineStep.Host(StepId.TowerLists, static host => host.TowerLists()),
        SpineStep.Sim(StepId.VisualDetection, static (engine, _) => engine.TickVisualDetection()),
        // The detectors run on every path so a conflict set a snapshot restore repopulated is re-examined rather
        // than pinned; only a broadcasting host does anything with the returned diff.
        SpineStep.Sim(StepId.ConflictAlerts, static (engine, host) => host.OnConflictAlerts(engine.TickConflictAlerts())),
        SpineStep.Sim(StepId.EramConflictAlerts, static (engine, host) => host.OnEramConflictAlerts(engine.TickEramConflictAlerts())),
        SpineStep.Host(StepId.AsdexAlerts, static host => host.AsdexAlerts()),
        // Runs on every path (ADR 0002 membership: live wins): an empty list outside solo mode, and the evaluator's
        // own record of what it has scored is engine state a replay must rebuild like any other.
        SpineStep.Sim(StepId.SoloTrainingEvaluation, static (engine, host) => host.OnSoloTrainingEvents(engine.TickSoloTrainingEvaluation())),
        // After the detectors, so a proactive rule that reads what visual detection or conflict alerting writes
        // sees the same picture on every path; before the drains, so an airborne check-in emits the tick it is
        // produced.
        SpineStep.Sim(StepId.PilotProactive, static (engine, _) => engine.TickPilotProactive()),
        SpineStep.Sim(StepId.Warnings, static (engine, host) => host.OnWarnings(engine.World.DrainAllWarnings())),
        SpineStep.Sim(StepId.Notifications, static (engine, host) => host.OnNotifications(engine.World.DrainAllNotifications())),
        // Speech before readbacks: two independent buffers feeding one terminal stream, so the only thing their
        // order decides is how a tick's lines read back.
        SpineStep.Sim(StepId.PilotSpeech, static (engine, host) => host.OnPilotSpeech(engine.World.DrainAllPilotSpeech())),
        SpineStep.Sim(StepId.PilotReadbacks, static (engine, host) => host.OnPilotReadbacks(engine.World.DrainAllPilotReadbacks())),
        SpineStep.Sim(StepId.PilotTransmissions, static (engine, host) => engine.DrainPilotTransmissionsInto(host)),
        SpineStep.Sim(StepId.ApproachScores, static (engine, host) => host.OnApproachScores(engine.World.DrainAllApproachScores())),
        SpineStep.Host(StepId.AutoArrivalStrips, static host => host.AutoArrivalStrips()),
        SpineStep.Host(StepId.AutoApproachDepartureStrips, static host => host.AutoApproachDepartureStrips()),
        SpineStep.Host(StepId.AutoTdlsQueue, static host => host.AutoTdlsQueue()),
        SpineStep.Host(StepId.TdlsAutoWilco, static host => host.TdlsAutoWilco()),
        SpineStep.Host(StepId.TdlsExpiry, static host => host.TdlsExpiry()),
        SpineStep.Host(StepId.TdlsTrackRemoval, static host => host.TdlsTrackRemoval()),
        // Immediately before AutoDelete, the only post-physics mutator that removes aircraft, so a strip command's
        // callsign still resolves on the tick it fires.
        SpineStep.Sim(StepId.StripDispatches, static (engine, host) => host.OnStripDispatches(engine.World.DrainAllStripDispatches())),
        // The only step that removes aircraft, on every path (ADR 0002 membership: live wins) — a replay that kept
        // an aircraft the live session auto-deleted drifted until the next snapshot restore snapped it back.
        SpineStep.Sim(StepId.AutoDelete, static (engine, host) => host.OnAutoDeleted(engine.TickAutoDelete())),
        SpineStep.Host(StepId.SurfaceCoastExpiry, static host => host.SurfaceCoastExpiry()),
        SpineStep.Host(StepId.RundownBroadcast, static host => host.RundownBroadcast()),
        SpineStep.Host(StepId.LiveTrafficStatusBroadcast, static host => host.LiveTrafficStatusBroadcast()),
        SpineStep.Host(StepId.TimersBroadcast, static host => host.TimersBroadcast()),
    ];

    public static readonly ImmutableArray<SpineStep> EndOfSecond =
    [
        SpineStep.Sim(StepId.PositionHistory, static (engine, _) => engine.SamplePositionHistory()),
        SpineStep.Sim(
            StepId.WeatherAdvance,
            static (engine, host) =>
            {
                if (engine.AdvanceWeatherTimeline() is { } profile)
                {
                    host.OnWeatherAdvanced(profile);
                }
            }
        ),
        SpineStep.Host(StepId.MetarIssuance, static host => host.IssueMetars()),
        SpineStep.Host(StepId.RecordedActions, static host => host.ApplyRecordedActions()),
        // The controller AI observes the completed second. Gated by RunProfile.RunsControllerAi inside.
        SpineStep.Sim(StepId.ControllerAi, static (engine, _) => engine.TickControllerAi()),
    ];
}
