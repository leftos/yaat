using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Pilot;

/// <summary>What a lined-up runway spawn does at the point <see cref="LinedUpAndWaitingPhase"/> would make its lined-up call.</summary>
public enum RunwaySpawnCallKind
{
    /// <summary>Today's "tower, runway 28R, ready." call.</summary>
    LinedUpCall,

    /// <summary>
    /// No call: a <see cref="InitialCallupPlan.RunwaySayOnly"/> spawn (its SAY preset is what it says), or a
    /// <see cref="InitialCallupPlan.RunwayNoPreset"/> spawn that the engine sends off with an automatic takeoff clearance:
    /// at a towered field, or flying VFR at an untowered one.
    /// </summary>
    Silent,

    /// <summary>
    /// An IFR <see cref="InitialCallupPlan.RunwayNoPreset"/> spawn at an untowered field asks the radar student for its
    /// release (7110.65 4-3-4, AIM 5-2-7.a.2).
    /// </summary>
    ReleaseRequest,
}

/// <summary>
/// The runway-spawn rules at the lined-up call point: a SAY-only spawn stays silent; a spawn with no preset and a radar
/// student (APP or CTR) departs on an automatic takeoff clearance at a towered field or, flying VFR, at an untowered one
/// (<see cref="Simulation.SimulationEngine"/> issues it after <see cref="LinedUpAndWaitingPhase.LinedUpReadyDelaySeconds"/>),
/// and flying IFR at an untowered field asks the student for a release 5 to 10 s after lining up
/// (<see cref="ReleaseRequestDelaySeconds"/>); every other spawn makes today's lined-up call after
/// <see cref="LinedUpAndWaitingPhase.LinedUpReadyDelaySeconds"/>.
/// </summary>
public static class RunwaySpawnCall
{
    /// <summary>The shortest pause between lining up and asking for a release, seconds.</summary>
    public const double MinReleaseRequestDelaySeconds = 5.0;

    /// <summary>How many whole seconds past <see cref="MinReleaseRequestDelaySeconds"/> the pause can run (5 to 10 s inclusive).</summary>
    private const uint ReleaseRequestDelayRangeSeconds = 6;

    /// <summary>Salt for the release-request draw, so it is not correlated with the other per-callsign draws.</summary>
    private const string ReleaseRequestSalt = "runway-spawn-release-request:";

    /// <summary>
    /// The pause between lining up and asking for a release: a fixed 5 to 10 s per aircraft from its callsign (FNV-1a over
    /// a salt and the callsign, <see cref="DeterministicHash"/>; replay-safe, no RNG state).
    /// </summary>
    public static double ReleaseRequestDelaySeconds(string callsign) =>
        MinReleaseRequestDelaySeconds + (DeterministicHash.Fnv1a(ReleaseRequestSalt, callsign) % ReleaseRequestDelayRangeSeconds);

    /// <summary>How long the aircraft stays lined up before it makes the call <paramref name="kind"/> names.</summary>
    public static double CallPointSeconds(RunwaySpawnCallKind kind, string callsign) =>
        kind == RunwaySpawnCallKind.ReleaseRequest ? ReleaseRequestDelaySeconds(callsign) : LinedUpAndWaitingPhase.LinedUpReadyDelaySeconds;

    /// <summary>The runway a lined-up aircraft is on: its departure runway, else its assigned runway.</summary>
    public static RunwayInfo? RunwayOf(AircraftState aircraft) => aircraft.Phases?.DepartureRunway ?? aircraft.Phases?.AssignedRunway;

    /// <summary>A student working a radar position (approach or center), for whom a lined-up call to "tower" means nothing.</summary>
    public static bool IsRadarStudent(string? studentPositionType) => studentPositionType is "APP" or "CTR";

    /// <summary>
    /// What the aircraft does at its lined-up call point, given the student's position type and whether its field has a
    /// tower (<paramref name="fieldTowered"/>, evaluated by the engine from the ARTCC config).
    /// </summary>
    public static RunwaySpawnCallKind Decide(AircraftState aircraft, string? studentPositionType, bool fieldTowered)
    {
        AircraftGroundOps ground = aircraft.Ground;
        if (ground.InitialCallup == InitialCallupPlan.RunwaySayOnly)
        {
            return RunwaySpawnCallKind.Silent;
        }

        if (
            (ground.InitialCallup != InitialCallupPlan.RunwayNoPreset)
            || ground.InitialCallupDecisionProcessed
            || !IsRadarStudent(studentPositionType)
        )
        {
            return RunwaySpawnCallKind.LinedUpCall;
        }

        // A spawn released through the hold-for-release spawn gate already has its release: it never asks for one.
        return (fieldTowered || aircraft.FlightPlan.IsVfr || ground.ReleasedAtSpawnGate)
            ? RunwaySpawnCallKind.Silent
            : RunwaySpawnCallKind.ReleaseRequest;
    }

    /// <summary>
    /// True when a <see cref="InitialCallupPlan.RunwayNoPreset"/> spawn has reached its lined-up call point with no takeoff
    /// clearance and no call made, under a radar student: the moment the engine clears it for takeoff at a towered field,
    /// or lets it depart on its own when it flies VFR from an untowered one.
    /// </summary>
    public static bool IsAtAutoTakeoffPoint(AircraftState aircraft, string? studentPositionType) =>
        (aircraft.Ground.InitialCallup == InitialCallupPlan.RunwayNoPreset)
        && !aircraft.Ground.InitialCallupDecisionProcessed
        && IsRadarStudent(studentPositionType)
        && !aircraft.HasAnnouncedLinedUpReady
        && (aircraft.Phases?.DepartureClearance is null)
        && (aircraft.Phases?.CurrentPhase is LinedUpAndWaitingPhase { ElapsedSeconds: >= LinedUpAndWaitingPhase.LinedUpReadyDelaySeconds });

    /// <summary>
    /// Asks the radar student, who works a <paramref name="positionType"/> position, for a release from
    /// <paramref name="runway"/>: queues the line, holds the aircraft for release so <c>REL</c> finds it, records the open
    /// <see cref="PilotPendingRequestKind.Release"/> request and marks the call made. False, changing nothing, when nobody
    /// answers; the phase retries next tick. The request is not the pilot's initial contact (AIM 5-2-7.a.1 Note 1): the
    /// airborne check-in still follows the takeoff (AIM 3-2-4, 3-2-5).
    /// </summary>
    public static bool TryRequestRelease(PhaseContext ctx, RunwayInfo runway, string positionType)
    {
        AircraftState aircraft = ctx.Aircraft;
        if (ctx.PilotContacts.ResolveFor(aircraft, positionType, runway.AirportId, ctx.ToEligibilityContext(), false) is not { } answering)
        {
            return false;
        }

        string fallback = positionType == "CTR" ? "center" : "approach";
        string facilityCallName = PilotResponder.ResolveAnsweringCallName(answering, positionType, fallback);
        PilotSpeechText line = PilotResponder.BuildReleaseRequest(aircraft, runway.Designator, runway.AirportId, facilityCallName);
        PilotResponder.QueueSoloPilotTransmission(aircraft, line, PilotTransmissionKind.Proactive, PilotResponder.SourceResponse);
        PilotRequestTracker.RecordRequest(
            aircraft,
            PilotPendingRequestKind.Release,
            ctx.ScenarioElapsedSeconds,
            line,
            PilotRequestContext.Runway(runway.Designator, facilityCallName)
        );
        aircraft.Ground.HeldForRelease = true;
        aircraft.Ground.ReleasedForDeparture = false;
        aircraft.HasAnnouncedLinedUpReady = true;
        aircraft.Ground.InitialCallupDecisionProcessed = true;
        return true;
    }
}
