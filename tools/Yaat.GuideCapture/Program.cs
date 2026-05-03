using Avalonia;
using Avalonia.Headless;
using Velopack;
using Yaat.Client;
using Yaat.GuideCapture.Capture;
#if HAS_YAAT_SERVER
using Yaat.GuideCapture.Server;
#endif

namespace Yaat.GuideCapture;

// Entry point for the User Guide screenshot harness. Boots a yaat-server
// in-process on a free loopback port, then runs Yaat.Client under Avalonia's
// headless platform with the real Skia backend (UseHeadlessDrawing = false)
// driven by HeadlessUnitTestSession. Scenes opt into autoconnect by setting
// App.AutoConnectTarget in their BeforeWindowAsync hook.
//
//   dotnet run --project tools/Yaat.GuideCapture
//   dotnet run --project tools/Yaat.GuideCapture -- --scene main-window-empty --out .tmp/guide
//
// Run from the repo root so the default --out resolves correctly.
public static class Program
{
    private static int _velopackInitialized;

    [STAThread]
    public static int Main(string[] args)
    {
#if !HAS_YAAT_SERVER
        Console.Error.WriteLine("Yaat.GuideCapture requires the yaat-server sibling repo at ../yaat-server.");
        Console.Error.WriteLine("This build was produced without it; the tool cannot run.");
        return 2;
#else
        if (!TryParseArgs(args, out var sceneFilter, out var outDir, out var error))
        {
            Console.Error.WriteLine(error);
            PrintUsage();
            return 2;
        }

        var exitCode = MainAsync(sceneFilter, outDir).GetAwaiter().GetResult();

        // Force-terminate. The Avalonia headless dispatcher and ASP.NET Core
        // hosted services leave non-background threads alive after Main
        // returns, which prevents the process from exiting cleanly. The
        // server.DisposeAsync + session.Dispose paths inside MainAsync have
        // already run, so this only cuts off the dispatcher loop.
        Environment.Exit(exitCode);
        return exitCode;
#endif
    }

#if HAS_YAAT_SERVER
    private static async Task<int> MainAsync(string? sceneFilter, string outDir)
    {
        await using var server = new InProcessServer();
        Console.WriteLine("Starting in-process yaat-server ...");
        await server.StartAsync();
        Console.WriteLine($"  Server listening on {server.Url}");

        using var session = HeadlessUnitTestSession.StartNew(typeof(Program));
        var ctx = new CaptureContext { ServerUrl = server.Url };

        return await session.Dispatch(() => Runner.RunAsync(outDir, sceneFilter, SceneCatalog.All, ctx), CancellationToken.None);
    }
#endif

    // Discovered by HeadlessUnitTestSession via reflection (same pattern xUnit's
    // AvaloniaTestApplicationAttribute uses). VelopackApp.Build().Run() must run
    // before AppBuilder is consumed because MainViewModel constructs an
    // UpdateService eagerly that requires VelopackLocator initialization.
    public static AppBuilder BuildAvaloniaApp()
    {
        if (Interlocked.CompareExchange(ref _velopackInitialized, 1, 0) == 0)
        {
            VelopackApp.Build().Run();
        }

        return AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().WithInterFont();
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
