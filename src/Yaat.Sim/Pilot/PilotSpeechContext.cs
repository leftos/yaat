namespace Yaat.Sim.Pilot;

/// <summary>What a readback's spoken wording depends on beyond the command and the aircraft.</summary>
public readonly record struct PilotSpeechContext(PilotPersonality Personality, FrequencyLoadBand Band, int RngSeed, int ElapsedSecond)
{
    /// <summary>
    /// Whether the readback closes with the abbreviated N-number ("november three alpha bravo") instead of the full
    /// callsign (<see cref="SpokenFormDraws.CanShortenCallsign"/>, <see cref="SpokenFormDraws.DrawShortCallsign"/>).
    /// </summary>
    public required bool ShortenCallsign { get; init; }

    /// <summary>
    /// Whether a contact readback speaks its frequency grouped without the leading 1 ("twenty five thirty five")
    /// (<see cref="SpokenFormDraws.DrawGroupedContactFrequency"/>).
    /// </summary>
    public required bool GroupContactFrequency { get; init; }

    /// <summary>The verbatim context at normal load, seed 0, second 0, full callsign and digit frequency: the canonical readback.</summary>
    public static PilotSpeechContext Canonical =>
        new(PilotPersonality.Verbatim, FrequencyLoadBand.Normal, 0, 0) { ShortenCallsign = false, GroupContactFrequency = false };
}
