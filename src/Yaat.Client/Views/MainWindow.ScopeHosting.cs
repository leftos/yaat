using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.Views;

/// <summary>
/// The primary Scope's two homes: the main window's Scope tab, and its pop-out window. Popping out hides the tab and opens
/// the window over the same view model; closing the window docks it back into the tab. Each extra Scope has a window of
/// its own, closed for good with its Scope.
/// </summary>
public partial class MainWindow
{
    /// <summary>The main window's Scope tab header.</summary>
    public const string ScopeTabHeader = "Radar";

    /// <summary>The main window's Scope tab, the third: Aircraft List, Ground View, Scope.</summary>
    public const int ScopeTabIndex = 2;

    /// <summary>The View › Windows check item that pops the Scope out and docks it back.</summary>
    public const string ScopePopOutMenuHeader = "_Radar";

    /// <summary>The View › Windows item that opens another Scope window.</summary>
    public const string NewScopeWindowMenuHeader = "New Radar _window…";

    private ScopeWindow? _scopeWindow;

    // One window per extra Scope (#2, #3, …), keyed by its instance, and each window's Closing handler, kept so a close the
    // view model orders can unsubscribe it first.
    private readonly Dictionary<ScopeViewInstance, ScopeWindow> _extraScopeWindows = [];
    private readonly Dictionary<ScopeViewInstance, EventHandler<WindowClosingEventArgs>> _extraScopeClosingHandlers = [];

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

    // The primary Scope lives as long as the app, and an extra window left open at exit keeps its Scope: each one's state
    // saves here, at exit, as its view model is released.
    private void DisposeScopesAtExit() => (DataContext as MainViewModel)?.DisposeScopes();

    /// <summary>The extra Scope windows open now, by instance. Test seam.</summary>
    internal IReadOnlyDictionary<ScopeViewInstance, ScopeWindow> ExtraScopeWindows => _extraScopeWindows;

    // Opens a window for every extra Scope the view model already holds and keeps the window set in step with the
    // instance collection from then on.
    private void WireScopeInstanceWindows(MainViewModel vm)
    {
        foreach (ScopeViewInstance instance in vm.ExtraScopeViews)
        {
            OpenExtraScopeWindow(vm, instance);
        }

        vm.ExtraScopeViews.CollectionChanged += (_, e) => OnExtraScopeViewsChanged(vm, e);
    }

    private void OnNewScopeWindowClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.OpenExtraScopeView();
        }
    }

    private void OnExtraScopeViewsChanged(MainViewModel vm, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (ScopeViewInstance instance in _extraScopeWindows.Keys.ToList())
            {
                CloseExtraScopeWindow(instance);
            }

            return;
        }

        foreach (ScopeViewInstance removed in e.OldItems?.OfType<ScopeViewInstance>() ?? [])
        {
            CloseExtraScopeWindow(removed);
        }

        foreach (ScopeViewInstance added in e.NewItems?.OfType<ScopeViewInstance>() ?? [])
        {
            OpenExtraScopeWindow(vm, added);
        }
    }

    private void OpenExtraScopeWindow(MainViewModel vm, ScopeViewInstance instance)
    {
        if (_extraScopeWindows.ContainsKey(instance))
        {
            return;
        }

        var window = new ScopeWindow(vm.Preferences, instance);
        void onClosing(object? _1, WindowClosingEventArgs _2)
        {
            // Drop the bookkeeping before telling the view model: CloseExtraScopeView removes the instance, and that change
            // re-enters CloseExtraScopeWindow, which must miss the window already inside its own Closing. A close at app
            // shutdown keeps the instance, and leaves its save to DisposeScopesAtExit.
            _extraScopeWindows.Remove(instance);
            _extraScopeClosingHandlers.Remove(instance);
            if (!IsClosingFromShutdown(_isMainWindowClosing))
            {
                vm.CloseExtraScopeView(instance);
            }
        }

        _extraScopeWindows[instance] = window;
        _extraScopeClosingHandlers[instance] = onClosing;
        window.Closing += onClosing;
        window.Show();
    }

    private void CloseExtraScopeWindow(ScopeViewInstance instance)
    {
        if (!_extraScopeWindows.Remove(instance, out ScopeWindow? window))
        {
            return;
        }

        if (_extraScopeClosingHandlers.Remove(instance, out EventHandler<WindowClosingEventArgs>? handler))
        {
            window.Closing -= handler;
        }

        window.Close();
    }
}
