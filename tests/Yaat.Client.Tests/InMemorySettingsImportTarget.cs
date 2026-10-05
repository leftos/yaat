using System.Text.Json.Nodes;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

/// <summary>
/// An import target that keeps everything in memory, standing in for the user's preferences file so the settings-bundle
/// tests never share state through the per-process preferences.json. Favorites go to a real store in a throwaway folder.
/// </summary>
internal sealed class InMemorySettingsImportTarget(FavoriteStore favorites) : ISettingsImportTarget
{
    public List<SavedMacro> MacroList { get; set; } = [];

    public List<SavedLayout> LayoutList { get; set; } = [];

    public Dictionary<CanonicalCommandType, List<string>> AppliedVerbs { get; } = [];

    public SavedGridLayout? GridLayout { get; private set; }

    public JsonObject? Preferences { get; private set; }

    public IReadOnlyList<SavedMacro> Macros => MacroList;

    public IReadOnlyList<SavedLayout> Layouts => LayoutList;

    public FavoriteStore Favorites => favorites;

    public void ReplaceMacros(IReadOnlyList<SavedMacro> macros) => MacroList = [.. macros];

    public void ReplaceLayouts(IReadOnlyList<SavedLayout> layouts) => LayoutList = [.. layouts];

    public int ApplyVerbs(CommandSchemeImport verbs)
    {
        foreach ((CanonicalCommandType type, List<string> aliases) in verbs.Verbs)
        {
            AppliedVerbs[type] = aliases;
        }

        return verbs.Verbs.Count;
    }

    public void ReplaceGridLayout(SavedGridLayout layout) => GridLayout = layout;

    public PreferencesImportResult ReplacePreferences(JsonObject preferences)
    {
        Preferences = preferences;
        return new PreferencesImportResult([.. preferences.Select(p => p.Key)], []);
    }
}
