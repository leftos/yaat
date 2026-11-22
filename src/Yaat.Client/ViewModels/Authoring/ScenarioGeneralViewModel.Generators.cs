using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

public sealed partial class ScenarioGeneralViewModel
{
    /// <summary>Gets the scenario's arrival generators (the vNAS kind), one row per entry, in the document's order.</summary>
    public ObservableCollection<ScenarioArrivalGeneratorRowViewModel> ArrivalGenerators { get; } = [];

    /// <summary>Gets the scenario's VFR arrival generators (a YAAT-only kind), one row per entry, in the document's order.</summary>
    public ObservableCollection<ScenarioVfrArrivalGeneratorRowViewModel> VfrArrivalGenerators { get; } = [];

    /// <summary>Gets the scenario's overflight generators (a YAAT-only kind), one row per entry, in the document's order.</summary>
    public ObservableCollection<ScenarioOverflightGeneratorRowViewModel> OverflightGenerators { get; } = [];

    [RelayCommand]
    private void AddArrivalGenerator()
    {
        var generator = new ScenarioGeneratorConfig { Id = NewItemId() };
        ApplyList("Add arrival generator", s => (s.AircraftGenerators ??= []).Add(generator));
    }

    [RelayCommand]
    private void RemoveArrivalGenerator(ScenarioArrivalGeneratorRowViewModel? row) =>
        ForCurrentRow(
            "Remove arrival generator",
            row?.IsCurrent == true,
            row?.Index ?? -1,
            i => ApplyList("Remove arrival generator", s => s.AircraftGenerators.RemoveAt(i))
        );

    [RelayCommand]
    private void MoveArrivalGeneratorUp(ScenarioArrivalGeneratorRowViewModel? row) =>
        ForCurrentRow(
            "Move arrival generator",
            row?.IsCurrent == true,
            row?.Index ?? -1,
            i => Move("Move arrival generator", s => s.AircraftGenerators, i, -1)
        );

    [RelayCommand]
    private void MoveArrivalGeneratorDown(ScenarioArrivalGeneratorRowViewModel? row) =>
        ForCurrentRow(
            "Move arrival generator",
            row?.IsCurrent == true,
            row?.Index ?? -1,
            i => Move("Move arrival generator", s => s.AircraftGenerators, i, 1)
        );

    [RelayCommand]
    private void AddVfrArrivalGenerator()
    {
        var generator = new VfrArrivalGeneratorConfig { Id = NewItemId() };
        ApplyList("Add VFR arrival generator", s => (s.VfrArrivalGenerators ??= []).Add(generator));
    }

    [RelayCommand]
    private void RemoveVfrArrivalGenerator(ScenarioVfrArrivalGeneratorRowViewModel? row) =>
        ForCurrentRow(
            "Remove VFR arrival generator",
            row?.IsCurrent == true,
            row?.Index ?? -1,
            i => ApplyList("Remove VFR arrival generator", s => s.VfrArrivalGenerators.RemoveAt(i))
        );

    [RelayCommand]
    private void MoveVfrArrivalGeneratorUp(ScenarioVfrArrivalGeneratorRowViewModel? row) =>
        ForCurrentRow(
            "Move VFR arrival generator",
            row?.IsCurrent == true,
            row?.Index ?? -1,
            i => Move("Move VFR arrival generator", s => s.VfrArrivalGenerators, i, -1)
        );

    [RelayCommand]
    private void MoveVfrArrivalGeneratorDown(ScenarioVfrArrivalGeneratorRowViewModel? row) =>
        ForCurrentRow(
            "Move VFR arrival generator",
            row?.IsCurrent == true,
            row?.Index ?? -1,
            i => Move("Move VFR arrival generator", s => s.VfrArrivalGenerators, i, 1)
        );

    [RelayCommand]
    private void AddOverflightGenerator()
    {
        var generator = new OverflightGeneratorConfig { Id = NewItemId() };
        ApplyList("Add overflight generator", s => (s.OverflightGenerators ??= []).Add(generator));
    }

    [RelayCommand]
    private void RemoveOverflightGenerator(ScenarioOverflightGeneratorRowViewModel? row) =>
        ForCurrentRow(
            "Remove overflight generator",
            row?.IsCurrent == true,
            row?.Index ?? -1,
            i => ApplyList("Remove overflight generator", s => s.OverflightGenerators.RemoveAt(i))
        );

    [RelayCommand]
    private void MoveOverflightGeneratorUp(ScenarioOverflightGeneratorRowViewModel? row) =>
        ForCurrentRow(
            "Move overflight generator",
            row?.IsCurrent == true,
            row?.Index ?? -1,
            i => Move("Move overflight generator", s => s.OverflightGenerators, i, -1)
        );

    [RelayCommand]
    private void MoveOverflightGeneratorDown(ScenarioOverflightGeneratorRowViewModel? row) =>
        ForCurrentRow(
            "Move overflight generator",
            row?.IsCurrent == true,
            row?.Index ?? -1,
            i => Move("Move overflight generator", s => s.OverflightGenerators, i, 1)
        );

    // One row per generator of each kind, wrapping the item at its index in the document as it is now; a list the
    // document holds as null has no rows (and stays null until an add).
    private void RebuildGeneratorRows()
    {
        Scenario scenario = _session.Document.Scenario;
        RebuildList(ArrivalGenerators, (scenario.AircraftGenerators ?? []).Count, i => new ScenarioArrivalGeneratorRowViewModel(_session, i, Apply));
        RebuildList(
            VfrArrivalGenerators,
            (scenario.VfrArrivalGenerators ?? []).Count,
            i => new ScenarioVfrArrivalGeneratorRowViewModel(_session, i, Apply)
        );
        RebuildList(
            OverflightGenerators,
            (scenario.OverflightGenerators ?? []).Count,
            i => new ScenarioOverflightGeneratorRowViewModel(_session, i, Apply)
        );
    }

    private void RebuildGeneratorRowOptions()
    {
        foreach (ScenarioArrivalGeneratorRowViewModel row in ArrivalGenerators)
        {
            row.UpdateOptions(_catalogOptions);
        }

        foreach (ScenarioVfrArrivalGeneratorRowViewModel row in VfrArrivalGenerators)
        {
            row.UpdateOptions(_catalogOptions);
        }
    }

    private static void RebuildList<TRow>(ObservableCollection<TRow> rows, int count, Func<int, TRow> create)
    {
        rows.Clear();
        for (int i = 0; i < count; i++)
        {
            rows.Add(create(i));
        }
    }
}
