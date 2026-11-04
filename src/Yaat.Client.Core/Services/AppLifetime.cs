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

    public static void MarkShuttingDown() => IsShuttingDown = true;

    /// <summary>
    /// Test-only: clears the flag so a later test in the same process starts from a live app. The running app never
    /// calls this — its lifetime is one process, so the flag stays latched from the first committed shutdown.
    /// </summary>
    internal static void ResetForTests() => IsShuttingDown = false;
}
