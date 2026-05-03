using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Yaat.Client;

namespace Yaat.GuideCapture.Capture;

internal static class Runner
{
    public static async Task<int> RunAsync(string outDir, string? sceneFilter, IReadOnlyList<Scene> allScenes, CaptureContext ctx)
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

        var failed = 0;
        foreach (var scene in scenes)
        {
            try
            {
                await CaptureOneAsync(scene, ctx, outDir);
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

    private static async Task CaptureOneAsync(Scene scene, CaptureContext ctx, string outDir)
    {
        Console.WriteLine($"Capturing {scene.Name} ({scene.Width}x{scene.Height}) ...");

        // Reset process-wide state that scenes opt into. Without this,
        // App.AutoConnectTarget set by an earlier connected scene would leak
        // into a later "disconnected" scene's MainWindow constructor.
        App.AutoConnectTarget = null;
        App.AutoLoadScenarioId = null;

        await scene.BeforeWindowAsync(ctx);

        var window = scene.CreateWindow(ctx);
        try
        {
            window.Width = scene.Width;
            window.Height = scene.Height;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            await scene.AfterShowAsync(window, ctx);

            await Task.Delay(scene.SettleAfterShow);
            Dispatcher.UIThread.RunJobs();

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
