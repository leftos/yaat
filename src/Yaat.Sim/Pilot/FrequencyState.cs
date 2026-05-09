using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Pilot;

public sealed class FrequencyState
{
    private const double MinimumAirtimeSeconds = 1.0;
    private const double SecondsPerWord = 0.25;

    /// <summary>
    /// Maximum wall-clock seconds either gate (awaited-readback or
    /// awaited-controller-response) holds back other transmissions. Real readbacks land in
    /// 1-2 seconds; controllers typically respond in a few seconds. This is generous to
    /// absorb dispatch latency and a backlog of higher-priority transmissions, but bounded
    /// so a missing readback / unresponsive controller (e.g. aircraft deleted, scenario
    /// quirk) can never silence every other pilot indefinitely. After the timeout the gate
    /// degrades to normal FIFO order — the held transmission can still emerge first if it's
    /// already at the head, just without veto power over the rest of the queue.
    /// </summary>
    private const double AwaitedTimeoutSeconds = 8.0;

    private static readonly ILogger Log = SimLog.CreateLogger("FrequencyState");

    private readonly Queue<PilotTransmission> _pending = [];
    private double _nextAvailableAtSeconds;
    private string? _awaitingReadbackFrom;
    private double _awaitingReadbackSinceSeconds;
    private string? _awaitingControllerResponseTo;
    private double _awaitingControllerResponseSinceSeconds;

    public FrequencyActivityMeter ActivityMeter { get; } = new();
    public int PendingCount => _pending.Count;
    public string? AwaitingReadbackFrom => _awaitingReadbackFrom;
    public string? AwaitingControllerResponseTo => _awaitingControllerResponseTo;

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

    /// <summary>
    /// Clears the awaiting-controller-response gate when the callsign matches. Called from
    /// <see cref="SimulationWorld.AcknowledgeControllerResponse"/> after a successful
    /// solo-mode controller dispatch, so other pilots can resume mic'ing up.
    /// </summary>
    public void AcknowledgeControllerResponse(string callsign)
    {
        if (!string.IsNullOrWhiteSpace(callsign) && string.Equals(_awaitingControllerResponseTo, callsign, StringComparison.OrdinalIgnoreCase))
        {
            Log.LogInformation("Controller acknowledged {Callsign}; releasing awaiting-controller-response gate", callsign);
            _awaitingControllerResponseTo = null;
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

        double airtime = EstimateAirtimeSeconds(transmission.SpeechText);
        _nextAvailableAtSeconds = elapsedSeconds + airtime;

        // A pilot-initiated call (Proactive/Report) is implicitly addressed to the
        // controller and expects a response. Hold other pilots' proactive calls until
        // the controller dispatches to this callsign (gate cleared by
        // SimulationEngine.SendCommand → SimulationWorld.AcknowledgeControllerResponse)
        // or until the AwaitedTimeoutSeconds ceiling falls through to FIFO.
        //
        // Start the timer at end-of-transmission, not start, so the airtime doesn't
        // eat into the controller's response window — the timeout means "N seconds of
        // silence after the pilot stops speaking", which is what radio etiquette
        // models.
        if (transmission.Kind is PilotTransmissionKind.Proactive or PilotTransmissionKind.Report)
        {
            _awaitingControllerResponseTo = transmission.Callsign;
            _awaitingControllerResponseSinceSeconds = _nextAvailableAtSeconds;
            Log.LogInformation(
                "Awaiting controller response from {Callsign} ({Kind} ends at t={EndSeconds:F1}s, airtime={Airtime:F1}s); other-pilot proactive/report transmissions held until response or {Timeout}s of silence",
                transmission.Callsign,
                transmission.Kind,
                _nextAvailableAtSeconds,
                airtime,
                AwaitedTimeoutSeconds
            );
        }

        ActivityMeter.Record(elapsedSeconds);
        return transmission;
    }

    public void Clear()
    {
        _pending.Clear();
        _awaitingReadbackFrom = null;
        _awaitingReadbackSinceSeconds = 0;
        _awaitingControllerResponseTo = null;
        _awaitingControllerResponseSinceSeconds = 0;
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
            // for AwaitedTimeoutSeconds. Past that, fall through to FIFO so
            // a missing readback (deleted aircraft, future bug, etc.) can't freeze
            // the frequency. The awaited callsign stays set: if the readback shows
            // up later it will dequeue normally; the gate just stops vetoing others.
            double waitedSeconds = elapsedSeconds - _awaitingReadbackSinceSeconds;
            if (waitedSeconds < AwaitedTimeoutSeconds)
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
            // Fall through to the controller-response gate / FIFO below.
        }

        if (_awaitingControllerResponseTo is { Length: > 0 } awaitingCallsign)
        {
            // Allow anything that isn't another pilot's proactive/report through.
            // The awaiting pilot themselves can keep talking (follow-ups, additional
            // requests). Readbacks and SayReadback are responses to controller-issued
            // commands and aren't gated here.
            int index = 0;
            foreach (var transmission in _pending)
            {
                bool isOtherPilotProactiveOrReport =
                    transmission.Kind is PilotTransmissionKind.Proactive or PilotTransmissionKind.Report
                    && !string.Equals(transmission.Callsign, awaitingCallsign, StringComparison.OrdinalIgnoreCase);

                if (!isOtherPilotProactiveOrReport)
                {
                    return index;
                }

                index++;
            }

            // Only other-pilot proactive/report transmissions are queued. Hold them
            // back until the controller responds or the timeout falls through.
            double waitedSeconds = elapsedSeconds - _awaitingControllerResponseSinceSeconds;
            if (waitedSeconds < AwaitedTimeoutSeconds)
            {
                Log.LogInformation(
                    "Holding frequency for controller response to {Callsign} ({Waited:F1}s waited); {Count} other-pilot transmissions queued",
                    awaitingCallsign,
                    waitedSeconds,
                    _pending.Count
                );
                return -1;
            }

            Log.LogWarning(
                "Awaited controller response to {Callsign} timed out after {Waited:F1}s; releasing frequency to FIFO",
                awaitingCallsign,
                waitedSeconds
            );
            _awaitingControllerResponseTo = null;
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
