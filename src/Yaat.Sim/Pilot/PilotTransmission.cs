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
}
