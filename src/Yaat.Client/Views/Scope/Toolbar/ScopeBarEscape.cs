using Avalonia;
using Avalonia.VisualTree;

namespace Yaat.Client.Views.Scope.Toolbar;

/// <summary>Whether an Esc belongs to a Scope bar's open submenu or menu rather than to the rest of the window.</summary>
internal static class ScopeBarEscape
{
    /// <summary>
    /// Whether an Esc is <paramref name="bar"/>'s: the keyboard focus is on the Scope holding it, by the Scope view's own
    /// test, and no field or entry popup of the Scope's is open, whose Esc closes it first whichever top-level handler runs
    /// first.
    /// </summary>
    /// <param name="bar">The bar asking, inside a <see cref="ScopeHost"/>.</param>
    public static bool IsBarsEscape(Visual bar) =>
        (bar.FindAncestorOfType<ScopeHost>()?.ScopeView is { } view)
        && view.IsKeyFocusInside()
        && (view.ActiveFieldPopup is not { IsOpen: true })
        && (view.ActiveEntryPopup is not { IsOpen: true });
}
