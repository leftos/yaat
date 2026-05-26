namespace Yaat.Client.Services;

/// <summary>
/// Process-wide app-shutdown signal. Set true once any code path has committed to ending the
/// app (MainWindow close confirmed, File &gt; Exit invoked, Console.CancelKeyPress, Velopack
/// restart, …) so pop-out window <c>Closing</c> handlers can distinguish a user-initiated
/// pop-out close (revert pop-out flag in preferences) from an app-shutdown close (preserve
/// pop-out flag so next launch restores the same layout).
/// </summary>
public static class AppLifetime
{
    public static bool IsShuttingDown { get; private set; }

    public static void MarkShuttingDown()
    {
        IsShuttingDown = true;
    }
}
