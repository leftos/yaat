namespace Yaat.Client.Services.Speech;

/// <summary>
/// Runs engine swaps under the pipeline's warm-up, so the mic shows Warming and then Idle, only while the mic rests (Idle,
/// ModelMissing or Error): a swap requested while the mic records, transcribes or maps waits for the next resting status,
/// so the transmission is not lost. The move into Warming is checked and made atomically
/// (<see cref="SpeechRecognitionService.TryBeginWarming"/>), so a push-to-talk that beats a deferred swap keeps recording
/// and the swap waits again. Requests made while busy share one pending swap (the swap reads the latest preferences when
/// it runs). With speech switched off there is no mic status to show, so a swap only swaps.
/// </summary>
public sealed class SttEngineSwapScheduler
{
    private readonly SpeechRecognitionService _speech;
    private readonly Func<Task> _swap;

    /// <summary>Guards <see cref="_pending"/>; always taken before the pipeline's status lock.</summary>
    private readonly Lock _gate = new();
    private TaskCompletionSource? _pending;

    /// <summary>Listens to <paramref name="speech"/>'s status for the resting status that runs a pending swap.</summary>
    /// <param name="speech">The speech pipeline whose status says when a swap is safe.</param>
    /// <param name="swap">Swaps the engine in; runs under the pipeline's warm-up while speech is on.</param>
    public SttEngineSwapScheduler(SpeechRecognitionService speech, Func<Task> swap)
    {
        _speech = speech;
        _swap = swap;
        speech.StatusChanged += OnStatusChanged;
    }

    private enum Start
    {
        Busy,
        Warm,
        SwapOnly,
    }

    /// <summary>
    /// Runs the swap now when nothing is pending and the mic rests; otherwise joins the pending swap, which runs at the next
    /// resting status. The task completes when the swap it ran (or joined) finishes.
    /// </summary>
    public Task RequestAsync()
    {
        Start start;
        lock (_gate)
        {
            start = _pending is null ? TryStart() : Start.Busy;
            if (start == Start.Busy)
            {
                _pending ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _pending.Task;
            }
        }
        return Run(start);
    }

    /// <summary>Called under <see cref="_gate"/>; with speech on, moves the mic into Warming when it rests.</summary>
    private Start TryStart()
    {
        if (!_speech.IsSpeechEnabled)
        {
            return (_speech.Status is SpeechStatus.Idle or SpeechStatus.ModelMissing or SpeechStatus.Error) ? Start.SwapOnly : Start.Busy;
        }
        return _speech.TryBeginWarming() ? Start.Warm : Start.Busy;
    }

    private Task Run(Start start) => start == Start.Warm ? _speech.WarmAsync(_swap, CancellationToken.None) : _swap();

    // The status argument may be stale (status events are raised outside the pipeline's lock); TryStart reads the live one.
    private void OnStatusChanged(SpeechStatus status)
    {
        if (status is not (SpeechStatus.Idle or SpeechStatus.ModelMissing or SpeechStatus.Error))
        {
            return;
        }
        TaskCompletionSource pending;
        Start start;
        lock (_gate)
        {
            if (_pending is null)
            {
                return;
            }
            start = TryStart();
            if (start == Start.Busy)
            {
                return;
            }
            pending = _pending;
            _pending = null;
        }
        // Off the status event: the event may be raised on the UI thread or inside the pipeline.
        _ = Task.Run(async () =>
        {
            try
            {
                await Run(start).ConfigureAwait(false);
                pending.TrySetResult();
            }
            catch (Exception ex)
            {
                pending.TrySetException(ex);
            }
        });
    }
}
