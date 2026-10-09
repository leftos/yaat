using Yaat.Sim.Data.Airport.Precompute;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// A temporary copy of the committed OAK precompute entry keyed the way a client with no vNAS keys it
/// (<see cref="PrecomputeKey.CurrentOffline"/>), so a seed over it passes the staleness check with targets that were
/// really planned; the committed file's own key is stale between the maintainer's regenerations.
/// </summary>
public sealed class OakPushTargetSeedCopy : IDisposable
{
    private static readonly string ShippedCacheDir = Path.Combine(AppContext.BaseDirectory, "Data", "PrecomputeCache");

    private static readonly string ArtccsDir = Path.Combine(AppContext.BaseDirectory, "Data", "ARTCCs");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-ui-push-seed-" + Guid.NewGuid());

    public OakPushTargetSeedCopy()
    {
        var shipped = new PrecomputeStore(ShippedCacheDir);
        if (!shipped.TryRead("OAK", out PrecomputeEntry? entry))
        {
            throw new InvalidOperationException($"The committed OAK cache file is missing from {ShippedCacheDir}");
        }

        new PrecomputeStore(_root).Write(entry with { Key = PrecomputeKey.CurrentOffline(AirportSidecarHash.For(ArtccsDir, "OAK")) });
    }

    /// <summary>A fresh seed over the copy, which has read nothing yet.</summary>
    public PushTargetSeed NewSeed() => new(new PrecomputeStore(_root), ArtccsDir);

    /// <summary>A seed over a folder that holds no cache file, so every stand has no seed.</summary>
    public static PushTargetSeed NoSeed() =>
        new(new PrecomputeStore(Path.Combine(Path.GetTempPath(), "yaat-ui-push-no-seed-" + Guid.NewGuid())), ArtccsDir);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
