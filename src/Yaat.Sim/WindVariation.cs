namespace Yaat.Sim;

/// <summary>
/// Deterministic time-varying wind perturbation. Computes the instantaneous wind an
/// aircraft experiences from the authored mean wind plus per-layer variability
/// (direction spread, VRB, gusts) as a pure function of sim time — no RNG, no
/// accumulated state — so live runs and replays evaluate identically.
/// Reporting surfaces (METAR issuance, windsock) sample at phase 0; each aircraft
/// samples at a stable callsign-derived phase offset so wobble is decorrelated
/// across the scope while the mean wind stays common.
/// </summary>
public static class WindVariation
{
    /// <summary>
    /// Span of the per-aircraft phase offset domain. Surface-layer turbulence has a
    /// lateral correlation length of ~100 m, so aircraft even 1 nm apart genuinely see
    /// decorrelated perturbations; a phase offset in [0, span) reproduces that without
    /// per-aircraft noise fields.
    /// </summary>
    public const double PerAircraftPhaseSpanSeconds = 3600.0;

    private const uint FnvOffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;

    /// <summary>
    /// Stable per-aircraft phase offset in [0, <see cref="PerAircraftPhaseSpanSeconds"/>).
    /// FNV-1a over the uppercased callsign characters — never <c>string.GetHashCode</c>,
    /// whose value is randomized per process and would break replay determinism.
    /// </summary>
    public static double PhaseSecondsFor(string callsign)
    {
        uint hash = FnvOffsetBasis;
        foreach (char c in callsign)
        {
            hash ^= char.ToUpperInvariant(c);
            hash *= FnvPrime;
        }

        return (hash / (double)uint.MaxValue) * PerAircraftPhaseSpanSeconds;
    }

    /// <summary>
    /// Applies the time-varying perturbation to an interpolated mean wind. The identity
    /// mapping — every wind lookup threads sim time and phase through this single choke
    /// point so the perturbation model has exactly one seam.
    /// </summary>
    public static WindAtAltitude Perturb(WindAtAltitude meanWind, double simTimeSeconds, double phaseSeconds)
    {
        _ = simTimeSeconds;
        _ = phaseSeconds;
        return meanWind;
    }
}
