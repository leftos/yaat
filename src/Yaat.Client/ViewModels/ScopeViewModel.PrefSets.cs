using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's pref sets: the display state captured as a <see cref="ScopePrefSet"/> and applied back, the per-facility
/// <see cref="ScopePrefSetStore"/> that <c>.scope pref</c> saves through, and the name of the set the Scope shows. A set is
/// applied straight through the Scope's own setters, never through dot commands, so a load changes the scope and echoes
/// once.
/// </summary>
public sealed partial class ScopeViewModel
{
    private static readonly ILogger Log = AppLog.CreateLogger<ScopeViewModel>();

    /// <summary>
    /// The pref set or saved state applied while the scope waited for the map source (<see cref="IsWaitingForMapSource"/>),
    /// whose maps, range and centre are held and applied right after the next seed.
    /// </summary>
    private ScopePrefSet? _pendingPrefSet;

    /// <summary>The facility the scope's state was last saved or restored for, or null when it has none.</summary>
    private string? _stateFacility;

    /// <summary>The active set's uppercase name, or null when no set is active.</summary>
    [ObservableProperty]
    private string? _activePrefSet;

    /// <summary>The store <c>.scope pref</c> reads and writes, or null when pref sets are unavailable.</summary>
    internal ScopePrefSetStore? PrefStore { get; }

    /// <summary>The facility's pref-set names, in file order, read on each call; empty without a store or an active facility.</summary>
    internal IReadOnlyList<string> PrefSetNames
    {
        get
        {
            if (PrefStore is not { } store)
            {
                return [];
            }

            string? facility = Viewer.EramFacilityId;
            if (!ScopePrefSetStore.IsSafeFacility(facility))
            {
                return [];
            }

            List<string> names = [];
            foreach (ScopePrefSet set in store.LoadSets(facility))
            {
                if (set.Name is { } name)
                {
                    names.Add(name);
                }
            }

            return names;
        }
    }

    /// <summary>Tells the Scope's views that the facility's pref sets changed, so an open PREF tray rebuilds.</summary>
    internal void NotifyPrefSetsChanged() => OnPropertyChanged(nameof(PrefSetNames));

    /// <summary>
    /// The scope's display state as a pref set named <paramref name="name"/>: range and centre, template, BRITE, DB, the
    /// maps that are on, the open views and every view's place, the tear-offs, the bar's and the views' order, the
    /// profile's name, the pinned band values, the vector lines' OWN or ALL, top-down mode and the pilot lens's switches
    /// (not its toggled tracks). Bands, and the values that follow them, live only in the profile's file.
    /// </summary>
    /// <param name="name">The set's uppercase name, or null for the scope's unnamed current state.</param>
    /// <remarks>
    /// While a set's maps, range and centre are held for the next seed, those are what the scope is about to show, so they
    /// are captured in place of the live ones.
    /// </remarks>
    internal ScopePrefSet CapturePrefSet(string? name)
    {
        ScopePrefSet shown = CaptureShownState(name);
        return (_pendingPrefSet is { } held)
            ? shown with
            {
                RangeNm = held.RangeNm,
                CenterLat = held.CenterLat,
                CenterLon = held.CenterLon,
                Maps = [.. held.Maps],
            }
            : shown;
    }

    private ScopePrefSet CaptureShownState(string? name) =>
        new()
        {
            Name = name,
            RangeNm = RangeNm,
            CenterLat = CenterLat,
            CenterLon = CenterLon,
            TemplateId = TemplateId,
            Brightness = ScopeBrightness.Groups.ToDictionary(group => group.Name, group => group.Get(Brightness)),
            Db = new ScopePrefDb(DbSettings.Line4, DbSettings.HoverDwell, DbSettings.Atpa, DbSettings.CharSize),
            Maps = CaptureMaps(),
            OpenViews = [.. OpenViews],
            ViewPlaces = [.. _allViews.Select(CaptureViewPlace)],
            Tearoffs = [.. Tearoffs.Select(CaptureTearoff)],
            Toolbar = [.. ToolbarButtons.Select(button => ScopeToolbarButtons.NameOf(ScopeToolbarButtons.KeyOf(button)))],
            Views = [.. _allViews.Select(view => view.Name)],
            Kind = ScopeProfilePicker.NameOf(ProfileKind),
            PinOthersLevel = Pins.OthersLevel,
            PinVectorMin = Pins.VectorMin,
            PinHistory = Pins.History,
            PinRings = Pins.Rings,
            VectorOwnership = ScopeVectorOwnerships.NameOf(VectorOwnership),
            Tdm = IsTdm,
            Lens = new ScopePrefLens(Lens.Enabled, Lens.Path, Lens.Queue, Lens.CheckIn),
            MapTools = CaptureMapTools(),
        };

    /// <summary>
    /// Applies <paramref name="set"/> to the scope, keeping the current value of any field the set holds out of range or
    /// unknown, and logs a warning for each one. The set's profile is not applied here: the saved-state restore never
    /// changes the profile, and <c>.scope pref load</c> applies it first (<see cref="ApplyPrefSetProfile"/>).
    /// </summary>
    /// <remarks>
    /// While the scope waits for the map source (<see cref="IsWaitingForMapSource"/>) everything but the set's maps, range
    /// and centre applies at once; those three, which the next seed would overwrite, are held and applied right after it.
    /// </remarks>
    /// <returns>The number of the set's maps this scope lacks; 0 while they are held.</returns>
    internal int ApplyPrefSet(ScopePrefSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        bool hold = IsWaitingForMapSource;
        _pendingPrefSet = hold ? set : null;
        ApplyTemplate(set.TemplateId);
        ApplyBrightness(set.Brightness);
        ApplyDb(set.Db);
        ApplyPins(set);
        ApplyVectorOwnership(set.VectorOwnership);
        IsTdm = set.Tdm;
        Lens = new ScopeLensSettings(set.Lens.Enabled, set.Lens.Path, set.Lens.Queue, set.Lens.CheckIn);
        WarnUnknownOpenViews(set.OpenViews);
        ApplyOpenViews(set.OpenViews);
        ApplyViewPlaces(set.ViewPlaces);
        ApplyTearoffs(set.Tearoffs);
        ApplyMapTools(set.MapTools);
        return hold ? 0 : ApplySeededParts(set);
    }

    /// <summary>Applies the parts of <paramref name="set"/> a seed overwrites: its range, centre and maps.</summary>
    /// <returns>The number of the set's maps this scope lacks.</returns>
    private int ApplySeededParts(ScopePrefSet set)
    {
        ApplyRange(set.RangeNm);
        ApplyCentre(set.CenterLat, set.CenterLon);
        ApplySeededMapTools(set.MapTools);
        return ApplyMaps(set);
    }

    /// <summary>
    /// The scope's pref sets belong to the viewer's facility, so a new viewer brings a new list, and a new facility brings
    /// back the state it was left in, after the state of the facility the scope leaves is saved.
    /// </summary>
    partial void OnViewerChanged(ScopeViewer value)
    {
        SyncStateFacility();
        OnPropertyChanged(nameof(PrefSetNames));
    }

    /// <summary>
    /// Saves the state of the facility the scope was on and restores the viewer's, when the viewer's facility differs
    /// from that one. The constructor runs it as its last step, so the saved state overrides the defaults it built.
    /// </summary>
    private void SyncStateFacility()
    {
        string? facility = SafeFacility(Viewer.EramFacilityId);
        if (facility == _stateFacility)
        {
            return;
        }

        SaveStateFor(_stateFacility);

        // A held set belongs to the facility the scope leaves; it was saved with that facility's state just now.
        _pendingPrefSet = null;
        RestoreStateFor(facility);
        _stateFacility = facility;
    }

    /// <summary>Saves the scope's state as its facility's current state, as a facility change and <see cref="Dispose"/> do.</summary>
    public void SaveState() => SaveStateFor(_stateFacility);

    /// <summary>Saves the scope's state as <paramref name="facility"/>'s current state, warning when the file cannot be written.</summary>
    private void SaveStateFor(string? facility)
    {
        if ((PrefStore is not { } store) || (!ScopePrefSetStore.IsSafeFacility(facility)))
        {
            return;
        }

        try
        {
            store.SaveCurrent(facility, Ordinal, CapturePrefSet(ActivePrefSet));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Could not save the scope's state for {Facility}", facility);
        }
    }

    /// <summary>
    /// Restores <paramref name="facility"/>'s saved current state, applying it and making its named set the active one.
    /// A facility with no saved state keeps the screen as it is and clears the active set, which named a set of the
    /// facility the scope left.
    /// </summary>
    private void RestoreStateFor(string? facility)
    {
        if ((PrefStore is not { } store) || (!ScopePrefSetStore.IsSafeFacility(facility)))
        {
            return;
        }

        if (store.LoadCurrent(facility, Ordinal) is not { } state)
        {
            ActivePrefSet = null;
            return;
        }

        ApplyPrefSet(state);
        ActivePrefSet = (state.Name is { } name) && (store.FindSet(facility, name) is not null) ? name : null;
    }

    /// <summary><paramref name="facility"/> when it can name a facility's files, else null.</summary>
    private static string? SafeFacility(string? facility) => ScopePrefSetStore.IsSafeFacility(facility) ? facility : null;

    /// <summary>The maps a set saves: the on toggles' STARS ids.</summary>
    private IReadOnlyList<int> CaptureMaps() => [.. MapToggles.Where(toggle => toggle.IsOn).Select(toggle => toggle.StarsId)];

    private static ScopePrefView CaptureViewPlace(ScopeDockViewModel view) =>
        new(view.Name, view.Place is { } place ? ScopePlacement.NameOf(place.Corner) : null, view.Place?.Dx ?? 0, view.Place?.Dy ?? 0);

    private static ScopePrefTearoff CaptureTearoff(ScopeTearoff tearoff) =>
        new(ScopeToolbarButtons.NameOf(tearoff.Key), ScopePlacement.NameOf(tearoff.Place.Corner), tearoff.Place.Dx, tearoff.Place.Dy);

    private void ApplyTemplate(string templateId)
    {
        if (!ScopeTemplates.IsSelectable(templateId))
        {
            Log.LogWarning("A pref set names unknown template '{TemplateId}'; keeping {Current}", templateId, TemplateId);
            return;
        }

        PickTemplateCommand.Execute(templateId);
    }

    private void ApplyBrightness(IReadOnlyDictionary<string, int> brightness)
    {
        foreach ((string name, int value) in brightness)
        {
            ScopeBrightnessGroup? group = ScopeBrightness.FindGroup(name);
            if ((group is null) || !group.Contains(value))
            {
                Log.LogWarning("A pref set's BRITE group '{Group}' = {Value} is not one this scope takes; keeping it", name, value);
                continue;
            }

            SetBrightness(group.Name, value);
        }
    }

    private void ApplyDb(ScopePrefDb db)
    {
        int size = DbSettings.CharSize;
        if (ScopeDbSettings.IsCharSize(db.CharSize))
        {
            size = db.CharSize;
        }
        else
        {
            Log.LogWarning(
                "A pref set's CHAR {Size} is outside {Min}-{Max}; keeping {Current}",
                db.CharSize,
                ScopeDbSettings.MinCharSize,
                ScopeDbSettings.MaxCharSize,
                size
            );
        }

        DbSettings = new ScopeDbSettings(db.Line4, db.HoverDwell, db.Atpa, size);
    }

    private void ApplyRange(double rangeNm)
    {
        if (rangeNm is >= 1 and <= 256)
        {
            RangeNm = rangeNm;
            return;
        }

        Log.LogWarning("A pref set's range {Range} is outside 1-256; keeping {Current}", rangeNm, RangeNm);
    }

    private void ApplyCentre(double lat, double lon)
    {
        if (!IsCentre(lat, lon))
        {
            Log.LogWarning("A pref set's centre {Lat}, {Lon} is not a centre; keeping {CurrentLat}, {CurrentLon}", lat, lon, CenterLat, CenterLon);
            return;
        }

        CenterLat = lat;
        CenterLon = lon;
    }

    /// <summary>Whether <paramref name="lat"/> and <paramref name="lon"/> are a centre: in range, and neither the "no centre" zero.</summary>
    private static bool IsCentre(double lat, double lon) => (lat is >= -90 and <= 90) && (lon is >= -180 and <= 180) && (lat != 0) && (lon != 0);

    /// <summary>Logs a warning for each open-view name the scope does not know, which <see cref="ApplyOpenViews"/> ignores.</summary>
    private static void WarnUnknownOpenViews(IReadOnlyList<string> names)
    {
        foreach (string name in names)
        {
            if (!IsViewName(name))
            {
                Log.LogWarning("A pref set names unknown view '{Name}' to open; ignoring it", name);
            }
        }
    }

    private void ApplyViewPlaces(IReadOnlyList<ScopePrefView> places)
    {
        foreach (ScopePrefView entry in places)
        {
            RestoreViewPlaceFrom(entry);
        }
    }

    private void RestoreViewPlaceFrom(ScopePrefView? entry)
    {
        if (entry?.Name is null)
        {
            Log.LogWarning("A pref set holds an empty view place; skipping it");
            return;
        }

        if (!IsViewName(entry.Name))
        {
            Log.LogWarning("A pref set names unknown view '{Name}'; keeping every view's place", entry.Name);
            return;
        }

        if (entry.Corner is null)
        {
            RestoreViewPlace(entry.Name, null);
            return;
        }

        if (!ScopePlacement.TryParseCorner(entry.Corner, out ScopeCorner corner))
        {
            Log.LogWarning("A pref set gives view '{Name}' unknown corner '{Corner}'; keeping its place", entry.Name, entry.Corner);
            return;
        }

        RestoreViewPlace(entry.Name, new ScopePlacement(corner, Math.Max(0, entry.Dx), Math.Max(0, entry.Dy)));
    }

    private void ApplyTearoffs(IReadOnlyList<ScopePrefTearoff> tearoffs)
    {
        DeleteTearoffs([.. Tearoffs.Select(tearoff => tearoff.Key)]);
        foreach (ScopePrefTearoff entry in tearoffs)
        {
            PlaceSavedTearoff(entry);
        }
    }

    private void PlaceSavedTearoff(ScopePrefTearoff? entry)
    {
        if ((entry?.Button is null) || (entry.Corner is null))
        {
            Log.LogWarning("A pref set holds an incomplete tear-off; skipping it");
            return;
        }

        if (!ScopeToolbarButtons.TryParseKey(entry.Button, out ScopeButtonKey? key))
        {
            Log.LogWarning("A pref set names unknown button '{Button}'; skipping its tear-off", entry.Button);
            return;
        }

        if (!ScopePlacement.TryParseCorner(entry.Corner, out ScopeCorner corner))
        {
            Log.LogWarning("A pref set gives tear-off '{Button}' unknown corner '{Corner}'; skipping it", entry.Button, entry.Corner);
            return;
        }

        PlaceTearoff(key, new ScopePlacement(corner, Math.Max(0, entry.Dx), Math.Max(0, entry.Dy)));
    }

    /// <summary>
    /// Turns the set's maps on and the rest off. A scope with no map toggles has nothing to turn on, so its maps are left
    /// alone rather than cleared, and every map of the set counts as missing.
    /// </summary>
    private int ApplyMaps(ScopePrefSet set) => (MapToggles.Count == 0) ? set.Maps.Count : ApplyMapsOver(set.Maps);

    /// <summary>Turns the maps with <paramref name="maps"/>' STARS ids on and the rest off.</summary>
    /// <returns>How many of them this scope has no toggle for.</returns>
    private int ApplyMapsOver(IReadOnlyList<int> maps)
    {
        ClearMaps();
        int missing = 0;
        foreach (int starsId in maps)
        {
            if (!SetMapOn(starsId, true))
            {
                missing++;
            }
        }

        return missing;
    }

    /// <summary>
    /// Applies the held set's range, centre and maps, after the seed that has just overwritten them, or after a load that
    /// ended with no new catalogue. A scope with no map toggles has nothing to turn on, so its maps are left alone. It adds
    /// no echo.
    /// </summary>
    private void ApplyPendingPrefSet()
    {
        if (_pendingPrefSet is not { } set)
        {
            return;
        }

        _pendingPrefSet = null;
        _ = ApplySeededParts(set);
    }
}
