using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The RUNDOWN list's place on the Radar view: display state the user moves by dragging the list, kept in pref sets and the
/// saved state.
/// </summary>
public sealed partial class ScopeViewModel
{
    /// <summary>
    /// Where the RUNDOWN list's title sits until the user moves it, in canvas points: top-left, below the entry preview.
    /// </summary>
    internal static readonly Point DefaultRundownOrigin = new(ScopeRundownList.DefaultOrigin.X, ScopeRundownList.DefaultOrigin.Y);

    /// <summary>
    /// The top-left of the RUNDOWN list's title line in canvas points; the canvas's drag writes it through its two-way binding.
    /// </summary>
    [ObservableProperty]
    private Point _rundownOrigin = DefaultRundownOrigin;
}
