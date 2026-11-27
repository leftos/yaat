using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Authoring;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>The merge window's whole-scenario choices.</summary>
public sealed partial class ScenarioMergeViewModel
{
    [RelayCommand]
    private void KeepMineForEveryClash() => KeepForEveryClash(MergeChoiceKind.KeepMine);

    [RelayCommand]
    private void KeepTheirsForEveryClash() => KeepForEveryClash(MergeChoiceKind.KeepTheirs);

    [RelayCommand]
    private void TakeMineForEverything() => TakeForEverything(MergeChoice.KeepMine);

    [RelayCommand]
    private void TakeTheirsForEverything() => TakeForEverything(MergeChoice.KeepTheirs);

    // The engine's clash-only choices replace each clash's choice; an auto row keeps its own side or its choice.
    private void KeepForEveryClash(MergeChoiceKind side)
    {
        foreach ((string path, MergeChoice choice) in Plan.ChooseAll(side))
        {
            _choices[path] = choice;
        }

        Refresh();
    }

    // Every row, auto and order rows included, takes the side; every earlier choice is dropped.
    private void TakeForEverything(MergeChoice choice)
    {
        _choices.Clear();
        foreach (ScenarioMergeRowViewModel row in Rows)
        {
            _choices[row.Path] = choice;
        }

        Refresh();
    }
}
