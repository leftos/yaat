namespace Yaat.Sim;

// Central resolver for the YAAT per-user data directory. Every call site that
// previously did Path.Combine(LocalApplicationData, "yaat", ...) must go through
// this helper so tests can redirect the base dir via the YAAT_APPDATA_DIR env var
// without touching the user's real %LOCALAPPDATA%/yaat folder.
public static class YaatPaths
{
    public static string AppDataRoot { get; } =
        Environment.GetEnvironmentVariable("YAAT_APPDATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "yaat");

    public static string Combine(params string[] parts)
    {
        var all = new string[parts.Length + 1];
        all[0] = AppDataRoot;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return Path.Combine(all);
    }
}
