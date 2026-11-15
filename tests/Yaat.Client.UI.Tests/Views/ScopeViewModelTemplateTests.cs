using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The scope's datablock template: seeded once from the EuroScope-mode preference when the scope opens, then switched only
/// by the scope window's Template menu (STARS / ERAM / EuroScope radio items), never written back to the preferences.
/// </summary>
public class ScopeViewModelTemplateTests
{
    [AvaloniaFact]
    public void Template_SeedsFromEuroScopeModeOnce()
    {
        // The assembly's [PreferencesFilePerTest] gives this test its own preferences file.
        var main = new MainViewModel(new FakeFilePickerService());
        main.Preferences.SetEuroScopeMode(false);
        using (var stars = new ScopeViewModel(main))
        {
            Assert.Equal(ScopeTemplates.StarsId, stars.TemplateId);
            Assert.True(stars.IsStarsTemplate);
        }

        main.Preferences.SetEuroScopeMode(true);
        using var euroScope = new ScopeViewModel(main);
        Assert.Equal(ScopeTemplates.EuroScopeId, euroScope.TemplateId);

        // A later preference change leaves the open scope's template alone.
        main.Preferences.SetEuroScopeMode(false);
        Assert.Equal(ScopeTemplates.EuroScopeId, euroScope.TemplateId);
    }

    [AvaloniaFact]
    public void TemplatePicks_SwitchTheTemplate_AndNeverTouchThePreference()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        bool before = main.Preferences.EuroScopeMode;
        using var scope = new ScopeViewModel(main);

        scope.PickTemplateCommand.Execute(ScopeTemplates.EramId);
        Assert.Equal(ScopeTemplates.EramId, scope.TemplateId);
        Assert.True(scope.IsEramTemplate);
        Assert.False(scope.IsStarsTemplate);

        scope.PickTemplateCommand.Execute(ScopeTemplates.EuroScopeId);
        Assert.True(scope.IsEuroScopeTemplate);
        scope.PickTemplateCommand.Execute(ScopeTemplates.StarsId);
        Assert.Equal(ScopeTemplates.StarsId, scope.TemplateId);
        Assert.Equal(before, main.Preferences.EuroScopeMode);
        Assert.Throws<ArgumentException>(() => scope.PickTemplateCommand.Execute("nope"));
        Assert.Equal(ScopeTemplates.StarsId, scope.TemplateId);
    }

    [AvaloniaFact]
    public void MixedPick_SwitchesTheTemplate_AndNeverTouchesThePreference()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        bool before = main.Preferences.EuroScopeMode;
        using var scope = new ScopeViewModel(main);

        scope.PickTemplateCommand.Execute(ScopeTemplates.MixedId);

        Assert.Equal(ScopeTemplates.MixedId, scope.TemplateId);
        Assert.True(scope.IsMixedTemplate);
        Assert.False(scope.IsStarsTemplate);
        Assert.False(scope.IsEramTemplate);
        Assert.Equal(before, main.Preferences.EuroScopeMode);
        scope.PickTemplateCommand.Execute(ScopeTemplates.EramId);
        Assert.False(scope.IsMixedTemplate);
    }

    [AvaloniaFact]
    public void ScopeWindow_TemplateMenu_ClickingMixedSwitchesTheScope()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main);
        var window = new ScopeWindow(main.Preferences, scope);
        try
        {
            window.Show();
            MenuItem template = window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header is "_Template");
            MenuItem mixed = template.Items.OfType<MenuItem>().Single(m => m.Header is "_Mixed");
            Assert.Equal(MenuItemToggleType.Radio, mixed.ToggleType);

            mixed.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(ScopeTemplates.MixedId, scope.TemplateId);
            Assert.True(scope.IsMixedTemplate);
            Assert.True(mixed.IsChecked);
            Assert.False(template.Items.OfType<MenuItem>().Single(m => m.Header is "_STARS").IsChecked);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ScopeWindow_TemplateMenu_ClickingEramSwitchesTheScope()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main);
        var window = new ScopeWindow(main.Preferences, scope);
        try
        {
            window.Show();
            MenuItem template = window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header is "_Template");
            Assert.Equal(["_STARS", "_ERAM", "_Mixed", "Euro_Scope"], template.Items.OfType<MenuItem>().Select(m => (string)m.Header!));
            MenuItem eram = template.Items.OfType<MenuItem>().Single(m => m.Header is "_ERAM");
            Assert.Equal(MenuItemToggleType.Radio, eram.ToggleType);

            eram.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(ScopeTemplates.EramId, scope.TemplateId);
            Assert.True(eram.IsChecked);
            Assert.False(template.Items.OfType<MenuItem>().Single(m => m.Header is "_STARS").IsChecked);
        }
        finally
        {
            window.Close();
        }
    }
}
