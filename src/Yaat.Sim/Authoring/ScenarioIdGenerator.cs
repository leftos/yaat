using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Authoring;

/// <summary>
/// Makes new scenario ids as ULIDs, the form vNAS uses: 26 Crockford base32 characters, ten for the millisecond Unix
/// timestamp and sixteen for 80 random bits. Ids made later sort after earlier ones, and every id is a valid store id.
/// </summary>
public static class ScenarioIdGenerator
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int TimeChars = 10;
    private const int RandomBytes = 10;
    private const int IdLength = 26;
    private const long MaxTimestamp = (1L << 48) - 1;

    private static readonly ILogger Log = SimLog.CreateLogger("ScenarioIdGenerator");

    /// <summary>Makes a ULID from a clock reading and the caller's random source.</summary>
    /// <param name="now">The time the id is made; its Unix milliseconds are the id's first ten characters.</param>
    /// <param name="random">The source of the id's 80 random bits; the same seed and time give the same id.</param>
    /// <returns>The 26-character id.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="now"/> is before 1970 or past the 48-bit range.</exception>
    public static string Create(DateTimeOffset now, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        long ms = now.ToUnixTimeMilliseconds();
        ArgumentOutOfRangeException.ThrowIfNegative(ms, nameof(now));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ms, MaxTimestamp, nameof(now));

        Span<char> id = stackalloc char[IdLength];
        for (int i = TimeChars - 1; i >= 0; i--)
        {
            id[i] = Alphabet[(int)(ms & 31)];
            ms >>= 5;
        }

        Span<byte> bytes = stackalloc byte[RandomBytes];
        random.NextBytes(bytes);
        EncodeRandom(bytes, id[TimeChars..]);
        string result = new(id);
        if (Log.IsEnabled(LogLevel.Trace))
        {
            Log.LogTrace("Made scenario id {Id}", result);
        }

        return result;
    }

    // 80 bits, most significant first, five bits per character.
    private static void EncodeRandom(ReadOnlySpan<byte> bytes, Span<char> chars)
    {
        int buffer = 0;
        int bits = 0;
        int at = 0;
        foreach (byte b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                chars[at++] = Alphabet[(buffer >> bits) & 31];
            }

            buffer &= (1 << bits) - 1;
        }
    }
}
