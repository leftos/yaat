namespace Yaat.Sim;

/// <summary>
/// The per-aircraft draw behind fixed per-callsign choices (a delay, a direction): FNV-1a 32 over a salt and the callsign,
/// replay-safe and with no RNG state. Each feature passes its own salt so its draws are not correlated with another's; an
/// empty salt is the plain FNV-1a of the callsign.
/// </summary>
internal static class DeterministicHash
{
    private const uint OffsetBasis = 2166136261u;
    private const uint Prime = 16777619u;

    /// <summary>FNV-1a over the UTF-16 code units of <paramref name="salt"/>, then those of <paramref name="callsign"/>.</summary>
    public static uint Fnv1a(string salt, string callsign)
    {
        uint h = OffsetBasis;
        foreach (char c in salt)
        {
            h = (h ^ c) * Prime;
        }

        foreach (char c in callsign)
        {
            h = (h ^ c) * Prime;
        }

        return h;
    }
}
