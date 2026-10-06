using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
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

    private static QuickCommandSituationRow Select(SettingsViewModel vm, AircraftSituation situation)
    {
        QuickCommandSituationRow row = vm.QuickCommandSituations.Single(r => r.Situation == situation);
        vm.SelectedQuickCommandSituation = row;
        return row;
    }

    private static QuickCommandEntryRow CatalogRow(SettingsViewModel vm, string catalogId) =>
        vm.QuickCommandEntries.Single(r => r.CatalogId == catalogId);

    private static List<QuickCommandEntry> Staged(SettingsViewModel vm) => [.. vm.QuickCommandEntries.Select(r => r.ToEntry())];

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
        Assert.Equal("West", vm.QuickCommandEntries[1].Label);
        Assert.Equal("HOLD", vm.QuickCommandEntries[1].GroundCommandText);
        Assert.Null(vm.QuickCommandEntries[1].ValidationMessage);

        Assert.False(Select(vm, AircraftSituation.Final).IsChanged);
        Assert.Equal(QuickCommandDefaults.For(AircraftSituation.Final), Staged(vm));
        QuickCommandEntryRow land = CatalogRow(vm, MenuIds.TowerClearedToLand);
        Assert.Equal(MenuCatalog.Get(MenuIds.TowerClearedToLand).Label, land.Label);
        Assert.Equal("tower", land.Family);
        Assert.Equal(QuickCommandGlyphs.For(MenuIds.TowerClearedToLand), land.Glyph);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void AddRemoveMove_ChangeOnlyTheStagedList_AndIsChanged()
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel();
        QuickCommandSituationRow final = Select(vm, AircraftSituation.Final);

        Assert.True(vm.TryAddQuickCommandCatalogEntry(MenuIds.GroundHoldPosition));
        Assert.Equal(MenuIds.GroundHoldPosition, vm.QuickCommandEntries[^1].CatalogId);
        Assert.True(final.IsChanged);
        Assert.DoesNotContain(vm.AvailableQuickCommandCatalogEntries, item => item.Id == MenuIds.GroundHoldPosition);

        vm.RemoveQuickCommandEntry(vm.QuickCommandEntries[^1]);
        Assert.False(final.IsChanged);
        Assert.Contains(vm.AvailableQuickCommandCatalogEntries, item => item.Id == MenuIds.GroundHoldPosition);

        vm.MoveQuickCommandEntry(0, 1);
        Assert.Equal(MenuIds.TowerGoAround, vm.QuickCommandEntries[0].CatalogId);
        Assert.True(final.IsChanged);

        Select(vm, AircraftSituation.Taxiing);
        Select(vm, AircraftSituation.Final);
        Assert.Equal(MenuIds.TowerGoAround, vm.QuickCommandEntries[0].CatalogId);
        Assert.False(vm.QuickCommandSituations.Single(r => r.Situation == AircraftSituation.Taxiing).IsChanged);

        vm.MoveQuickCommandEntry(1, 0);
        Assert.False(final.IsChanged);
        Assert.Equal(QuickCommandDefaults.For(AircraftSituation.Final), new UserPreferences().GetQuickCommandList(AircraftSituation.Final));
        Assert.Throws<ArgumentOutOfRangeException>(() => vm.MoveQuickCommandEntry(0, vm.QuickCommandEntries.Count));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void AddCatalogEntry_RefusesADuplicateOrAnIneligibleId()
    {
        using var scope = new PreferencesFileScope();
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

    [AvaloniaFact(Timeout = 60_000)]
    public void FlightRulesChoice_EqualToTheCatalogDefault_StoresNull()
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel();
        QuickCommandSituationRow pattern = Select(vm, AircraftSituation.Pattern);
        QuickCommandEntryRow follow = CatalogRow(vm, MenuIds.PatternFollow);
        Assert.Null(follow.SelectedFlightRules.Rules);

        follow.SelectedFlightRules = follow.FlightRulesOptions.Single(o => o.Rules == MenuFlightRules.VfrOnly);
        Assert.Null(Assert.IsType<CatalogQuickCommandEntry>(follow.ToEntry()).FlightRules);
        Assert.False(pattern.IsChanged);

        follow.SelectedFlightRules = follow.FlightRulesOptions.Single(o => o.Rules == MenuFlightRules.IfrOnly);
        Assert.Equal(MenuFlightRules.IfrOnly, Assert.IsType<CatalogQuickCommandEntry>(follow.ToEntry()).FlightRules);
        Assert.True(pattern.IsChanged);

        vm.ApplyCommand.Execute(null);

        QuickCommandEntry stored = new UserPreferences()
            .GetQuickCommandList(AircraftSituation.Pattern)
            .Single(e => e is CatalogQuickCommandEntry { CatalogId: MenuIds.PatternFollow });
        Assert.Equal(new CatalogQuickCommandEntry(MenuIds.PatternFollow, MenuFlightRules.IfrOnly), stored);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void FlightRulesOptions_CaptionTheCatalogDefault_AndCustomRowsHaveNoDefault()
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel();
        Select(vm, AircraftSituation.Pattern);

        Assert.Equal(
            ["Default (VFR only)", "IFR and VFR", "IFR only", "VFR only"],
            CatalogRow(vm, MenuIds.PatternFollow).FlightRulesOptions.Select(o => o.Caption)
        );
        Assert.Equal("Default (IFR and VFR)", CatalogRow(vm, MenuIds.TowerClearedToLand).FlightRulesOptions[0].Caption);

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
        var vm = new SettingsViewModel();
        Select(vm, AircraftSituation.Pattern);
        Assert.Equal(9, vm.QuickCommandEntries.Count(r => r.Glyph is not null));
        AddValidCustom(vm, "Custom");
        Assert.True(vm.TryAddQuickCommandCatalogEntry(MenuIds.HeadingFly));
        Assert.True(vm.TryAddQuickCommandCatalogEntry(MenuIds.AltitudeMaintain));

        Assert.True(CatalogRow(vm, MenuIds.HeadingFly).IsInStrip);
        Assert.False(CatalogRow(vm, MenuIds.AltitudeMaintain).IsInStrip);
        Assert.False(CatalogRow(vm, MenuIds.PatternFollow).IsInStrip);
        Assert.False(vm.QuickCommandEntries.Single(r => r.IsCustom).IsInStrip);
        Assert.Equal(QuickCommandGlyphs.StripCapacity, vm.QuickCommandEntries.Count(r => r.IsInStrip));

        vm.MoveQuickCommandEntry(vm.QuickCommandEntries.Count - 1, 0);

        Assert.True(CatalogRow(vm, MenuIds.AltitudeMaintain).IsInStrip);
        Assert.False(CatalogRow(vm, MenuIds.HeadingFly).IsInStrip);
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
        vm.MoveQuickCommandEntry(0, 1);
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
