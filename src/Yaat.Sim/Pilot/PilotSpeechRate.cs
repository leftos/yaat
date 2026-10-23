namespace Yaat.Sim.Pilot;

/// <summary>How fast a pilot speaks relative to the controller's chosen voice speed, by frequency load.</summary>
public static class PilotSpeechRate
{
    /// <summary>
    /// The factor the client multiplies its speech speed by: faster when the frequency is busy, never slower than the
    /// chosen speed.
    /// </summary>
    /// <param name="band">The frequency's load band when the transmission went on air.</param>
    /// <returns>1.1 for Busy, 1.0 for Normal and Quiet.</returns>
    public static double LoadFactor(FrequencyLoadBand band) =>
        band switch
        {
            FrequencyLoadBand.Busy => 1.1,
            FrequencyLoadBand.Normal or FrequencyLoadBand.Quiet => 1.0,
            _ => throw new ArgumentOutOfRangeException(nameof(band), band, "Unknown frequency load band."),
        };
}
