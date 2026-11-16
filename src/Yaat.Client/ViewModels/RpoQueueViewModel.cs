using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Views.Scope.Dock;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The pilot lens's RPO queue view: the check-in exceptions and the queued pilot commands waiting on their triggers
/// (<see cref="RpoQueueRows"/>), rebuilt on every applied burst of aircraft updates, on an aircraft added or removed and on a
/// viewer change while the view is open. Its title counts the rows. A row's click selects its aircraft; nothing is sent.
/// </summary>
public sealed partial class RpoQueueViewModel(ScopeViewModel scope) : ScopeDockViewModel(scope, ScopeToolbarButtons.ViewRpoQueueName, TitleFor(0))
{
    [ObservableProperty]
    private IReadOnlyList<RpoQueueRow> _rows = [];

    internal override void StartReading()
    {
        Scope.Main.AircraftModelUpdated += Rebuild;
        Scope.Main.Aircraft.CollectionChanged += OnAircraftChanged;
        Scope.PropertyChanged += OnScopePropertyChanged;
        Rebuild();
    }

    internal override void StopReading()
    {
        Scope.Main.AircraftModelUpdated -= Rebuild;
        Scope.Main.Aircraft.CollectionChanged -= OnAircraftChanged;
        Scope.PropertyChanged -= OnScopePropertyChanged;
        Rows = [];
    }

    partial void OnRowsChanged(IReadOnlyList<RpoQueueRow> value) => Title = TitleFor(value.Count);

    private static string TitleFor(int count) => $"PILOT LENS · RPO QUEUE {count}";

    private void OnAircraftChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnScopePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScopeViewModel.Viewer))
        {
            Rebuild();
        }
    }

    private void Rebuild() => Rows = RpoQueueRows.Build(Scope.Main.Aircraft, Scope.Viewer);

    /// <summary>Selects the aircraft a clicked row names, as a left click on its track does.</summary>
    [RelayCommand]
    private void Select(string callsign) => Scope.SelectAircraft(callsign);
}
