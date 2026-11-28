using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// The editor's satellites: the spawn planner over the open copy, refreshed after each edit made in the editor, its
/// applied plans written as one undo step the Aircraft tab shows, and closed with the copy; and what the route composer
/// opens over, the open copy's ARTCC and its video maps; and Apply Route for the selected aircraft, with the Find Route it
/// runs in its own window.
/// </summary>
public sealed partial class ScenarioEditorViewModel
{
    private SpawnPlannerViewModel? _spawnPlanner;

    /// <summary>Raised when the open copy closes or changes, so the window closes the satellite windows over it.</summary>
    public event EventHandler? SatellitesClosing;

    /// <summary>
    /// Creates a spawn planner over the open copy and keeps it as the current planner, which each edit in the editor
    /// refreshes; a plan it applies is one undo step, and the Aircraft tab reloads to show it.
    /// </summary>
    /// <returns>The planner; null when no copy is open.</returns>
    public SpawnPlannerViewModel? CreateSpawnPlanner()
    {
        if (Session is not { } session)
        {
            return null;
        }

        var planner = new SpawnPlannerViewModel(session, (description, change) => ApplyToSession(session, description, change));
        _spawnPlanner = planner;
        return planner;
    }

    /// <summary>Stops refreshing a planner whose window closed; a planner that is no longer the current one is ignored.</summary>
    /// <param name="planner">The planner whose window closed.</param>
    public void ReleaseSpawnPlanner(SpawnPlannerViewModel planner)
    {
        if (ReferenceEquals(_spawnPlanner, planner))
        {
            _spawnPlanner = null;
        }
    }

    /// <summary>
    /// Creates what the route composer opens over: the open copy's ARTCC and that ARTCC's video maps, loaded with no room.
    /// </summary>
    /// <returns>The context; null when no copy is open.</returns>
    public RouteComposerContext? CreateRouteComposerContext()
    {
        if (Session is not { } session)
        {
            return null;
        }

        string artccId = session.ArtccId;
        return new RouteComposerContext(
            artccId,
            FixResolutionContext.None,
            ct => ArtccVideoMapLoader.ListAsync(_services.Configs, artccId, ct),
            (mapId, ct) => ArtccVideoMapLoader.LoadOneAsync(_services.DownloadVideoMaps, artccId, mapId, _services.Configs, ct)
        );
    }

    /// <summary>Gets the route library the route composer saves to and Apply Route lists: the services' one.</summary>
    public RouteLibrary Routes => _services.Routes;

    /// <summary>
    /// Creates Apply Route for the Aircraft tab's selected aircraft over the services' stamp profiles and route library, the
    /// navdata as loaded, the shared random source and the system clock; its Apply is one undo step the Aircraft tab shows.
    /// </summary>
    /// <returns>The view model, which the caller disposes; null with no copy open or no aircraft selected.</returns>
    public ApplyRouteViewModel? CreateApplyRoute()
    {
        if ((Session is not { } session) || (Aircraft is not { Selected: not null } aircraft))
        {
            return null;
        }

        var services = new ApplyRouteServices(
            _services.StampProfiles,
            _services.Routes,
            () => NavigationDatabase.InstanceOrNull,
            Random.Shared,
            TimeProvider.System
        );
        return new ApplyRouteViewModel(aircraft, services, session.ArtccId);
    }

    /// <summary>
    /// Opens Find Route inside Apply Route for the selected aircraft over the services' route sources and starts its search;
    /// its Apply hands the found route to Apply Route, which writes nothing until its own Apply.
    /// </summary>
    /// <param name="applyRoute">The Apply Route the found route goes to.</param>
    /// <returns>The open Find Route, which the caller disposes; null with no copy open or no aircraft selected.</returns>
    public RouteFinderViewModel? CreateApplyRouteFinder(ApplyRouteViewModel applyRoute)
    {
        ArgumentNullException.ThrowIfNull(applyRoute);
        if ((Session is not { } session) || (Aircraft?.SelectedForStamp() is not { } target))
        {
            return null;
        }

        var finderTarget = new RouteFinderTarget(
            target.Aircraft,
            prefix => RouteFinderViewModel.FreeCallsign(session.Document.Scenario, prefix, _services.Random),
            _services.Clock.GetUtcNow().UtcDateTime
        );
        var finder = new RouteFinderViewModel(
            finderTarget,
            _services.RouteFinding.Sources(),
            _services.Post,
            choice => applyRoute.UseFoundRoute(choice.Route)
        );
        finder.StartSearch();
        return finder;
    }

    // A satellite's edit on the copy it was opened over; one on a copy no longer open writes nothing.
    private void ApplyToSession(ScenarioEditSession session, string description, Action<Scenario> change)
    {
        if (!ReferenceEquals(session, Session))
        {
            return;
        }

        if (session.Edit(description, change))
        {
            Aircraft?.Reload();
            OnEdited(this, EventArgs.Empty);
        }
    }

    private void RefreshSatellites() => _spawnPlanner?.Refresh();

    private void CloseSatellites() => SatellitesClosing?.Invoke(this, EventArgs.Empty);
}
