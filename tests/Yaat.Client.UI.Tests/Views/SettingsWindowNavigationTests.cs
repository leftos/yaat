using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Settings;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Settings sidebar: every section has exactly one selectable row, selecting a section shows its
/// view, group headers cannot be selected, and each row carries its UI Automation id and name.
/// </summary>
public class SettingsWindowNavigationTests
{
    private static readonly Dictionary<SettingsSectionId, Type> SectionViews = new()
    {
        [SettingsSectionId.General] = typeof(GeneralSection),
        [SettingsSectionId.Appearance] = typeof(AppearanceSection),
        [SettingsSectionId.ScenarioDefaults] = typeof(ScenarioDefaultsSection),
        [SettingsSectionId.Radar] = typeof(RadarSection),
        [SettingsSectionId.Ground] = typeof(GroundSection),
        [SettingsSectionId.AircraftList] = typeof(AircraftListSection),
        [SettingsSectionId.StripsAndTdls] = typeof(StripsAndTdlsSection),
        [SettingsSectionId.Terminal] = typeof(TerminalSection),
        [SettingsSectionId.CommandInput] = typeof(CommandInputSection),
        [SettingsSectionId.CommandVerbs] = typeof(CommandVerbsSection),
        [SettingsSectionId.Macros] = typeof(MacrosSection),
        [SettingsSectionId.Keys] = typeof(KeysSection),
        [SettingsSectionId.Speech] = typeof(SpeechSection),
        [SettingsSectionId.AudioDevices] = typeof(AudioDevicesSection),
        [SettingsSectionId.ServerAdmin] = typeof(ServerAdminSection),
    };

    [Fact]
    public void EverySection_HasExactlyOneSelectableNavItem()
    {
        foreach (SettingsSectionId id in Enum.GetValues<SettingsSectionId>())
        {
            Assert.Single(SettingsNavigation.Items, item => item.Id == id);
        }

        Assert.Equal(Enum.GetValues<SettingsSectionId>().Length, SectionViews.Count);
    }

    [AvaloniaTheory(Timeout = 60_000)]
    [InlineData(SettingsSectionId.Keys, "Push-to-talk key → Speech", SettingsSectionId.Speech)]
    [InlineData(SettingsSectionId.Keys, "Always on top → General", SettingsSectionId.General)]
    [InlineData(SettingsSectionId.Speech, "Microphone → Audio devices", SettingsSectionId.AudioDevices)]
    public void SectionLink_OpensItsTargetSection(SettingsSectionId from, string link, SettingsSectionId target)
    {
        var window = new SettingsWindow();
        window.SelectSection(from);
        window.ShowAndRunLayout();

        try
        {
            Button button = window
                .FindControl<ContentControl>("SectionHost")!
                .GetLogicalDescendants()
                .OfType<Button>()
                .Single(b => b.Classes.Contains("section-link") && (b.Content as string) == link);

            // A link inside a collapsed group (the Speech section's speech-to-text group) is only reachable expanded.
            foreach (Expander group in button.GetLogicalAncestors().OfType<Expander>())
            {
                group.IsExpanded = true;
            }

            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.IsType(SectionViews[target], window.FindControl<ContentControl>("SectionHost")!.Content);
            Assert.Equal(target, Assert.IsType<SettingsNavItem>(window.FindControl<ListBox>("SectionNav")!.SelectedItem).Id);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void SelectSection_ShowsThatSectionsView_ForEverySection()
    {
        var window = new SettingsWindow();
        window.SelectSection(SettingsSectionId.Speech);
        window.ShowAndRunLayout();

        try
        {
            ContentControl host = window.FindControl<ContentControl>("SectionHost")!;
            ListBox nav = window.FindControl<ListBox>("SectionNav")!;
            Assert.IsType<SpeechSection>(host.Content);

            foreach (SettingsSectionId id in Enum.GetValues<SettingsSectionId>())
            {
                window.SelectSection(id);
                Dispatcher.UIThread.RunJobs();

                Assert.IsType(SectionViews[id], host.Content);
                Assert.Equal(id, Assert.IsType<SettingsNavItem>(nav.SelectedItem).Id);
            }
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void NavRows_CarryAutomationIds_AndHeadersCannotBeSelected()
    {
        var window = new SettingsWindow();
        window.ShowAndRunLayout();

        try
        {
            ListBox nav = window.FindControl<ListBox>("SectionNav")!;
            for (int i = 0; i < SettingsNavigation.Items.Count; i++)
            {
                SettingsNavItem item = SettingsNavigation.Items[i];
                Control container = RealizedContainer(nav, i);
                Assert.Equal(item.Title, AutomationProperties.GetName(container));
                if (item.Id is { } id)
                {
                    Assert.Equal($"SettingsNav.{id}", AutomationProperties.GetAutomationId(container));
                    Assert.True(container.IsHitTestVisible);
                }
                else
                {
                    Assert.False(container.IsHitTestVisible);
                    Assert.False(container.Focusable);
                }
            }

            window.SelectSection(SettingsSectionId.Ground);
            Dispatcher.UIThread.RunJobs();
            nav.SelectedItem = SettingsNavigation.Items.First(item => item.IsHeader && (item.Group == "Views"));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(SettingsSectionId.Ground, Assert.IsType<SettingsNavItem>(nav.SelectedItem).Id);
            Assert.IsType<GroundSection>(window.FindControl<ContentControl>("SectionHost")!.Content);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void SearchQuery_FiltersTheSidebarToMatchingSections_WithCounts_AndKeepsAutomationIds()
    {
        var window = new SettingsWindow();
        window.ShowAndRunLayout();

        try
        {
            ListBox nav = window.FindControl<ListBox>("SectionNav")!;
            Search(window, "font");

            SettingsSearchResult expected = SettingsSearch.Run("font", SettingsSearchCatalog.Entries);
            List<SettingsNavItem> rows = [.. nav.Items.OfType<SettingsNavItem>()];
            SettingsSectionId[] shown = [.. rows.Select(r => r.Id).OfType<SettingsSectionId>()];
            SettingsSectionId[] matching =
            [
                .. SettingsNavigation.Items.Select(i => i.Id).OfType<SettingsSectionId>().Where(id => expected.CountFor(id) > 0),
            ];
            Assert.Equal(matching, shown);
            Assert.Contains(SettingsSectionId.Appearance, shown);
            Assert.Contains(SettingsSectionId.Radar, shown);
            Assert.DoesNotContain(SettingsSectionId.Speech, shown);

            for (int i = 0; i < rows.Count; i++)
            {
                SettingsNavItem row = rows[i];
                Control container = RealizedContainer(nav, i);
                if (row.Id is { } id)
                {
                    int count = expected.CountFor(id);
                    Assert.Equal($"{row.Title}, {count} {(count == 1 ? "match" : "matches")}", AutomationProperties.GetName(container));
                    Assert.Equal($"SettingsNav.{id}", AutomationProperties.GetAutomationId(container));
                    TextBlock badge = container.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("nav-count"));
                    Assert.True(badge.IsEffectivelyVisible);
                    Assert.Equal(expected.CountFor(id).ToString(System.Globalization.CultureInfo.InvariantCulture), badge.Text);
                }
                else
                {
                    Assert.Equal(row.Title, AutomationProperties.GetName(container));

                    // A group header shows only with one of its sections under it.
                    Assert.True(i + 1 < rows.Count);
                    Assert.Equal(row.Group, rows[i + 1].Group);
                    Assert.False(rows[i + 1].IsHeader);
                    Assert.Null(AutomationProperties.GetAutomationId(container));
                }
            }
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void SelectingAFilteredSection_HighlightsItsFirstMatch_AndANewQueryClearsIt()
    {
        var window = new SettingsWindow();
        window.ShowAndRunLayout();

        try
        {
            ContentControl host = window.FindControl<ContentControl>("SectionHost")!;
            Search(window, "ptt");

            // Keys matches only through its link to the push-to-talk key, so the link is what lights up.
            window.SelectSection(SettingsSectionId.Keys);
            Dispatcher.UIThread.RunJobs();
            Button link = Assert.IsType<Button>(Assert.Single(Highlighted(host)));
            Assert.Contains("section-link", link.Classes);
            Assert.Equal("Push-to-talk key → Speech", link.Content);

            // Speech's first match sits in the speech-to-text group, which opens to show it.
            window.SelectSection(SettingsSectionId.Speech);
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<SpeechSection>(host.Content);
            CheckBox hit = Assert.IsType<CheckBox>(Assert.Single(Highlighted(host)));
            Assert.Equal("Auto-focus command input after PTT", hit.Content);
            Assert.All(hit.GetLogicalAncestors().OfType<Expander>(), group => Assert.True(group.IsExpanded));

            Search(window, "ptt key");
            Assert.DoesNotContain(hit, Highlighted(host));

            Search(window, "");
            Assert.Empty(Highlighted(host));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void EscapeInTheSearchBox_ClearsTheQuery_AndKeepsTheWindowOpen()
    {
        var window = new SettingsWindow();
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.ShowAndRunLayout();

        try
        {
            TextBox box = window.FindControl<TextBox>("SettingsSearchBox")!;
            Search(window, "font");
            box.Focus();
            Dispatcher.UIThread.RunJobs();

            window.DispatchKey(Avalonia.Input.Key.Escape);

            Assert.False(closed);
            Assert.True(string.IsNullOrEmpty(box.Text));
            Assert.Equal(SettingsNavigation.Items.Count, window.FindControl<ListBox>("SectionNav")!.ItemCount);

            // With nothing to clear, Escape cancels the window as before.
            window.DispatchKey(Avalonia.Input.Key.Escape);
            Assert.True(closed);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void EnterInTheSearchBox_MovesToTheMatch_AndKeepsTheWindowOpen()
    {
        var window = new SettingsWindow();
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.ShowAndRunLayout();

        try
        {
            TextBox box = window.FindControl<TextBox>("SettingsSearchBox")!;
            TextBlock noMatch = window.FindControl<TextBlock>("NoMatchText")!;

            // Nothing matches: the sidebar says so, and Enter stays in the box.
            Search(window, "xyzzy");
            Assert.True(noMatch.IsVisible);
            Assert.Equal(0, window.FindControl<ListBox>("SectionNav")!.ItemCount);
            box.Focus();
            Dispatcher.UIThread.RunJobs();
            window.DispatchKey(Avalonia.Input.Key.Enter);
            Assert.False(closed);
            Assert.Same(box, window.FocusManager!.GetFocusedElement());

            // A match: Enter moves focus to the highlighted setting instead of pressing OK.
            Search(window, "admin mode");
            Assert.False(noMatch.IsVisible);
            box.Focus();
            Dispatcher.UIThread.RunJobs();
            window.DispatchKey(Avalonia.Input.Key.Enter);

            Assert.False(closed);
            CheckBox adminMode = Assert.IsType<CheckBox>(window.FocusManager!.GetFocusedElement());
            Assert.Equal("Server Admin Mode", adminMode.Content);
            Assert.Contains("search-hit", adminMode.Classes);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void HiddenSettings_AreNotCounted_OrHighlighted()
    {
        var window = new SettingsWindow();
        window.ViewModel.IsAdminMode = false;
        window.ViewModel.AutoAcceptEnabled = false;
        window.ShowAndRunLayout();

        try
        {
            ListBox nav = window.FindControl<ListBox>("SectionNav")!;
            ContentControl host = window.FindControl<ContentControl>("SectionHost")!;

            // The admin password shows only in admin mode.
            Search(window, "password");
            Assert.DoesNotContain(nav.Items.OfType<SettingsNavItem>(), row => row.Id == SettingsSectionId.ServerAdmin);

            window.ViewModel.IsAdminMode = true;
            Search(window, "");
            Search(window, "password");
            Assert.Contains(nav.Items.OfType<SettingsNavItem>(), row => row.Id == SettingsSectionId.ServerAdmin);

            // The auto-accept delay is hidden while auto-accept is off: "delay" counts and lights up the command run delay.
            Search(window, "delay");
            SettingsNavItem scenarioDefaults = Assert.Single(
                nav.Items.OfType<SettingsNavItem>(),
                row => row.Id == SettingsSectionId.ScenarioDefaults
            );
            Assert.Equal(2, scenarioDefaults.MatchCount);
            window.SelectSection(SettingsSectionId.ScenarioDefaults);
            Dispatcher.UIThread.RunJobs();
            StackPanel hit = Assert.IsType<StackPanel>(Assert.Single(Highlighted(host)));
            Assert.Contains(hit.Children.OfType<TextBlock>(), text => text.Text == "Min");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // The window's own sections, so the Keys section's rows (built from its item template) are there to find even though
    // the section on show is General.
    // SettingsWindow.OnOpened lays the Keys section out once as the window opens, which realises its keybind rows.
    [AvaloniaFact(Timeout = 60_000)]
    public void EveryCatalogEntry_ResolvesToItsOwnControlInItsSection()
    {
        var window = new SettingsWindow();
        window.ShowAndRunLayout();

        try
        {
            var unresolved = new List<string>();
            var resolved = new List<Control>();
            foreach (SettingsSearchEntry entry in SettingsSearchCatalog.Entries)
            {
                if (SettingsWindow.FindLabel(window.SectionView(entry.Section), entry) is { } label)
                {
                    resolved.Add(label);
                }
                else
                {
                    unresolved.Add($"{entry.Section}: {entry.Label} (after {entry.Within})");
                }
            }

            Assert.Empty(unresolved);
            Assert.Equal(resolved.Count, resolved.Distinct(ReferenceEqualityComparer.Instance).Count());
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void KeybindRows_AreFoundByTheirOwnWords_AndByHotkey_BeforeAndAfterTheKeysSectionIsOpened()
    {
        var window = new SettingsWindow();
        window.ShowAndRunLayout();

        try
        {
            ListBox nav = window.FindControl<ListBox>("SectionNav")!;
            ContentControl host = window.FindControl<ContentControl>("SectionHost")!;
            TextBox box = window.FindControl<TextBox>("SettingsSearchBox")!;

            // The Keys section has not been opened, yet its six pop-out rows count.
            Search(window, "pop out");
            Assert.Equal(6, MatchCount(nav, SettingsSectionId.Keys));

            // Opening it lights up the first pop-out row's label, and Enter moves to that row's capture button.
            window.SelectSection(SettingsSectionId.Keys);
            Dispatcher.UIThread.RunJobs();
            TextBlock hit = Assert.IsType<TextBlock>(Assert.Single(Highlighted(host)));
            Assert.Equal("Pop out aircraft list key:", hit.Text);
            box.Focus();
            Dispatcher.UIThread.RunJobs();
            window.DispatchKey(Avalonia.Input.Key.Enter);
            Button capture = Assert.IsType<Button>(window.FocusManager!.GetFocusedElement());
            Assert.Contains("key-capture", capture.Classes);
            Assert.Equal("PopOutAircraftList", Assert.IsType<KeybindRow>(capture.DataContext).Id);

            // "hotkey" counts every Keys row and its link to the push-to-talk key, and the push-to-talk key in Speech,
            // with the Keys section on show and after leaving it.
            int keysRows = SettingsViewModel.KeybindDescriptors.Count(d => d.InKeysSection);
            Search(window, "hotkey");
            Assert.Equal(keysRows + 1, MatchCount(nav, SettingsSectionId.Keys));
            Assert.Equal(1, MatchCount(nav, SettingsSectionId.Speech));

            window.SelectSection(SettingsSectionId.Speech);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("PTT key:", Assert.IsType<TextBlock>(Assert.Single(Highlighted(host))).Text);

            Search(window, "");
            window.SelectSection(SettingsSectionId.General);
            Dispatcher.UIThread.RunJobs();
            Search(window, "hotkey");
            Assert.Equal(keysRows + 1, MatchCount(nav, SettingsSectionId.Keys));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static int? MatchCount(ListBox nav, SettingsSectionId id) =>
        nav.Items.OfType<SettingsNavItem>().SingleOrDefault(row => row.Id == id)?.MatchCount;

    [AvaloniaFact(Timeout = 60_000)]
    public void ClearingTheQuery_RestoresEverySection_AndKeepsTheSelection()
    {
        var window = new SettingsWindow();
        window.ShowAndRunLayout();

        try
        {
            ListBox nav = window.FindControl<ListBox>("SectionNav")!;
            ContentControl host = window.FindControl<ContentControl>("SectionHost")!;
            Search(window, "font");
            window.SelectSection(SettingsSectionId.Radar);
            Dispatcher.UIThread.RunJobs();

            Search(window, "");

            Assert.Equal(SettingsNavigation.Items, nav.Items.OfType<SettingsNavItem>());
            Assert.Equal(SettingsSectionId.Radar, Assert.IsType<SettingsNavItem>(nav.SelectedItem).Id);
            Assert.IsType<RadarSection>(host.Content);

            // Opening a section the query filtered out clears the query first.
            Search(window, "font");
            window.SelectSection(SettingsSectionId.Speech);
            Dispatcher.UIThread.RunJobs();

            Assert.True(string.IsNullOrEmpty(window.FindControl<TextBox>("SettingsSearchBox")!.Text));
            Assert.Equal(SettingsNavigation.Items, nav.Items.OfType<SettingsNavItem>());
            Assert.Equal(SettingsSectionId.Speech, Assert.IsType<SettingsNavItem>(nav.SelectedItem).Id);
            Assert.IsType<SpeechSection>(host.Content);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Search(SettingsWindow window, string query)
    {
        window.FindControl<TextBox>("SettingsSearchBox")!.Text = query;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static List<Control> Highlighted(ContentControl host) =>
        [.. host.GetLogicalDescendants().OfType<Control>().Where(c => c.Classes.Contains("search-hit"))];

    private static Control RealizedContainer(ListBox nav, int index)
    {
        if (nav.ContainerFromIndex(index) is { } container)
        {
            return container;
        }

        nav.ScrollIntoView(index);
        Dispatcher.UIThread.RunJobs();
        return nav.ContainerFromIndex(index) ?? throw new InvalidOperationException($"Sidebar row {index} was not realized");
    }
}
