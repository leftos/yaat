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

    /// <summary>
    /// How long after the end of a check-in's airtime a second pilot's own check-in may butt in while the controller has
    /// not answered the first. Inside the window the second pilot draws <see cref="SpokenFormDraws.CheckInButtInPercent"/>;
    /// outside it the call keeps the ordinary hold, up to <see cref="AwaitedTimeoutSeconds"/>.
    /// </summary>
    private const double CheckInButtInDelaySeconds = 3.0;

    private static readonly ILogger Log = SimLog.CreateLogger("FrequencyState");

    private readonly Queue<PilotTransmission> _pending = [];
    private readonly Queue<AwaitedReadback> _awaitingReadbacks = [];
    private double _nextAvailableAtSeconds;
    private string? _awaitingControllerResponseTo;
    private bool _awaitingControllerResponseIsCheckIn;
    private double _awaitingControllerResponseSinceSeconds;

    public FrequencyActivityMeter ActivityMeter { get; } = new();
    public int PendingCount => _pending.Count;
    public string? AwaitingReadbackFrom => (_awaitingReadbacks.Count > 0) ? _awaitingReadbacks.Peek().Transmission.Callsign : null;
    public string? AwaitingControllerResponseTo => _awaitingControllerResponseTo;

    /// <summary>The frequency's load band at <paramref name="elapsedSeconds"/>, trimming the meter's window first.</summary>
    public FrequencyLoadBand GetLoadBand(double elapsedSeconds)
    {
        ActivityMeter.Trim(elapsedSeconds);
        return ActivityMeter.PeekLoadBand(elapsedSeconds);
    }

    /// <summary>The frequency's load band at <paramref name="elapsedSeconds"/>, read without trimming the meter.</summary>
    public FrequencyLoadBand PeekLoadBand(double elapsedSeconds) => ActivityMeter.PeekLoadBand(elapsedSeconds);

    /// <summary>
    /// Records that the controller's instructions whose applied read-backs are <paramref name="readbacks"/> await those
    /// exact transmissions. They keep their arrival order and are matched by reference: the first awaited read-back is
    /// given airtime priority over every other transmission, so a second instruction in the same tick (or before the first
    /// read-back speaks) cannot displace it, a pilot's own earlier read-back — even to the same callsign — neither
    /// satisfies nor clears the entry armed for the instruction's read-back, and a second read-back to the same aircraft
    /// keeps its own entry.
    /// </summary>
    public void ExpectReadback(IEnumerable<PilotTransmission> readbacks, double elapsedSeconds)
    {
        foreach (PilotTransmission readback in readbacks)
        {
            _awaitingReadbacks.Enqueue(new AwaitedReadback(readback, elapsedSeconds));
        }
    }

    /// <summary>
    /// Drops the awaited read-backs of an aircraft that left the world: they can never arrive, so holding the frequency
    /// for one would silence every other pilot for a transmission that no longer exists.
    /// </summary>
    public void ForgetAircraft(string callsign)
    {
        if (_awaitingReadbacks.Count == 0)
        {
            return;
        }

        AwaitedReadback[] keep =
        [
            .. _awaitingReadbacks.Where(a => !string.Equals(a.Transmission.Callsign, callsign, StringComparison.OrdinalIgnoreCase)),
        ];
        if (keep.Length == _awaitingReadbacks.Count)
        {
            return;
        }

        _awaitingReadbacks.Clear();
        foreach (AwaitedReadback entry in keep)
        {
            _awaitingReadbacks.Enqueue(entry);
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
            _awaitingControllerResponseIsCheckIn = false;
        }
    }

    public void Enqueue(PilotTransmission transmission) => _pending.Enqueue(transmission);

    /// <summary>
    /// The next transmission allowed on the air at <paramref name="elapsedSeconds"/>, or null while none is.
    /// <paramref name="rngSeed"/> is the scenario seed of every seeded draw a held call makes, so a replay reproduces the
    /// same check-in butt-in.
    /// </summary>
    public PilotTransmission? TryDequeueReady(double elapsedSeconds, int rngSeed)
    {
        ActivityMeter.Trim(elapsedSeconds);
        if (_pending.Count == 0 || elapsedSeconds < _nextAvailableAtSeconds)
        {
            return null;
        }

        int index = SelectReadyIndex(elapsedSeconds, rngSeed);
        if (index < 0)
        {
            return null;
        }

        PilotTransmission transmission = DequeueAt(index);
        if (
            (transmission.Kind == PilotTransmissionKind.Readback)
            && (_awaitingReadbacks.Count > 0)
            && ReferenceEquals(_awaitingReadbacks.Peek().Transmission, transmission)
        )
        {
            _awaitingReadbacks.Dequeue();
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
            _awaitingControllerResponseIsCheckIn = transmission.IsInitialContact;
            _awaitingControllerResponseSinceSeconds = _nextAvailableAtSeconds;
            Log.LogInformation(
                "Awaiting controller response from {Callsign} ({Kind} ends at t={EndSeconds:F1}s, airtime={Airtime:F1}s); "
                    + "other-pilot proactive/report transmissions held until response or {Timeout}s of silence",
                transmission.Callsign,
                transmission.Kind,
                _nextAvailableAtSeconds,
                airtime,
                AwaitedTimeoutSeconds
            );
        }

        // The meter takes the canonical text's airtime, not the spoken one: a busy pilot's terser read-back must not make
        // the frequency read quieter. Keyed at this second, it adds nothing yet to the band read here: the band is the
        // minute's airtime up to the second the transmission goes on air.
        ActivityMeter.Record(elapsedSeconds, transmission.CanonicalAirtimeSeconds);
        return transmission with { LoadBand = ActivityMeter.PeekLoadBand(elapsedSeconds) };
    }

    public void Clear()
    {
        _pending.Clear();
        _awaitingReadbacks.Clear();
        _awaitingControllerResponseTo = null;
        _awaitingControllerResponseIsCheckIn = false;
        _awaitingControllerResponseSinceSeconds = 0;
        _nextAvailableAtSeconds = 0;
        ActivityMeter.Restore([]);
    }

    private int SelectReadyIndex(double elapsedSeconds, int rngSeed)
    {
        if (AwaitedReadbackIndex(elapsedSeconds) is int awaitedIndex)
        {
            return awaitedIndex;
        }

        if (_awaitingControllerResponseTo is { Length: > 0 } awaitingCallsign)
        {
            return ControllerResponseGateIndex(awaitingCallsign, elapsedSeconds, rngSeed);
        }

        return 0;
    }

    /// <summary>
    /// The queue index the controller-response gate allows at <paramref name="elapsedSeconds"/>. Anything that is not
    /// another pilot's proactive/report call passes at once: the awaiting pilot can keep talking (follow-ups, additional
    /// requests), and read-backs are responses to controller-issued commands, not new requests. Only other-pilot
    /// proactive/report transmissions are held back, until the controller answers, a check-in butts in, or
    /// <see cref="AwaitedTimeoutSeconds"/> of silence fall through to FIFO.
    /// </summary>
    private int ControllerResponseGateIndex(string awaitingCallsign, double elapsedSeconds, int rngSeed)
    {
        int index = 0;
        foreach (PilotTransmission transmission in _pending)
        {
            if (!IsOtherPilotCall(transmission, awaitingCallsign))
            {
                return index;
            }

            index++;
        }

        double waitedSeconds = elapsedSeconds - _awaitingControllerResponseSinceSeconds;
        if (waitedSeconds >= AwaitedTimeoutSeconds)
        {
            Log.LogWarning(
                "Awaited controller response to {Callsign} timed out after {Waited:F1}s; releasing frequency to FIFO",
                awaitingCallsign,
                waitedSeconds
            );
            _awaitingControllerResponseTo = null;
            _awaitingControllerResponseIsCheckIn = false;
            return 0;
        }

        int buttInIndex = ButtInIndex(elapsedSeconds, rngSeed);
        if (buttInIndex >= 0)
        {
            return buttInIndex;
        }

        Log.LogInformation(
            "Holding frequency for controller response to {Callsign} ({Waited:F1}s waited); {Count} other-pilot transmissions queued",
            awaitingCallsign,
            waitedSeconds,
            _pending.Count
        );
        return -1;
    }

    /// <summary>Whether <paramref name="transmission"/> is another pilot's new request, the kind the gate holds back.</summary>
    private static bool IsOtherPilotCall(PilotTransmission transmission, string awaitingCallsign) =>
        transmission.Kind is PilotTransmissionKind.Proactive or PilotTransmissionKind.Report
        && !string.Equals(transmission.Callsign, awaitingCallsign, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The queue index of the first other-pilot initial-contact check-in whose seeded draw allows it to butt in behind the
    /// awaited check-in, or <c>-1</c> while the butt-in window is shut. The draws are made in queue order and keyed on the
    /// first call's end second, not the current tick, so a held call gets one answer that repeats until it speaks or the
    /// gate clears.
    /// </summary>
    private int ButtInIndex(double elapsedSeconds, int rngSeed)
    {
        if (!ButtInWindowOpen(elapsedSeconds))
        {
            return -1;
        }

        int firstCallEndSecond = (int)Math.Floor(_awaitingControllerResponseSinceSeconds);
        int index = 0;
        foreach (PilotTransmission transmission in _pending)
        {
            if (IsButtingIn(transmission, firstCallEndSecond, rngSeed))
            {
                Log.LogInformation(
                    "{ButtIn} butts in {Waited:F1}s after {Callsign}'s unanswered check-in",
                    transmission.Callsign,
                    elapsedSeconds - LastTransmissionEndSeconds(),
                    _awaitingControllerResponseTo
                );
                return index;
            }

            index++;
        }

        return -1;
    }

    /// <summary>
    /// Whether a second pilot's check-in may butt in now: the gate is waiting on a check-in, the moment is inside
    /// <see cref="AwaitedTimeoutSeconds"/>, and <see cref="CheckInButtInDelaySeconds"/> of silence have passed since the
    /// frequency last carried a transmission. A pilot listens and pauses after every transmission (AIM 4-2-2.a), so a
    /// winner held back by a third call keys three seconds after that call ends, not the instant it ends.
    /// </summary>
    private bool ButtInWindowOpen(double elapsedSeconds)
    {
        if (!_awaitingControllerResponseIsCheckIn)
        {
            return false;
        }

        if (elapsedSeconds - _awaitingControllerResponseSinceSeconds >= AwaitedTimeoutSeconds)
        {
            return false;
        }

        return elapsedSeconds - LastTransmissionEndSeconds() >= CheckInButtInDelaySeconds;
    }

    /// <summary>The second the frequency last carried a transmission, never earlier than the awaited check-in's end.</summary>
    private double LastTransmissionEndSeconds() => Math.Max(_awaitingControllerResponseSinceSeconds, _nextAvailableAtSeconds);

    private static bool IsButtingIn(PilotTransmission transmission, int firstCallEndSecond, int rngSeed) =>
        (transmission.Kind == PilotTransmissionKind.Proactive)
        && transmission.IsInitialContact
        && SpokenFormDraws.DrawCheckInButtIn(transmission.Callsign, firstCallEndSecond, rngSeed);

    /// <summary>
    /// The queue index of the read-back the frequency is holding for, <c>-1</c> while it is still held, or <c>null</c>
    /// when no awaited read-back is left and the caller goes on to the controller-response gate and FIFO.
    ///
    /// A read-back that is merely waiting on airtime behind a long transmission keeps its slot: the window counts from
    /// when the awaited pilot could first speak — the later of the instruction and the frequency's next free second — so
    /// waiting for airtime cannot burn it. Past the window <see cref="AwaitedTimeoutSeconds"/> the entry is dropped, so a
    /// read-back that never arrives (deleted aircraft, future bug) cannot freeze the frequency; one that shows up later
    /// dequeues normally, without veto power over the rest.
    /// </summary>
    private int? AwaitedReadbackIndex(double elapsedSeconds)
    {
        while (_awaitingReadbacks.Count > 0)
        {
            AwaitedReadback awaited = _awaitingReadbacks.Peek();
            int index = PendingIndexOf(awaited.Transmission);
            if (index >= 0)
            {
                return index;
            }

            double waitedSeconds = elapsedSeconds - Math.Max(awaited.SinceSeconds, _nextAvailableAtSeconds);
            if (waitedSeconds < AwaitedTimeoutSeconds)
            {
                Log.LogDebug(
                    "Holding frequency for {Callsign} readback ({Waited:F1}s waited); {Count} other transmissions queued",
                    awaited.Transmission.Callsign,
                    waitedSeconds,
                    _pending.Count
                );
                return -1;
            }

            Log.LogDebug(
                "Awaited readback from {Callsign} timed out after {Waited:F1}s; releasing frequency to FIFO",
                awaited.Transmission.Callsign,
                waitedSeconds
            );
            _awaitingReadbacks.Dequeue();
        }

        return null;
    }

    /// <summary>The queue index of <paramref name="transmission"/> itself and not a value-equal copy, or -1.</summary>
    private int PendingIndexOf(PilotTransmission transmission)
    {
        int index = 0;
        foreach (PilotTransmission pending in _pending)
        {
            if (ReferenceEquals(pending, transmission))
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    /// <summary>One controller instruction's read-back, awaited from the second its instruction was dispatched.</summary>
    private readonly record struct AwaitedReadback(PilotTransmission Transmission, double SinceSeconds);

    private PilotTransmission DequeueAt(int targetIndex)
    {
        PilotTransmission? result = null;
        int originalCount = _pending.Count;
        for (int i = 0; i < originalCount; i++)
        {
            PilotTransmission item = _pending.Dequeue();
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

    /// <summary>
    /// The estimated airtime of a spoken (TTS-form) text: a quarter second per word, at least one second. The one
    /// estimate behind the airtime gate and the duration of every transmission recorded on the <see cref="ActivityMeter"/>; a
    /// transmission migrated from a V38 snapshot carries <see cref="Simulation.Snapshots.SnapshotSchemaMigrator.LegacyTransmissionSeconds"/> instead.
    /// </summary>
    public static double EstimateAirtimeSeconds(string text)
    {
        int words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        return Math.Max(MinimumAirtimeSeconds, words * SecondsPerWord);
    }
}
