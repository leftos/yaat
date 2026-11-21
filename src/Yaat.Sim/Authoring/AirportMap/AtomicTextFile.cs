using System.Text;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Authoring.AirportMap;

/// <summary>
/// Writes a whole text file through a temp file and a replacing move, so a reader never sees a half-written file and a
/// failed write leaves the old file whole.
/// </summary>
public static class AtomicTextFile
{
    private const int MaxMoveAttempts = 8;

    private const int MoveRetryStepMs = 25;

    private static readonly ILogger Log = SimLog.CreateLogger("AtomicTextFile");

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/> as UTF-8 without a byte order mark: first to
    /// <c>{path}.{guid}.tmp</c> beside it, then moved over the target. The move is retried with a growing wait (about
    /// 0.7 s in all) while a scan or another process holds the target open, and the temp file is deleted whatever happens.
    /// </summary>
    /// <param name="path">The file to write; its folder is created when missing.</param>
    /// <param name="text">The whole new content.</param>
    /// <exception cref="IOException">The target stayed locked through every attempt, or the temp file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The target or its folder cannot be written.</exception>
    public static void WriteAllText(string path, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(text);
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            ReplaceWithRetry(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Never let the cleanup replace the error in flight; the temp file is named in the log instead.
                    if (Log.IsEnabled(LogLevel.Warning))
                    {
                        Log.LogWarning(ex, "Deleting the temporary file {Path} failed; it is left behind", tempPath);
                    }
                }
            }
        }
    }

    // The same retry PrecomputeStore.ReplaceWithRetry uses: a just-written target is often held for a few milliseconds.
    private static void ReplaceWithRetry(string tempPath, string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempPath, path, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is UnauthorizedAccessException or IOException) && (attempt < MaxMoveAttempts))
            {
                if (Log.IsEnabled(LogLevel.Debug))
                {
                    Log.LogDebug(ex, "Replacing {Path} failed on attempt {Attempt}; retrying", path, attempt);
                }
                Thread.Sleep(MoveRetryStepMs * attempt);
            }
        }
    }
}
