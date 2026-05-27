namespace Yaat.Sim;

// Central resolver for the per-user data directory. Every call site that
// previously did Path.Combine(LocalApplicationData, "yaat", ...) goes through
// this helper so tests can redirect the base dir via the YAAT_APPDATA_DIR env
// var without touching the user's real %LOCALAPPDATA%/yaat folder.
//
// Each app calls Initialize(appDirName) at startup before any path access:
//   Yaat.Client → "yaat"
// The env var, when set, takes precedence over the configured dir name —
// tests get a fully isolated temp directory regardless of which app is running.
public static class YaatPaths
{
    private static string? _appDataRoot;
    private static string _appDirName = "yaat";

    public static void Initialize(string appDirName)
    {
        _appDirName = appDirName;
        _appDataRoot = null;
    }

    public static string AppDataRoot
    {
        get
        {
            if (_appDataRoot is not null)
            {
                return _appDataRoot;
            }

            var envOverride = Environment.GetEnvironmentVariable("YAAT_APPDATA_DIR");
            _appDataRoot = envOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _appDirName);
            return _appDataRoot;
        }
    }

    public static string Combine(params string[] parts)
    {
        var all = new string[parts.Length + 1];
        all[0] = AppDataRoot;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return Path.Combine(all);
    }
}
