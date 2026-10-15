using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Weather;

/// <summary>
/// One owner's pins on a set of GFS grids in <see cref="WindGridRegistry"/>: while it is undisposed none of
/// <see cref="Hashes"/> can be evicted. Disposing releases every pin once.
/// </summary>
/// <param name="hashes">The hashes of the pinned grids.</param>
/// <param name="pins">The pins on <paramref name="hashes"/>, owned from here on.</param>
public sealed class WindGridPins(IReadOnlyList<string> hashes, IReadOnlyList<IDisposable> pins) : IDisposable
{
    private static readonly ILogger Log = SimLog.CreateLogger("WindGridPins");

    private int _disposed;

    /// <summary>The hashes of the pinned grids.</summary>
    public IReadOnlyList<string> Hashes { get; } = hashes;

    /// <summary>
    /// A second owner's pins on the same grids, independent of this one: disposing either leaves the other's in place. A
    /// grid no longer registered (this owner was disposed and it was evicted) is left out with a warning.
    /// </summary>
    public WindGridPins Repin()
    {
        var hashes = new List<string>(Hashes.Count);
        var pins = new List<IDisposable>(Hashes.Count);
        foreach (string hash in Hashes)
        {
            if (WindGridRegistry.TryPin(hash, out IDisposable? pin))
            {
                hashes.Add(hash);
                pins.Add(pin);
            }
            else
            {
                Log.LogWarning("GFS wind grid {Hash} is no longer registered; the weather naming it flies the FD wind layers", hash);
            }
        }

        return new WindGridPins(hashes, pins);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (IDisposable pin in pins)
        {
            pin.Dispose();
        }
    }
}
