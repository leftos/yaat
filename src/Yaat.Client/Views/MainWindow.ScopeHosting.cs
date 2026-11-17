using Avalonia.Controls;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.Views;

/// <summary>
/// The primary Scope's two homes: the main window's Scope tab, and its pop-out window. Popping out hides the tab and opens
/// the window over the same view model; closing the window docks it back into the tab.
/// </summary>
public partial class MainWindow
{
    /// <summary>The main window's Scope tab header.</summary>
    public const string ScopeTabHeader = "Scope";

    /// <summary>The View › Windows check item that pops the Scope out and docks it back.</summary>
    public const string ScopePopOutMenuHeader = "Sco_pe";

    private ScopeWindow? _scopeWindow;

    /// <summary>The primary Scope's pop-out window, while it is popped out. Test seam.</summary>
    internal ScopeWindow? ScopeWindow => _scopeWindow;

    private void HandleScopePopOut(MainViewModel vm)
    {
        if (vm.IsScopePoppedOut)
        {
            OpenScopeWindow(vm);
        }
        else
        {
            CloseScopeWindow();
        }
    }

    private void OpenScopeWindow(MainViewModel vm)
    {
        if (_scopeWindow is not null)
        {
            return;
        }

        // The window shows the primary Scope but does not own it: the view model outlives the window and docks back.
        _scopeWindow = new ScopeWindow(vm.Preferences, vm.PrimaryScope, ownsViewModel: false);
        _scopeWindow.Closing += OnScopeWindowClosing;
        _scopeWindow.Show();
    }

    private void CloseScopeWindow()
    {
        if (_scopeWindow is { } window)
        {
            _scopeWindow = null;
            window.Closing -= OnScopeWindowClosing;
            SavePrimaryScope();
            window.Close();
        }
    }

    // As the other pop-outs: clear the field before flipping the flag, whose change re-enters CloseScopeWindow. A close
    // at app shutdown keeps the flag, and leaves the save to DisposePrimaryScopeAtExit.
    private void OnScopeWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        _scopeWindow = null;
        if (IsClosingFromShutdown(_isMainWindowClosing))
        {
            return;
        }

        SavePrimaryScope();
        if (DataContext is MainViewModel vm)
        {
            vm.IsScopePoppedOut = false;
        }
    }

    private void SavePrimaryScope() => (DataContext as MainViewModel)?.PrimaryScope.SaveState();

    // The primary Scope lives as long as the app: its state saves here, at exit, as its view model is released.
    private void DisposePrimaryScopeAtExit() => (DataContext as MainViewModel)?.PrimaryScope.Dispose();
}
