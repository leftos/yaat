using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The scope's datablock template: seeded once from the EuroScope-mode preference when the scope opens, then switched only
/// by a pick (the toolbar's TEMPLATE tray: STARS, ERAM, MIXED, EUROSCOPE), never written back to the preferences.
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

        scope.PickTemplateCommand.Execute(ScopeTemplates.EuroScopeId);
        Assert.Equal(ScopeTemplates.EuroScopeId, scope.TemplateId);
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
        Assert.Equal(before, main.Preferences.EuroScopeMode);
        scope.PickTemplateCommand.Execute(ScopeTemplates.EramId);
        Assert.Equal(ScopeTemplates.EramId, scope.TemplateId);
    }

    [AvaloniaFact]
    public void ScopeWindow_TemplateMenu_ClickingMixedSwitchesTheScope()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main);
        main.OpenScope = scope;
        var window = new ScopeWindow(main.Preferences, scope);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            ScopeToolbarTests.Click(ScopeToolbarTests.ButtonOf(window, ScopeButtonId.Template));

            ScopeToolbarTests.Click(ScopeToolbarTests.ButtonOf(window, ScopeButtonId.TemplateMixed));

            Assert.Equal(ScopeTemplates.MixedId, scope.TemplateId);
            ScopeToolbarTests.Click(ScopeToolbarTests.ButtonOf(window, ScopeButtonId.Template));
            Assert.Contains("active", ScopeToolbarTests.ButtonOf(window, ScopeButtonId.TemplateMixed).Classes);
            Assert.DoesNotContain("active", ScopeToolbarTests.ButtonOf(window, ScopeButtonId.TemplateStars).Classes);
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
        main.OpenScope = scope;
        var window = new ScopeWindow(main.Preferences, scope);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            ScopeToolbarTests.Click(ScopeToolbarTests.ButtonOf(window, ScopeButtonId.Template));
            Button eram = ScopeToolbarTests.ButtonOf(window, ScopeButtonId.TemplateEram);

            ScopeToolbarTests.Click(eram);

            Assert.Equal(ScopeTemplates.EramId, scope.TemplateId);
            ScopeToolbarTests.Click(ScopeToolbarTests.ButtonOf(window, ScopeButtonId.Template));
            Assert.Contains("active", ScopeToolbarTests.ButtonOf(window, ScopeButtonId.TemplateEram).Classes);
            Assert.DoesNotContain("active", ScopeToolbarTests.ButtonOf(window, ScopeButtonId.TemplateStars).Classes);
        }
        finally
        {
            window.Close();
        }
    }
}
