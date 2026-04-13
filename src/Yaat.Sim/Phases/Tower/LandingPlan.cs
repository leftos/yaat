namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Immutable per-aircraft plan built by <see cref="LandingPhase.OnStart"/> from
/// the runway geometry and category constants. The plan is never mutated after
/// construction — the phase state machine reads it each tick but only advances
/// its own sub-state and position tracking variables.
///
/// <para>
/// Flare is closed-form: <c>vsi(agl)</c> and <c>spd(agl)</c> are pure functions
/// of current AGL, not of elapsed time or history. This mirrors the Design D
/// invariant I2 used by <see cref="LineUpPhase"/>: the dependent variables
/// (descent rate, airspeed) are functions of a single scalar phase variable
/// (AGL), so numerical error cannot compound into a floating or off-runway
/// landing.
/// </para>
/// </summary>
public sealed record LandingPlan
{
    /// <summary>Field elevation in feet MSL. Computed once at phase start.</summary>
    public required double FieldElevation { get; init; }

    /// <summary>Runway true heading — used for rollout steering and stop projection.</summary>
    public required TrueHeading RunwayHeading { get; init; }

    /// <summary>Runway threshold latitude — origin for XTE computation.</summary>
    public required double ThresholdLat { get; init; }

    /// <summary>Runway threshold longitude — origin for XTE computation.</summary>
    public required double ThresholdLon { get; init; }

    /// <summary>Runway identifier (e.g. "28R"), used by exit search and logging.</summary>
    public string? RunwayId { get; init; }

    /// <summary>AGL at which flare begins. Category-specific (jet 30 ft, TP 20, piston 15, heli 50).</summary>
    public required double FlareEntryAgl { get; init; }

    /// <summary>Peak flare descent rate at entry, in fpm. Ramps to 0 at touchdown.</summary>
    public required double FlareFpm { get; init; }

    /// <summary>Stabilized approach speed at flare entry, in knots.</summary>
    public required double Vref { get; init; }

    /// <summary>Target touchdown speed. Flare bleeds airspeed from Vref to this value over the flare window.</summary>
    public required double Vtd { get; init; }

    /// <summary>Coast speed handoff target for <see cref="Ground.RunwayExitPhase"/>. Category-specific.</summary>
    public required double CoastSpeed { get; init; }

    /// <summary>Default rollout decel rate (kt/s). Used as comfortable-brake baseline and as the floor for firm-brake decisions.</summary>
    public required double DefaultDecel { get; init; }

    /// <summary>AGL below which touchdown gate fires (2 ft for wheeled, 0 ft for helicopter).</summary>
    public required double TouchdownAgl { get; init; }
}
