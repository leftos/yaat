using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.Views;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The YAAT Scope opens from View › Windows as a pop-out-only window, beside "New radar window…". It has one instance: a
/// second click brings the open window forward instead of opening another.
/// </summary>
public class ScopeWindowMenuTests
{
    private const string ScopeHeader = "YAAT _Scope";

    private static MainWindow BootMainWindow() => MainWindowHost.Boot().Main;

    private static MenuItem WindowsMenu(MainWindow window)
    {
        MenuItem view = window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header is "_View");
        return view.Items.OfType<MenuItem>().Single(m => m.Header is "_Windows");
    }

    [AvaloniaFact]
    public void ViewMenu_HasYaatScopeItem()
    {
        MainWindow window = BootMainWindow();
        try
        {
            MenuItem item = Assert.Single(WindowsMenu(window).Items.OfType<MenuItem>(), m => m.Header is ScopeHeader);
            Assert.True(item.IsEnabled);
            Assert.NotEqual(MenuItemToggleType.CheckBox, item.ToggleType);
        }
        finally
        {
            MainWindowHost.CloseAll(window);
        }
    }

    [AvaloniaFact]
    public void YaatScopeItem_OpensOneScopeWindow()
    {
        MainWindow window = BootMainWindow();
        try
        {
            MenuItem item = WindowsMenu(window).Items.OfType<MenuItem>().Single(m => m.Header is ScopeHeader);
            Assert.Null(window.ScopeWindow);

            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ScopeWindow first = Assert.IsType<ScopeWindow>(window.ScopeWindow);
            Assert.True(first.IsVisible);

            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(first, window.ScopeWindow);
            Assert.True(first.IsVisible);

            // Closing it frees the slot, so the next click opens a fresh window.
            first.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(window.ScopeWindow);
        }
        finally
        {
            window.ScopeWindow?.Close();
            MainWindowHost.CloseAll(window);
        }
    }
}
