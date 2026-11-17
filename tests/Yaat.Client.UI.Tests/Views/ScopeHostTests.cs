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
