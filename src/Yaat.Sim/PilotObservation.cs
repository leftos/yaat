namespace Yaat.Sim;

/// <summary>
/// Pilot-side "watch for a condition" state — the pilot has acknowledged a
/// request that cannot be satisfied immediately and will report when it can be.
///
/// <para>Observations are re-evaluated each tick by
/// <see cref="PilotObservationUpdater"/>. When the condition becomes
/// satisfied, the observation emits a pilot readback (routed through
/// <see cref="AircraftState.PendingWarnings"/> for traffic acquisition so the
/// event catches the RPO's attention) and the tick processor removes it from
/// <see cref="AircraftState.PendingObservations"/>.</para>
///
/// <para><see cref="TrafficAcquisitionObservation"/> models RTIS soft-fail /
/// "looking for traffic". <see cref="FieldAcquisitionObservation"/> is the
/// matching RFIS soft-fail. Siblings for "report leaving altitude" or
/// "report passing fix" can slot in later.</para>
/// </summary>
public abstract record PilotObservation;

/// <summary>
/// Pilot is looking for traffic identified by <paramref name="TargetCallsign"/>.
/// Each tick, <see cref="PilotObservationUpdater"/> re-runs the visual
/// acquisition check. On acquisition the pilot reports "in sight" and the
/// observation resolves; if the target leaves the simulation the observation
/// silently clears.
/// </summary>
public sealed record TrafficAcquisitionObservation(string TargetCallsign) : PilotObservation;

/// <summary>
/// Pilot is looking for the assigned destination airport. Each tick,
/// <see cref="PilotObservationUpdater"/> re-runs the airport visual
/// acquisition check; on acquisition the pilot reports "field in sight" and
/// the observation resolves. If the destination is cleared or no longer in
/// the nav database the observation silently clears.
/// </summary>
public sealed record FieldAcquisitionObservation : PilotObservation;
