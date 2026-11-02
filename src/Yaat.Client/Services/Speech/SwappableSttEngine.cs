using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Speech;

namespace Yaat.Client.Services.Speech;

/// <summary>
/// An <see cref="ISttEngine"/> whose engine can be replaced while the app runs. Warm-ups, transcriptions, swaps and
/// <see cref="Dispose"/> share one gate, so a transcription started mid-swap waits for the swap and then runs on the new
/// engine, and a dispose waits for the call in flight. The replaced engine is disposed on the thread pool, never on the
/// caller's thread, since a sherpa engine's dispose waits for a running load. Every call after <see cref="Dispose"/>
/// throws <see cref="ObjectDisposedException"/>.
/// </summary>
/// <param name="first">The engine transcriptions run on until the first swap.</param>
public sealed class SwappableSttEngine(ISttEngine first) : ISttEngine
{
    private static readonly ILogger Log = AppLog.CreateLogger<SwappableSttEngine>();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private volatile ISttEngine _current = first;
    private volatile bool _disposed;
    private long _latestRequest;

    /// <summary>The engine transcriptions run on now.</summary>
    public ISttEngine Current => _current;

    /// <summary>The mode of the engine transcriptions run on now.</summary>
    public TranscriptionMode Mode => _current.Mode;

    public async Task WarmUpAsync(CancellationToken ct)
    {
        await EnterAsync(ct).ConfigureAwait(false);
        try
        {
            await _current.WarmUpAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SttHypotheses> TranscribeAsync(float[] samples16k, string? prompt, CancellationToken ct)
    {
        await EnterAsync(ct).ConfigureAwait(false);
        try
        {
            return await _current.TranscribeAsync(samples16k, prompt, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Warms <paramref name="next"/> up and makes it the engine transcriptions run on, then disposes the replaced engine
    /// on the thread pool. A warm-up that throws leaves the current engine in place, disposes <paramref name="next"/> and
    /// rethrows; so does a swap that finds this wrapper disposed.
    /// </summary>
    /// <param name="next">The engine to switch to; this wrapper owns it from now on.</param>
    /// <param name="ct">Cancels waiting for the gate and the warm-up.</param>
    /// <exception cref="ObjectDisposedException">This wrapper was disposed.</exception>
    public async Task SwapAsync(ISttEngine next, CancellationToken ct)
    {
        ISttEngine replaced;
        try
        {
            await EnterAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
            await DisposeOffThreadAsync(next).ConfigureAwait(false);
            throw;
        }
        try
        {
            try
            {
                await next.WarmUpAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "The new speech-recognition engine failed to warm up; keeping the current one");
                await DisposeOffThreadAsync(next).ConfigureAwait(false);
                throw;
            }
            replaced = _current;
            _current = next;
        }
        finally
        {
            _gate.Release();
        }

        Log.LogInformation("Speech-recognition engine swapped to {Engine}", next.GetType().Name);
        await DisposeOffThreadAsync(replaced).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds an engine with <paramref name="build"/> and swaps to it, unless a later request started while it was being
    /// built: then the built engine is disposed on the thread pool and the later request's engine wins, whichever build
    /// finishes first.
    /// </summary>
    /// <param name="build">Builds the engine to switch to.</param>
    /// <param name="ct">Cancels the swap.</param>
    /// <returns>True when this request's engine was swapped in; false when a later request superseded it.</returns>
    /// <exception cref="ObjectDisposedException">This wrapper was disposed.</exception>
    public async Task<bool> SwapToLatestAsync(Func<Task<ISttEngine>> build, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long request = Interlocked.Increment(ref _latestRequest);
        ISttEngine next = await build().ConfigureAwait(false);
        await _applyGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (request != Interlocked.Read(ref _latestRequest))
            {
                Log.LogInformation("A later speech-recognition engine request superseded {Engine}; discarding it", next.GetType().Name);
                await DisposeOffThreadAsync(next).ConfigureAwait(false);
                return false;
            }
            await SwapAsync(next, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _applyGate.Release();
        }
    }

    /// <summary>Waits for the call in flight, then disposes the current engine.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _gate.Wait(CancellationToken.None);
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _current.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Takes the gate; throws, holding nothing, when this wrapper is or becomes disposed.</summary>
    private async Task EnterAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        if (_disposed)
        {
            _gate.Release();
            throw new ObjectDisposedException(nameof(SwappableSttEngine));
        }
    }

    private static Task DisposeOffThreadAsync(ISttEngine engine) =>
        Task.Run(() =>
        {
            try
            {
                engine.Dispose();
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "Disposing the speech-recognition engine {Engine} failed", engine.GetType().Name);
            }
        });
}
