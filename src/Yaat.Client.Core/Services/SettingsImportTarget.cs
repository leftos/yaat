using System.Text.Json.Nodes;
using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

/// <summary>
/// Where an import plan reads the current state from and writes the imported state to: the user's preferences
/// directly (<see cref="UserPreferencesImportTarget"/>), or a host that stages the change until the user applies it.
/// Favorites have no staging and always import into the store itself.
/// </summary>
public interface ISettingsImportTarget
{
    /// <summary>The macros as they stand now; clashes are found against these.</summary>
    IReadOnlyList<SavedMacro> Macros { get; }

    /// <summary>The saved layouts as they stand now; clashes are found against these.</summary>
    IReadOnlyList<SavedLayout> Layouts { get; }

    /// <summary>The favorites store an import adds to or replaces.</summary>
    FavoriteStore Favorites { get; }

    /// <summary>Makes the macro list exactly this list.</summary>
    void ReplaceMacros(IReadOnlyList<SavedMacro> macros);

    /// <summary>Makes the saved layouts exactly this list; a layout absent from it is deleted.</summary>
    void ReplaceLayouts(IReadOnlyList<SavedLayout> layouts);

    /// <summary>Overlays the verbs: each listed command takes the file's aliases, every other command keeps its own.</summary>
    /// <returns>How many commands changed.</returns>
    int ApplyVerbs(CommandSchemeImport verbs);

    /// <summary>Replaces the Aircraft List column layout.</summary>
    void ReplaceGridLayout(SavedGridLayout layout);

    /// <summary>Writes the allowlisted preferences in the object and ignores every other key.</summary>
    PreferencesImportResult ReplacePreferences(JsonObject preferences);
}

/// <summary>An import target that writes straight to the user's preferences and favorites store.</summary>
/// <param name="preferences">The preferences an import changes; each write saves the file.</param>
/// <param name="favorites">The favorites store an import changes.</param>
public sealed class UserPreferencesImportTarget(UserPreferences preferences, FavoriteStore favorites) : ISettingsImportTarget
{
    public IReadOnlyList<SavedMacro> Macros => [.. preferences.Macros.Select(m => new SavedMacro { Name = m.Name, Expansion = m.Expansion })];

    public IReadOnlyList<SavedLayout> Layouts => preferences.Layouts;

    public FavoriteStore Favorites => favorites;

    public void ReplaceMacros(IReadOnlyList<SavedMacro> macros) =>
        preferences.SetMacros([.. macros.Select(m => new MacroDefinition { Name = m.Name, Expansion = m.Expansion })]);

    public void ReplaceLayouts(IReadOnlyList<SavedLayout> layouts) => preferences.ReplaceLayouts(layouts);

    public int ApplyVerbs(CommandSchemeImport verbs)
    {
        var patterns = preferences.CommandScheme.Patterns.ToDictionary(p => p.Key, p => new CommandPattern { Aliases = [.. p.Value.Aliases] });
        int applied = 0;
        foreach ((CanonicalCommandType type, List<string> aliases) in verbs.Verbs)
        {
            if (patterns.TryGetValue(type, out CommandPattern? pattern) && !pattern.Aliases.SequenceEqual(aliases))
            {
                pattern.Aliases = [.. aliases];
                applied++;
            }
        }

        preferences.SetCommandScheme(new CommandScheme { Patterns = patterns });
        return applied;
    }

    public void ReplaceGridLayout(SavedGridLayout layout) => preferences.SetGridLayout(layout);

    public PreferencesImportResult ReplacePreferences(JsonObject preferencesJson) => preferences.ImportBundlePreferences(preferencesJson);
}
