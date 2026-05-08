using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Pilot;

public sealed class FrequencyState
{
    private const double MinimumAirtimeSeconds = 1.0;
    private const double SecondsPerWord = 0.25;
    private static readonly ILogger Log = SimLog.CreateLogger("FrequencyState");

    private readonly Queue<PilotTransmission> _pending = [];
    private double _nextAvailableAtSeconds;
    private string? _awaitingReadbackFrom;

    public FrequencyActivityMeter ActivityMeter { get; } = new();
    public int PendingCount => _pending.Count;
    public string? AwaitingReadbackFrom => _awaitingReadbackFrom;

    public void ExpectReadback(string callsign)
    {
        if (!string.IsNullOrWhiteSpace(callsign))
        {
            _awaitingReadbackFrom = callsign;
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

        var index = SelectReadyIndex();
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
        _nextAvailableAtSeconds = 0;
        ActivityMeter.Trim(double.PositiveInfinity);
    }

    private int SelectReadyIndex()
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

            Log.LogDebug("Waiting for readback from {Callsign}; {Count} pilot transmissions queued", callsign, _pending.Count);
            return -1;
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
