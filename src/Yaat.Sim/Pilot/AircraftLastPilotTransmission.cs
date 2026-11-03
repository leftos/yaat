using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Pilot;

/// <summary>
/// The last line an aircraft spoke on the student's frequency, recorded as the frequency dequeued it, so a "say again"
/// can re-queue the same words. It carries only what a re-queued copy needs to be spoken and gated exactly as the
/// original: the load band is stamped afresh at each dequeue, and an initial-contact call is never repeated as one.
/// </summary>
public sealed class AircraftLastPilotTransmission
{
    /// <summary>Terminal (SAY-column, callsign-free) form of the line.</summary>
    public required string Text { get; init; }

    /// <summary>Spoken (TTS, callsign spelled) form of the line.</summary>
    public required string SpeechText { get; init; }

    /// <summary>What kind of call it was: it selects the terminal entry and the frequency gates a repeat arms.</summary>
    public required PilotTransmissionKind Kind { get; init; }

    /// <summary>The builder that produced the line, which a re-queued copy carries for the client's voice path.</summary>
    public required string SourceKind { get; init; }

    /// <summary>The airtime the frequency meter records for the line: a repeat counts the same as the original.</summary>
    public required double CanonicalAirtimeSeconds { get; init; }

    /// <summary>The sim second the line was dequeued from the frequency queue.</summary>
    public required double AtSeconds { get; init; }

    public AircraftLastPilotTransmissionDto ToSnapshot() =>
        new()
        {
            Text = Text,
            SpeechText = SpeechText,
            Kind = (int)Kind,
            SourceKind = SourceKind,
            CanonicalAirtimeSeconds = CanonicalAirtimeSeconds,
            AtSeconds = AtSeconds,
        };

    public static AircraftLastPilotTransmission FromSnapshot(AircraftLastPilotTransmissionDto dto) =>
        new()
        {
            Text = dto.Text,
            SpeechText = dto.SpeechText,
            Kind = (PilotTransmissionKind)dto.Kind,
            SourceKind = dto.SourceKind,
            CanonicalAirtimeSeconds = dto.CanonicalAirtimeSeconds,
            AtSeconds = dto.AtSeconds,
        };
}
