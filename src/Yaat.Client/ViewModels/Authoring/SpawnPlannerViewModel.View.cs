using Yaat.Sim.Authoring.Spawns;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>The spawn planner's members a window binds that the planner's own inputs do not offer as they are.</summary>
public sealed partial class SpawnPlannerViewModel
{
    /// <summary>Gets the start kind filter's choices, in <see cref="KindIndex"/> order: any, then each <see cref="SpawnKind"/>.</summary>
    public static IReadOnlyList<string> KindChoices { get; } = ["Any", "Departure", "Arrival", "Overflight"];

    /// <summary>Gets or sets the start kind filter as an index into <see cref="KindChoices"/>; zero, or below, takes any kind.</summary>
    public int KindIndex
    {
        get => (Kind is { } kind) ? (int)kind + 1 : 0;
        set => Kind = ((value <= 0) || (value >= KindChoices.Count)) ? null : (SpawnKind)(value - 1);
    }

    partial void OnKindChanged(SpawnKind? value) => OnPropertyChanged(nameof(KindIndex));
}
