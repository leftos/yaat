using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// The editor's satellites: the spawn planner over the open copy, refreshed after each edit made in the editor, its
/// applied plans written as one undo step the Aircraft tab shows, and closed with the copy.
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
