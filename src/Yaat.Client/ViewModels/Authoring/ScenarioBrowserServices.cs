using Avalonia.Threading;
using Yaat.Client.Services;
using Yaat.Sim.Authoring;
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
    ArtccConfigSource Configs
)
{
    // One client and one config source for the app's lifetime: every editor window shares the parsed configs.
    private static readonly HttpClient ConfigHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly ArtccConfigSource AppConfigs = new(ConfigHttp);

    /// <summary>
    /// The app's services: the app-data store, the given ARTCC access, data-api (scenarios and ARTCC configs),
    /// the system clock and the dispatcher; Test in Room is off (it can never load) until a room is given (<see cref="WithRoom"/>).
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
            AppConfigs
        );
    }

    /// <summary>
    /// These services with Test in Room over the main window's room: it can load while
    /// <see cref="MainViewModel.CanLoadScenario"/> holds, loads through <see cref="MainViewModel.LoadTestScenarioAsync"/>, and
    /// asks first while the room runs a scenario.
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
