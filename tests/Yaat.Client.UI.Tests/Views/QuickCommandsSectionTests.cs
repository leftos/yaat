using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Settings;
using Yaat.Sim.Situation;
using Path = Avalonia.Controls.Shapes.Path;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Settings window's Quick Commands section as the instructor sees it: the situation list drives the entry list,
/// the add-command flyout keeps its choice while its list refills, rows reorder by dragging their handle, a divider marks
/// where the icon strip ends, catalog and custom rows show the controls each may edit, and the preview draws the strip the
/// menu would.
/// </summary>
public class QuickCommandsSectionTests
{
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

    private static string TextOnlyId => QuickCommandCatalog.Eligible.First(item => item.Glyph is null).Id;

    [AvaloniaFact(Timeout = 60_000)]
    public void QuickCommandsSection_SelectingASituation_ShowsItsEntriesInOrder()
    {
        using var scope = new PreferencesFileScope();
        Store(AircraftSituation.Taxiing, MenuIds.GroundHoldPosition, MenuIds.GroundResumeTaxi);
        Store(AircraftSituation.Final, MenuIds.TowerGoAround, MenuIds.SquawkCode, MenuIds.TowerClearedToLand);

        Run(
            (window, section) =>
            {
                SelectSituation(window, section, AircraftSituation.Final);

                string[] expected = [MenuIds.TowerGoAround, MenuIds.SquawkCode, MenuIds.TowerClearedToLand];
                Assert.Equal(expected, Rows(section).Select(r => r.CatalogId));
                Assert.Equal(
                    expected.Select(id => MenuCatalog.Get(id).Label),
                    Rows(section).Select(r => Single<TextBlock>(Container(section, r), "label-text").Text)
                );
            }
        );
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void QuickCommandsSection_AddCommandFlyout_AddAddsTheSelection()
    {
        using var scope = new PreferencesFileScope();
        Store(AircraftSituation.Taxiing, MenuIds.GroundHoldPosition, MenuIds.GroundResumeTaxi);

        Run(
            (window, section) =>
            {
                SelectSituation(window, section, AircraftSituation.Taxiing);
                Button add = section.FindControl<Button>("AddQuickCommandButton")!;
                add.Flyout!.ShowAt(add);
                try
                {
                    Settle(window);
                    ListBox list = section.FindControl<ListBox>("AddQuickCommandList")!;
                    Button confirm = ((Panel)list.Parent!).Children.OfType<Button>().Single();
                    Assert.False(confirm.IsEffectivelyEnabled);

                    // A family heading chosen by keyboard adds nothing.
                    list.SelectedItem = list.Items.OfType<QuickCommandFamilyHeader>().First();
                    Settle(window);
                    Assert.False(confirm.IsEffectivelyEnabled);

                    QuickCommandCatalogItem chosen = list.Items.OfType<QuickCommandCatalogItem>().ElementAt(3);
                    list.SelectedItem = chosen;
                    Settle(window);
                    Assert.True(confirm.IsEffectivelyEnabled);
                    ((IInvokeProvider)ControlAutomationPeer.CreatePeerForElement(confirm)).Invoke();
                    Settle(window);

                    Assert.Equal(chosen.Id, window.ViewModel.QuickCommandEntries[^1].CatalogId);
                    Assert.Equal(chosen.Id, Rows(section)[^1].CatalogId);
                    Assert.Null(list.SelectedItem);
                    Assert.DoesNotContain(list.Items.OfType<QuickCommandCatalogItem>(), item => item.Id == chosen.Id);
                }
                finally
                {
                    add.Flyout.Hide();
                }
            }
        );
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void QuickCommandsSection_DropAfterLastRow_MovesEntryToEnd()
    {
        using var scope = new PreferencesFileScope();
        string[] ids = [.. GlyphIds.Take(4)];
        Store(AircraftSituation.Taxiing, ids);

        Run(
            (window, section) =>
            {
                SelectSituation(window, section, AircraftSituation.Taxiing);
                List<QuickCommandEntryRow> rows = Rows(section);
                Control last = Container(section, rows[^1]);
                Point below = last.TranslatePoint(new Point(last.Bounds.Width / 2, last.Bounds.Height + 6), window)!.Value;

                window.MouseDrag(HandleCentre(window, section, rows[0]), below);

                Assert.Equal([ids[1], ids[2], ids[3], ids[0]], Rows(section).Select(r => r.CatalogId));
            }
        );
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void QuickCommandsSection_DropAtTop_MovesEntryToIndexZero()
    {
        using var scope = new PreferencesFileScope();
        string[] ids = [.. GlyphIds.Take(4)];
        Store(AircraftSituation.Taxiing, ids);

        Run(
            (window, section) =>
            {
                SelectSituation(window, section, AircraftSituation.Taxiing);
                List<QuickCommandEntryRow> rows = Rows(section);
                Control first = Container(section, rows[0]);
                Point top = first.TranslatePoint(new Point(first.Bounds.Width / 2, 2), window)!.Value;

                window.MouseDrag(HandleCentre(window, section, rows[3]), top);

                Assert.Equal([ids[3], ids[0], ids[1], ids[2]], Rows(section).Select(r => r.CatalogId));
            }
        );
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void QuickCommandsSection_CaptureLostMidDrag_KeepsOrderAndHidesIndicator()
    {
        using var scope = new PreferencesFileScope();
        string[] ids = [.. GlyphIds.Take(4)];
        Store(AircraftSituation.Taxiing, ids);

        Run(
            (window, section) =>
            {
                bool closed = false;
                window.Closed += (_, _) => closed = true;
                SelectSituation(window, section, AircraftSituation.Taxiing);
                List<QuickCommandEntryRow> rows = Rows(section);
                Control last = Container(section, rows[^1]);
                Point below = last.TranslatePoint(new Point(last.Bounds.Width / 2, last.Bounds.Height + 6), window)!.Value;
                Border indicator = section.FindControl<Border>("DropIndicator")!;

                window.MouseDown(HandleCentre(window, section, rows[0]), MouseButton.Left);
                Settle(window);
                window.MouseMove(below, RawInputModifiers.LeftMouseButton);
                Settle(window);
                Assert.True(indicator.IsVisible);

                // Escape releases the capture mid-drag; the release that follows moves nothing.
                window.DispatchKey(Key.Escape);
                Settle(window);
                Assert.False(indicator.IsVisible);
                window.MouseUp(below, MouseButton.Left);
                Settle(window);

                Assert.Equal(ids, Rows(section).Select(r => r.CatalogId));
                Assert.False(indicator.IsVisible);
                Assert.False(closed);
            }
        );
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void QuickCommandsSection_DividerFollowsTenthGlyphEntry()
    {
        using var scope = new PreferencesFileScope();

        // A text-only action third: the strip counts glyph entries, not positions, so the tenth glyph entry is at index 10.
        string[] ids = [.. GlyphIds.Take(2), TextOnlyId, .. GlyphIds.Skip(2)];
        Store(AircraftSituation.Taxiing, ids);

        Run(
            (window, section) =>
            {
                SelectSituation(window, section, AircraftSituation.Taxiing);
                List<object> items = [.. Entries(section).Items.OfType<object>()];

                int divider = Assert.Single(Enumerable.Range(0, items.Count), i => items[i] is QuickCommandStripDivider);
                Assert.Equal(GlyphIds[9], Assert.IsType<QuickCommandEntryRow>(items[divider - 1]).CatalogId);
                Assert.Equal(GlyphIds[10], Assert.IsType<QuickCommandEntryRow>(items[divider + 1]).CatalogId);
                Control shown = Entries(section).ContainerFromIndex(divider)!;
                Assert.True(shown.IsEffectivelyVisible);
                Assert.True(shown.Bounds.Height > 0);

                // Taking a glyph entry out of the strip moves the divider down with it.
                SettingsViewModel vm = window.ViewModel;
                vm.RemoveQuickCommandEntry(vm.QuickCommandEntries[0]);
                vm.AddQuickCommandCustomEntry();
                Settle(window);
                items = [.. Entries(section).Items.OfType<object>()];
                divider = Assert.Single(Enumerable.Range(0, items.Count), i => items[i] is QuickCommandStripDivider);
                Assert.Equal(GlyphIds[10], Assert.IsType<QuickCommandEntryRow>(items[divider - 1]).CatalogId);
            }
        );
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void QuickCommandsSection_TenGlyphRowsWithNothingAfter_ShowNoDivider()
    {
        using var scope = new PreferencesFileScope();
        Store(AircraftSituation.Taxiing, [.. GlyphIds.Take(QuickCommandGlyphs.StripCapacity)]);

        Run(
            (window, section) =>
            {
                SelectSituation(window, section, AircraftSituation.Taxiing);

                Assert.Equal(QuickCommandGlyphs.StripCapacity, Rows(section).Count(r => r.IsInStrip));
                Assert.DoesNotContain(Entries(section).Items.OfType<object>(), item => item is QuickCommandStripDivider);
            }
        );
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void QuickCommandsSection_CatalogRowDefaultCaption_NamesTheCatalogRule()
    {
        using var scope = new PreferencesFileScope();
        QuickCommandCatalogItem vfrOnly = QuickCommandCatalog.Eligible.First(item => item.DefaultFlightRules == MenuFlightRules.VfrOnly);
        Store(AircraftSituation.Pattern, vfrOnly.Id);

        Run(
            (window, section) =>
            {
                SelectSituation(window, section, AircraftSituation.Pattern);
                ComboBox rules = Single<ComboBox>(Container(section, Rows(section)[0]), "flight-rules");

                Assert.Equal("Default (VFR only)", Assert.IsType<QuickCommandFlightRulesOption>(rules.SelectedItem).Caption);
                Assert.Contains(rules.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Default (VFR only)");
                Assert.Equal(
                    ["Default (VFR only)", "Both", "IFR only", "VFR only"],
                    rules.Items.OfType<QuickCommandFlightRulesOption>().Select(o => o.Caption)
                );
            }
        );
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void QuickCommandsSection_LabelEditableOnlyForCustomRows()
    {
        using var scope = new PreferencesFileScope();
        Store(AircraftSituation.Taxiing, MenuIds.GroundHoldPosition);

        Run(
            (window, section) =>
            {
                SelectSituation(window, section, AircraftSituation.Taxiing);
                window.ViewModel.AddQuickCommandCustomEntry();
                Settle(window);
                List<QuickCommandEntryRow> rows = Rows(section);

                Control catalog = Container(section, rows[0]);
                Assert.False(Single<TextBox>(catalog, "label-editor").IsEffectivelyVisible);
                Assert.True(Single<TextBlock>(catalog, "label-text").IsEffectivelyVisible);

                Control custom = Container(section, rows[1]);
                TextBox editor = Single<TextBox>(custom, "label-editor");
                Assert.True(editor.IsEffectivelyVisible);
                Assert.False(editor.IsReadOnly);
                Assert.False(Single<TextBlock>(custom, "label-text").IsEffectivelyVisible);
                editor.Text = "West";
                Settle(window);
                Assert.Equal("West", rows[1].Label);
            }
        );
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void QuickCommandsSection_InvalidCustomRow_ShowsWarning()
    {
        using var scope = new PreferencesFileScope();
        Store(AircraftSituation.Taxiing, MenuIds.GroundHoldPosition);

        Run(
            (window, section) =>
            {
                SelectSituation(window, section, AircraftSituation.Taxiing);
                window.ViewModel.AddQuickCommandCustomEntry();
                Settle(window);
                QuickCommandEntryRow row = Rows(section)[1];
                TextBlock warning = Single<TextBlock>(Container(section, row), "row-warning");

                Assert.True(warning.IsEffectivelyVisible);
                Assert.Equal($"⚠ {row.ValidationMessage}", warning.Text);
                Assert.False(Single<TextBlock>(Container(section, Rows(section)[0]), "row-warning").IsEffectivelyVisible);

                row.Label = "West";
                row.CommandText = "FH 270";
                Settle(window);
                Assert.False(warning.IsEffectivelyVisible);
            }
        );
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void QuickCommandsSection_Preview_DrawsFirstTenGlyphEntriesInOrder()
    {
        using var scope = new PreferencesFileScope();
        string[] ids = [.. GlyphIds.Take(2), TextOnlyId, .. GlyphIds.Skip(2)];
        Store(AircraftSituation.Taxiing, ids);

        Run(
            (window, section) =>
            {
                SelectSituation(window, section, AircraftSituation.Taxiing);
                QuickCommandResolution split = QuickCommandGlyphs.Split([.. ids.Select(MenuCatalog.Get)]);
                List<Border> cells =
                [
                    .. section
                        .FindControl<Border>("PreviewStripHost")!
                        .GetLogicalDescendants()
                        .OfType<Border>()
                        .Where(b => b.Classes.Contains("preview-cell")),
                ];

                Assert.Equal(split.Strip.Select(item => item.Entry.Id), cells.Select(c => (string)c.Tag!));
                for (int i = 0; i < cells.Count; i++)
                {
                    Path glyph = Assert.Single(cells[i].GetLogicalDescendants().OfType<Path>());
                    Color expected = QuickCommandStrip.FamilyColor(split.Strip[i].Glyph.Family);
                    Assert.Equal(expected, Assert.IsType<SolidColorBrush>(glyph.Stroke).Color);
                }

                List<string?> text = [.. section.FindControl<Panel>("PreviewTextEntries")!.Children.OfType<TextBlock>().Select(t => t.Text)];
                Assert.Equal(split.Text.Select(entry => entry.Label), text);
            }
        );
    }

    private static void Run(Action<SettingsWindow, QuickCommandsSection> test)
    {
        var window = new SettingsWindow();
        window.SelectSection(SettingsSectionId.QuickCommands);
        window.ShowAndRunLayout();
        try
        {
            test(window, Assert.IsType<QuickCommandsSection>(window.SectionView(SettingsSectionId.QuickCommands)));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // Stores the catalog actions as the situation's list before the window is built, so a test does not lean on the defaults.
    private static void Store(AircraftSituation situation, params string[] catalogIds) =>
        new UserPreferences().SetQuickCommandList(situation, [.. catalogIds.Select(id => new CatalogQuickCommandEntry(id, null))]);

    private static void SelectSituation(SettingsWindow window, QuickCommandsSection section, AircraftSituation situation)
    {
        ListBox situations = section.FindControl<ListBox>("SituationList")!;
        situations.SelectedItem = situations.Items.OfType<QuickCommandSituationRow>().Single(r => r.Situation == situation);
        Settle(window);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static ItemsControl Entries(QuickCommandsSection section) => section.FindControl<ItemsControl>("EntryList")!;

    private static List<QuickCommandEntryRow> Rows(QuickCommandsSection section) => [.. Entries(section).Items.OfType<QuickCommandEntryRow>()];

    private static Control Container(QuickCommandsSection section, QuickCommandEntryRow row) =>
        Entries(section).ContainerFromItem(row) ?? throw new InvalidOperationException($"No container for {row.Label}");

    private static T Single<T>(Control container, string styleClass)
        where T : Control => Assert.Single(container.GetVisualDescendants().OfType<T>(), c => c.Classes.Contains(styleClass));

    private static Point HandleCentre(Window window, QuickCommandsSection section, QuickCommandEntryRow row)
    {
        Border handle = Single<Border>(Container(section, row), "drag-handle");
        return handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
    }
}
