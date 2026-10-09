using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Yaat.Client;

namespace Yaat.GuideCapture.Capture;

internal static class Runner
{
    // outDirOverride is the --out folder; null writes each scene's PNG to its own DefaultOutDir under the repo root.
    public static async Task<int> RunAsync(string? outDirOverride, string? sceneFilter, CaptureContext ctx, double renderScaling)
    {
        IReadOnlyList<Scene> scenes = SceneCatalog.Select(sceneFilter);

        if (scenes.Count == 0)
        {
            Console.Error.WriteLine($"No scenes match filter '{sceneFilter}'.");
            Console.Error.WriteLine("Available scenes:");
            foreach (Scene s in SceneCatalog.All)
            {
                Console.Error.WriteLine($"  {s.Name}");
            }
            return 1;
        }

        int failed = 0;
        foreach (Scene scene in scenes)
        {
            try
            {
                string outDir = outDirOverride ?? Path.Combine(ctx.RepoRoot, scene.DefaultOutDir);
                Directory.CreateDirectory(outDir);
                Exception? afterCaptureFailure = await CaptureOneAsync(scene, ctx, outDir, renderScaling);
                if (afterCaptureFailure is not null)
                {
                    failed++;
                }
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

    // Returns the exception a scene's AfterCapture threw, or null. A failed AfterCapture fails the
    // scene: the run counts it and its exit status reflects it, the way a capture failure does.
    private static async Task<Exception?> CaptureOneAsync(Scene scene, CaptureContext ctx, string outDir, double renderScaling)
    {
        Console.WriteLine($"Capturing {scene.Name} ({scene.Width}x{scene.Height}) ...");

        // Reset process-wide state that scenes opt into. Without this,
        // App.AutoConnectTarget set by an earlier connected scene would leak
        // into a later "disconnected" scene's MainWindow constructor.
        App.AutoConnectTarget = null;
        App.AutoLoadScenarioId = null;

        await scene.BeforeWindowAsync(ctx);

        Window window = scene.CreateWindow(ctx);
        try
        {
            // Width/Height of 0 means "use the window's declared default" — for
            // dialog windows whose layout assumes a specific size set in XAML.
            if (scene.Width > 0)
            {
                window.Width = scene.Width;
            }
            if (scene.Height > 0)
            {
                window.Height = scene.Height;
            }
            // Render scaling must be applied before the first layout pass so the captured frame is
            // rasterized at N x device pixels. Avalonia 12 exposes this on headless windows; at 1.0
            // it is a no-op, so the default output stays byte-comparable with previous runs.
            if (renderScaling != 1.0)
            {
                window.SetRenderScaling(renderScaling);
            }

            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            await scene.AfterShowAsync(window, ctx);

            await Task.Delay(scene.SettleAfterShow);
            Dispatcher.UIThread.RunJobs();

            Window captureTarget = scene.GetCaptureTarget(window);
            WriteableBitmap bitmap =
                captureTarget.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("CaptureRenderedFrame returned null. UseHeadlessDrawing must be false.");

            string path = Path.Combine(outDir, $"{scene.Name}.png");
            bitmap.Save(path, PngBitmapEncoderOptions.Default);
            Console.WriteLine($"  -> {path}");
        }
        catch
        {
            RestoreAndClose(scene, window);
            throw;
        }

        return RestoreAndClose(scene, window);
    }

    // Runs the scene's AfterCapture and closes its windows; returns the AfterCapture exception, if any, so the scene counts as failed.
    private static Exception? RestoreAndClose(Scene scene, Window window)
    {
        Exception? afterCaptureFailure = null;
        try
        {
            scene.AfterCapture();
            Dispatcher.UIThread.RunJobs();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  {scene.Name}: restoring state after the capture failed: {ex}");
            afterCaptureFailure = ex;
        }

        foreach (Window extra in scene.ExtraWindows)
        {
            try
            {
                extra.Close();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  {scene.Name}: closing extra window '{extra.Title}' failed: {ex}");
            }
        }
        window.Close();
        Dispatcher.UIThread.RunJobs();
        return afterCaptureFailure;
    }
}
