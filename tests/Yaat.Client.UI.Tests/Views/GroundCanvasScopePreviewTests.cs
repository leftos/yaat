using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>The ground view hands its view model's scope preview to its canvas, and its clear too.</summary>
public class GroundCanvasScopePreviewTests
{
    [AvaloniaFact]
    public void ViewModelScopePreview_ReachesTheCanvas_AndClears()
    {
        var vm = new GroundViewModel(new ServerConnection(), (_, _, _) => Task.CompletedTask, preferences: new UserPreferences())
        {
            RoomActiveRunways = () => new Dictionary<string, IReadOnlyList<string>>(),
        };
        var view = new GroundView { DataContext = vm };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        GroundCanvas canvas = Assert.IsType<GroundCanvas>(view.FindControl<GroundCanvas>("Canvas"));
        var preview = new ScopePreview("UAL123", null, null, null, new LatLon(37.62, -122.38));

        vm.ScopePreview = preview;
        Dispatcher.UIThread.RunJobs();

        Assert.Same(preview, canvas.ScopePreview);

        vm.ScopePreview = null;
        Dispatcher.UIThread.RunJobs();

        Assert.Null(canvas.ScopePreview);
        window.Close();
    }
}
