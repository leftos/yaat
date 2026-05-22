namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// Options for <see cref="NavDataPathResolver.EnsureCurrent"/>.
/// </summary>
public sealed record NavDataResolveOptions(
    string? ExplicitPath = null,
    string? BundledPath = null,
    string? BundledManifestPath = null,
    bool AllowDownload = true
);
