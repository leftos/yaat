using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Commands;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The Import / Export hub opened from Settings stages its import into the open window: macros, verbs and preferences
/// land in the view model's rows and fields, the preferences file is untouched until Apply, and a window closed with
/// Cancel leaves it as it was.
/// </summary>
public class SettingsViewModelImportTargetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-settings-import-target-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Macros_MergeWithOverwriteAndRename_StageInTheRows_AndApplyWritesThem()
    {
        using var scope = new PreferencesFileScope();
        var preferences = new UserPreferences();
        preferences.SetMacros([Saved("HC &hdg", "FH &hdg"), Saved("DEP", "CTO")]);
        var vm = new SettingsViewModel(preferences);
        var target = new SettingsViewModelImportTarget(vm, NewStore());

        SettingsBundleEntry entry = SettingsBundleItems.Macros([Macro("HC &h", "TL &h"), Macro("DEP", "CTO MRC"), Macro("PAT", "ERD")]);
        SettingsImportPlan plan = SettingsImportPlanner.Plan(entry, SettingsImportMode.Merge, target);
        plan.Clashes.Single(c => c.IncomingName == "HC &h").Choice = ClashChoice.Overwrite;
        plan.Clashes.Single(c => c.IncomingName == "DEP").Choice = ClashChoice.Rename;
        SettingsImportPlanner.Apply(plan, target);

        const string Expected = "HC &h=TL &h|DEP=CTO|DEP_2=CTO MRC|PAT=ERD";
        Assert.Equal(Expected, Describe(vm.MacroRows.Select(r => (r.Name, r.Expansion))));
        Assert.Equal("HC &hdg=FH &hdg|DEP=CTO", Describe(new UserPreferences().Macros.Select(m => (m.Name, m.Expansion))));

        vm.ApplyCommand.Execute(null);

        Assert.Equal(Expected, Describe(new UserPreferences().Macros.Select(m => (m.Name, m.Expansion))));
    }

    [Fact]
    public void Verbs_Replace_OverlaysTheGrid_KeepsUnlistedEdits_AndApplyWritesThem()
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel(new UserPreferences());
        var target = new SettingsViewModelImportTarget(vm, NewStore());
        VerbMappingRow edited = vm.VerbMappings.First(r => r.CommandType != CanonicalCommandType.Timer);
        edited.Aliases = "ZZEDIT";

        var file = new CommandScheme
        {
            Patterns = new Dictionary<CanonicalCommandType, CommandPattern> { [CanonicalCommandType.Timer] = new() { Aliases = ["TMRX"] } },
        };
        SettingsImportPlan plan = SettingsImportPlanner.Plan(SettingsBundleItems.Verbs(file), SettingsImportMode.Replace, target);
        SettingsImportResult result = SettingsImportPlanner.Apply(plan, target);

        Assert.Equal(1, result.Overwritten);
        Assert.Equal(["TMRX"], vm.VerbMappings.Single(r => r.CommandType == CanonicalCommandType.Timer).AliasesList);
        Assert.Equal(["ZZEDIT"], edited.AliasesList);
        Assert.NotEqual(["TMRX"], new UserPreferences().CommandScheme.Patterns[CanonicalCommandType.Timer].Aliases);

        vm.ApplyCommand.Execute(null);

        CommandScheme saved = new UserPreferences().CommandScheme;
        Assert.Equal(["TMRX"], saved.Patterns[CanonicalCommandType.Timer].Aliases);
        Assert.Equal(["ZZEDIT"], saved.Patterns[edited.CommandType].Aliases);
    }

    [Fact]
    public void Preferences_Replace_StageInTheFields_AndApplyWritesThem()
    {
        using var scope = new PreferencesFileScope();
        var preferences = new UserPreferences();
        int savedFontSize = preferences.DataGridFontSize;
        int importedFontSize = (savedFontSize == 15) ? 16 : 15;
        var vm = new SettingsViewModel(preferences);
        var target = new SettingsViewModelImportTarget(vm, NewStore());

        SettingsImportPlan plan = SettingsImportPlanner.Plan(PreferencesFile(preferences, importedFontSize), SettingsImportMode.Replace, target);
        SettingsImportResult result = SettingsImportPlanner.Apply(plan, target);

        Assert.Contains("dataGridFontSize", result.Preferences!.AppliedKeys);
        Assert.Empty(result.Preferences.IgnoredKeys);
        Assert.Equal(importedFontSize, vm.DataGridFontSize);
        Assert.Equal(savedFontSize, new UserPreferences().DataGridFontSize);

        vm.ApplyCommand.Execute(null);

        Assert.Equal(importedFontSize, new UserPreferences().DataGridFontSize);
    }

    [Fact]
    public void StagedImports_DiscardedWithTheWindow_LeaveThePreferencesFileUnchanged()
    {
        using var scope = new PreferencesFileScope();
        var preferences = new UserPreferences();
        preferences.SetMacros([Saved("DEP", "CTO")]);
        string before = File.ReadAllText(YaatPaths.Combine("preferences.json"));
        var vm = new SettingsViewModel(preferences);
        var target = new SettingsViewModelImportTarget(vm, NewStore());
        SettingsBundleEntry preferencesFile = PreferencesFile(preferences, (preferences.DataGridFontSize == 15) ? 16 : 15);
        var file = new CommandScheme
        {
            Patterns = new Dictionary<CanonicalCommandType, CommandPattern> { [CanonicalCommandType.Timer] = new() { Aliases = ["TMRX"] } },
        };

        SettingsImportPlanner.Apply(
            SettingsImportPlanner.Plan(SettingsBundleItems.Macros([Macro("PAT", "ERD")]), SettingsImportMode.Replace, target),
            target
        );
        SettingsImportPlanner.Apply(SettingsImportPlanner.Plan(SettingsBundleItems.Verbs(file), SettingsImportMode.Replace, target), target);
        SettingsImportPlanner.Apply(SettingsImportPlanner.Plan(preferencesFile, SettingsImportMode.Replace, target), target);

        Assert.Equal("PAT=ERD", Describe(vm.MacroRows.Select(r => (r.Name, r.Expansion))));
        Assert.Equal(before, File.ReadAllText(YaatPaths.Combine("preferences.json")));
        Assert.Equal("DEP=CTO", Describe(new UserPreferences().Macros.Select(m => (m.Name, m.Expansion))));
    }

    [Fact]
    public void ExportFromSettings_CarriesTheStagedUnsavedEdits()
    {
        using var scope = new PreferencesFileScope();
        var preferences = new UserPreferences();
        preferences.SetMacros([Saved("DEP", "CTO")]);
        int savedFontSize = preferences.DataGridFontSize;
        int stagedFontSize = (savedFontSize == 15) ? 16 : 15;
        var vm = new SettingsViewModel(preferences);
        using var target = new SettingsViewModelImportTarget(vm, NewStore());
        var source = new SettingsViewModelExportSource(vm, preferences, target, selectedMacros: null);

        vm.DataGridFontSize = stagedFontSize;
        vm.MacroRows.Single().Expansion = "CTO MRC";

        JsonObject exported = Assert.IsType<JsonObject>(JsonNode.Parse(source.Export(SettingsItemType.Preferences).Content));
        Assert.Equal(stagedFontSize, exported["dataGridFontSize"]!.GetValue<int>());
        List<SavedMacro>? macros = JsonSerializer.Deserialize<List<SavedMacro>>(
            source.Export(SettingsItemType.Macros).Content,
            UserPreferences.JsonOptions
        );
        Assert.Equal("DEP=CTO MRC", Describe(macros!.Select(m => (m.Name, m.Expansion))));
        Assert.Equal(savedFontSize, new UserPreferences().DataGridFontSize);
        Assert.Equal("DEP=CTO", Describe(new UserPreferences().Macros.Select(m => (m.Name, m.Expansion))));
    }

    [Fact]
    public void ColumnLayoutAndLayouts_StageUntilApply_ThenApplyWritesBoth_AndReportsTheColumnLayoutOnlyThatTime()
    {
        using var scope = new PreferencesFileScope();
        var preferences = new UserPreferences();
        string gridBefore = JsonSerializer.Serialize(preferences.GridLayout, UserPreferences.JsonOptions);
        var vm = new SettingsViewModel(preferences);
        using var target = new SettingsViewModelImportTarget(vm, NewStore());
        var columnLayout = new SavedGridLayout { ColumnOrder = ["Callsign", "Type"], HiddenColumns = ["Type"] };

        SettingsImportPlanner.Apply(
            SettingsImportPlanner.Plan(SettingsBundleItems.GridLayout(columnLayout), SettingsImportMode.Replace, target),
            target
        );
        SettingsImportPlanner.Apply(
            SettingsImportPlanner.Plan(SettingsBundleItems.Layouts([new SavedLayout { Name = "STG-Layout" }]), SettingsImportMode.Replace, target),
            target
        );

        Assert.Equal(["STG-Layout"], vm.StagedLayouts.Select(l => l.Name));
        Assert.DoesNotContain(new UserPreferences().Layouts, l => l.Name == "STG-Layout");
        Assert.Equal(gridBefore, JsonSerializer.Serialize(new UserPreferences().GridLayout, UserPreferences.JsonOptions));

        vm.ApplyCommand.Execute(null);

        var saved = new UserPreferences();
        Assert.Equal(["STG-Layout"], saved.Layouts.Select(l => l.Name));
        Assert.Equal(["Callsign", "Type"], saved.GridLayout!.ColumnOrder);
        Assert.Equal(["Type"], saved.GridLayout.HiddenColumns);
        Assert.True(vm.LastApplyCommittedGridLayout);

        vm.ApplyCommand.Execute(null);

        Assert.False(vm.LastApplyCommittedGridLayout);
    }

    [Fact]
    public void EveryBundledPreference_ExportedWithANonDefaultValue_StagedAndApplied_ComesBackAsExported()
    {
        using var scope = new PreferencesFileScope();
        JsonObject defaults = UserPreferences.CreateDefaults().ExportBundlePreferences();
        JsonObject nonDefaults = NonDefaultBundledPreferences(defaults);
        var source = new UserPreferences();
        Assert.Empty(source.ImportBundlePreferences(nonDefaults).IgnoredKeys);
        JsonObject exported = source.ExportBundlePreferences();
        Assert.Equal(UserPreferences.BundledPreferenceKeys, exported.Select(p => p.Key));
        Assert.All(exported, p => Assert.False(JsonNode.DeepEquals(p.Value, defaults[p.Key]), $"{p.Key} kept its default"));

        var preferences = new UserPreferences();
        preferences.ImportBundlePreferences(defaults);
        Assert.True(JsonNode.DeepEquals(defaults, preferences.ExportBundlePreferences()));
        var vm = new SettingsViewModel(preferences);
        var target = new SettingsViewModelImportTarget(vm, NewStore());
        SettingsBundleEntry file = SettingsBundleItems.Preferences(source);
        SettingsImportResult result = SettingsImportPlanner.Apply(SettingsImportPlanner.Plan(file, SettingsImportMode.Replace, target), target);
        vm.ApplyCommand.Execute(null);

        Assert.Empty(result.Preferences!.IgnoredKeys);
        JsonObject applied = preferences.ExportBundlePreferences();
        List<string> differing = [.. exported.Where(p => !JsonNode.DeepEquals(p.Value, applied[p.Key])).Select(p => p.Key)];
        Assert.Empty(differing);
    }

    // Every bundled preference with a value its rule accepts and its default is not; keybinds each get their own combo.
    private static JsonObject NonDefaultBundledPreferences(JsonObject defaults)
    {
        var values = new JsonObject();
        int keybind = 0;
        foreach ((string key, JsonNode? defaultValue) in defaults)
        {
            BundledPreferenceRule rule = UserPreferences.BundledPreferenceRules[key];
            IEnumerable<JsonNode?> candidates =
                ReferenceEquals(rule, BundledPreferenceRule.KeyName) ? [JsonValue.Create($"Ctrl+Alt+{(char)('A' + keybind++)}")]
                : ReferenceEquals(rule, BundledPreferenceRule.ModelSource) ? [JsonValue.Create("fhi-model-" + key)]
                : Candidates(defaultValue);
            JsonNode? value = candidates.FirstOrDefault(c =>
                !JsonNode.DeepEquals(c, defaultValue) && UserPreferences.TryReadBundledPreference(key, c, out _)
            );
            values[key] =
                value?.DeepClone() ?? throw new InvalidOperationException($"No candidate for {key}, default {defaultValue?.ToJsonString()}");
        }

        return values;
    }

    // Whole-number steps first (an int preference rejects a fraction), then fractions a double keeps exactly.
    private static IEnumerable<JsonNode?> Candidates(JsonNode? defaultValue)
    {
        if (defaultValue is null)
        {
            return [JsonValue.Create(5.0), JsonValue.Create(50.0)];
        }

        return defaultValue.GetValueKind() switch
        {
            JsonValueKind.True or JsonValueKind.False => [JsonValue.Create(!defaultValue.GetValue<bool>())],
            JsonValueKind.Number =>
            [
                .. new[] { 1, -1, 5, -5, 10, -10 }
                    .Where(_ => int.TryParse(defaultValue.ToJsonString(), out _))
                    .Select(i => (JsonNode?)JsonValue.Create(defaultValue.GetValue<int>() + i)),
                .. new[] { 0.5, -0.5, 0.25, -0.25, 1, -1 }.Select(d => (JsonNode?)JsonValue.Create(defaultValue.GetValue<double>() + d)),
                null,
            ],
            _ =>
            [
                .. new[] { "#80123456", "Never", "OnLanding", "Parked", "Above", "Below" }
                    .Concat(Enum.GetNames<VfrCommandsForIfr>())
                    .Select(s => (JsonNode?)JsonValue.Create(s)),
            ],
        };
    }

    // Every bundled preference as saved, but the Aircraft List font size: an import of it writes every Settings field.
    private static SettingsBundleEntry PreferencesFile(UserPreferences saved, int dataGridFontSize)
    {
        JsonObject file = saved.ExportBundlePreferences();
        file["dataGridFontSize"] = dataGridFontSize;
        return SettingsBundleItems.Preferences(saved) with { Content = JsonSerializer.SerializeToUtf8Bytes(file) };
    }

    private static SavedMacro Macro(string name, string expansion) => new() { Name = name, Expansion = expansion };

    private static MacroDefinition Saved(string name, string expansion) => new() { Name = name, Expansion = expansion };

    private static string Describe(IEnumerable<(string Name, string Expansion)> macros) =>
        string.Join("|", macros.Select(m => $"{m.Name}={m.Expansion}"));

    private FavoriteStore NewStore() => new(Path.Combine(_root, "favorites-" + Guid.NewGuid().ToString("N")));
}
