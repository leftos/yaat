using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Where the YAAT Scope lives: the primary Scope, built with this view model and alive until the app exits, shows as the
/// main window's Scope tab or, popped out, in its own window; extra Scope windows (#2, #3, …) open beside it, each with its
/// own state; the Scope a typed verb acts on is the one whose canvas last took the focus, while a Scope's toolbar, tear-offs
/// and cards act on their own Scope.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The window-geometry and pop-out key of the primary Scope's window.</summary>
    public const string ScopeViewKey = "ScopeView";

    private ScopeViewModel? _activeScope;

    /// <summary>The old Radar BRITE targets that have a Scope group, and the group each one feeds.</summary>
    private static readonly (string BriteTarget, string Group)[] MigratedBrightnessGroups =
    [
        ("MapA", "mapa"),
        ("MapB", "mapb"),
        ("RangeRing", "rings"),
        ("Fdb", "fdb"),
        ("Hst", "hist"),
    ];

    /// <summary>The sizes a Scope range ring takes, in NM.</summary>
    private static readonly double[] MigratedRingSizes = [2, 5, 10, 15, 20];

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

        // The view model keeps no disposed Scope: the measuring surface it held falls back to the primary Scope, which
        // ActiveMeasureScope resolves on read; the lines it placed and the anchor it holds go with it.
        if (ReferenceEquals(_activeMeasureScope, instance.Vm))
        {
            _activeMeasureScope = null;
        }

        Measure.RemoveSurface(instance.Vm);
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

    /// <summary>
    /// Copies a scenario's old per-scenario Radar settings into the primary Scope's saved state for the student position's
    /// facility, once, when that facility has none, then drops the entry from the preferences. A facility the primary Scope
    /// was already on counts as having state, so the first old scenario loaded at a facility wins.
    /// </summary>
    /// <param name="scenarioId">The scenario whose settings to migrate.</param>
    /// <param name="facilityBefore">The primary Scope's facility before the load set the student position.</param>
    private void MigrateRadarSettings(string? scenarioId, string? facilityBefore)
    {
        if ((string.IsNullOrEmpty(scenarioId)) || (PrimaryScope.PrefStore is not { } store))
        {
            return;
        }

        string? facility = PrimaryScope.Viewer.EramFacilityId;
        if ((!ScopePrefSetStore.IsSafeFacility(facility)) || (!_preferences.TryGetRadarSettings(scenarioId, out SavedRadarSettings? settings)))
        {
            return;
        }

        bool live = string.Equals(facilityBefore, facility, StringComparison.Ordinal);
        if ((live) || (store.LoadCurrent(facility, ScopePrefSetStore.PrimarySlot) is not null))
        {
            _preferences.RemoveRadarSettings(scenarioId);
            return;
        }

        ScopePrefSet migrated = BuildMigratedPrefSet(PrimaryScope.CapturePrefSet(null), settings);
        PrimaryScope.ApplyPrefSet(migrated);
        try
        {
            store.SaveCurrent(facility, ScopePrefSetStore.PrimarySlot, migrated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not migrate scenario {ScenarioId}'s old Radar settings to the {Facility} Scope state", scenarioId, facility);
            return;
        }

        _preferences.RemoveRadarSettings(scenarioId);
        _log.LogInformation("Migrated scenario {ScenarioId}'s old Radar settings into the {Facility} Scope state", scenarioId, facility);
    }

    /// <summary>
    /// <paramref name="captured"/> with the rows of the old per-scenario Radar settings <paramref name="old"/> that have a
    /// Scope equivalent, and a value inside the Scope's gates, overridden. The capture supplies what has no old row: the
    /// template, the DB, the lens and the map tools the Scope shows. The centre is copied as stored, so the Scope's
    /// <c>IsCentre</c> gate drops a zero one and the map source's seed stands; a range outside 1-256 NM takes
    /// <see cref="ScopeViewModel.DefaultRangeNm"/>, never the captured range, which may belong to the facility being left.
    /// PTL has no Scope equivalent and is dropped.
    /// </summary>
    private ScopePrefSet BuildMigratedPrefSet(ScopePrefSet captured, SavedRadarSettings old) =>
        captured with
        {
            Maps = [.. old.EnabledStarsIds],
            CenterLat = old.CenterLat,
            CenterLon = old.CenterLon,
            RangeNm = (old.RangeNm is >= 1 and <= 256) ? old.RangeNm : ScopeViewModel.DefaultRangeNm,
            MapTools = captured.MapTools with
            {
                Lock = old.IsPanZoomLocked,
                RangeRing = MigrateRangeRing(old),
                FixesAndMarkers = MigrateFixesAndMarkers(old),
            },
            Tdm = old.ShowTopDown,
            Brightness = MigrateBrightness(captured.Brightness, old.BrightnessValues),
            PinHistory = (old.HistoryCount is > 0 and <= ScopeViewModel.MaxHistory) ? old.HistoryCount : captured.PinHistory,
        };

    /// <summary>Whether the old range ring's centre is placed: not both latitude and longitude zero, the old "no centre".</summary>
    private static bool IsRingCentrePlaced(double lat, double lon) => (lat != 0) || (lon != 0);

    /// <summary>The old range ring as a Scope one, or null for the Scope's default (on, 5 NM, the facility's home centre).</summary>
    private static ScopePrefRangeRing? MigrateRangeRing(SavedRadarSettings old)
    {
        int size = (Array.IndexOf(MigratedRingSizes, old.RangeRingSizeNm) >= 0) ? (int)old.RangeRingSizeNm : 5;
        bool placed = IsRingCentrePlaced(old.RangeRingCenterLat, old.RangeRingCenterLon);
        if ((old.ShowRangeRings) && (size == 5) && (!placed))
        {
            return null;
        }

        return new ScopePrefRangeRing(old.ShowRangeRings, size, placed ? old.RangeRingCenterLat : null, placed ? old.RangeRingCenterLon : null);
    }

    /// <summary>The old FIX overlay and markers as a Scope entry, or null for the Scope's default (FIX off, no marker).</summary>
    private static ScopePrefFixesAndMarkers? MigrateFixesAndMarkers(SavedRadarSettings old) =>
        ((!old.ShowFixes) && (old.PinnedMarkers.Count == 0))
            ? null
            : new ScopePrefFixesAndMarkers(old.ShowFixes, [.. old.PinnedMarkers.Select(marker => marker.ToUpperInvariant())]);

    /// <summary>
    /// <paramref name="captured"/> with the old BRITE values that have a Scope group and lie inside its range overridden; a
    /// value outside the range keeps the captured one, with a warning, and a target with no Scope group is dropped.
    /// </summary>
    private IReadOnlyDictionary<string, int> MigrateBrightness(IReadOnlyDictionary<string, int> captured, Dictionary<string, int>? values)
    {
        if (values is null)
        {
            return captured;
        }

        Dictionary<string, int> brightness = new(captured);
        foreach ((string briteTarget, string groupName) in MigratedBrightnessGroups)
        {
            if (!values.TryGetValue(briteTarget, out int value))
            {
                continue;
            }

            if (ScopeBrightness.FindGroup(groupName)?.Contains(value) == true)
            {
                brightness[groupName] = value;
            }
            else
            {
                _log.LogWarning(
                    "The old {Target} brightness {Value} is outside the Scope's {Group} range; keeping the captured value",
                    briteTarget,
                    value,
                    groupName
                );
            }
        }

        return brightness;
    }

    private static (ScopePrefSetStore?, ScopeProfileStore?) DiskScopeStores() =>
        (
            new ScopePrefSetStore(ScopePrefSetStore.DefaultRootDir, AppLog.CreateLogger<ScopePrefSetStore>()),
            new ScopeProfileStore(ScopeProfileStore.DefaultRootDir, AppLog.CreateLogger<ScopeProfileStore>())
        );
}
