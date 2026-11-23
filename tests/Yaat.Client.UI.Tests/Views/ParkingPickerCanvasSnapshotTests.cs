using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;
using Yaat.Client.Views.Authoring;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The parking picker's canvas over the real KOAK stands: each frame's snapshot carries the scene, the selection and the
/// hover as plain values (a later click does not change an earlier snapshot), and a render draws the stands and the
/// selected stand's amber ring.
/// </summary>
public class ParkingPickerCanvasSnapshotTests
{
    private static readonly SKColor ParkingColour = new(0x6C, 0xB4, 0xEE);
    private static readonly SKColor AmberColour = new(0xFF, 0xC1, 0x07);

    [AvaloniaFact]
    public void Snapshot_CarriesSelectionAndHover()
    {
        string current = OakParkingLayout.FirstParkingName;
        (Window window, ParkingPickerCanvas canvas, ParkingPickerScene scene) = Show(current);
        try
        {
            ParkingPickerRenderSnapshot first = canvas.SnapshotForTesting()!;
            Assert.Same(scene, first.Scene);
            Assert.Equal(current, first.Selected);
            Assert.Null(first.Hover);

            (ParkingPickerStand stand, Point at) = OakParkingLayout.IsolatedStand(scene, canvas.Viewport, notNamed: current);
            Point inWindow = canvas.TranslatePoint(at, window)!.Value;
            window.MouseMove(inWindow);
            Dispatcher.UIThread.RunJobs();
            ParkingPickerRenderSnapshot hovered = canvas.SnapshotForTesting()!;
            Assert.Equal(stand.Name, hovered.Hover);
            Assert.Equal(current, hovered.Selected);

            string? raised = null;
            canvas.SelectionChanged += (_, name) => raised = name;
            window.MouseDown(inWindow, MouseButton.Left);
            window.MouseUp(inWindow, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            ParkingPickerRenderSnapshot selected = canvas.SnapshotForTesting()!;
            Assert.Equal(stand.Name, raised);
            Assert.Equal(stand.Name, selected.Selected);
            Assert.Equal(stand.Name, selected.Hover);
            Assert.Equal(current, first.Selected);
            Assert.Equal(current, hovered.Selected);
        }
        finally
        {
            Close(window, canvas);
        }
    }

    [AvaloniaFact]
    public void RenderForTesting_DrawsANonEmptyBitmap()
    {
        string current = OakParkingLayout.FirstParkingName;
        (Window window, ParkingPickerCanvas canvas, _) = Show(current);
        try
        {
            using SKBitmap frame = canvas.RenderForTesting();

            Assert.Equal(800, frame.Width);
            int parking = CountNear(frame, ParkingColour);
            int amber = CountNear(frame, AmberColour);
            Assert.True(parking >= 20, $"{parking} parking-colour pixels drawn");
            Assert.True(amber >= 10, $"{amber} amber pixels drawn");
        }
        finally
        {
            Close(window, canvas);
        }
    }

    // Drawing reads only its snapshot: a frame taken before a click selects another stand still draws the old selection.
    [AvaloniaFact]
    public void ASnapshotTakenBeforeASelectionChange_DrawsTheOldSelection()
    {
        string current = OakParkingLayout.FirstParkingName;
        (Window window, ParkingPickerCanvas canvas, ParkingPickerScene scene) = Show(current);
        try
        {
            ParkingPickerRenderSnapshot before = canvas.SnapshotForTesting()!;
            ParkingPickerStand old = scene.Find(current)!;
            (float ox, float oy) = canvas.Viewport.LatLonToScreen(old.Lat, old.Lon);
            (ParkingPickerStand stand, Point at) = OakParkingLayout.IsolatedStand(scene, canvas.Viewport, notNamed: current);
            Point inWindow = canvas.TranslatePoint(at, window)!.Value;
            window.MouseDown(inWindow, MouseButton.Left);
            window.MouseUp(inWindow, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(stand.Name, canvas.Selected);

            using SKBitmap frame = canvas.RenderForTesting(before);

            int oldRing = AmberRingSamples(frame, ox, oy);
            int newRing = AmberRingSamples(frame, at.X, at.Y);
            Assert.True(oldRing >= 8, $"{oldRing} of 16 samples amber around the old selection");
            Assert.True(newRing <= 2, $"{newRing} of 16 samples amber around the new selection");
        }
        finally
        {
            Close(window, canvas);
        }
    }

    // How many of 16 points on the selection ring's circle around a point are amber.
    private static int AmberRingSamples(SKBitmap frame, double cx, double cy)
    {
        int amber = 0;
        for (int i = 0; i < 16; i++)
        {
            double angle = i * Math.PI / 8;
            int x = (int)Math.Round(cx + (10 * Math.Cos(angle)));
            int y = (int)Math.Round(cy + (10 * Math.Sin(angle)));
            SKColor c = frame.GetPixel(x, y);
            bool near =
                (Math.Abs(c.Red - AmberColour.Red) <= 40)
                && (Math.Abs(c.Green - AmberColour.Green) <= 40)
                && (Math.Abs(c.Blue - AmberColour.Blue) <= 60);
            amber += near ? 1 : 0;
        }

        return amber;
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

    private static (Window Window, ParkingPickerCanvas Canvas, ParkingPickerScene Scene) Show(string? current)
    {
        var scene = ParkingPickerScene.Build(OakParkingLayout.Layout, current);
        var canvas = new ParkingPickerCanvas { Scene = scene };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = canvas,
        };
        window.Show();
        Settle(window);
        canvas.FitToStands();
        Settle(window);
        return (window, canvas, scene);
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

    private static void Close(Window window, ParkingPickerCanvas canvas)
    {
        window.Close();
        canvas.Dispose();
        Dispatcher.UIThread.RunJobs();
    }
}
