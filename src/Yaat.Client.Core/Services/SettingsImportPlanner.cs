using System.Text.Json.Nodes;

namespace Yaat.Client.Services;

/// <summary>What happens to an incoming named entry whose name (or, for a favorite set, id) an existing one already uses.</summary>
public enum ClashChoice
{
    /// <summary>The incoming entry takes the existing one's place.</summary>
    Overwrite,

    /// <summary>The incoming entry is not imported; the existing one stays.</summary>
    Skip,

    /// <summary>The incoming entry is imported under <see cref="ImportClash.RenameTo"/>; the existing one stays.</summary>
    Rename,
}

/// <summary>One incoming named entry (a macro, a favorite set, a layout) that clashes with an existing entry.</summary>
public sealed class ImportClash
{
    internal ImportClash(string incomingName, string existingName, string suggestedName, int incomingIndex)
    {
        IncomingName = incomingName;
        ExistingName = existingName;
        SuggestedName = suggestedName;
        RenameTo = suggestedName;
        IncomingIndex = incomingIndex;
    }

    /// <summary>The entry's name in the file (a macro's full name, parameters included).</summary>
    public string IncomingName { get; }

    /// <summary>The existing entry's name it clashes with.</summary>
    public string ExistingName { get; }

    /// <summary>
    /// A free name to rename to: macros <c>Name_2</c>…<c>Name_99</c> then <c>Name_renamed</c>; favorite sets and layouts
    /// <c>Name (2)</c>, <c>Name (3)</c>….
    /// </summary>
    public string SuggestedName { get; }

    /// <summary>What the import does with the entry; Overwrite unless the user picks otherwise.</summary>
    public ClashChoice Choice { get; set; } = ClashChoice.Overwrite;

    /// <summary>The name used when <see cref="Choice"/> is Rename; starts as <see cref="SuggestedName"/>. A macro's parameters carry over.</summary>
    public string RenameTo { get; set; }

    /// <summary>Position of the entry in the item's incoming list (the entity index for favorites).</summary>
    internal int IncomingIndex { get; }
}

/// <summary>A clash whose rename cannot be used, with the reason in words.</summary>
/// <param name="Clash">The clash.</param>
/// <param name="Message">"Name is required", "Invalid macro name", "Name already exists" or "Duplicate rename".</param>
public sealed record RenameError(ImportClash Clash, string Message);

/// <summary>
/// One bundle entry parsed and checked against the current state: what the file holds and, under Merge, which named
/// entries clash. The user's choices go into <see cref="Clashes"/>; <see cref="SettingsImportPlanner.Apply"/> carries it
/// out once. Nothing is written while planning.
/// </summary>
public sealed class SettingsImportPlan
{
    internal SettingsImportPlan(SettingsBundleEntry entry, SettingsImportMode mode)
    {
        Entry = entry;
        Mode = mode;
    }

    public SettingsBundleEntry Entry { get; }

    public SettingsItemType ItemType => Entry.ItemType;

    public SettingsImportMode Mode { get; }

    /// <summary>
    /// What the file holds, for a preview: the macros' full names, the favorite sets' display names, the layouts' names,
    /// the commands whose verbs it lists, or the preference keys it carries; empty for the grid layout.
    /// </summary>
    public IReadOnlyList<string> IncomingNames { get; internal set; } = [];

    /// <summary>The clashing named entries, in file order; empty under Replace and for item types without names.</summary>
    public IReadOnlyList<ImportClash> Clashes { get; internal set; } = [];

    /// <summary>Command names in a verbs file this version does not know; they are skipped.</summary>
    public IReadOnlyList<string> UnknownCommands { get; internal set; } = [];

    /// <summary>Names (base names for macros) a rename may not take: every existing name plus every non-clashing incoming one.</summary>
    internal HashSet<string> TakenNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    internal List<SavedMacro>? Macros { get; set; }

    internal List<SavedLayout>? Layouts { get; set; }

    internal CommandSchemeImport? Verbs { get; set; }

    internal SavedGridLayout? GridLayout { get; set; }

    internal JsonObject? Preferences { get; set; }

    internal FavoritesBundleImport? Favorites { get; set; }
}

/// <summary>What one item's import changed.</summary>
public sealed class SettingsImportResult
{
    public required SettingsItemType ItemType { get; init; }

    /// <summary>Entries added without a clash; for favorites, the named sets the store added (renamed ones included).</summary>
    public int Added { get; init; }

    /// <summary>Entries that took an existing one's place: overwriting clashes, changed verbs, written preferences, the grid layout.</summary>
    public int Overwritten { get; init; }

    public int Renamed { get; init; }

    /// <summary>Entries not imported: skipped clashes, duplicates within the file, ignored preference keys.</summary>
    public int Skipped { get; init; }

    public IReadOnlyList<string> UnknownCommands { get; init; } = [];

    /// <summary>The favorites store's own report, including the new set ids the file asked to load; null for other items or an empty file.</summary>
    public FavoriteImportResult? Favorites { get; init; }

    /// <summary>
    /// For a favorites import, every named set's id in the file mapped to its id in the store, or to null when the set was
    /// skipped or not imported. <see cref="SettingsImportPlanner.RemapFavoriteSetIds"/> applies it to a layouts plan.
    /// </summary>
    public IReadOnlyDictionary<string, string?> FavoriteSetIdMap { get; init; } = new Dictionary<string, string?>();

    public PreferencesImportResult? Preferences { get; init; }
}

/// <summary>
/// Plans and applies the import of bundle entries. Under Merge, named entries clash case-insensitively — macros by base
/// name, layouts by name, favorite sets by name or by an id an existing set with another name has — and each clash is
/// resolved by its own <see cref="ClashChoice"/>. Under Replace the entry takes the place of the current item.
/// </summary>
public static class SettingsImportPlanner
{
    /// <summary>Parses the entry and lists its clashes against the target's current state.</summary>
    /// <exception cref="ArgumentException">Merge was asked for an item that imports by Replace only.</exception>
    /// <exception cref="InvalidDataException">The entry's bytes do not parse; the message names its file.</exception>
    public static SettingsImportPlan Plan(SettingsBundleEntry entry, SettingsImportMode mode, ISettingsImportTarget target)
    {
        if ((mode == SettingsImportMode.Merge) && !SettingsBundleFormats.SupportsMerge(entry.ItemType))
        {
            throw new ArgumentException($"{SettingsBundleFormats.ItemTypeName(entry.ItemType)} imports by Replace only.", nameof(mode));
        }

        var plan = new SettingsImportPlan(entry, mode);
        switch (entry.ItemType)
        {
            case SettingsItemType.Macros:
                PlanMacros(plan, target);
                break;
            case SettingsItemType.Layouts:
                PlanLayouts(plan, target);
                break;
            case SettingsItemType.Favorites:
                PlanFavorites(plan, target);
                break;
            default:
                PlanReplaceOnly(plan);
                break;
        }

        return plan;
    }

    /// <summary>Checks every clash set to Rename: a name is required, valid for its type, not taken, and not used by another rename.</summary>
    public static IReadOnlyList<RenameError> ValidateRenames(SettingsImportPlan plan)
    {
        List<(ImportClash Clash, string Key)> renames =
        [
            .. plan.Clashes.Where(c => c.Choice == ClashChoice.Rename).Select(c => (c, NameKey(plan.ItemType, c.RenameTo.Trim()))),
        ];

        var errors = new List<RenameError>();
        foreach ((ImportClash clash, string key) in renames)
        {
            string? problem =
                RenameProblem(plan, clash.RenameTo.Trim(), key)
                ?? ((renames.Count(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase)) > 1) ? "Duplicate rename" : null);
            if (problem is not null)
            {
                errors.Add(new RenameError(clash, problem));
            }
        }

        return errors;
    }

    /// <summary>Carries out one plan against the target's state as it is now.</summary>
    /// <exception cref="InvalidOperationException">A rename does not validate (<see cref="ValidateRenames"/>); nothing is written.</exception>
    public static SettingsImportResult Apply(SettingsImportPlan plan, ISettingsImportTarget target)
    {
        EnsureRenamesValid(plan);
        return plan.ItemType switch
        {
            SettingsItemType.Macros => ApplyMacros(plan, target),
            SettingsItemType.Layouts => ApplyLayouts(plan, target),
            SettingsItemType.Favorites => ApplyFavorites(plan, target),
            SettingsItemType.Verbs => ApplyVerbs(plan, target),
            SettingsItemType.GridLayout => ApplyGridLayout(plan, target),
            _ => ApplyPreferences(plan, target),
        };
    }

    /// <summary>
    /// Carries out the plans of one import, at most one per item type: favorites first, so the layouts plan's loaded
    /// favorite set ids can follow the ids the favorites import gave (<see cref="RemapFavoriteSetIds"/>), then the rest in
    /// the order given. Every rename is validated before anything is written.
    /// </summary>
    /// <returns>Each plan's result, in the order applied.</returns>
    /// <exception cref="ArgumentException">Two plans share an item type.</exception>
    /// <exception cref="InvalidOperationException">A rename does not validate; nothing is written.</exception>
    public static IReadOnlyList<SettingsImportResult> ApplyAll(IReadOnlyList<SettingsImportPlan> plans, ISettingsImportTarget target)
    {
        if (plans.GroupBy(p => p.ItemType).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            throw new ArgumentException($"One import applies one plan per item type; {duplicate.Key} appears twice.", nameof(plans));
        }

        foreach (SettingsImportPlan plan in plans)
        {
            EnsureRenamesValid(plan);
        }

        var results = new List<SettingsImportResult>();
        IReadOnlyDictionary<string, string?> setIdMap = new Dictionary<string, string?>();
        foreach (SettingsImportPlan plan in plans.OrderBy(p => (p.ItemType == SettingsItemType.Favorites) ? 0 : 1))
        {
            if (plan.ItemType == SettingsItemType.Layouts)
            {
                RemapFavoriteSetIds(plan, setIdMap);
            }

            SettingsImportResult result = Apply(plan, target);
            if (plan.ItemType == SettingsItemType.Favorites)
            {
                setIdMap = result.FavoriteSetIdMap;
            }

            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Points the incoming layouts' loaded favorite sets at the ids a favorites import gave them: a mapped id is replaced,
    /// an id mapped to null (a skipped or unimported set) is dropped, and an id the map does not name is kept.
    /// </summary>
    /// <exception cref="ArgumentException">The plan is not a layouts plan.</exception>
    public static void RemapFavoriteSetIds(SettingsImportPlan layoutsPlan, IReadOnlyDictionary<string, string?> setIdMap)
    {
        if (layoutsPlan.Layouts is not { } layouts)
        {
            throw new ArgumentException("Only a layouts plan carries loaded favorite set ids.", nameof(layoutsPlan));
        }

        foreach (SavedLayout layout in layouts)
        {
            if (layout.LoadedFavoriteSetIds is { } ids)
            {
                layout.LoadedFavoriteSetIds = [.. ids.Select(id => setIdMap.TryGetValue(id, out string? mapped) ? mapped : id).OfType<string>()];
            }
        }
    }

    private static void EnsureRenamesValid(SettingsImportPlan plan)
    {
        if (ValidateRenames(plan) is [{ } first, ..])
        {
            throw new InvalidOperationException(
                $"Cannot import: renaming '{first.Clash.IncomingName}' to '{first.Clash.RenameTo}': {first.Message}."
            );
        }
    }

    private static void PlanMacros(SettingsImportPlan plan, ISettingsImportTarget target)
    {
        List<SavedMacro> incoming = SettingsBundleItems.ReadMacros(plan.Entry);
        plan.Macros = incoming;
        plan.IncomingNames = [.. incoming.Select(m => m.Name)];
        if (plan.Mode == SettingsImportMode.Merge)
        {
            PlanClashes(
                plan,
                [.. target.Macros.Select(m => (MacroKey(m.Name), m.Name))],
                [.. incoming.Select((m, i) => (MacroKey(m.Name), m.Name, i))],
                MacroSuggestion,
                _ => null
            );
        }
    }

    private static void PlanLayouts(SettingsImportPlan plan, ISettingsImportTarget target)
    {
        List<SavedLayout> incoming = SettingsBundleItems.ReadLayouts(plan.Entry);
        plan.Layouts = incoming;
        plan.IncomingNames = [.. incoming.Select(l => l.Name)];
        if (plan.Mode == SettingsImportMode.Merge)
        {
            PlanClashes(
                plan,
                [.. target.Layouts.Select(l => (l.Name.Trim(), l.Name))],
                [.. incoming.Select((l, i) => (l.Name.Trim(), l.Name, i))],
                NumberedSuggestion,
                _ => null
            );
        }
    }

    private static void PlanFavorites(SettingsImportPlan plan, ISettingsImportTarget target)
    {
        var favorites = FavoritesBundleImport.Parse(plan.Entry);
        plan.Favorites = favorites;
        List<(int Index, FavoriteSet Set)> sets = [.. favorites.Sets];
        plan.IncomingNames = [.. sets.Select(s => s.Set.DisplayName)];
        if (plan.Mode != SettingsImportMode.Merge)
        {
            return;
        }

        FavoriteStore store = target.Favorites;
        var setByIndex = sets.ToDictionary(s => s.Index, s => s.Set);

        // A set whose id the store already gives a named set of another name would silently rename that set.
        string? IdClash(int index) =>
            (
                (store.GetSet(setByIndex[index].Id ?? "") is { Kind: FavoriteSetKind.Named } existing)
                && !string.Equals(existing.Name.Trim(), setByIndex[index].Name.Trim(), StringComparison.OrdinalIgnoreCase)
            )
                ? existing.Name
                : null;

        PlanClashes(
            plan,
            [.. store.OrderedSets.Where(s => s.Kind == FavoriteSetKind.Named).Select(s => (s.Name.Trim(), s.Name))],
            [.. sets.Where(s => s.Set.Kind == FavoriteSetKind.Named).Select(s => (s.Set.Name.Trim(), s.Set.Name, s.Index))],
            NumberedSuggestion,
            IdClash
        );
    }

    private static void PlanReplaceOnly(SettingsImportPlan plan)
    {
        switch (plan.ItemType)
        {
            case SettingsItemType.Verbs:
                plan.Verbs = SettingsBundleItems.ReadVerbs(plan.Entry);
                plan.IncomingNames = [.. plan.Verbs.Verbs.Keys.Select(k => k.ToString())];
                plan.UnknownCommands = plan.Verbs.UnknownCommands;
                break;
            case SettingsItemType.GridLayout:
                plan.GridLayout = SettingsBundleItems.ReadGridLayout(plan.Entry);
                break;
            default:
                plan.Preferences = SettingsBundleItems.ReadPreferences(plan.Entry);
                plan.IncomingNames = [.. plan.Preferences.Select(p => p.Key)];
                break;
        }
    }

    // Lists every incoming entry whose key an existing entry uses (or that idClash names an existing entry for).
    // Suggestions avoid existing names, every incoming name and the suggestions already handed out, so two clashes never
    // start with the same rename.
    private static void PlanClashes(
        SettingsImportPlan plan,
        List<(string Key, string Name)> existing,
        List<(string Key, string Name, int Index)> incoming,
        Func<string, HashSet<string>, string> suggest,
        Func<int, string?> idClash
    )
    {
        var existingByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string name) in existing)
        {
            existingByKey.TryAdd(key, name);
        }

        var taken = new HashSet<string>(existingByKey.Keys, StringComparer.OrdinalIgnoreCase);
        var avoid = new HashSet<string>(taken.Concat(incoming.Select(i => i.Key)), StringComparer.OrdinalIgnoreCase);
        var clashes = new List<ImportClash>();
        foreach ((string key, string name, int index) in incoming)
        {
            string? existingName = existingByKey.GetValueOrDefault(key) ?? idClash(index);
            if (existingName is null)
            {
                taken.Add(key);
                continue;
            }

            string suggestion = suggest(key, avoid);
            avoid.Add(suggestion);
            clashes.Add(new ImportClash(name, existingName, suggestion, index));
        }

        plan.Clashes = clashes;
        plan.TakenNames = taken;
    }

    private static string MacroSuggestion(string baseName, HashSet<string> avoid)
    {
        for (int i = 2; i < 100; i++)
        {
            string candidate = $"{baseName}_{i}";
            if (!avoid.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{baseName}_renamed";
    }

    private static string NumberedSuggestion(string name, HashSet<string> avoid)
    {
        for (int i = 2; ; i++)
        {
            string candidate = $"{name} ({i})";
            if (!avoid.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string? RenameProblem(SettingsImportPlan plan, string name, string key)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Name is required";
        }

        if ((plan.ItemType == SettingsItemType.Macros) && !MacroDefinition.IsValidName(name))
        {
            return "Invalid macro name";
        }

        return plan.TakenNames.Contains(key) ? "Name already exists" : null;
    }

    private static string NameKey(SettingsItemType itemType, string name) => (itemType == SettingsItemType.Macros) ? MacroKey(name) : name;

    private static string MacroKey(string name) => string.IsNullOrWhiteSpace(name) ? "" : MacroDefinition.ExtractBaseName(name);

    private static SettingsImportResult ApplyMacros(SettingsImportPlan plan, ISettingsImportTarget target)
    {
        List<SavedMacro> incoming = plan.Macros!;
        if (plan.Mode == SettingsImportMode.Replace)
        {
            target.ReplaceMacros(incoming);
            return new SettingsImportResult { ItemType = plan.ItemType, Added = incoming.Count };
        }

        var merge = new NamedMerge<SavedMacro>([.. target.Macros], m => MacroKey(m.Name));
        merge.Run(incoming, plan.Clashes, (m, newName) => new SavedMacro { Name = RenamedMacroName(newName, m.Name), Expansion = m.Expansion });
        target.ReplaceMacros(merge.Items);
        return merge.Result(plan.ItemType);
    }

    // A rename to a bare base name keeps the macro's declared parameters, as the macro import has always done.
    private static string RenamedMacroName(string newName, string originalName)
    {
        string[] originalTokens = originalName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return ((originalTokens.Length > 1) && !newName.Contains(' ')) ? $"{newName} {string.Join(" ", originalTokens.Skip(1))}" : newName;
    }

    private static SettingsImportResult ApplyLayouts(SettingsImportPlan plan, ISettingsImportTarget target)
    {
        List<SavedLayout> incoming = plan.Layouts!;
        if (plan.Mode == SettingsImportMode.Replace)
        {
            target.ReplaceLayouts(incoming);
            return new SettingsImportResult { ItemType = plan.ItemType, Added = incoming.Count };
        }

        var merge = new NamedMerge<SavedLayout>([.. target.Layouts], l => l.Name.Trim());
        merge.Run(
            incoming,
            plan.Clashes,
            (layout, newName) =>
            {
                layout.Name = newName;
                return layout;
            }
        );
        target.ReplaceLayouts(merge.Items);
        return merge.Result(plan.ItemType);
    }

    private static SettingsImportResult ApplyFavorites(SettingsImportPlan plan, ISettingsImportTarget target)
    {
        FavoritesImportOutcome outcome = plan.Favorites!.Import(plan.Clashes, target.Favorites, plan.Mode);
        return new SettingsImportResult
        {
            ItemType = plan.ItemType,
            Added = outcome.Result?.SetsAdded ?? 0,
            Overwritten = outcome.Overwritten,
            Renamed = outcome.Renamed,
            Skipped = outcome.Skipped,
            Favorites = outcome.Result,
            FavoriteSetIdMap = outcome.SetIdMap,
        };
    }

    private static SettingsImportResult ApplyVerbs(SettingsImportPlan plan, ISettingsImportTarget target) =>
        new()
        {
            ItemType = plan.ItemType,
            Overwritten = target.ApplyVerbs(plan.Verbs!),
            UnknownCommands = plan.UnknownCommands,
        };

    private static SettingsImportResult ApplyGridLayout(SettingsImportPlan plan, ISettingsImportTarget target)
    {
        target.ReplaceGridLayout(plan.GridLayout!);
        return new SettingsImportResult { ItemType = plan.ItemType, Overwritten = 1 };
    }

    private static SettingsImportResult ApplyPreferences(SettingsImportPlan plan, ISettingsImportTarget target)
    {
        PreferencesImportResult result = target.ReplacePreferences(plan.Preferences!);
        return new SettingsImportResult
        {
            ItemType = plan.ItemType,
            Overwritten = result.AppliedKeys.Count,
            Skipped = result.IgnoredKeys.Count,
            Preferences = result,
        };
    }

    /// <summary>
    /// Merges incoming named entries into the current list: a non-clashing entry is appended (a second entry with the same
    /// name in the file is skipped), and a clashing one is overwritten in place, skipped, or appended under its new name.
    /// </summary>
    private sealed class NamedMerge<T>(List<T> items, Func<T, string> key)
    {
        private int _added;
        private int _overwritten;
        private int _renamed;
        private int _skipped;

        public List<T> Items => items;

        public void Run(IReadOnlyList<T> incoming, IReadOnlyList<ImportClash> clashes, Func<T, string, T> rename)
        {
            var clashByIndex = clashes.ToDictionary(c => c.IncomingIndex);
            var taken = new HashSet<string>(items.Select(key), StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < incoming.Count; i++)
            {
                T item = incoming[i];
                if (clashByIndex.TryGetValue(i, out ImportClash? clash))
                {
                    Resolve(item, clash, rename);
                }
                else if ((key(item).Length > 0) && taken.Add(key(item)))
                {
                    items.Add(item);
                    _added++;
                }
                else
                {
                    _skipped++;
                }
            }
        }

        public SettingsImportResult Result(SettingsItemType itemType) =>
            new()
            {
                ItemType = itemType,
                Added = _added,
                Overwritten = _overwritten,
                Renamed = _renamed,
                Skipped = _skipped,
            };

        private void Resolve(T item, ImportClash clash, Func<T, string, T> rename)
        {
            switch (clash.Choice)
            {
                case ClashChoice.Overwrite:
                    int index = items.FindIndex(existing => string.Equals(key(existing), key(item), StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                    {
                        items[index] = item;
                    }
                    else
                    {
                        items.Add(item);
                    }

                    _overwritten++;
                    break;
                case ClashChoice.Rename:
                    items.Add(rename(item, clash.RenameTo.Trim()));
                    _renamed++;
                    break;
                default:
                    _skipped++;
                    break;
            }
        }
    }
}
