using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.ControllerAi.Rules;

/// <summary>
/// Watchdog: an aircraft in a movement phase that makes no net progress for <see cref="StuckAfterSeconds"/> is stuck —
/// a pathfinder loop, a give-way deadlock, a phase that never completes. Stops the controller ordered (HOLD / GIVEWAY,
/// a hold for release) are sequencing, not stalls (7110.65 §3-8-1), and never count; a stop the ground-conflict
/// detector imposed (the aircraft is yielding to traffic ahead) is routine in a departure queue and only counts after
/// <see cref="YieldingStuckAfterSeconds"/> — and a stall that yielded at any point keeps that longer threshold, since the
/// detector releases its limit a moment before the aircraft actually rolls. Phases that legitimately wait for a command
/// (parking, hold-short, LUAW, the ground holds) and airborne holds are never stuck.
/// </summary>
public sealed class StuckAircraftRule : IDecisionRule
{
    public const double StuckAfterSeconds = 180.0;
    public const double YieldingStuckAfterSeconds = 600.0;
    public const double MovedFt = 50.0;
    private const double FeetPerNm = 6076.12;

    public string Name => "stuck-aircraft";

    public void Evaluate(AiRuleScope scope)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var aircraft in scope.Jurisdiction)
        {
            seen.Add(aircraft.Callsign);
            var memo = scope.MemoFor(aircraft);
            var phase = aircraft.Phases?.CurrentPhase;
            var ground = aircraft.Ground;
            bool orderedStop = (ground.Hold is not null) || ground.HeldForRelease;
            if (!IsMovementPhase(phase) || orderedStop)
            {
                memo.MovementAnchor = null;
                scope.Tick.Anomalies.Close(AiAnomalyKind.StuckAircraft, scope.Position.PositionId, aircraft.Callsign, scope.Now);
                continue;
            }

            if (memo.MovementAnchor is not { } anchor || ((GeoMath.DistanceNm(anchor, aircraft.Position) * FeetPerNm) >= MovedFt))
            {
                memo.MovementAnchor = aircraft.Position;
                memo.MovementAnchorAtSeconds = scope.Now;
                memo.YieldedDuringStall = ground.SpeedLimit is not null;
                scope.Tick.Anomalies.Close(AiAnomalyKind.StuckAircraft, scope.Position.PositionId, aircraft.Callsign, scope.Now);
                continue;
            }

            bool yielding = ground.SpeedLimit is not null;
            memo.YieldedDuringStall |= yielding;
            double stalled = scope.Now - memo.MovementAnchorAtSeconds;
            if (stalled >= (memo.YieldedDuringStall ? YieldingStuckAfterSeconds : StuckAfterSeconds))
            {
                var cause = yielding
                    ? $" while yielding to {ground.AutoYieldTarget ?? "ground traffic"}"
                    : (memo.YieldedDuringStall ? " after yielding to ground traffic" : "");
                scope.Tick.Anomalies.Open(
                    AiAnomalyKind.StuckAircraft,
                    scope.Position.PositionId,
                    aircraft.Callsign,
                    scope.Now,
                    $"{phase!.Name}: no net movement for {stalled:F0}s{cause}"
                );
            }
        }

        scope.CloseVanished(AiAnomalyKind.StuckAircraft, seen);
    }

    /// <summary>Phases whose whole point is to move the aircraft; anything else is allowed to sit still.</summary>
    public static bool IsMovementPhase(Phases.Phase? phase) =>
        phase
            is TaxiingPhase
                or PushbackPhase
                or PushbackToSpotPhase
                or CrossingRunwayPhase
                or ClearRunwayPhase
                or RunwayExitPhase
                or FollowingPhase
                or AirTaxiPhase
                or LineUpPhase
                or TakeoffPhase;
}
