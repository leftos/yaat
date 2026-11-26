using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Dock;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope as one control: the host shows the Scope's toolbar, view and views dock over its view model wherever it sits, so
/// the main window's Scope tab and the Scope window show the same Scope.
/// </summary>
public class ScopeHostTests
{
    [AvaloniaFact]
    public async Task Host_InABareWindow_ShowsToolbarViewAndDock()
    {
        (MainViewModel _, ScopeViewModel scope) = await ScopeTestFixture.OpenScopeAsync();
        var host = new ScopeHost { DataContext = scope };
        var window = new Window
        {
            Width = 1200,
            Height = 800,
            Content = host,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        try
        {
            ScopeToolbar toolbar = Assert.Single(host.GetVisualDescendants().OfType<ScopeToolbar>());
            ScopeView view = Assert.Single(host.GetVisualDescendants().OfType<ScopeView>());
            ScopeDock dock = Assert.Single(host.GetVisualDescendants().OfType<ScopeDock>());

            Assert.Same(host.ScopeToolbar, toolbar);
            Assert.Same(host.ScopeView, view);
            Assert.Same(host.ScopeDock, dock);
            Assert.Same(scope, view.DataContext);
            Assert.Same(scope, toolbar.DataContext);
            Assert.Same(scope, dock.DataContext);
            Assert.True(view.Bounds.Height > 0);
        }
        finally
        {
            window.Close();
            scope.Dispose();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task BarKind_FollowsTheRoomProfile()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeTestFixture.OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope profile stars");
            Assert.Equal(ScopeBarKind.Stars, scope.BarKind);

            main.RunScopeCommand(".scope profile eram");
            Assert.Equal(ScopeBarKind.Eram, scope.BarKind);

            main.RunScopeCommand(".scope profile mixed");
            Assert.Equal(ScopeBarKind.Classic, scope.BarKind);
        }
        finally
        {
            scope.Dispose();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task BarKind_IgnoresATemplatePick()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeTestFixture.OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope profile stars");
            Assert.Equal(ScopeBarKind.Stars, scope.BarKind);

            scope.PickTemplateCommand.Execute("eram");

            Assert.Equal("eram", scope.TemplateId);
            Assert.Equal(ScopeBarKind.Stars, scope.BarKind);
        }
        finally
        {
            scope.Dispose();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task TheHost_ShowsTodaysBar_ForEveryKind()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeTestFixture.OpenScopeAsync();
        var host = new ScopeHost { DataContext = scope };
        var window = new Window
        {
            Width = 1200,
            Height = 800,
            Content = host,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        try
        {
            ScopeToolbar first = host.ScopeToolbar;
            Assert.NotNull(first);

            foreach (string profile in new[] { "stars", "eram", "mixed" })
            {
                main.RunScopeCommand($".scope profile {profile}");
                Dispatcher.UIThread.RunJobs();
                Assert.Same(first, host.ScopeToolbar);
            }
        }
        finally
        {
            window.Close();
            scope.Dispose();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task ScopeWindow_ShowsTheScopeThroughAHost()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeTestFixture.OpenScopeAsync();
        var window = new ScopeWindow(main.Preferences, scope);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            ScopeHost host = Assert.Single(window.GetVisualDescendants().OfType<ScopeHost>());
            Assert.Same(window.ScopeHost, host);
            Assert.Same(scope, host.DataContext);
            Assert.Single(window.GetVisualDescendants().OfType<ScopeView>());
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
