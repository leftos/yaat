using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport.Precompute;

/// <summary>
/// The shipped push-target seed for an airport's stands: the entries of its <see cref="PrecomputeStore"/> file, read
/// without materializing the layout, so a caller that has its own layout (the client) can fill before its own live plan
/// of a stand returns. An airport is read at most once and that one read is shared by every caller, so the seed is safe
/// to ask from a UI thread and from a background plan at the same time.
/// </summary>
/// <param name="store">The store the seed is read from.</param>
/// <param name="artccsBaseDir">The ARTCCs data directory the airport's sidecar hash is taken over.</param>
public sealed class PushTargetSeed(PrecomputeStore store, string artccsBaseDir)
{
    private static readonly ILogger Log = SimLog.CreateLogger("PushTargetSeed");

    private readonly ConcurrentDictionary<string, Lazy<AirportSeed>> _airports = new(StringComparer.Ordinal);

    /// <summary>
    /// The seeded entry for <paramref name="standName"/> and <paramref name="group"/>, or null when there is no seed to
    /// use: no file for the airport, a file whose key is not current offline
    /// (<see cref="PrecomputeKey.PushTargetsMatchOffline"/>), no entry for the stand, or no entry for the stand and the
    /// group. Each of those is logged at Debug. Two failures are logged at Warning and answered null instead, because
    /// the live plan that follows is the correct answer rather than a stand-in: a file whose contents cannot be parsed
    /// (<see cref="InvalidDataException"/>, remembered as no seed, since a file that is not a precompute document will
    /// not become one) and a file that cannot be opened this time (<see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/>, not remembered, so the next call reads it again).
    /// </summary>
    /// <param name="airportId">
    /// The airport as its layout names it: the FAA id the cache file is named by (<c>OAK.json.br</c>). A K-prefixed
    /// CONUS ICAO (<c>KOAK</c>) is accepted; an id whose own first letter is K is not (<c>PHNL</c>, <c>PANC</c> and
    /// <c>TJSJ</c> keep their K and find no file).
    /// </param>
    /// <param name="standName">The stand's name, as the layout names it.</param>
    /// <param name="group">The aircraft's design group.</param>
    /// <returns>The seeded entry, or null when the airport has no seed for it.</returns>
    /// <exception cref="DirectoryNotFoundException">
    /// <c>artccsBaseDir</c> does not exist: the sidecar hash cannot be taken, so no stored key can be judged current.
    /// </exception>
    public PushTargetEntry? Find(string airportId, string standName, AirplaneDesignGroup group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(airportId);
        ArgumentException.ThrowIfNullOrWhiteSpace(standName);

        // The cache files are named for the FAA id (OAK.json.br), and the sidecar hash normalizes what it is given, so
        // one normalized id serves both. It is also the memo key, so OAK and KOAK share a single read.
        string faaId = NavigationDatabase.NormalizeAirport(airportId);
        AirportSeed seed = ReadOnce(faaId);
        if (seed.Targets is not { } targets)
        {
            return null;
        }

        PushTargetEntry? stand = targets.FirstOrDefault(t => string.Equals(t.StandName, standName, StringComparison.Ordinal));
        if (stand is null)
        {
            Log.LogDebug("Stand {Stand} at {Airport} has no seeded push targets", standName, faaId);
            return null;
        }

        // PushTargetPlanner writes the entry's design group as the group's Roman form, so that is what is compared.
        string roman = group.ToString();
        PushTargetEntry? entry = targets.FirstOrDefault(t =>
            (string.Equals(t.StandName, standName, StringComparison.Ordinal)) && (string.Equals(t.DesignGroup, roman, StringComparison.Ordinal))
        );
        if (entry is null)
        {
            Log.LogDebug("Stand {Stand} at {Airport} has no seeded push targets for design group {Group}", standName, faaId, roman);
        }

        return entry;
    }

    /// <summary>
    /// The airport's seed, read once however many callers ask for it at once. A read that faults is not remembered: the
    /// entry is dropped before the fault is rethrown, so the next call reads the file again rather than rethrowing the
    /// first caller's failure for the rest of the session.
    /// </summary>
    private AirportSeed ReadOnce(string faaId)
    {
        Lazy<AirportSeed> lazy = _airports.GetOrAdd(faaId, id => new Lazy<AirportSeed>(() => Read(id), LazyThreadSafetyMode.ExecutionAndPublication));
        AirportSeed seed;
        try
        {
            seed = lazy.Value;
        }
        catch (Exception)
        {
            _airports.TryRemove(KeyValuePair.Create(faaId, lazy));
            throw;
        }

        if (seed.Transient)
        {
            _airports.TryRemove(KeyValuePair.Create(faaId, lazy));
        }

        return seed;
    }

    /// <summary>The airport's seed, or why there is none.</summary>
    private AirportSeed Read(string faaId)
    {
        PrecomputeKey? key;
        IReadOnlyList<PushTargetEntry>? targets;
        try
        {
            if (!store.TryReadPushTargets(faaId, out key, out targets))
            {
                Log.LogDebug("There is no precompute cache file for {Airport}; there is no push-target seed", faaId);
                return new AirportSeed(null, Transient: false);
            }
        }
        catch (InvalidDataException ex)
        {
            Log.LogWarning(ex, "The precompute cache file for {Airport} cannot be read; it is treated as no seed", faaId);
            return new AirportSeed(null, Transient: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "The precompute cache file for {Airport} cannot be opened; no seed this time", faaId);
            return new AirportSeed(null, Transient: true);
        }

        if (key.PushTargetsMatchOffline(PrecomputeKey.CurrentOffline(AirportSidecarHash.For(artccsBaseDir, faaId))))
        {
            return new AirportSeed(targets, Transient: false);
        }

        Log.LogDebug("The push-target seed for {Airport} is stale; it is not used", faaId);
        return new AirportSeed(null, Transient: false);
    }

    /// <summary>
    /// One airport's seed: its stored entries, or null when there is no seed to use for it.
    /// <see cref="Transient"/> marks a read that failed on a passing condition, so it must not be remembered.
    /// </summary>
    private sealed record AirportSeed(IReadOnlyList<PushTargetEntry>? Targets, bool Transient);
}
