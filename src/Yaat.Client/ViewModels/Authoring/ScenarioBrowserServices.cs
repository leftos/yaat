using Avalonia.Threading;
using Yaat.Client.Services;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>What the scenario browser works through: the store, the network, the user and the UI thread.</summary>
/// <param name="Preferences">The Settings ARTCC and the editor's remembered ARTCC pick.</param>
/// <param name="Store">The local scenario store.</param>
/// <param name="Access">The ARTCCs the user may edit.</param>
/// <param name="Download">Downloads an ARTCC's server scenarios into the store, reporting progress; the network boundary.</param>
/// <param name="Prompts">The dialogs and pickers.</param>
/// <param name="Clock">The clock new scenario ids are made from.</param>
/// <param name="Random">The random source new scenario ids are made from.</param>
/// <param name="Post">Runs an action on the UI thread; tests queue it and choose the order.</param>
/// <param name="CanLoadInRoom">Whether a scenario can be loaded into the room now; re-read through
/// <see cref="ScenarioBrowserViewModel.RefreshTestInRoom"/>.</param>
/// <param name="LoadInRoom">Loads a scenario into the room: its JSON, display name and server origin id (null when local-only).</param>
/// <param name="RoomHasScenario">Whether the room runs a scenario now, which Test in Room asks to replace before it loads.</param>
/// <param name="Configs">The ARTCC configs the editor's position and airport pickers list their options from.</param>
/// <param name="RouteFinding">What Find Route searches: the route history's HTTP client and endpoint, and the other route sources.</param>
/// <param name="DownloadVideoMaps">Downloads and parses one video map of an ARTCC for the route composer.</param>
public sealed record ScenarioBrowserServices(
    UserPreferences Preferences,
    LocalScenarioStore Store,
    IArtccAccessSource Access,
    Func<string, IProgress<DownloadProgress>, CancellationToken, Task<DownloadReport>> Download,
    IScenarioBrowserPrompts Prompts,
    TimeProvider Clock,
    Random Random,
    Action<Action> Post,
    Func<bool> CanLoadInRoom,
    Func<string, string, string?, Task> LoadInRoom,
    Func<bool> RoomHasScenario,
    ArtccConfigSource Configs,
    RouteFindingServices RouteFinding,
    Func<string, VideoMapInfoDto, CancellationToken, Task<VideoMapData?>> DownloadVideoMaps
)
{
    // One client and one config source for the app's lifetime: every editor window shares the parsed configs.
    private static readonly HttpClient ConfigHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly ArtccConfigSource AppConfigs = new(ConfigHttp);

    // One video-map service for the app's lifetime: every editor window shares its parsed maps.
    private static readonly VideoMapService AppVideoMaps = new();

    /// <summary>
    /// The app's services: the app-data store, the given ARTCC access, data-api (scenarios and ARTCC configs), the video
    /// maps the route composer downloads, the system clock and the dispatcher; Test in Room is off (it can never load)
    /// until a room is given (<see cref="WithRoom"/>).
    /// </summary>
    /// <param name="preferences">The user's preferences.</param>
    /// <param name="prompts">The window's dialogs and pickers.</param>
    /// <param name="access">The ARTCCs the user may edit.</param>
    /// <returns>The services.</returns>
    public static ScenarioBrowserServices ForApp(UserPreferences preferences, IScenarioBrowserPrompts prompts, IArtccAccessSource access)
    {
        var store = new LocalScenarioStore();
        return new ScenarioBrowserServices(
            preferences,
            store,
            access,
            DownloadInto(store),
            prompts,
            TimeProvider.System,
            Random.Shared,
            action => Dispatcher.UIThread.Post(action),
            () => false,
            (_, _, _) => Task.CompletedTask,
            () => false,
            AppConfigs,
            RouteFindingServices.ForApp(),
            AppVideoMaps.LoadMapAsync
        );
    }

    /// <summary>
    /// These services with Test in Room over the main window's room: it can load while
    /// <see cref="MainViewModel.CanLoadScenario"/> holds, loads through <see cref="MainViewModel.LoadTestScenarioAsync"/>, and
    /// asks first while the room runs a scenario; the route composer's maps come from the room's video-map service, so a
    /// room on the same ARTCC shares its parsed cache.
    /// </summary>
    /// <param name="room">The main window's view model.</param>
    /// <returns>The services.</returns>
    public ScenarioBrowserServices WithRoom(MainViewModel room)
    {
        ArgumentNullException.ThrowIfNull(room);
        return this with
        {
            CanLoadInRoom = () => room.CanLoadScenario,
            LoadInRoom = room.LoadTestScenarioAsync,
            RoomHasScenario = () => room.ActiveScenarioId is not null,
            RouteFinding = RouteFinding with { HistoryEndpoint = () => RouteFindingServices.RoomEndpointAsync(room) },
            DownloadVideoMaps = room.VideoMaps.LoadMapAsync,
        };
    }

    /// <summary>A download seam over <see cref="ScenarioDownloader"/> with a new data-api client per run.</summary>
    /// <param name="store">The store the scenarios land in.</param>
    /// <returns>The seam.</returns>
    public static Func<string, IProgress<DownloadProgress>, CancellationToken, Task<DownloadReport>> DownloadInto(LocalScenarioStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return async (artccId, progress, ct) =>
        {
            using var client = new VnasTrainingClient();
            return await new ScenarioDownloader(client, store).DownloadArtccAsync(artccId, progress, ct).ConfigureAwait(false);
        };
    }
}

/// <summary>
/// What Find Route searches: csko, navdata and the FAA preferred routes (<paramref name="OtherSources"/>), then the SWIM route
/// history over <paramref name="HistoryHttp"/> at the endpoint <paramref name="HistoryEndpoint"/> resolves when a search
/// starts (null: not connected, which the history reports as its failure).
/// </summary>
/// <param name="HistoryHttp">The HTTP client the route history is asked over.</param>
/// <param name="HistoryEndpoint">Resolves the server and access token the route history is asked at, or null when there is none.</param>
/// <param name="OtherSources">The route sources searched before the history, in source order.</param>
public sealed record RouteFindingServices(
    HttpClient HistoryHttp,
    Func<Task<RouteHistoryEndpoint?>> HistoryEndpoint,
    Func<IReadOnlyList<IRouteSource>> OtherSources
)
{
    // One history client, csko source and preferred-route source for the app's lifetime: the sources keep their caches.
    private static readonly HttpClient AppHistoryHttp = new();
    private static readonly Lazy<CskoRouteSource> AppCsko = new(() => new CskoRouteSource());
    private static readonly Lazy<PreferredRouteSource> AppPreferred = new(() => new PreferredRouteSource(NavigationDatabase.Instance));

    /// <summary>The app's route sources: csko, navdata over the current navdata, preferred routes, and a history with no endpoint.</summary>
    /// <returns>The services.</returns>
    public static RouteFindingServices ForApp() =>
        new(AppHistoryHttp, NoEndpoint, () => [AppCsko.Value, new NavdataRouteSource(NavigationDatabase.Instance), AppPreferred.Value]);

    /// <summary>No route history endpoint: not connected to a YAAT server.</summary>
    /// <returns>Null.</returns>
    public static Task<RouteHistoryEndpoint?> NoEndpoint() => Task.FromResult<RouteHistoryEndpoint?>(null);

    /// <summary>
    /// The route history endpoint of the main window's server: its URL and a valid access token for it; null when it has
    /// not connected or has no token.
    /// </summary>
    /// <param name="room">The main window's view model.</param>
    /// <returns>The endpoint, or null.</returns>
    public static async Task<RouteHistoryEndpoint?> RoomEndpointAsync(MainViewModel room)
    {
        ArgumentNullException.ThrowIfNull(room);
        string url = room.ConnectedServerUrl;
        if ((string.IsNullOrWhiteSpace(url)) || (!Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out Uri? server)))
        {
            return null;
        }

        string? token = await room.Auth.GetValidAccessTokenAsync(url).ConfigureAwait(false);
        return string.IsNullOrEmpty(token) ? null : new RouteHistoryEndpoint(server, token);
    }

    /// <summary>The sources a search runs: <see cref="OtherSources"/>, then the route history.</summary>
    /// <returns>The sources, in source order.</returns>
    public IReadOnlyList<IRouteSource> Sources() => [.. OtherSources(), new ResolvedHistorySource(HistoryHttp, HistoryEndpoint)];

    // The route history at the endpoint resolved when its search starts.
    private sealed class ResolvedHistorySource(HttpClient http, Func<Task<RouteHistoryEndpoint?>> endpoint) : IRouteSource
    {
        public string Name => RouteHistorySource.SourceName;

        public async Task<RouteSourceResult> FindAsync(RouteQuery query, CancellationToken ct)
        {
            RouteHistoryEndpoint? resolved = await endpoint().WaitAsync(ct).ConfigureAwait(false);
            return await new RouteHistorySource(http, () => resolved).FindAsync(query, ct).ConfigureAwait(false);
        }
    }
}
