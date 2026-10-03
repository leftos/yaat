namespace Yaat.ClientDriver.Mcp.Recording;

/// <summary>
/// Finds the window recorder helper (tools/Yaat.WindowRecorder) that the build copies under <c>recorder/</c> beside this
/// assembly, so the server starts the copy that travels with its own build output rather than one in another bin folder.
/// </summary>
public static class RecorderLocator
{
    private const string RecorderFolder = "recorder";
    private const string RecorderExe = "Yaat.WindowRecorder.exe";

    /// <summary>The recorder's exe next to this assembly.</summary>
    /// <exception cref="InvalidOperationException">The build output carries no recorder.</exception>
    public static string FindRecorder() => FindRecorder(AppContext.BaseDirectory);

    /// <summary>The recorder's exe under <paramref name="baseDirectory"/>'s <c>recorder/</c> folder.</summary>
    /// <param name="baseDirectory">The folder the MCP server runs from.</param>
    /// <exception cref="InvalidOperationException">The folder carries no recorder.</exception>
    public static string FindRecorder(string baseDirectory)
    {
        string path = Path.Combine(baseDirectory, RecorderFolder, RecorderExe);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The window recorder is missing: no {RecorderExe} at {path}. "
                    + $"Rebuild tools/Yaat.ClientDriver.Mcp, which copies tools/Yaat.WindowRecorder's output under {RecorderFolder}/."
            );
        }

        return path;
    }
}
