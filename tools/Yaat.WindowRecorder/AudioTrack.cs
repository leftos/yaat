using System.IO.Pipes;
using System.Threading.Channels;

namespace Yaat.WindowRecorder;

/// <summary>
/// One run's audio: a process-loopback capture and where its raw s16le goes, a named pipe for ffmpeg to open as a second
/// input or, without one, stdout. The pipe server exists from <see cref="Open"/> on, so it is there before ffmpeg opens it.
/// </summary>
internal sealed class AudioTrack(int processId, ProcessAudioCapture capture, NamedPipeServerStream? pipe) : IDisposable
{
    private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(15);

    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );

    /// <summary>Activates the capture and creates the pipe; throws <see cref="AudioActivationException"/> when activation fails.</summary>
    public static AudioTrack Open(int processId, string? pipeName)
    {
        ProcessAudioCapture capture = new(processId);
        NamedPipeServerStream? pipe = pipeName is null
            ? null
            : new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
        return new AudioTrack(processId, capture, pipe);
    }

    /// <summary>
    /// Records audio until <paramref name="deadline"/> passes. With <paramref name="startOnConnect"/> the clock starts when
    /// the reader opens the pipe (audio alone, where the reader's other inputs start with it); without it the clock starts
    /// now and the audio is held until the reader connects (beside the video, whose first frame is the run's start). Either
    /// way the audio ends at the deadline. Returns false when the reader never connected.
    /// </summary>
    public async Task<bool> RecordAsync(Deadline deadline, bool startOnConnect)
    {
        Task<bool> connecting = ConnectAsync();
        if (startOnConnect)
        {
            if (!await connecting.ConfigureAwait(false))
            {
                return false;
            }
            deadline.Start();
        }
        await Console
            .Error.WriteLineAsync(
                $"Yaat.WindowRecorder: audio of process {processId} and its children, {ProcessAudioCapture.SampleRate} Hz stereo s16le {deadline.Describe()}"
            )
            .ConfigureAwait(false);
        Task<long> capturing = Task.Factory.StartNew(
            () => capture.Run(deadline, _chunks.Writer),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
        bool connected = await connecting.ConfigureAwait(false);
        long written = connected ? await CopyAsync().ConfigureAwait(false) : 0;
        long captured = await capturing.ConfigureAwait(false);
        await Console
            .Error.WriteLineAsync(
                $"Yaat.WindowRecorder: audio {written} frames written; {captured} came from the loopback stream and the rest is silence filling"
            )
            .ConfigureAwait(false);
        return connected;
    }

    public void Dispose()
    {
        pipe?.Dispose();
        capture.Dispose();
    }

    private async Task<bool> ConnectAsync()
    {
        if (pipe is null)
        {
            return true;
        }
        using CancellationTokenSource timeout = new(ConnectWait);
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            await Console
                .Error.WriteLineAsync($"Yaat.WindowRecorder: nothing opened the audio pipe in {ConnectWait.TotalSeconds} s; no audio was written.")
                .ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>Copies the captured chunks to the reader; returns how many frames reached it.</summary>
    private async Task<long> CopyAsync()
    {
        Stream output = pipe ?? Console.OpenStandardOutput();
        long frames = 0;
        try
        {
            await foreach (byte[] chunk in _chunks.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await output.WriteAsync(chunk).ConfigureAwait(false);
                frames += chunk.Length / ProcessAudioCapture.BytesPerFrame;
            }
            await output.FlushAsync().ConfigureAwait(false);
            // Closing the server end must not cut off what ffmpeg has not read yet, so the run ends once the pipe is drained.
            pipe?.WaitForPipeDrain();
        }
        catch (IOException closed)
        {
            // ffmpeg closes its inputs when it stops (on q, at the same deadline), which can land before the last few
            // milliseconds were written.
            await Console
                .Error.WriteLineAsync($"Yaat.WindowRecorder: the reader closed the audio stream early ({closed.Message}).")
                .ConfigureAwait(false);
        }
        finally
        {
            if (pipe is null)
            {
                await output.DisposeAsync().ConfigureAwait(false);
            }
        }
        return frames;
    }
}
