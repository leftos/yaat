using System.Globalization;

namespace Yaat.Sim.Pilot;

/// <summary>The seeded, replay-safe choice among a phrase's variants for one pilot, one canonical body and one second.</summary>
public static class VariantPick
{
    /// <summary>An index in [0, <paramref name="count"/>) from the callsign, the canonical body, the whole second and the scenario seed.</summary>
    public static int Index(string callsign, string canonicalBody, int elapsedSecond, int rngSeed, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        string salt = string.Create(CultureInfo.InvariantCulture, $"variant:{rngSeed}:{elapsedSecond}:{canonicalBody}:");
        return (int)(DeterministicHash.Mix(DeterministicHash.Fnv1a(salt, callsign)) % (uint)count);
    }
}
