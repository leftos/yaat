using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// One row of the container picker shown in the favorite flyouts: an existing set (SetId non-null)
/// or a container that will be created on demand when first checked (the active airport/scenario
/// before any favorite was saved to it).
/// </summary>
public sealed record FavoriteContainerOption(string Label, string? SetId, FavoriteSetKind Kind, string? Key);

/// <summary>
/// Favorite commands: quick-access buttons for frequently used commands. Favorites are identity
/// entities in <see cref="FavoriteStore"/>; every container (Global, per-airport, per-scenario,
/// named sets) is a set of favorite ids, and the display renders each visible container's block.
/// </summary>
public partial class MainViewModel
{
    public FavoriteStore FavoriteStore => _favoriteStore;

    public ObservableCollection<FavoriteDisplayEntry> DisplayFavorites { get; } = [];

    /// <summary>
    /// Label shown in the pop-out Favorites Panel status bar so the user can see which aircraft a
    /// favorite click will act on (favorites target <see cref="SelectedAircraft"/>). Notified via
    /// <c>[NotifyPropertyChangedFor]</c> on <c>SelectedAircraft</c>.
    /// </summary>
    public string FavoritePanelTargetText => SelectedAircraft is { } aircraft ? $"Target: {aircraft.Callsign}" : "No aircraft selected";

    public void RefreshDisplayFavorites()
    {
        List<FavoriteDisplayEntry> composed = _favoriteStore.ComposeDisplay(
            ActiveScenarioId,
            ActiveScenarioPrimaryAirportId,
            _preferences.FavoriteSetOrder,
            _preferences.LoadedFavoriteSetIds,
            _preferences.ShowGlobalFavoriteSet
        );

        // Entries are records over stable entity references, so sequence equality is a valid
        // change detector. Skipping the no-op case matters: mutators refresh synchronously AND
        // the store's Changed event posts a deferred refresh, so without this guard every
        // ordinary favorites action would rebuild the bar/panel a second time.
        if (composed.SequenceEqual(DisplayFavorites))
        {
            return;
        }

        DisplayFavorites.Clear();
        foreach (FavoriteDisplayEntry entry in composed)
        {
            DisplayFavorites.Add(entry);
        }
    }

    /// <summary>
    /// The container list for the flyout membership pickers, in display order: Global, the active
    /// airport (created on demand), other airports with favorites, the active scenario (created on
    /// demand), other scenarios with favorites, then every named set.
    /// </summary>
    public List<FavoriteContainerOption> BuildFavoriteContainerOptions()
    {
        var options = new List<FavoriteContainerOption> { new("Global", _favoriteStore.GlobalSet.Id, FavoriteSetKind.Global, Key: null) };

        string? activeAirport = FavoriteStore.NormalizeAirportId(ActiveScenarioPrimaryAirportId);
        if (activeAirport is not null && _favoriteStore.FindAirportSet(activeAirport) is null)
        {
            options.Add(new FavoriteContainerOption($"Airport ({activeAirport})", SetId: null, FavoriteSetKind.Airport, activeAirport));
        }

        bool activeScenarioPending = ActiveScenarioId is not null && _favoriteStore.FindScenarioSet(ActiveScenarioId) is null;
        foreach (FavoriteSet set in _favoriteStore.OrderedSets)
        {
            switch (set.Kind)
            {
                case FavoriteSetKind.Airport:
                case FavoriteSetKind.Scenario:
                    options.Add(new FavoriteContainerOption(set.DisplayName, set.Id, set.Kind, set.Key));
                    break;
                case FavoriteSetKind.Named:
                    string label = IsFavoriteSetLoaded(set.Id) ? set.Name : $"{set.Name} (not loaded)";
                    options.Add(new FavoriteContainerOption(label, set.Id, set.Kind, Key: null));
                    break;
            }
        }

        if (activeScenarioPending)
        {
            string scenarioLabel = $"Scenario ({ActiveScenarioName ?? ActiveScenarioId})";
            var scenarioOption = new FavoriteContainerOption(scenarioLabel, SetId: null, FavoriteSetKind.Scenario, ActiveScenarioId);
            int insertAt = options.FindLastIndex(o => o.Kind is FavoriteSetKind.Global or FavoriteSetKind.Airport or FavoriteSetKind.Scenario) + 1;
            options.Insert(insertAt, scenarioOption);
        }

        return options;
    }

    /// <summary>Resolves a picker option to a concrete set id, creating the on-demand airport/scenario container if needed.</summary>
    public string EnsureFavoriteContainer(FavoriteContainerOption option)
    {
        if (option.SetId is not null)
        {
            return option.SetId;
        }

        return option.Kind switch
        {
            FavoriteSetKind.Airport when option.Key is not null => _favoriteStore.GetOrCreateAirportSet(option.Key).Id,
            FavoriteSetKind.Scenario when option.Key is not null => _favoriteStore
                .GetOrCreateScenarioSet(option.Key, ActiveScenarioName ?? option.Key)
                .Id,
            _ => _favoriteStore.GlobalSet.Id,
        };
    }

    public IReadOnlyList<FavoriteSet> NamedFavoriteSets => [.. _favoriteStore.OrderedSets.Where(s => s.Kind == FavoriteSetKind.Named)];

    public bool IsFavoriteSetLoaded(string setId) =>
        _preferences.LoadedFavoriteSetIds.Any(id => string.Equals(id, setId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Loads or unloads one named set; the display refreshes via FavoriteSetsChanged.</summary>
    public void SetFavoriteSetLoaded(string setId, bool loaded)
    {
        _preferences.SetFavoriteSetLoaded(setId, loaded);
        RefreshDisplayFavorites();
    }

    /// <summary>Shows or hides the Global set on the bar; off hides it as an unloaded named set is hidden.</summary>
    public void SetShowGlobalFavoriteSet(bool shown)
    {
        _preferences.SetShowGlobalFavoriteSet(shown);
        RefreshDisplayFavorites();
    }

    /// <summary>A Sets flyout row click: turns Global on or off for the Global row, else loads or unloads the named set.</summary>
    public void ToggleFavoriteSetRow(string entry)
    {
        if (string.Equals(entry, FavoriteSetOrdering.GlobalSlot, StringComparison.OrdinalIgnoreCase))
        {
            SetShowGlobalFavoriteSet(!_preferences.ShowGlobalFavoriteSet);
            return;
        }

        SetFavoriteSetLoaded(entry, !IsFavoriteSetLoaded(entry));
    }

    /// <summary>A button newly put in Global while Global is off turns Global back on, so the button shows.</summary>
    private void ShowGlobalWhenAddedTo(IEnumerable<string> setIds) =>
        _preferences.ShowGlobalFavoriteSetIfAddedTo(_favoriteStore.GlobalSet.Id, setIds);

    /// <summary>
    /// The Sets flyout's rows in bar order: Global, the airport and scenario slots while a set fills them,
    /// and every named set, loaded or not.
    /// </summary>
    public IReadOnlyList<FavoriteSetRow> FavoriteSetRows
    {
        get
        {
            List<string> visible = [.. NormalizedFavoriteSetOrder().Where(IsFavoriteSetEntryVisible)];
            return [.. visible.Select((entry, index) => BuildFavoriteSetRow(entry, canMoveUp: index > 0, canMoveDown: index < visible.Count - 1))];
        }
    }

    /// <summary>
    /// Moves one row of the bar order a place up (<paramref name="step"/> -1) or down (+1), swapping it with the
    /// nearest visible row in that direction; hidden slots keep their places. No-op past the visible edge.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is neither -1 nor +1.</exception>
    public void MoveFavoriteSet(string entry, int step)
    {
        if ((step != -1) && (step != 1))
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "step is -1 (▲) or +1 (▼)");
        }

        List<string> order = NormalizedFavoriteSetOrder();
        int from = order.FindIndex(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase));
        if ((from < 0) || !IsFavoriteSetEntryVisible(order[from]))
        {
            return;
        }

        int to = from + step;
        while ((to >= 0) && (to < order.Count) && !IsFavoriteSetEntryVisible(order[to]))
        {
            to += step;
        }

        if ((to < 0) || (to >= order.Count))
        {
            return;
        }

        (order[from], order[to]) = (order[to], order[from]);
        _preferences.SetFavoriteSetOrder(order);
        RefreshDisplayFavorites();
    }

    /// <summary>
    /// The ids of the sets the bar shows, in bar order: Global while it is shown, the airport and scenario sets filling
    /// their slots, and each loaded named set. A new button goes to these when exactly one is visible.
    /// </summary>
    public List<string> VisibleFavoriteSetIds() =>
        [
            .. _favoriteStore
                .VisibleSets(
                    ActiveScenarioId,
                    ActiveScenarioPrimaryAirportId,
                    _preferences.FavoriteSetOrder,
                    _preferences.LoadedFavoriteSetIds,
                    _preferences.ShowGlobalFavoriteSet
                )
                .Select(s => s.Id),
        ];

    private List<string> NormalizedFavoriteSetOrder() =>
        FavoriteSetOrdering.Normalize(_preferences.FavoriteSetOrder, _preferences.LoadedFavoriteSetIds, [.. NamedFavoriteSets.Select(s => s.Id)]);

    /// <summary>False for an airport or scenario slot no set fills; such a slot keeps its place but has no row.</summary>
    private bool IsFavoriteSetEntryVisible(string entry) =>
        entry switch
        {
            FavoriteSetOrdering.AirportSlot => ActiveFavoriteAirportSet() is not null,
            FavoriteSetOrdering.ScenarioSlot => (ActiveScenarioId is not null) && (_favoriteStore.FindScenarioSet(ActiveScenarioId) is not null),
            _ => true,
        };

    private FavoriteSet? ActiveFavoriteAirportSet() =>
        FavoriteStore.NormalizeAirportId(ActiveScenarioPrimaryAirportId) is { } airport ? _favoriteStore.FindAirportSet(airport) : null;

    private FavoriteSetRow BuildFavoriteSetRow(string entry, bool canMoveUp, bool canMoveDown) =>
        entry switch
        {
            FavoriteSetOrdering.GlobalSlot => new FavoriteSetRow(
                entry,
                "Global",
                IsSlot: true,
                IsLoaded: _preferences.ShowGlobalFavoriteSet,
                canMoveUp,
                canMoveDown
            ),
            FavoriteSetOrdering.AirportSlot => new FavoriteSetRow(
                entry,
                $"Airport ({ActiveFavoriteAirportSet()?.Key})",
                IsSlot: true,
                IsLoaded: true,
                canMoveUp,
                canMoveDown
            ),
            FavoriteSetOrdering.ScenarioSlot => new FavoriteSetRow(entry, "Scenario", IsSlot: true, IsLoaded: true, canMoveUp, canMoveDown),
            _ => new FavoriteSetRow(
                entry,
                _favoriteStore.GetSet(entry)?.Name ?? entry,
                IsSlot: false,
                IsFavoriteSetLoaded(entry),
                canMoveUp,
                canMoveDown
            ),
        };

    public static FavoriteCommandCategory NormalizeFavoriteCategory(FavoriteCommandCategory category) =>
        Enum.IsDefined(category) ? category : FavoriteCommandCategory.Air;

    /// <summary>Airport-id normalization shared with the video-map favorite scopes (trim + uppercase, blank → null).</summary>
    public static string? NormalizeFavoriteAirportId(string? airportId) => FavoriteStore.NormalizeAirportId(airportId);

    public async Task ExecuteFavoriteAsync(FavoriteCommand favorite)
    {
        if (favorite.IsSpacer)
        {
            return;
        }

        string currentText = CommandText.Trim();
        string favoriteCommandText = ResolveFavoriteCommandText(favorite);

        // Build the full command: "{currentText} {favoriteCommandText}" or just "{favoriteCommandText}"
        string fullCommand = string.IsNullOrEmpty(currentText) ? favoriteCommandText : $"{currentText} {favoriteCommandText}";

        string savedText = CommandText;
        int savedCaret = CommandCaretIndex;
        CommandText = fullCommand;
        CommandCaretIndex = fullCommand.Length;

        try
        {
            await SendCommandAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Favorite command execution failed");
            CommandText = savedText;
            CommandCaretIndex = Math.Min(savedCaret, savedText.Length);
        }
    }

    public void AppendFavoriteToInput(FavoriteCommand favorite)
    {
        if (favorite.IsSpacer)
        {
            return;
        }

        string current = CommandText.TrimEnd();
        string favoriteCommandText = ResolveFavoriteCommandText(favorite);
        string newText = string.IsNullOrEmpty(current) ? favoriteCommandText : $"{current}, {favoriteCommandText}";
        CommandText = newText;
        CommandCaretIndex = newText.Length;
    }

    public string ResolveFavoriteCommandText(FavoriteCommand favorite) => ResolveFavoriteCommandTextFor(favorite, SelectedAircraft);

    public static string ResolveFavoriteCommandTextFor(FavoriteCommand favorite, AircraftModel? aircraft)
    {
        if (favorite.IsSpacer)
        {
            return "";
        }

        if (aircraft?.IsOnGround == true && !string.IsNullOrWhiteSpace(favorite.GroundCommandText))
        {
            return favorite.GroundCommandText.Trim();
        }

        return favorite.CommandText.Trim();
    }

    /// <summary>Saves the favorite entity and appends it to each destination set.</summary>
    public void AddFavorite(FavoriteCommand favorite, IReadOnlyList<string> setIds) => AddFavorites([favorite], setIds);

    /// <summary>Saves each favorite entity and appends all of them to each destination set (shared entities, not copies).</summary>
    public void AddFavorites(IReadOnlyList<FavoriteCommand> favorites, IReadOnlyList<string> setIds)
    {
        ShowGlobalWhenAddedTo(setIds);
        foreach (FavoriteCommand favorite in favorites)
        {
            _favoriteStore.SaveFavorite(favorite);
            foreach (string setId in setIds)
            {
                _favoriteStore.AddToSet(setId, favorite.Id);
            }
        }
        RefreshDisplayFavorites();
    }

    /// <summary>
    /// Commits an edit to the entity (visible in every container at once) and syncs its membership
    /// to exactly the given set ids: newly checked sets get it appended, unchecked ones lose it.
    /// </summary>
    public void UpdateFavorite(FavoriteCommand updated, IReadOnlyList<string> setIds)
    {
        _favoriteStore.SaveFavorite(updated);
        List<string> current = _favoriteStore.GetMembershipSetIds(updated.Id);
        List<string> added = [.. setIds.Where(id => !current.Contains(id, StringComparer.OrdinalIgnoreCase))];
        ShowGlobalWhenAddedTo(added);
        foreach (string? setId in added)
        {
            _favoriteStore.AddToSet(setId, updated.Id);
        }

        foreach (string? setId in current.Where(id => !setIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
        {
            _favoriteStore.RemoveFromSet(setId, updated.Id);
        }
        RefreshDisplayFavorites();
    }

    /// <summary>Deletes the entity everywhere (all containers, its file, the works).</summary>
    public void DeleteFavorite(string favoriteId)
    {
        _favoriteStore.DeleteFavorite(favoriteId);
        RefreshDisplayFavorites();
    }

    public List<string> GetFavoriteMembership(string favoriteId) => _favoriteStore.GetMembershipSetIds(favoriteId);

    public void InsertBlankBefore(FavoriteDisplayEntry anchor, FavoriteCommand blank) => InsertFavoriteNear(anchor, blank, offset: 0);

    public void InsertBlankAfter(FavoriteDisplayEntry anchor, FavoriteCommand blank) => InsertFavoriteNear(anchor, blank, offset: 1);

    /// <summary>
    /// The button the Edit Favorite flyout's Copy holds for Paste, as it stood when copied. One buffer for the bar and the
    /// Favorites panel; never saved, so a restart empties it. Null until the first Copy.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFavoriteToPaste))]
    private FavoriteCommand? _copiedFavorite;

    /// <summary>
    /// The button and set the Edit Favorite flyout's Cut marked for Paste to move. Exclusive with
    /// <see cref="CopiedFavorite"/>: a Cut clears the copy, a Copy clears the cut, and one Paste clears the cut.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFavoriteToPaste))]
    private FavoriteDisplayEntry? _cutFavoriteSource;

    /// <summary>True while a Copy or a Cut is waiting for Paste.</summary>
    public bool HasFavoriteToPaste => (CopiedFavorite is not null) || (CutFavoriteSource is not null);

    /// <summary>Holds a snapshot of <paramref name="favorite"/> for <see cref="PasteFavoriteOnto"/>, replacing any pending cut.</summary>
    public void CopyFavorite(FavoriteCommand favorite)
    {
        CutFavoriteSource = null;
        CopiedFavorite = favorite.Clone();
    }

    /// <summary>Marks <paramref name="source"/> (this button in this set) for <see cref="PasteFavoriteOnto"/> to move, replacing any copy.</summary>
    public void CutFavorite(FavoriteDisplayEntry source)
    {
        CopiedFavorite = null;
        CutFavoriteSource = source;
    }

    /// <summary>
    /// Empties <paramref name="entry"/>'s slot in its own set: a new blank of the button's tab and height takes its place.
    /// The button itself is untouched; other sets keep it, and with none left it is listed under "Not in any set".
    /// </summary>
    public void DeleteFavoriteSettings(FavoriteDisplayEntry entry)
    {
        if (FindFavoriteSlot(nameof(DeleteFavoriteSettings), entry.SetId, entry.Favorite.Id) is not { } slot)
        {
            return;
        }

        FavoriteCommand blank = NewBlankLike(entry.Favorite);
        _favoriteStore.SaveFavorite(blank);
        slot.Ids[slot.Index] = blank.Id;
        _favoriteStore.ReplaceSetFavorites(slot.Set.Id, slot.Ids);
        RefreshDisplayFavorites();
    }

    /// <summary>A set, a copy of its id list, and the index of one favorite in it.</summary>
    private sealed record FavoriteSlot(FavoriteSet Set, List<string> Ids, int Index);

    /// <summary>
    /// Finds <paramref name="favoriteId"/>'s place in <paramref name="setId"/>; null, with a warning, when the set or
    /// the slot is gone.
    /// </summary>
    private FavoriteSlot? FindFavoriteSlot(string caller, string setId, string favoriteId)
    {
        FavoriteSet? set = _favoriteStore.GetSet(setId);
        if (set is null)
        {
            _log.LogWarning("{Caller}: unknown set {SetId}", caller, setId);
            return null;
        }

        var ids = set.FavoriteIds.ToList();
        int index = ids.FindIndex(id => string.Equals(id, favoriteId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            _log.LogWarning("{Caller}: favorite {FavoriteId} is not in set {SetId}", caller, favoriteId, set.Id);
            return null;
        }

        return new FavoriteSlot(set, ids, index);
    }

    /// <summary>A new blank (new id) of <paramref name="favorite"/>'s tab and height, to hold its place.</summary>
    private FavoriteCommand NewBlankLike(FavoriteCommand favorite) =>
        new()
        {
            Id = _favoriteStore.NewFavoriteId(),
            IsSpacer = true,
            Category = favorite.Category,
            ButtonHeight = favorite.ButtonHeight,
        };

    /// <summary>Gives a pasted button the anchor's tab (except onto an All button, where it keeps its own) and height.</summary>
    private static void FitToSlot(FavoriteCommand pasted, FavoriteCommand anchor)
    {
        if ((anchor.IsSpacer) || (anchor.Category != FavoriteCommandCategory.All))
        {
            pasted.Category = anchor.Category;
        }
        pasted.ButtonHeight = anchor.ButtonHeight;
    }

    /// <summary>A pasted-over blank no set holds any more is deleted; a pasted-over real button stays in the store.</summary>
    private void DeleteReplacedBlank(FavoriteCommand replaced)
    {
        if (replaced.IsSpacer && (_favoriteStore.GetMembershipSetIds(replaced.Id).Count == 0))
        {
            _favoriteStore.DeleteFavorite(replaced.Id);
        }
    }

    /// <summary>
    /// The cut button, its source slot and the anchor's slot, or null when the paste must not happen: the anchor is the
    /// cut slot itself, a slot is gone, or the anchor's set already holds the button elsewhere. A cut whose source is gone
    /// is dropped; the other refusals keep it pending.
    /// </summary>
    private (FavoriteCommand Moving, FavoriteSlot Source, FavoriteSlot Target)? ResolveCutPaste(FavoriteDisplayEntry cut, FavoriteDisplayEntry anchor)
    {
        bool isCutSlot =
            (string.Equals(cut.SetId, anchor.SetId, StringComparison.OrdinalIgnoreCase))
            && (string.Equals(cut.Favorite.Id, anchor.Favorite.Id, StringComparison.OrdinalIgnoreCase));
        if (isCutSlot)
        {
            return null;
        }

        FavoriteSlot? source = FindFavoriteSlot(nameof(PasteFavoriteOnto), cut.SetId, cut.Favorite.Id);
        if ((source is null) || (_favoriteStore.GetFavorite(cut.Favorite.Id) is not { } moving))
        {
            CutFavoriteSource = null;
            return null;
        }

        if (FindFavoriteSlot(nameof(PasteFavoriteOnto), anchor.SetId, anchor.Favorite.Id) is not { } target)
        {
            return null;
        }

        bool sameSet = string.Equals(source.Set.Id, target.Set.Id, StringComparison.OrdinalIgnoreCase);
        if ((!sameSet) && (target.Ids.Contains(moving.Id, StringComparer.OrdinalIgnoreCase)))
        {
            _log.LogWarning("PasteFavoriteOnto: set {SetId} already holds the cut button {FavoriteId}", target.Set.Id, moving.Id);
            return null;
        }

        return (moving, source, target);
    }

    /// <summary>
    /// Moves the cut button (same id) onto <paramref name="anchor"/>'s slot, fitted to it as a copy is, and puts a new
    /// blank of the button's tab and height in the slot it was cut from. Other sets keep linking it. Clears the cut.
    /// </summary>
    private void PasteCutFavoriteOnto(FavoriteDisplayEntry cut, FavoriteDisplayEntry anchor)
    {
        if (ResolveCutPaste(cut, anchor) is not { } resolved)
        {
            return;
        }

        (FavoriteCommand moving, FavoriteSlot source, FavoriteSlot target) = resolved;

        FavoriteCommand blank = NewBlankLike(moving);
        FavoriteCommand moved = moving.Clone();
        FitToSlot(moved, anchor.Favorite);
        _favoriteStore.SaveFavorite(blank);
        _favoriteStore.SaveFavorite(moved);

        bool sameSet = string.Equals(source.Set.Id, target.Set.Id, StringComparison.OrdinalIgnoreCase);
        target.Ids[target.Index] = moved.Id;
        List<string> sourceIds = sameSet ? target.Ids : source.Ids;
        sourceIds[source.Index] = blank.Id;
        _favoriteStore.ReplaceSetFavorites(target.Set.Id, target.Ids);
        if (!sameSet)
        {
            _favoriteStore.ReplaceSetFavorites(source.Set.Id, source.Ids);
        }

        DeleteReplacedBlank(anchor.Favorite);
        CutFavoriteSource = null;
        RefreshDisplayFavorites();
    }

    /// <summary>
    /// Replaces <paramref name="anchor"/> at its position in its own set with an independent copy of the copied button
    /// (a new id, a spacer staying a spacer). The copy takes the anchor's tab (category) and height so the slot keeps
    /// its place in the grid, except onto an All button, where it keeps its own tab; every other field is the copied
    /// button's. Only this set changes: a blank the anchor shares with other sets stays blank there. The replaced button
    /// stays in the store, listed under "Not in any set" when no set holds it any more, so it can be got back; a replaced
    /// blank that no other set holds is deleted. No-op while nothing is copied, and when the anchor's set or slot is gone.
    /// A pending cut moves the cut button instead (see <see cref="PasteCutFavoriteOnto"/>).
    /// </summary>
    public void PasteFavoriteOnto(FavoriteDisplayEntry anchor)
    {
        if (CutFavoriteSource is { } cut)
        {
            PasteCutFavoriteOnto(cut, anchor);
            return;
        }

        if (CopiedFavorite?.Clone() is not { } copy)
        {
            return;
        }

        if (FindFavoriteSlot(nameof(PasteFavoriteOnto), anchor.SetId, anchor.Favorite.Id) is not { } slot)
        {
            return;
        }

        copy.Id = _favoriteStore.NewFavoriteId();
        FitToSlot(copy, anchor.Favorite);
        slot.Ids[slot.Index] = copy.Id;

        _favoriteStore.SaveFavorite(copy);
        _favoriteStore.ReplaceSetFavorites(slot.Set.Id, slot.Ids);
        DeleteReplacedBlank(anchor.Favorite);

        RefreshDisplayFavorites();
    }

    public void MoveFavoriteBefore(FavoriteDisplayEntry moving, FavoriteDisplayEntry anchor) => MoveFavoriteNear(moving, anchor, offset: 0);

    public void MoveFavoriteAfter(FavoriteDisplayEntry moving, FavoriteDisplayEntry anchor) => MoveFavoriteNear(moving, anchor, offset: 1);

    private void InsertFavoriteNear(FavoriteDisplayEntry anchor, FavoriteCommand favorite, int offset)
    {
        FavoriteSet? set = _favoriteStore.GetSet(anchor.SetId);
        if (set is null)
        {
            return;
        }

        _favoriteStore.SaveFavorite(favorite);
        int anchorIndex = set.FavoriteIds.FindIndex(id => string.Equals(id, anchor.Favorite.Id, StringComparison.OrdinalIgnoreCase));
        _favoriteStore.InsertInSet(set.Id, favorite.Id, anchorIndex < 0 ? int.MaxValue : anchorIndex + offset);
        RefreshDisplayFavorites();
    }

    private void MoveFavoriteNear(FavoriteDisplayEntry moving, FavoriteDisplayEntry anchor, int offset)
    {
        // Bar/palette reordering stays within one container; changing which containers a favorite
        // is in is the membership checkboxes' (or the editor's) job.
        if (moving == anchor || !string.Equals(moving.SetId, anchor.SetId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        FavoriteSet? set = _favoriteStore.GetSet(moving.SetId);
        if (set is null)
        {
            return;
        }

        var ids = set.FavoriteIds.ToList();
        if (ids.RemoveAll(id => string.Equals(id, moving.Favorite.Id, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            return;
        }

        int anchorIndex = ids.FindIndex(id => string.Equals(id, anchor.Favorite.Id, StringComparison.OrdinalIgnoreCase));
        if (anchorIndex < 0)
        {
            ids.Add(moving.Favorite.Id);
        }
        else
        {
            ids.Insert(anchorIndex + offset, moving.Favorite.Id);
        }

        _favoriteStore.ReplaceSetFavorites(set.Id, ids);
        RefreshDisplayFavorites();
    }
}
