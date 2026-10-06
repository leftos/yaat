using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// The import clash planner: under Merge, named entries that clash (a macro by base name, a favorite set by name, a layout
/// by name) are listed with a suggested rename, each clash's Skip / Overwrite / Rename lands as chosen, and renames are
/// validated before anything is written.
/// </summary>
public class SettingsBundleClashTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-settingsbundle-clash-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void MacroClash_IsListed_WithTheNextFreeNumberedSuggestion()
    {
        InMemorySettingsImportTarget target = NewTarget();
        target.MacroList = [Macro("ILSAPP &rwy", "old"), Macro("ILSAPP_2", "taken")];
        SettingsBundleEntry entry = SettingsBundleItems.Macros([Macro("ilsapp &r", "new"), Macro("DEP", "CTO")]);

        SettingsImportPlan plan = SettingsImportPlanner.Plan(entry, SettingsImportMode.Merge, target);

        ImportClash clash = Assert.Single(plan.Clashes);
        Assert.Equal("ilsapp &r", clash.IncomingName);
        Assert.Equal("ILSAPP &rwy", clash.ExistingName);
        Assert.Equal("ilsapp_3", clash.SuggestedName);
        Assert.Equal("ilsapp_3", clash.RenameTo);
        Assert.Equal(ClashChoice.Overwrite, clash.Choice);
    }

    [Fact]
    public void FavoriteSetClash_IsListed_WithTheNextFreeParenthesisedSuggestion()
    {
        InMemorySettingsImportTarget target = NewTarget();
        target.Favorites.CreateNamedSet("Tower");
        target.Favorites.CreateNamedSet("Tower (2)");
        FavoriteStore source = SeededStore("source", "tower", "FH 270");

        SettingsImportPlan plan = SettingsImportPlanner.Plan(SettingsBundleItems.Favorites(source, []), SettingsImportMode.Merge, target);

        ImportClash clash = Assert.Single(plan.Clashes);
        Assert.Equal("tower", clash.IncomingName);
        Assert.Equal("Tower", clash.ExistingName);
        Assert.Equal("tower (3)", clash.SuggestedName);
        Assert.Equal(ClashChoice.Overwrite, clash.Choice);
    }

    [Fact]
    public void LayoutClash_IsListed_WithTheNextFreeParenthesisedSuggestion()
    {
        InMemorySettingsImportTarget target = NewTarget();
        target.LayoutList = [new SavedLayout { Name = "GC" }];
        SettingsBundleEntry entry = SettingsBundleItems.Layouts([new SavedLayout { Name = "gc" }, new SavedLayout { Name = "LC" }]);

        SettingsImportPlan plan = SettingsImportPlanner.Plan(entry, SettingsImportMode.Merge, target);

        ImportClash clash = Assert.Single(plan.Clashes);
        Assert.Equal("gc", clash.IncomingName);
        Assert.Equal("GC", clash.ExistingName);
        Assert.Equal("gc (2)", clash.SuggestedName);
    }

    [Theory]
    [InlineData(ClashChoice.Overwrite, "HC &h=TL &h|DEP=CTO")]
    [InlineData(ClashChoice.Skip, "HC &hdg=FH &hdg|DEP=CTO")]
    [InlineData(ClashChoice.Rename, "HC &hdg=FH &hdg|HC_2 &h=TL &h|DEP=CTO")]
    public void MacroClash_ChoiceIsApplied(ClashChoice choice, string expected)
    {
        InMemorySettingsImportTarget target = NewTarget();
        target.MacroList = [Macro("HC &hdg", "FH &hdg")];
        SettingsBundleEntry entry = SettingsBundleItems.Macros([Macro("HC &h", "TL &h"), Macro("DEP", "CTO")]);
        SettingsImportPlan plan = SettingsImportPlanner.Plan(entry, SettingsImportMode.Merge, target);
        plan.Clashes[0].Choice = choice;

        SettingsImportPlanner.Apply(plan, target);

        Assert.Equal(expected, string.Join("|", target.MacroList.Select(m => $"{m.Name}={m.Expansion}")));
    }

    [Theory]
    [InlineData(ClashChoice.Overwrite, "GC:True|LC:False")]
    [InlineData(ClashChoice.Skip, "GC:False|LC:False")]
    [InlineData(ClashChoice.Rename, "GC:False|GC (2):True|LC:False")]
    public void LayoutClash_ChoiceIsApplied(ClashChoice choice, string expected)
    {
        InMemorySettingsImportTarget target = NewTarget();
        target.LayoutList = [new SavedLayout { Name = "GC" }];
        SettingsBundleEntry entry = SettingsBundleItems.Layouts([
            new SavedLayout { Name = "GC", IsTerminalPoppedOut = true },
            new SavedLayout { Name = "LC" },
        ]);
        SettingsImportPlan plan = SettingsImportPlanner.Plan(entry, SettingsImportMode.Merge, target);
        plan.Clashes[0].Choice = choice;

        SettingsImportPlanner.Apply(plan, target);

        Assert.Equal(expected, string.Join("|", target.LayoutList.Select(l => $"{l.Name}:{l.IsTerminalPoppedOut}")));
    }

    [Theory]
    [InlineData(ClashChoice.Overwrite, "Tower=New", true)]
    [InlineData(ClashChoice.Skip, "Tower=Old", false)]
    [InlineData(ClashChoice.Rename, "Tower=Old|Tower (2)=New", true)]
    public void FavoriteSetClash_ChoiceIsApplied(ClashChoice choice, string expected, bool newFavoriteImported)
    {
        InMemorySettingsImportTarget target = NewTarget();
        FavoriteStore targetStore = target.Favorites;
        FavoriteSet existing = targetStore.CreateNamedSet("Tower")!;
        var old = new FavoriteCommand { Label = "Old", CommandText = "Old" };
        targetStore.SaveFavorite(old);
        targetStore.AddToSet(existing.Id, old.Id);
        FavoriteStore source = SeededStore("source", "Tower", "New");
        SettingsImportPlan plan = SettingsImportPlanner.Plan(SettingsBundleItems.Favorites(source, []), SettingsImportMode.Merge, target);
        plan.Clashes[0].Choice = choice;

        SettingsImportPlanner.Apply(plan, target);

        IEnumerable<string> namedSets = targetStore
            .OrderedSets.Where(s => s.Kind == FavoriteSetKind.Named)
            .Select(s => $"{s.Name}={string.Join(",", targetStore.GetSetFavorites(s.Id).Select(f => f.Label))}");
        Assert.Equal(expected, string.Join("|", namedSets));
        Assert.Equal(newFavoriteImported, targetStore.AllFavorites.Any(f => f.Label == "New"));
        Assert.Equal(existing.Id, targetStore.FindNamedSet("Tower")!.Id);
    }

    [Fact]
    public void RenameValidation_RejectsEmptyInvalidExistingAndDuplicateNames()
    {
        InMemorySettingsImportTarget target = NewTarget();
        target.MacroList = [Macro("AA", "x"), Macro("BB", "x"), Macro("CC", "x"), Macro("EE", "x"), Macro("FF", "x"), Macro("EXIST", "x")];
        SettingsBundleEntry entry = SettingsBundleItems.Macros([
            Macro("AA", "y"),
            Macro("BB", "y"),
            Macro("CC", "y"),
            Macro("EE", "y"),
            Macro("FF", "y"),
            Macro("FRESH", "y"),
        ]);
        SettingsImportPlan plan = SettingsImportPlanner.Plan(entry, SettingsImportMode.Merge, target);
        string[] renames = ["  ", "9bad", "exist", "NEWNAME", "newname"];
        for (int i = 0; i < renames.Length; i++)
        {
            plan.Clashes[i].Choice = ClashChoice.Rename;
            plan.Clashes[i].RenameTo = renames[i];
        }

        IReadOnlyList<RenameError> errors = SettingsImportPlanner.ValidateRenames(plan);

        Assert.Equal(
            ["AA: Name is required", "BB: Invalid macro name", "CC: Name already exists", "EE: Duplicate rename", "FF: Duplicate rename"],
            errors.Select(e => $"{e.Clash.IncomingName}: {e.Message}")
        );
        Assert.Throws<InvalidOperationException>(() => SettingsImportPlanner.Apply(plan, target));
        Assert.Equal(6, target.MacroList.Count);
    }

    [Fact]
    public void RenameValidation_RejectsARenameOntoANonClashingIncomingName()
    {
        InMemorySettingsImportTarget target = NewTarget();
        target.LayoutList = [new SavedLayout { Name = "GC" }];
        SettingsBundleEntry entry = SettingsBundleItems.Layouts([new SavedLayout { Name = "GC" }, new SavedLayout { Name = "LC" }]);
        SettingsImportPlan plan = SettingsImportPlanner.Plan(entry, SettingsImportMode.Merge, target);
        plan.Clashes[0].Choice = ClashChoice.Rename;
        plan.Clashes[0].RenameTo = "lc";

        RenameError error = Assert.Single(SettingsImportPlanner.ValidateRenames(plan));

        Assert.Equal("Name already exists", error.Message);
    }

    [Fact]
    public void LayoutsReplace_DeletesLayoutsAbsentFromTheBundle()
    {
        var preferences = new UserPreferences();
        preferences.SaveLayout(new SavedLayout { Name = "BundleTest-Old" });
        var target = new UserPreferencesImportTarget(preferences, NewStore("favorites"));
        SettingsBundleEntry entry = SettingsBundleItems.Layouts([new SavedLayout { Name = "BundleTest-New", IsMetarPoppedOut = true }]);

        SettingsImportPlanner.Apply(SettingsImportPlanner.Plan(entry, SettingsImportMode.Replace, target), target);

        SavedLayout layout = Assert.Single(preferences.Layouts);
        Assert.Equal("BundleTest-New", layout.Name);
        Assert.True(layout.IsMetarPoppedOut);
        preferences.DeleteLayout("BundleTest-New");
    }

    [Fact]
    public void LayoutsMerge_KeepsLayoutsAbsentFromTheBundle()
    {
        var preferences = new UserPreferences();
        preferences.SaveLayout(new SavedLayout { Name = "BundleTest-Kept" });
        var target = new UserPreferencesImportTarget(preferences, NewStore("favorites"));
        SettingsBundleEntry entry = SettingsBundleItems.Layouts([new SavedLayout { Name = "BundleTest-Added" }]);

        SettingsImportPlanner.Apply(SettingsImportPlanner.Plan(entry, SettingsImportMode.Merge, target), target);

        Assert.NotNull(preferences.GetLayout("BundleTest-Kept"));
        Assert.NotNull(preferences.GetLayout("BundleTest-Added"));
        preferences.DeleteLayout("BundleTest-Kept");
        preferences.DeleteLayout("BundleTest-Added");
    }

    [Theory]
    [InlineData(SettingsItemType.Preferences)]
    [InlineData(SettingsItemType.Verbs)]
    [InlineData(SettingsItemType.GridLayout)]
    public void ReplaceOnlyItems_RejectMerge(SettingsItemType type)
    {
        InMemorySettingsImportTarget target = NewTarget();
        var entry = new SettingsBundleEntry(type, "unused", "unused.json", []);

        Assert.Throws<ArgumentException>(() => SettingsImportPlanner.Plan(entry, SettingsImportMode.Merge, target));
    }

    private static SavedMacro Macro(string name, string expansion) => new() { Name = name, Expansion = expansion };

    private FavoriteStore NewStore(string name) => new(Path.Combine(_root, name));

    private InMemorySettingsImportTarget NewTarget() => new(NewStore("target"));

    private FavoriteStore SeededStore(string storeName, string setName, string label)
    {
        FavoriteStore store = NewStore(storeName);
        FavoriteSet set = store.CreateNamedSet(setName)!;
        var favorite = new FavoriteCommand { Label = label, CommandText = label };
        store.SaveFavorite(favorite);
        store.AddToSet(set.Id, favorite.Id);
        return store;
    }
}
