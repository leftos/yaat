using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.ControllerAi;

/// <summary>
/// A frequency is serial: a position transmits at most once per tick, leaves a minimum gap (jittered from the AI's own
/// RNG stream) between transmissions, and takes a per-aircraft think time between noticing something and keying up.
/// The think time is a stateless FNV-1a draw on the callsign and the rule, so the same seed reproduces every decision
/// and no aircraft is "quick" on every rule at once. One instance per position brain; reset with its memos.
/// </summary>
public sealed class AiPacing
{
    public const double MinGapSeconds = 5;
    public const double GapJitterSeconds = 2;
    public const double ThinkMinSeconds = 2;
    public const double ThinkMaxSeconds = 8;

    public double NextTransmitAtSeconds { get; private set; } = double.NegativeInfinity;

    public bool IssuedThisTick { get; private set; }

    public void BeginTick() => IssuedThisTick = false;

    public bool CanTransmit(double now) => !IssuedThisTick && (now >= NextTransmitAtSeconds);

    public void MarkTransmitted(double now, SerializableRandom rng)
    {
        IssuedThisTick = true;
        NextTransmitAtSeconds = now + MinGapSeconds + (((2 * rng.NextDouble()) - 1) * GapJitterSeconds);
    }

    /// <summary>Seconds between the rule first applying to the aircraft and the position keying up for it.</summary>
    public static double ThinkTimeSeconds(string callsign, string rule) =>
        ThinkMinSeconds + ((ThinkMaxSeconds - ThinkMinSeconds) * FinalApproachSpeedVariety.UnitInterval(callsign, rule));

    public void Reset()
    {
        NextTransmitAtSeconds = double.NegativeInfinity;
        IssuedThisTick = false;
    }
}
