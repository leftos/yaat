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
/// Pilot is looking for traffic identified by <paramref name="TargetCallsign"/>, told of it at
/// <paramref name="IssuedAtSeconds"/> on the scenario clock and <paramref name="ArmedAirborne"/> when the pilot was
/// airborne. Each tick, <see cref="PilotObservationUpdater"/> re-runs the visual acquisition check.
///
/// <para>A lookout is not open-ended: it closes after
/// <see cref="PilotObservationUpdater.TrafficLookoutLifetimeSeconds"/> of sim time, or when an airborne ownship touches
/// down, and the pilot then keys up once with "still negative contact on that traffic" — the traffic may have passed
/// long ago, and a sighting minutes later would report traffic the controller never asked about. A new RTIS or traffic
/// advisory re-arms it; RTISF, FOLLOWF and CVA settle it. A lookout armed on the ground (holding short, a helicopter on
/// a pad) runs on the clock alone: that pilot has already landed.</para>
///
/// <para>On acquisition the pilot reports "in sight" and the observation resolves; if the target leaves the simulation
/// the observation silently clears.</para>
///
/// <para>The two-minute figure and the unsolicited "negative contact" are an owner ruling for training value: FAA text
/// gives no pilot-side limit, and closing out an unsighted advisory is the controller's job (7110.65 2-1-21.a.7).</para>
/// </summary>
public sealed record TrafficAcquisitionObservation(string TargetCallsign, double IssuedAtSeconds, bool ArmedAirborne) : PilotObservation;

/// <summary>
/// Pilot is looking for the assigned destination airport. Each tick,
/// <see cref="PilotObservationUpdater"/> re-runs the airport visual
/// acquisition check; on acquisition the pilot reports "field in sight" and
/// the observation resolves. If the destination is cleared or no longer in
/// the nav database the observation silently clears.
/// </summary>
public sealed record FieldAcquisitionObservation : PilotObservation;
