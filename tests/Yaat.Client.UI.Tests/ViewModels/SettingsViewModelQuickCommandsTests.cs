using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Sim.Commands;
using Yaat.Sim.Situation;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The Settings window's Quick Commands editor: each situation's list is a staged copy that add, remove, move, the
/// flight-rules choice and the resets edit without writing, Apply writes only the lists that differ from what the
/// preferences hold, an invalid custom entry blocks Apply, and the strip flag marks the first ten glyph entries.
/// </summary>
public class SettingsViewModelQuickCommandsTests
{
    private const string ValidCommand = "FH 270";

    /// <summary>Eleven glyph-bearing catalog actions, in list order.</summary>
    private static readonly string[] GlyphIds =
    [
        MenuIds.TrackTrack,
        MenuIds.TrackInitiateHandoff,
        MenuIds.SquawkCode,
        MenuIds.SimControlWarp,
        MenuIds.SimControlDelete,
        MenuIds.HeadingFly,
        MenuIds.AltitudeMaintain,
        MenuIds.SpeedAssign,
        MenuIds.NavigationDirectTo,
        MenuIds.ApproachCleared,
        MenuIds.HoldPattern,
    ];

    private static QuickCommandSituationRow Select(SettingsViewModel vm, AircraftSituation situation)
    {
        QuickCommandSituationRow row = vm.QuickCommandSituations.Single(r => r.Situation == situation);
        vm.SelectedQuickCommandSituation = row;
        return row;
    }

    private static QuickCommandEntryRow CatalogRow(SettingsViewModel vm, string catalogId) =>
        vm.QuickCommandEntries.Single(r => r.CatalogId == catalogId);

    private static List<QuickCommandEntry> Staged(SettingsViewModel vm) => [.. vm.QuickCommandEntries.Select(r => r.ToEntry())];

    private static List<string?> Ids(SettingsViewModel vm) => [.. vm.QuickCommandEntries.Select(r => r.CatalogId)];

    // Stores the catalog actions as the situation's list before the view model is built, so a test does not lean on the defaults.
    private static void Store(AircraftSituation situation, params string[] catalogIds) =>
        new UserPreferences().SetQuickCommandList(situation, [.. catalogIds.Select(id => new CatalogQuickCommandEntry(id, null))]);

    private static QuickCommandFlightRulesOption Option(QuickCommandEntryRow row, MenuFlightRules? rules) =>
        row.FlightRulesOptions.Single(o => o.Rules == rules);

    private static QuickCommandEntryRow AddValidCustom(SettingsViewModel vm, string label)
    {
        vm.AddQuickCommandCustomEntry();
        QuickCommandEntryRow row = vm.QuickCommandEntries[^1];
        row.Label = label;
        row.CommandText = ValidCommand;
        return row;
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void Load_ShowsStoredOverrides_AndDefaultsElsewhere()
    {
        using var scope = new PreferencesFileScope();
        List<QuickCommandEntry> taxiing =
        [
            new CatalogQuickCommandEntry(MenuIds.GroundHoldPosition, MenuFlightRules.IfrOnly),
            new CustomQuickCommandEntry("West", ValidCommand, "HOLD", MenuFlightRules.VfrOnly),
        ];
        new UserPreferences().SetQuickCommandList(AircraftSituation.Taxiing, taxiing);

        var vm = new SettingsViewModel();

        Assert.Equal(QuickCommandSituationNames.Classified, vm.QuickCommandSituations.Select(r => r.Situation));
        Assert.All(vm.QuickCommandSituations, r => Assert.Equal(QuickCommandSituationNames.NameOf(r.Situation), r.Name));
        Assert.True(Select(vm, AircraftSituation.Taxiing).IsChanged);
        Assert.Equal(taxiing, Staged(vm));
        Assert.Equal([QuickCommandEntryKind.Catalog, QuickCommandEntryKind.Custom], vm.QuickCommandEntries.Select(r => r.Kind));
        QuickCommandEntryRow hold = vm.QuickCommandEntries[0];
        Assert.Equal(MenuCatalog.Get(MenuIds.GroundHoldPosition).Label, hold.Label);
        Assert.Equal("ground", hold.Family);
        Assert.Equal(QuickCommandGlyphs.For(MenuIds.GroundHoldPosition), hold.Glyph);
        Assert.Equal("West", vm.QuickCommandEntries[1].Label);
        Assert.Equal("HOLD", vm.QuickCommandEntries[1].GroundCommandText);
        Assert.Null(vm.QuickCommandEntries[1].ValidationMessage);

        Assert.False(Select(vm, AircraftSituation.Final).IsChanged);
        Assert.Equal(QuickCommandDefaults.For(AircraftSituation.Final), Staged(vm));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void AddRemove_ChangeOnlyTheStagedList()
    {
        using var scope = new PreferencesFileScope();
        Store(AircraftSituation.Final, MenuIds.TowerClearedToLand, MenuIds.TowerGoAround, MenuIds.TowerExitLeft);
        List<QuickCommandEntry> stored = [.. new UserPreferences().GetQuickCommandList(AircraftSituation.Final)];
        var vm = new SettingsViewModel();
        Select(vm, AircraftSituation.Final);

        Assert.True(vm.TryAddQuickCommandCatalogEntry(MenuIds.GroundHoldPosition));
        Assert.Equal([MenuIds.TowerClearedToLand, MenuIds.TowerGoAround, MenuIds.TowerExitLeft, MenuIds.GroundHoldPosition], Ids(vm));
        Assert.DoesNotContain(vm.AvailableQuickCommandCatalogEntries, item => item.Id == MenuIds.GroundHoldPosition);

        vm.RemoveQuickCommandEntry(vm.QuickCommandEntries[0]);
        Assert.Equal([MenuIds.TowerGoAround, MenuIds.TowerExitLeft, MenuIds.GroundHoldPosition], Ids(vm));
        Assert.Contains(vm.AvailableQuickCommandCatalogEntries, item => item.Id == MenuIds.TowerClearedToLand);

        Select(vm, AircraftSituation.Taxiing);
        Select(vm, AircraftSituation.Final);
        Assert.Equal([MenuIds.TowerGoAround, MenuIds.TowerExitLeft, MenuIds.GroundHoldPosition], Ids(vm));
        Assert.Equal(stored, new UserPreferences().GetQuickCommandList(AircraftSituation.Final));
    }

    /// <summary>
    /// A move takes an insert position, 0 to Count, as a drop "before row i" gives it: Count lands the row last, a move
    /// down lands it before the original row i, and the row's own position (or the one after it) leaves the list alone.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public void Move_TakesAnInsertPosition()
    {
        using var scope = new PreferencesFileScope();
        const string a = MenuIds.TowerClearedToLand;
        const string b = MenuIds.TowerGoAround;
        const string c = MenuIds.TowerExitLeft;
        Store(AircraftSituation.Final, a, b, c);
        var vm = new SettingsViewModel();
        Select(vm, AircraftSituation.Final);

        vm.MoveQuickCommandEntry(0, vm.QuickCommandEntries.Count);
        Assert.Equal([b, c, a], Ids(vm));

        vm.MoveQuickCommandEntry(0, 2);
        Assert.Equal([c, b, a], Ids(vm));

        vm.MoveQuickCommandEntry(1, 1);
        vm.MoveQuickCommandEntry(1, 2);
        Assert.Equal([c, b, a], Ids(vm));

        vm.MoveQuickCommandEntry(2, 0);
        Assert.Equal([a, c, b], Ids(vm));

        Assert.Throws<ArgumentOutOfRangeException>(() => vm.MoveQuickCommandEntry(0, vm.QuickCommandEntries.Count + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => vm.MoveQuickCommandEntry(vm.QuickCommandEntries.Count, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => vm.MoveQuickCommandEntry(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => vm.MoveQuickCommandEntry(0, -1));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void EditingTheDefaultList_SetsIsChanged_AndUndoingTheEditClearsIt()
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel();
        QuickCommandSituationRow final = Select(vm, AircraftSituation.Final);
        List<QuickCommandEntry> defaults = Staged(vm);
        Assert.False(final.IsChanged);

        vm.MoveQuickCommandEntry(0, 2);
        Assert.True(final.IsChanged);
        vm.MoveQuickCommandEntry(1, 0);
        Assert.False(final.IsChanged);

        Assert.True(vm.TryAddQuickCommandCatalogEntry(vm.AvailableQuickCommandCatalogEntries[0].Id));
        Assert.True(final.IsChanged);
        vm.RemoveQuickCommandEntry(vm.QuickCommandEntries[^1]);
        Assert.False(final.IsChanged);

        Assert.Equal(defaults, Staged(vm));
        Assert.Empty(new UserPreferences().QuickCommandOverrides);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void AddCatalogEntry_RefusesADuplicateOrAnIneligibleId()
    {
        using var scope = new PreferencesFileScope();
        Store(AircraftSituation.Final, MenuIds.TowerClearedToLand, MenuIds.TowerGoAround);
        var vm = new SettingsViewModel();
        Select(vm, AircraftSituation.Final);
        int count = vm.QuickCommandEntries.Count;

        Assert.False(vm.TryAddQuickCommandCatalogEntry(MenuIds.TowerClearedToLand));
        Assert.False(vm.TryAddQuickCommandCatalogEntry(MenuIds.PointDirectTo));

        Assert.Equal(count, vm.QuickCommandEntries.Count);
        Assert.DoesNotContain(vm.AvailableQuickCommandCatalogEntries, item => item.Id == MenuIds.TowerClearedToLand);
        Assert.Equal(vm.AvailableQuickCommandCatalogEntries, vm.AvailableQuickCommandCatalogEntriesFor(AircraftSituation.Final));
        Assert.Equal(QuickCommandCatalog.Eligible.Count - count, vm.AvailableQuickCommandCatalogEntries.Count);
    }

    /// <summary>
    /// The available catalog actions are one collection the editor keeps: a row's label, command or flight-rules edit
    /// leaves it alone, and an add takes the action out of it.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public void AvailableCatalogEntries_ChangeOnlyWhenTheListDoes()
    {
        using var scope = new PreferencesFileScope();
        Store(AircraftSituation.Taxiing, MenuIds.GroundHoldPosition);
        var vm = new SettingsViewModel();
        Select(vm, AircraftSituation.Taxiing);
        QuickCommandEntryRow custom = AddValidCustom(vm, "West");
        IReadOnlyList<QuickCommandCatalogItem> available = vm.AvailableQuickCommandCatalogEntries;
        List<string?> raised = [];
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        custom.Label = "East";
        custom.CommandText = "FH 090";
        QuickCommandEntryRow hold = CatalogRow(vm, MenuIds.GroundHoldPosition);
        hold.SelectedFlightRules = Option(hold, MenuFlightRules.IfrOnly);

        Assert.DoesNotContain(nameof(SettingsViewModel.AvailableQuickCommandCatalogEntries), raised);
        Assert.Same(available, vm.AvailableQuickCommandCatalogEntries);

        string added = available[0].Id;
        Assert.True(vm.TryAddQuickCommandCatalogEntry(added));
        Assert.Same(available, vm.AvailableQuickCommandCatalogEntries);
        Assert.DoesNotContain(available, item => item.Id == added);

        Select(vm, AircraftSituation.Final);
        Assert.Same(available, vm.AvailableQuickCommandCatalogEntries);
        Assert.Equal(vm.AvailableQuickCommandCatalogEntriesFor(AircraftSituation.Final), available);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void FlightRulesChoice_EqualToTheCatalogDefault_StoresTheExplicitChoice()
    {
        using var scope = new PreferencesFileScope();
        Store(AircraftSituation.Pattern, MenuIds.PatternFollow, MenuIds.TowerClearedToLand);
        MenuFlightRules catalogDefault = MenuCatalog.Get(MenuIds.PatternFollow).DefaultFlightRules;
        var vm = new SettingsViewModel();
        Select(vm, AircraftSituation.Pattern);
        QuickCommandEntryRow follow = CatalogRow(vm, MenuIds.PatternFollow);
        Assert.Null(follow.SelectedFlightRules.Rules);

        follow.SelectedFlightRules = Option(follow, catalogDefault);
        Assert.Equal(catalogDefault, Assert.IsType<CatalogQuickCommandEntry>(follow.ToEntry()).FlightRules);

        follow.SelectedFlightRules = Option(follow, null);
        Assert.Null(Assert.IsType<CatalogQuickCommandEntry>(follow.ToEntry()).FlightRules);

        follow.SelectedFlightRules = Option(follow, catalogDefault);
        vm.ApplyCommand.Execute(null);

        QuickCommandEntry stored = new UserPreferences()
            .GetQuickCommandList(AircraftSituation.Pattern)
            .Single(e => e is CatalogQuickCommandEntry { CatalogId: MenuIds.PatternFollow });
        Assert.Equal(new CatalogQuickCommandEntry(MenuIds.PatternFollow, catalogDefault), stored);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ExplicitStoredChoice_SurvivesAnUnrelatedApply()
    {
        using var scope = new PreferencesFileScope();
        List<QuickCommandEntry> pattern =
        [
            new CatalogQuickCommandEntry(MenuIds.PatternFollow, MenuCatalog.Get(MenuIds.PatternFollow).DefaultFlightRules),
            new CatalogQuickCommandEntry(MenuIds.TowerClearedToLand, null),
        ];
        new UserPreferences().SetQuickCommandList(AircraftSituation.Pattern, pattern);
        var vm = new SettingsViewModel();

        vm.TerminalFontSize++;
        vm.ApplyCommand.Execute(null);

        Assert.Equal(pattern, new UserPreferences().GetQuickCommandList(AircraftSituation.Pattern));
        var reopened = new SettingsViewModel();
        Select(reopened, AircraftSituation.Pattern);
        Assert.Equal(pattern[0], CatalogRow(reopened, MenuIds.PatternFollow).ToEntry());
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void FlightRulesOptions_CaptionTheCatalogDefault_AndCustomRowsHaveNoDefault()
    {
        using var scope = new PreferencesFileScope();
        Store(AircraftSituation.Pattern, MenuIds.PatternFollow, MenuIds.TowerClearedToLand);
        var vm = new SettingsViewModel();
        Select(vm, AircraftSituation.Pattern);

        Assert.Equal(
            ["Default (VFR only)", "Both", "IFR only", "VFR only"],
            CatalogRow(vm, MenuIds.PatternFollow).FlightRulesOptions.Select(o => o.Caption)
        );
        Assert.Equal("Default (Both)", CatalogRow(vm, MenuIds.TowerClearedToLand).FlightRulesOptions[0].Caption);

        vm.AddQuickCommandCustomEntry();
        QuickCommandEntryRow custom = vm.QuickCommandEntries[^1];
        Assert.Equal([MenuFlightRules.Both, MenuFlightRules.IfrOnly, MenuFlightRules.VfrOnly], custom.FlightRulesOptions.Select(o => o.Rules));
        Assert.Equal(MenuFlightRules.Both, custom.SelectedFlightRules.Rules);
        Assert.Equal("custom", custom.Family);
        Assert.Null(custom.Glyph);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void StripFlag_MarksTheFirstTenGlyphEntries_InListOrder()
    {
        using var scope = new PreferencesFileScope();
        Store(AircraftSituation.IfrEnroute, GlyphIds[..9]);
        var vm = new SettingsViewModel();
        Select(vm, AircraftSituation.IfrEnroute);
        Assert.All(vm.QuickCommandEntries, r => Assert.NotNull(r.Glyph));
        AddValidCustom(vm, "Custom");
        Assert.True(vm.TryAddQuickCommandCatalogEntry(GlyphIds[9]));
        Assert.True(vm.TryAddQuickCommandCatalogEntry(GlyphIds[10]));

        Assert.True(CatalogRow(vm, GlyphIds[9]).IsInStrip);
        Assert.False(CatalogRow(vm, GlyphIds[10]).IsInStrip);
        Assert.False(vm.QuickCommandEntries.Single(r => r.IsCustom).IsInStrip);
        Assert.Equal(QuickCommandGlyphs.StripCapacity, vm.QuickCommandEntries.Count(r => r.IsInStrip));

        vm.MoveQuickCommandEntry(vm.QuickCommandEntries.Count - 1, 0);

        Assert.True(CatalogRow(vm, GlyphIds[10]).IsInStrip);
        Assert.False(CatalogRow(vm, GlyphIds[9]).IsInStrip);
    }

    /// <summary>
    /// A stored list may name an action twice; the strip flag goes by position, so the second copy, past the tenth glyph
    /// row, is not in it.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public void StripFlag_GoesByPosition_WhenAnIdIsDuplicated()
    {
        using var scope = new PreferencesFileScope();
        Store(AircraftSituation.IfrEnroute, [.. GlyphIds[..10], GlyphIds[0]]);
        var vm = new SettingsViewModel();
        Select(vm, AircraftSituation.IfrEnroute);
        Assert.Equal(11, vm.QuickCommandEntries.Count);
        Assert.All(vm.QuickCommandEntries, r => Assert.NotNull(r.Glyph));

        Assert.Equal([.. Enumerable.Repeat(true, 10), false], vm.QuickCommandEntries.Select(r => r.IsInStrip));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void CustomRow_IsValidatedLikeTryItOut_AndAnInvalidRowBlocksApply()
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel();
        Select(vm, AircraftSituation.Taxiing);
        Assert.True(vm.CanApply);

        vm.AddQuickCommandCustomEntry();
        QuickCommandEntryRow row = vm.QuickCommandEntries[^1];
        Assert.Equal("Enter a label.", row.ValidationMessage);
        Assert.True(vm.HasQuickCommandErrors);
        Assert.False(vm.CanApply);
        Assert.Equal("Quick commands to fix: Taxiing (Quick commands)", vm.ApplyBlockedSummary);

        row.Label = "West";
        Assert.Equal("Enter a command.", row.ValidationMessage);

        row.CommandText = "ZZQQ 1";
        Assert.Equal("Command: Unrecognized command", row.ValidationMessage);

        row.CommandText = ValidCommand;
        Assert.Null(row.ValidationMessage);
        Assert.False(vm.HasQuickCommandErrors);
        Assert.True(vm.CanApply);
        Assert.Null(vm.ApplyBlockedSummary);

        row.GroundCommandText = "ZZQQ";
        Assert.Equal("Ground command: Unrecognized command", row.ValidationMessage);
        Assert.False(vm.CanApply);

        row.GroundCommandText = "HOLD";
        Assert.Null(row.ValidationMessage);
        Assert.True(vm.CanApply);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void CustomRow_BecomesValid_WhenItsMacroIsAdded()
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel();
        Select(vm, AircraftSituation.Taxiing);
        QuickCommandEntryRow row = AddValidCustom(vm, "West");
        row.CommandText = "!WD";
        Assert.Equal("Command: Unknown macro \"!WD\"", row.ValidationMessage);
        Assert.False(vm.CanApply);

        vm.AddMacroCommand.Execute(null);
        vm.MacroRows[^1].Name = "WD";
        vm.MacroRows[^1].Expansion = ValidCommand;

        Assert.Null(row.ValidationMessage);
        Assert.True(vm.CanApply);
        Assert.Null(vm.ApplyBlockedSummary);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void CustomRow_BecomesInvalid_WhenItsMacroIsRemoved()
    {
        using var scope = new PreferencesFileScope();
        var prefs = new UserPreferences();
        prefs.SetMacros([new MacroDefinition { Name = "WD", Expansion = ValidCommand }]);
        prefs.SetQuickCommandList(AircraftSituation.Taxiing, [new CustomQuickCommandEntry("West", "!WD", null, MenuFlightRules.Both)]);
        var vm = new SettingsViewModel();
        Select(vm, AircraftSituation.Taxiing);
        QuickCommandEntryRow row = Assert.Single(vm.QuickCommandEntries);
        Assert.Null(row.ValidationMessage);
        Assert.True(vm.CanApply);

        vm.MacroRows[0].RemoveCommand.Execute(null);

        Assert.Equal("Command: Unknown macro \"!WD\"", row.ValidationMessage);
        Assert.False(vm.CanApply);
        Assert.Equal("Quick commands to fix: Taxiing (Quick commands)", vm.ApplyBlockedSummary);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void CustomRow_ValidationFlips_WhenAVerbAliasChanges()
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel();
        Select(vm, AircraftSituation.Taxiing);
        QuickCommandEntryRow row = AddValidCustom(vm, "West");
        VerbMappingRow heading = vm.VerbMappings.Single(r => r.CommandType == CanonicalCommandType.FlyHeading);
        string aliases = heading.Aliases;

        heading.Aliases = "TURNTO";
        Assert.Equal("Command: Unrecognized command", row.ValidationMessage);
        Assert.False(vm.CanApply);

        heading.Aliases = aliases;
        Assert.Null(row.ValidationMessage);
        Assert.True(vm.CanApply);
    }

    /// <summary>Resetting the verbs rebuilds their rows without an alias edit, so the reset itself revalidates the custom rows.</summary>
    [AvaloniaFact(Timeout = 60_000)]
    public void ResetCommandVerbs_RevalidatesCustomRows()
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel();
        Select(vm, AircraftSituation.Taxiing);
        QuickCommandEntryRow row = AddValidCustom(vm, "West");
        vm.VerbMappings.Single(r => r.CommandType == CanonicalCommandType.FlyHeading).Aliases = "TURNTO";
        row.CommandText = "TURNTO 270";
        Assert.Null(row.ValidationMessage);

        vm.SelectedSection = SettingsSectionId.CommandVerbs;
        vm.ResetSectionCommand.Execute(null);

        Assert.Equal("Command: Unrecognized command", row.ValidationMessage);
        Assert.False(vm.CanApply);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void Apply_WritesTheChangedLists_AndLeavesAnUnchangedSituationUnstored()
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel();
        Select(vm, AircraftSituation.Taxiing);
        AddValidCustom(vm, "  West  ").GroundCommandText = "  ";
        List<QuickCommandEntry> taxiing = Staged(vm);
        Select(vm, AircraftSituation.Final);
        vm.RemoveQuickCommandEntry(vm.QuickCommandEntries[0]);
        List<QuickCommandEntry> final = Staged(vm);
        Select(vm, AircraftSituation.AtParking);
        vm.MoveQuickCommandEntry(0, 2);
        vm.MoveQuickCommandEntry(1, 0);

        vm.ApplyCommand.Execute(null);

        var stored = new UserPreferences();
        Assert.Equal(taxiing, stored.GetQuickCommandList(AircraftSituation.Taxiing));
        Assert.Equal(
            new CustomQuickCommandEntry("West", ValidCommand, null, MenuFlightRules.Both),
            stored.GetQuickCommandList(AircraftSituation.Taxiing)[^1]
        );
        Assert.Equal(final, stored.GetQuickCommandList(AircraftSituation.Final));
        Assert.Equal([AircraftSituation.Taxiing, AircraftSituation.Final], stored.QuickCommandOverrides.Keys.Order());
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void Cancel_WithoutApply_WritesNothing()
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel();
        Select(vm, AircraftSituation.Taxiing);
        AddValidCustom(vm, "West");
        vm.ResetAllQuickCommandSituations();
        Select(vm, AircraftSituation.Final);
        vm.RemoveQuickCommandEntry(vm.QuickCommandEntries[0]);

        Assert.Empty(new UserPreferences().QuickCommandOverrides);
    }

    private static List<string> OfferIds(SettingsViewModel vm) =>
        [.. vm.QuickCommandCatalogOffer.OfType<QuickCommandCatalogItem>().Select(i => i.Id)];

    [AvaloniaFact(Timeout = 60_000)]
    public void CatalogOffer_GroupsByFamily_AndKeepsTheSelectionAcrossRefills()
    {
        using var scope = new PreferencesFileScope();
        Store(AircraftSituation.Taxiing, MenuIds.GroundHoldPosition, MenuIds.GroundResumeTaxi);
        var vm = new SettingsViewModel();
        Select(vm, AircraftSituation.Taxiing);

        // One heading per family, each followed only by that family's actions, all of the available actions offered.
        List<object> offer = [.. vm.QuickCommandCatalogOffer];
        Assert.IsType<QuickCommandFamilyHeader>(offer[0]);
        Assert.Equal(
            vm.AvailableQuickCommandCatalogEntries.Select(i => i.Family).Distinct().Count(),
            offer.OfType<QuickCommandFamilyHeader>().Count()
        );
        string? family = null;
        foreach (object entry in offer)
        {
            family = (entry is QuickCommandCatalogItem item) ? (family ?? item.Family) : null;
            Assert.True((entry is QuickCommandFamilyHeader) || (((QuickCommandCatalogItem)entry).Family == family));
        }

        Assert.Equal(vm.AvailableQuickCommandCatalogEntries.Select(i => i.Id).Order(), OfferIds(vm).Order());
        Assert.Contains(offer.OfType<QuickCommandFamilyHeader>(), h => h.Title == "Tower");

        QuickCommandCatalogItem chosen = vm.QuickCommandCatalogOffer.OfType<QuickCommandCatalogItem>().ElementAt(3);
        vm.SelectedQuickCommandCatalogItem = chosen;

        // Removing an entry puts it back on offer, and adding another takes that one off: two refills.
        vm.RemoveQuickCommandEntry(vm.QuickCommandEntries[0]);
        Assert.Contains(MenuIds.GroundHoldPosition, OfferIds(vm));
        string other = OfferIds(vm).First(id => id != chosen.Id);
        Assert.True(vm.TryAddQuickCommandCatalogEntry(other));
        Assert.DoesNotContain(other, OfferIds(vm));

        Assert.Equal(chosen.Id, Assert.IsType<QuickCommandCatalogItem>(vm.SelectedQuickCommandCatalogItem).Id);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void CatalogSearch_NarrowsTheOffer_AndClearingItRestoresTheSelection()
    {
        using var scope = new PreferencesFileScope();
        Store(AircraftSituation.Taxiing, MenuIds.GroundHoldPosition);
        var vm = new SettingsViewModel();
        Select(vm, AircraftSituation.Taxiing);
        const string Query = "squawk";
        QuickCommandCatalogItem chosen = vm
            .QuickCommandCatalogOffer.OfType<QuickCommandCatalogItem>()
            .First(i =>
                !i.Label.Contains(Query, StringComparison.OrdinalIgnoreCase) && !i.Family.Contains(Query, StringComparison.OrdinalIgnoreCase)
            );
        vm.SelectedQuickCommandCatalogItem = chosen;
        int everything = OfferIds(vm).Count;

        vm.QuickCommandCatalogSearch = "  SQUAWK ";
        List<QuickCommandCatalogItem> narrowed = [.. vm.QuickCommandCatalogOffer.OfType<QuickCommandCatalogItem>()];
        Assert.NotEmpty(narrowed);
        Assert.True(narrowed.Count < everything);
        Assert.All(
            narrowed,
            i =>
                Assert.True(
                    i.Label.Contains(Query, StringComparison.OrdinalIgnoreCase) || i.Family.Contains(Query, StringComparison.OrdinalIgnoreCase)
                )
        );
        Assert.Null(vm.SelectedQuickCommandCatalogItem);
        Assert.False(vm.AddQuickCommandCatalogEntryCommand.CanExecute(null));

        vm.QuickCommandCatalogSearch = "";
        Assert.Equal(everything, OfferIds(vm).Count);
        Assert.Equal(chosen.Id, Assert.IsType<QuickCommandCatalogItem>(vm.SelectedQuickCommandCatalogItem).Id);

        // A search that keeps the chosen action on offer keeps it selected.
        vm.QuickCommandCatalogSearch = chosen.Family;
        Assert.Equal(chosen.Id, Assert.IsType<QuickCommandCatalogItem>(vm.SelectedQuickCommandCatalogItem).Id);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void AddCatalogEntryCommand_AddsTheSelection_AndOnlyACatalogActionEnablesIt()
    {
        using var scope = new PreferencesFileScope();
        Store(AircraftSituation.Taxiing, MenuIds.GroundHoldPosition);
        var vm = new SettingsViewModel();
        Select(vm, AircraftSituation.Taxiing);
        Assert.False(vm.AddQuickCommandCatalogEntryCommand.CanExecute(null));

        vm.SelectedQuickCommandCatalogItem = vm.QuickCommandCatalogOffer.OfType<QuickCommandFamilyHeader>().First();
        Assert.False(vm.AddQuickCommandCatalogEntryCommand.CanExecute(null));

        QuickCommandCatalogItem chosen = vm.QuickCommandCatalogOffer.OfType<QuickCommandCatalogItem>().ElementAt(2);
        vm.SelectedQuickCommandCatalogItem = chosen;
        Assert.True(vm.AddQuickCommandCatalogEntryCommand.CanExecute(null));
        vm.AddQuickCommandCatalogEntryCommand.Execute(null);

        Assert.Equal([MenuIds.GroundHoldPosition, chosen.Id], Ids(vm));
        Assert.Null(vm.SelectedQuickCommandCatalogItem);
        Assert.DoesNotContain(chosen.Id, OfferIds(vm));
        Assert.False(vm.AddQuickCommandCatalogEntryCommand.CanExecute(null));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ResetSituation_AndResetAll_AreStaged_AndCancelUndoesThem()
    {
        using var scope = new PreferencesFileScope();
        var prefs = new UserPreferences();
        prefs.SetQuickCommandList(AircraftSituation.Taxiing, [new CatalogQuickCommandEntry(MenuIds.GroundHoldPosition, null)]);
        prefs.SetQuickCommandList(AircraftSituation.Final, [new CatalogQuickCommandEntry(MenuIds.TowerGoAround, null)]);
        var vm = new SettingsViewModel();

        QuickCommandSituationRow taxiing = Select(vm, AircraftSituation.Taxiing);
        vm.ResetSelectedQuickCommandSituation();
        Assert.False(taxiing.IsChanged);
        Assert.Equal(QuickCommandDefaults.For(AircraftSituation.Taxiing), Staged(vm));
        Assert.True(vm.QuickCommandSituations.Single(r => r.Situation == AircraftSituation.Final).IsChanged);

        vm.ResetAllQuickCommandSituations();
        Assert.All(vm.QuickCommandSituations, r => Assert.False(r.IsChanged));
        Assert.Equal(2, new UserPreferences().QuickCommandOverrides.Count);

        var reopened = new SettingsViewModel();
        Assert.Equal(
            [AircraftSituation.Taxiing, AircraftSituation.Final],
            reopened.QuickCommandSituations.Where(r => r.IsChanged).Select(r => r.Situation)
        );
    }
}
