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

    /// <summary>
    /// The reading-back aircraft's indicated airspeed in knots as the readback is built, which decides whether a speed readback
    /// may say it is slowing, speeding up or maintaining (<see cref="Yaat.Sim.Speech.PilotVariant.SpeedChangeTags"/>); null when
    /// the caller has no aircraft state to read it from, and then no such row is spoken.
    /// </summary>
    public required double? CurrentIndicatedAirspeed { get; init; }

    /// <summary>
    /// The verbatim context at normal load, seed 0, second 0, full callsign, digit frequency and no current speed: the
    /// canonical readback.
    /// </summary>
    public static PilotSpeechContext Canonical =>
        new(PilotPersonality.Verbatim, FrequencyLoadBand.Normal, 0, 0)
        {
            ShortenCallsign = false,
            GroupContactFrequency = false,
            CurrentIndicatedAirspeed = null,
        };
}
