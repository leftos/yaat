namespace Yaat.Sim.Pilot;

/// <summary>What a readback's spoken wording depends on beyond the command and the aircraft.</summary>
public readonly record struct PilotSpeechContext(PilotPersonality Personality, FrequencyLoadBand Band, int RngSeed, int ElapsedSecond)
{
    /// <summary>The verbatim context at normal load, seed 0, second 0: the canonical readback.</summary>
    public static PilotSpeechContext Canonical => new(PilotPersonality.Verbatim, FrequencyLoadBand.Normal, 0, 0);
}
