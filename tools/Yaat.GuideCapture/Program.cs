using Avalonia;
using Avalonia.Headless;
using Velopack;
using Yaat.Client;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture;

// Entry point for the User Guide screenshot harness. Boots Yaat.Client under
// Avalonia's headless platform with the real Skia backend (UseHeadlessDrawing
// = false), then runs every Scene in Scenes.All — or a single one named via
// --scene — and writes a PNG per scene to --out (default docs/user-guide/img/).
//
//   dotnet run --project tools/Yaat.GuideCapture
//   dotnet run --project tools/Yaat.GuideCapture -- --scene main-window-empty --out .tmp/guide
//
// Run from the repo root so the default --out resolves correctly.
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!TryParseArgs(args, out var sceneFilter, out var outDir, out var error))
        {
            Console.Error.WriteLine(error);
            PrintUsage();
            return 2;
        }

        // MainViewModel constructs UpdateService eagerly, which requires
        // VelopackLocator initialization. Build().Run() is idempotent here
        // because we don't pass install-hook args.
        VelopackApp.Build().Run();

        AppBuilder
            .Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .SetupWithoutStarting();

        return Runner.Run(outDir, sceneFilter, SceneCatalog.All);
    }

    private static bool TryParseArgs(string[] args, out string? sceneFilter, out string outDir, out string error)
    {
        sceneFilter = null;
        outDir = Path.Combine(Environment.CurrentDirectory, "docs", "user-guide", "img");
        error = string.Empty;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scene":
                    if (i + 1 >= args.Length)
                    {
                        error = "--scene requires a value";
                        return false;
                    }
                    sceneFilter = args[++i];
                    break;
                case "--out":
                    if (i + 1 >= args.Length)
                    {
                        error = "--out requires a value";
                        return false;
                    }
                    outDir = Path.GetFullPath(args[++i]);
                    break;
                case "--help":
                case "-h":
                    error = string.Empty;
                    return false;
                default:
                    error = $"Unknown argument: {args[i]}";
                    return false;
            }
        }

        return true;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("Yaat.GuideCapture — regenerate User Guide screenshots.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  dotnet run --project tools/Yaat.GuideCapture [-- --scene <name>] [--out <dir>]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Options:");
        Console.Error.WriteLine("  --scene <name>   Capture only the named scene (default: all).");
        Console.Error.WriteLine("  --out <dir>      Output directory (default: docs/user-guide/img/).");
    }
}
