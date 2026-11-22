namespace Yaat.Sim.Data.Vnas;

/// <summary>Where an <see cref="ArtccConfigLoad"/>'s config came from, or why there is none.</summary>
public enum ArtccConfigStatus
{
    /// <summary>Read from data-api now, or from a disk copy inside the TTL.</summary>
    Fresh,

    /// <summary>The disk copy, served because a refresh failed or data-api no longer has the ARTCC; it may be stale.</summary>
    FromCache,

    /// <summary>data-api has no such ARTCC (HTTP 404) and nothing is cached; or the id is not a valid ARTCC id.</summary>
    NotFound,

    /// <summary>data-api could not be reached and nothing is cached.</summary>
    Unreachable,

    /// <summary>The config that was read (from data-api or from disk) is not a readable ARTCC config.</summary>
    Unparseable,
}

/// <summary>What <see cref="ArtccConfigSource.LoadAsync(string, CancellationToken)"/> found for one ARTCC.</summary>
/// <param name="ArtccId">The ARTCC id as loaded: trimmed and upper-cased.</param>
/// <param name="Config">The config, or null when <paramref name="Status"/> is NotFound, Unreachable or Unparseable.</param>
/// <param name="Status">Where the config came from, or why there is none.</param>
/// <param name="Problem">What went wrong, for the user; null when the config is fresh.</param>
/// <param name="CachedCopyUtc">When the disk copy served as <see cref="ArtccConfigStatus.FromCache"/> was written; else null.</param>
public sealed record ArtccConfigLoad(string ArtccId, ArtccConfigRoot? Config, ArtccConfigStatus Status, string? Problem, DateTime? CachedCopyUtc);
