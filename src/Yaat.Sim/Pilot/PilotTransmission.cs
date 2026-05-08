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
public sealed record PilotTransmission(string Callsign, string Text, string SpeechText, string SourceKind, PilotTransmissionKind Kind);
