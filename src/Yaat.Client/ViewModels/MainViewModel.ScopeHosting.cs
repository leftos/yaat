using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Logging;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Where the YAAT Scope lives: the primary Scope, built with this view model and alive until the app exits, shows as the
/// main window's Scope tab or, popped out, in its own window; the Scope a typed verb acts on is the one whose canvas last
/// took the focus.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The window-geometry and pop-out key of the primary Scope's window.</summary>
    public const string ScopeViewKey = "ScopeView";

    private ScopeViewModel? _activeScope;

    /// <summary>
    /// Where the primary Scope's pref-set and profile stores come from: the stores on disk, by default. A headless test sets
    /// one that answers none, so the Scope runs without files.
    /// </summary>
    public static Func<(ScopePrefSetStore? PrefStore, ScopeProfileStore? ProfileStore)> ScopeStoreFactory { get; set; } = DiskScopeStores;

    /// <summary>
    /// The primary Scope: the main window's Scope tab, or its pop-out window. Built with this view model, it lives on while
    /// docked, popped out or hidden, and is disposed, saving its state, only when the app exits.
    /// </summary>
    public ScopeViewModel PrimaryScope { get; }

    /// <summary>Whether the primary Scope shows in its own window rather than as the main window's tab.</summary>
    [ObservableProperty]
    private bool _isScopePoppedOut;

    /// <summary>The Scope whose canvas last took the keyboard focus, or null before any did.</summary>
    public ScopeViewModel? ActiveScope
    {
        get => _activeScope;
        set => SetProperty(ref _activeScope, value);
    }

    /// <summary>
    /// The Scope the typed verbs act on: the active Scope, else the primary. Never null; setting null (a test's closed Scope)
    /// hands the verbs back to the primary.
    /// </summary>
    [AllowNull]
    public ScopeViewModel OpenScope
    {
        get => ActiveScope ?? PrimaryScope;
        set => ActiveScope = value;
    }

    /// <summary>Every Scope driven by this view model: the one Scope there is, <see cref="OpenScope"/>.</summary>
    public IEnumerable<ScopeViewModel> AllScopeViews => [OpenScope];

    partial void OnIsScopePoppedOutChanged(bool value)
    {
        _preferences.SetPoppedOut(ScopeViewKey, value);
        OnTabPoppedOutChanged();
    }

    private ScopeViewModel CreatePrimaryScope()
    {
        (ScopePrefSetStore? prefStore, ScopeProfileStore? profileStore) = ScopeStoreFactory();
        return new ScopeViewModel(this, prefStore, profileStore);
    }

    private static (ScopePrefSetStore?, ScopeProfileStore?) DiskScopeStores() =>
        (
            new ScopePrefSetStore(ScopePrefSetStore.DefaultRootDir, AppLog.CreateLogger<ScopePrefSetStore>()),
            new ScopeProfileStore(ScopeProfileStore.DefaultRootDir, AppLog.CreateLogger<ScopeProfileStore>())
        );
}
