using System.Reflection;
using Velopack.Locators;

namespace Yaat.Client.Services;

/// <summary>
/// Identifies the running build: version string from <see cref="AssemblyInformationalVersionAttribute"/>
/// (which the .NET SDK populates from <c>Directory.Build.props</c>'s <c>&lt;Version&gt;</c>),
/// and whether the app is installed via Velopack ("release") or running as a dev build.
/// </summary>
/// <remarks>
/// The release/dev distinction comes from <see cref="VelopackLocator.Current"/>'s
/// <see cref="IVelopackLocator.CurrentlyInstalledVersion"/>, which is non-null only when the
/// process was launched from a Velopack install layout. The locator is initialized by
/// <c>VelopackApp.Build().Run()</c> in <c>Program.Main</c>, so this type must not be touched
/// before that call.
/// </remarks>
public static class BuildInfo
{
    public static string Version { get; } = ReadVersion();

    public static bool IsInstalledRelease { get; } = DetectInstalledRelease();

    public static string BuildKind => IsInstalledRelease ? "release" : "dev build";

    /// <summary>Title-bar suffix: "0.1.1-alpha" for release, "0.1.1-alpha (dev build)" otherwise.</summary>
    public static string TitleSuffix => IsInstalledRelease ? Version : $"{Version} (dev build)";

    /// <summary>Single-line build identifier intended for the top of yaat-client.log.</summary>
    public static string LogSummary => IsInstalledRelease ? $"YAAT {Version} — release" : $"YAAT {Version} — dev build (not installed via Velopack)";

    private static string ReadVersion()
    {
        var attr = typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (attr is null)
        {
            return "unknown";
        }

        // SourceLink, when enabled, appends "+<git-sha>". Strip that for display purposes.
        var v = attr.InformationalVersion;
        var plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? v[..plus] : v;
    }

    private static bool DetectInstalledRelease()
    {
        try
        {
            return VelopackLocator.Current?.CurrentlyInstalledVersion is not null;
        }
        catch
        {
            return false;
        }
    }
}
