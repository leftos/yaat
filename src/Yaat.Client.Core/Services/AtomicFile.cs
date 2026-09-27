using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>Crash-safe whole-file writes: the contents go to a <c>.tmp</c> sibling that then replaces the target.</summary>
public static class AtomicFile
{
    private static readonly ILogger Log = AppLog.CreateLogger("AtomicFile");

    private const int MaxMoveAttempts = 8;

    private const int RetryStepMs = 25;

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> so a crash mid-write never leaves it truncated.
    /// A replacing move is denied while another process holds the target open without delete sharing, as an
    /// antivirus scan of a file just written does for a few milliseconds; the move is retried with a growing wait
    /// (about 0.7 s in all) before the last failure is thrown.
    /// </summary>
    /// <param name="path">The file to replace.</param>
    /// <param name="contents">Its new contents.</param>
    public static void WriteAllText(string path, string contents)
    {
        string tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, contents);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmpPath, path, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is UnauthorizedAccessException or IOException) && (attempt < MaxMoveAttempts))
            {
                Log.LogDebug(ex, "Replacing {Path} failed on attempt {Attempt}; retrying", path, attempt);
                Thread.Sleep(RetryStepMs * attempt);
            }
        }
    }
}
