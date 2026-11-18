using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Scope.Dock;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's LA/CA/MCI view: one row per conflict-alert pair the client holds (<see cref="ConflictListRows"/>), rebuilt
/// on every applied broadcast, on every applied burst of aircraft updates and on an aircraft added or removed while the view
/// is open, so a member that becomes tracked or files a plan between broadcasts changes its row. The rows are replaced only
/// when their text or dimming changes. It lists the pairs whatever the conflict-alert preference says.
/// </summary>
public sealed partial class ConflictListViewModel(ScopeViewModel scope) : ScopeDockViewModel(scope, ScopeToolbarButtons.ViewCaName, "LA/CA/MCI")
{
    [ObservableProperty]
    private IReadOnlyList<ConflictListRow> _rows = [];

    internal override void StartReading()
    {
        Scope.Main.ConflictAlertsApplied += Rebuild;
        Scope.Main.AircraftModelUpdated += Rebuild;
        Scope.Main.Aircraft.CollectionChanged += OnAircraftChanged;
        Rebuild();
    }

    internal override void StopReading()
    {
        Scope.Main.ConflictAlertsApplied -= Rebuild;
        Scope.Main.AircraftModelUpdated -= Rebuild;
        Scope.Main.Aircraft.CollectionChanged -= OnAircraftChanged;
        Rows = [];
    }

    private void OnAircraftChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        IReadOnlyList<ConflictListRow> rows = BuildRows(Scope.Main.ConflictAlerts);
        if (!rows.SequenceEqual(Rows))
        {
            Rows = rows;
        }
    }

    private IReadOnlyList<ConflictListRow> BuildRows(IReadOnlyList<ConflictAlertDto> pairs)
    {
        if (pairs.Count == 0)
        {
            return [];
        }

        var byCallsign = new Dictionary<string, AircraftModel>(StringComparer.Ordinal);
        foreach (AircraftModel ac in Scope.Main.Aircraft)
        {
            byCallsign.TryAdd(ac.Callsign, ac);
        }

        return ConflictListRows.Rows(pairs, callsign => byCallsign.GetValueOrDefault(callsign));
    }
}
