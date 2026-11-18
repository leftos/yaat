using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Where the YAAT Scope lives: the primary Scope, built with this view model and alive until the app exits, shows as the
/// main window's Scope tab or, popped out, in its own window; extra Scope windows (#2, #3, …) open beside it, each with its
/// own state; the Scope a typed verb acts on is the one whose canvas last took the focus.
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

    /// <summary>
    /// The extra Scope windows (#2, #3, …) open now. The primary Scope is the implicit #1 and has no entry here. Each
    /// instance owns its own <see cref="ScopeViewModel"/>, with its own state; the traffic and the selection stay app-wide.
    /// </summary>
    public ObservableCollection<ScopeViewInstance> ExtraScopeViews { get; } = [];

    /// <summary>
    /// Every Scope driven by this view model: the primary first, then the extra windows. A stand-alone Scope set as the
    /// active one (a test's Scope, which no window hosts) stands in for the primary.
    /// </summary>
    public IEnumerable<ScopeViewModel> AllScopeViews
    {
        get
        {
            // The primary is null only while the constructor builds the views it comes after.
            ScopeViewModel? first = ((ActiveScope is { } active) && !IsHostedScope(active)) ? active : PrimaryScope;
            if (first is not null)
            {
                yield return first;
            }

            foreach (ScopeViewInstance instance in ExtraScopeViews)
            {
                yield return instance.Vm;
            }
        }
    }

    /// <summary>Opens another Scope window on the lowest free number, a view of the same facility with its own state.</summary>
    public ScopeViewInstance OpenExtraScopeView() => OpenScopeInstance(ViewInstanceOrdinals.NextFree(ExtraScopeViews.Select(i => i.Ordinal)));

    /// <summary>
    /// Drops an extra Scope window and disposes its view model, which saves its state in its slot. The typed verbs go back
    /// to the primary when it was the active Scope. A no-op when it was already removed.
    /// </summary>
    public void CloseExtraScopeView(ScopeViewInstance instance)
    {
        if (!ExtraScopeViews.Remove(instance))
        {
            return;
        }

        if (ReferenceEquals(ActiveScope, instance.Vm))
        {
            ActiveScope = null;
        }

        // The view model keeps no disposed Scope: the measuring surface it held falls back to the Radar.
        if (ReferenceEquals(_activeMeasureScope, instance.Vm))
        {
            SetActiveMeasureView(RblView.Radar);
        }

        instance.Vm.Dispose();
    }

    /// <summary>
    /// Opens and closes extra Scope windows to match a layout's list: an open Scope the list does not name is closed (and
    /// saves its state), a listed number not open opens with its own saved state, and an open listed Scope is left as it
    /// is. Entries match by number only; numbers below 2 are skipped, the primary Scope being #1.
    /// </summary>
    public void ReconcileExtraScopeViews(IReadOnlyList<SavedExtraView> entries)
    {
        HashSet<int> wanted = [.. entries.Select(e => e.Ordinal).Where(o => o > ScopePrefSetStore.PrimarySlot)];
        foreach (ScopeViewInstance instance in ExtraScopeViews.Where(i => !wanted.Contains(i.Ordinal)).ToList())
        {
            CloseExtraScopeView(instance);
        }

        HashSet<int> open = [.. ExtraScopeViews.Select(i => i.Ordinal)];
        foreach (int ordinal in wanted.Where(o => !open.Contains(o)).Order())
        {
            OpenScopeInstance(ordinal);
        }
    }

    /// <summary>Disposes every Scope, saving each one's state, as the app exits.</summary>
    public void DisposeScopes()
    {
        foreach (ScopeViewModel scope in AllScopeViews.ToList())
        {
            scope.Dispose();
        }
    }

    private bool IsHostedScope(ScopeViewModel scope) =>
        ReferenceEquals(scope, PrimaryScope) || ExtraScopeViews.Any(instance => ReferenceEquals(instance.Vm, scope));

    partial void OnIsScopePoppedOutChanged(bool value)
    {
        _preferences.SetPoppedOut(ScopeViewKey, value);
        OnTabPoppedOutChanged();
    }

    // An extra Scope's view model reads its own slot's saved state as it is built.
    private ScopeViewInstance OpenScopeInstance(int ordinal)
    {
        (ScopePrefSetStore? prefStore, ScopeProfileStore? profileStore) = ScopeStoreFactory();
        var instance = new ScopeViewInstance { Ordinal = ordinal, Vm = new ScopeViewModel(this, prefStore, profileStore, ordinal) };
        ExtraScopeViews.Add(instance);
        return instance;
    }

    private ScopeViewModel CreatePrimaryScope()
    {
        (ScopePrefSetStore? prefStore, ScopeProfileStore? profileStore) = ScopeStoreFactory();
        return new ScopeViewModel(this, prefStore, profileStore, ScopePrefSetStore.PrimarySlot);
    }

    private static (ScopePrefSetStore?, ScopeProfileStore?) DiskScopeStores() =>
        (
            new ScopePrefSetStore(ScopePrefSetStore.DefaultRootDir, AppLog.CreateLogger<ScopePrefSetStore>()),
            new ScopeProfileStore(ScopeProfileStore.DefaultRootDir, AppLog.CreateLogger<ScopeProfileStore>())
        );
}
