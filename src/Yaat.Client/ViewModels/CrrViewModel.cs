using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Views.Scope.Dock;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.ViewModels;

/// <summary>
/// ERAM's Continuous Range Readout view: each CRR group's label in its colour and its members with their distance to the
/// group's point (<see cref="CrrRows"/>), rebuilt on every applied burst of aircraft updates, on an aircraft added or removed
/// and on a change of the room's groups while the view is open. Read-only: its rows take no clicks.
/// </summary>
public sealed partial class CrrViewModel(ScopeViewModel scope) : ScopeDockViewModel(scope, ScopeToolbarButtons.ViewCrrName, ViewTitle)
{
    /// <summary>The view's title, as CRC's.</summary>
    public const string ViewTitle = "CRR";

    [ObservableProperty]
    private IReadOnlyList<CrrRow> _rows = [];

    internal override void StartReading()
    {
        Scope.Main.AircraftModelUpdated += Rebuild;
        Scope.Main.Aircraft.CollectionChanged += OnAircraftChanged;
        Scope.Main.PropertyChanged += OnMainPropertyChanged;
        Rebuild();
    }

    internal override void StopReading()
    {
        Scope.Main.AircraftModelUpdated -= Rebuild;
        Scope.Main.Aircraft.CollectionChanged -= OnAircraftChanged;
        Scope.Main.PropertyChanged -= OnMainPropertyChanged;
        Rows = [];
    }

    private void OnAircraftChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CrrGroups))
        {
            Rebuild();
        }
    }

    private void Rebuild() => Rows = CrrRows.Build(Scope.Main.CrrGroups, Scope.Main.Aircraft);
}
