using Yaat.Sim.Data.Faa;

namespace Yaat.PrecomputeCache;

/// <summary>Everything a run reaches outside its arguments, so a test can stand in for the network and the navdata.</summary>
public sealed record PrecomputeCacheServices
{
    /// <summary>The client every vNAS request goes through.</summary>
    public required HttpClient Http { get; init; }

    /// <summary>Where fetched airport GeoJSON is cached.</summary>
    public required string GeoJsonCacheDir { get; init; }

    /// <summary>The ARTCCs data directory the sidecars and their hash are read from.</summary>
    public required string ArtccsDir { get; init; }

    /// <summary>The folder relative paths, and the search for the repo root, start from.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// Loads the current navdata and CIFP into the navigation database and returns the loaded navdata's serial, which is a
    /// cached copy's when the download failed.
    /// </summary>
    public required Func<Task<long>> InitializeNavigationAsync { get; init; }

    /// <summary>Loads the current FAA aircraft records by ICAO designator; empty when none could be loaded.</summary>
    public required Func<Task<IReadOnlyDictionary<string, FaaAircraftRecord>>> LoadFaaRecordsAsync { get; init; }

    /// <summary>Where progress and results go.</summary>
    public required TextWriter Out { get; init; }

    /// <summary>Where failures, stale entries and warnings go.</summary>
    public required TextWriter Error { get; init; }
}
