namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// Options for <see cref="CifpPathResolver.EnsureCurrentCycle"/>.
/// </summary>
public sealed record CifpResolveOptions(
    string? ExplicitPath = null,
    string? BundledGzPath = null,
    string? BundledManifestPath = null,
    bool AllowDownload = true
)
{
    /// <summary>
    /// Resolve the committed <see cref="BundledGzPath"/> alone: never the user cache, never a download, never the
    /// stale-cache fallback, and the reported cycle is <see cref="BundledManifestPath"/>'s. Every test entry point
    /// pins this way, so each machine runs the AIRAC cycle committed to the repository whatever its user cache
    /// holds; a production caller never sets it. A missing bundle or manifest under the pin is a hard failure.
    /// </summary>
    public required bool PinToBundled { get; init; }
}
