using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Weather;

/// <summary>
/// The process-wide store of GFS wind grids, content-addressed by the SHA-256 of their raw GRIB2 bytes (lowercase hex). A
/// weather profile names its grid by that hash (<see cref="WeatherProfile.WindGridHash"/>), so the text every replay,
/// restore and record path carries resolves to the same grid wherever the bytes were registered. A pinned entry is never
/// evicted; at most <see cref="MaxUnpinned"/> unpinned entries are kept, the least recently used evicted first.
/// Thread-safe.
/// </summary>
public static class WindGridRegistry
{
    private static readonly ILogger Log = SimLog.CreateLogger("WindGridRegistry");

    /// <summary>The most unpinned entries kept; registering past it evicts the least recently used.</summary>
    public const int MaxUnpinned = 8;

    /// <summary>
    /// The largest GFS grid, in raw GRIB2 bytes, any path takes in: a member's upload, a recording's or a checkpoint's grid
    /// entry once decompressed, and the client's fetch.
    /// </summary>
    public const int MaxGridBytes = 512 * 1024;

    /// <summary>
    /// The most GFS grids one room may pin besides its pending upload, and the most a recording may carry.
    /// GFS runs every 6 hours, so 16 covers four days of a session's weather.
    /// </summary>
    public const int MaxRoomGrids = 16;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    private static long _clock;

    /// <summary>The number of grids held, pinned or not.</summary>
    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return Entries.Count;
            }
        }
    }

    /// <summary>
    /// Decompresses a Brotli-compressed grid entry, reading at most <see cref="MaxGridBytes"/> + 1 bytes of its output, so an
    /// entry that would inflate past the limit costs no more than the limit to refuse. Whatever length the archive declares
    /// for the entry is not consulted.
    /// </summary>
    /// <param name="compressed">The entry's stream; left open.</param>
    /// <returns>The GRIB2 bytes, or null when they pass <see cref="MaxGridBytes"/>.</returns>
    /// <exception cref="InvalidOperationException">The bytes are not a Brotli stream.</exception>
    /// <exception cref="IOException">The entry cannot be read.</exception>
    public static byte[]? ReadBrotliWithinLimit(Stream compressed)
    {
        using var brotli = new BrotliStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        byte[] buffer = new byte[MaxGridBytes + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = brotli.Read(buffer.AsSpan(total));
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total > MaxGridBytes ? null : buffer[..total];
    }

    /// <summary>The SHA-256 of <paramref name="grib2"/> as lowercase hex: the name a grid is registered and recorded under.</summary>
    public static string HashOf(ReadOnlySpan<byte> grib2) => Convert.ToHexStringLower(SHA256.HashData(grib2));

    /// <summary>
    /// Decodes <paramref name="grib2"/> into a wind field and stores the bytes and the field under their hash. Registering
    /// bytes already held returns the same hash and stores nothing new. The entry is unpinned and may be evicted at once by a
    /// later registration elsewhere; a caller that needs it kept uses <see cref="RegisterAndPin"/>.
    /// </summary>
    /// <param name="grib2">A GRIB2 file holding UGRD, VGRD and TMP on isobaric levels.</param>
    /// <returns>The hash the grid is stored under.</returns>
    /// <exception cref="Grib2FormatException">The bytes are not a GRIB2 file the reader can decode.</exception>
    /// <exception cref="InvalidOperationException">The messages do not make a wind field (<see cref="GriddedWindField.FromMessages"/>).</exception>
    public static string Register(byte[] grib2)
    {
        string hash = HashOf(grib2);
        Store(hash, grib2, pin: false);
        return hash;
    }

    /// <summary>
    /// <see cref="Register"/>, with the entry pinned in the same step, so no registration elsewhere can evict it between the
    /// insert and the pin.
    /// </summary>
    /// <param name="grib2">A GRIB2 file holding UGRD, VGRD and TMP on isobaric levels.</param>
    /// <param name="hash">The hash the grid is stored under.</param>
    /// <returns>The pin; disposing it makes the entry evictable again once no other pin holds it.</returns>
    /// <exception cref="Grib2FormatException">The bytes are not a GRIB2 file the reader can decode.</exception>
    /// <exception cref="InvalidOperationException">The messages do not make a wind field (<see cref="GriddedWindField.FromMessages"/>).</exception>
    public static IDisposable RegisterAndPin(byte[] grib2, out string hash)
    {
        hash = HashOf(grib2);
        return Store(hash, grib2, pin: true)!;
    }

    /// <summary>The decoded field registered under <paramref name="hash"/>; marks the entry used.</summary>
    public static bool TryGetField(string hash, [NotNullWhen(true)] out GriddedWindField? field)
    {
        lock (Gate)
        {
            if (Entries.TryGetValue(hash, out Entry? entry))
            {
                entry.LastUse = ++_clock;
                field = entry.Field;
                return true;
            }
        }

        field = null;
        return false;
    }

    /// <summary>The raw GRIB2 bytes registered under <paramref name="hash"/>; marks the entry used.</summary>
    public static bool TryGetBytes(string hash, out ReadOnlyMemory<byte> bytes)
    {
        lock (Gate)
        {
            if (Entries.TryGetValue(hash, out Entry? entry))
            {
                entry.LastUse = ++_clock;
                bytes = entry.Bytes;
                return true;
            }
        }

        bytes = ReadOnlyMemory<byte>.Empty;
        return false;
    }

    /// <summary>
    /// Keeps the grid under <paramref name="hash"/> from eviction until the returned handle is disposed. Pins count: the
    /// entry is evictable again once every handle is disposed.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No grid is registered under <paramref name="hash"/>.</exception>
    public static IDisposable Pin(string hash) =>
        TryPin(hash, out IDisposable? pin) ? pin : throw new KeyNotFoundException($"Cannot pin GFS wind grid {hash}: it is not registered");

    /// <summary><see cref="Pin"/>, returning false instead of throwing when no grid is registered under <paramref name="hash"/>.</summary>
    public static bool TryPin(string hash, [NotNullWhen(true)] out IDisposable? pin)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(hash, out Entry? entry))
            {
                pin = null;
                return false;
            }

            entry.LastUse = ++_clock;
            entry.PinCount++;
            pin = new PinHandle(hash, entry);
            return true;
        }
    }

    /// <summary>
    /// Sets <paramref name="profile"/>'s <see cref="WeatherProfile.WindGrid"/> from its <see cref="WeatherProfile.WindGridHash"/>.
    /// Only live weather carries a grid (<see cref="IsLiveWeather"/>): a hash on any other profile is ignored with a warning.
    /// A hash with no registered grid logs a warning and leaves the profile on its FD wind layers.
    /// </summary>
    public static void Attach(WeatherProfile profile)
    {
        if (profile.WindGridHash is not { } hash)
        {
            return;
        }

        if (!IsLiveWeather(profile))
        {
            Log.LogWarning(
                "GFS wind grid {Hash} on weather '{Name}' ignored: only live weather (METARs and a surface wind layer at 0 ft) carries a grid",
                hash,
                profile.Name
            );
            return;
        }

        if (!TryGetField(hash, out GriddedWindField? field))
        {
            Log.LogWarning("GFS wind grid {Hash} not available; flying the FD wind layers", hash);
            return;
        }

        profile.WindGrid = field;
    }

    /// <summary>
    /// True for a profile shaped like live weather: it has METARs, and its lowest wind layer is the METAR surface layer at
    /// 0 ft MSL that the grid column sits above.
    /// </summary>
    public static bool IsLiveWeather(WeatherProfile profile) =>
        (profile.Metars.Count > 0) && (profile.WindLayers.Count > 0) && (profile.WindLayers[0].Altitude == 0);

    /// <summary>
    /// Forgets every grid and pin, so a test starts from an empty registry. A pin handle taken before the reset releases
    /// nothing afterwards, even when the same bytes have been registered again.
    /// </summary>
    public static void ResetForTests()
    {
        lock (Gate)
        {
            Entries.Clear();
            _clock = 0;
        }
    }

    /// <summary>Inserts the grid unless held, marks it used, pins it when asked, then evicts past the unpinned bound.</summary>
    private static PinHandle? Store(string hash, byte[] grib2, bool pin)
    {
        lock (Gate)
        {
            if (Entries.TryGetValue(hash, out Entry? held))
            {
                return UseAndPin(hash, held, pin);
            }
        }

        byte[] copy = [.. grib2];
        var field = GriddedWindField.FromMessages(Grib2Reader.Read(copy));
        lock (Gate)
        {
            if (Entries.TryGetValue(hash, out Entry? raced))
            {
                return UseAndPin(hash, raced, pin);
            }

            var entry = new Entry(copy, field);
            Entries[hash] = entry;
            Log.LogInformation(
                "Registered GFS wind grid {Hash}: {Bytes} bytes, run {ReferenceTimeUtc:u} f{ForecastHours:D3}, {Levels} levels",
                hash,
                copy.Length,
                field.ReferenceTimeUtc,
                field.ForecastHours,
                field.LevelsHpa.Count
            );
            PinHandle? handle = UseAndPin(hash, entry, pin);
            EvictUnpinned();
            return handle;
        }
    }

    private static PinHandle? UseAndPin(string hash, Entry entry, bool pin)
    {
        entry.LastUse = ++_clock;
        if (!pin)
        {
            return null;
        }

        entry.PinCount++;
        return new PinHandle(hash, entry);
    }

    private static void Unpin(string hash, Entry pinned)
    {
        lock (Gate)
        {
            // The entry a handle pinned may have been dropped by ResetForTests and the bytes registered again as a new entry;
            // the handle then has nothing to release.
            if (Entries.TryGetValue(hash, out Entry? entry) && ReferenceEquals(entry, pinned) && (entry.PinCount > 0))
            {
                entry.PinCount--;
                EvictUnpinned();
            }
        }
    }

    private static void EvictUnpinned()
    {
        List<KeyValuePair<string, Entry>> unpinned = [.. Entries.Where(e => e.Value.PinCount == 0).OrderBy(e => e.Value.LastUse)];
        for (int i = 0; i < unpinned.Count - MaxUnpinned; i++)
        {
            Entries.Remove(unpinned[i].Key);
            Log.LogInformation("Evicted GFS wind grid {Hash}, the least recently used of {Count} unpinned grids", unpinned[i].Key, unpinned.Count);
        }
    }

    private sealed class Entry(byte[] bytes, GriddedWindField field)
    {
        public byte[] Bytes { get; } = bytes;
        public GriddedWindField Field { get; } = field;
        public long LastUse { get; set; }
        public int PinCount { get; set; }
    }

    private sealed class PinHandle(string hash, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Unpin(hash, entry);
            }
        }
    }
}
