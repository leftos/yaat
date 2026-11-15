using Avalonia.Controls;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope.Dock;

/// <summary>
/// The Scope window's views dock: the docked views' cards (<see cref="ScopeViewModel.DockViews"/>, each a
/// <see cref="ScopeViewCard"/>) stacked top to bottom in dock order.
/// </summary>
public partial class ScopeDock : UserControl
{
    public ScopeDock()
    {
        InitializeComponent();
    }
}
