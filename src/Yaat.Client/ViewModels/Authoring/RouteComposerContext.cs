using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>One video map the route composer can draw under the route.</summary>
/// <param name="Name">The map's name, as its check box shows it.</param>
/// <param name="Data">The map's parsed lines.</param>
public sealed record RouteComposerMap(string Name, VideoMapData Data);

/// <summary>What the route composer opens over: the ARTCC it composes in, how fixes resolve, and that ARTCC's video maps.</summary>
/// <param name="ArtccId">The ARTCC a saved route is tagged with and the route list is filtered to by default, such as <c>ZOA</c>.</param>
/// <param name="Fixes">The airports a fix or FRD anchor may fall back to, as the sim resolves the route.</param>
/// <param name="LoadMaps">Loads the ARTCC's video maps; run off the UI thread.</param>
public sealed record RouteComposerContext(
    string ArtccId,
    FixResolutionContext Fixes,
    Func<CancellationToken, Task<IReadOnlyList<RouteComposerMap>>> LoadMaps
);
