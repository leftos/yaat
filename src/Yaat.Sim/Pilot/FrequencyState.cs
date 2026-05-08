using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Pilot;

public sealed class FrequencyState
{
    private const double MinimumAirtimeSeconds = 1.0;
    private const double SecondsPerWord = 0.25;

    /// <summary>
    /// Maximum wall-clock seconds the awaited-readback gate holds back other
    /// transmissions. Real readbacks land in 1-2 seconds; this is generous to absorb
    /// dispatch latency and a backlog of higher-priority transmissions, but bounded
    /// so a missing readback (e.g. aircraft deleted between dispatch and drain) can
    /// never silence every other pilot indefinitely. After the timeout, the gate
    /// degrades to normal FIFO order — the readback can still emerge first if it's
    /// already at the head, just without veto power over the rest of the queue.
    /// </summary>
    private const double AwaitedReadbackTimeoutSeconds = 8.0;

    private static readonly ILogger Log = SimLog.CreateLogger("FrequencyState");

    private readonly Queue<PilotTransmission> _pending = [];
    private double _nextAvailableAtSeconds;
    private string? _awaitingReadbackFrom;
    private double _awaitingReadbackSinceSeconds;

    public FrequencyActivityMeter ActivityMeter { get; } = new();
    public int PendingCount => _pending.Count;
    public string? AwaitingReadbackFrom => _awaitingReadbackFrom;

    public FrequencyActivityLevel GetActivityLevel(double elapsedSeconds)
    {
        ActivityMeter.Trim(elapsedSeconds);
        return ActivityMeter.Level;
    }

    public void ExpectReadback(string callsign, double elapsedSeconds)
    {
        if (!string.IsNullOrWhiteSpace(callsign))
        {
            _awaitingReadbackFrom = callsign;
            _awaitingReadbackSinceSeconds = elapsedSeconds;
        }
    }

    public void Enqueue(PilotTransmission transmission)
    {
        _pending.Enqueue(transmission);
    }

    public PilotTransmission? TryDequeueReady(double elapsedSeconds)
    {
        ActivityMeter.Trim(elapsedSeconds);
        if (_pending.Count == 0 || elapsedSeconds < _nextAvailableAtSeconds)
        {
            return null;
        }

        var index = SelectReadyIndex(elapsedSeconds);
        if (index < 0)
        {
            return null;
        }

        var transmission = DequeueAt(index);
        if (
            transmission.Kind == PilotTransmissionKind.Readback
            && string.Equals(_awaitingReadbackFrom, transmission.Callsign, StringComparison.OrdinalIgnoreCase)
        )
        {
            _awaitingReadbackFrom = null;
        }

        _nextAvailableAtSeconds = elapsedSeconds + EstimateAirtimeSeconds(transmission.SpeechText);
        ActivityMeter.Record(elapsedSeconds);
        return transmission;
    }

    public void Clear()
    {
        _pending.Clear();
        _awaitingReadbackFrom = null;
        _awaitingReadbackSinceSeconds = 0;
        _nextAvailableAtSeconds = 0;
        ActivityMeter.Trim(double.PositiveInfinity);
    }

    private int SelectReadyIndex(double elapsedSeconds)
    {
        if (_awaitingReadbackFrom is { Length: > 0 } callsign)
        {
            int index = 0;
            foreach (var transmission in _pending)
            {
                if (
                    transmission.Kind == PilotTransmissionKind.Readback
                    && string.Equals(transmission.Callsign, callsign, StringComparison.OrdinalIgnoreCase)
                )
                {
                    return index;
                }

                index++;
            }

            // The expected readback hasn't reached the queue yet. Hold other
            // transmissions back so the readback gets airtime priority — but only
            // for AwaitedReadbackTimeoutSeconds. Past that, fall through to FIFO so
            // a missing readback (deleted aircraft, future bug, etc.) can't freeze
            // the frequency. The awaited callsign stays set: if the readback shows
            // up later it will dequeue normally; the gate just stops vetoing others.
            double waitedSeconds = elapsedSeconds - _awaitingReadbackSinceSeconds;
            if (waitedSeconds < AwaitedReadbackTimeoutSeconds)
            {
                Log.LogDebug(
                    "Holding frequency for {Callsign} readback ({Waited:F1}s waited); {Count} other transmissions queued",
                    callsign,
                    waitedSeconds,
                    _pending.Count
                );
                return -1;
            }

            Log.LogDebug("Awaited readback from {Callsign} timed out after {Waited:F1}s; releasing frequency to FIFO", callsign, waitedSeconds);
            _awaitingReadbackFrom = null;
            return _pending.Count > 0 ? 0 : -1;
        }

        return 0;
    }

    private PilotTransmission DequeueAt(int targetIndex)
    {
        PilotTransmission? result = null;
        int originalCount = _pending.Count;
        for (int i = 0; i < originalCount; i++)
        {
            var item = _pending.Dequeue();
            if (i == targetIndex)
            {
                result = item;
            }
            else
            {
                _pending.Enqueue(item);
            }
        }

        return result ?? throw new InvalidOperationException("Frequency queue index was invalid");
    }

    private static double EstimateAirtimeSeconds(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        return Math.Max(MinimumAirtimeSeconds, words * SecondsPerWord);
    }
}
