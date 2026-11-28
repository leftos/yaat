using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Client.Views.Authoring;
using Yaat.Sim;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The route composer's canvas over the committed navdata around SFO: each frame's snapshot is a copy (a point added later
/// does not reach an earlier frame), carries only the ticked video maps that are loaded and has no imagery member, and a
/// render draws the route in its colour.
/// </summary>
public sealed class RouteComposerCanvasSnapshotTests : IDisposable
{
    private static readonly SKColor RouteColour = SKColor.Parse(RouteComposerViewModel.DefaultColor);

    private readonly string _root = YaatPaths.Combine("route-composer-canvas-tests", Guid.NewGuid().ToString("N"));

    private static NavigationDatabase NavDb => NavigationDatabase.Instance;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task ASnapshot_IsACopy_APointAddedLaterLeavesItUnchanged()
    {
        (Window window, RouteComposerCanvas canvas, RouteComposerViewModel vm) = await Show([]);
        try
        {
            Assert.True(vm.AddAt(FixPosition("SFO"), "SFO"));
            Assert.True(vm.AddAt(FixPosition("OAK"), "OAK"));
            RouteComposerRenderSnapshot before = canvas.SnapshotForTesting()!;

            Assert.True(vm.AddAt(FixPosition("SUNOL"), "SUNOL"));
            RouteComposerRenderSnapshot after = canvas.SnapshotForTesting()!;

            Assert.Equal([0, 1], before.Scene.Points.Select(p => p.Index));
            Assert.Equal(1, before.Selected);
            Assert.Equal([0, 1, 2], after.Scene.Points.Select(p => p.Index));
            Assert.Equal(2, after.Selected);
            Assert.NotEmpty(before.Scene.Fixes);
        }
        finally
        {
            Close(window, canvas);
        }
    }

    [AvaloniaFact]
    public async Task AMapToggledOff_IsAbsentFromTheSnapshot()
    {
        VideoMapData east = Map("EAST", -122.2);
        VideoMapData west = Map("WEST", -122.6);
        (Window window, RouteComposerCanvas canvas, RouteComposerViewModel vm) = await Show([("East", "EAST", east), ("West", "WEST", west)]);
        try
        {
            Assert.Equal([east, west], canvas.SnapshotForTesting()!.Maps);

            vm.Maps.Single(m => m.Name == "West").IsEnabled = false;

            Assert.Equal([east], canvas.SnapshotForTesting()!.Maps);
        }
        finally
        {
            Close(window, canvas);
        }
    }

    [AvaloniaFact]
    public async Task NavdataSeenForTheFirstTime_BringsTheFixesInWithoutAPanOrZoom()
    {
        NavigationDatabase? current = null;
        (Window window, RouteComposerCanvas canvas, RouteComposerViewModel vm) = await Show([], () => current);
        try
        {
            Assert.Empty(canvas.SnapshotForTesting()!.Scene.Fixes);

            current = NavDb;
            Assert.True(vm.AddAt(FixPosition("SFO"), "SFO"));

            Assert.Equal(RouteComposerViewModel.ReadyText, vm.StatusText);
            Assert.Contains(canvas.SnapshotForTesting()!.Scene.Fixes, f => f.Name == "SFO");
        }
        finally
        {
            Close(window, canvas);
        }
    }

    [Fact]
    public void TheSnapshot_HasNoImageryMember()
    {
        string[] members = [.. typeof(RouteComposerRenderSnapshot).GetProperties().Select(p => p.Name)];

        Assert.Equal(["Scene", "Selected", "Hover", "Maps", "RouteColor"], members);
        Assert.DoesNotContain(
            members,
            m => m.Contains("Imag", StringComparison.OrdinalIgnoreCase) || m.Contains("Tile", StringComparison.OrdinalIgnoreCase)
        );
    }

    [AvaloniaFact]
    public async Task ARender_DrawsTheRouteInItsColour()
    {
        (Window window, RouteComposerCanvas canvas, RouteComposerViewModel vm) = await Show([]);
        try
        {
            Assert.True(vm.AddAt(FixPosition("SFO"), "SFO"));
            Assert.True(vm.AddAt(FixPosition("OAK"), "OAK"));

            using SKBitmap frame = canvas.RenderForTesting(canvas.SnapshotForTesting());

            int route = CountNear(frame, RouteColour);
            Assert.True(route >= 40, $"{route} route-colour pixels drawn");
        }
        finally
        {
            Close(window, canvas);
        }
    }

    private static VideoMapData Map(string id, double lon) =>
        new() { MapId = id, Lines = [new VideoMapLine { Points = [(37.5, lon), (37.8, lon)] }] };

    private static LatLon FixPosition(string name)
    {
        (double Lat, double Lon) pos = NavDb.GetFixPosition(name) ?? throw new InvalidOperationException(name + " is not in navdata");
        return new LatLon(pos.Lat, pos.Lon);
    }

    private static int CountNear(SKBitmap frame, SKColor colour)
    {
        int count = 0;
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                SKColor actual = frame.GetPixel(x, y);
                bool near =
                    (Math.Abs(actual.Red - colour.Red) <= 12)
                    && (Math.Abs(actual.Green - colour.Green) <= 12)
                    && (Math.Abs(actual.Blue - colour.Blue) <= 12);
                count += near ? 1 : 0;
            }
        }

        return count;
    }

    private Task<(Window Window, RouteComposerCanvas Canvas, RouteComposerViewModel Vm)> Show((string Name, string Id, VideoMapData Data)[] maps) =>
        Show(maps, () => NavDb);

    private async Task<(Window Window, RouteComposerCanvas Canvas, RouteComposerViewModel Vm)> Show(
        (string Name, string Id, VideoMapData Data)[] maps,
        Func<NavigationDatabase?> navDb
    )
    {
        RouteComposerMap[] listed = [.. maps.Select(m => new RouteComposerMap(m.Name, m.Id))];
        var context = new RouteComposerContext(
            "ZOA",
            FixResolutionContext.None,
            _ => Task.FromResult<IReadOnlyList<RouteComposerMap>>(listed),
            (id, _) => Task.FromResult<VideoMapData?>(maps.FirstOrDefault(m => m.Id == id).Data)
        );
        var vm = new RouteComposerViewModel(context, new RouteLibrary(_root), navDb);
        await vm.LoadMapsAsync(CancellationToken.None);
        foreach (RouteComposerMapLayer layer in vm.Maps)
        {
            layer.IsEnabled = true;
            await vm.MapLoadTask.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        }

        var canvas = new RouteComposerCanvas { ViewModel = vm };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = canvas,
        };
        window.Show();
        Settle(window);
        canvas.CenterOn(FixPosition("SFO"), 30);
        Settle(window);
        return (window, canvas, vm);
    }

    private static void Settle(Window window)
    {
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Close(Window window, RouteComposerCanvas canvas)
    {
        window.Close();
        canvas.Dispose();
        Dispatcher.UIThread.RunJobs();
    }
}
