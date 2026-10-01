using Avalonia.Controls;

namespace Yaat.Client.Automation;

/// <summary>Where the automation host listens and is advertised, and the automation-mode guard that starts it.</summary>
public static class AutomationHostFactory
{
    // Deliberately not YaatPaths: the MCP must find a client whatever YAAT_APPDATA_DIR either process runs with.
    /// <summary>The discovery directory, <c>%TEMP%/yaat-automation</c>.</summary>
    public static readonly string DiscoveryDirectory = Path.Combine(Path.GetTempPath(), "yaat-automation");

    /// <summary>The pipe name for process <paramref name="pid"/>: <c>yaat-automation-&lt;pid&gt;</c>.</summary>
    public static string PipeName(int pid) => $"yaat-automation-{pid}";

    /// <summary>
    /// Starts an <see cref="AutomationHost"/> when <paramref name="isEnabled"/> (automation mode) is on, and returns it;
    /// returns null without opening a pipe or writing a file when it is off.
    /// </summary>
    public static AutomationHost? StartIfEnabled(
        bool isEnabled,
        string pipeName,
        string discoveryDirectory,
        Func<IEnumerable<TopLevel>> rootsProvider
    )
    {
        if (!isEnabled)
        {
            return null;
        }

        var host = new AutomationHost(pipeName, discoveryDirectory, rootsProvider);
        try
        {
            host.Start();
        }
        catch (Exception)
        {
            host.Dispose();
            throw;
        }

        return host;
    }
}
