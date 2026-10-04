using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
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
