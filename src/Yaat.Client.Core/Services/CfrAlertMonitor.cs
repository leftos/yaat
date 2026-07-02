using Yaat.Sim.Commands;

namespace Yaat.Client.Core.Services;

/// <summary>
/// Tracks per-aircraft Call-For-Release windows against real wall-clock UTC and reports each expiry
/// violation (early/late takeoff, expired-while-grounded) exactly once. The wall-clock/latch logic is
/// split out from the view-model so it can be unit-tested with an injected <c>nowUtc</c>. Alert-only —
/// it never affects the simulation (GitHub issue #230).
/// </summary>
public sealed class CfrAlertMonitor
{
    private readonly Dictionary<string, State> _state = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct State(CfrAlertKind Fired, DateTime Start, DateTime End);

    /// <summary>
    /// Evaluates one aircraft's window and returns the newly-tripped alert kind, or null. Latches so each
    /// kind fires once; the latch resets when the window changes and clears when the window is removed.
    /// <paramref name="wasOnGround"/> is the aircraft's ground state at the previous observation, so a
    /// wheels-up transition can be detected (pass the current state for a periodic sweep).
    /// </summary>
    public CfrAlertKind? Evaluate(string callsign, DateTime? startUtc, DateTime? endUtc, bool isOnGround, bool wasOnGround, DateTime nowUtc)
    {
        if (startUtc is null || endUtc is null)
        {
            _state.Remove(callsign);
            return null;
        }

        var window = new ReleaseWindow(startUtc.Value, endUtc.Value);
        if (!_state.TryGetValue(callsign, out var s) || s.Start != window.StartUtc || s.End != window.EndUtc)
        {
            s = new State(CfrAlertKind.None, window.StartUtc, window.EndUtc);
        }

        var kind = CfrAlertEvaluator.Evaluate(window, nowUtc, isOnGround, wasOnGround, s.Fired);
        if (kind is { } fired)
        {
            s = s with { Fired = s.Fired | fired };
        }

        _state[callsign] = s;
        return kind;
    }

    /// <summary>Drops a callsign's latch (call when the aircraft is removed).</summary>
    public void Remove(string callsign) => _state.Remove(callsign);
}
