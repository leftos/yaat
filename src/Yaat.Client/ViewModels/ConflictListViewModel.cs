using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Views.Scope.Dock;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's LA/CA/MCI view: one row per conflict-alert pair the client holds (<see cref="ConflictListRows"/>), rebuilt
/// on every applied broadcast while the view is open. It lists the pairs whatever the conflict-alert preference says.
/// </summary>
public sealed partial class ConflictListViewModel(ScopeViewModel scope) : ScopeDockViewModel(scope, ScopeToolbarButtons.ViewCaName, "LA/CA/MCI")
{
    [ObservableProperty]
    private IReadOnlyList<ConflictListRow> _rows = [];

    internal override void StartReading()
    {
        Scope.Main.ConflictAlertsApplied += Rebuild;
        Rebuild();
    }

    internal override void StopReading()
    {
        Scope.Main.ConflictAlertsApplied -= Rebuild;
        Rows = [];
    }

    private void Rebuild() =>
        Rows = ConflictListRows.Rows(Scope.Main.ConflictAlerts, callsign => Scope.Main.Aircraft.FirstOrDefault(ac => ac.Callsign == callsign));
}
