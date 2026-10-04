namespace Yaat.ClientDriver.Mcp.Recording;

/// <summary>A process's top-level window as a recording targets it.</summary>
/// <param name="Hwnd">The native handle; 0 when it has none.</param>
/// <param name="Title">The window title, or null when it has none.</param>
public sealed record TopLevelWindow(long Hwnd, string? Title);

/// <summary>The recorder's <c>--probe</c> answer: the frame size it will send, or why it refused.</summary>
/// <param name="Width">The frame width; 0 on a refusal.</param>
/// <param name="Height">The frame height; 0 on a refusal.</param>
/// <param name="Refusal">The recorder's reason and exit code when it refused; null when it answered a size.</param>
public sealed record RecorderProbe(int Width, int Height, string? Refusal);

/// <summary>The running recorder-into-ffmpeg pipeline.</summary>
public interface IRecordingProcess : IDisposable
{
    /// <summary>True once the pipeline has ended.</summary>
    bool HasExited { get; }

    /// <summary>The pipeline's exit code (ffmpeg's, the last stage); read only once <see cref="HasExited"/>.</summary>
    int ExitCode { get; }

    /// <summary>Waits up to <paramref name="timeout"/> for the pipeline to end; false when it is still running.</summary>
    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the recorder, the pipeline's first stage, to end; false when it is still
    /// running. Its end closes ffmpeg's input, so ffmpeg finishes the MP4 after it.
    /// </summary>
    Task<bool> WaitForRecorderExitAsync(TimeSpan timeout, CancellationToken ct);

    /// <summary>Kills the recorder alone, which ends ffmpeg's input and lets ffmpeg finish the MP4.</summary>
    void KillRecorder();

    /// <summary>Kills the pipeline and everything it started.</summary>
    void KillTree();
}

/// <summary>
/// Everything a recording does outside this process: finding ffmpeg and the recorder, choosing the encoder, reading the
/// window, probing it, starting the pipeline and moving its deadline. The real one runs processes and reads windows; the
/// tests use a fake, so the recording tools are tested without a GPU, ffmpeg or a window.
/// </summary>
public interface IRecordingBackend
{
    /// <summary>The full path of ffmpeg on PATH, or null when there is none.</summary>
    string? FindFfmpeg();

    /// <summary>The window recorder's exe.</summary>
    /// <exception cref="InvalidOperationException">The build output carries no recorder.</exception>
    string FindRecorder();

    /// <summary>The video encoder this machine's <paramref name="ffmpeg"/> encodes with.</summary>
    Task<string> ChooseEncoderAsync(string ffmpeg, CancellationToken ct);

    /// <summary>The top-level windows of a process with no automation pipe, through UI Automation.</summary>
    IReadOnlyList<TopLevelWindow> TopLevelWindows(int pid);

    /// <summary>The window's frame, client area and minimized state, or null when the handle names no window.</summary>
    WindowGeometry? ReadWindow(long hwnd);

    /// <summary>Runs the recorder with <paramref name="arguments"/> (a <c>--probe</c>) and reads its answer.</summary>
    Task<RecorderProbe> ProbeAsync(string recorder, IReadOnlyList<string> arguments, CancellationToken ct);

    /// <summary>Starts the pipeline: <paramref name="commandLine"/> under <c>cmd /c</c>, with its path variables set.</summary>
    IRecordingProcess Start(PipelineCommandLine commandLine);

    /// <summary>Writes <paramref name="endUtc"/> to the deadline file at <paramref name="path"/>, atomically.</summary>
    void WriteDeadline(string path, DateTime endUtc);
}
