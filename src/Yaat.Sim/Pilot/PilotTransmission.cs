namespace Yaat.Sim.Pilot;

public enum PilotTransmissionKind
{
    Readback,
    Proactive,
    Report,
    SayReadback,
}

/// <summary>
/// Typed metadata for solo-training pilot speech. The frequency queue drains
/// these entries into delayed SAY terminal lines and client audio broadcasts.
/// </summary>
/// <param name="CanonicalAirtimeSeconds">
/// The airtime the frequency meter records for the transmission: <see cref="FrequencyState.EstimateAirtimeSeconds"/> of
/// the canonical spoken text, never of a load-varied <see cref="SpeechText"/>, so a terser busy read-back cannot make the
/// frequency read quieter.
/// </param>
public sealed record PilotTransmission(
    string Callsign,
    string Text,
    string SpeechText,
    string SourceKind,
    PilotTransmissionKind Kind,
    double CanonicalAirtimeSeconds
)
{
    /// <summary>
    /// The frequency's load band at the second the transmission went on air: the airtime of the minute up to that second,
    /// to which the transmission, keyed then, adds nothing yet. Null until <see cref="FrequencyState.TryDequeueReady"/> dequeues it.
    /// </summary>
    public FrequencyLoadBand? LoadBand { get; init; }

    /// <summary>
    /// Whether this is a pilot's initial contact with the position it is calling — an airborne check-in, a tower check-in
    /// on final, a pattern arrival's first call. Only such a call may butt in under the frequency's controller-response
    /// gate (<see cref="FrequencyState"/>); every other call and every read-back is false. A ground or departure first
    /// call — ready to taxi, pushback, clearance request, taxi-in, release request, ready for departure — is false on
    /// purpose: the flag marks airborne and pattern check-ins only.
    /// </summary>
    public bool IsInitialContact { get; init; }
}
