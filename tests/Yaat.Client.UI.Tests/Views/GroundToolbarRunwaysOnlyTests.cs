using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Ground View toolbar of an airport with no ground map (a runways-only layout) hides the toggles that need a taxi graph
/// (TWY, HS, PARK, SPOT) and keeps their saved modes; the other toggles stay. A full layout shows all of them.
/// </summary>
public class GroundToolbarRunwaysOnlyTests
{
    private static readonly string[] GraphToggles = ["TWY", "HS", "PARK", "SPOT"];
    private static readonly string[] OtherToggles = ["SAT", "MAP", "GND", "RBL", "RWY", "ADW"];

    [AvaloniaFact]
    public void RunwaysOnlyLayout_HidesTheGraphToggles_AndKeepsTheirModes()
    {
        GroundViewModel vm = NewViewModel(FullLayout());
        vm.ShowHoldShort = GroundFilterMode.IconsOnly;
        vm.ShowTaxiwayLabels = false;
        (Window window, GroundView view) = Show(vm);
        try
        {
            vm.Layout = RunwaysOnlyLayout();
            PumpLayout(window);

            Assert.All(GraphToggles, name => Assert.False(Toggle(view, name).IsVisible, $"{name} should be hidden on a runways-only layout"));
            Assert.All(OtherToggles, name => Assert.True(Toggle(view, name).IsVisible, $"{name} should stay on a runways-only layout"));
            Assert.Equal(GroundFilterMode.IconsOnly, vm.ShowHoldShort);
            Assert.False(vm.ShowTaxiwayLabels);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void FullLayout_ShowsEveryToggle()
    {
        (Window window, GroundView view) = Show(NewViewModel(FullLayout()));
        try
        {
            Assert.All(GraphToggles.Concat(OtherToggles), name => Assert.True(Toggle(view, name).IsVisible, $"{name} should show on a full layout"));
        }
        finally
        {
            window.Close();
        }
    }

    private static Button Toggle(GroundView view, string content) =>
        Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("gnd-filter") && (b.Content as string == content));

    private static GroundViewModel NewViewModel(GroundLayoutDto layout) =>
        new(new ServerConnection(), sendCommand: (_, _, _) => Task.CompletedTask)
        {
            RoomActiveRunways = () => new Dictionary<string, IReadOnlyList<string>>(),
            Layout = layout,
        };

    private static (Window Window, GroundView View) Show(GroundViewModel vm)
    {
        var view = new GroundView { DataContext = vm };
        var window = new Window
        {
            Width = 1200,
            Height = 600,
            Content = view,
        };
        window.Show();
        PumpLayout(window);
        return (window, view);
    }

    private static void PumpLayout(Window window)
    {
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static GroundLayoutDto FullLayout()
    {
        var nodes = new List<GroundNodeDto>
        {
            new(1, 37.61, -122.39, "Taxiway", null, null, null),
            new(2, 37.63, -122.36, "Taxiway", null, null, null),
        };
        return new GroundLayoutDto("SFO", nodes, [], null, null, null, IsRunwaysOnly: false);
    }

    private static GroundLayoutDto RunwaysOnlyLayout() =>
        new(
            "SMF",
            [],
            [],
            null,
            [
                new GroundRunwayDto(
                    "17L/35R",
                    [
                        [38.70, -121.60],
                        [38.68, -121.59],
                    ],
                    150
                ),
            ],
            null,
            IsRunwaysOnly: true
        );
}
