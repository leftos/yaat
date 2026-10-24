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
/// the add-command flyout keeps its choice while its list refills, rows reorder by dragging their handle, catalog and
/// custom rows show the controls each may edit, and the preview draws the strip the menu would, grouped by category.
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
        MenuIds.SquawkNormal,
        MenuIds.SquawkAssigned,
        MenuIds.ProceduresCrossFix,
        MenuIds.ProceduresDescendViaStar,
        MenuIds.ApproachReportFieldInSight,
        MenuIds.HoldPattern,
    ];

    private static string TextOnlyId => QuickCommandCatalog.Eligible.First(item => item.Glyph is null).Id;

    [AvaloniaFact(Timeout = 60_000)]
    public void QuickCommandsSection_SelectingASituation_ShowsItsEntriesInOrder()
    {
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
    public void QuickCommandsSection_CatalogRowDefaultCaption_NamesTheCatalogRule()
    {
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
    public void QuickCommandsSection_Preview_DrawsEveryGlyphEntry_GroupedByCategoryAsTheMenuDoes()
    {
        string[] ids = [.. GlyphIds.Take(2), TextOnlyId, .. GlyphIds.Skip(2)];
        Store(AircraftSituation.Taxiing, ids);

        Run(
            (window, section) =>
            {
                SelectSituation(window, section, AircraftSituation.Taxiing);
                QuickCommandResolution split = QuickCommandGlyphs.Split([.. ids.Select(MenuCatalog.Get)]);
                IReadOnlyList<QuickCommandStripRow<string>> layout = QuickCommandStripLayout.Pack(
                    [.. split.Strip.Select(item => item.Entry.Id)],
                    MenuCategories.Of
                );
                Border host = section.FindControl<Border>("PreviewStripHost")!;
                List<Border> cells = [.. host.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("preview-cell"))];

                Assert.Equal(layout.SelectMany(row => row.Segments).SelectMany(segment => segment.Items), cells.Select(c => (string)c.Tag!));
                Assert.Equal(GlyphIds.Length, cells.Count);
                Assert.Equal(layout.Count, Assert.IsType<StackPanel>(host.Child).Children.Count);
                Assert.Equal(
                    layout
                        .SelectMany(row => row.Segments)
                        .Where(segment => segment.ShowsLabel)
                        .Select(segment => MenuCategories.Label(segment.Category!.Value).ToUpperInvariant()),
                    host.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text)
                );
                foreach (Border cell in cells)
                {
                    Path glyph = Assert.Single(cell.GetLogicalDescendants().OfType<Path>());
                    Color expected = QuickCommandStrip.FamilyColor(QuickCommandGlyphs.For((string)cell.Tag!)!.Family);
                    Assert.Equal(expected, Assert.IsType<SolidColorBrush>(glyph.Stroke).Color);
                }

                List<string?> text = [.. section.FindControl<Panel>("PreviewTextEntries")!.Children.OfType<TextBlock>().Select(t => t.Text)];
                Assert.Equal(split.Text.Select(entry => entry.Label), text);
            }
        );
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void QuickCommandsSection_Preview_FollowsTheShowIconsCheckbox()
    {
        string[] ids =
        [
            MenuIds.TrackTrack,
            MenuIds.HeadingFly,
            MenuIds.CoordinationReleaseHeld,
            MenuIds.SimControlWarp,
            MenuIds.TowerClearedToLand,
            MenuIds.AltitudeMaintain,
        ];
        Store(AircraftSituation.Taxiing, ids);

        Run(
            (window, section) =>
            {
                SelectSituation(window, section, AircraftSituation.Taxiing);
                window.ViewModel.AddQuickCommandCustomEntry();
                Settle(window);
                Border strip = section.FindControl<Border>("PreviewStripHost")!;
                Panel text = section.FindControl<Panel>("PreviewTextEntries")!;
                CheckBox showIcons = section.FindControl<CheckBox>("ShowQuickCommandIconsCheckBox")!;
                // A category label line reads as [label].
                List<string?> Text() =>
                    [.. text.Children.OfType<TextBlock>().Select(t => t.Classes.Contains("preview-category") ? $"[{t.Text}]" : t.Text)];
                string Label(string id) => MenuCatalog.Get(id).Label;
                Assert.True(showIcons.IsChecked);
                Assert.True(strip.IsVisible);
                Assert.DoesNotContain(Label(MenuIds.HeadingFly), Text());

                showIcons.IsChecked = false;
                Settle(window);

                // As the menu draws them: each category's rows under its label in category order, the release row among
                // Track and coordination's in list order, then the custom row, in no category, after a gap.
                Assert.False(strip.IsVisible);
                Assert.Null(strip.Child);
                Assert.Equal(
                    [
                        $"[{MenuCategories.Label(MenuCategory.Runway)}]",
                        Label(MenuIds.TowerClearedToLand),
                        $"[{MenuCategories.Label(MenuCategory.Vectors)}]",
                        Label(MenuIds.HeadingFly),
                        Label(MenuIds.AltitudeMaintain),
                        $"[{MenuCategories.Label(MenuCategory.TrackAndCoordination)}]",
                        Label(MenuIds.TrackTrack),
                        Label(MenuIds.CoordinationReleaseHeld),
                        $"[{MenuCategories.Label(MenuCategory.Sim)}]",
                        Label(MenuIds.SimControlWarp),
                        "(no label)",
                    ],
                    Text()
                );
                List<TextBlock> rows = [.. text.Children.OfType<TextBlock>()];
                Assert.True(rows[^1].Margin.Top > rows[^2].Margin.Top);

                showIcons.IsChecked = true;
                Settle(window);

                Assert.True(strip.IsVisible);
                Assert.DoesNotContain(Label(MenuIds.HeadingFly), Text());
                Assert.DoesNotContain(text.Children.OfType<TextBlock>(), t => t.Classes.Contains("preview-category"));
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
