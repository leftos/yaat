using Avalonia.Controls;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The YAAT Scope as one control: its toolbar, its view and its views dock, over the <see cref="ViewModels.ScopeViewModel"/>
/// in its DataContext. The main window's Scope tab and the Scope window each build their own host over the same view model,
/// never both at once, since a control has one parent.
/// </summary>
public partial class ScopeHost : UserControl
{
    public ScopeHost()
    {
        InitializeComponent();
    }

    /// <summary>The Scope's view: its canvas and its key handling.</summary>
    internal ScopeView ScopeView => View;

    /// <summary>The Scope's toolbar.</summary>
    internal Toolbar.ScopeToolbar ScopeToolbar => Toolbar;

    /// <summary>The Scope's views dock.</summary>
    internal Dock.ScopeDock ScopeDock => Dock;
}
