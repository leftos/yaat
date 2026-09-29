using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// Shows a file in the OS file manager — Explorer selecting the file on Windows, Finder revealing it
/// on macOS, and the containing folder on Linux. Used after a bug report bundle is written so the user
/// can drag it into the GitHub issue they were just sent to; the picker's own dialog cannot be reused
/// because the file already exists. Never throws: a platform that refuses is logged, not surfaced.
/// </summary>
public static class FileReveal
{
    private static readonly ILogger Log = AppLog.CreateLogger("FileReveal");

    public static void Show(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var revealed = Process.Start("explorer.exe", $"/select,\"{path}\"");
                return;
            }

            var startInfo = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open");
            if (OperatingSystem.IsMacOS())
            {
                startInfo.ArgumentList.Add("-R");
                startInfo.ArgumentList.Add(path);
            }
            else
            {
                startInfo.ArgumentList.Add(Path.GetDirectoryName(path) ?? path);
            }

            using var started = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to reveal {Path} in the file manager", path);
        }
    }
}
