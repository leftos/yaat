using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim;

/// <summary>
/// File primitives shared by the scenario store, the ARTCC list and the downloader: an atomic whole-file write and the id
/// rule every file and folder name of the store follows. They live in the root namespace so <c>Yaat.Sim.Data.Vnas</c>
/// does not depend on <c>Yaat.Sim.Authoring</c> for them.
/// </summary>
internal static partial class StoreFiles
{
    private const int MaxMoveAttempts = 5;

    // Short steps (10, 20, 30, 40 ms): when a caller holds a lock, other workers wait on it.
    private const int MoveRetryStepMs = 10;

    private static readonly ILogger Log = SimLog.CreateLogger("StoreFiles");

    /// <summary>
    /// Whether <paramref name="id"/> is a valid scenario or ARTCC id: letters, digits, '_' and '-' only, and not a
    /// Windows reserved device name (CON, PRN, AUX, NUL, COM1-9, LPT1-9, any case).
    /// </summary>
    /// <param name="id">The id to check.</param>
    /// <returns>True when the id can name a file or folder of the store.</returns>
    internal static bool IsValidId(string? id) => (id is not null) && IdPattern().IsMatch(id) && !ReservedNamePattern().IsMatch(id);

    /// <summary>Writes <paramref name="text"/> to <paramref name="path"/> in one step, so no reader sees a half-written file.</summary>
    /// <remarks>
    /// It writes a uniquely named sibling, then replaces the target, so a reader sees the old file or the new one. The
    /// replacing move is retried briefly because an antivirus scan can hold a just-written target.
    /// </remarks>
    /// <param name="path">The file to create or replace; its folder is created when it is missing.</param>
    /// <param name="text">The text to write.</param>
    internal static void WriteAtomically(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmpPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmpPath, text);
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tmpPath, path, overwrite: true);
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
        finally
        {
            DeleteTemp(tmpPath);
        }
    }

    // A temp file left behind is logged, never thrown: it must not replace the error that left it.
    private static void DeleteTemp(string tmpPath)
    {
        try
        {
            if (File.Exists(tmpPath))
            {
                File.Delete(tmpPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Could not delete the temporary file {Path}", tmpPath);
            }
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]+\z")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedNamePattern();
}
