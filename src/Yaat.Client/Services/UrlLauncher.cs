using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// Opens HTTPS URLs in the user's default browser. Wraps the cross-platform
/// <see cref="Process.Start(ProcessStartInfo)"/> + <see cref="ProcessStartInfo.UseShellExecute"/> idiom
/// so callers don't have to remember the flag — without it, .NET tries to invoke the URL as an executable.
/// </summary>
public static class UrlLauncher
{
    private static readonly ILogger Log = AppLog.CreateLogger("UrlLauncher");

    public static void OpenInBrowser(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to open URL {Url} in default browser", url);
        }
    }
}
