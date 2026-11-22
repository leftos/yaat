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
    Func<string, string, string?, Task> LoadInRoom
)
{
    /// <summary>
    /// The app's services: the app-data store, the given ARTCC access, data-api, the system clock and the dispatcher; Test
    /// in Room is off (it can never load) until a room loader is given.
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
            (_, _, _) => Task.CompletedTask
        );
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
