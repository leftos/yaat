using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.ContextMenus;

namespace Yaat.Client.ViewModels;

public sealed partial class ScopeViewModel
{
    /// <summary>The map cue a pointed or focused menu row shows (<see cref="IMenuHost.SetMapCue"/>); null for none.</summary>
    [ObservableProperty]
    private MapCue? _menuCue;
}
