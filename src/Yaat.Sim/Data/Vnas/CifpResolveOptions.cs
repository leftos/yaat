namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// Options for <see cref="CifpPathResolver.EnsureCurrentCycle"/>.
/// </summary>
public sealed record CifpResolveOptions(
    string? ExplicitPath = null,
    string? BundledGzPath = null,
    string? BundledManifestPath = null,
    bool AllowDownload = true
);
