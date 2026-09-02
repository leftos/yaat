namespace Yaat.Sim.ControllerAi;

/// <summary>What the Ground brain last did for an aircraft, and what it is waiting on.</summary>
public enum GroundIntent
{
    None,
    TaxiIssued,
    CrossingRequested,
    CrossingIssued,
    HandedToLocal,
    TaxiInIssued,
}

/// <summary>
/// What a brain remembers about one aircraft between ticks: the watchdog's movement anchor, the last command it issued
/// and whether the outcome is still pending, the bounded-retry ledger, and when a rule first started applying (the
/// think-time base). Never snapshotted; every field is re-derivable from world state or safe to reset — a reset costs
/// timing (a think time restarts, a transfer may be repeated), never correctness.
/// </summary>
public sealed class AiAircraftMemo
{
    /// <summary>The bounded-retry budget: an attempt plus this many retries, then the brain leaves the aircraft alone.</summary>
    public const int MaxRetries = 2;

    /// <summary>Backoff per accumulated rejection before the next attempt.</summary>
    public const double RetryBackoffSeconds = 10;

    /// <summary>Where the aircraft last made net progress, and when — the stuck-aircraft watchdog's anchor.</summary>
    public LatLon? MovementAnchor { get; set; }

    public double MovementAnchorAtSeconds { get; set; }

    /// <summary>The ground-conflict detector held the aircraft at some point during the current stall (a queue, not a stuck aircraft).</summary>
    public bool YieldedDuringStall { get; set; }

    public GroundIntent Intent { get; set; }

    /// <summary>The command issued for this aircraft whose outcome the host has not reported yet.</summary>
    public AiCommandRequest? InFlight { get; private set; }

    public double IssuedAtSeconds { get; private set; }

    /// <summary>Past this time an in-flight command whose effect never showed up counts as rejected.</summary>
    public double EffectDeadlineSeconds { get; private set; }

    public int Rejections { get; private set; }

    /// <summary>The retry budget is spent: the brain stops commanding this aircraft (the watchdogs still watch it).</summary>
    public bool GaveUp { get; private set; }

    public double NextAttemptAtSeconds { get; private set; }

    /// <summary>The rule whose guard currently applies to the aircraft, and since when.</summary>
    public string? ObservedRule { get; private set; }

    public double ObservedAtSeconds { get; private set; }

    /// <summary>The hold-short node of the crossing the brain last cleared (or asked Local to approve).</summary>
    public int? PendingCrossingNodeId { get; set; }

    public double CoordinationRequestedAtSeconds { get; set; }

    public bool CanAct(double now) => (InFlight is null) && !GaveUp && (now >= NextAttemptAtSeconds);

    /// <summary>Starts the think-time clock for <paramref name="rule"/> unless it is already running for it.</summary>
    public void Observe(string rule, double now)
    {
        if (!string.Equals(ObservedRule, rule, StringComparison.Ordinal))
        {
            ObservedRule = rule;
            ObservedAtSeconds = now;
        }
    }

    /// <summary>Stops the think-time clock when the rule's guard no longer applies.</summary>
    public void ForgetObservation(string rule)
    {
        if (string.Equals(ObservedRule, rule, StringComparison.Ordinal))
        {
            ObservedRule = null;
        }
    }

    public void MarkIssued(AiCommandRequest request, double now, double effectWindowSeconds)
    {
        InFlight = request;
        IssuedAtSeconds = now;
        EffectDeadlineSeconds = now + effectWindowSeconds;
    }

    /// <summary>Records the host's verdict on the in-flight command: a success clears the ledger, a rejection backs off and eventually gives up.</summary>
    public void Complete(bool success, double now)
    {
        InFlight = null;
        if (success)
        {
            Rejections = 0;
            return;
        }

        Rejections++;
        Intent = GroundIntent.None;
        NextAttemptAtSeconds = now + (RetryBackoffSeconds * Rejections);
        if (Rejections > MaxRetries)
        {
            GaveUp = true;
        }
    }
}
