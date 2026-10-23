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
public sealed record PilotTransmission(string Callsign, string Text, string SpeechText, string SourceKind, PilotTransmissionKind Kind)
{
    /// <summary>
    /// The frequency's load band at the moment the transmission went on air, counting the transmission itself.
    /// Null until <see cref="FrequencyState.TryDequeueReady"/> dequeues it.
    /// </summary>
    public FrequencyLoadBand? LoadBand { get; init; }
}
