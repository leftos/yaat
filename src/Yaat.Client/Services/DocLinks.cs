namespace Yaat.Client.Services;

/// <summary>
/// GitHub URLs for the user-facing markdown docs surfaced from the Help menu.
/// Release builds link to the matching tag (e.g. <c>v0.1.1-alpha</c>) so users see the
/// docs that ship with their installed version; dev builds link to <c>main</c>.
/// </summary>
public static class DocLinks
{
    private const string RepoUrl = "https://github.com/leftos/yaat";

    private static string Ref => BuildInfo.IsInstalledRelease ? $"v{BuildInfo.Version}" : "main";

    public static string Repo => RepoUrl;
    public static string Issues => $"{RepoUrl}/issues/new/choose";
    public static string Releases => $"{RepoUrl}/releases";

    // Optional "buy me a coffee" support link surfaced in Help → About.
    public static string Donate => "https://ko-fi.com/leftos";

    public static string GettingStarted => $"{RepoUrl}/blob/{Ref}/GETTING_STARTED.md";
    public static string UserGuide => $"{RepoUrl}/blob/{Ref}/USER_GUIDE.md";
    public static string Commands => $"{RepoUrl}/blob/{Ref}/COMMANDS.md";
    public static string Changelog => $"{RepoUrl}/blob/{Ref}/CHANGELOG.md";
    public static string Install => $"{RepoUrl}/blob/{Ref}/INSTALL.md";

    // Fallback for Help → Command Cheatsheet when the bundled HTML next to the EXE is missing.
    public static string CommandCheatsheet => $"{RepoUrl}/blob/{Ref}/docs/command-cheatsheet.html";
}
