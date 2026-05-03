using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Yaat.GuideCapture.Capture;

internal static class Runner
{
    public static int Run(string outDir, string? sceneFilter, IReadOnlyList<Scene> allScenes)
    {
        Directory.CreateDirectory(outDir);

        var scenes = sceneFilter is null
            ? allScenes
            : allScenes.Where(s => string.Equals(s.Name, sceneFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        if (scenes.Count == 0)
        {
            Console.Error.WriteLine($"No scenes match filter '{sceneFilter}'.");
            Console.Error.WriteLine("Available scenes:");
            foreach (var s in allScenes)
            {
                Console.Error.WriteLine($"  {s.Name}");
            }
            return 1;
        }

        var ctx = new CaptureContext();
        var failed = 0;
        foreach (var scene in scenes)
        {
            try
            {
                CaptureOne(scene, ctx, outDir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL {scene.Name}: {ex.Message}");
                Console.Error.WriteLine(ex);
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? $"OK: {scenes.Count} scene(s) captured." : $"FAILED: {failed}/{scenes.Count} scene(s)");
        return failed == 0 ? 0 : 1;
    }

    private static void CaptureOne(Scene scene, CaptureContext ctx, string outDir)
    {
        Console.WriteLine($"Capturing {scene.Name} ({scene.Width}x{scene.Height}) ...");

        // Phase A: SetupAsync is a no-op for every scene, so awaiting on the
        // dispatcher thread is safe (no continuations queued back to it). Phase
        // B will add a proper sync-over-async pump once scenes need server I/O.
        scene.SetupAsync(ctx).GetAwaiter().GetResult();

        var window = scene.CreateWindow(ctx);
        try
        {
            window.Width = scene.Width;
            window.Height = scene.Height;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var settleEnd = DateTime.UtcNow + scene.SettleAfterShow;
            while (DateTime.UtcNow < settleEnd)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            var bitmap =
                window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("CaptureRenderedFrame returned null. UseHeadlessDrawing must be false.");

            var path = Path.Combine(outDir, $"{scene.Name}.png");
            bitmap.Save(path);
            Console.WriteLine($"  -> {path}");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
