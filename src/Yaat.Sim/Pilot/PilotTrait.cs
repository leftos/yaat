using System.Globalization;

namespace Yaat.Sim.Pilot;

/// <summary>How much a pilot says on the frequency, independent of how busy it is.</summary>
public enum PilotTrait
{
    Terse,
    Normal,
    Chatty,
}

/// <summary>The per-pilot trait drawn from the callsign and the scenario seed, and the load band it makes the pilot speak at.</summary>
public static class PilotTraits
{
    /// <summary>The pilot's trait: terse 25%, normal 50%, chatty 25%, fixed per callsign and scenario seed.</summary>
    public static PilotTrait For(string callsign, int rngSeed) =>
        (DeterministicHash.Mix(DeterministicHash.Fnv1a(string.Create(CultureInfo.InvariantCulture, $"pilot-trait:{rngSeed}:"), callsign)) % 4) switch
        {
            0 => PilotTrait.Terse,
            3 => PilotTrait.Chatty,
            _ => PilotTrait.Normal,
        };

    /// <summary>
    /// The band a pilot of <paramref name="trait"/> speaks at: terse one band busier, chatty one band quieter, clamped to Quiet..Busy.
    /// </summary>
    public static FrequencyLoadBand EffectiveBand(FrequencyLoadBand band, PilotTrait trait)
    {
        if (!Enum.IsDefined(band))
        {
            throw new ArgumentOutOfRangeException(nameof(band), band, "Unknown frequency load band.");
        }

        int shift = trait switch
        {
            PilotTrait.Terse => 1,
            PilotTrait.Normal => 0,
            PilotTrait.Chatty => -1,
            _ => throw new ArgumentOutOfRangeException(nameof(trait), trait, "Unknown pilot trait."),
        };
        return (FrequencyLoadBand)Math.Clamp((int)band + shift, (int)FrequencyLoadBand.Quiet, (int)FrequencyLoadBand.Busy);
    }
}
