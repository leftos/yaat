using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>One video map the route composer can draw under the route, as the ARTCC's config lists it.</summary>
/// <param name="Name">The map's name, as its check box shows it.</param>
/// <param name="MapId">The map's id in the ARTCC's config, the id <see cref="RouteComposerContext.LoadMap"/> loads it by.</param>
public sealed record RouteComposerMap(string Name, string MapId);

/// <summary>What the route composer opens over: the ARTCC it composes in, how fixes resolve, and that ARTCC's video maps.</summary>
/// <param name="ArtccId">The ARTCC a saved route is tagged with and the route list is filtered to by default, such as <c>ZOA</c>.</param>
/// <param name="Fixes">The airports a fix or FRD anchor may fall back to, as the sim resolves the route.</param>
/// <param name="LoadMaps">Lists the ARTCC's video maps; run off the UI thread.</param>
/// <param name="LoadMap">
/// Loads one of those maps by its id, or null when it is not cached and cannot be fetched; run off the UI thread.
/// </param>
public sealed record RouteComposerContext(
    string ArtccId,
    FixResolutionContext Fixes,
    Func<CancellationToken, Task<IReadOnlyList<RouteComposerMap>>> LoadMaps,
    Func<string, CancellationToken, Task<VideoMapData?>> LoadMap
);
