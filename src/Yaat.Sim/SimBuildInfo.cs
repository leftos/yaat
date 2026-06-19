using System.Reflection;

namespace Yaat.Sim;

/// <summary>
/// Identifies the running build of the shared simulation library. The version string comes from
/// <see cref="AssemblyInformationalVersionAttribute"/>, which the .NET SDK populates from the
/// repo's <c>Directory.Build.props</c> <c>&lt;Version&gt;</c>.
/// </summary>
/// <remarks>
/// On the server this is the authoritative simulation code that executed a session and produced a
/// recording — Yaat.Server itself carries no independent version, so the Yaat.Sim version is the
/// meaningful "what ran" identifier. In the client process it matches the client version, since
/// Yaat.Client and Yaat.Sim are built from the same <c>Directory.Build.props</c>.
/// </remarks>
public static class SimBuildInfo
{
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var attr = typeof(SimBuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (attr is null)
        {
            return "unknown";
        }

        // SourceLink, when enabled, appends "+<git-sha>". Strip that for display purposes.
        var v = attr.InformationalVersion;
        var plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? v[..plus] : v;
    }
}
