using Yaat.Client.Services;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// Loads the video maps the route composer draws, with no room: it lists every map of an ARTCC's vNAS config, ordered by
/// STARS id with unnamed ones last, then by name, and named by the config's name, its short name or its id; and it loads
/// one map by its id through the client's video-map service, whose parsed maps are cached on disk for the next offline
/// open. Listing downloads nothing.
/// </summary>
public static class ArtccVideoMapLoader
{
    /// <summary>Lists one ARTCC's video maps for the route composer, downloading nothing.</summary>
    /// <param name="configs">The config source the editor already reads the ARTCC from.</param>
    /// <param name="artccId">The ARTCC whose maps to list.</param>
    /// <param name="ct">Cancels the listing.</param>
    /// <returns>The maps, in the order the composer's check boxes show them.</returns>
    /// <exception cref="InvalidOperationException">The ARTCC's config could not be read.</exception>
    public static async Task<IReadOnlyList<RouteComposerMap>> ListAsync(ArtccConfigSource configs, string artccId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(configs);
        (_, ArtccConfigRoot config) = await ConfigAsync(configs, artccId, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return [.. Ordered(config.VideoMaps).Select(map => new RouteComposerMap(NameOf(map), map.Id))];
    }

    /// <summary>Loads one of an ARTCC's video maps by its id in the ARTCC's config.</summary>
    /// <param name="download">Downloads and parses one map of an ARTCC.</param>
    /// <param name="artccId">The ARTCC the map belongs to.</param>
    /// <param name="mapId">The map's id in the ARTCC's config.</param>
    /// <param name="configs">The config source the map is described from.</param>
    /// <param name="ct">Cancels the load.</param>
    /// <returns>The parsed map, or null when it cannot be fetched.</returns>
    /// <exception cref="InvalidOperationException">
    /// The ARTCC's config could not be read, or it no longer lists the map.
    /// </exception>
    public static async Task<VideoMapData?> LoadOneAsync(
        Func<string, VideoMapInfoDto, CancellationToken, Task<VideoMapData?>> download,
        string artccId,
        string mapId,
        ArtccConfigSource configs,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(configs);
        (ArtccConfigLoad load, ArtccConfigRoot config) = await ConfigAsync(configs, artccId, ct).ConfigureAwait(false);
        if (config.VideoMaps.Find(map => map.Id == mapId) is not { } listed)
        {
            throw new InvalidOperationException($"The ARTCC config no longer lists the video map {mapId}.");
        }

        ct.ThrowIfCancellationRequested();
        return await download(load.ArtccId, ToInfo(listed), ct).ConfigureAwait(false);
    }

    // The ARTCC's config and the load that carried it; a config that could not be read throws with the load's problem.
    private static async Task<(ArtccConfigLoad Load, ArtccConfigRoot Config)> ConfigAsync(
        ArtccConfigSource configs,
        string artccId,
        CancellationToken ct
    )
    {
        ArtccConfigLoad load = await configs.LoadAsync(artccId, ct).ConfigureAwait(false);
        if (load.Config is not { } config)
        {
            throw new InvalidOperationException(load.Problem ?? $"the ARTCC config for {load.ArtccId} could not be read");
        }

        return (load, config);
    }

    private static IEnumerable<VideoMapConfig> Ordered(List<VideoMapConfig> maps) =>
        maps.OrderBy(SortId).ThenBy(map => map.Name, StringComparer.OrdinalIgnoreCase);

    // A map's STARS id, or int.MaxValue when it has none: a config field left out reads as 0, and those sort last.
    private static int SortId(VideoMapConfig map) => (map.StarsId <= 0) ? int.MaxValue : map.StarsId;

    private static string NameOf(VideoMapConfig map) =>
        !string.IsNullOrWhiteSpace(map.Name) ? map.Name
        : !string.IsNullOrWhiteSpace(map.ShortName) ? map.ShortName
        : map.Id;

    private static VideoMapInfoDto ToInfo(VideoMapConfig map) =>
        new(map.Id, map.Name, map.ShortName, map.Tags, map.StarsBrightnessCategory, map.StarsId, map.StarsAlwaysVisible, map.TdmOnly);
}
